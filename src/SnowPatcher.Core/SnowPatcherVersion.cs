using System.Text.RegularExpressions;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core;

public static partial class SnowPatcherVersion
{
    public static string Current { get; } = GetCurrentVersion();

    public static bool IsValid(string? value) => value is not null && VersionPattern().IsMatch(value);

    public static int Compare(string left, string right)
    {
        if (!IsValid(left) || !IsValid(right))
        {
            throw new ArgumentException("Use a MAJOR.MINOR.PATCH version.");
        }

        return PackageManifestReader.CompareVersions(left, right);
    }

    internal static void Require(string minimum, string? modName = null)
    {
        if (!IsValid(minimum))
        {
            throw new InvalidDataException("minSnowPatcherVersion must use MAJOR.MINOR.PATCH.");
        }

        if (Compare(Current, minimum) < 0)
        {
            throw new SnowPatcherUpdateRequiredException(minimum) { ModName = modName };
        }
    }

    private static string GetCurrentVersion()
    {
        var version = typeof(SnowPatcherVersion).Assembly.GetName().Version!;
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

public sealed class SnowPatcherUpdateRequiredException(string requiredVersion)
    : IOException($"This mod requires SnowPatcher {requiredVersion} or newer. Update SnowPatcher.")
{
    public string RequiredVersion { get; } = requiredVersion;
    public string? ModName { get; internal set; }
    public Mods.DeferredModArchive? Archive { get; internal set; }
}
