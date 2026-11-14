using System.Buffers.Binary;
using System.Text.Json;

namespace SnowPatcher.Core.Compatibility;

internal sealed record ExtensionFunction(int Identity, string BodyHash, JsonElement Definition);
internal sealed record ModuleExtension(byte[] Scaffold, IReadOnlyList<ExtensionFunction> Functions);

internal sealed partial class SslModule
{
    // The payload may append tables/members/constants and insert instructions.
    // Existing declarations and instructions must survive exactly, including jumps.
    internal ModuleExtension Extend(byte[] payload, JsonElement operation, CancellationToken token)
    {
        var added = new SslModule(payload);
        Require(_bytes.AsSpan(0, 12).SequenceEqual(payload.AsSpan(0, 12)), "The module identity changed.");
        ValidateExtensionLayout(added, token);

        Require(added._bodies.Length >= _bodies.Length, "An existing function was removed.");
        var definitions = new Dictionary<int, JsonElement>();
        foreach (var definition in operation.GetProperty("functions").EnumerateArray())
        {
            Require(definitions.TryAdd(FindBody(definition), definition), "More than one insertion definition refers to the same function body.");
        }
        var functions = new List<ExtensionFunction>();
        var restoredBodies = new Dictionary<Field, byte[]>();
        for (var i = 0; i < _bodies.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var before = Fields(_bodies[i].Data);
            var after = Fields(added._bodies[i].Data);
            Require(before.All(f => f.Number is >= 1 and <= 5) && after.All(f => f.Number is >= 1 and <= 5), "Unknown function metadata needs review.");
            Same(before.Where(f => f.Number is not (4 or 5)), after.Where(f => f.Number is not (4 or 5)));
            Prefix(before.Where(f => f.Number == 4), after.Where(f => f.Number == 4));
            var oldStream = One(before, 5, 2);
            var newStream = One(after, 5, 2);
            if (definitions.TryGetValue(i, out var definition))
            {
                var edits = ExtractInsertions(Code(oldStream.Data), Code(newStream.Data), definition);
                var function = JsonSerializer.SerializeToElement(new
                {
                    @class = definition.GetProperty("class").GetString(),
                    name = definition.GetProperty("name").GetString(),
                    bodySha256 = GameResources.Hash(_bodies[i].Data)
                });
                functions.Add(new ExtensionFunction(i, GameResources.Hash(_bodies[i].Data), JsonSerializer.SerializeToElement(new
                {
                    function,
                    moduleContextSha256 = added.ContextHash(),
                    edits
                })));
            }
            else
            {
                Require(oldStream.Data.AsSpan().SequenceEqual(newStream.Data), "A function changed without declared insertions.");
                Require(before.Count(f => f.Number == 4) == after.Count(f => f.Number == 4), "Function constants changed without declared insertions.");
            }

            if (!oldStream.Data.AsSpan().SequenceEqual(newStream.Data))
            {
                restoredBodies.Add(added._bodies[i], Replace(after, newStream, oldStream.Data));
            }
        }

        Require(definitions.Count == functions.Count, "An insertion names a new or missing function.");
        var scaffold = added._fields.Select(field => restoredBodies.TryGetValue(field, out var body)
            ? field with { Data = body, Encoded = EncodeField(field.Number, body) }
            : field).ToList();
        return new ModuleExtension(EncodeModule(scaffold), functions);
    }

    private static byte[] EncodeField(int number, byte[] bytes)
    {
        using var output = new MemoryStream();
        WriteVarint(output, ((ulong)number << 3) | 2);
        WriteVarint(output, (ulong)bytes.Length);
        output.Write(bytes);
        return output.ToArray();
    }

    internal int InstructionCount(JsonElement definition) => Code(One(Fields(_bodies[FindBody(definition)].Data), 5, 2).Data).Length;

    private byte[] EncodeModule(List<Field> fields)
    {
        var payload = fields.SelectMany(f => f.Encoded).ToArray();
        var output = new byte[16 + payload.Length];
        _bytes.AsSpan(0, 12).CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), checked((uint)payload.Length));
        payload.CopyTo(output, 16);
        return output;
    }

    private static (int Op, int Arg)[] Code(byte[] bytes) => Fields(bytes).Select(f =>
    {
        Require(f.Number == 1 && f.Wire == 0 && f.Integer <= uint.MaxValue, "Invalid instruction encoding.");
        var value = (long)f.Integer - 0x80000000L;
        return ((int)(value & 255), (int)(value >> 8));
    }).ToArray();

    private static void Prefix(IEnumerable<Field> before, IEnumerable<Field> after)
    {
        var original = before.ToArray();
        Same(original, after.Take(original.Length));
    }

    private static void Same(IEnumerable<Field> before, IEnumerable<Field> after) =>
        Require(before.SelectMany(f => f.Encoded).SequenceEqual(after.SelectMany(f => f.Encoded)), "The extension changes existing module structure.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new CheckFailure("changed", message);
        }
    }
}
