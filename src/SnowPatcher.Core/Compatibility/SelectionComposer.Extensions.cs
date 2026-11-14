using System.Text.Json;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Compatibility;

internal static partial class SelectionComposer
{
    private static void ApplyExtension(GameResources game, ResourceView view, Dictionary<string, FunctionGroup> groups,
        ModPackage package, PackageOperation operation, CancellationToken token)
    {
        var prefix = PackagePath.TargetKey(operation.Target) + ":";
        var current = view.Read(operation.Target) ?? throw new CheckFailure("missing", "The script module is missing.");
        var expected = operation.Definition.GetProperty("beforeSha256").GetString();
        var baseline = groups.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p.Value.Module)
            .Prepend(current).Append(new ResourceView(game, token).Read(operation.Target))
            .FirstOrDefault(bytes => bytes is not null && GameResources.Hash(bytes) == expected);
        if (baseline is null)
        {
            throw new CheckFailure("changed", "The script extension's checked baseline is unavailable.");
        }
        var source = new SslModule(baseline);
        var payload = package.ReadPayload(operation.Definition.GetProperty("payload").GetString()!, token);
        var extension = source.Extend(payload, operation.Definition, token);
        var layout = new SslModule(current).ContextHash();
        if (layout != source.ContextHash() && layout != new SslModule(extension.Scaffold).ContextHash())
        {
            throw new CheckFailure("conflict", "The script extension overlaps another module layout change.");
        }

        foreach (var key in groups.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            groups[key] = groups[key] with { Module = extension.Scaffold };
        }
        foreach (var function in extension.Functions)
        {
            var key = prefix + function.Identity;
            if (!groups.TryGetValue(key, out var group))
            {
                groups[key] = group = new FunctionGroup(extension.Scaffold, function.BodyHash, function.Definition);
            }
            if (group.BodyHash != function.BodyHash)
            {
                throw new CheckFailure("conflict", "The extension and function edits use different baselines.");
            }
            group.ExtensionLayout = true;
            foreach (var edit in function.Definition.GetProperty("edits").EnumerateArray())
            {
                if (!group.Edits.Any(e => JsonElement.DeepEquals(e, edit)))
                {
                    group.Edits.Add(edit);
                }
            }
        }

        view.Write(operation.Target, extension.Scaffold);
        foreach (var group in groups.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p.Value))
        {
            token.ThrowIfCancellationRequested();
            view.Write(operation.Target, group.ApplyTo(view.Read(operation.Target)!, token));
        }
    }
}
