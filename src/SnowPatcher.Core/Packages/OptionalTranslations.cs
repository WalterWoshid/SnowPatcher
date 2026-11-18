using System.Text.Json;

namespace SnowPatcher.Core.Packages;

internal static class OptionalTranslations
{
    internal const string MinimumVersion = "1.5.0";
    private const string EnglishFile = "[strings]/strings_english.str";

    internal static HashSet<string> GetOperationIds(PackageManifest manifest)
    {
        var englishKeys = manifest.Operations
            .Where(o => IsStringTable(o) && o.Target.Members[0].Name == EnglishFile && !IsOptIn(o))
            .SelectMany(o => Edits(o).Where(IsAddition))
            .Select(Key)
            .ToHashSet(StringComparer.Ordinal);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in manifest.Operations)
        {
            var hasFlag = operation.Definition.TryGetProperty("optional", out var optional);
            var optIn = hasFlag && optional.GetBoolean();
            if (hasFlag && (manifest.MinSnowPatcherVersion is null ||
                SnowPatcherVersion.Compare(manifest.MinSnowPatcherVersion, MinimumVersion) < 0))
            {
                throw new InvalidDataException($"The optional flag requires minSnowPatcherVersion {MinimumVersion} or newer.");
            }

            // Published Repair Transfer packages predate the optional flag.
            var legacy = manifest.Id == "repair-transfer" && !hasFlag;
            if (!optIn && !legacy)
            {
                continue;
            }

            var eligible = IsStringTable(operation) && operation.Target.Members[0].Name != EnglishFile &&
                Edits(operation).All(edit => IsAddition(edit) && englishKeys.Contains(Key(edit)));
            if (optIn && !eligible)
            {
                throw new InvalidDataException($"Optional operation {operation.Id} must add a non-English translation for keys added in English by this mod.");
            }

            if (eligible)
            {
                ids.Add(operation.Id);
            }
        }

        return ids;
    }

    private static bool IsStringTable(PackageOperation operation) =>
        operation.Type == "strings.edit" && operation.Target.File == "preload/paks/client/initial.pak" &&
        operation.Target.Members is [{ Format: "zip", Name: var name }] &&
        name.StartsWith("[strings]/strings_", StringComparison.Ordinal) &&
        name.EndsWith(".str", StringComparison.Ordinal) && name.LastIndexOf('/') == "[strings]".Length;

    private static bool IsOptIn(PackageOperation operation) =>
        operation.Definition.TryGetProperty("optional", out var optional) && optional.GetBoolean();

    private static JsonElement.ArrayEnumerator Edits(PackageOperation operation) =>
        operation.Definition.GetProperty("edits").EnumerateArray();

    private static bool IsAddition(JsonElement edit) =>
        edit.GetProperty("before").GetProperty("state").GetString() == "absent";

    private static string Key(JsonElement edit) => edit.GetProperty("key").GetString()!;
}
