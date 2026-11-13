using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace SnowPatcher.Core.Packages;

internal static class PackageManifestReader
{
    private static readonly Lazy<JsonSchema> Schema = new(LoadSchema);

    public static PackageManifest Read(byte[] bytes)
    {
        try
        {
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            var json = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            CheckJson(root);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("formatVersion", out var version) &&
                version.ValueKind == JsonValueKind.Number && version.TryGetDecimal(out var number) && number is not (1 or 2))
            {
                throw new InvalidDataException("Unsupported mod package format. This build reads formatVersion 1 and 2.");
            }

            var result = Schema.Value.Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!result.IsValid)
            {
                var errors = Errors(result).Distinct().Take(3);
                throw new InvalidDataException("snowpatcher.json does not match the package format. " + string.Join(" ", errors));
            }

            var manifest = new PackageManifest(
                SingleLine(root.GetProperty("id").GetString()!),
                SingleLine(root.GetProperty("name").GetString()!),
                SingleLine(root.GetProperty("version").GetString()!),
                Items(root, "authors").Select(x => SingleLine(x.GetString()!)).ToImmutableArray(),
                root.TryGetProperty("description", out var description) ? description.GetString() : null,
                Items(root, "testedBuilds").Select(x => SingleLine(x.GetString()!)).ToImmutableArray(),
                Items(root, "dependencies").Select(x => new PackageDependency(
                    SingleLine(x.GetProperty("id").GetString()!), SingleLine(x.GetProperty("minVersion").GetString()!),
                    x.TryGetProperty("maxVersionExclusive", out var max) ? SingleLine(max.GetString()!) : null)).ToImmutableArray(),
                Items(root, "conflicts").Select(x => SingleLine(x.GetString()!)).ToImmutableArray(),
                Items(root, "checks").Select(x => new PackageCheck(Target(x.GetProperty("target")), Hash(x.GetProperty("sha256")))).ToImmutableArray(),
                Items(root, "files").Select(x => new PackageFile(PackagePath.Validate(x.GetProperty("path").GetString()!),
                    Hash(x.GetProperty("sha256")), Integer(x.GetProperty("size")))).ToImmutableArray(),
                Items(root, "operations").Select(x => new PackageOperation(SingleLine(x.GetProperty("id").GetString()!),
                    x.GetProperty("type").GetString()!, Target(x.GetProperty("target")), x.Clone())).ToImmutableArray(),
                root.TryGetProperty("relocations", out var relocations) ? relocations.Clone() : null,
                root.TryGetProperty("modioUrl", out var modioUrl) ? modioUrl.GetString() : null);
            PackageSemantics.Validate(manifest);
            return manifest;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or OverflowException or InvalidOperationException)
        {
            throw new InvalidDataException("snowpatcher.json must contain valid UTF-8 JSON within the supported limits.", error);
        }
    }

    private static JsonSchema LoadSchema()
    {
        using var stream = typeof(PackageManifestReader).Assembly.GetManifestResourceStream("SnowPatcher.PackageSchema.json")
            ?? throw new InvalidOperationException("The package schema is missing from this build.");
        using var document = JsonDocument.Parse(stream);
        return JsonSchema.Build(document.RootElement.Clone(), new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry { Fetch = (_, _) => null }
        });
    }

    private static void CheckJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                CheckUnicode(property.Name);
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("snowpatcher.json contains a duplicate JSON property.");
                }

                CheckJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CheckJson(item);
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            CheckUnicode(element.GetString()!);
        }
        else if (element.ValueKind == JsonValueKind.Number && (!element.TryGetDouble(out var number) || !double.IsFinite(number)))
        {
            throw new InvalidDataException("snowpatcher.json contains a non-finite or excessively large number.");
        }
    }

    private static void CheckUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i]))
            {
                continue;
            }

            if (!char.IsSurrogatePair(value, i))
            {
                throw new InvalidDataException("snowpatcher.json contains an invalid Unicode string.");
            }

            i++;
        }
    }

    private static IEnumerable<string> Errors(EvaluationResults result)
    {
        foreach (var error in result.Errors ?? [])
        {
            var text = $"{result.InstanceLocation}: {error.Value}";
            text = new string(text.Select(c => char.IsControl(c) ? ' ' : c).Take(240).ToArray());
            yield return text;
        }
        foreach (var detail in result.Details ?? [])
        {
            foreach (var error in Errors(detail))
            {
                yield return error;
            }
        }
    }

    internal static IEnumerable<JsonElement> Items(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? value.EnumerateArray().ToArray() : [];

    internal static long Integer(JsonElement element)
    {
        if (element.TryGetInt64(out var integer))
        {
            return integer;
        }

        if (element.TryGetDecimal(out var value) && decimal.Truncate(value) == value && value >= long.MinValue && value <= long.MaxValue)
        {
            return (long)value;
        }

        throw new InvalidDataException("A package integer is outside the supported range.");
    }

    internal static string Hash(JsonElement element)
    {
        var hash = element.GetString()!;
        if (hash.Length != 64 || hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException("Package hashes must be 64 lowercase hexadecimal characters.");
        }

        return hash;
    }

    internal static string SingleLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new InvalidDataException("A package identifier, name or label is blank or contains control characters.");
        }

        return value;
    }

    internal static PackageTarget Target(JsonElement element) => PackagePath.ValidateTarget(new PackageTarget(
        element.GetProperty("file").GetString()!,
        element.GetProperty("members").EnumerateArray().Select(x => new PackageMember(
            x.GetProperty("format").GetString()!, x.GetProperty("name").GetString()!)).ToImmutableArray()));

    // Version components are not limited to Int32; compare their digit counts first.
    internal static int CompareVersions(string left, string right)
    {
        var first = left.Split('.');
        var second = right.Split('.');
        for (var i = 0; i < 3; i++)
        {
            var comparison = first[i].Length.CompareTo(second[i].Length);
            if (comparison == 0)
            {
                comparison = string.Compare(first[i], second[i], StringComparison.Ordinal);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }
        return 0;
    }
}
