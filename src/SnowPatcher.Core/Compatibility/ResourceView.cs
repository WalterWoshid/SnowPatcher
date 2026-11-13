/*
Cache-block layout reference/adaptation: chase-000/SnowRunnerTools.

MIT License

Copyright (c) 2020 chase <s.chubukov@protonmail.com>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Compatibility;

internal sealed class ResourceView(GameResources game, CancellationToken token)
{
    private readonly Dictionary<string, (PackageTarget Target, byte[]? Bytes)> _changes = new(StringComparer.Ordinal);
    public byte[]? Read(PackageTarget target, bool exactContent = true)
    {
        token.ThrowIfCancellationRequested();
        PackagePath.ValidateTarget(target);
        var prefix = new PackageTarget(target.File, []);
        byte[]? bytes = game.Root(target.File);
        if (_changes.TryGetValue(PackagePath.TargetKey(prefix), out var rootChange))
        {
            bytes = rootChange.Bytes;
        }

        foreach (var member in target.Members)
        {
            if (bytes is null)
            {
                throw new CheckFailure("missing", $"The parent resource is missing: {Describe(prefix)}");
            }

            var parent = prefix;
            prefix = prefix with
            {
                Members = prefix.Members.Add(member)
            };
            if (_changes.TryGetValue(PackagePath.TargetKey(prefix), out var change))
            {
                bytes = change.Bytes;
                continue;
            }

            var container = Container(parent, bytes, member.Format);
            bytes = container.Get(member.Name);
        }

        if (exactContent)
        {
            bytes = Materialize(target, bytes);
        }

        return bytes;
    }

    public IReadOnlyDictionary<string, byte[]?> Outputs() => _changes.Values.Select(c => c.Target.File).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(file => file, file => Read(new PackageTarget(file, [])), StringComparer.OrdinalIgnoreCase);
    private byte[]? Materialize(PackageTarget target, byte[]? bytes)
    {
        var descendants = _changes.Values.Where(c => IsAncestor(target, c.Target)).ToArray();
        if (descendants.Length == 0)
        {
            return bytes;
        }

        if (bytes is null)
        {
            throw new CheckFailure("missing", "A changed container no longer exists.");
        }

        var formats = descendants.Select(c => c.Target.Members[target.Members.Length].Format).Distinct().ToArray();
        if (formats.Length != 1)
        {
            throw new CheckFailure("conflict", "Mods disagree about the target container format.");
        }

        var format = formats[0];
        var changes = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var member in descendants.Select(c => c.Target.Members[target.Members.Length]).Distinct())
        {
            var child = target with
            {
                Members = target.Members.Add(member)
            };
            changes[member.Name] = Read(child);
        }

        return ContainerWriter.Rebuild(bytes, format, changes, token);
    }

    public void Write(PackageTarget target, byte[]? bytes)
    {
        foreach (var key in _changes.Where(p => IsAncestor(target, p.Value.Target)).Select(p => p.Key).ToArray())
        {
            _changes.Remove(key);
        }

        _changes[PackagePath.TargetKey(target)] = (target, bytes);
    }

    public void RequireParent(PackageTarget target)
    {
        if (target.Members.IsEmpty)
        {
            game.RequireParentDirectory(target.File);
            return;
        }

        var parent = target with
        {
            Members = target.Members.RemoveAt(target.Members.Length - 1)
        };
        var data = Read(parent, exactContent: false) ?? throw new CheckFailure("missing", $"The parent resource is missing: {Describe(parent)}");
        _ = Container(parent, data, target.Members[^1].Format);
    }

    public void CheckAddedModuleIdentity(PackageTarget target, byte[] payload)
    {
        if (target.Members.IsEmpty || target.Members[^1].Format != "sslbundle")
        {
            return;
        }

        var parent = target with
        {
            Members = target.Members.RemoveAt(target.Members.Length - 1)
        };
        var data = Read(parent, false) ?? throw new CheckFailure("missing", "The script bundle is missing.");
        var container = Container(parent, data, "sslbundle");
        var changed = _changes.Values.Where(c => IsImmediateChild(parent, c.Target)).ToDictionary(c => c.Target.Members[^1].Name, c => c.Bytes, StringComparer.Ordinal);
        var names = container.Names.Concat(changed.Keys).Distinct(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (name == target.Members[^1].Name)
            {
                continue;
            }

            var other = changed.TryGetValue(name, out var replacement) ? replacement : container.Get(name);
            if (other is { Length: >= 12 } && other.AsSpan(8, 4).SequenceEqual(payload.AsSpan(8, 4)))
            {
                throw new CheckFailure("conflict", $"The module identity is already used by {name}.");
            }
        }
    }

    private ResourceContainer Container(PackageTarget target, byte[] data, string format)
    {
        return game.Container(data, format);
    }

    internal static bool IsAncestor(PackageTarget parent, PackageTarget child)
    {
        if (!string.Equals(parent.File, child.File, StringComparison.OrdinalIgnoreCase) || parent.Members.Length >= child.Members.Length)
        {
            return false;
        }

        return parent.Members.Select((m, i) => MemberEquals(m, child.Members[i])).All(x => x);
    }

    private static bool IsImmediateChild(PackageTarget parent, PackageTarget child) => child.Members.Length == parent.Members.Length + 1 && IsAncestor(parent, child);
    private static bool MemberEquals(PackageMember a, PackageMember b) => a.Format == b.Format && string.Equals(a.Name, b.Name, a.Format == "sslbundle" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    internal static string Describe(PackageTarget target) => string.Join(" -> ", new[] { target.File }.Concat(target.Members.Select(m => m.Name)));
}
