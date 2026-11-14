using SnowPatcher.Core.Epic;
using SnowPatcher.Core.Steam;

namespace SnowPatcher.Core.Installations;

public static class GameInstallationVerifier
{
    public static async Task<InstallationInspection> InspectAsync(GameInstallation installation, CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var state = GameInstallState.Read(installation);
            return state.Store switch
            {
                GameStore.Steam => await SteamInstallationVerifier.InspectAsync(installation, token).ConfigureAwait(false),
                GameStore.Epic => await EpicInstallationVerifier.InspectAsync(installation, state, token).ConfigureAwait(false),
                _ => new(state.BuildId, FileVerificationState.UnableToVerify,
                    "No launcher reference is available. Compatibility checks use the installed files.", Store: state.Store)
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return new(null, FileVerificationState.UnableToVerify, error.Message);
        }
    }

    internal static void VerifyOriginals(GameInstallation installation, GameInstallState state, IReadOnlyDictionary<string, byte[]?> files)
    {
        state.EnsureUnchanged(installation);
        if (state.Store == GameStore.Steam)
        {
            SteamInstallationVerifier.VerifyOriginals(installation, SteamInstallState.Read(installation), files);
        }
        else if (state.Store == GameStore.Epic)
        {
            EpicInstallationVerifier.VerifyOriginals(state, files);
        }
        else
        {
            throw new InvalidDataException("Original files cannot be verified for this installation. Restore the saved starting files or a recorded backup before reapplying mods.");
        }
        state.EnsureUnchanged(installation);
    }
}
