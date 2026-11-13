using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SnowPatcher.App.ViewModels;
using SnowPatcher.Core;
using SnowPatcher.Core.Backups;

namespace SnowPatcher.App.Views;

public partial class RestoreBackupWindow : Window
{
    private readonly RestoreBackupViewModel _viewModel;
    private readonly Func<ArchiveBackup, Task<BackupRestoreResult>> _restore;
    private readonly Func<ArchiveBackup, string?, Task<ArchiveBackup>>? _rename;
    private readonly DispatcherTimer _timer;

    // Used by Avalonia's runtime resource loader and Rider's XAML preview.
    public RestoreBackupWindow() : this(new GameInstallation(Path.GetTempPath()), [],
        _ => Task.FromException<BackupRestoreResult>(new InvalidOperationException("Select a game installation first.")))
    {
    }

    public RestoreBackupWindow(GameInstallation installation, IReadOnlyList<ArchiveBackup> backups,
        Func<ArchiveBackup, Task<BackupRestoreResult>> restore,
        Func<ArchiveBackup, string?, Task<ArchiveBackup>>? rename = null)
    {
        InitializeComponent();
        _viewModel = new RestoreBackupViewModel(installation, backups, canRename: rename is not null);
        _restore = restore;
        _rename = rename;
        DataContext = _viewModel;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _viewModel.RefreshAvailability();
        Opened += (_, _) => { _viewModel.RefreshAvailability(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
        Closing += (_, args) => { if (_viewModel.IsBusy) { args.Cancel = true; } };
    }

    private void OnCancel(object? sender, RoutedEventArgs args)
    {
        _viewModel.CancelRename();
        Close();
    }

    private void OnRename(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: BackupItemViewModel row })
        {
            BeginRename(row);
        }
    }

    private void BeginRename(BackupItemViewModel row)
    {
        _viewModel.BeginRename(row);
        Dispatcher.UIThread.Post(() =>
        {
            var editor = BackupList.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => ReferenceEquals(t.DataContext, row) && t.IsVisible);
            editor?.Focus();
            editor?.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnCancelName(object? sender, RoutedEventArgs args)
    {
        if (_viewModel.EditingBackup is { } row)
        {
            CancelRename(row);
        }
    }

    private void CancelRename(BackupItemViewModel row)
    {
        _viewModel.CancelRename();
        FocusRename(row);
    }

    private void FocusRename(BackupItemViewModel row) => Dispatcher.UIThread.Post(() =>
        BackupList.GetVisualDescendants().OfType<Button>().FirstOrDefault(b =>
            ReferenceEquals(b.DataContext, row) && b.Classes.Contains("renameBackup") && b.IsVisible)?.Focus(), DispatcherPriority.Input);

    private async void OnAcceptName(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: BackupItemViewModel row })
        {
            await SaveNameAsync(row);
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (_viewModel.EditingBackup is { } editing)
        {
            if (args.Key == Key.Escape)
            {
                args.Handled = true;
                if (!_viewModel.IsBusy)
                {
                    CancelRename(editing);
                }
            }
            else if (args.Key == Key.Enter && args.Source is TextBox)
            {
                args.Handled = true;
                if (!_viewModel.IsBusy)
                {
                    await SaveNameAsync(editing);
                }
            }
        }
        else if (args.Key == Key.F2 && _viewModel.SelectedBackup is { CanRename: true } selected && !_viewModel.IsBusy)
        {
            args.Handled = true;
            BeginRename(selected);
        }
    }

    private async Task SaveNameAsync(BackupItemViewModel row)
    {
        if (_viewModel.IsBusy || _viewModel.EditingBackup != row || _rename is null)
        {
            return;
        }

        var name = row.DraftName;
        if (name.Trim() == (row.Backup.Label ?? ""))
        {
            CancelRename(row);
            return;
        }
        _viewModel.SetBusy(true);
        _viewModel.SetMessage("");
        try
        {
            var updated = await _rename(row.Backup, name);
            _viewModel.FinishRename(row, updated);
            FocusRename(row);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                                      InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            _viewModel.SetMessage(error.Message);
        }
        finally { _viewModel.SetBusy(false); }
    }

    private async void OnRestore(object? sender, RoutedEventArgs args)
    {
        _viewModel.RefreshAvailability();
        if (!_viewModel.CanRestore || _viewModel.SelectedBackup is not { } backup)
        {
            return;
        }

        _viewModel.SetBusy(true);
        _viewModel.SetMessage("Restoring backup...");
        try
        {
            var result = await _restore(backup.Backup);
            _viewModel.SetBusy(false);
            Close(result);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                                      InvalidOperationException or FormatException or System.Text.Json.JsonException)
        {
            _viewModel.SetMessage(error.Message);
        }
        finally { _viewModel.SetBusy(false); }
    }
}
