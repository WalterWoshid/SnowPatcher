using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Compatibility;

internal static partial class TextEdits
{
    public static TextPreview Strings(byte[] bytes, JsonElement operation, bool guards)
    {
        var encoding = TextEncoding.Read(bytes);
        var rows = new Dictionary<string, (string Value, int Start, int End)>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        foreach (var line in encoding.Text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                var match = StringRow().Match(line.TrimEnd('\r'));
                if (!match.Success)
                {
                    throw new CheckFailure("unsupported", "The language file uses syntax this checker does not support.");
                }

                var key = match.Groups[1].Success ? match.Groups[1].Value : JsonSerializer.Deserialize<string>(match.Groups[2].Value)!;
                var value = JsonSerializer.Deserialize<string>(match.Groups[3].Value)!;
                if (!rows.TryAdd(key, (value, offset + match.Groups[3].Index, offset + match.Groups[3].Index + match.Groups[3].Length)))
                {
                    duplicates.Add(key);
                }
            }

            offset += line.Length + 1;
        }

        var changes = new List<TextChange>();
        var replacements = new List<Replacement>();
        foreach (var edit in operation.GetProperty("edits").EnumerateArray())
        {
            var key = edit.GetProperty("key").GetString()!;
            var exists = rows.TryGetValue(key, out var row);
            if (duplicates.Contains(key))
            {
                throw new CheckFailure("unsupported", $"The language key being edited has more than one definition: {key}");
            }

            var before = edit.GetProperty("before");
            var absent = before.GetProperty("state").GetString() == "absent";
            var old = absent ? "absent" : "string:" + before.GetProperty("value").GetString();
            if (guards && (absent ? exists : !exists || row.Value != before.GetProperty("value").GetString()))
            {
                throw new CheckFailure("changed", $"The old language value does not match for {key}.");
            }

            var value = edit.GetProperty("value").GetString()!;
            changes.Add(new TextChange([key], "set", old, "string:" + value));
            var encoded = JsonSerializer.Serialize(value, TextJson);
            replacements.Add(exists ? new Replacement(row.Start, row.End, encoded) : new Replacement(encoding.Text.Length, encoding.Text.Length, "\n" + key + "\t" + encoded + "\n"));
        }

        return new TextPreview(encoding.Encode(Replace(encoding.Text, replacements)), changes);
    }

    [GeneratedRegex("^\\s*(?:([^\\s\"]+)|(\"(?:[^\"\\\\]|\\\\.)*\"))\\s+(\"(?:[^\"\\\\]|\\\\.)*\")\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex StringRow();
}
