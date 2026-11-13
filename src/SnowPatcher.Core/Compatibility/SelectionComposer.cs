using System.Text.Json;
using System.Text.Json.Nodes;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Compatibility;

internal static class SelectionComposer
{
    internal static IReadOnlyDictionary<string, byte[]?> Compose(GameResources game, IReadOnlyList<ModPackage> packages, CancellationToken token)
    {
        var view = new ResourceView(game, token);
        var groups = new Dictionary<string, FunctionGroup>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            foreach (var operation in package.Manifest.Operations)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (operation.Type == "sslb.function.edit")
                    {
                        var current = view.Read(operation.Target) ?? throw new CheckFailure("missing", "The script module is missing.");
                        var definition = operation.Definition.GetProperty("function");
                        // Resolve aliases by physical body identity, not just their class/member spelling.
                        var probe = new SslModule(current);
                        var expectedHash = definition.GetProperty("bodySha256").GetString()!;
                        var key = PackagePath.TargetKey(operation.Target) + ":" + probe.FunctionIdentity(definition);
                        if (!groups.TryGetValue(key, out var group) || group.BodyHash != expectedHash)
                        {
                            if (probe.FunctionHash(definition) != expectedHash)
                            {
                                throw new CheckFailure("unsupported", "This function has changes from several dependency baselines. Split the edits into a compatible package set.");
                            }

                            group = new FunctionGroup(current, expectedHash, operation.Definition);
                            groups[key] = group;
                        }
                        foreach (var edit in operation.Definition.GetProperty("edits").EnumerateArray())
                        {
                            if (!group.Edits.Any(e => JsonElement.DeepEquals(e, edit)))
                            {
                                group.Edits.Add(edit);
                            }
                        }
                        group.BoundReferences.UnionWith(PortablePackage.BoundReferences(package.Manifest, operation));

                        var combined = JsonNode.Parse(group.Definition.GetRawText())!.AsObject();
                        combined["edits"] = new JsonArray(group.Edits.Select(e => JsonNode.Parse(e.GetRawText())).ToArray());
                        var preview = new SslModule(group.Module).Preview(JsonSerializer.SerializeToElement(combined), false, group.BoundReferences, token);
                        view.Write(operation.Target, probe.AdoptFunction(preview.Bytes, definition));
                    }
                    else
                    {
                        if (operation.Type.StartsWith("resource.", StringComparison.Ordinal))
                        {
                            // A whole-resource operation creates a new base for later function operations.
                            var prefix = PackagePath.TargetKey(operation.Target);
                            foreach (var key in groups.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                            {
                                groups.Remove(key);
                            }

                            if (operation.Type == "resource.remove" && view.Read(operation.Target) is null)
                            {
                                continue;
                            }
                        }
                        CompatibilityChecker.Replay(view, package, operation, token, packages.Count);
                        if (operation.Type == "sslb.methods.add")
                        {
                            // Keep each function's unedited baseline, with the same
                            // appended tables as the current composed module.
                            var prefix = PackagePath.TargetKey(operation.Target) + ":";
                            foreach (var key in groups.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                            {
                                groups[key] = groups[key] with
                                {
                                    Module = new SslModule(groups[key].Module).AddMethods(operation.Definition, false, token)
                                };
                            }
                        }
                    }
                }
                catch (CheckFailure error) { throw new CheckFailure(error.Code, $"{package.Manifest.Name}, {operation.Id}: {error.Message}"); }
            }
        }

        return view.Outputs();
    }

    private sealed record FunctionGroup(byte[] Module, string BodyHash, JsonElement Definition)
    {
        public List<JsonElement> Edits { get; } = [];
        public HashSet<(int Op, int Value)> BoundReferences { get; } = [];
    }
}
