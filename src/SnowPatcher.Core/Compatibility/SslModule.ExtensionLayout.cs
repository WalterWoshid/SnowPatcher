namespace SnowPatcher.Core.Compatibility;

internal sealed partial class SslModule
{
    private void ValidateExtensionLayout(SslModule added, CancellationToken token)
    {
        foreach (var number in _fields.Concat(added._fields).Select(f => f.Number).Distinct())
        {
            if (number is 2 or 5 or 6)
            {
                Prefix(_fields.Where(f => f.Number == number), added._fields.Where(f => f.Number == number));
            }
            else if (number is not (4 or 7 or 8))
            {
                Same(_fields.Where(f => f.Number == number), added._fields.Where(f => f.Number == number));
            }
        }

        var beforeRefs = Fields(One(_fields, 4, 2).Data);
        var afterRefs = Fields(One(added._fields, 4, 2).Data);
        foreach (var number in beforeRefs.Concat(afterRefs).Select(f => f.Number).Distinct())
        {
            if (number is 1 or 2 or 4)
            {
                Prefix(beforeRefs.Where(f => f.Number == number), afterRefs.Where(f => f.Number == number));
            }
            else
            {
                Same(beforeRefs.Where(f => f.Number == number), afterRefs.Where(f => f.Number == number));
            }
        }

        var beforeClasses = _fields.Where(f => f.Number == 7).ToArray();
        var afterClasses = added._fields.Where(f => f.Number == 7).ToArray();
        Require(afterClasses.Length >= beforeClasses.Length, "An existing class was removed.");
        var classNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < afterClasses.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var after = Fields(afterClasses[i].Data);
            var name = added.StringAt(One(after, 1, 0).Integer);
            var newClass = i >= beforeClasses.Length;
            if (!classNames.Add(name) && newClass)
            {
                throw new CheckFailure("conflict", $"Added class {name} already exists.");
            }
            var oldCount = 0;
            if (!newClass)
            {
                var before = Fields(beforeClasses[i].Data);
                Same(before.Where(f => f.Number != 3), after.Where(f => f.Number != 3));
                Prefix(before.Where(f => f.Number == 3), after.Where(f => f.Number == 3));
                oldCount = before.Count(f => f.Number == 3);
            }

            var slots = new HashSet<ulong>();
            var names = new Dictionary<string, HashSet<ulong>>(StringComparer.Ordinal);
            var members = after.Where(f => f.Number == 3).Select(f => Fields(f.Data)).ToArray();
            for (var j = 0; j < members.Length; j++)
            {
                token.ThrowIfCancellationRequested();
                var member = members[j];
                var memberName = added.StringAt(One(member, 1, 0).Integer);
                var slot = One(member, 2, 0).Integer;
                var kind = One(member, 3, 0).Integer;
                if (!names.TryGetValue(memberName, out var kinds))
                {
                    names.Add(memberName, kinds = []);
                }
                if (j >= oldCount)
                {
                    // Events and their backing fields legitimately share a name.
                    var eventPair = kinds.Count == 1 && (kind == 3 && kinds.Contains(5) || kind == 5 && kinds.Contains(3));
                    Require(!slots.Contains(slot) && (kinds.Count == 0 || eventPair), $"Added member {name}::{memberName} has a duplicate name or slot.");
                    if (kind == 2)
                    {
                        Require(One(member, 4, 0).Integer < (ulong)added._bodies.Length, $"Added method {name}::{memberName} has an invalid body reference.");
                    }
                }
                slots.Add(slot);
                kinds.Add(kind);
            }
        }
    }
}
