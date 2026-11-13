using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Compatibility;

internal static partial class TextEdits
{
    private sealed record Property(Node Value, int Start);
    private sealed class Node(string kind, string scalar = "")
    {
        public string Kind { get; } = kind;
        public string Scalar { get; } = scalar;
        public Dictionary<string, Property> Properties { get; } = new(StringComparer.Ordinal);
        public List<Node> Items { get; } = [];
        public int Start { get; set; }
        public int End { get; set; }
        public int Close { get; set; }
        public string Fingerprint => Kind switch
        {
            "object" => "o:" + JsonSerializer.Serialize(Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new[] { p.Key, p.Value.Value.Fingerprint })),
            "array" => "a:" + JsonSerializer.Serialize(Items.Select(n => n.Fingerprint)),
            _ => Kind + ":" + Scalar
        };

        // Keep native identifiers (including __type) and assignment line breaks.
        // Our inspection parser also accepts compact forms the game may reject.
        public string Render() => Kind switch
        {
            "object" => "{\n" + string.Join("\n", Properties.Select(p => PropertyName(p.Key) + " = " + p.Value.Value.Render())) + "\n}",
            "array" => "[\n" + string.Join(",\n", Items.Select(n => n.Render())) + "\n]",
            "string" => JsonSerializer.Serialize(Scalar, TextJson),
            "boolean" => Scalar == "true" ? "True" : "False",
            "null" => "null",
            _ => Scalar
        };
        public static Node FromJson(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    var obj = new Node("object");
                    foreach (var property in value.EnumerateObject())
                    {
                        obj.Properties.Add(property.Name, new Property(FromJson(property.Value), 0));
                    }

                    return obj;
                case JsonValueKind.Array:
                    var array = new Node("array");
                    array.Items.AddRange(value.EnumerateArray().Select(FromJson));
                    return array;
                case JsonValueKind.String:
                    return new Node("string", value.GetString()!);
                case JsonValueKind.Number:
                    return new Node("number", value.GetRawText());
                case JsonValueKind.True:
                    return new Node("boolean", "true");
                case JsonValueKind.False:
                    return new Node("boolean", "false");
                case JsonValueKind.Null:
                    return new Node("null");
                default:
                    throw new InvalidDataException("Unsupported SSO value.");
            }
        }
    }

    private sealed class SsoParser(string text)
    {
        private int _position;
        private int _nodes;
        public Node Document()
        {
            Skip();
            var value = _position < text.Length && text[_position] == '{' ? Value(0) : Object(false, 0);
            Skip();
            if (_position != text.Length)
            {
                throw new CheckFailure("unsupported", "Unconsumed SSO syntax.");
            }

            if (value.Kind != "object")
            {
                throw new CheckFailure("unsupported", "The SSO document is not an object.");
            }

            return value;
        }

        public Node Fragment()
        {
            var value = Value(0);
            Skip();
            if (_position != text.Length)
            {
                throw new CheckFailure("unsupported", "Unconsumed SSO fragment syntax.");
            }

            return value;
        }

        private Node Value(int depth)
        {
            Skip();
            if (depth > 64 || ++_nodes > 200000 || _position >= text.Length)
            {
                throw new CheckFailure("unsupported", "Unsupported SSO depth, size or truncated value.");
            }

            var start = _position;
            Node node;
            if (text[_position] == '{')
            {
                return Object(true, depth + 1);
            }

            if (text[_position] == '[')
            {
                _position++;
                node = new Node("array")
                {
                    Start = start
                };
                while (true)
                {
                    Skip();
                    if (_position >= text.Length)
                    {
                        throw new InvalidDataException("Truncated SSO array.");
                    }

                    if (text[_position] == ']')
                    {
                        node.Close = _position++;
                        break;
                    }

                    node.Items.Add(Value(depth + 1));
                    Skip();
                    if (_position < text.Length && text[_position] == ',')
                    {
                        _position++;
                    }
                }
            }
            else if (text[_position] == '"')
            {
                node = new Node("string", Quoted());
            }
            else
            {
                var match = Atom().Match(text, _position);
                if (!match.Success || match.Index != _position)
                {
                    throw new CheckFailure("unsupported", "The SSO file contains unsupported syntax.");
                }

                var atom = match.Value;
                _position += atom.Length;
                node = atom switch
                {
                    "True" => new Node("boolean", "true"),
                    "False" => new Node("boolean", "false"),
                    "null" or "None" => new Node("null"),
                    _ => new Node(char.IsAsciiLetter(atom[0]) || atom[0] == '_' ? "symbol" : "number", atom)
                };
            }

            node.Start = start;
            node.End = _position;
            return node;
        }

        private Node Object(bool braced, int depth)
        {
            if (depth > 64)
            {
                throw new CheckFailure("unsupported", "The SSO nesting is too deep.");
            }

            var node = new Node("object")
            {
                Start = _position
            };
            if (braced)
            {
                _position++;
            }

            while (true)
            {
                Skip();
                if (_position == text.Length)
                {
                    if (braced)
                    {
                        throw new InvalidDataException("Truncated SSO object.");
                    }

                    node.Close = _position;
                    break;
                }

                if (braced && text[_position] == '}')
                {
                    node.Close = _position++;
                    break;
                }

                if (text[_position] == ',')
                {
                    _position++;
                    continue;
                }

                var start = _position;
                string key;
                if (text[_position] == '"')
                {
                    key = Quoted();
                }
                else
                {
                    var match = Identifier().Match(text, _position);
                    if (!match.Success || match.Index != _position)
                    {
                        throw new CheckFailure("unsupported", "Unsupported SSO property name.");
                    }

                    key = match.Value;
                    _position += key.Length;
                }

                Skip();
                if (_position >= text.Length || text[_position++] != '=')
                {
                    throw new CheckFailure("unsupported", "Expected an SSO assignment.");
                }

                if (!node.Properties.TryAdd(key, new Property(Value(depth + 1), start)))
                {
                    throw new CheckFailure("unsupported", $"Duplicate SSO property: {key}");
                }
            }

            node.End = _position;
            return node;
        }

        private string Quoted()
        {
            var start = _position++;
            while (_position < text.Length)
            {
                var c = text[_position++];
                if (c == '\\')
                {
                    _position++;
                    continue;
                }

                if (c == '"')
                {
                    return JsonSerializer.Deserialize<string>(text[start.._position])!;
                }
            }

            throw new InvalidDataException("Truncated quoted SSO string.");
        }

        private void Skip()
        {
            while (_position < text.Length)
            {
                if (char.IsWhiteSpace(text[_position]))
                {
                    _position++;
                    continue;
                }

                if (text.AsSpan(_position).StartsWith("//"))
                {
                    var end = text.IndexOf('\n', _position);
                    _position = end < 0 ? text.Length : end + 1;
                    continue;
                }

                if (text.AsSpan(_position).StartsWith("/*"))
                {
                    var end = text.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        throw new InvalidDataException("Unclosed SSO comment.");
                    }

                    _position = end + 2;
                    continue;
                }

                break;
            }
        }
    }

    [GeneratedRegex(@"\G(?:-?[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?|[A-Za-z_][A-Za-z_0-9]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Atom();
    [GeneratedRegex(@"\G[A-Za-z_][A-Za-z_0-9]*", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z_0-9]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyIdentifier();
}
