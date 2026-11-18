using System.Collections.Immutable;
using System.Text.Json;
using SnowPatcher.Core.Installations;
using SnowPatcher.Core.Mods;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Compatibility;

public static partial class CompatibilityChecker
{
    public static async Task<CompatibilityReport> CheckAsync(GameInstallation installation, IReadOnlyList<CompatibilityInput> inputs, CancellationToken cancellationToken = default)
    {
        var selected = inputs.ToArray();
        var inspection = await GameInstallationVerifier.InspectAsync(installation, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Check(installation, selected, inspection, cancellationToken).Report, cancellationToken).ConfigureAwait(false);
    }

    internal static (CompatibilityReport Report, IReadOnlyDictionary<string, byte[]?> Outputs) Check(GameInstallation installation, CompatibilityInput[] inputs, InstallationInspection inspection, CancellationToken token, IReadOnlyDictionary<string, byte[]?>? baseline = null)
    {
        var results = inputs.Select(i => new Result(i)).ToArray();
        var game = new GameResources(installation.RootDirectory, token, baseline);
        GameInstallState? installationState = null;
        try
        {
            installationState = GameInstallState.Read(installation);
        }
        catch (IOException)
        {
        }
        catch (InvalidDataException)
        {
        }
        catch (InvalidOperationException error)
        {
            foreach (var result in results)
            {
                result.Add("changed", error.Message);
            }
        }

        var packages = new List<CheckedFile>();
        foreach (var result in results)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var archive = ModArchiveReader.Read(result.Input.Archive.SourcePath, token);
                if (archive.Sha256 != result.Input.Archive.Sha256)
                {
                    throw new CheckFailure("changed", "The imported ZIP changed. Remove it and add it again.");
                }

                packages.Add(new CheckedFile(archive.SourcePath, archive.Sha256));
                result.Package = archive.Package;
                if (result.Package is null)
                {
                    result.Add("manual", "This ZIP has no snowpatcher.json. Its changes cannot be checked or combined until the mod is packaged for SnowPatcher.");
                }
            }
            catch (Exception error) when (Recoverable(error))
            {
                result.Failure(error);
            }
        }

        var modern = results.Where(r => r.Package is not null).ToArray();
        var groups = modern.GroupBy(r => r.Id, StringComparer.Ordinal).ToArray();
        foreach (var group in groups.Where(g => g.Count() != 1))
        {
            foreach (var result in group)
            {
                result.Add("conflict", $"More than one enabled package uses the mod ID {group.Key}. Disable the extra version.");
            }
        }

        var byId = groups.Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var pending = new SortedSet<string>(byId.Keys, StringComparer.Ordinal);
        var ordered = new List<Result>();
        foreach (var result in byId.Values)
        {
            foreach (var dependency in result.Package!.Manifest.Dependencies)
            {
                if (!byId.TryGetValue(dependency.Id, out var required))
                {
                    result.Add("dependency", $"Enable dependency {dependency.Id} ({dependency.MinVersion} or newer).");
                }
                else if (PackageManifestReader.CompareVersions(required.Package!.Manifest.Version, dependency.MinVersion) < 0 || (dependency.MaxVersionExclusive is { } max && PackageManifestReader.CompareVersions(required.Package.Manifest.Version, max) >= 0))
                {
                    result.Add("dependency", $"{required.Name} has an unsupported version for this mod's dependency range.");
                }
            }

            foreach (var incompatible in result.Package.Manifest.Conflicts)
            {
                if (!byId.TryGetValue(incompatible, out var other))
                {
                    continue;
                }

                result.Add("conflict", $"This mod declares a conflict with {other.Name}.", other: other.Id);
                other.Add("conflict", $"{result.Name} declares a conflict with this mod.", other: result.Id);
            }
        }

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var ready = pending.FirstOrDefault(id => byId[id].Package!.Manifest.Dependencies.All(d => !pending.Contains(d.Id)));
            if (ready is null)
            {
                foreach (var id in pending)
                {
                    byId[id].Add("dependency", "The selected dependencies contain a cycle or depend on one.");
                }

                break;
            }

            pending.Remove(ready);
            var result = byId[ready];
            foreach (var dependency in result.Package!.Manifest.Dependencies)
            {
                if (!byId.TryGetValue(dependency.Id, out var required))
                {
                    continue;
                }

                result.Ancestors.Add(required.Id);
                result.Ancestors.UnionWith(required.Ancestors);
            }

            ordered.Add(result);
        }

        var claims = new List<Claim>();
        var operations = new List<CheckedOperation>();
        foreach (var result in ordered)
        {
            token.ThrowIfCancellationRequested();
            if (result.HasIssues)
            {
                continue;
            }

            var view = new ResourceView(game, token);
            try
            {
                foreach (var ancestor in ordered.Where(r => result.Ancestors.Contains(r.Id)))
                {
                    if (ancestor.HasIssues)
                    {
                        throw new CheckFailure("dependency", $"Dependency {ancestor.Name} did not pass its checks.");
                    }

                    foreach (var operation in ancestor.Package!.Manifest.Operations.Where(o => !o.IsOptional))
                    {
                        Apply(view, ancestor, operation, false, null, modern.Length, token);
                    }
                }

                var bound = PortablePackage.Bind(result.Package!, view, token);
                result.Package = bound.Package;
                foreach (var read in bound.Reads)
                {
                    claims.Add(new Claim(result, "prerequisite", read.Target, read.Kind, read.Path, "", "", 0, 0, false, read.Function));
                }

                foreach (var check in result.Package!.Manifest.Checks)
                {
                    var data = view.Read(check.Target) ?? throw new CheckFailure("missing", $"A required resource is missing: {ResourceView.Describe(check.Target)}");
                    if (GameResources.Hash(data) != check.Sha256)
                    {
                        throw new CheckFailure("changed", $"A required resource changed: {ResourceView.Describe(check.Target)}");
                    }

                    claims.Add(new Claim(result, "prerequisite", check.Target, "read", [], "", "", 0, 0, false, ""));
                }
            }
            catch (Exception error) when (Recoverable(error))
            {
                result.Failure(error);
                continue;
            }

            foreach (var operation in result.Package!.Manifest.Operations.Where(o => !o.IsOptional))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    Apply(view, result, operation, true, claims, modern.Length, token);
                    operations.Add(new CheckedOperation(result.Id, operation.Id, operation.Type, ResourceView.Describe(operation.Target)));
                }
                catch (Exception error) when (Recoverable(error))
                {
                    result.Failure(error, operation.Id, ResourceView.Describe(operation.Target));
                    // Later operations may depend on this output, so do not evaluate them against a false input.
                    break;
                }
            }
        }

        FindConflicts(claims, token);
        foreach (var result in ordered)
        {
            foreach (var ancestor in ordered.Where(r => result.Ancestors.Contains(r.Id) && r.HasIssues))
            {
                result.Add("dependency", $"Dependency {ancestor.Name} has unresolved compatibility issues.");
            }
        }

        IReadOnlyDictionary<string, byte[]?> outputs = new Dictionary<string, byte[]?>();
        if (results.All(r => !r.HasIssues))
        {
            try
            {
                var view = SelectionComposer.Compose(game, ordered.Select(r => r.Package!).ToArray(), token);
                ApplyOptionalTranslations(view, ordered, claims, operations, token);
                outputs = view.Outputs();
            }
            catch (Exception error) when (Recoverable(error))
            {
                foreach (var result in results)
                {
                    result.Failure(error);
                }
            }
        }

        try
        {
            inspection = GameInstallationVerifier.InspectAsync(installation, token).GetAwaiter().GetResult();
            game.EnsureUnchanged();
            installationState?.EnsureUnchanged(installation);
            foreach (var package in packages)
            {
                token.ThrowIfCancellationRequested();
                if (ModArchiveReader.Read(package.Path, token).Sha256 != package.Sha256)
                {
                    throw new IOException("A package changed during the check. Run Check mods again.");
                }
            }
        }
        catch (Exception error) when (Recoverable(error))
        {
            foreach (var result in results)
            {
                result.Add("changed", error.Message);
            }
        }

        var report = new CompatibilityReport(DateTimeOffset.UtcNow, Path.GetFullPath(installation.RootDirectory), installationState?.BuildId ?? inspection.BuildId, inspection.State, game.Snapshots.ToImmutableArray(), packages.ToImmutableArray(), ordered.Select(r => r.Id).ToImmutableArray(), operations.ToImmutableArray(), results.Select(r => new ModCompatibility(r.Input.LibraryId, r.Name, r.Issues.Distinct().ToImmutableArray())).ToImmutableArray(), Store: inspection.Store);
        return (report, outputs);
    }

    private static bool Recoverable(Exception error) => error is CheckFailure or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or OverflowException or KeyNotFoundException or IndexOutOfRangeException or FormatException;
    private sealed class Result(CompatibilityInput input)
    {
        public CompatibilityInput Input { get; } = input;
        public ModPackage? Package { get; set; }
        public string Id => Package!.Manifest.Id;
        public string Name => Package?.Manifest.Name ?? Input.Archive.DisplayName;
        public List<CompatibilityIssue> Issues { get; } = [];
        public HashSet<string> Ancestors { get; } = new(StringComparer.Ordinal);
        public bool HasIssues => Issues.Count > 0;

        public void Add(string code, string message, string? operation = null, string? target = null, string? other = null) => Issues.Add(new CompatibilityIssue(code, message, operation, target, other));
        public void Failure(Exception error, string? operation = null, string? target = null) => Add(error switch
        {
            CheckFailure check => check.Code,
            FileNotFoundException or DirectoryNotFoundException => "missing",
            IOException => "changed",
            _ => "unsupported"
        }, error.Message, operation, target);
    }
}
