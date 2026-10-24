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
    int CacheRepaired = 0,
    long? DiscoveredPhotoFolderId = null,
    string? DiscoveredPhotoFolderPath = null);

public sealed record ScanResult(
    int ImageFilesSeen,
    int Updated,
    int Unchanged,
    int Failed,
    int Removed = 0,
    int MetadataUpdated = 0)
{
    // Re-index only when content, removal, or catalog annotations changed.
    public bool CatalogChanged => Updated > 0 || Removed > 0 || MetadataUpdated > 0;
}

public sealed class FolderScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif",
        ".webp", ".heic", ".heif", ".avif", ".jxl",
        ".arw", ".nef", ".nrw", ".cr2", ".cr3", ".dng",
        ".raf", ".rw2", ".orf", ".pef", ".srw", ".crw"
    };

    /// <summary>
    /// Durable-for-this-session enumeration cursor. Scanner yields may restart
    /// the worker delegate, but never restart already traversed NAS folders.
    /// The source generation fences invalidate an unsafe stale checkpoint.
    /// </summary>
    public sealed class ScanResumeState : IDisposable
    {
        internal IEnumerator<string>? Enumerator;
        internal string? CurrentFile;
        internal bool EnumerationComplete;
        internal long? SourceGeneration;
        internal long? ScanEpoch;
        internal readonly Dictionary<string, long> PhotoFolderIds =
            new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> ObservedPaths =
            new(StringComparer.OrdinalIgnoreCase);
        internal int ImageFilesSeen, Updated, Unchanged, Added, Changed,
            CacheRepaired, MetadataUpdated, Failed;
        internal Exception? FirstFailure;
        internal string? FirstFailurePath;

        public int CommittedFiles => ImageFilesSeen;
        public void Reset()
        {
            Enumerator?.Dispose();
            Enumerator = null;
            CurrentFile = null;
            EnumerationComplete = false;
            SourceGeneration = null;
            ScanEpoch = null;
            PhotoFolderIds.Clear();
            ObservedPaths.Clear();
            ImageFilesSeen = Updated = Unchanged = Added = Changed =
                CacheRepaired = MetadataUpdated = Failed = 0;
            FirstFailure = null;
            FirstFailurePath = null;
        }

        public void Dispose() => Reset();
    }

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
        CancellationToken cancellationToken = default,
        ScanResumeState? resume = null)
    {
        return ScanDirectoryLocked(
            folder, localDirectory, recursive, progress, cancellationToken,
            resume: resume);
    }

    public ScanResult ScanFile(
        MonitoredFolder folder,
        string localFile,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool publishCatalogChange = true)
    {
        if (!IsSupportedImagePath(localFile) || !File.Exists(localFile))
            return new ScanResult(0, 0, 0, 0);
        return ScanDirectoryLocked(
            folder, Path.GetDirectoryName(localFile)!, false,
            progress, cancellationToken, localFile,
            publishCatalogChange: publishCatalogChange);
    }

    private ScanResult ScanDirectoryLocked(
        MonitoredFolder folder,
        string localDirectory,
        bool recursive,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        string? specificFile = null,
        ScanResumeState? resume = null,
        bool publishCatalogChange = true)
    {
        var scanRoot = Path.GetFullPath(localDirectory).Replace('/', '\\');
        EnsureInsideMonitoredFolder(folder, scanRoot);

        if (!Directory.Exists(scanRoot))
        {
            throw new DirectoryNotFoundException(
                $"Der einzulesende Ordner ist nicht erreichbar: {scanRoot}");
        }

        var progressClock = Stopwatch.StartNew();
        var catalogCommitClock = Stopwatch.StartNew();

        var sourceState = _folderRepository.GetSourceState(folder.CanonicalPath);
        if (sourceState is not { DesiredPresent: true })
            throw new OperationCanceledException("The monitored source was removed.");
        var generation = sourceState.Value.Generation;
        if (resume?.SourceGeneration is long previousGeneration &&
            previousGeneration != generation)
        {
            // Monitoring/exclusion changed while this scan was paused.
            // The old package must not silently restart from image zero;
            // a new source request owns the replacement inventory.
            resume.Reset();
            throw new OperationCanceledException(
                "The scan source configuration changed during a paused inventory.");
        }
        if (resume is not null) resume.SourceGeneration = generation;
        // Capture the inventory epoch only once per resumable enumeration.
        // A newer manual scan can supersede its deletion authority without
        // discarding the old scanner's work or restarting NAS enumeration.
        var scanEpoch = resume?.ScanEpoch ??
            _folderRepository.GetScanEpoch(folder.CanonicalPath);
        if (resume is not null) resume.ScanEpoch = scanEpoch;
        var exclusions = _folderRepository.GetExcludedPaths(folder.Id);
        if (FolderExclusionRules.IsExcluded(scanRoot, exclusions))
            return new ScanResult(0, 0, 0, 0);

        var imageFilesSeen = resume?.ImageFilesSeen ?? 0;
        var updated = resume?.Updated ?? 0;
        var unchanged = resume?.Unchanged ?? 0;
        var added = resume?.Added ?? 0;
        var changed = resume?.Changed ?? 0;
        var cacheRepaired = resume?.CacheRepaired ?? 0;
        var metadataUpdated = resume?.MetadataUpdated ?? 0;
        var failed = resume?.Failed ?? 0;
        Exception? firstFailure = resume?.FirstFailure;
        string? firstFailurePath = resume?.FirstFailurePath;
        var photoFolderIds = resume?.PhotoFolderIds ??
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var observedPaths = specificFile is null
            ? resume?.ObservedPaths ??
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;

        var profile = _browseProfile.Get();
        var scanSession = _catalog.CreateScanSession(
            folder.CanonicalPath, folder.Id, generation, cancellationToken);
        // ExifTool is reused throughout this scan, not restarted per file.
        using var exifReader = new ImageExifReader();
        var enumerator = resume?.Enumerator ??
            (specificFile is null
                ? EnumerateIncludedFiles(
                    scanRoot, recursive, exclusions, CancellationToken.None)
                : new[] { specificFile }).GetEnumerator();
        if (resume is not null) resume.Enumerator = enumerator;
        var enumerationComplete = resume?.EnumerationComplete ?? false;
        try
        {
            while (!enumerationComplete)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string localPath;
                if (resume?.CurrentFile is string pendingFile)
                {
                    localPath = pendingFile;
                }
                else
                {
                    if (!enumerator.MoveNext())
                    {
                        enumerationComplete = true;
                        break;
                    }
                    localPath = enumerator.Current;
                    if (resume is not null) resume.CurrentFile = localPath;
                }

                var extension = Path.GetExtension(localPath);
                if (!SupportedExtensions.Contains(extension))
                {
                    if (resume is not null) resume.CurrentFile = null;
                    continue;
                }

                long? discoveredFolderId = null;
                string? discoveredFolderPath = null;

                try
                {
                    // Metadata edits and original-image decoding share one
                    // file-specific lock, not the global pack/index lock.
                    using (SourceFileLocks.Enter(localPath, cancellationToken))
                    {
                    observedPaths?.Add(localPath);
                    var file = new FileInfo(localPath);
                    // Read ALL network metadata before opening a SQLite write
                    // transaction. An offline SMB path must not hold the writer
                    // while a manual removal is waiting for the database.
                    var fileLength = file.Length;
                    var modifiedUtcTicks = file.LastWriteTimeUtc.Ticks;
                    var canonicalPath = ResolveCanonicalFullPath(folder, localPath);

                    var photoFolderLocalPath = Path.GetDirectoryName(localPath)
                        ?? scanRoot;
                    var photoFolderCanonicalPath = ResolveCanonicalFullPath(
                        folder,
                        photoFolderLocalPath);

                    var firstFileInThisScanFolder =
                        !photoFolderIds.TryGetValue(photoFolderCanonicalPath, out var photoFolderId);
                    if (firstFileInThisScanFolder)
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
                        // The resume cursor must never retain an uncommitted
                        // folder id. Cancellation during FlushPending rolls
                        // back the row; only publish the id after commit.
                        scanSession.FlushPending();
                        photoFolderIds.Add(photoFolderCanonicalPath, photoFolderId);
                    }

                    var existing = scanSession.GetState(canonicalPath);
                    if (existing is not null &&
                        existing.FileSize == fileLength &&
                        existing.ModifiedUtcTicks == modifiedUtcTicks &&
                        File.Exists(existing.ThumbnailPath) &&
                        (!RawImageSource.IsRawPath(localPath) ||
                         Path.GetFileName(existing.ThumbnailPath).StartsWith(
                             "raw2-", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (existing.PhotoFolderId != photoFolderId ||
                            !string.Equals(existing.LocalPath, localPath, StringComparison.OrdinalIgnoreCase))
                        {
                            scanSession.UpdateLocation(
                                canonicalPath,
                                folder.Id,
                                photoFolderId,
                                localPath);
                            scanSession.FlushPending();
                        }

                        // Enrich old catalogs without touching their thumbnails.
                        if (!existing.HasCurrentExif)
                        {
                            scanSession.FlushPending();
                            var exif = exifReader.Read(localPath, cancellationToken);
                            scanSession.SaveExif(canonicalPath, modifiedUtcTicks, fileLength, exif);
                            scanSession.FlushPending();
                            metadataUpdated++;
                        }

                        unchanged++;
                        if (firstFileInThisScanFolder)
                        {
                            discoveredFolderId = photoFolderId;
                            discoveredFolderPath = photoFolderLocalPath;
                        }
                    }
                    else
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        scanSession.FlushPending();
                        var renderPath = RawImageSource.Resolve(localPath,
                            cancellationToken);
                        var thumbnail = _thumbnailCache.CreateOrUpdate(
                            renderPath,
                            canonicalPath,
                            fileLength,
                            modifiedUtcTicks,
                            originalSourcePath: localPath,
                            cancellationToken: cancellationToken);

                        cancellationToken.ThrowIfCancellationRequested();

                        // Decode/encode outside the cache lock. Keep the
                        // append and its catalog commit together so pack
                        // compaction cannot delete an unreferenced new pack.
                        // Each image owns exactly one pack writer lifetime.
                        // A later cache compaction must never race a cached,
                        // still-open handle to a previous pack file.
                        using var packStore = new PackedThumbnailStore();
                        var smallTiers = profile.Tiers.Where(tier => tier <= 512).ToArray();
                        var largeTiers = profile.Tiers.Where(tier => tier > 512).ToArray();
                        var preparedSmall = smallTiers.Length > 0
                            ? packStore.PrepareTiers(
                                thumbnail.Path, new BrowseThumbnailProfile(smallTiers),
                                cancellationToken)
                            : Array.Empty<PackedThumbnailStore.PreparedThumbnailTier>();
                        var preparedLarge = largeTiers.Length > 0
                            ? packStore.PrepareTiers(
                                renderPath, new BrowseThumbnailProfile(largeTiers),
                                cancellationToken, originalSourcePath: localPath)
                            : Array.Empty<PackedThumbnailStore.PreparedThumbnailTier>();
                        var keywords = ImageTagRepository.ReadKeywords(localPath);
                        var exif = exifReader.Read(localPath, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();

                        lock (CatalogRemovalService.CacheLock)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var locations = new List<PackedThumbnailLocation>();
                            locations.AddRange(packStore.AppendPrepared(preparedSmall, cancellationToken));
                            locations.AddRange(packStore.AppendPrepared(preparedLarge, cancellationToken));
                            scanSession.Upsert(new CatalogImageData(
                                folder.Id, photoFolderId, localPath, canonicalPath,
                                file.Name, extension.ToLowerInvariant(), fileLength,
                                modifiedUtcTicks, thumbnail.Width, thumbnail.Height,
                                thumbnail.Path));
                            scanSession.SaveExif(canonicalPath, modifiedUtcTicks, fileLength, exif);
                            if (keywords is not null)
                                scanSession.SaveTags(canonicalPath, modifiedUtcTicks, keywords);
                            scanSession.SaveBrowseLocations(
                                canonicalPath, modifiedUtcTicks, locations, profile.Tiers.Count);
                            // The lock covers durable SQLite commit as well as
                            // append; a cancel after appending rolls back index
                            // writes and idle cleanup later reclaims orphan bytes.
                            scanSession.FlushPending();
                            packStore.Dispose();
                        }

                        updated++;
                        // The gallery observes catalog commits, not scanner
                        // progress, tree state or scan completion. Publication
                        // occurs only after the SQLite image+cache index commit.
                        if (publishCatalogChange)
                            CatalogChanges.PublishImageCommitted();
                        if (firstFileInThisScanFolder)
                        {
                            discoveredFolderId = photoFolderId;
                            discoveredFolderPath = photoFolderLocalPath;
                        }
                        if (existing is null)
                            added++;
                        else if (existing.FileSize != fileLength ||
                                 existing.ModifiedUtcTicks != modifiedUtcTicks)
                            changed++;
                        else
                            cacheRepaired++;
                    }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanSession.RollbackPending();
                    failed++;
                    if (firstFailure is null)
                    {
                        firstFailure = ex;
                        firstFailurePath = localPath;
                    }
                }

                // Advance only after the image's processing has completed or
                // its failure has been recorded. A scheduler yield while
                // waiting for a file lock must not double-count this file.
                imageFilesSeen++;
                if (resume is not null)
                {
                    resume.ImageFilesSeen = imageFilesSeen;
                    resume.Updated = updated;
                    resume.Unchanged = unchanged;
                    resume.Added = added;
                    resume.Changed = changed;
                    resume.CacheRepaired = cacheRepaired;
                    resume.MetadataUpdated = metadataUpdated;
                    resume.Failed = failed;
                    resume.FirstFailure = firstFailure;
                    resume.FirstFailurePath = firstFailurePath;
                    resume.CurrentFile = null;
                }

                // The checkpoint is authoritative before any subsequent
                // cancellation boundary, connection rollover or UI callback.
                if (catalogCommitClock.ElapsedMilliseconds >= 1000)
                {
                    scanSession.Dispose();
                    scanSession = _catalog.CreateScanSession(
                        folder.CanonicalPath, folder.Id, generation, cancellationToken);
                    catalogCommitClock.Restart();
                }

                // Show each newly discovered folder and regularly refresh
                // progress; presentation cannot own the scanner cursor.
                ReportProgress(localPath, force: discoveredFolderId is not null,
                    discoveredFolderId, discoveredFolderPath);
            }
        }
        catch (OperationCanceledException)
        {
            scanSession.RollbackPending();
            throw;
        }
        catch
        {
            resume?.Reset();
            throw;
        }
        finally
        {
            scanSession.Dispose();
            if (resume is null || enumerationComplete)
                enumerator.Dispose();
            if (enumerationComplete && resume is not null)
            {
                resume.Enumerator = null;
                resume.EnumerationComplete = true;
            }
        }

        ReportProgress(null, force: true);
        // Even the final UI progress callback can cancel a scan. A completed
        // enumeration is not permission to reconcile a cancelled operation.
        cancellationToken.ThrowIfCancellationRequested();

        // Only a completely enumerated directory may authorize pruning.
        // Individual decode/EXIF failures do NOT invalidate the filesystem
        // inventory: every supported file path is added to observedPaths
        // before its image/metadata processing begins. Such files are kept,
        // while demonstrably absent catalog entries can still be removed.
        // Enumeration, access and cancellation failures abort above, before
        // this reconciliation. A single broken JPEG must not prevent every
        // unrelated rename/deletion in that directory from converging.
        var removed = 0;
        if (observedPaths is not null)
        {
            // The source may disappear AFTER successful enumeration (VPN/SMB
            // drop, drive removal, renamed root) but BEFORE reconciliation.
            // Empty/missing sources never authorize pruning old catalog rows.
            if (!Directory.Exists(folder.LocalPath) || !Directory.Exists(scanRoot))
                throw new DirectoryNotFoundException(
                    $"Ordner nach Einlesen nicht mehr erreichbar: {scanRoot}");
            cancellationToken.ThrowIfCancellationRequested();

            var cleanup = new CatalogRemovalService(_catalog.Database);
            // Only directories that were actually enumerated can authorize
            // deleting missing catalog entries. A shallow scan must not prune
            // images from nested folders it never visited.
            removed = cleanup.RemoveMissing(folder.Id, scanRoot, observedPaths,
                recursive: recursive, cancellationToken: cancellationToken,
                sourceCanonicalPath: folder.CanonicalPath,
                expectedGeneration: generation, expectedScanEpoch: scanEpoch);
        }

        if (imageFilesSeen > 0 && updated == 0 && unchanged == 0 && failed == imageFilesSeen)
        {
            throw new InvalidOperationException(
                $"Keine der {imageFilesSeen:N0} gefundenen Bilddateien konnte eingelesen werden.\n\n" +
                $"Erste fehlerhafte Datei:\n{firstFailurePath}\n\n" +
                $"Ursache:\n{firstFailure?.Message}",
                firstFailure);
        }

        // Exactly the same complete scanner path clears the persistent
        // first-import request, whether invoked manually or by the monitor.
        // A partial/file scan or a failed enumeration never acknowledges it.
        if (specificFile is null && recursive && failed == 0 &&
            string.Equals(FolderExclusionRules.Normalize(scanRoot),
                FolderExclusionRules.Normalize(folder.LocalPath),
                StringComparison.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _folderRepository.CompleteInitialScan(folder.Id, generation, scanEpoch);
        }

        return new ScanResult(imageFilesSeen, updated, unchanged, failed, removed, metadataUpdated);

        void ReportProgress(string? currentFile, bool force = false,
            long? discoveredFolderId = null, string? discoveredFolderPath = null)
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
                cacheRepaired,
                discoveredFolderId,
                discoveredFolderPath));
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

    internal static string ResolveCanonicalFullPath(
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
