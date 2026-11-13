using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Compatibility;

internal sealed record PortableRead(PackageTarget Target, string Kind, string[] Path, string Function = "");
internal static class PortablePackage
{
    private static readonly HashSet<int> MemberOps = [10, 13, 14, 60];

    internal static IReadOnlySet<(int Op, int Value)> BoundReferences(PackageManifest manifest, PackageOperation operation)
    {
        var references = new HashSet<(int Op, int Value)>();
        if (manifest.Relocations is not { } recipe)
        {
            return references;
        }

        // Bind has already checked these named fields and method signatures.
        foreach (var binding in recipe.GetProperty("operands").EnumerateArray())
        {
            if (binding.GetProperty("operation").GetString() != operation.Id ||
                binding.GetProperty("kind").GetString() is not ("member" or "function" or "global"))
            {
                continue;
            }

            var path = binding.GetProperty("path").EnumerateArray().ToArray();
            if (path.Length != 5 || path[0].GetString() != "edits" ||
                path[2].GetString() != "code" || path[4].GetString() != "arg")
            {
                continue;
            }

            var row = operation.Definition.GetProperty("edits")[path[1].GetInt32()]
                .GetProperty("code")[path[3].GetInt32()];
            var op = row.GetProperty("op").GetInt32();
            if (op is 8 or 10 or 13)
            {
                references.Add((op, row.GetProperty("arg").GetInt32()));
            }
        }

        return references;
    }

    internal static void Validate(PackageManifest manifest)
    {
        if (manifest.Relocations is not { } recipe)
        {
            return;
        }

        var operations = manifest.Operations.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var covered = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in recipe.GetProperty("operands").EnumerateArray())
        {
            var id = binding.GetProperty("operation").GetString()!;
            if (!operations.TryGetValue(id, out var operation) || !operation.Type.StartsWith("sslb.", StringComparison.Ordinal))
            {
                throw new InvalidDataException("A relocation names an unknown script operation.");
            }

            var path = binding.GetProperty("path");
            var key = id + ":" + JsonSerializer.Serialize(path.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? (object)p.GetString()! : p.GetInt32()).ToArray());
            if (!covered.TryAdd(key, binding))
            {
                throw new InvalidDataException("Duplicate relocation path.");
            }

            var current = operation.Definition;
            foreach (var part in path.EnumerateArray())
            {
                current = part.ValueKind == JsonValueKind.String ? current.GetProperty(part.GetString()!) : current[part.GetInt32()];
            }

