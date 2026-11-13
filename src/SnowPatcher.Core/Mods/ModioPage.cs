using System.Text.RegularExpressions;

namespace SnowPatcher.Core.Mods;

public static partial class ModioPage
{
    public static Uri? Parse(string? value)
    {
        if (value is null || value.Length > 2048 || !PageUrl().IsMatch(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri;
    }

    public static Uri ForId(long id) => id > 0 ? new Uri($"https://mod.io/g/snowrunner/m/{id}")
        : throw new ArgumentOutOfRangeException(nameof(id));

    [GeneratedRegex(@"\Ahttps://(?:www\.)?mod\.io/g/snowrunner/m/[A-Za-z0-9][A-Za-z0-9_-]*/?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PageUrl();
}
