using System.IO;
using PCasa.App.Models;

namespace PCasa.App.Services;

public enum BackgroundFolderChangeKind
{
    ScanDirectory,
    ScanFile,
    RemovePath,
    RescanRoot,
    RecoverCache
}

public sealed record BackgroundFolderChange(
    long MonitoredFolderId,
    string Path,
    BackgroundFolderChangeKind Kind,
    bool Recursive);

public sealed class BackgroundFolderMonitor : IDisposable
{
    private readonly Dictionary<long, WatcherState> _watchers = new();
    private readonly Func<string, FileSystemWatcher> _watcherFactory;
    private readonly SourceAvailabilityGate? _sources;
    private bool _disposed;

    public BackgroundFolderMonitor(Func<string, FileSystemWatcher>? watcherFactory = null,
        SourceAvailabilityGate? sources = null)
    {
        // A single scheduler source gate is shared with Tag and scan work.
        // Tests can still inject a deterministic watcher factory.
        _watcherFactory = watcherFactory ?? (path => new FileSystemWatcher(path));
        _sources = sources;
    }

    public event EventHandler<BackgroundFolderChange>? Changed;

    // This method probes disk/network paths and constructs FileSystemWatchers.
    // NEVER invoke it on WPF's UI thread. MainWindow runs it on a background
    // worker and publishes the resulting availability snapshot on Dispatcher.
    public IReadOnlyDictionary<long, bool> Refresh(
        IReadOnlyList<MonitoredFolder> folders,
        bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!enabled)
        {
            Clear();
        }

        var desired = folders.Select(folder => folder.Id).ToHashSet();
        foreach (var existingId in _watchers.Keys
                     .Where(id => !desired.Contains(id))
                     .ToArray())
        {
            Remove(existingId);
        }

        var availability = new Dictionary<long, bool>(folders.Count);
        foreach (var folder in folders)
        {
            // Even a single Directory.Exists can block for seconds or longer
            // when an SMB server disappears. Only the background worker probes.
            bool reachable;
            try
            {
                var rootReady = _sources?.CanAccess(folder.LocalPath) ?? true;
                reachable = rootReady && Directory.Exists(folder.LocalPath);
                // A VPN may disappear AFTER a recent successful source probe.
                // Confirm one missing monitored directory against the root;
                // if the root is offline, every other folder on that drive
                // skips its own expensive Directory.Exists immediately.
                if (rootReady && !reachable && _sources is not null)
                    _sources.CheckAfterSourceError(folder.LocalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reachable = false;
            }

            availability[folder.Id] = reachable;

            if (!reachable)
            {
                Remove(folder.Id);
                continue;
            }

            if (!enabled)
            {
                continue;
            }

            if (_watchers.TryGetValue(folder.Id, out var existing) &&
                string.Equals(existing.Folder.LocalPath, folder.LocalPath,
                    StringComparison.OrdinalIgnoreCase) &&
                Volatile.Read(ref existing.Faulted) == 0)
            {
                continue;
            }
            // A watcher that reported Error may have lost its OS handle.
            // Rescanning once is necessary, but subsequent changes also
            // require replacing that watcher rather than reusing it.

            Remove(folder.Id);
            TryAdd(folder);
        }

        return availability;
    }

