using System.Collections.ObjectModel;
using SnowPatcher.App.Services;
using SnowPatcher.Core;
using SnowPatcher.Core.Backups;
using SnowPatcher.Core.Compatibility;
using SnowPatcher.Core.Mods;
using SnowPatcher.Core.Patching;
using SnowPatcher.Core.Settings;
using SnowPatcher.Core.Updates;

namespace SnowPatcher.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private string _gameFolder = "";
    private string _statusText = "";
    private bool _isDetecting;
    private int _folderSelectionVersion;
    private CancellationTokenSource? _verificationCancellation;
    private string _buildText = "";
    private string _buildDetail = "";
    private readonly BackupStore _backupStore;
    private bool _hasBackups;
    private bool _isBackingUp;
    private bool _isRestoring;
    private readonly SemaphoreSlim _archiveImportLock = new(1, 1);
    private readonly CancellationTokenSource _archiveImportCancellation = new();
    private readonly ModioClient _modio;
    private readonly ModLibraryStore _library;
    private bool _libraryReady;
    private Task? _initializationTask;
    private Task? _closeTask;
    private Task? _applyTask;
    private Task<BackupRestoreResult>? _restoreTask;
    private bool _isClosing;
    private bool _revertingToggle;
    private bool _checkingUpdates;
    private string _libraryError = "";
    private string _compatibilitySummary = "";
    private CancellationTokenSource? _compatibilityCancellation;
    private int _compatibilityVersion;
    private bool _isCheckingCompatibility;
    private readonly PatchInstaller _patchInstaller;
    private PatchCheckResult? _patchCheck;
    private bool _isApplying;
    private readonly bool _includeBundledMods;
    private readonly PreferencesService? _preferences;

    public MainWindowViewModel(ModioClient? modio = null, ModLibraryStore? library = null, PatchInstaller? patchInstaller = null, BackupStore? backupStore = null, bool includeBundledMods = true, ApplicationUpdateService? applicationUpdates = null, PreferencesService? preferences = null)
    {
        _modio = modio ?? new ModioClient(ModioConfiguration.ReadApiKey());
        _library = library ?? new ModLibraryStore();
        _patchInstaller = patchInstaller ?? new PatchInstaller();
        _backupStore = backupStore ?? new BackupStore();
        _includeBundledMods = includeBundledMods;
        _applicationUpdates = applicationUpdates ?? new ApplicationUpdateService();
        _preferences = preferences;
    }

    public AppPreferences CurrentPreferences => _preferences?.Current ?? DefaultPreferences;
    private static AppPreferences DefaultPreferences { get; } = new();
    public PreferencesService? Preferences => _preferences;
    public ObservableCollection<ModItemViewModel> Mods { get; } = [];
    public string GameFolder => _gameFolder;
    public string StatusText => _statusText.Length > 0 ? _statusText : _compatibilitySummary;
    public bool IsEmpty => Mods.Count == 0;
    public string ModCountText => $"{Mods.Count} {(Mods.Count == 1 ? "mod" : "mods")}";
    public bool HasInstallationDetails => !string.IsNullOrEmpty(GameFolder);
    public string BuildText => _buildText;
    public string BuildDetail => _buildDetail;
    public InstallationInspection? Inspection
    { get; private set; }
    public bool CanBrowse => !_isBackingUp && !_isRestoring && !_isApplying && !_updatingApplication && !_automaticWork && !_pendingInstallationRunning && !IsClosing;
    public bool CanRestore => _hasBackups && CanBrowse;
    public bool IsRestoring => _isRestoring;
    public bool CanAddMods => _libraryReady && CanEditMods;
    public bool CanEditMods => !_isApplying && !_isRestoring && !_updatingApplication && !_automaticWork && !_pendingInstallationRunning && !IsClosing;
    public bool IsApplying => _isApplying;
    public bool CanApply => !_automaticWork && !_pendingInstallationRunning && CanApplySelection;
    private bool CanApplySelection => !IsClosing && _patchCheck?.CanApply == true && !_isApplying && !_isBackingUp && !_isRestoring && !_isCheckingCompatibility && !_updatingApplication &&
        !Mods.Any(m => m.IsBusy || (m.IsEnabled && m.IsUnavailable)) && _archiveImportLock.CurrentCount > 0;
    public string ApplyText => _isApplying ? "Applying..." : _patchCheck?.NeedsRecovery == true ? "Recover" : "Apply";
    public string ApplyDetail => _patchCheck?.BlockReason ?? "Apply the enabled mods. Existing files are backed up first.";
    public bool IsClosing => _isClosing;
    public bool CanCheckMods => !_automaticWork && !_pendingInstallationRunning && CanCheckSelection;
    private bool CanCheckSelection => _libraryReady && GameFolder.Length > 0 && Inspection is not null &&
        !_isCheckingCompatibility && !_isBackingUp && !_isRestoring && !_isApplying && !_updatingApplication && !IsClosing;
    public string CheckModsText => _isCheckingCompatibility ? "Checking..." : "Check mods";
    public CompatibilityReport? Compatibility
    { get; private set; }

    public void CancelImports() => _ = CloseAsync();

    public Task CloseAsync() => _closeTask ??= CloseCoreAsync();

    public ValueTask DisposeAsync() => new(CloseAsync());

    private async Task CloseCoreAsync()
    {
        _isClosing = true;
        _compatibilityCancellation?.Cancel();
        CancelVerification();
        _archiveImportCancellation.Cancel();
        RaisePropertyChanged(nameof(CanAddMods));
        RaisePropertyChanged(nameof(CanEditMods));
        RefreshBackupActions();
        try
        {
            if (_initializationTask is not null)
            {
                await _initializationTask;
            }
            if (_applyTask is not null)
            {
                await _applyTask;
            }

            if (_restoreTask is not null)
            {
                await _restoreTask;
            }
            if (_automaticTask is not null)
            {
                await _automaticTask;
            }
            if (_applicationCheckTask is not null)
            {
                await _applicationCheckTask;
            }
            if (_applicationUpdateTask is not null)
            {
                await _applicationUpdateTask;
            }
        }
        finally
        {
            await _archiveImportLock.WaitAsync();
            _archiveImportLock.Release();
            _library.Dispose();
            _archiveImportLock.Dispose();
            _archiveImportCancellation.Dispose();
        }
    }

    private static bool IsLibraryError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or InvalidOperationException;

    public void SetStatus(string text)
    {
        _statusText = text.Length == 0 ? _libraryError : text;
        RaisePropertyChanged(nameof(StatusText));
    }
}
