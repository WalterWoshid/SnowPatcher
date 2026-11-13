using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Compatibility;

internal static partial class TextEdits
{
    internal static IReadOnlyList<string> SsoSliceHashes(byte[] bytes, IReadOnlyList<string[]> paths)
    {
        var source = TextEncoding.Read(bytes).Text;
        var root = new SsoParser(source).Document();
        return paths.Select(path =>
        {
            var current = root;
            foreach (var key in path)
            {
                current = current.Properties.TryGetValue(key, out var property) ? property.Value : throw new CheckFailure("changed", "A required native definition is missing: " + string.Join("/", path));
            }

            var tokens = Regex.Matches(source[current.Start..current.End], "\"(?:\\\\.|[^\"\\\\])*\"|//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/|[^\\s{}=\\[\\],\"]+|[{}=\\[\\],]");
            var normalized = string.Concat(tokens.Where(m => !m.Value.StartsWith("//", StringComparison.Ordinal) && !m.Value.StartsWith("/*", StringComparison.Ordinal)).Select(m => m.Value.StartsWith('"') ? "s:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Deserialize<string>(m.Value)!)) + ";" : "t:" + m.Value + ";"));
            return GameResources.Hash(Encoding.UTF8.GetBytes(normalized));
        }).ToArray();
    }

    public static TextPreview Sso(byte[] bytes, JsonElement operation, ModPackage package, bool guards, CancellationToken token, int activeModCount = 1)
    {
        var encoding = TextEncoding.Read(bytes);
        var parser = new SsoParser(encoding.Text);
        var root = parser.Document();
        var changes = new List<TextChange>();
        var replacements = new List<Replacement>();
        var appends = new Dictionary<string, (Node Array, List<Node> Values)>();
        foreach (var edit in operation.GetProperty("edits").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var path = edit.GetProperty("path").EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? "s:" + p.GetString() : "i:" + PackageManifestReader.Integer(p).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var (parent, current, property) = Resolve(root, path);
            var before = edit.GetProperty("before");
            var absent = before.GetProperty("state").GetString() == "absent";
            var expected = absent ? null : Value(before.GetProperty("value"));
            if (guards && (absent ? current is not null : current is null || current.Fingerprint != expected!.Fingerprint))
            {
                throw new CheckFailure("changed", $"The old SSO value does not match at {Display(path)}.");
            }

            var action = edit.GetProperty("action").GetString()!;
            var value = action == "remove" ? null : Value(edit.GetProperty("value"));
            var requirement = absent ? "absent" : expected!.Fingerprint;
            changes.Add(new TextChange(path, action, requirement, value?.Fingerprint ?? "absent"));
            if (action == "append-unique")
            {
                if (current is null || current.Kind != "array")
                {
                    throw new CheckFailure("changed", "The SSO append target is not an array.");
                }

                if (guards && current.Items.Any(n => n.Fingerprint == value!.Fingerprint))
                {
                    throw new CheckFailure("changed", "The array already contains the item this mod wants to append.");
                }

                var key = JsonSerializer.Serialize(path);
                if (!appends.TryGetValue(key, out var append))
                {
                    appends[key] = append = (current, []);
                }

                if (!current.Items.Any(n => n.Fingerprint == value!.Fingerprint) && !append.Values.Any(n => n.Fingerprint == value!.Fingerprint))
                {
                    append.Values.Add(value!);
                }
            }
            else if (action == "remove")
            {
                if (current is null || property is null)
                {
                    throw new CheckFailure("changed", "The SSO property to remove is missing.");
                }

                var end = current.End;
                var after = end;
                while (after < encoding.Text.Length && char.IsWhiteSpace(encoding.Text[after]))
                {
                    after++;
                }

                if (after < encoding.Text.Length && encoding.Text[after] == ',')
                {
                    end = after + 1;
                }

                replacements.Add(new Replacement(property.Start, end, ""));
            }
            else if (current is not null)
            {
                replacements.Add(new Replacement(current.Start, current.End, value!.Render()));
            }
            else
            {
                if (parent.Kind != "object" || !path[^1].StartsWith("s:", StringComparison.Ordinal))
                {
                    throw new CheckFailure("unsupported", "SSO set cannot resize an array.");
                }

                replacements.Add(new Replacement(parent.Close, parent.Close, "\n" + PropertyName(path[^1][2..]) + " = " + value!.Render() + "\n"));
            }
        }

        foreach (var append in appends.Values)
        {
            if (append.Values.Count == 0)
            {
                continue;
            }

            var previous = append.Array.Close - 1;
            while (previous >= append.Array.Start && char.IsWhiteSpace(encoding.Text[previous]))
            {
                previous--;
            }

            var separator = append.Array.Items.Count > 0 && encoding.Text[previous] != ',' ? ", " : "";
            replacements.Add(new Replacement(append.Array.Close, append.Array.Close, separator + string.Join(", ", append.Values.Select(n => n.Render()))));
        }

        var result = Replace(encoding.Text, replacements);
        _ = new SsoParser(result).Document();
        return new TextPreview(encoding.Encode(result), changes);
        Node Value(JsonElement definition)
        {
            string Label()
            {
                if (activeModCount < 1)
                {
                    throw new CheckFailure("unsupported", "An active-mod label requires at least one enabled package.");
                }

                var prefix = definition.TryGetProperty("prefix", out var start) ? start.GetString() : "";
                var suffix = definition.TryGetProperty("suffix", out var end) ? end.GetString() : "";
                return prefix + $"SnowPatcher: {activeModCount} {(activeModCount == 1 ? "mod" : "mods")} active" + suffix;
            }

            if (definition.TryGetProperty("activeModCount", out _))
            {
                return Node.FromJson(JsonSerializer.SerializeToElement(Label()));
            }

            if (definition.TryGetProperty("json", out var json))
            {
                return Node.FromJson(json);
            }

            var fragment = TextEncoding.Read(package.ReadPayload(definition.GetProperty("ssoFile").GetString()!, token));
            return new SsoParser(fragment.Text).Fragment();
        }
    }

    private static (Node Parent, Node? Value, Property? Property) Resolve(Node root, string[] path)
    {
        var parent = root;
        for (var i = 0; i < path.Length; i++)
        {
            var part = path[i];
            Node? value = null;
            Property? property = null;
            if (part.StartsWith("s:", StringComparison.Ordinal) && parent.Kind == "object")
            {
                parent.Properties.TryGetValue(part[2..], out property);
                value = property?.Value;
            }
            else if (part.StartsWith("i:", StringComparison.Ordinal) && parent.Kind == "array")
            {
                if (!int.TryParse(part[2..], out var index) || index >= parent.Items.Count)
                {
                    throw new CheckFailure("changed", "An SSO array index no longer exists.");
                }

                value = parent.Items[index];
            }
            else
            {
                throw new CheckFailure("changed", $"The SSO parent type changed at {Display(path[..(i + 1)])}.");
            }

            if (i == path.Length - 1)
            {
                return (parent, value, property);
            }

            parent = value ?? throw new CheckFailure("missing", $"The SSO parent is missing at {Display(path[..(i + 1)])}.");
        }

        throw new InvalidDataException("Empty SSO path.");
    }

    private static string Display(IEnumerable<string> path) => string.Join("/", path.Select(p => p.Length > 2 ? p[2..] : p));
    private static string PropertyName(string name) => PropertyIdentifier().IsMatch(name) ? name : JsonSerializer.Serialize(name, TextJson);
}
