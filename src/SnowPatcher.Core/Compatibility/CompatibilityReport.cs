using System.Collections.Immutable;
using SnowPatcher.Core.Installations;
using SnowPatcher.Core.Mods;

namespace SnowPatcher.Core.Compatibility;

public sealed record CompatibilityInput(Guid LibraryId, ModArchive Archive);
public sealed record CompatibilityIssue(string Code, string Message, string? Operation = null, string? Target = null,
    string? OtherMod = null);
public sealed record ModCompatibility(Guid LibraryId, string Name, ImmutableArray<CompatibilityIssue> Issues)
{
    public string Status => Issues.Any(i => i.Code == "conflict") ? "Conflict" :
        Issues.Any(i => i.Code == "dependency") ? "Dependency" :
        Issues.Any(i => i.Code == "changed") ? "Changed" :
        Issues.Any(i => i.Code == "missing") ? "Missing" :
        Issues.Any(i => i.Code == "manual") ? "Manual ZIP" : Issues.Length > 0 ? "Needs review" : "Matches";
    public string Detail => Issues.Length == 0 ? "This mod's required game files match." :
        string.Join("\n\n", Issues.Select(i => i.Message + (i.Operation is null ? "" : $"\nOperation: {i.Operation}") +
            (i.Target is null ? "" : $"\nTarget: {i.Target}")));
}
public sealed record CheckedFile(string Path, string Sha256);
public sealed record CheckedOperation(string ModId, string OperationId, string Type, string Target);

// A read-only structural check. Apply preserves the starting files and validates outputs before committing them.
public sealed record CompatibilityReport(DateTimeOffset CheckedAt, string GameRoot, string? BuildId,
    FileVerificationState InitialPakState, ImmutableArray<CheckedFile> GameFiles, ImmutableArray<CheckedFile> Packages,
    ImmutableArray<string> ModOrder, ImmutableArray<CheckedOperation> Operations, ImmutableArray<ModCompatibility> Mods, bool UsesManagedBaseline = false,
    GameStore Store = GameStore.Steam)
{
    public bool HasIssues => Mods.Any(m => !m.Issues.IsEmpty);
    public string BaselineDetail => UsesManagedBaseline ? "Checked against SnowPatcher's saved starting files for this build." : InitialPakState switch
    {
        FileVerificationState.Original => $"initial.pak matches {(Store == GameStore.Epic ? "Epic's" : "Steam's")} original for this build.",
        FileVerificationState.Changed => "Checked against the existing modified installation. Patches must match these files before they can be applied.",
        _ when Store == GameStore.Manual => "Checked against the installed files. No launcher reference is available to verify originals.",
        _ => "The launcher's original initial.pak could not be verified. These results describe the installed files."
    };
}

internal sealed class CheckFailure(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
