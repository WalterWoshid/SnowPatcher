using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Compatibility;

internal sealed record TextChange(string[] Path, string Action, string Requirement, string Outcome);
internal sealed record TextPreview(byte[] Bytes, IReadOnlyList<TextChange> Changes);
internal static partial class TextEdits
{
    // These are native game files, not HTML documents. Keep literal Unicode and
    // rich-text markup while escaping quotes, backslashes and control characters.
    private static readonly JsonSerializerOptions TextJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private sealed record Replacement(int Start, int End, string Text);
    private static string Replace(string text, IEnumerable<Replacement> replacements)
    {
        var result = new StringBuilder(text);
        var previousStart = text.Length + 1;
        foreach (var replacement in replacements.GroupBy(r => (r.Start, r.End)).Select(g => g.Key.Start == g.Key.End ? new Replacement(g.Key.Start, g.Key.End, string.Concat(g.Select(r => r.Text))) : g.Single()).OrderByDescending(r => r.Start))
        {
            if (replacement.End > previousStart)
            {
                throw new CheckFailure("conflict", "Text edits overlap.");
            }

            result.Remove(replacement.Start, replacement.End - replacement.Start).Insert(replacement.Start, replacement.Text);
            previousStart = replacement.Start;
        }

        return result.ToString();
    }

    private sealed record TextEncoding(string Text, Encoding Encoding, byte[] Preamble)
    {
        public byte[] Encode(string text) => [.. Preamble, .. Encoding.GetBytes(text)];
        public static TextEncoding Read(byte[] bytes)
        {
            if (bytes.Length > 16 * 1024 * 1024)
            {
                throw new CheckFailure("unsupported", "The text resource exceeds the checker's 16 MiB limit.");
            }

            Encoding encoding;
            var skip = 0;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
            {
                encoding = new UnicodeEncoding(false, false, true);
                skip = 2;
            }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            {
                encoding = new UnicodeEncoding(true, false, true);
                skip = 2;
            }
            else
            {
                encoding = new UTF8Encoding(false, true);
                if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
                {
                    skip = 3;
                }
            }

            return new TextEncoding(encoding.GetString(bytes, skip, bytes.Length - skip), encoding, bytes[..skip]);
        }
    }
}
