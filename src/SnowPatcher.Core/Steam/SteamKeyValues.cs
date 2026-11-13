using System.Text;

namespace SnowPatcher.Core.Steam;

// Reads Steam's text manifests without changing their contents.
internal sealed class SteamKeyValues
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SteamKeyValues> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static SteamKeyValues Parse(string text) => new Reader(text).ReadObject();

    private enum TokenKind
    {
        Value, Open, Close
    }
    private readonly record struct Token(TokenKind Kind, string Text = "");

    private sealed class Reader(string text)
    {
        private int _position;

        public SteamKeyValues ReadObject(bool nested = false, int depth = 0)
        {
            if (depth > 32)
            {
                throw new FormatException("Steam manifest nesting is too deep.");
            }

            var result = new SteamKeyValues();

            while (ReadToken() is { } key)
            {
                if (key.Kind == TokenKind.Close)
                {
                    if (!nested)
                    {
                        throw new FormatException("Unexpected closing brace in Steam manifest.");
                    }

                    return result;
                }

                if (key.Kind != TokenKind.Value || ReadToken() is not { } value)
                {
                    throw new FormatException("Missing key or value in Steam manifest.");
                }

                if (value.Kind == TokenKind.Open)
                {
                    result.Objects[key.Text] = ReadObject(nested: true, depth + 1);
                }
                else if (value.Kind == TokenKind.Value)
                {
                    result.Values[key.Text] = value.Text;
                }
                else
                {
                    throw new FormatException("Missing value in Steam manifest.");
                }
            }

            if (nested)
            {
                throw new FormatException("Unclosed object in Steam manifest.");
            }

            return result;
        }

        private Token? ReadToken()
        {
            while (_position < text.Length)
            {
                if (char.IsWhiteSpace(text[_position]) || text[_position] == '\uFEFF')
                {
                    _position++;
                    continue;
                }

                if (text[_position] == '/' && _position + 1 < text.Length && text[_position + 1] == '/')
                {
                    while (_position < text.Length && text[_position] != '\n')
                    {
                        _position++;
                    }

                    continue;
                }

                break;
            }

            if (_position == text.Length)
            {
                return null;
            }

            var current = text[_position++];
            if (current == '{')
            {
                return new Token(TokenKind.Open);
            }

            if (current == '}')
            {
                return new Token(TokenKind.Close);
            }

            if (current == '"')
            {
                var value = new StringBuilder();
                while (_position < text.Length)
                {
                    current = text[_position++];
                    if (current == '"')
                    {
                        return new Token(TokenKind.Value, value.ToString());
                    }

                    if (current == '\\' && _position < text.Length && text[_position] is '\\' or '"')
                    {
                        current = text[_position++];
                    }

                    value.Append(current);
                }

                throw new FormatException("Unclosed string in Steam manifest.");
            }

            var start = _position - 1;
            while (_position < text.Length && !char.IsWhiteSpace(text[_position]) && text[_position] is not ('{' or '}'))
            {
                _position++;
            }

            return new Token(TokenKind.Value, text[start.._position]);
        }
    }
}
