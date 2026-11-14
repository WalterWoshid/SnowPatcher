using System.Collections.Immutable;
using System.Text.Json;
using SnowPatcher.Core.Mods;
using SnowPatcher.Core.Packages;
using SnowPatcher.Core.Steam;

namespace SnowPatcher.Core.Compatibility;

public static partial class CompatibilityChecker
{
    internal static void Replay(ResourceView view, ModPackage package, PackageOperation operation, CancellationToken token, int activeModCount = 1)
    {
        var result = new Result(new CompatibilityInput(Guid.Empty, new ModArchive(package.SourcePath, 0)))
        {
            Package = package
        };
        Apply(view, result, operation, false, null, activeModCount, token);
    }

    private static void Apply(ResourceView view, Result result, PackageOperation operation, bool guards, List<Claim>? claims, int activeModCount, CancellationToken token)
    {
        var package = result.Package!;
        var target = operation.Target;
        var definition = operation.Definition;
        var data = view.Read(target);
        if (guards && definition.TryGetProperty("beforeSha256", out var before) && (data is null || GameResources.Hash(data) != before.GetString()))
        {
            throw new CheckFailure(data is null ? "missing" : "changed", "The original resource hash does not match. This mod needs an update for these files.");
        }

        switch (operation.Type)
        {
            case "resource.add":
            case "resource.replace":
                if (operation.Type == "resource.add")
                {
                    view.RequireParent(target);
                    if (guards && data is not null)
                    {
                        throw new CheckFailure("changed", "The resource this mod wants to add already exists.");
                    }
                }
                else if (data is null)
                {
                    throw new CheckFailure("missing", "The resource to replace is missing.");
                }

                var payload = package.ReadPayload(definition.GetProperty("payload").GetString()!, token);
                long payloadBytes = 0;
                if (ContainsCompiled(payload, 0, ref payloadBytes, token) && !package.Manifest.Checks.Any(c => c.Target.File == PackageSemantics.NativeMetadataPath && c.Target.Members.IsEmpty))
                {
                    throw new CheckFailure("unsupported", "Compiled payloads require a native metadata check, including when embedded inside another container.");
                }

                if (!target.Members.IsEmpty && target.Members[^1].Format == "sslbundle")
                {
                    SslModule.ValidateHeader(payload);
                    view.CheckAddedModuleIdentity(target, payload);
                    claims?.Add(new Claim(result, operation.Id, target with { Members = target.Members.RemoveAt(target.Members.Length - 1) }, "identity", [Convert.ToHexString(payload.AsSpan(8, 4))], operation.Type, target.Members[^1].Name + ":" + GameResources.Hash(payload), 0, 0, false, target.Members[^1].Name));
                }

                claims?.Add(new Claim(result, operation.Id, target, "whole", [], definition.TryGetProperty("beforeSha256", out before) ? before.GetString()! : "absent", operation.Type + ":" + GameResources.Hash(payload), 0, 0, false, ""));
                view.Write(target, payload);
                break;
            case "resource.remove":
                if (data is null)
                {
                    throw new CheckFailure("missing", "The resource to remove is missing.");
                }

                claims?.Add(new Claim(result, operation.Id, target, "whole", [], definition.GetProperty("beforeSha256").GetString()!, "remove", 0, 0, false, ""));
                view.Write(target, null);
                break;
            case "sso.edit":
            case "strings.edit":
            case "xml.edit":
                if (data is null)
                {
                    throw new CheckFailure("missing", "The text resource is missing.");
                }

                var text = operation.Type switch
                {
                    "sso.edit" => TextEdits.Sso(data, definition, package, guards, token, activeModCount),
                    "xml.edit" => TextEdits.Xml(data, definition, guards, token),
                    _ => TextEdits.Strings(data, definition, guards)
                };
                var kind = operation.Type switch { "sso.edit" => "property", "xml.edit" => "xml-attribute", _ => "string" };
                foreach (var change in text.Changes)
                {
                    claims?.Add(new Claim(result, operation.Id, target, kind, change.Path, change.Requirement, change.Action + ":" + change.Outcome, 0, 0, false, ""));
                }

                view.Write(target, text.Bytes);
                break;
            case "sslb.methods.add":
                if (data is null)
                {
                    throw new CheckFailure("missing", "The script module is missing.");
                }

                var module = new SslModule(data).AddMethods(definition, guards, token);
                if (package.Manifest.Relocations is not null)
                {
                    foreach (var method in definition.GetProperty("methods").EnumerateArray())
                    {
                        claims?.Add(new Claim(result, operation.Id, target, "method-add", [definition.GetProperty("class").GetString()!, method.GetProperty("name").GetString()!, method.GetProperty("slot").GetRawText()], "absent", package.Manifest.Id + ":" + operation.Id, 0, 0, false, method.GetProperty("name").GetString()!));
                    }
                }
                else
                {
                    claims?.Add(new Claim(result, operation.Id, target, "whole", [], definition.GetProperty("moduleContextSha256").GetString()!, operation.Type + ":" + package.Manifest.Id + ":" + GameResources.Hash(module), 0, 0, false, definition.GetProperty("class").GetString()!));
                }

                view.Write(target, module);
                break;
            case "sslb.function.edit":
                if (data is null)
                {
                    throw new CheckFailure("missing", "The script module is missing.");
                }

                var function = new SslModule(data).Preview(definition, guards, PortablePackage.BoundReferences(package.Manifest, operation), token);
                foreach (var change in function.Changes)
                {
                    claims?.Add(new Claim(result, operation.Id, target, "function", [change.Identity], change.BodyHash, change.Fingerprint, change.Start, change.End, change.Insertion, definition.GetProperty("function").GetProperty("class").GetString() + "::" + definition.GetProperty("function").GetProperty("name").GetString()));
                }

                view.Write(target, function.Bytes);
                break;
            default:
                throw new CheckFailure("unsupported", $"Unsupported operation: {operation.Type}");
        }
    }

    private static bool ContainsCompiled(byte[] data, int depth, ref long total, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 8)
        {
            throw new CheckFailure("unsupported", "The payload containers are nested too deeply.");
        }

        total += data.Length;
        if (total > GameResources.MaxBytes)
        {
            throw new CheckFailure("unsupported", "The expanded payload exceeds the compiled-content check limit.");
        }

        if (data.AsSpan().StartsWith("SSLB"u8))
        {
            SslModule.ValidateHeader(data);
            return true;
        }

        var format = data.AsSpan().StartsWith("S3DB"u8) ? "sslbundle" : data.AsSpan().StartsWith("1SERcache_block\0"u8) ? "cache" : data.AsSpan().StartsWith("PK\x03\x04"u8) ? "zip" : null;
        if (format is null)
        {
            return false;
        }

        var container = ResourceContainer.Read(data, format, token, GameResources.MaxBytes - total);
        var compiled = false;
        foreach (var name in container.Names)
        {
            compiled |= ContainsCompiled(container.Get(name)!, depth + 1, ref total, token);
        }

        return compiled;
    }
}
