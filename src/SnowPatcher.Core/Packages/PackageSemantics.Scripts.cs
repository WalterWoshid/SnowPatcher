namespace SnowPatcher.Core.Packages;

internal static partial class PackageSemantics
{
    private static void ValidateExtension(PackageManifest manifest, PackageOperation operation)
    {
        if (manifest.MinSnowPatcherVersion is null || PackageManifestReader.CompareVersions(manifest.MinSnowPatcherVersion, "1.4.0") < 0)
        {
            throw new InvalidDataException("Script extensions require minSnowPatcherVersion 1.4.0 or newer.");
        }
        PackageManifestReader.Hash(operation.Definition.GetProperty("beforeSha256"));
        var extendedFunctions = operation.Definition.GetProperty("functions").EnumerateArray().ToArray();
        Unique(extendedFunctions.Select(f => PackageManifestReader.SingleLine(f.GetProperty("class").GetString()!) + "::" +
            PackageManifestReader.SingleLine(f.GetProperty("name").GetString()!)), "extended function names");
        foreach (var extendedFunction in extendedFunctions)
        {
            var previousAt = -1;
            foreach (var insertion in extendedFunction.GetProperty("insertions").EnumerateArray())
            {
                if (!insertion.GetProperty("at").TryGetInt32(out var at) || !insertion.GetProperty("length").TryGetInt32(out var length) || at <= previousAt || length <= 0)
                {
                    throw new InvalidDataException("Script insertions need ordered, unique nonnegative positions and positive 32-bit lengths.");
                }
                previousAt = at;
            }
        }
    }

    private static void ValidateScriptMinimum(PackageManifest manifest, PackageOperation operation)
    {
        if (operation.Type is "sslb.function.edit" or "sslb.methods.add")
        {
            var code = operation.Type == "sslb.function.edit"
                ? operation.Definition.GetProperty("edits").EnumerateArray().SelectMany(e => e.GetProperty("code").EnumerateArray())
                : operation.Definition.GetProperty("methods").EnumerateArray().SelectMany(m => m.GetProperty("code").EnumerateArray());
            var arrayMatch = operation.Type == "sslb.function.edit" && operation.Definition.GetProperty("edits").EnumerateArray()
                .Any(edit => edit.GetProperty("match").EnumerateArray().Any(row => row[0].GetInt32() is 63 or 65 or 66));
            if ((arrayMatch || code.Any(row => row.TryGetProperty("op", out var op) && op.GetInt32() is 63 or 65 or 66)) &&
                (manifest.MinSnowPatcherVersion is null || PackageManifestReader.CompareVersions(manifest.MinSnowPatcherVersion, "1.4.0") < 0))
            {
                throw new InvalidDataException("Array instructions require minSnowPatcherVersion 1.4.0 or newer.");
            }
        }
    }
}
