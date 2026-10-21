using System.Diagnostics;
using System.IO;
using PCasa.App.Data;
using PCasa.App.Models;

namespace PCasa.App.Services;

public sealed record ScanProgress(
    int ImageFilesSeen,
    int Updated,
    int Unchanged,
    int Failed,
    string? CurrentFile,
    int Added = 0,
    int Changed = 0,
    int CacheRepaired = 0);

public sealed record ScanResult(
    int ImageFilesSeen,
    int Updated,
    int Unchanged,
    int Failed,
    int Removed = 0)
{
    // A pure deletion is a catalog change even when no source was imported.
    // The background coordinator must refresh the gallery in both cases.
    public bool CatalogChanged => Updated > 0 || Removed > 0;
}

public sealed class FolderScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif",
        ".webp", ".heic", ".heif", ".avif", ".jxl"
    };

    private readonly ImageCatalogRepository _catalog;
    private readonly ThumbnailCache _thumbnailCache;
    private readonly FolderRepository _folderRepository;
    private readonly BrowseThumbnailProfileRepository _browseProfile;

    public FolderScanner(
        ImageCatalogRepository catalog,
        ThumbnailCache thumbnailCache,
        FolderRepository? folderRepository = null,
        BrowseThumbnailProfileRepository? profileRepository = null)
    {
        _browseProfile = profileRepository ?? new BrowseThumbnailProfileRepository(new LocalDatabase());
        _catalog = catalog;
        _thumbnailCache = thumbnailCache;
        _folderRepository = folderRepository ?? new FolderRepository(new LocalDatabase());
    }

    public static bool IsSupportedImagePath(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path));

    public ScanResult Scan(
        MonitoredFolder folder,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => ScanDirectory(
            folder,
            folder.LocalPath,
            recursive: true,
            progress,
            cancellationToken);

    public ScanResult ScanDirectory(
        MonitoredFolder folder,
        string localDirectory,
        bool recursive,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return ScanDirectoryLocked(folder, localDirectory, recursive, progress, cancellationToken);
    }

    public ScanResult ScanFile(
        MonitoredFolder folder,
        string localFile,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedImagePath(localFile) || !File.Exists(localFile))
            return new ScanResult(0, 0, 0, 0);
        return ScanDirectoryLocked(
            folder, Path.GetDirectoryName(localFile)!, false,
            progress, cancellationToken, localFile);
    }

    private ScanResult ScanDirectoryLocked(
        MonitoredFolder folder,
        string localDirectory,
        bool recursive,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        string? specificFile = null)
    {
        var scanRoot = Path.GetFullPath(localDirectory).Replace('/', '\\');
        EnsureInsideMonitoredFolder(folder, scanRoot);

        if (!Directory.Exists(scanRoot))
        {
            throw new DirectoryNotFoundException(
                $"Der einzulesende Ordner ist nicht erreichbar: {scanRoot}");
        }

        var imageFilesSeen = 0;
        var updated = 0;
        var unchanged = 0;
        var added = 0;
        var changed = 0;
        var cacheRepaired = 0;
        var failed = 0;
        Exception? firstFailure = null;
        string? firstFailurePath = null;
        var photoFolderIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var observedPaths = specificFile is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
        var progressClock = Stopwatch.StartNew();
        var catalogCommitClock = Stopwatch.StartNew();

        var sourceState = _folderRepository.GetSourceState(folder.CanonicalPath);
        if (sourceState is not { DesiredPresent: true })
            throw new OperationCanceledException("The monitored source was removed.");
        var generation = sourceState.Value.Generation;
        var exclusions = _folderRepository.GetExcludedPaths(folder.Id);

        // Do not even enumerate excluded subtrees. Merely filtering files
        // after Directory.EnumerateFiles(... RecurseSubdirectories=true) would
        // still traverse potentially hundreds of thousands of excluded files.
        if (FolderExclusionRules.IsExcluded(scanRoot, exclusions))
        {
            return new ScanResult(0, 0, 0, 0);
        }

        var profile = _browseProfile.Get();
        using var packStore = new PackedThumbnailStore();
        var scanSession = _catalog.CreateScanSession(
            folder.CanonicalPath, folder.Id, generation, cancellationToken);
        try
        {
            foreach (var localPath in specificFile is null
                ? EnumerateIncludedFiles(scanRoot, recursive, exclusions, cancellationToken)
                : new[] { specificFile })
            {
                cancellationToken.ThrowIfCancellationRequested();

                var extension = Path.GetExtension(localPath);
                if (!SupportedExtensions.Contains(extension))
                {
                    continue;
                }

                imageFilesSeen++;

                try
                {
                    observedPaths?.Add(localPath);
                    var file = new FileInfo(localPath);
                    var canonicalPath = ResolveCanonicalFullPath(folder, localPath);
                    var modifiedUtcTicks = file.LastWriteTimeUtc.Ticks;

                    var photoFolderLocalPath = Path.GetDirectoryName(localPath)
                        ?? scanRoot;
                    var photoFolderCanonicalPath = ResolveCanonicalFullPath(
                        folder,
                        photoFolderLocalPath);

                    if (!photoFolderIds.TryGetValue(photoFolderCanonicalPath, out var photoFolderId))
                    {
                        var trimmedFolderPath = Path.TrimEndingDirectorySeparator(photoFolderLocalPath);
                        var displayName = Path.GetFileName(trimmedFolderPath);
                        if (string.IsNullOrWhiteSpace(displayName))
                        {
                            displayName = photoFolderLocalPath;
                        }

                        photoFolderId = scanSession.GetOrCreatePhotoFolder(
                            folder.Id,
                            photoFolderLocalPath,
                            photoFolderCanonicalPath,
                            displayName);
                        photoFolderIds.Add(photoFolderCanonicalPath, photoFolderId);
                    }

                    var existing = scanSession.GetState(canonicalPath);
                    if (existing is not null &&
                        existing.FileSize == file.Length &&
                        existing.ModifiedUtcTicks == modifiedUtcTicks &&
                        File.Exists(existing.ThumbnailPath))
                    {
                        if (existing.PhotoFolderId != photoFolderId ||
                            !string.Equals(existing.LocalPath, localPath, StringComparison.OrdinalIgnoreCase))
                        {
                            scanSession.UpdateLocation(
                                canonicalPath,
                                folder.Id,
                                photoFolderId,
                                localPath);
                        }

                        unchanged++;
                    }
                    else
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        scanSession.FlushPending();
                        var thumbnail = _thumbnailCache.CreateOrUpdate(
                            localPath,
                            canonicalPath,
                            file.Length,
                            modifiedUtcTicks);

                        cancellationToken.ThrowIfCancellationRequested();

                        // Decode/encode outside the cache lock. Keep the
                        // append and its catalog commit together so pack
                        // compaction cannot delete an unreferenced new pack.
                        var smallTiers = profile.Tiers.Where(tier => tier <= 512).ToArray();
                        var largeTiers = profile.Tiers.Where(tier => tier > 512).ToArray();
                        var preparedSmall = smallTiers.Length > 0
                            ? packStore.PrepareTiers(
                                thumbnail.Path, new BrowseThumbnailProfile(smallTiers),
                                cancellationToken)
                            : Array.Empty<PackedThumbnailStore.PreparedThumbnailTier>();
                        var preparedLarge = largeTiers.Length > 0
                            ? packStore.PrepareTiers(
                                localPath, new BrowseThumbnailProfile(largeTiers),
                                cancellationToken)
                            : Array.Empty<PackedThumbnailStore.PreparedThumbnailTier>();
                        var keywords = ImageTagRepository.ReadKeywords(localPath);
                        cancellationToken.ThrowIfCancellationRequested();

                        lock (CatalogRemovalService.CacheLock)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var locations = new List<PackedThumbnailLocation>();
                            locations.AddRange(packStore.AppendPrepared(preparedSmall, cancellationToken));
                            locations.AddRange(packStore.AppendPrepared(preparedLarge, cancellationToken));
                            scanSession.Upsert(new CatalogImageData(
                                folder.Id, photoFolderId, localPath, canonicalPath,
                                file.Name, extension.ToLowerInvariant(), file.Length,
                                modifiedUtcTicks, thumbnail.Width, thumbnail.Height,
                                thumbnail.Path));
                            if (keywords is not null)
                                scanSession.SaveTags(canonicalPath, modifiedUtcTicks, keywords);
                            scanSession.SaveBrowseLocations(
                                canonicalPath, modifiedUtcTicks, locations, profile.Tiers.Count);
                            // The lock covers durable SQLite commit as well as
                            // append; a cancel after appending rolls back index
                            // writes and idle cleanup later reclaims orphan bytes.
                            scanSession.FlushPending();
                        }

                        updated++;
                        if (existing is null)
                            added++;
                        else if (existing.FileSize != file.Length ||
                                 existing.ModifiedUtcTicks != modifiedUtcTicks)
                            changed++;
                        else
                            cacheRepaired++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    failed++;
                    if (firstFailure is null)
                    {
                        firstFailure = ex;
                        firstFailurePath = localPath;
                    }
                }

                if (catalogCommitClock.ElapsedMilliseconds >= 1000)
                {
                    scanSession.Dispose();
                    scanSession = _catalog.CreateScanSession(
                        folder.CanonicalPath, folder.Id, generation, cancellationToken);
                    catalogCommitClock.Restart();
                }

                ReportProgress(localPath);
            }
        }
        catch (OperationCanceledException)
        {
            scanSession.RollbackPending();
            throw;
        }
        finally
        {
            scanSession.Dispose();
        }

        ReportProgress(null, force: true);
        // Even the final UI progress callback can cancel a scan. A completed
        // enumeration is not permission to reconcile a cancelled operation.
        cancellationToken.ThrowIfCancellationRequested();

        // Complete scans converge the catalog to the filesystem; single-file
        // events never prune. Failed or interrupted scans must never delete.
        var removed = 0;
        if (observedPaths is not null && failed == 0)
        {
            // The source may disappear AFTER successful enumeration (VPN/SMB
            // drop, drive removal, renamed root) but BEFORE reconciliation.
            // Empty/missing sources never authorize pruning old catalog rows.
            if (!Directory.Exists(folder.LocalPath) || !Directory.Exists(scanRoot))
                throw new DirectoryNotFoundException(
                    $"Ordner nach Einlesen nicht mehr erreichbar: {scanRoot}");
            cancellationToken.ThrowIfCancellationRequested();

            packStore.Dispose();
            var cleanup = new CatalogRemovalService(_catalog.Database);
            // Only directories that were actually enumerated can authorize
            // deleting missing catalog entries. A shallow scan must not prune
            // images from nested folders it never visited.
            removed = cleanup.RemoveMissing(folder.Id, scanRoot, observedPaths,
                recursive: recursive, cancellationToken: cancellationToken,
                sourceCanonicalPath: folder.CanonicalPath,
                expectedGeneration: generation);
        }

        if (imageFilesSeen > 0 && updated == 0 && unchanged == 0 && failed == imageFilesSeen)
        {
            throw new InvalidOperationException(
                $"Keine der {imageFilesSeen:N0} gefundenen Bilddateien konnte eingelesen werden.\n\n" +
                $"Erste fehlerhafte Datei:\n{firstFailurePath}\n\n" +
                $"Ursache:\n{firstFailure?.Message}",
                firstFailure);
        }

        return new ScanResult(imageFilesSeen, updated, unchanged, failed, removed);

        void ReportProgress(string? currentFile, bool force = false)
        {
            if (!force && progressClock.ElapsedMilliseconds < 250)
            {
                return;
            }

            progressClock.Restart();
            progress?.Report(new ScanProgress(
                imageFilesSeen,
                updated,
                unchanged,
                failed,
                currentFile,
                added,
                changed,
                cacheRepaired));
        }
    }

    private static IEnumerable<string> EnumerateIncludedFiles(
        string scanRoot,
        bool recursive,
        IReadOnlyCollection<string> exclusions,
        CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(scanRoot);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            // Never silently omit inaccessible directories: an incomplete scan
            // must not authorize deletion of the supposedly missing images.
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (FolderExclusionRules.IsExcluded(directory, exclusions)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.GetFiles(directory, "*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Verzeichnis konnte nicht vollständig gelesen werden: {directory}", ex);
            }

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                yield return file;
            }

            if (!recursive) continue;
            string[] children;
            try
            {
                children = Directory.GetDirectories(directory, "*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Verzeichnis konnte nicht vollständig gelesen werden: {directory}", ex);
            }

            foreach (var child in children.Reverse())
            {
                if (!FolderExclusionRules.IsExcluded(child, exclusions))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static void EnsureInsideMonitoredFolder(MonitoredFolder folder, string scanRoot)
    {
        var monitoredRoot = Path.GetFullPath(folder.LocalPath)
            .Replace('/', '\\')
            .TrimEnd('\\');
        var normalizedScanRoot = scanRoot.TrimEnd('\\');

        if (string.Equals(
                monitoredRoot,
                normalizedScanRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var prefix = monitoredRoot + "\\";
        if (!normalizedScanRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Der Ordner liegt nicht innerhalb des überwachten Bereichs: {scanRoot}");
        }
    }

    private static string ResolveCanonicalFullPath(
        MonitoredFolder folder,
        string fullLocalPath)
    {
        var normalizedLocal = Path.GetFullPath(fullLocalPath);

        if (string.Equals(
            folder.LocalPath,
            folder.CanonicalPath,
            StringComparison.OrdinalIgnoreCase))
        {
            return normalizedLocal.Replace('/', '\\');
        }

        var suffix = Path.GetRelativePath(folder.LocalPath, normalizedLocal);
        if (suffix == ".")
        {
            return folder.CanonicalPath.Replace('/', '\\');
        }

        return Path.Combine(folder.CanonicalPath, suffix).Replace('/', '\\');
    }
}
