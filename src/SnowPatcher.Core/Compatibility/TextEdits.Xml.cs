using System.Text.Json;
using System.Xml;

namespace SnowPatcher.Core.Compatibility;

internal static partial class TextEdits
{
    public static TextPreview Xml(byte[] bytes, JsonElement operation, bool guards, CancellationToken token)
    {
        try
        {
            var encoding = TextEncoding.Read(bytes);
            var elements = NativeXml.Read(encoding.Text, token);
            var edits = operation.GetProperty("edits").EnumerateArray().Select(XmlAttributeEdit.Read).ToArray();
            var replacements = new List<Replacement>();
            var changes = new List<TextChange>();
            foreach (var edit in edits)
            {
                token.ThrowIfCancellationRequested();
                var (element, current) = ResolveXmlAttribute(elements, edit.Path, edit.Attribute);
                if (guards && (edit.Absent ? current is not null : current is null || NativeXml.Value(encoding.Text, current) != edit.Before))
                {
                    throw new CheckFailure("changed", $"The old XML attribute does not match: {string.Join('/', edit.Path)}/@{edit.Attribute}.");
                }
                var value = edit.Value;
                changes.Add(new([.. edit.Path, "@" + edit.Attribute], edit.Action, edit.Absent ? "absent" : JsonSerializer.Serialize(edit.Before),
                    value is null ? "absent" : JsonSerializer.Serialize(value)));
                if (value is null)
                {
                    if (current is not null)
                    {
                        replacements.Add(new(current.Start, current.End, ""));
                    }
                }
                else if (current is null)
                {
                    replacements.Add(new(element.NameEnd, element.NameEnd, $" {edit.Attribute}=\"{NativeXml.Escape(value)}\""));
                }
                else
                {
                    replacements.Add(new(current.ValueStart, current.ValueEnd, NativeXml.Escape(value)));
                }
            }
            var output = Replace(encoding.Text, replacements);
            var updated = NativeXml.Read(output, token);
            foreach (var edit in edits)
            {
                token.ThrowIfCancellationRequested();
                var (_, current) = ResolveXmlAttribute(updated, edit.Path, edit.Attribute);
                if (edit.Value is null ? current is not null : current is null || NativeXml.Value(output, current) != edit.Value)
                {
                    throw new CheckFailure("changed", "The XML output does not match the planned attribute changes.");
                }
            }
            return new(encoding.Encode(output), changes);
        }
        catch (XmlException error)
        {
            throw new CheckFailure("unsupported", "The XML resource cannot be edited: " + error.Message);
        }
    }

    private static (NativeXmlElement Element, NativeXmlAttribute? Attribute) ResolveXmlAttribute(
        IReadOnlyList<NativeXmlElement> elements, string[] path, string attribute)
    {
        var matches = elements.Where(e => e.Path.SequenceEqual(path)).ToArray();
        if (matches.Length != 1)
        {
            throw new CheckFailure("changed", $"The XML element is missing or ambiguous: {string.Join('/', path)}.");
        }
        var attributes = matches[0].Attributes.Where(a => a.Name == attribute).ToArray();
        if (attributes.Length > 1)
        {
            throw new CheckFailure("changed", $"The XML attribute is ambiguous: {string.Join('/', path)}/@{attribute}.");
        }
        return (matches[0], attributes.SingleOrDefault());
    }

    private sealed record XmlAttributeEdit(string[] Path, string Attribute, string Action, bool Absent, string? Before, string? Value)
    {
        public static XmlAttributeEdit Read(JsonElement edit)
        {
            var absent = edit.GetProperty("before").GetProperty("state").GetString() == "absent";
            var action = edit.GetProperty("action").GetString()!;
            return new(edit.GetProperty("path").EnumerateArray().Select(p => p.GetString()!).ToArray(),
                edit.GetProperty("attribute").GetString()!, action, absent,
                absent ? null : edit.GetProperty("before").GetProperty("value").GetString(),
                action == "remove" ? null : edit.GetProperty("value").GetString());
        }
    }
}