    private void TryAdd(MonitoredFolder folder)
    {
        try
        {
            var watcher = _watcherFactory(folder.LocalPath);
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.FileName |
                                   NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.Size |
                                   NotifyFilters.CreationTime;
            watcher.Filter = "*";
            watcher.InternalBufferSize = 64 * 1024;
            watcher.EnableRaisingEvents = false;

            var state = new WatcherState(folder, watcher);
            watcher.Created += (_, e) =>
                RunWatcherCallback(() => OnCreatedOrChanged(state, e.FullPath, created: true));
            watcher.Changed += (_, e) =>
                RunWatcherCallback(() => OnCreatedOrChanged(state, e.FullPath, created: false));
            watcher.Deleted += (_, e) =>
                RunWatcherCallback(() => OnDeleted(state, e.FullPath));
            watcher.Renamed += (_, e) =>
                RunWatcherCallback(() => OnRenamed(state, e));
            watcher.Error += (_, e) => RunWatcherCallback(() =>
            {
                // The callback may run on another thread during Refresh().
                // Do not mutate the watcher dictionary from this callback;
                // only mark this particular watcher for replacement.
                Volatile.Write(ref state.Faulted, 1);
                if (e.GetException() is { } error)
                {
                    PCasaDiagnostics.Write("FileSystemWatcher.Error", error);
                }

                Raise(new BackgroundFolderChange(
                    state.Folder.Id,
                    state.Folder.LocalPath,
                    BackgroundFolderChangeKind.RescanRoot,
                    Recursive: true));
            });

            watcher.EnableRaisingEvents = true;
            _watchers[folder.Id] = state;
        }
        catch
        {
            // An unavailable network share must not prevent PCasa from running.
            // Refresh() will retry when the monitored-folder list is refreshed.
        }
    }

    private void OnCreatedOrChanged(WatcherState state, string path, bool created)
    {
        if (_disposed)
        {
            return;
        }

        if (Directory.Exists(path))
        {
            if (created)
            {
                Raise(new BackgroundFolderChange(
                    state.Folder.Id,
                    path,
                    BackgroundFolderChangeKind.ScanDirectory,
                    Recursive: true));
            }

            return;
        }

        if (!FolderScanner.IsSupportedImagePath(path))
        {
            return;
        }

        Raise(new BackgroundFolderChange(
            state.Folder.Id,
            path,
            BackgroundFolderChangeKind.ScanFile,
            Recursive: false));
    }

    private void OnDeleted(WatcherState state, string path)
    {
        if (_disposed)
        {
            return;
        }

        Raise(new BackgroundFolderChange(
            state.Folder.Id,
            path,
            BackgroundFolderChangeKind.RemovePath,
            Recursive: false));

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Raise(new BackgroundFolderChange(
                state.Folder.Id,
                parent,
                BackgroundFolderChangeKind.ScanDirectory,
                Recursive: false));
        }
    }

    private void OnRenamed(WatcherState state, RenamedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Raise(new BackgroundFolderChange(
            state.Folder.Id,
            e.OldFullPath,
            BackgroundFolderChangeKind.RemovePath,
            Recursive: false));

        if (Directory.Exists(e.FullPath))
        {
            Raise(new BackgroundFolderChange(
                state.Folder.Id,
                e.FullPath,
                BackgroundFolderChangeKind.ScanDirectory,
                Recursive: true));
            return;
        }

        if (FolderScanner.IsSupportedImagePath(e.FullPath))
        {
            var parent = Path.GetDirectoryName(e.FullPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Raise(new BackgroundFolderChange(
                    state.Folder.Id,
                    parent,
                    BackgroundFolderChangeKind.ScanDirectory,
                    Recursive: false));
            }
        }
    }

    private void RunWatcherCallback(Action callback)
    {
        if (_disposed) return;
        try
        {
            callback();
        }
        catch (Exception ex)
        {
            // A lost VPN share can raise events on background OS threads.
            // Never let a watcher exception become an unhandled process fault.
            PCasaDiagnostics.Write("FileSystemWatcher.Callback", ex);
        }
    }

    private void Raise(BackgroundFolderChange change)
    {
        if (!_disposed) Changed?.Invoke(this, change);
    }

    private void Remove(long folderId)
    {
        if (!_watchers.Remove(folderId, out var state))
        {
            return;
        }

        try
        {
            state.Watcher.EnableRaisingEvents = false;
            state.Watcher.Dispose();
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("FileSystemWatcher.Dispose", ex);
        }
    }

    private void Clear()
    {
        foreach (var folderId in _watchers.Keys.ToArray())
        {
            Remove(folderId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }

    private sealed record WatcherState(MonitoredFolder Folder, FileSystemWatcher Watcher)
    {
        public int Faulted;
    }
}
