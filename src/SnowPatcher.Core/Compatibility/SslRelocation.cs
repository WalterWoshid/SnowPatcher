using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SnowPatcher.Core.Compatibility;

// Semantic identities are deliberately limited to the layouts we understand.
// No original function bodies or game assets are stored in a portable package.
internal sealed partial class SslModule
{
    private List<Field>[]? _relocationSymbols;
    private List<Field>[]? _relocationClasses;
    private List<Field>[]? _relocationReferences;
    private string[]? _relocationNative;
    private readonly Dictionary<string, (int Slot, string Contract)> _memberContracts = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _slotContracts = [];
    private string? _contextHash;

    internal string ContextHash() => _contextHash ??= GameResources.Hash(_fields.Where(f => f.Number != 8).SelectMany(f => f.Encoded).ToArray());
    private List<Field>[] RelocationSymbols => _relocationSymbols ??= _fields.Where(f => f.Number == 5).Select(f => Fields(f.Data)).ToArray();
    private List<Field>[] RelocationClasses => _relocationClasses ??= _fields.Where(f => f.Number == 7).Select(f => Fields(f.Data)).ToArray();
    private List<Field>[] RelocationReferences => _relocationReferences ??= Fields(One(_fields, 4, 2).Data).Where(f => f.Number == 4).Select(f => Fields(f.Data)).ToArray();
    private string[] RelocationNative => _relocationNative ??= Fields(One(_fields, 4, 2).Data).Where(f => f.Number == 1)
        .Select(f => StringAt(One(Fields(f.Data), 1, 0).Integer)).ToArray();

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static void KnownIntegers(List<Field> fields, params int[] numbers)
    {
        if (fields.Any(f => f.Wire != 0 || !numbers.Contains(f.Number)))
        {
            throw new CheckFailure("unsupported", "Unknown nested type metadata needs a relocation adapter.");
        }
    }
    internal string TypeName(int index, HashSet<int>? visiting = null)
    {
        visiting ??= [];
        if (index < 0 || index >= RelocationSymbols.Length || !visiting.Add(index) || visiting.Count > 32)
        {
            throw new CheckFailure("unsupported", "An SSLB type cannot be resolved safely.");
        }

        try
        {
            var symbol = RelocationSymbols[index];
            var kind = One(symbol, 1, 0).Integer;
            var expected = kind switch
            {
                1 or 3 or 8 => 2,
                6 => 3,
                10 => 6,
                2 => 7,
                5 => 8,
                4 => 9,
                9 => 4,
                _ => -1
            };
            if (symbol.Count != 2 || symbol.Any(f => f.Number != 1 && f.Number != expected))
            {
                throw new CheckFailure("unsupported", "Unknown type-symbol metadata needs a relocation adapter.");
            }

            var id = kind is 1 or 3 ? checked((int)One(symbol, 2, 0).Integer) : -1;
            if (kind == 1 && id >= 0 && id < RelocationClasses.Length)
            {
                return StringAt(One(RelocationClasses[id], 1, 0).Integer);
            }

            if (kind == 3 && id >= 0 && id < RelocationNative.Length)
            {
                return RelocationNative[id];
            }

            if (kind == 6)
            {
                return "parameter:" + StringAt(One(symbol, 3, 0).Integer);
            }

            if (kind == 8)
            {
                return "reference<" + TypeName(checked((int)One(symbol, 2, 0).Integer), visiting) + ">";
            }

            if (kind == 10)
            {
                var data = Fields(One(symbol, 6, 2).Data);
                KnownIntegers(data, 1, 2, 3);
                return "binding<" + TypeName(checked((int)One(data, 1, 0).Integer), visiting) + "," + One(data, 2, 0).Integer.ToString(CultureInfo.InvariantCulture) + "," + TypeName(checked((int)One(data, 3, 0).Integer), visiting) + ">";
            }
            if (kind is 2 or 5)
            {
                var data = Fields(One(symbol, kind == 2 ? 7 : 8, 2).Data);
                KnownIntegers(data, kind == 2 ? [1, 2, 3, 4, 5] : [1, 2, 3]);
                return "callback" + kind.ToString(CultureInfo.InvariantCulture) + "<" + TypeName(checked((int)One(data, 1, 0).Integer), visiting) + "," + B64(StringAt(One(data, 2, 0).Integer)) + "," +
                    (kind == 2 ? B64(StringAt(One(data, 3, 0).Integer)) + "," + One(data, 4, 0).Integer + "," + One(data, 5, 0).Integer : One(data, 3, 0).Integer.ToString(CultureInfo.InvariantCulture)) + ">";
            }
            if (kind == 4)
            {
                var data = Fields(One(symbol, 9, 2).Data);
                var native = checked((int)One(data, 1, 0).Integer);
                KnownIntegers(data, 1, 2);
                if (native < 0 || native >= RelocationNative.Length)
                {
                    throw new CheckFailure("unsupported", "Invalid generic type.");
                }

                var arguments = data.Where(f => f.Number == 2).Select(f => TypeName(checked((int)f.Integer), visiting)).ToArray();
                return RelocationNative[native] + (arguments.Length == 0 ? "" : "<" + string.Join(",", arguments) + ">");
            }
            if (kind == 9)
            {
                var data = Fields(One(symbol, 4, 2).Data);
                KnownIntegers(data, 1, 2);
                return TypeName(checked((int)One(data, 1, 0).Integer), visiting) + "(" +
                    string.Join(",", data.Where(f => f.Number == 2).Select(f => TypeName(checked((int)f.Integer), visiting))) + ")";
            }
            throw new CheckFailure("unsupported", $"Type-symbol kind {kind} needs a relocation adapter.");
        }
        finally { visiting.Remove(index); }
    }

