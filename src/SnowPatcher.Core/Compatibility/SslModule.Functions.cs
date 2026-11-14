using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Compatibility;

internal sealed partial class SslModule
{
    public FunctionPreview Preview(JsonElement operation, bool guards, IReadOnlySet<(int Op, int Value)>? boundReferences = null, bool extensionLayout = false, CancellationToken cancellationToken = default)
    {
        if (guards && ContextHash() != operation.GetProperty("moduleContextSha256").GetString())
        {
            throw new CheckFailure("changed", "The function's shared tables or class metadata changed. This mod needs an update.");
        }

        var definition = operation.GetProperty("function");
        var className = definition.GetProperty("class").GetString()!;
        var functionName = definition.GetProperty("name").GetString()!;
        var found = new List<int>();
        foreach (var type in _fields.Where(f => f.Number == 7 && f.Wire == 2))
        {
            var typeFields = Fields(type.Data);
            if (StringAt(One(typeFields, 1, 0).Integer) != className)
            {
                continue;
            }

            foreach (var member in typeFields.Where(f => f.Number == 3 && f.Wire == 2))
            {
                var row = Fields(member.Data);
                if (One(row, 3, 0).Integer != 2 || StringAt(One(row, 1, 0).Integer) != functionName)
                {
                    continue;
                }

                var index = One(row, 4, 0).Integer;
                if (index >= (ulong)_bodies.Length)
                {
                    throw new CheckFailure("unsupported", "Invalid function body reference.");
                }

                found.Add((int)index);
            }
        }

        if (found.Count != 1)
        {
            throw new CheckFailure(found.Count == 0 ? "missing" : "unsupported", found.Count == 0 ? $"Function {className}::{functionName} was not found." : $"Function {className}::{functionName} is ambiguous.");
        }

        var bodyIndex = found[0];
        var body = _bodies[bodyIndex];
        var bodyHash = GameResources.Hash(body.Data);
        if (guards && bodyHash != definition.GetProperty("bodySha256").GetString())
        {
            throw new CheckFailure("changed", $"Function {className}::{functionName} changed. This mod needs an update.");
        }

        var bodyFields = Fields(body.Data);
        if (bodyFields.Any(f => f.Number is < 1 or > 5))
        {
            throw new CheckFailure("unsupported", "The function has metadata this checker cannot safely relocate.");
        }

        var stream = One(bodyFields, 5, 2);
        var code = Fields(stream.Data).Select(f =>
        {
            if (f.Number != 1 || f.Wire != 0 || f.Integer > uint.MaxValue)
            {
                throw new CheckFailure("unsupported", "Unsupported instruction encoding.");
            }

            var word = (long)f.Integer - 0x80000000L;
            var instruction = ((int)(word & 255), (int)(word >> 8));
            if (!extensionLayout && !Opcodes.Contains(instruction.Item1))
            {
                throw new CheckFailure("unsupported", $"Opcode {instruction.Item1} needs an adapter before this function can be checked.");
            }

            if (instruction.Item1 is 21 or 56 or 63 or 65 or 66 or 68 or 69 or 72 && instruction.Item2 != 0)
            {
                throw new CheckFailure("unsupported", "Unsupported instruction operand flags.");
            }

            return instruction;
        }).ToArray();
        if (code.Length > 100000 || code.Any(i => Branches.Contains(i.Item1) && (i.Item2 < 0 || i.Item2 > code.Length)))
        {
            throw new CheckFailure("unsupported", "Unsupported function size or branch destinations.");
        }

        var edits = new List<Edit>();
        foreach (var edit in operation.GetProperty("edits").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = edit.GetProperty("match").EnumerateArray().Select(p => (Int32(p[0]), Int32(p[1]))).ToArray();
            var positions = new List<int>();
            for (var i = 0; i <= code.Length - match.Length; i++)
            {
                if ((i & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (code.AsSpan(i, match.Length).SequenceEqual(match))
                {
                    positions.Add(i);
                }

                if (positions.Count > 1)
                {
                    break;
                }
            }

            if (positions.Count != 1)
            {
                throw new CheckFailure("changed", positions.Count == 0 ? "The instruction sequence was not found in the function." : "The instruction sequence matches more than once.");
            }

            var start = positions[0];
            var end = start + match.Length;
            var mode = edit.GetProperty("mode").GetString()!;
            var boundary = mode == "after" ? end : start;
            var change = new Edit(start, end, boundary, mode, edit.GetProperty("incoming").GetString()!, edit.GetProperty("code").Clone());
            foreach (var other in edits)
            {
                if (Overlaps(change, other))
                {
                    throw new CheckFailure("conflict", "Instruction edits overlap inside this operation.");
                }
            }

            edits.Add(change);
        }

        var output = new List<(int Op, int Arg, int OldTarget, Edit? Edit, JsonElement? Reference)>();
        var localCount = code.Length > 0 && code[0].Item1 == 1 ? code[0].Item2 : 0;
        var argumentCount = code.Where(i => i.Item1 is 18 or 19).Select(i => i.Item2).DefaultIfEmpty(0).Max();
        foreach (var edit in edits.Where(e => e.Mode == "replace" && e.Start == 0))
        {
            var first = edit.Code.EnumerateArray().FirstOrDefault(row => row.TryGetProperty("op", out _));
            if (first.ValueKind == JsonValueKind.Object && first.GetProperty("op").GetInt32() == 1 && first.GetProperty("arg").TryGetInt32(out var count) && count >= 0)
            {
                localCount = count;
            }
        }
        var oldTargets = Enumerable.Repeat(-1, code.Length + 1).ToArray();
        var originalBoundaries = Enumerable.Repeat(-1, code.Length + 1).ToArray();
        var indexAt = 0;
        while (indexAt <= code.Length)
        {
            var replacement = edits.SingleOrDefault(e => e.Mode == "replace" && e.Start == indexAt);
            if (replacement is not null)
            {
                oldTargets[indexAt] = output.Count;
                replacement.Entry = output.Count;
                Emit(replacement);
                replacement.Exit = output.Count;
                indexAt = replacement.End;
                continue;
            }

            var insertion = edits.SingleOrDefault(e => e.Mode != "replace" && e.Boundary == indexAt);
            var entry = output.Count;
            if (insertion is not null)
            {
                insertion.Entry = entry;
                Emit(insertion);
                insertion.Exit = output.Count;
            }

            oldTargets[indexAt] = insertion?.Incoming == "include" ? entry : output.Count;
            originalBoundaries[indexAt] = output.Count;
            if (indexAt == code.Length)
            {
                break;
            }

            var instruction = code[indexAt];
            output.Add((instruction.Item1, instruction.Item2, Branches.Contains(instruction.Item1) ? instruction.Item2 : -1, null, null));
            indexAt++;
        }

        if (output.Count > 100000)
        {
            throw new CheckFailure("unsupported", "The edited function is too large.");
        }

        var originalFrame = code.Length > 0 && code[0].Item1 == 1 ? code[0].Item2 : 0;
        var editedFrame = output.Count > 0 && output[0].Op == 1 ? output[0].Arg : 0;
        if (edits.Any(e => e.Mode == "replace" && e.Start == 0) && editedFrame < originalFrame &&
            output.Any(i => (i.Op is 7 or 59) && i.Arg < -editedFrame))
        {
            throw new CheckFailure("unsupported", "The edited function still uses local slots outside its initial frame.");
        }

        using var encodedCode = new MemoryStream();
        foreach (var instruction in output)
        {
            var argument = instruction.Arg;
            if (instruction.OldTarget >= 0)
            {
                argument = oldTargets[instruction.OldTarget];
                if (argument < 0)
                {
                    throw new CheckFailure("conflict", "A surviving branch enters instructions removed by this edit.");
                }
            }
            else if (instruction.Reference is { } reference)
            {
                var owner = instruction.Edit!;
                if (reference.TryGetProperty("label", out var label))
                {
                    if (!owner.Labels.TryGetValue(label.GetString()!, out argument))
                    {
                        throw new CheckFailure("unsupported", "An instruction label could not be resolved.");
                    }
                }
                else if (extensionLayout && reference.TryGetProperty("original", out var original))
                {
                    var index = original.GetInt32();
                    argument = index >= 0 && index < oldTargets.Length ? oldTargets[index] : -1;
                }
                else
                {
                    argument = reference.GetProperty("boundary").GetString() switch
                    {
                        "function.end" => output.Count,
                        "match.start" => owner.Mode == "replace" ? owner.Entry : originalBoundaries[owner.Start],
                        "match.end" => owner.Mode == "replace" ? owner.Exit : originalBoundaries[owner.End],
                        _ => -1
                    };
                }

                if (argument < 0 || argument > output.Count)
                {
                    throw new CheckFailure("conflict", "A new branch refers to a removed boundary.");
                }
            }

            if (argument is < -8388608 or > 8388607)
            {
                throw new CheckFailure("unsupported", "An instruction operand exceeds the supported encoding.");
            }

            WriteVarint(encodedCode, 8);
            WriteVarint(encodedCode, checked((ulong)(0x80000000L + ((long)argument << 8) + instruction.Op)));
        }

        var newBody = Replace(bodyFields, stream, encodedCode.ToArray());
        var payload = Replace(_fields, body, newBody);
        var result = new byte[16 + payload.Length];
        _bytes.AsSpan(0, 12).CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(result, 16);
        return new FunctionPreview(result, edits.Select(e => new FunctionChange(bodyIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), bodyHash, e.Mode == "replace" ? e.Start : e.Boundary, e.Mode == "replace" ? e.End : e.Boundary, e.Mode != "replace", GameResources.Hash(Encoding.UTF8.GetBytes(e.Code.GetRawText() + ":" + e.Incoming)))).ToArray());
        void Emit(Edit edit)
        {
            foreach (var row in edit.Code.EnumerateArray())
            {
                if (row.TryGetProperty("label", out var label))
                {
                    edit.Labels.Add(label.GetString()!, output.Count);
                    continue;
                }

                var op = Int32(row.GetProperty("op"));
                var arg = row.GetProperty("arg");
                if (!Opcodes.Contains(op) && !(extensionLayout && code.Any(i => i.Item1 == op)))
                {
                    throw new CheckFailure("unsupported", $"New opcode {op} is not supported by the checker.");
                }

                if (Branches.Contains(op))
                {
                    if (arg.ValueKind != JsonValueKind.Object)
                    {
                        throw new CheckFailure("unsupported", "New branch destinations must use labels or boundaries.");
                    }

                    output.Add((op, 0, -1, edit, arg.Clone()));
                    continue;
                }

                var value = Int32(arg);
                if (op == 1 && value < 0)
                {
                    throw new CheckFailure("unsupported", "A local frame size cannot be negative.");
                }
                if (!extensionLayout && (op is 1 or 8 or 10 or 13 or 18 or 19 or 60) && !code.Contains((op, value)) &&
                    !(op is 8 or 10 or 13 or 60 && boundReferences?.Contains((op, value)) == true) && !(op == 1 && value == localCount))
                {
                    throw new CheckFailure("unsupported", "The edit uses a frame size or reference not already used by this function. It needs a more complete VM adapter.");
                }

                if (op is 7 or 59 && !code.Any(i => i.Item1 is 7 or 59 && i.Item2 == value) &&
                    !(value < 0 && value >= -localCount) && !(value >= 0 && value < argumentCount))
                {
                    throw new CheckFailure("unsupported", "The edit requires a local slot not already used by this function.");
                }

                if (UsesConstantOperand(op) && (value < 0 || value >= bodyFields.Count(f => f.Number == 4)))
                {
                    throw new CheckFailure("unsupported", "The edit uses a missing function constant.");
                }

                if (op == 5 && value is not (0 or 1))
                {
                    throw new CheckFailure("unsupported", "Invalid boolean instruction operand.");
                }

                if (op is 2 or 3 or 21 or 27 or 32 or 36 or 56 or 63 or 65 or 66 or 68 or 69 or 72 && value != 0)
                {
                    throw new CheckFailure("unsupported", "An instruction uses unsupported operand flags.");
                }

                if (op is 48 or 49 or 50 && !code.Contains((op, value)) && !(op == 48 && value is 7 or 8))
                {
                    throw new CheckFailure("unsupported", "The edit introduces comparison flags not already used by this function.");
                }

                output.Add((op, value, -1, edit, null));
            }
        }
    }

    internal string FunctionHash(JsonElement definition) => GameResources.Hash(_bodies[FindBody(definition)].Data);
    internal int FunctionIdentity(JsonElement definition) => FindBody(definition);
    internal byte[] AdoptFunction(byte[] changedModule, JsonElement definition)
    {
        var changed = new SslModule(changedModule);
        var index = FindBody(definition);
        if (index != changed.FindBody(definition) || !_fields.Where(f => f.Number != 8).SelectMany(f => f.Encoded).SequenceEqual(changed._fields.Where(f => f.Number != 8).SelectMany(f => f.Encoded)))
        {
            throw new CheckFailure("conflict", "The function's shared context changed while combining mods.");
        }

        var payload = Replace(_fields, _bodies[index], changed._bodies[index].Data);
        var bytes = new byte[16 + payload.Length];
        _bytes.AsSpan(0, 12).CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(bytes, 16);
        return bytes;
    }

    private int FindBody(JsonElement definition)
    {
        var found = new List<int>();
        foreach (var type in _fields.Where(f => f.Number == 7 && f.Wire == 2))
        {
            var fields = Fields(type.Data);
            if (StringAt(One(fields, 1, 0).Integer) != definition.GetProperty("class").GetString())
            {
                continue;
            }

            foreach (var member in fields.Where(f => f.Number == 3 && f.Wire == 2))
            {
                var row = Fields(member.Data);
                if (One(row, 3, 0).Integer == 2 && StringAt(One(row, 1, 0).Integer) == definition.GetProperty("name").GetString())
                {
                    found.Add(checked((int)One(row, 4, 0).Integer));
                }
            }
        }

        if (found.Count != 1 || found[0] < 0 || found[0] >= _bodies.Length)
        {
            throw new CheckFailure("unsupported", "The function cannot be resolved uniquely.");
        }

        return found[0];
    }

    private static bool Overlaps(Edit a, Edit b) => a.Mode != "replace" && b.Mode != "replace" ? a.Boundary == b.Boundary : a.Mode == "replace" && b.Mode == "replace" ? a.Start < b.End && b.Start < a.End : a.Mode == "replace" ? b.Boundary >= a.Start && b.Boundary <= a.End : a.Boundary >= b.Start && a.Boundary <= b.End;
    private sealed class Edit(int start, int end, int boundary, string mode, string incoming, JsonElement code)
    {
        public int Start { get; } = start;
        public int End { get; } = end;
        public int Boundary { get; } = boundary;
        public string Mode { get; } = mode;
        public string Incoming { get; } = incoming;
        public JsonElement Code { get; } = code;
        public int Entry { get; set; }
        public int Exit { get; set; }
        public Dictionary<string, int> Labels { get; } = new(StringComparer.Ordinal);
    }
}
