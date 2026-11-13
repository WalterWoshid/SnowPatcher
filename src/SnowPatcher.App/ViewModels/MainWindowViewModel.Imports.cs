namespace SnowPatcher.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task AddArchivesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        if (_isApplying || IsClosing)
        {
            return;
        }

        if (!_libraryReady)
        {
            SetStatus("The mod library is not ready.");
            return;
        }
        var selectedPaths = paths.ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _archiveImportCancellation.Token);
        cancellationToken = cancellation.Token;
        try
        {
            await _archiveImportLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        try
        {
            var failures = new List<string>();
            var added = 0;
            foreach (var path in selectedPaths)
            {
                try
                {
                    var fullPath = Path.GetFullPath(path);
                    SetStatus($"Checking {Path.GetFileName(fullPath)}...");
                    var archive = await _library.ImportAsync(fullPath, cancellationToken: cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await AcceptArchiveAsync(archive, null, cancellationToken))
                    {
                        added++;
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    failures.Add($"{Path.GetFileName(path)}: {error.Message}");
                }
            }

            RefreshModCount();
            SetStatus(failures.Count > 0
                ? string.Join(Environment.NewLine, failures)
                : added > 0
                    ? ""
                    : "Those ZIPs are already in the list.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { _archiveImportLock.Release(); RefreshCompatibilityActions(); }
    }

    public async Task AddFromModioAsync(string link, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_isApplying || IsClosing)
        {
            return;
        }

        if (!_libraryReady)
        {
            throw new IOException("The mod library is not ready.");
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _archiveImportCancellation.Token);
        var token = cancellation.Token;
        await _archiveImportLock.WaitAsync(token);
        try
        {
            var release = await _modio.ResolveAsync(link, token);
            var existing = Mods.FirstOrDefault(m => m.Source?.ModId == release.Source.ModId);
            if (existing is not null)
            {
                existing.SetChecked(release);
                SaveLibrary();
                SetStatus("That mod is already in the list.");
                return;
            }
            var download = await _library.DownloadAsync(_modio, release, progress, token);
            token.ThrowIfCancellationRequested();
            await AcceptArchiveAsync(download.Archive, download.Source, token);
            SetStatus("");
        }
        finally { _archiveImportLock.Release(); RefreshCompatibilityActions(); }
    }
}
