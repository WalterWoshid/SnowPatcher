using System.Collections.Immutable;
using System.Text.Json;
using SnowPatcher.Core.Mods;
using SnowPatcher.Core.Packages;
using SnowPatcher.Core.Steam;

namespace SnowPatcher.Core.Compatibility;

public static partial class CompatibilityChecker
{
    private static void FindConflicts(List<Claim> claims, CancellationToken token)
    {
        var groups = claims.GroupBy(c => PackagePath.TargetKey(c.Target)).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var group in groups.Values)
        {
            var mods = group.GroupBy(c => c.Mod).ToArray();
            for (var i = 0; i < mods.Length; i++)
            {
                for (var j = i + 1; j < mods.Length; j++)
                {
                    var first = mods[i].Key;
                    var second = mods[j].Key;
                    if (first.Ancestors.Contains(second.Id) || second.Ancestors.Contains(first.Id))
                    {
                        continue;
                    }

                    foreach (var left in mods[i])
                    {
                        token.ThrowIfCancellationRequested();
                        foreach (var right in mods[j])
                        {
                            Compare(left, right, false);
                        }
                    }
                }
            }

            var target = group[0].Target;
            for (var depth = 0; depth < target.Members.Length; depth++)
            {
                token.ThrowIfCancellationRequested();
                var parent = target with
                {
                    Members = target.Members.Take(depth).ToImmutableArray()
                };
                if (!groups.TryGetValue(PackagePath.TargetKey(parent), out var parents))
                {
                    continue;
                }

                foreach (var other in parents.Where(c => c.Kind is "whole" or "read"))
                {
                    foreach (var claim in group)
                    {
                        Compare(claim, other, true);
                    }
                }
            }
        }
    }

    private static void Compare(Claim a, Claim b, bool ancestor)
    {
        if (a.Mod == b.Mod || a.Mod.Ancestors.Contains(b.Mod.Id) || b.Mod.Ancestors.Contains(a.Mod.Id))
        {
            return;
        }

        if ((a.Kind == "read" || a.Kind.StartsWith("read-", StringComparison.Ordinal)) && (b.Kind == "read" || b.Kind.StartsWith("read-", StringComparison.Ordinal)))
        {
            return;
        }

        var conflict = ancestor;
        if (!ancestor)
        {
            if (a.Kind == "whole" && b.Kind == "whole")
            {
                conflict = a.Requirement != b.Requirement || a.Outcome != b.Outcome;
            }
            else if (a.Kind is "whole" or "read" || b.Kind is "whole" or "read")
            {
                conflict = true;
            }
            else if (a.Kind == "read-function" || b.Kind == "read-function")
            {
                var read = a.Kind == "read-function" ? a : b;
                var write = ReferenceEquals(read, a) ? b : a;
                conflict = write.Kind == "function" && read.Path.SequenceEqual(write.Path);
            }
            else if (a.Kind == "read-member" || b.Kind == "read-member")
            {
                conflict = false;
            }
            else if (a.Kind == "method-add" || b.Kind == "method-add")
            {
                conflict = a.Kind == b.Kind && a.Path[0] == b.Path[0] && (a.Path[1] == b.Path[1] || a.Path[2] == b.Path[2]);
            }
            else if (a.Kind == "read-property" || b.Kind == "read-property")
            {
                conflict = a.Path.SequenceEqual(b.Path) || Prefix(a.Path, b.Path) || Prefix(b.Path, a.Path);
            }
            else if (a.Kind == "identity" || b.Kind == "identity")
            {
                conflict = a.Kind == b.Kind && a.Path.SequenceEqual(b.Path) && a.Outcome != b.Outcome;
            }
            else if (a.Kind != b.Kind)
            {
                conflict = true;
            }
            else if (a.Kind == "function")
            {
                if (!a.Path.SequenceEqual(b.Path))
                {
                    return;
                }

                conflict = a.Requirement != b.Requirement || (a.Insertion && b.Insertion ? a.Start == b.Start : a.Insertion ? a.Start >= b.Start && a.Start <= b.End : b.Insertion ? b.Start >= a.Start && b.Start <= a.End : a.Start < b.End && b.Start < a.End);
                if (conflict && !a.Insertion && !b.Insertion && a.Start == b.Start && a.End == b.End && a.Requirement == b.Requirement && a.Outcome == b.Outcome)
                {
                    conflict = false;
                }
            }
            else
            {
                var same = a.Path.SequenceEqual(b.Path);
                conflict = same || Prefix(a.Path, b.Path) || Prefix(b.Path, a.Path);
                if (same && a.Requirement == b.Requirement && ((a.Outcome.StartsWith("append-unique:", StringComparison.Ordinal) && b.Outcome.StartsWith("append-unique:", StringComparison.Ordinal)) || a.Outcome == b.Outcome))
                {
                    conflict = false;
                }
            }
        }

        if (!conflict)
        {
            return;
        }

        var specific = a.Kind is "whole" or "read" ? b : a;
        var detail = ResourceView.Describe(specific.Target) + (specific.Function.Length > 0 ? " / " + specific.Function : specific.Path.Length == 0 ? "" : " / " + string.Join("/", specific.Path));
        a.Mod.Add("conflict", $"Changes overlap with {b.Mod.Name} ({b.Operation}).", a.Operation, detail, b.Mod.Id);
        b.Mod.Add("conflict", $"Changes overlap with {a.Mod.Name} ({a.Operation}).", b.Operation, detail, a.Mod.Id);
    }

    private static bool Prefix(string[] parent, string[] child) => parent.Length < child.Length && parent.SequenceEqual(child.Take(parent.Length));
    private sealed record Claim(Result Mod, string Operation, PackageTarget Target, string Kind, string[] Path, string Requirement, string Outcome, int Start, int End, bool Insertion, string Function);
}
