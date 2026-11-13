using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SnowPatcher.Core.Compatibility;

internal sealed partial class SslModule
{
    internal byte[] AddMethods(JsonElement definition, bool guards, CancellationToken token)
    {
        using var context = new MemoryStream();
        foreach (var field in _fields.Where(f => f.Number != 8))
        {
            context.Write(field.Encoded);
        }

        if (guards && GameResources.Hash(context.ToArray()) != definition.GetProperty("moduleContextSha256").GetString())
        {
            throw new CheckFailure("changed", "The method's class layout changed. This mod needs an update.");
        }

        var matches = _fields.Where(f => f.Number == 7 && f.Wire == 2 &&
            StringAt(One(Fields(f.Data), 1, 0).Integer) == definition.GetProperty("class").GetString()).ToArray();
        if (matches.Length != 1)
        {
            throw new CheckFailure("unsupported", "The class could not be resolved uniquely.");
        }

        var owner = matches[0];
        var members = Fields(owner.Data).Where(f => f.Number == 3 && f.Wire == 2).Select(f => Fields(f.Data)).ToArray();
        var templates = members.Where(m => StringAt(One(m, 1, 0).Integer) == definition.GetProperty("templateMember").GetString()).ToArray();
        if (templates.Length != 1 || One(templates[0], 3, 0).Integer != 2 || templates[0].Any(f => f.Number is not (1 or 2 or 3 or 4 or 7)))
        {
            throw new CheckFailure("unsupported", "The template is not a supported script method.");
        }

        var template = templates[0];
        var strings = _strings.ToList();
        var symbols = _fields.Where(f => f.Number == 5 && f.Wire == 2).Select(f => Fields(f.Data)).ToArray();
        var referenceTable = One(_fields, 4, 2);
        var references = Fields(referenceTable.Data);
        var methodReferences = references.Where(f => f.Number == 4 && f.Wire == 2).Select(f => Fields(f.Data)).Where(r =>
            One(r, 1, 0).Integer == 2 && StringAt(One(r, 2, 0).Integer) == definition.GetProperty("templateMember").GetString()).ToArray();
        if (methodReferences.Length != 1 || methodReferences[0].Any(f => f.Number is < 1 or > 5 || f.Wire != 0))
        {
            throw new CheckFailure("unsupported", "The inherited method reference could not be resolved safely.");
        }

        var referenceTemplate = methodReferences[0];
        using var changedReferences = new MemoryStream();
        changedReferences.Write(referenceTable.Data);
        var names = members.Select(m => StringAt(One(m, 1, 0).Integer)).ToHashSet(StringComparer.Ordinal);
        var slots = members.Select(m => One(m, 2, 0).Integer).ToHashSet();
        using var additions = new MemoryStream();
        var signatureSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        for (var i = 0; i < symbols.Length; i++)
        {
            if (One(symbols[i], 1, 0).Integer == 9)
            {
                signatureSymbols.TryAdd(Convert.ToHexString(One(symbols[i], 4, 2).Data), (ulong)i);
            }
        }

        var nextSymbol = (ulong)symbols.Length;
        using var changedClass = new MemoryStream();
        changedClass.Write(owner.Data);
        var functionIndex = _bodies.Length;
        foreach (var method in definition.GetProperty("methods").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var name = method.GetProperty("name").GetString()!;
            var slot = checked((ulong)method.GetProperty("slot").GetInt32());
            if (!names.Add(name) || !slots.Add(slot))
            {
                throw new CheckFailure("changed", "The class already declares that method name or slot.");
            }

            using var member = new MemoryStream();
            foreach (var field in template)
            {
                if (field.Number is 1 or 2 or 4)
                {
                    Integer(member, field.Number, field.Number == 1 ? (ulong)String(name) : field.Number == 2 ? slot : (ulong)functionIndex);
                }
                else
                {
                    member.Write(field.Encoded);
                }
            }
            Data(changedClass, 3, member.ToArray());
            var signature = method.GetProperty("signature");
            using var type = new MemoryStream();
            Integer(type, 1, checked((ulong)ResolveType(signature.GetProperty("returns").GetString()!)));
            foreach (var argument in signature.GetProperty("arguments").EnumerateArray())
            {
                Integer(type, 2, checked((ulong)ResolveType(argument.GetString()!)));
            }

            var signatureKey = Convert.ToHexString(type.ToArray());
            if (!signatureSymbols.TryGetValue(signatureKey, out var signatureSymbol))
            {
                signatureSymbol = nextSymbol++;
                signatureSymbols.Add(signatureKey, signatureSymbol);
                using var symbol = new MemoryStream();
                Integer(symbol, 1, 9);
                Data(symbol, 4, type.ToArray());
                Data(additions, 5, symbol.ToArray());
            }
            using var methodReference = new MemoryStream();
            foreach (var field in referenceTemplate)
            {
                Integer(methodReference, field.Number, field.Number == 2 ? (ulong)String(name) : field.Number == 4 ? slot : field.Number == 5 ? signatureSymbol : field.Integer);
            }

            Data(changedReferences, 4, methodReference.ToArray());
            using var debug = new MemoryStream();
            Integer(debug, 1, (ulong)String(name));
            for (var i = 0; i < signature.GetProperty("arguments").GetArrayLength(); i++)
            {
                using var arg = new MemoryStream();
                Integer(arg, 1, (ulong)String($"arg{i}"));
                Data(debug, 2, arg.ToArray());
            }
            using var body = new MemoryStream();
            Integer(body, 1, signatureSymbol);
            Data(body, 2, debug.ToArray());
            Integer(body, 3, 0);
            var constants = method.GetProperty("constants").EnumerateArray().ToArray();
            foreach (var constant in constants)
            {
                using var value = new MemoryStream();
                if (constant.TryGetProperty("string", out var text))
                {
                    Integer(value, 1, 4);
                    Integer(value, 5, (ulong)String(text.GetString()!));
                }
                else if (constant.TryGetProperty("float", out var number))
                {
                    if (!number.TryGetSingle(out var scalar) || !float.IsFinite(scalar))
                    {
                        throw new CheckFailure("unsupported", "A method float constant is not finite.");
                    }

                    Integer(value, 1, 3);
                    WriteVarint(value, 37);
                    var bytes = new byte[4];
                    BinaryPrimitives.WriteSingleLittleEndian(bytes, scalar);
                    value.Write(bytes);
                }
                else
                {
                    var index = constant.GetProperty("typeSymbol").GetInt32();
                    if (index >= symbols.Length || One(symbols[index], 1, 0).Integer is not (1 or 3 or 4))
                    {
                        throw new CheckFailure("unsupported", "The method uses an unsupported type symbol.");
                    }

                    Integer(value, 1, 5);
                    Integer(value, 6, (ulong)index);
                }
                Data(body, 4, value.ToArray());
            }
            Data(body, 5, Assemble(method.GetProperty("code"), constants.Length, signature.GetProperty("arguments").GetArrayLength() + 1, token));
            Data(additions, 8, body.ToArray());
            functionIndex++;
        }
        for (var i = _strings.Length; i < strings.Count; i++)
        {
            Data(additions, 6, Encoding.UTF8.GetBytes(strings[i]));
        }

        using var payload = new MemoryStream();
        foreach (var field in _fields)
        {
            if (ReferenceEquals(field, owner))
            {
                Data(payload, 7, changedClass.ToArray());
            }
            else if (ReferenceEquals(field, referenceTable))
            {
                Data(payload, 4, changedReferences.ToArray());
            }
            else
            {
                payload.Write(field.Encoded);
            }
        }
        payload.Write(additions.ToArray());
        var result = new byte[16 + payload.Length];
        _bytes.AsSpan(0, 12).CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), checked((uint)payload.Length));
        payload.ToArray().CopyTo(result, 16);
        return result;

        int String(string value)
        {
            var index = strings.IndexOf(value);
            if (index >= 0)
            {
                return index;
            }

            strings.Add(value);
            return strings.Count - 1;
        }
    }

    private static byte[] Assemble(JsonElement definition, int constantCount, int argumentCount, CancellationToken token)
    {
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        var instructions = new List<(int Op, JsonElement Arg)>();
        foreach (var row in definition.EnumerateArray())
        {
            if (row.TryGetProperty("label", out var label))
            {
                if (!labels.TryAdd(label.GetString()!, instructions.Count))
                {
                    throw new CheckFailure("unsupported", "Duplicate method label.");
                }
            }
            else
            {
                instructions.Add((row.GetProperty("op").GetInt32(), row.GetProperty("arg")));
            }
        }
        var locals = instructions.Count > 0 && instructions[0].Op == 1 ? instructions[0].Arg.GetInt32() : 0;
        using var output = new MemoryStream();
        foreach (var (op, arg) in instructions)
        {
            token.ThrowIfCancellationRequested();
            if (!Opcodes.Contains(op) && op is not (14 or 33 or 37 or 40 or 71))
            {
                throw new CheckFailure("unsupported", $"Method opcode {op} needs an adapter.");
            }

            int value;
            if (Branches.Contains(op))
            {
                if (!arg.TryGetProperty("label", out var label) || !labels.TryGetValue(label.GetString()!, out value) || value >= instructions.Count)
                {
                    throw new CheckFailure("unsupported", "Method branches must refer to an existing local label.");
                }
            }
            else
            {
                value = arg.GetInt32();
            }

            if (value is < -8388608 or > 8388607 ||
                op is 1 or 8 or 10 or 13 or 14 or 60 && value < 0 ||
                op is 7 or 59 && (value < -locals || value >= argumentCount) ||
                op is 6 or 15 or 20 or 53 or 57 or 58 && (value < 0 || value >= constantCount) ||
                op is 18 or 19 && value != argumentCount ||
                op == 5 && value is not (0 or 1) ||
                op is 2 or 3 or 21 or 27 or 32 or 33 or 36 or 37 or 40 or 56 or 68 or 69 or 71 or 72 && value != 0)
            {
                throw new CheckFailure("unsupported", "The method has an unsupported frame, constant or instruction operand.");
            }

            WriteVarint(output, 8);
            WriteVarint(output, checked((ulong)(0x80000000L + ((long)value << 8) + op)));
        }
        return output.ToArray();
    }

    private static void Integer(Stream stream, int field, ulong value)
    {
        WriteVarint(stream, (ulong)field << 3);
        WriteVarint(stream, value);
    }
    private static void Data(Stream stream, int field, byte[] value)
    {
        WriteVarint(stream, ((ulong)field << 3) | 2);
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
    }
}
