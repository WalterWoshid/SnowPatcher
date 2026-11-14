using System.Text.Json;

namespace SnowPatcher.Core.Compatibility;

internal sealed partial class SslModule
{
    private static JsonElement[] ExtractInsertions((int Op, int Arg)[] before, (int Op, int Arg)[] after, JsonElement definition)
    {
        var insertions = definition.GetProperty("insertions").EnumerateArray()
            .Select(e => (At: e.GetProperty("at").GetInt32(), Length: e.GetProperty("length").GetInt32())).ToArray();
        Require(insertions.Length > 0 && insertions.All(e => e.At >= 0 && e.At <= before.Length && e.Length > 0) &&
            insertions.Select(e => e.At).SequenceEqual(insertions.Select(e => e.At).Distinct().Order()), "Invalid insertion boundaries.");
        var entries = new int[before.Length + 1];
        var originals = new int[before.Length + 1];
        var sizes = insertions.ToDictionary(e => e.At, e => e.Length);
        var position = 0;
        for (var i = 0; i <= before.Length; i++)
        {
            entries[i] = position;
            position = checked(position + sizes.GetValueOrDefault(i));
            originals[i] = position;
            if (i < before.Length)
            {
                position++;
            }
        }
        Require(position == after.Length, "The declared insertions do not match the payload size.");
        for (var i = 0; i < before.Length; i++)
        {
            var expected = before[i];
            if (Branches.Contains(expected.Op))
            {
                Require(expected.Arg >= 0 && expected.Arg <= before.Length, "Invalid original jump.");
                expected.Arg = entries[expected.Arg];
            }
            Require(after[originals[i]] == expected, "The extension replaces or removes an existing instruction.");
        }

        var oldTargets = entries.Select((value, index) => (value, index)).ToDictionary(p => p.value, p => p.index);
        return insertions.Select(insertion =>
        {
            var start = entries[insertion.At];
            var end = originals[insertion.At];
            var rows = new List<object>();
            for (var i = start; i < end; i++)
            {
                rows.Add(new { label = "extension_" + i });
                var instruction = after[i];
                object operand = instruction.Arg;
                if (Branches.Contains(instruction.Op))
                {
                    if (oldTargets.TryGetValue(instruction.Arg, out var original))
                    {
                        operand = new { original };
                    }
                    else
                    {
                        Require(instruction.Arg >= start && instruction.Arg <= end, "A new jump crosses insertion blocks.");
                        operand = new { label = "extension_" + instruction.Arg };
                    }
                }
                rows.Add(new { op = instruction.Op, arg = operand });
            }
            rows.Add(new { label = "extension_" + end });
            var atEnd = insertion.At == before.Length;
            var match = (atEnd ? before : before[insertion.At..]).Select(i => new[] { i.Op, i.Arg }).ToArray();
            return JsonSerializer.SerializeToElement(new { match, mode = atEnd ? "after" : "before", incoming = "include", code = rows });
        }).ToArray();
    }

}
