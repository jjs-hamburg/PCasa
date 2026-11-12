using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// Foreground, read-only UI I/O. This is NOT a scheduler worker and owns no
/// durable jobs. Local and SMB capacities are separate, so an unreachable NAS
/// cannot monopolize local image or Explorer directory reads.
/// SourceFileLocks still guards actual original-file access against writers.
/// </summary>
public sealed class ForegroundReadService
{
    private readonly SemaphoreSlim _localViewer = new(2, 2);
    private readonly SemaphoreSlim _remoteViewer = new(2, 2);
    private readonly SemaphoreSlim _localExplorer = new(2, 2);
    private readonly SemaphoreSlim _remoteExplorer = new(2, 2);
    private readonly SemaphoreSlim _preview = new(2, 2);

    public Task<T> ReadViewerAsync<T>(string path,
        Func<CancellationToken, T> read, CancellationToken token)
        => ReadAsync(path, _localViewer, _remoteViewer, read, token);

    public async Task<T> ReadPreviewAsync<T>(
        Func<CancellationToken, T> read, CancellationToken token)
    {
        await _preview.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var value = read(token);
                token.ThrowIfCancellationRequested();
                return value;
            }, token).ConfigureAwait(false);
        }
        finally { _preview.Release(); }
    }

    public Task<string[]> ReadFoldersAsync(string folder, CancellationToken token)
        => ReadAsync(folder, _localExplorer, _remoteExplorer,
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0
                };
                var result = Directory.EnumerateDirectories(folder, "*", options)
                    .Select(FolderExclusionRules.Normalize)
                    .OrderBy(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p)),
                        StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                ct.ThrowIfCancellationRequested();
                return result;
            }, token);

    // Unlike Explorer projection, the manager avoids system folders and
    // reparse-point traversal; preserve its existing folder-tree policy.
    public Task<string[]> ReadManagerFoldersAsync(string folder, CancellationToken token)
        => ReadAsync(folder, _localExplorer, _remoteExplorer,
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
                };
                var result = Directory.GetDirectories(folder, "*", options)
                    .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                ct.ThrowIfCancellationRequested();
                return result;
            }, token);

    private static async Task<T> ReadAsync<T>(string path,
        SemaphoreSlim local, SemaphoreSlim remote,
        Func<CancellationToken, T> work, CancellationToken token)
    {
        // DriveInfo may itself block on an unavailable mapped SMB drive.
        // Resolve it off the dispatcher before choosing read capacity.
        var network = await Task.Run(
            () => PathClassifier.Classify(path).IsNetwork, token).ConfigureAwait(false);
        var gate = network ? remote : local;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var value = work(token);
                token.ThrowIfCancellationRequested();
                return value;
            }, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
