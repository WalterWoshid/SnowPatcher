using System.Collections.Immutable;
using SnowPatcher.Core.Backups;
using SnowPatcher.Core.Compatibility;
using SnowPatcher.Core.Mods;
using SnowPatcher.Core.Steam;

namespace SnowPatcher.Core.Patching;

public sealed partial class PatchInstaller
{
    private async Task<Prepared> PrepareAsync(GameInstallation installation, CompatibilityInput[] inputs, CancellationToken token)
    {
        var store = new PatchStorage(_root, installation);
        var inspection = await SteamInstallationVerifier.InspectAsync(installation, token).ConfigureAwait(false);
        var previous = store.ReadState();
        SteamInstallState? steam = null;
        string? reason = null;
        try
        {
            steam = SteamInstallState.Read(installation);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        {
            reason = error.Message;
        }

        var baseline = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usesSavedBaseline = false;
        var needsRecovery = store.ReadJournal() is not null;
        if (needsRecovery)
        {
            reason = "An interrupted apply needs recovery. Close SnowRunner and click Recover.";
            var recoveryReport = new CompatibilityReport(DateTimeOffset.UtcNow, Path.GetFullPath(installation.RootDirectory), steam?.BuildId ?? inspection.BuildId, inspection.State, [], [], [], [], inputs.Select(i => new ModCompatibility(i.LibraryId, i.Archive.DisplayName, [new CompatibilityIssue("unsupported", reason)])).ToImmutableArray());
            return new Prepared(new PatchCheckResult(recoveryReport, true, true, reason, NeedsRecovery: true), steam, previous, baseline, [], []);
        }

        if (previous is not null && steam is not null)
        {
            var sameBuild = previous.BuildId == steam.BuildId && previous.DepotFingerprint == steam.DepotFingerprint;
            var current = previous.Files.ToDictionary(f => f.Path, f => PatchStorage.ReadFile(PatchStorage.GamePath(installation, f.Path)), StringComparer.OrdinalIgnoreCase);
            foreach (var pair in current)
            {
                observed[pair.Key] = PatchStorage.Hash(pair.Value);
            }

            var managed = sameBuild && previous.Files.All(f => PatchStorage.Hash(current[f.Path]) is var hash && (hash == f.OutputHash || hash == f.BaselineHash));
            var restored = !managed && sameBuild ? store.FindMatchingHistory(steam, current, token) : null;
            if (managed || restored is not null)
            {
                foreach (var file in (restored ?? previous).Files)
                {
                    baseline[file.Path] = store.Blob(file.BaselineHash);
                }

                usesSavedBaseline = true;
            }
            else
            {
                // Only first ownership may adopt manual changes. After ownership,
                // accepting arbitrary outputs as a new baseline could bake our
                // own patches into it and make disabling them lose other edits.
                try
                {
                    SteamInstallationVerifier.VerifyOriginals(installation, steam, current);
                    foreach (var pair in current)
                    {
                        baseline[pair.Key] = pair.Value;
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException)
                {
                    reason = "Game files changed outside SnowPatcher. The saved starting files and backups were kept. " + error.Message;
                    foreach (var file in previous.Files)
                    {
                        baseline[file.Path] = store.Blob(file.BaselineHash);
                    }

                    usesSavedBaseline = true;
                }
            }
        }

        foreach (var target in inputs.SelectMany(i => i.Archive.Package?.Manifest.Operations.Select(o => o.Target.File) ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (baseline.ContainsKey(target))
            {
                continue;
            }

            // A new target starts from the user's current bytes, including
            // existing manual mods. Patch guards still apply to those bytes;
            // Apply saves this baseline before it writes anything.
            baseline[target] = PatchStorage.ReadFile(PatchStorage.GamePath(installation, target));
            observed[target] = PatchStorage.Hash(baseline[target]);
        }

        var checkedSet = CompatibilityChecker.Check(installation, inputs, inspection, token, baseline);
        var report = checkedSet.Report with
        {
            UsesManagedBaseline = usesSavedBaseline,
            GameFiles = checkedSet.Report.GameFiles.Concat(observed.Select(p => new CheckedFile(PatchStorage.GamePath(installation, p.Key), p.Value))).Distinct().ToImmutableArray()
        };
        var outputs = baseline.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in checkedSet.Outputs)
        {
            outputs[pair.Key] = pair.Value;
        }

        var packages = inputs.Where(i => i.Archive.Package is not null).GroupBy(i => i.Archive.Package!.Manifest.Id, StringComparer.Ordinal).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Archive.Package!, StringComparer.Ordinal);
        var mods = report.ModOrder.Where(packages.ContainsKey).Select(id => packages[id]).Select(p => new ManagedMod(p.Manifest.Id, p.Manifest.Version, p.ArchiveSha256, p.Manifest.Operations.Select(o => o.Id).ToArray())).ToArray();
        var pending = outputs.Any(p => PatchStorage.Hash(p.Value) != PatchStorage.Hash(PatchStorage.ReadFile(PatchStorage.GamePath(installation, p.Key)))) || (previous is null ? mods.Length > 0 : !SameMods(previous.Mods, mods));
        if (report.HasIssues)
        {
            reason ??= "Resolve the compatibility issues before applying mods.";
        }

        var gameChanged = previous is not null && (previous.BuildId != steam?.BuildId || previous.DepotFingerprint != steam?.DepotFingerprint ||
            previous.Files.Any(f => f.OutputHash != (observed.TryGetValue(f.Path, out var hash) ? hash : PatchStorage.Hash(PatchStorage.ReadFile(PatchStorage.GamePath(installation, f.Path))))));
        return new Prepared(new PatchCheckResult(report, reason is null && pending && steam is not null, pending, reason,
            GameNeedsReapply: gameChanged && pending, MatchesAppliedSelection: previous is not null && SameMods(previous.Mods, mods)), steam, previous, baseline, outputs, mods);
    }

    private static bool SameMods(ManagedMod[] a, ManagedMod[] b) => a.Length == b.Length && a.Zip(b).All(pair => pair.First.Id == pair.Second.Id && pair.First.Version == pair.Second.Version && pair.First.ArchiveHash == pair.Second.ArchiveHash);
    private sealed record Prepared(PatchCheckResult Check, SteamInstallState? Steam, ManagedState? Previous, Dictionary<string, byte[]?> Baseline, Dictionary<string, byte[]?> Outputs, ManagedMod[] Mods);
}
