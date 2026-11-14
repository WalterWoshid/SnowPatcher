using System.Text;
using System.Text.Json;

namespace SnowPatcher.Core.Packages;

internal static partial class PackageSemantics
{
    internal const string NativeMetadataPath = "Sources/BinEditor/meta_desc_ssl.ps";

    public static void Validate(PackageManifest manifest)
    {
        if (manifest.MinSnowPatcherVersion is { } minimum && SnowPatcherVersion.Compare(minimum, "1.1.0") < 0)
        {
            throw new InvalidDataException("Package format 3 requires minSnowPatcherVersion 1.1.0 or newer.");
        }

        if (manifest.ModioUrl is not null && Mods.ModioPage.Parse(manifest.ModioUrl) is null)
        {
            throw new InvalidDataException("modioUrl must be an HTTPS SnowRunner mod page on mod.io.");
        }

        Unique(manifest.Dependencies.Select(x => x.Id), "dependency IDs");
        Unique(manifest.Checks.Select(x => PackagePath.TargetKey(x.Target)), "check targets");
        Unique(manifest.Files.Select(x => x.Path), "payload paths", StringComparer.OrdinalIgnoreCase);
        Unique(manifest.Operations.Select(x => x.Id), "operation IDs");
        var files = manifest.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
        if (manifest.Files.Any(x => x.Path.Equals(PackageArchive.ManifestName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The manifest must not list itself as a payload.");
        }

        if (manifest.Files.Sum(x => x.Size) > PackageArchive.MaxExpandedSize)
        {
            throw new InvalidDataException("The package exceeds the 1 GiB expanded-payload limit.");
        }

        var conflicts = manifest.Conflicts.ToHashSet(StringComparer.Ordinal);
        foreach (var dependency in manifest.Dependencies)
        {
            if (dependency.Id == manifest.Id || conflicts.Contains(dependency.Id))
            {
                throw new InvalidDataException("A mod cannot depend on itself or on a mod it declares incompatible.");
            }

            if (dependency.MaxVersionExclusive is { } upper && PackageManifestReader.CompareVersions(dependency.MinVersion, upper) >= 0)
            {
                throw new InvalidDataException($"Invalid dependency version range: {dependency.Id}");
            }
        }
        if (conflicts.Contains(manifest.Id))
        {
            throw new InvalidDataException("A mod cannot conflict with itself.");
        }

        var hasNativeCheck = manifest.Checks.Any(x => x.Target.File == NativeMetadataPath && x.Target.Members.IsEmpty);
        foreach (var operation in manifest.Operations)
        {
            var definition = operation.Definition;
            if (definition.TryGetProperty("payload", out var payload))
            {
                RequirePayload(payload.GetString()!);
            }

            if (definition.TryGetProperty("beforeSha256", out var beforeHash))
            {
                PackageManifestReader.Hash(beforeHash);
            }

            if (operation.Type.StartsWith("sslb.", StringComparison.Ordinal) || operation.Target.Members.Any(x => x.Format == "sslbundle") ||
                operation.Target.File.EndsWith(".sslbundle", StringComparison.OrdinalIgnoreCase) ||
                operation.Target.Members.Any(x => x.Name.EndsWith(".sslbundle", StringComparison.OrdinalIgnoreCase)))
            {
                if (!hasNativeCheck && !(manifest.Relocations is not null && operation.Type is "sslb.function.edit" or "sslb.methods.add"))
                {
                    throw new InvalidDataException($"Operation {operation.Id} requires a native metadata check.");
                }
            }
            switch (operation.Type)
            {
                case "xml.edit":
                    if (manifest.MinSnowPatcherVersion is null || PackageManifestReader.CompareVersions(manifest.MinSnowPatcherVersion, "1.3.0") < 0)
                    {
                        throw new InvalidDataException("XML edits require minSnowPatcherVersion 1.3.0 or newer.");
                    }
                    ValidateXml(operation);
                    break;
                case "sso.edit":
                    ValidateSso(operation, RequirePayload);
                    break;
                case "strings.edit":
                    var keys = definition.GetProperty("edits").EnumerateArray().Select(x =>
                        PackageManifestReader.SingleLine(x.GetProperty("key").GetString()!));
                    Unique(keys, $"language keys in {operation.Id}");
                    break;
                case "sslb.function.edit":
                    ValidateFunction(operation);
                    break;
                case "sslb.methods.add":
                    PackageManifestReader.Hash(definition.GetProperty("moduleContextSha256"));
                    PackageManifestReader.SingleLine(definition.GetProperty("class").GetString()!);
                    PackageManifestReader.SingleLine(definition.GetProperty("templateMember").GetString()!);
                    var methods = definition.GetProperty("methods").EnumerateArray().ToArray();
                    Unique(methods.Select(m => m.GetProperty("name").GetString()!), "added method names");
                    Unique(methods.Select(m => m.GetProperty("slot").GetRawText()), "added method slots");
                    foreach (var method in methods)
                    {
                        PackageManifestReader.SingleLine(method.GetProperty("signature").GetProperty("returns").GetString()!);
                        foreach (var argument in method.GetProperty("signature").GetProperty("arguments").EnumerateArray())
                        {
                            PackageManifestReader.SingleLine(argument.GetString()!);
                        }
                    }
                    break;
            }
        }

        Compatibility.PortablePackage.Validate(manifest);

        void RequirePayload(string path)
        {
            PackagePath.Validate(path);
            if (!files.ContainsKey(path))
            {
                throw new InvalidDataException($"A referenced payload is not listed in files: {path}");
            }
        }
    }

    private static void ValidateSso(PackageOperation operation, Action<string> requirePayload)
    {
        var paths = new List<(string Key, string Action)>();
        foreach (var edit in operation.Definition.GetProperty("edits").EnumerateArray())
        {
            var action = edit.GetProperty("action").GetString()!;
            var path = edit.GetProperty("path").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String
                ? "s:" + x.GetString() : "i:" + PackageManifestReader.Integer(x).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var before = edit.GetProperty("before");
            if (path.Any(x => x.StartsWith("i:", StringComparison.Ordinal)) && !operation.Definition.TryGetProperty("beforeSha256", out _))
            {
                throw new InvalidDataException($"Array-index edits need beforeSha256: {operation.Id}");
            }

            if (action == "remove" && path[^1].StartsWith("i:", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Package v1 removes SSO object properties, not array elements.");
            }

            if (action == "append-unique" && (before.GetProperty("state").GetString() != "present" ||
                (before.GetProperty("value").TryGetProperty("json", out var array) && array.ValueKind != JsonValueKind.Array)))
            {
                throw new InvalidDataException("An append-unique edit requires a present array guard.");
            }

            if (before.TryGetProperty("value", out var beforeValue))
            {
                CheckValue(beforeValue);
            }

            if (edit.TryGetProperty("value", out var value))
            {
                CheckValue(value);
            }

            var key = new StringBuilder();
            foreach (var segment in path)
            {
                key.Append(segment.Length).Append(':').Append(segment);
            }

            paths.Add((key.ToString(), action));
        }
        // Sorted, length-prefixed keys expose ancestor/duplicate paths without a quadratic scan.
        var sorted = paths.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
        for (var i = 1; i < sorted.Length; i++)
        {
            var prior = sorted[i - 1];
            var current = sorted[i];
            if (current.Key.StartsWith(prior.Key, StringComparison.Ordinal) &&
                !(current.Key == prior.Key && current.Action == "append-unique" && prior.Action == "append-unique"))
            {
                throw new InvalidDataException($"Duplicate or overlapping SSO edits in {operation.Id}. Use separate ordered operations for sequential changes.");
            }
        }

        void CheckValue(JsonElement value)
        {
            if (value.TryGetProperty("ssoFile", out var payload))
            {
                requirePayload(payload.GetString()!);
            }
        }
    }

    private static void ValidateFunction(PackageOperation operation)
    {
        var definition = operation.Definition;
        PackageManifestReader.Hash(definition.GetProperty("moduleContextSha256"));
        var function = definition.GetProperty("function");
        PackageManifestReader.SingleLine(function.GetProperty("class").GetString()!);
        PackageManifestReader.SingleLine(function.GetProperty("name").GetString()!);
        PackageManifestReader.Hash(function.GetProperty("bodySha256"));
        foreach (var edit in definition.GetProperty("edits").EnumerateArray())
        {
            var labels = new HashSet<string>(StringComparer.Ordinal);
            var references = new List<string>();
            foreach (var row in edit.GetProperty("code").EnumerateArray())
            {
                if (row.TryGetProperty("label", out var label))
                {
                    if (!labels.Add(PackageManifestReader.SingleLine(label.GetString()!)))
                    {
                        throw new InvalidDataException($"Duplicate instruction label in {operation.Id}.");
                    }
                }
                else if (row.GetProperty("arg").ValueKind == JsonValueKind.Object && row.GetProperty("arg").TryGetProperty("label", out label))
                {
                    references.Add(PackageManifestReader.SingleLine(label.GetString()!));
                }
            }
            if (references.Any(x => !labels.Contains(x)))
            {
                throw new InvalidDataException($"Unresolved instruction label in {operation.Id}.");
            }
        }
    }

    private static void Unique(IEnumerable<string> values, string subject, IEqualityComparer<string>? comparer = null)
    {
        var seen = new HashSet<string>(comparer ?? StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                throw new InvalidDataException($"The manifest contains duplicate {subject}.");
            }
        }
    }
}
