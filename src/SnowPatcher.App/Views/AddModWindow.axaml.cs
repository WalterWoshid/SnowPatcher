using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SnowPatcher.App.ViewModels;

namespace SnowPatcher.App.Views;

[SuppressMessage("Design", "CA1001", Justification = "The window's Closed handler disposes its cancellation source.")]
public partial class AddModWindow : Window
{
    private readonly MainWindowViewModel _main;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _busy;
    private bool _closeWhenFinished;

    public AddModWindow() : this(new MainWindowViewModel()) { }

    public AddModWindow(MainWindowViewModel main)
    {
        _main = main;
        InitializeComponent();
        Opened += (_, _) => LinkBox.Focus();
        Closing += (_, args) =>
        {
            if (!_busy)
            {
                return;
            }

            args.Cancel = true;
            _closeWhenFinished = true;
            _cancellation.Cancel();
            ShowMessage("Cancelling...");
        };
        Closed += (_, _) => _cancellation.Dispose();
    }

    private void OnLinkChanged(object? sender, TextChangedEventArgs args)
    {
        if (AddButton is not null)
        {
            AddButton.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(LinkBox.Text);
        }
    }

    private async void OnAdd(object? sender, RoutedEventArgs args)
    {
        if (_busy || string.IsNullOrWhiteSpace(LinkBox.Text))
        {
            return;
        }

        SetBusy(true);
        ShowMessage("Looking up mod...");
        try
        {
            await _main.AddFromModioAsync(LinkBox.Text, new Progress<int>(p => ShowMessage($"Downloading... {p}%")), _cancellation.Token);
            _closeWhenFinished = true;
        }
        catch (OperationCanceledException) { _closeWhenFinished = true; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                                          InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            ShowMessage(error.Message);
        }
        finally
        {
            SetBusy(false);
            if (_closeWhenFinished)
            {
                Close();
            }
        }
    }

    private async void OnChooseZip(object? sender, RoutedEventArgs args)
    {
        if (_busy)
        {
            return;
        }

        try
        {
            if (!StorageProvider.CanOpen)
            {
                ShowMessage("File selection is unavailable on this desktop.");
                return;
            }
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add mod ZIPs",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("ZIP archives") { Patterns = ["*.zip"] }]
            });
            if (files.Count == 0)
            {
                return;
            }

            SetBusy(true);
            ShowMessage("Checking ZIPs...");
            await _main.AddArchivesAsync(files.Select(file => file.TryGetLocalPath()).OfType<string>(), _cancellation.Token);
            _closeWhenFinished = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowMessage(error.Message);
        }
        finally
        {
            SetBusy(false);
            if (_closeWhenFinished)
            {
                Close();
            }
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs args) => Close();
    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.IsVisible = text.Length > 0;
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        LinkBox.IsEnabled = !busy;
        ChooseZipButton.IsEnabled = !busy;
        AddButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(LinkBox.Text);
    }
}