    internal int ResolveType(string name)
    {
        var found = new List<int>();
        for (var i = 0; i < RelocationSymbols.Length; i++)
        {
            // Unsupported, unused symbols do not invalidate a known named type.
            try
            {
                if (TypeName(i) == name)
                {
                    found.Add(i);
                }
            }
            catch (CheckFailure) { }
        }
        if (found.Count != 1)
        {
            throw new CheckFailure("changed", $"Type {name} is missing or ambiguous.");
        }

        return found[0];
    }

    internal (int Slot, string Contract) ResolveMember(string name, int? slotHint = null)
    {
        var cacheKey = slotHint is { } hint ? name + "#" + hint : name;
        if (_memberContracts.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var parts = name.Split("::", StringSplitOptions.None);
        if (parts.Length != 2)
        {
            throw new CheckFailure("unsupported", "A member needs Class::Name.");
        }

        var refs = RelocationReferences.Where(r => StringAt(One(r, 2, 0).Integer) == parts[1] && TypeName(checked((int)One(r, 3, 0).Integer)) == parts[0] &&
            (slotHint is null || One(r, 4, 0).Integer == (ulong)slotHint.Value)).ToArray();
        if (refs.Any(r => r.Any(f => f.Number is < 1 or > 5 || f.Wire != 0)))
        {
            throw new CheckFailure("unsupported", "Unknown member-reference metadata.");
        }

        var owners = RelocationClasses.Where(c => StringAt(One(c, 1, 0).Integer) == parts[0]).ToArray();
        if (owners.Length > 1)
        {
            throw new CheckFailure("changed", $"Class {parts[0]} is ambiguous.");
        }

        var declarations = owners.SelectMany(c => c.Where(f => f.Number == 3).Select(f => Fields(f.Data)))
            .Where(m => StringAt(One(m, 1, 0).Integer) == parts[1] && (slotHint is null || One(m, 2, 0).Integer == (ulong)slotHint.Value)).ToArray();
        if (declarations.Length > 1)
        {
            throw new CheckFailure("changed", $"Member {name} is ambiguous.");
        }

        int slot;
        ulong kind;
        string signature;
        string extra = "";
        string parent = "";
        if (declarations.Length == 1)
        {
            var member = declarations[0];
            slot = checked((int)One(member, 2, 0).Integer);
            kind = One(member, 3, 0).Integer;
            if (kind == 2)
            {
                var index = checked((int)One(member, 4, 0).Integer);
                if (index < 0 || index >= _bodies.Length)
                {
                    throw new CheckFailure("unsupported", "Invalid method body.");
                }

                signature = TypeName(checked((int)One(Fields(_bodies[index].Data), 1, 0).Integer));
            }
            else if (member.Any(f => f.Number == 8))
            {
                signature = TypeName(checked((int)One(Fields(One(member, 8, 2).Data), 1, 0).Integer));
            }
            else
            {
                signature = ReferenceSignature(refs);
            }

            extra = Convert.ToHexString(member.Where(f => f.Number is not (1 or 2 or 3 or 8) && !(kind == 2 && f.Number == 4)).SelectMany(f => f.Encoded).ToArray());
            if (owners[0].Any(f => f.Number == 2))
            {
                parent = TypeName(checked((int)One(owners[0], 2, 0).Integer));
            }

            if (refs.Any(r => One(r, 4, 0).Integer != (ulong)slot))
            {
                throw new CheckFailure("unsupported", $"Inconsistent member slot: {name}.");
            }
        }
        else
        {
            var slots = refs.Select(r => One(r, 4, 0).Integer).Distinct().ToArray();
            var kinds = refs.Select(r => One(r, 1, 0).Integer).Distinct().ToArray();
            if (slots.Length != 1 || kinds.Length != 1)
            {
                throw new CheckFailure("changed", $"Member {name} is missing or ambiguous.");
            }

            slot = checked((int)slots[0]);
            kind = kinds[0];
            signature = ReferenceSignature(refs);
        }
        var contract = string.Join("|", B64(parts[0]), B64(parts[1]), kind.ToString(CultureInfo.InvariantCulture), B64(signature), B64(parent), extra);
        return _memberContracts[cacheKey] = (slot, GameResources.Hash(Encoding.UTF8.GetBytes(contract)));
    }

    private string ReferenceSignature(List<Field>[] references)
    {
        var signatures = references.Select(r => r.Any(f => f.Number == 5) ? TypeName(checked((int)One(r, 5, 0).Integer)) : "").Distinct().ToArray();
        if (signatures.Length > 1)
        {
            throw new CheckFailure("unsupported", "Overloaded member references need an explicit adapter.");
        }

        return signatures.SingleOrDefault() ?? "";
    }

    private string SlotContract(int slot)
    {
        if (_slotContracts.TryGetValue(slot, out var cached))
        {
            return cached;
        }

        var names = RelocationReferences.Where(r => One(r, 4, 0).Integer == (ulong)slot)
            .Select(r => TypeName(checked((int)One(r, 3, 0).Integer)) + "::" + StringAt(One(r, 2, 0).Integer)).ToList();
        foreach (var owner in RelocationClasses)
        {
            names.AddRange(owner.Where(f => f.Number == 3).Select(f => Fields(f.Data)).Where(m => One(m, 2, 0).Integer == (ulong)slot)
                .Select(m => StringAt(One(owner, 1, 0).Integer) + "::" + StringAt(One(m, 1, 0).Integer)));
        }

        var contracts = names.Distinct(StringComparer.Ordinal).Select(n => n + "=" + ResolveMember(n, slot).Contract).Order(StringComparer.Ordinal).ToArray();
        var functions = Fields(One(_fields, 4, 2).Data).Where(f => f.Number == 2).Select(f => Fields(f.Data))
            .Where(r => One(r, 3, 0).Integer == (ulong)slot).Select(r => GlobalName(checked((int)One(r, 4, 0).Integer)) + "::" + StringAt(One(r, 1, 0).Integer))
            .Distinct(StringComparer.Ordinal).Select(n => "global:" + n + "=" + ResolveGlobalFunction(n).Contract);
        contracts = contracts.Concat(functions).Order(StringComparer.Ordinal).ToArray();
        if (contracts.Length == 0)
        {
            throw new CheckFailure("unsupported", $"Dispatch slot {slot} has no known member identity.");
        }

        return _slotContracts[slot] = "m:" + string.Join(";", contracts);
    }
    internal (int Slot, string Contract) ResolveGlobalFunction(string name)
    {
        var found = Fields(One(_fields, 4, 2).Data).Where(f => f.Number == 2).Select(f => Fields(f.Data))
            .Where(r => GlobalName(checked((int)One(r, 4, 0).Integer)) + "::" + StringAt(One(r, 1, 0).Integer) == name).ToArray();
        var slots = found.Select(r => One(r, 3, 0).Integer).Distinct().ToArray();
        var signatures = found.Select(r => TypeName(checked((int)One(r, 2, 0).Integer))).Distinct().ToArray();
        if (slots.Length != 1 || signatures.Length != 1)
        {
            throw new CheckFailure("changed", $"Global function {name} is missing or ambiguous.");
        }

        return (checked((int)slots[0]), GameResources.Hash(Encoding.UTF8.GetBytes(B64(name) + "|" + B64(signatures[0]))));
    }

    internal string GlobalName(int index)
    {
        var globals = _fields.Where(f => f.Number == 2).ToArray();
        if (index < 0 || index >= globals.Length)
        {
            throw new CheckFailure("unsupported", "Invalid global reference.");
        }

        return StringAt(globals[index].Integer);
    }
    internal int ResolveGlobal(string name)
    {
        var found = _fields.Where(f => f.Number == 2).Select((f, i) => (Name: StringAt(f.Integer), Index: i)).Where(x => x.Name == name).ToArray();
        if (found.Length != 1)
        {
            throw new CheckFailure("changed", $"Global {name} is missing or ambiguous.");
        }

        return found[0].Index;
    }
    private string ConstantIdentity(List<Field> value)
    {
        var kind = One(value, 1, 0).Integer;
        if (kind == 6)
        {
            // Opaque callback values cannot be relocated across shared-table changes.
            return "opaque:" + ContextHash() + ":" +
                Convert.ToBase64String(value.SelectMany(f => f.Encoded).ToArray());
        }
        var operand = kind switch
        {
            1 => 2,
            3 => 4,
            4 => 5,
            5 => 6,
            _ => -1
        };
        if (value.Count != 2 || value.Any(f => f.Number != 1 && f.Number != operand))
        {
            throw new CheckFailure("unsupported", "Unknown constant metadata needs a relocation adapter.");
        }

        return kind switch
        {
            1 => "i:" + One(value, 2, 0).Integer.ToString(CultureInfo.InvariantCulture),
            3 => "f:" + Convert.ToHexString(One(value, 4, 5).Data),
            4 => "s:" + B64(StringAt(One(value, 5, 0).Integer)),
            5 => "t:" + B64(TypeName(checked((int)One(value, 6, 0).Integer))),
            _ => throw new CheckFailure("unsupported", $"Constant kind {kind} needs a relocation adapter.")
        };
    }
    internal string ConstantName(JsonElement function, int index)
    {
        var constants = Fields(_bodies[FindBody(function)].Data).Where(f => f.Number == 4).ToArray();
        if (index < 0 || index >= constants.Length)
        {
            throw new CheckFailure("unsupported", "Invalid constant reference.");
        }

        return ConstantIdentity(Fields(constants[index].Data));
    }
    internal int ResolveConstant(JsonElement function, string identity)
    {
        var found = Fields(_bodies[FindBody(function)].Data).Where(f => f.Number == 4).Select((f, i) => (Name: ConstantIdentity(Fields(f.Data)), Index: i))
            .Where(x => x.Name == identity).ToArray();
        if (found.Length != 1)
        {
            throw new CheckFailure("changed", "A required function constant is missing or ambiguous.");
        }

        return found[0].Index;
    }
    internal string SemanticFunctionHash(JsonElement definition)
    {
        var body = Fields(_bodies[FindBody(definition)].Data);
        if (body.Any(f => f.Number is < 1 or > 5))
        {
            throw new CheckFailure("unsupported", "Unknown function metadata needs a relocation adapter.");
        }

        var lines = new List<string> { "owner:" + ResolveMember(definition.GetProperty("class").GetString() + "::" + definition.GetProperty("name").GetString()).Contract, "sig:" + B64(TypeName(checked((int)One(body, 1, 0).Integer))), "flags:" + One(body, 3, 0).Integer };
        foreach (var field in Fields(One(body, 5, 2).Data))
        {
            if (field.Number != 1 || field.Wire != 0 || field.Integer > uint.MaxValue)
            {
                throw new CheckFailure("unsupported", "Unknown instruction encoding.");
            }

            var word = (long)field.Integer - 0x80000000L;
            var op = (int)(word & 255);
            var arg = (int)(word >> 8);
            if (!Opcodes.Contains(op) && op is not (14 or 16 or 17 or 30 or 33 or 37 or 40 or 43 or 63 or 65 or 71))
            {
                throw new CheckFailure("unsupported", $"Opcode {op} needs a relocation adapter.");
            }

            if (op is 21 or 30 or 43 or 56 or 63 or 65 or 66 or 69 or 72 && arg != 0)
            {
                throw new CheckFailure("unsupported", "Unsupported scalar/array instruction flags.");
            }

            var identity = UsesConstantOperand(op) ? "c:" + ConstantName(definition, arg) : op switch
            {
                10 or 13 or 14 or 60 => SlotContract(arg),
                8 => "g:" + B64(GlobalName(arg)),
                // Event subscription operands are preserved only with an identical shared context.
                16 or 17 => "opaque:" + ContextHash() + ":" + arg.ToString(CultureInfo.InvariantCulture),
                _ => "n:" + arg.ToString(CultureInfo.InvariantCulture)
            };
            lines.Add(op.ToString(CultureInfo.InvariantCulture) + ":" + identity);
        }
        return GameResources.Hash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
    }
}