            if (current.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidDataException("A relocation must replace a numeric operand.");
            }
        }
        foreach (var operation in manifest.Operations.Where(o => o.Type.StartsWith("sslb.", StringComparison.Ordinal)))
        {
            var d = operation.Definition;
            var function = operation.Type == "sslb.function.edit" ? d.GetProperty("function") :
                JsonSerializer.SerializeToElement(new
                {
                    @class = d.GetProperty("class").GetString(),
                    name = d.GetProperty("templateMember").GetString()
                });
            if (!recipe.GetProperty("functions").EnumerateArray().Any(f => PackagePath.TargetKey(PackageManifestReader.Target(f.GetProperty("target"))) == PackagePath.TargetKey(operation.Target) &&
                f.GetProperty("class").GetString() == function.GetProperty("class").GetString() && f.GetProperty("name").GetString() == function.GetProperty("name").GetString() &&
                (operation.Type != "sslb.function.edit" || f.GetProperty("mode").GetString() == "body")))
            {
                throw new InvalidDataException("A portable script operation needs its original function guard.");
            }

            if (operation.Type == "sslb.function.edit")
            {
                var edits = d.GetProperty("edits").EnumerateArray().ToArray();
                for (var e = 0; e < edits.Length; e++)
                {
                    var match = edits[e].GetProperty("match").EnumerateArray().ToArray();
                    for (var i = 0; i < match.Length; i++)
                    {
                        Require(match[i][0].GetInt32(), ["edits", e, "match", i, 1], true);
                    }

                    Code(edits[e].GetProperty("code"), ["edits", e, "code"], true);
                }
            }
            else
            {
                var methods = d.GetProperty("methods").EnumerateArray().ToArray();
                for (var m = 0; m < methods.Length; m++)
                {
                    Need(["methods", m, "slot"], "member");
                    var constants = methods[m].GetProperty("constants").EnumerateArray().ToArray();
                    for (var c = 0; c < constants.Length; c++)
                    {
                        if (constants[c].TryGetProperty("typeSymbol", out _))
                        {
                            Need(["methods", m, "constants", c, "typeSymbol"], "type");
                        }
                    }

                    Code(methods[m].GetProperty("code"), ["methods", m, "code"], false);
                }
            }
            void Code(JsonElement code, object[] prefix, bool constants)
            {
                var rows = code.EnumerateArray().ToArray();
                for (var i = 0; i < rows.Length; i++)
                {
                    if (rows[i].TryGetProperty("op", out var op))
                    {
                        Require(op.GetInt32(), [.. prefix, i, "arg"], constants);
                    }
                }
            }
            void Require(int op, object[] path, bool constants)
            {
                if (op == 4 && covered.ContainsKey(operation.Id + ":" + JsonSerializer.Serialize(path)))
                {
                    // Callback construction pushes a dispatch slot as an integer.
                    Need(path, "member");
                }
                else if (MemberOps.Contains(op))
                {
                    Need(path, op == 13 ? "call" : "member");
                }
                else if (op == 8)
                {
                    Need(path, "global");
                }
                else if (constants && SslModule.UsesConstantOperand(op))
                {
                    Need(path, "constant");
                }
            }
            void Need(object[] path, string kind)
            {
                var key = operation.Id + ":" + JsonSerializer.Serialize(path);
                required.Add(key);
                if (!covered.TryGetValue(key, out var binding) || (kind == "call" ? binding.GetProperty("kind").GetString() is not ("member" or "function") : binding.GetProperty("kind").GetString() != kind))
                {
                    throw new InvalidDataException("A portable operation has an unbound or mismatched numeric reference.");
                }

                if (binding.GetProperty("kind").GetString() is "constant" or "global" or "type" or "function" && PackagePath.TargetKey(PackageManifestReader.Target(binding.GetProperty("target"))) != PackagePath.TargetKey(operation.Target))
                {
                    throw new InvalidDataException("A module-local reference cannot use another module's indices.");
                }
            }
        }
        if (covered.Keys.Any(k => !required.Contains(k)))
        {
            throw new InvalidDataException("A relocation targets an unsupported operand path.");
        }
    }

    internal static (ModPackage Package, IReadOnlyList<PortableRead> Reads) Bind(ModPackage package, ResourceView view, CancellationToken token)
    {
        if (package.Manifest.Relocations is not { } recipe)
        {
            return (package, []);
        }

        var reads = new List<PortableRead>();
        var modules = new Dictionary<string, SslModule>(StringComparer.Ordinal);
        SslModule Module(PackageTarget target)
        {
            var key = PackagePath.TargetKey(target);
            if (!modules.TryGetValue(key, out var module))
            {
                modules[key] = module = new SslModule(view.Read(target) ?? throw new CheckFailure("missing", "A required script module is missing."));
            }

            return module;
        }
        foreach (var function in recipe.GetProperty("functions").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var target = PackageManifestReader.Target(function.GetProperty("target"));
            var module = Module(target);
            var signatureOnly = function.GetProperty("mode").GetString() == "signature";
            var hash = signatureOnly ? module.ResolveMember(function.GetProperty("class").GetString() + "::" + function.GetProperty("name").GetString()).Contract : module.SemanticFunctionHash(function);
            if (hash != function.GetProperty("sha256").GetString())
            {
                throw new CheckFailure("changed", $"Required function {function.GetProperty("class").GetString()}::{function.GetProperty("name").GetString()} changed. Review this mod before applying it.");
            }

            if (function.GetProperty("read").GetBoolean())
            {
                reads.Add(new PortableRead(target, signatureOnly ? "read-member" : "read-function",
                    [signatureOnly ? function.GetProperty("class").GetString() + "::" + function.GetProperty("name").GetString() : module.FunctionIdentity(function).ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    function.GetProperty("class").GetString() + "::" + function.GetProperty("name").GetString()));
            }
        }
        var values = recipe.GetProperty("values").EnumerateArray().ToArray();
        foreach (var group in values.GroupBy(v => PackagePath.TargetKey(v.TryGetProperty("target", out var valueTarget) ? PackageManifestReader.Target(valueTarget) : new PackageTarget(PackageSemantics.NativeMetadataPath, []))))
        {
            var native = group.ToArray();
            var target = native[0].TryGetProperty("target", out var valueTarget) ? PackageManifestReader.Target(valueTarget) : new PackageTarget(PackageSemantics.NativeMetadataPath, []);
            var paths = native.Select(n => n.GetProperty("path").EnumerateArray().Select(p => p.GetString()!).ToArray()).ToArray();
            var hashes = TextEdits.SsoSliceHashes(view.Read(target) ?? throw new CheckFailure("missing", "Native definitions are missing."), paths);
            for (var i = 0; i < paths.Length; i++)
            {
                if (hashes[i] != native[i].GetProperty("sha256").GetString())
                {
                    throw new CheckFailure("changed", "A required native definition changed: " + string.Join("/", paths[i]));
                }

                reads.Add(new PortableRead(target, "read-property", paths[i].Select(p => "s:" + p).ToArray()));
            }
        }
        var definitions = package.Manifest.Operations.ToDictionary(o => o.Id, o => JsonNode.Parse(o.Definition.GetRawText())!, StringComparer.Ordinal);
        foreach (var binding in recipe.GetProperty("operands").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var id = binding.GetProperty("operation").GetString()!;
            var target = PackageManifestReader.Target(binding.GetProperty("target"));
            var module = Module(target);
            var name = binding.GetProperty("name").GetString()!;
            int value;
            switch (binding.GetProperty("kind").GetString())
            {
                case "member":
                    var member = module.ResolveMember(name);
                    if (member.Contract != binding.GetProperty("contract").GetString())
                    {
                        throw new CheckFailure("changed", $"Required member {name} changed its type or signature.");
                    }

                    value = member.Slot;
                    reads.Add(new PortableRead(target, "read-member", [name]));
                    break;
                case "type":
                    value = module.ResolveType(name);
                    break;
                case "function":
                    var function = module.ResolveGlobalFunction(name);
                    if (function.Contract != binding.GetProperty("contract").GetString())
                    {
                        throw new CheckFailure("changed", $"Required global function {name} changed its signature.");
                    }

                    value = function.Slot;
                    reads.Add(new PortableRead(target, "read-member", [name]));
                    break;
                case "global":
                    value = module.ResolveGlobal(name);
                    break;
                case "constant":
                    value = module.ResolveConstant(package.Manifest.Operations.Single(o => o.Id == id).Definition.GetProperty("function"), name);
                    break;
                default:
                    throw new CheckFailure("unsupported", "Unknown reference relocation.");
            }
            var parts = binding.GetProperty("path").EnumerateArray().ToArray();
            var node = definitions[id];
            foreach (var part in parts[..^1])
            {
                node = part.ValueKind == JsonValueKind.String ? node[part.GetString()!]! : node[part.GetInt32()]!;
            }

            if (parts[^1].ValueKind == JsonValueKind.String)
            {
                node[parts[^1].GetString()!] = value;
            }
            else
            {
                node[parts[^1].GetInt32()] = value;
            }
        }
        var operations = package.Manifest.Operations.Select(o =>
        {
            var definition = definitions[o.Id];
            if (o.Type.StartsWith("sslb.", StringComparison.Ordinal))
            {
                var module = Module(o.Target);
                definition["moduleContextSha256"] = module.ContextHash();
                if (o.Type == "sslb.function.edit")
                {
                    definition["function"]!["bodySha256"] = module.FunctionHash(o.Definition.GetProperty("function"));
                }
            }
            return o with
            {
                Definition = JsonSerializer.SerializeToElement(definition)
            };
        }).ToImmutableArray();
        return (new ModPackage(package.SourcePath, package.ArchiveSha256, package.Manifest with
        {
            Operations = operations
        }), reads);
    }
}
