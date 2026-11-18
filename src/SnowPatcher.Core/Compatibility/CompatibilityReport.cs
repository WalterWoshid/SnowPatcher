using System.Collections.Immutable;
using SnowPatcher.Core.Installations;
using SnowPatcher.Core.Mods;

namespace SnowPatcher.Core.Compatibility;

public sealed record CompatibilityInput(Guid LibraryId, ModArchive Archive);
public sealed record CompatibilityIssue(string Code, string Message, string? Operation = null, string? Target = null,
    string? OtherMod = null, bool IsBlocking = true);
public sealed record ModCompatibility(Guid LibraryId, string Name, ImmutableArray<CompatibilityIssue> Issues)
{
    public bool HasBlockingIssues => Issues.Any(i => i.IsBlocking);
    public bool HasWarnings => Issues.Any(i => !i.IsBlocking);
    public string Status => HasBlockingCode("conflict") ? "Conflict" :
        HasBlockingCode("dependency") ? "Dependency" :
        HasBlockingCode("changed") ? "Changed" :
        HasBlockingCode("missing") ? "Missing" :
        HasBlockingCode("manual") ? "Manual ZIP" : HasBlockingIssues ? "Needs review" : HasWarnings ? "Translation skipped" : "Matches";
    public string Detail => Issues.Length == 0 ? "This mod's required game files match." :
        string.Join("\n\n", Issues.Select(i => i.Message + (i.Operation is null ? "" : $"\nOperation: {i.Operation}") +
            (i.Target is null ? "" : $"\nTarget: {i.Target}")));

    private bool HasBlockingCode(string code) => Issues.Any(i => i.IsBlocking && i.Code == code);
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
    public bool HasBlockingIssues => Mods.Any(m => m.HasBlockingIssues);
    public bool HasWarnings => Mods.Any(m => m.HasWarnings);
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
