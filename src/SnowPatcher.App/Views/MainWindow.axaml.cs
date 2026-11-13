using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SnowPatcher.App.ViewModels;
using SnowPatcher.Core;
using SnowPatcher.Core.Backups;

namespace SnowPatcher.App.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly IBrush? _normalListBorder;
    private bool _closing;
    private bool _closeApproved;
    public MainWindow() : this(new MainWindowViewModel()) { }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _normalListBorder = ModsBorder.BorderBrush;
        AddHandler(DragDrop.DragEnterEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        Opened += async (_, _) =>
        {
            await Task.WhenAll(ViewModel.AutoDetectGameFolderAsync(), ViewModel.InitializeLibraryAsync());
            if (!ViewModel.IsClosing)
            {
                _updateTimer.Start();
            }
        };
        _updateTimer.Tick += async (_, _) => await ViewModel.CheckUpdatesAsync();
        Closed += (_, _) => _updateTimer.Stop();
        Closing += OnClosing;
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closeApproved)
        {
            return;
        }

        args.Cancel = true;
        if (_closing || ViewModel.IsRestoring || ViewModel.IsApplying)
        {
            return;
        }

        _closing = true;
        _updateTimer.Stop();
        try
        {
            await ViewModel.CloseAsync();
            _closeApproved = true;
            Dispatcher.UIThread.Post(Close);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            _closing = false;
            ViewModel.SetStatus("Could not finish closing: " + error.Message);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs args)
    {
        var accepted = ViewModel.CanAddMods && !ViewModel.IsClosing &&
            (args.DragEffects & DragDropEffects.Copy) != 0 && GetDroppedZipPaths(args).Length > 0;
        args.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        ModsBorder.BorderBrush = accepted ? Brushes.RoyalBlue : _normalListBorder;
        args.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs args) => ModsBorder.BorderBrush = _normalListBorder;

    private async void OnDrop(object? sender, DragEventArgs args)
    {
        var paths = GetDroppedZipPaths(args);
        var accepted = ViewModel.CanAddMods && !ViewModel.IsClosing &&
            (args.DragEffects & DragDropEffects.Copy) != 0 && paths.Length > 0;
        args.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
        ModsBorder.BorderBrush = _normalListBorder;
        if (accepted)
        {
            await ViewModel.AddArchivesAsync(paths);
        }
    }

    private static string[] GetDroppedZipPaths(DragEventArgs args)
    {
        try
        {
            return (args.DataTransfer.TryGetFiles() ?? []).OfType<IStorageFile>()
                .Select(file => file.TryGetLocalPath()).OfType<string>()
                .Where(path => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or NotSupportedException or
                                          System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    private async void OnBrowseGameFolder(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (!StorageProvider.CanPickFolder)
            {
                ViewModel.SetStatus("Folder selection is unavailable on this desktop.");
                return;
            }

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the SnowRunner installation folder",
                AllowMultiple = false
            });

            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                await ViewModel.SelectGameFolderAsync(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ViewModel.SetStatus($"Could not select the game folder: {error.Message}");
        }
    }

    private async void OnAddMods(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.CanAddMods)
        {
            await new AddModWindow(ViewModel).ShowDialog(this);
        }
    }

    private async void OnUpdateMod(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: ModItemViewModel mod })
        {
            await ViewModel.RunUpdateActionAsync(mod);
        }
    }

    private async void OnModPage(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: ModItemViewModel { ModPage: { } page } })
        {
            return;
        }

        try
        {
            if (!await Launcher.LaunchUriAsync(page))
            {
                ViewModel.SetStatus("Could not open the mod.io page.");
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            ViewModel.SetStatus("Could not open the mod.io page: " + error.Message);
        }
    }

    private async void OnCheckMods(object? sender, RoutedEventArgs args) => await ViewModel.CheckCompatibilityAsync();

    private async void OnPreviousVersions(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: ModItemViewModel mod } button)
        {
            return;
        }

        try
        {
            var versions = await ViewModel.GetPreviousVersionsAsync(mod);
            if (ViewModel.IsClosing)
            {
                return;
            }

            if (versions.Count == 0)
            {
                ViewModel.SetStatus("No previous ZIPs are available.");
                return;
            }
            var items = versions.Select(archive =>
            {
                var version = archive.Package?.Manifest.Version;
                var label = version is null ? "Use previous ZIP" : $"Use version {version}";
                if (version is null || version == mod.Version || versions.Count(a => a.Package?.Manifest.Version == version) > 1)
                {
                    label += $" ({archive.Sha256[..8]})";
                }

                var item = new MenuItem { Header = label };
                ToolTip.SetTip(item, archive.SourcePath);
                item.Click += async (_, _) => await ViewModel.UsePreviousVersionAsync(mod, archive);
                return item;
            }).ToArray();
            var menu = new ContextMenu { ItemsSource = items, Placement = Avalonia.Controls.PlacementMode.Bottom };
            menu.Open(button);
        }
        catch (OperationCanceledException) when (ViewModel.IsClosing) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            ViewModel.SetStatus(error.Message);
        }
    }
    private async void OnApplyMods(object? sender, RoutedEventArgs args) => await ViewModel.ApplyModsAsync();

    private async void OnCompatibilityDetails(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: ModItemViewModel mod })
        {
            await new CompatibilityDetailsWindow(mod.Name, mod.CheckDetail).ShowDialog(this);
        }
    }

    private void OnRemoveMod(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: ModItemViewModel mod })
        {
            ViewModel.RemoveMod(mod);
        }
    }

    private async void OnRestoreBackup(object? sender, RoutedEventArgs args)
    {
        if (!ViewModel.CanRestore)
        {
            return;
        }

        try
        {
            var backups = await ViewModel.GetBackupsAsync();
            if (backups.Count == 0)
            {
                ViewModel.SetStatus("No backups are available for this game build.");
                return;
            }
            var installation = new GameInstallation(ViewModel.GameFolder);
            var dialog = new RestoreBackupWindow(installation, backups, ViewModel.RestoreBackupAsync, ViewModel.RenameBackupAsync);
            var result = await dialog.ShowDialog<BackupRestoreResult?>(this);
            if (result is not null)
            {
                await ViewModel.SelectGameFolderAsync(installation.RootDirectory);
                if (ViewModel.Inspection?.State != FileVerificationState.UnableToVerify)
                {
                    ViewModel.SetStatus(result.Changed ? "Backup restored." : "The archive already matches this backup.");
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            ViewModel.SetStatus(error.Message);
        }
    }
}
