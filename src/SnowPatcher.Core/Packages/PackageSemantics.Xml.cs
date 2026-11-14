using System.Text.Json;
using System.Xml;

namespace SnowPatcher.Core.Packages;

internal static partial class PackageSemantics
{
    private static void ValidateXml(PackageOperation operation)
    {
        var paths = new List<string>();
        try
        {
            foreach (var edit in operation.Definition.GetProperty("edits").EnumerateArray())
            {
                var path = edit.GetProperty("path").EnumerateArray().Select(p => XmlConvert.VerifyName(p.GetString()!)).ToArray();
                var attribute = XmlConvert.VerifyName(edit.GetProperty("attribute").GetString()!);
                paths.Add(JsonSerializer.Serialize(path.Append("@" + attribute)));
                if (edit.GetProperty("before").TryGetProperty("value", out var old))
                {
                    XmlConvert.VerifyXmlChars(old.GetString()!);
                }
                if (edit.TryGetProperty("value", out var value))
                {
                    XmlConvert.VerifyXmlChars(value.GetString()!);
                }
                if (edit.GetProperty("action").GetString() == "remove" && edit.GetProperty("before").GetProperty("state").GetString() != "present")
                {
                    throw new InvalidDataException("Removing an XML attribute requires a present old-value guard.");
                }
            }
        }
        catch (XmlException error)
        {
            throw new InvalidDataException("An XML edit contains an invalid name or value.", error);
        }
        Unique(paths, $"XML attribute paths in {operation.Id}");
    }
}
