using System.Text;

namespace SnowPatcher.Core.Packages;

internal static class PackagePath
{
    public static string Validate(string path)
    {
        if (path.Length is 0 or > 1024 || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl))
        {
            throw new InvalidDataException("The package contains an unsafe or excessively long path.");
        }

        var segments = path.Split('/');
        if (segments.Length > 64)
        {
            throw new InvalidDataException("The package path has too many folder levels.");
        }

        foreach (var segment in segments)
        {
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || Reserved(segment))
            {
                throw new InvalidDataException("The package contains an unsafe file path.");
            }
        }
        return path;
    }

    public static PackageTarget ValidateTarget(PackageTarget target)
    {
        Validate(target.File);
        foreach (var member in target.Members)
        {
            switch (member.Format)
            {
                case "zip":
                    Validate(member.Name);
                    break;
                case "cache":
                    Validate(member.Name);
                    var separator = member.Name.IndexOf("]/", StringComparison.Ordinal);
                    if (!member.Name.StartsWith('[') || separator < 2 || member.Name[..separator].Contains('/'))
                    {
                        throw new InvalidDataException("Cache targets must use [namespace]/path names.");
                    }

                    break;
                case "sslbundle":
                    if (member.Name.Length > 8192 || member.Name.Split('.').Any(part => part.Length == 0 ||
                        !(char.IsAsciiLetter(part[0]) || part[0] == '_') || part.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))))
                    {
                        throw new InvalidDataException("An SSL bundle target has an unsupported module identifier.");
                    }

                    break;
                default:
                    throw new InvalidDataException("The package requires an unsupported container format.");
            }
        }
        return target;
    }

    public static string TargetKey(PackageTarget target)
    {
        var key = new StringBuilder();
        Append(target.File.ToUpperInvariant());
        foreach (var member in target.Members)
        {
            Append(member.Format);
            Append(member.Format == "sslbundle" ? member.Name : member.Name.ToUpperInvariant());
        }
        return key.ToString();

        void Append(string part) => key.Append(part.Length).Append(':').Append(part);
    }

    private static bool Reserved(string segment)
    {
        var name = segment.Split('.')[0].TrimEnd(' ');
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789\u00b9\u00b2\u00b3".Contains(name[3]);
    }
}
