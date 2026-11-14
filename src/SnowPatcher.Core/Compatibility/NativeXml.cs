using System.Xml;

namespace SnowPatcher.Core.Compatibility;

internal sealed record NativeXmlAttribute(string Name, int Start, int End, int ValueStart, int ValueEnd, char Quote);
internal sealed record NativeXmlElement(string[] Path, int NameEnd, IReadOnlyList<NativeXmlAttribute> Attributes);

// SnowRunner permits XML fragments and duplicate unrelated attributes in some stock files.
// Keep their bytes; require an unambiguous element and attribute for each edited value.
internal static class NativeXml
{
    public static IReadOnlyList<NativeXmlElement> Read(string text, CancellationToken token)
    {
        var elements = new List<NativeXmlElement>();
        var stack = new List<string>();
        var position = 0;
        while (position < text.Length)
        {
            token.ThrowIfCancellationRequested();
            if (text[position++] != '<')
            {
                continue;
            }
            if (Skip("!--", "-->") || Skip("![CDATA[", "]]>") || Skip("?", "?>"))
            {
                continue;
            }
            if (position == text.Length || text[position] == '!')
            {
                throw new XmlException("DTD and other markup declarations are not supported.");
            }
            if (text[position] == '/')
            {
                position++;
                var name = Name();
                WhiteSpace();
                Take('>');
                if (stack.Count == 0 || stack[^1] != name)
                {
                    throw new XmlException("An XML closing tag does not match its opening tag.");
                }
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            var elementName = Name();
            var nameEnd = position;
            var attributes = new List<NativeXmlAttribute>();
            var selfClosing = false;
            while (true)
            {
                var beforeSpace = position;
                WhiteSpace();
                if (position < text.Length && text[position] == '/')
                {
                    position++;
                    selfClosing = true;
                    Take('>');
                    break;
                }
                if (position < text.Length && text[position] == '>')
                {
                    position++;
                    break;
                }
                if (beforeSpace == position)
                {
                    throw new XmlException("An XML attribute needs separating whitespace.");
                }
                var start = position;
                var attributeName = Name();
                WhiteSpace();
                Take('=');
                WhiteSpace();
                if (position == text.Length || text[position] is not ('\'' or '"'))
                {
                    throw new XmlException("An XML attribute must have a quoted value.");
                }
                var quote = text[position++];
                var valueStart = position;
                position = text.IndexOf(quote, position);
                if (position < 0)
                {
                    throw new XmlException("An XML attribute value is not terminated.");
                }
                var valueEnd = position++;
                attributes.Add(new(attributeName, start, position, valueStart, valueEnd, quote));
            }
            elements.Add(new([.. stack, elementName], nameEnd, attributes));
            if (!selfClosing)
            {
                stack.Add(elementName);
            }
        }
        if (stack.Count != 0 || elements.Count == 0)
        {
            throw new XmlException("The XML resource is empty or has unclosed tags.");
        }
        return elements;

        bool Skip(string prefix, string terminator)
        {
            if (!text.AsSpan(position).StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
            var end = text.IndexOf(terminator, position + prefix.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new XmlException("An XML comment, CDATA section or processing instruction is not terminated.");
            }
            position = end + terminator.Length;
            return true;
        }
        string Name()
        {
            var start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('/' or '>' or '=' or '<' or '\'' or '"'))
            {
                position++;
            }
            if (position == start)
            {
                throw new XmlException("An XML element or attribute name is missing.");
            }
            return XmlConvert.VerifyName(text[start..position]);
        }
        void WhiteSpace()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
        }
        void Take(char expected)
        {
            if (position == text.Length || text[position++] != expected)
            {
                throw new XmlException($"Expected '{expected}' in XML markup.");
            }
        }
    }

    public static string Value(string text, NativeXmlAttribute attribute)
    {
        var value = text[attribute.ValueStart..attribute.ValueEnd];
        using var reader = XmlReader.Create(new StringReader($"<v a={attribute.Quote}{value}{attribute.Quote}/>"),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        reader.MoveToContent();
        return reader.GetAttribute("a")!;
    }

    public static string Escape(string value)
    {
        XmlConvert.VerifyXmlChars(value);
        return value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal).Replace("\r", "&#13;", StringComparison.Ordinal)
            .Replace("\n", "&#10;", StringComparison.Ordinal).Replace("\t", "&#9;", StringComparison.Ordinal);
    }
}
