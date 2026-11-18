using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SnowPatcher.Core.Compatibility;

internal static partial class TextEdits
{
    public static TextPreview Strings(byte[] bytes, JsonElement operation, bool guards)
    {
        var encoding = TextEncoding.Read(bytes);
        if (encoding.Text.Contains('\0'))
        {
            throw new CheckFailure("unsupported", "The language file contains NUL characters. Save UTF-16 language files with a byte order mark.");
        }

        var edits = operation.GetProperty("edits").EnumerateArray().ToArray();
        var keys = edits.Select(edit => edit.GetProperty("key").GetString()!).ToHashSet(StringComparer.Ordinal);
        var rows = new Dictionary<string, (string Value, int Start, int End)>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        var opaqueKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        var offset = 0;
        var lineNumber = 0;
        string? newline = null;
        var remaining = encoding.Text.AsSpan();
        while (!remaining.IsEmpty)
        {
            var end = remaining.IndexOfAny('\r', '\n');
            var line = end < 0 ? remaining : remaining[..end];
            lineNumber++;
            var trimmed = line.TrimStart();
            if (!trimmed.IsEmpty && !trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                var rowText = line.ToString();
                var match = StringRow().Match(rowText);
                var key = match.Success ? match.Groups["bare"].Success
                    ? match.Groups["bare"].Value : ReadQuotedString(match.Groups["key"].Value) : null;
                if (key is null)
                {
                    // Preserve unfamiliar rows unless they might define a key we need to edit.
                    foreach (Match token in StringKeyCandidate().Matches(rowText))
                    {
                        var candidate = token.Value[0] == '"' ? ReadQuotedString(token.Value) : token.Value;
                        if (candidate is not null && keys.Contains(candidate))
                        {
                            opaqueKeys.TryAdd(candidate, lineNumber);
                        }
                    }
                }
                else if (keys.Contains(key))
                {
                    var valueGroup = match.Groups["value"];
                    var value = ReadQuotedString(valueGroup.Value);
                    if (value is null || match.Groups["assignment"].Success)
                    {
                        opaqueKeys.TryAdd(key, lineNumber);
                    }
                    else if (!rows.TryAdd(key, (value, offset + valueGroup.Index, offset + valueGroup.Index + valueGroup.Length)))
                    {
                        duplicates.Add(key);
                    }
                }
            }

            if (end < 0)
            {
                break;
            }

            var width = remaining[end..].StartsWith("\r\n", StringComparison.Ordinal) ? 2 : 1;
            newline ??= remaining.Slice(end, width).ToString();
            offset += end + width;
            remaining = remaining[(end + width)..];
        }

        newline ??= "\n";
        var changes = new List<TextChange>();
        var replacements = new List<Replacement>();
        var additions = new StringBuilder();
        foreach (var edit in edits)
        {
            var key = edit.GetProperty("key").GetString()!;
            if (opaqueKeys.TryGetValue(key, out var line))
            {
                throw new CheckFailure("unsupported", FormattableString.Invariant($"Cannot safely edit language key {key}. It appears in an unsupported row on line {line}."));
            }

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
            if (exists)
            {
                replacements.Add(new Replacement(row.Start, row.End, encoded));
            }
            else
            {
                additions.Append(key).Append('\t').Append(encoded).Append(newline);
            }
        }

        if (additions.Length > 0)
        {
            if (encoding.Text.Length > 0 && encoding.Text[^1] is not ('\n' or '\r'))
            {
                additions.Insert(0, newline);
            }

            replacements.Add(new Replacement(encoding.Text.Length, encoding.Text.Length, additions.ToString()));
        }

        return new TextPreview(encoding.Encode(Replace(encoding.Text, replacements)), changes);
    }

    private static string? ReadQuotedString(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("""^\s*(?:(?<bare>[A-Za-z_0-9.,/?-]+)|(?<key>"(?:[^"\\]|\\.)*"))(?:\s*(?<assignment>[=:])\s*|\s+)(?<value>"(?:[^"\\]|\\.)*")\s*(?://.*)?$""", RegexOptions.CultureInvariant)]
    private static partial Regex StringRow();

    [GeneratedRegex("""(?:"(?:[^"\\]|\\.)*")|[A-Za-z_0-9]+""", RegexOptions.CultureInvariant)]
    private static partial Regex StringKeyCandidate();
}
