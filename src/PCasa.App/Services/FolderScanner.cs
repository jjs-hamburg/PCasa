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
    string? DiscoveredPhotoFolderPath = null,
    string? CurrentThumbnailPath = null,
    string? VerifiedDirectoryPath = null,
    // The action on the CURRENT file, not a summary of all scan counts.
    // null before the first source file; true while it is being indexed
    // (new/changed), false while its existing index is verified.
    bool? ReadingNew = null,
    // A phase notification precedes file work; only a post-commit callback
    // may advertise a thumbnail as ready for preview.
    bool PreviewCommitted = true,
    int? CatalogIndexed = null);

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
    // Number of files actually admitted to source processing, distinct from
    // directory entries compared using already supplied metadata.
    public int SourceProcessingCount { get; init; }
}

public sealed class FolderScanner
{
    /// <summary>
    /// Durable-for-this-session enumeration cursor. Scanner yields may restart
    /// the worker delegate, but never restart already traversed NAS folders.
    /// The source generation fences invalidate an unsafe stale checkpoint.
    /// </summary>
    public sealed class ScanResumeState : IDisposable
    {
        internal IEnumerator<string>? Enumerator;
        internal string? CurrentFile;
        internal CancellationToken DifferenceToken;
        internal Action<string>? DifferenceUnchangedHandler;
        internal Action<string, IReadOnlySet<string>>? DifferenceDirectoryHandler;
        internal int SourceProcessingCount;
        internal bool EnumerationComplete;
        internal long? SourceGeneration;
        internal long? ScanEpoch;
        internal readonly Dictionary<string, long> PhotoFolderIds =
            new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> ObservedPaths =
            new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> FailedDirectories =
            new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string>? CompletedInitialDirectories;
        internal Dictionary<string, string>? InitialFileCursors;
        internal HashSet<string>? RestartedPartialDirectories;
        internal Action<string>? DirectoryCompletionHandler;
        internal int ImageFilesSeen, Updated, Unchanged, Added, Changed,
            CacheRepaired, MetadataUpdated, Failed;
        internal readonly HashSet<string> VerifiedDirectories = new(StringComparer.OrdinalIgnoreCase);
        internal int Removed;
        internal Exception? FirstFailure;
        internal string? FirstFailurePath;

        public int CommittedFiles => ImageFilesSeen;
        public void Reset()
        {
            Enumerator?.Dispose();
            Enumerator = null;
            CurrentFile = null;
            DifferenceToken = default;
            DifferenceUnchangedHandler = null;
            DifferenceDirectoryHandler = null;
            SourceProcessingCount = 0;
            EnumerationComplete = false;
            SourceGeneration = null;
            ScanEpoch = null;
            PhotoFolderIds.Clear();
            ObservedPaths.Clear();
            FailedDirectories.Clear();
            VerifiedDirectories.Clear();
            Removed = 0;
            CompletedInitialDirectories = null;
            InitialFileCursors = null;
            RestartedPartialDirectories = null;
            DirectoryCompletionHandler = null;
            ImageFilesSeen = Updated = Unchanged = Added = Changed =
                CacheRepaired = MetadataUpdated = Failed = 0;
            FirstFailure = null;
            FirstFailurePath = null;
        }

        public void Dispose() => Reset();
    }

    private readonly ImageCatalogRepository _catalog;
    private readonly IFolderImportMedia _thumbnailCache;
    private readonly Func<IImportExifReader> _exifReaderFactory;
    private readonly FolderRepository _folderRepository;
    private readonly BrowseThumbnailProfileRepository _browseProfile;

    public FolderScanner(
        ImageCatalogRepository catalog,
        IFolderImportMedia thumbnailCache,
        FolderRepository? folderRepository = null,
        BrowseThumbnailProfileRepository? profileRepository = null,
        Func<IImportExifReader>? exifReaderFactory = null)
    {
        _browseProfile = profileRepository ?? new BrowseThumbnailProfileRepository(new LocalDatabase());
        _catalog = catalog;
        _thumbnailCache = thumbnailCache;
        _exifReaderFactory = exifReaderFactory ?? (() => new ImageExifReader());
        _folderRepository = folderRepository ?? new FolderRepository(new LocalDatabase());
    }

    public static bool IsSupportedImagePath(string path)
        => CatalogMediaFormats.IsSupported(path);

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
        ScanResumeState? resume = null,
        bool durableInitialImport = false,
        string? durableScanKey = null,
        bool preferFirstPass = false,
        bool catalogOwnedProgress = false,
        bool retryFailedFiles = false,
        bool prioritizeImportFrontier = false)
    {
        return ScanDirectoryLocked(
            folder, localDirectory, recursive, progress, cancellationToken,
            resume: resume, durableInitialImport: durableInitialImport,
            durableScanKey: durableScanKey, preferFirstPass: preferFirstPass,
            catalogOwnedProgress: catalogOwnedProgress,
            retryFailedFiles: retryFailedFiles, prioritizeImportFrontier: prioritizeImportFrontier);
    }

    public ScanResult ScanFile(
        MonitoredFolder folder,
        string localFile,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool publishCatalogChange = true,
        bool forceExifRefresh = false, bool forceContentRefresh = false)
    {
        if (!IsSupportedImagePath(localFile) || !File.Exists(localFile))
            return new ScanResult(0, 0, 0, 0);
        return ScanDirectoryLocked(
            folder, Path.GetDirectoryName(localFile)!, false,
            progress, cancellationToken, localFile,
            publishCatalogChange: publishCatalogChange,
            forceExifRefresh: forceExifRefresh, forceContentRefresh: forceContentRefresh);
    }

    private ScanResult ScanDirectoryLocked(
        MonitoredFolder folder,
        string localDirectory,
        bool recursive,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        string? specificFile = null,
        ScanResumeState? resume = null,
        bool publishCatalogChange = true,
        bool durableInitialImport = false,
        string? durableScanKey = null,
        bool forceExifRefresh = false,
        bool preferFirstPass = false,
        bool catalogOwnedProgress = false,
        bool retryFailedFiles = false, bool forceContentRefresh = false,
        bool prioritizeImportFrontier = false)
    {
        var scanRoot = CatalogPathPolicy.NormalizeSeparators(Path.GetFullPath(localDirectory));
        EnsureInsideMonitoredFolder(folder, scanRoot);

        if (!Directory.Exists(scanRoot))
        {
            throw new DirectoryNotFoundException(
                $"Der einzulesende Ordner ist nicht erreichbar: {scanRoot}");
        }

        var preparationClock = Stopwatch.StartNew();
        var progressClock = Stopwatch.StartNew();
        var catalogCountClock = Stopwatch.StartNew();
        int? catalogIndexed = progress is null ? null :
            _catalog.CountIndexedImages(folder.Id, scanRoot, recursive);
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
        // Validate current exclusions before allocating a durable scan receipt.
        // A stale watcher event must not leave an orphan inventory for a
        // directory excluded since the event was queued.
        var exclusions = _folderRepository.GetExcludedPaths(folder.Id);
        var inclusions = _folderRepository.GetIncludedPaths(folder.Id);
        if (FolderExclusionRules.IsExcluded(scanRoot, exclusions, inclusions) &&
            !FolderExclusionRules.MayContainIncludedDescendant(scanRoot, inclusions))
            return new ScanResult(0, 0, 0, 0);
        if (resume is not null) resume.SourceGeneration = generation;
        // Same scanner and traversal for every inventory. The durable key
        // belongs to one scan request; completed requests erase their receipt.
        var durableRefresh = catalogOwnedProgress && !preferFirstPass && specificFile is null && durableScanKey is not null;
        var regularCheckpoint = durableScanKey is not null
            ? _folderRepository.BeginOrResumeRegularScan(
                durableScanKey, folder.Id, scanRoot, folder.CanonicalPath,
                beginManualEpoch: false,
                loadLegacyProgress: !catalogOwnedProgress || durableRefresh,
                verifiedDirectoryReceipts: durableRefresh)
            : null;
        if ((!catalogOwnedProgress || durableRefresh) && regularCheckpoint is not null &&
            resume is not null && resume.Enumerator is null &&
            resume.ImageFilesSeen == 0)
            resume.ImageFilesSeen = regularCheckpoint.Checked;
        var scanEpoch = resume?.ScanEpoch ?? regularCheckpoint?.Epoch ??
            _folderRepository.GetScanEpoch(folder.CanonicalPath);
        if (resume is not null) resume.ScanEpoch = scanEpoch;

        var imageFilesSeen = resume?.ImageFilesSeen ??
            (catalogOwnedProgress && !durableRefresh ? 0 : regularCheckpoint?.Checked ?? 0);
        var updated = resume?.Updated ?? 0;
        var unchanged = resume?.Unchanged ?? 0;
        var added = resume?.Added ?? 0;
        var changed = resume?.Changed ?? 0;
        var cacheRepaired = resume?.CacheRepaired ?? 0;
        var metadataUpdated = resume?.MetadataUpdated ?? 0;
        var failed = resume?.Failed ?? 0;
        bool? readingNew = null;
        Exception? firstFailure = resume?.FirstFailure;
        string? firstFailurePath = resume?.FirstFailurePath;
        var photoFolderIds = resume?.PhotoFolderIds ??
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var observedPaths = specificFile is null
            ? resume?.ObservedPaths ??
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;

        // First-import and other scan receipts drive the same enumerator.
        // Re-enumerate every current directory entry; previous filename
        // frontiers cannot establish that a file is unchanged or still exists.
        // Existing valid rows skip expensive import work by file identity.
        // Catalog-owned progress and directory-owned import frontiers are
        // independent of a job's checked counter. P4 reconciliation never
        // uses the import shortcut.
        var catalogImport = catalogOwnedProgress && preferFirstPass && specificFile is null;
        var detectDifferences = catalogOwnedProgress && !catalogImport && specificFile is null;
        var persistentScan = catalogImport || durableRefresh || (!catalogOwnedProgress &&
            (durableInitialImport || durableScanKey is not null));
        // Only legacy scopes need a root-wide directory snapshot.
        var savedFrontier = catalogImport && resume?.Enumerator is null
            ? _folderRepository.GetImportFrontier(folder.Id, generation, scanRoot) : null;
        var directoryState = catalogImport && savedFrontier is null &&
            resume?.CompletedInitialDirectories is null
            ? _folderRepository.GetDirectoryFirstPass(folder.Id, generation, scanRoot)
            : null;
        var completedDirectories = catalogImport
            ? resume?.CompletedInitialDirectories ?? directoryState?.Completed ??
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : !persistentScan ? null : durableInitialImport
            ? resume?.CompletedInitialDirectories ??
                _folderRepository.GetCompletedInitialScanDirectories(
                    folder.Id, generation, scanEpoch)
            : durableScanKey is not null
                ? resume?.CompletedInitialDirectories ?? regularCheckpoint!.Completed
                : null;
        if (persistentScan && resume is not null)
            resume.CompletedInitialDirectories = completedDirectories;
        var fileCursors = catalogImport
            ? resume?.InitialFileCursors ?? directoryState?.Cursors ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : !persistentScan ? null : durableInitialImport
            ? resume?.InitialFileCursors ??
                _folderRepository.GetInitialScanFileCursors(folder.Id, generation, scanEpoch)
            : durableScanKey is not null
                ? resume?.InitialFileCursors ?? regularCheckpoint!.Cursors
                : null;
        if (persistentScan && resume is not null)
            resume.InitialFileCursors = fileCursors;

        // A manual first-pass import never starts again at filename zero just
        // because a new UI request has a different scheduler package key.
        // Upgrade the old package's existing receipts before they are deleted
        // on completion. Background verification deliberately ignores this
        // directory frontier, so new/modified/deleted photos are checked later.
        if (!catalogImport && persistentScan && preferFirstPass && durableScanKey is not null)
        {
            _folderRepository.ImportFirstPassFromRegularScans(
                folder.Id, generation);
            var firstPass = _folderRepository.GetDirectoryFirstPass(
                folder.Id, generation, scanRoot);
            completedDirectories!.UnionWith(firstPass.Completed);
            foreach (var (directory, cursor) in firstPass.Cursors)
                if (!completedDirectories.Contains(directory) &&
                    (!fileCursors!.TryGetValue(directory, out var previous) ||
                     StringComparer.OrdinalIgnoreCase.Compare(cursor, previous) > 0))
                    fileCursors[directory] = cursor;
        }

        // A P0 first-pass scan consults the existing LOCAL catalog once.
        // A completed directory is still enumerated for NEW paths only;
        // already-indexed originals never go through metadata/decode again.
        // P4 full verification deliberately does not use this shortcut.
        HashSet<string>? indexedFirstPassPaths = null;
        if (!catalogImport && persistentScan && preferFirstPass && specificFile is null)
        {
            indexedFirstPassPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var snapshotDb = _catalog.Database.OpenConnection();
            using var known = snapshotDb.CreateCommand();
            known.CommandText = "SELECT local_path FROM images WHERE folder_id=$root;";
            known.Parameters.AddWithValue("$root", folder.Id);
            using var knownReader = known.ExecuteReader();
            while (knownReader.Read())
                indexedFirstPassPaths.Add(knownReader.GetString(0));
        }

        if (persistentScan && specificFile is null)
        {
            // A restart log must identify the actual persisted checkpoint,
            // not just the progress window's transient "checked" counter.
            var last = fileCursors!.OrderBy(pair => pair.Key,
                StringComparer.OrdinalIgnoreCase).LastOrDefault();
            PCasaDiagnostics.WriteMessage("Scan.CursorLoaded",
                $"Mode={(durableInitialImport ? "InitialImport" : "Regular")}; " +
                $"Root={scanRoot}; Job={durableScanKey ?? "initial"}; " +
                $"Epoch={scanEpoch}; Checked={imageFilesSeen}; " +
                $"CompletedDirectories={completedDirectories!.Count}; " +
                $"FileCursors={fileCursors.Count}; " +
                $"LastDirectory={last.Key ?? "-"}; LastFile={last.Value ?? "-"}");
        }
        // A partial directory seen-set after restart must never prune images
        // that were committed and skipped before this process started.
        var restartedPartialDirectories = persistentScan && !durableRefresh
            ? resume?.RestartedPartialDirectories ??
                new HashSet<string>(fileCursors!.Keys, StringComparer.OrdinalIgnoreCase)
            : null;
        if (persistentScan && resume is not null)
            resume.RestartedPartialDirectories = restartedPartialDirectories;
        var failedDirectories = resume?.FailedDirectories ??
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removedByCompletedDirectories = resume?.Removed ?? 0;
        var verifiedDirectories = resume?.VerifiedDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkedAtLastDirectory = regularCheckpoint?.Checked ?? 0;
        void DirectoryCompleted(string directory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (durableRefresh)
            {
                verifiedDirectories.Add(FolderExclusionRules.Normalize(directory));
                if (completedDirectories!.Contains(directory)) return;
            }
            if (!Directory.Exists(folder.LocalPath) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException(
                    $"Verzeichnis vor Checkpoint nicht erreichbar: {directory}");
            var seen = detectDifferences ? null : new HashSet<string>(
                observedPaths!.Where(path => string.Equals(
                    Path.GetDirectoryName(path), directory,
                    StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
            // A restarted partial directory did not revisit earlier files.
            // Its observed set is incomplete and must NEVER authorize deletes.
            if (!detectDifferences && !catalogImport && restartedPartialDirectories?.Contains(directory) != true &&
                !failedDirectories.Contains(directory))
            {
                // A file read/decode failed: its path may not have reached
                // observedPaths, so pruning this directory is unsafe. The
                // enumeration itself still completes and its cursor commits.
                var cleanup = new CatalogRemovalService(_catalog.Database);
                removedByCompletedDirectories += cleanup.RemoveMissing(
                    folder.Id, directory, seen!,
                    recursive: false, cancellationToken: cancellationToken,
                    sourceCanonicalPath: folder.CanonicalPath,
                    expectedGeneration: generation, expectedScanEpoch: scanEpoch);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var committed = catalogImport
                ? _folderRepository.SaveDirectoryFirstPass(folder.Id, generation, directory, null, completed: true)
                : durableInitialImport
                ? _folderRepository.CompleteInitialScanDirectory(
                    folder.Id, directory, generation, scanEpoch)
                : _folderRepository.CompleteRegularScanDirectory(
                    durableScanKey!, directory, imageFilesSeen, generation, scanEpoch);
            if (!committed)
                throw new OperationCanceledException("Directory checkpoint superseded.");
            if (!catalogImport && (durableInitialImport || preferFirstPass) &&
                !_folderRepository.SaveDirectoryFirstPass(
                    folder.Id, generation, directory, null, completed: true))
                throw new OperationCanceledException(
                    "First-pass directory checkpoint superseded.");
            completedDirectories!.Add(directory);
            checkedAtLastDirectory = imageFilesSeen;
            if (resume is not null) resume.Removed = removedByCompletedDirectories;
            // A completed directory is now durable; do not keep a large
            // duplicate inventory in memory across the rest of the NAS scan.
            if (!detectDifferences)
                observedPaths!.RemoveWhere(path => string.Equals(
                    Path.GetDirectoryName(path), directory,
                    StringComparison.OrdinalIgnoreCase));
            // Publish a green verification ONLY after the scanner's durable
            // directory checkpoint succeeded. This is not a parallel status
            // monitor and never probes the NAS again.
            ReportProgress(null, force: true, verifiedDirectoryPath: directory);
        }

        // A scheduler yield re-enters ScanDirectoryLocked with a new cancellation
        // token. The persistent iterator must dispatch into THIS invocation,
        // never the first invocation's cancelled token or stale cleanup context.
        if (resume is not null)
            resume.DirectoryCompletionHandler = persistentScan
                ? DirectoryCompleted : null;
        var profile = _browseProfile.Get();
        var scanSession = _catalog.CreateScanSession(
            folder.CanonicalPath, folder.Id, generation, cancellationToken);
        // ExifTool is reused throughout this scan, not restarted per file.
        using var exifReader = _exifReaderFactory();
        // A first-import frontier is a preferred starting scope, never a
        // filter on an explicit refresh. Revisit the rest of the selected
        // tree after the outstanding directories, without visiting any twice.
        string[]? preferredDirectories = null;
        if (prioritizeImportFrontier && !catalogImport && specificFile is null &&
            resume?.Enumerator is null && _folderRepository.HasIncompleteImport(folder.Id))
        {
            preferredDirectories = (_folderRepository.GetImportFrontier(folder.Id, generation, scanRoot)
                ?? _folderRepository.GetImportFrontier(folder.Id, generation, folder.LocalPath)
                ?? _folderRepository.GetDirectoryFirstPass(folder.Id, generation, scanRoot).Cursors.Keys.ToArray())
                .Where(path => FolderExclusionRules.IsAtOrBelow(path, scanRoot)).ToArray();
        }
        if (durableRefresh && resume?.Enumerator is null && regularCheckpoint!.Cursors.Count > 0)
            preferredDirectories = regularCheckpoint.Cursors.Keys.Concat(preferredDirectories ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Unchanged counts are detector statistics, never scanner work.
        // No per-file UI callbacks, source locks, or seen-path SQL inserts.
        void DifferenceUnchanged(string directory)
        {
            imageFilesSeen++;
            unchanged++;
            if (resume is not null)
            {
                resume.ImageFilesSeen = imageFilesSeen;
                resume.Unchanged = unchanged;
            }
        }
        void DifferenceDirectoryCompleted(string directory, IReadOnlySet<string> missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            verifiedDirectories.Add(FolderExclusionRules.Normalize(directory));
            if (missing.Count > 0 && !failedDirectories.Contains(directory))
            {
                if (!Directory.Exists(folder.LocalPath) || !Directory.Exists(directory))
                    throw new DirectoryNotFoundException(directory);
                removedByCompletedDirectories += new CatalogRemovalService(_catalog.Database)
                    .RemoveConfirmedMissing(folder.Id, directory, missing, cancellationToken,
                        folder.CanonicalPath, generation, scanEpoch);
                if (resume is not null) resume.Removed = removedByCompletedDirectories;
            }
            if (persistentScan) DirectoryCompleted(directory);
        }
        if (resume is not null)
        {
            resume.DifferenceToken = cancellationToken;
            resume.DifferenceUnchangedHandler = DifferenceUnchanged;
            resume.DifferenceDirectoryHandler = DifferenceDirectoryCompleted;
        }
        var sourceProcessingCount = resume?.SourceProcessingCount ?? 0;
        var enumerator = resume?.Enumerator ??
            (catalogImport
                ? EnumerateFirstImportFiles(folder, generation, scanRoot, recursive,
                    exclusions, inclusions, completedDirectories!, fileCursors!,
                    resume is null ? DirectoryCompleted :
                        directory => resume.DirectoryCompletionHandler?.Invoke(directory))
                : detectDifferences
                ? CatalogInventoryCheck.EnumerateDifferences(_catalog.Database, folder, scanRoot,
                    recursive, exclusions, inclusions,
                    () => resume?.DifferenceToken ?? cancellationToken,
                    directory =>
                    {
                        if (resume is not null) resume.DifferenceUnchangedHandler!(directory);
                        else DifferenceUnchanged(directory);
                    },
                    (directory, missing) =>
                    {
                        if (resume is not null) resume.DifferenceDirectoryHandler!(directory, missing);
                        else DifferenceDirectoryCompleted(directory, missing);
                    },
                    completedDirectories, preferredDirectories).Select(difference => difference.Path)
                : specificFile is null
                ? EnumerateIncludedFiles(
                    scanRoot, recursive, exclusions, inclusions, CancellationToken.None,
                    completedDirectories,
                    durableRefresh ? null : fileCursors,
                    indexedFirstPassPaths,
                    catalogImport,
                    persistentScan
                        ? resume is null
                            ? DirectoryCompleted
                            : directory => resume.DirectoryCompletionHandler?.Invoke(directory)
                        : null,
                    preferredDirectories, reportCompletedDirectories: durableRefresh)
                : new[] { specificFile }).GetEnumerator();
        if (resume is not null) resume.Enumerator = enumerator;
        var enumerationComplete = resume?.EnumerationComplete ?? false;
        var firstFileReported = false;
        if (specificFile is null)
            PCasaDiagnostics.WriteMessage("Scan.Preparation",
                $"Root={scanRoot}; FirstImport={catalogImport}; BeforeEnumerationMs={preparationClock.ElapsedMilliseconds}");
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
                    try
                    {
                        if (!enumerator.MoveNext())
                        {
                            enumerationComplete = true;
                            break;
                        }
                    }
                    catch (OperationCanceledException) when ((catalogImport || detectDifferences) && resume is not null)
                    {
                        if (detectDifferences) resume.Reset();
                        else
                        {
                            enumerator.Dispose();
                            resume.Enumerator = null;
                        }
                        throw;
                    }
                    localPath = enumerator.Current;
                    // A traversal checkpoint returns to the current scheduler
                    // token even when no new image has been found.
                    if (localPath.Length == 0) continue;
                    if (resume is not null) resume.CurrentFile = localPath;
                }

                var extension = Path.GetExtension(localPath);
                if (!CatalogMediaFormats.IsSupported(localPath))
                {
                    if (resume is not null) resume.CurrentFile = null;
                    continue;
                }

                if (!firstFileReported && specificFile is null)
                {
                    firstFileReported = true;
                    PCasaDiagnostics.WriteMessage("Scan.FirstFile",
                        $"Root={scanRoot}; File={localPath}; ElapsedMs={preparationClock.ElapsedMilliseconds}");
                }
                long? discoveredFolderId = null;
                string? discoveredFolderPath = null;
                // Only local cache paths belong in the progress preview.
                // Never ask the progress dialog to decode an SMB original.
                string? currentThumbnailPath = null;
                CatalogImageData? decodeIdentity = null;
                bool firstFolderFile = false;

                sourceProcessingCount++;
                if (resume is not null) resume.SourceProcessingCount = sourceProcessingCount;
                var imageClock = detectDifferences ? Stopwatch.StartNew() : null;
                long sourcePreparationMs = 0, hashMs = 0, thumbnailMs = 0,
                    tierMs = 0, metadataMs = 0, commitMs = 0;
                var imageStage = 0L;
                try
                {
                    // Metadata edits and original-image decoding share one
                    // file-specific lock, not the global pack/index lock.
                    using (SourceFileLocks.Enter(localPath, cancellationToken))
                    {
                    if (!detectDifferences) observedPaths?.Add(localPath);
                    var file = new FileInfo(localPath);
                    // Read ALL network metadata before opening a SQLite write
                    // transaction. An offline SMB path must not hold the writer
                    // while a manual removal is waiting for the database.
                    var fileLength = file.Length;
                    var modifiedUtcTicks = file.LastWriteTimeUtc.Ticks;
                    var createdUtcTicks = file.CreationTimeUtc.Ticks;
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
                    var unchangedError = !retryFailedFiles && !forceExifRefresh && !forceContentRefresh &&
                        existing?.ImportError is not null &&
                        existing.FileSize == fileLength &&
                        existing.ModifiedUtcTicks == modifiedUtcTicks &&
                        (folder.IsNetwork || existing.CreatedUtcTicks == createdUtcTicks);
                    firstFolderFile = firstFileInThisScanFolder;
                    var currentIndex = !forceContentRefresh && existing is not null &&
                        existing.ImportError is null &&
                        existing.FileSize == fileLength &&
                        existing.ModifiedUtcTicks == modifiedUtcTicks &&
                        (folder.IsNetwork || existing.CreatedUtcTicks is null or <= 0 ||
                         existing.CreatedUtcTicks == createdUtcTicks) &&
                        File.Exists(existing.ThumbnailPath) &&
                        (!_thumbnailCache.IsRawPath(localPath) ||
                         Path.GetFileName(existing.ThumbnailPath).StartsWith(
                             "raw2-", StringComparison.OrdinalIgnoreCase));
                    // Publish the actual pending operation BEFORE slow EXIF
                    // or thumbnail work, not after it has finished. Emit the
                    // extra UI callback only on a phase transition.
                    var nextReadingNew = !currentIndex && !unchangedError;
                    if (readingNew != nextReadingNew)
                    {
                        readingNew = nextReadingNew;
                        ReportProgress(localPath, force: true,
                            previewCommitted: false);
                    }
                    if (unchangedError)
                    {
                        // Known permanent error with an identical stamp:
                        // neither decode the original nor queue another retry.
                        currentThumbnailPath = existing!.ThumbnailPath;
                        unchanged++;
                    }
                    else if (currentIndex)
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

                        // Old local catalogs may have no source creation
                        // time. Backfill on the same ordinary scan, without
                        // re-reading EXIF or replacing the thumbnail pack.
                        if (createdUtcTicks > 0 &&
                            existing.CreatedUtcTicks != createdUtcTicks)
                        {
                            scanSession.UpdateCreatedUtc(canonicalPath, createdUtcTicks);
                            scanSession.FlushPending();
                        }
                        // Enrich old catalogs without touching their thumbnails.
                        if (forceExifRefresh || !existing.HasCurrentExif)
                        {
                            scanSession.FlushPending();
                            var exif = exifReader.Read(localPath, cancellationToken);
                            scanSession.SaveExif(canonicalPath, modifiedUtcTicks, fileLength, exif);
                            scanSession.FlushPending();
                            metadataUpdated++;
                        }

                        currentThumbnailPath = existing.ThumbnailPath;
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
                        decodeIdentity = new CatalogImageData(folder.Id, photoFolderId,
                            localPath, canonicalPath, file.Name, extension.ToLowerInvariant(),
                            fileLength, modifiedUtcTicks, 256, 256, "", createdUtcTicks);
                        // Fingerprint only actual imports/rebuilds, never unchanged inventory.
                        // Hold a read share until the pixels and metadata are committed.
                        sourcePreparationMs = imageClock?.ElapsedMilliseconds ?? 0;
                        imageStage = sourcePreparationMs;
                        using var sourceBytes = VideoFormats.IsVideo(localPath) ? null :
                            new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                128 * 1024, FileOptions.SequentialScan);
                        // Stage only network still images with a self-contained
                        // JPEG payload. Keep RAW/PSD/video decoder semantics unchanged.
                        // The source handle stays open with FileShare.Read through commit.
                        using var staged = folder.IsNetwork && sourceBytes is not null &&
                            (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                            ? ScanSourceSnapshot.Create(sourceBytes, localPath, fileLength, cancellationToken)
                            : null;
                        var mediaPath = staged?.Path ?? localPath;
                        byte[]? fingerprint = staged?.Fingerprint ?? (sourceBytes is null ? null :
                            System.Security.Cryptography.SHA256.HashDataAsync(sourceBytes, cancellationToken)
                                .AsTask().GetAwaiter().GetResult());
                        hashMs = (imageClock?.ElapsedMilliseconds ?? 0) - imageStage;
                        imageStage += hashMs;
                        var renderPath = _thumbnailCache.ResolveSource(mediaPath, cancellationToken);
                        var thumbnail = _thumbnailCache.CreateOrUpdate(
                            renderPath,
                            fingerprint is null ? canonicalPath :
                                canonicalPath + "|sha256:" + Convert.ToHexString(fingerprint),
                            fileLength,
                            modifiedUtcTicks,
                            originalSourcePath: mediaPath,
                            cancellationToken: cancellationToken);

                        cancellationToken.ThrowIfCancellationRequested();

                        thumbnailMs = (imageClock?.ElapsedMilliseconds ?? 0) - imageStage;
                        imageStage += thumbnailMs;
                        // Decode/encode outside the cache lock. Keep the
                        // append and its catalog commit together so pack
                        // compaction cannot delete an unreferenced new pack.
                        // Each image owns exactly one pack writer lifetime.
                        // A later cache compaction must never race a cached,
                        // still-open handle to a previous pack file.
                        using var packStore = _thumbnailCache.CreatePackStore();
                        var smallTiers = profile.Tiers.Where(tier => tier <= 512).ToArray();
                        var largeTiers = profile.Tiers.Where(tier => tier > 512).ToArray();
                        var preparedSmall = smallTiers.Length > 0
                            ? packStore.PrepareTiers(
                                thumbnail.Path, new BrowseThumbnailProfile(smallTiers),
                                cancellationToken)
                            : Array.Empty<ImportThumbnailTier>();
                        var preparedLarge = largeTiers.Length > 0
                            ? packStore.PrepareTiers(
                                renderPath, new BrowseThumbnailProfile(largeTiers),
                                cancellationToken, originalSourcePath: mediaPath)
                            : Array.Empty<ImportThumbnailTier>();
                        // Only source decode failures qualify as permanent
                        // import errors; do not mislabel database/write faults.
                        tierMs = (imageClock?.ElapsedMilliseconds ?? 0) - imageStage;
                        imageStage += tierMs;
                        decodeIdentity = null;
                        var keywords = _thumbnailCache.ReadKeywords(mediaPath);
                        var exif = exifReader.Read(mediaPath, cancellationToken);
                        if (staged is not null)
                            exif = ImageExifReader.RebindSnapshotSource(exif, mediaPath, localPath);
                        cancellationToken.ThrowIfCancellationRequested();

                        metadataMs = (imageClock?.ElapsedMilliseconds ?? 0) - imageStage;
                        imageStage += metadataMs;
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
                                thumbnail.Path, createdUtcTicks), fingerprint);
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

                        commitMs = (imageClock?.ElapsedMilliseconds ?? 0) - imageStage;
                        currentThumbnailPath = thumbnail.Path;
                        updated++;
                        if (catalogIndexed is int confirmed && (existing is null || existing.ImportError is not null))
                            catalogIndexed = confirmed + 1;
                        // The gallery observes catalog commits, not scanner
                        // progress, tree state or scan completion. Publication
                        // occurs only after the SQLite image+cache index commit.
                        if (publishCatalogChange)
                        {
                            CatalogChanges.PublishImageIndexed(canonicalPath, photoFolderId);
                            CatalogChanges.PublishImageCommitted();
                        }
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
                    // Database/write failures may compromise the transaction.
                    // Do not treat them as a harmless corrupt JPEG and advance
                    // the durable cursor over uncommitted catalog state.
                    if (ex is Microsoft.Data.Sqlite.SqliteException)
                        throw;
                    scanSession.RollbackPending();
                    failed++;
                    if (decodeIdentity is { } identity && _thumbnailCache.IsDecodeFailure(ex))
                    {
                        using var sourceLock = SourceFileLocks.Enter(localPath, cancellationToken);
                        // Verify access and the same file identity again. A
                        // disappearing/locked NAS source is not a corrupt file.
                        using (var accessible = File.Open(localPath, FileMode.Open,
                            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            var now = new FileInfo(localPath);
                            if (accessible.Length != identity.FileSize ||
                                now.LastWriteTimeUtc.Ticks != identity.ModifiedUtcTicks ||
                                (!folder.IsNetwork && now.CreationTimeUtc.Ticks != identity.CreatedUtcTicks))
                                throw new IOException("Source changed during import: " + localPath);
                            _ = accessible.ReadByte();
                        }
                        var placeholder = _thumbnailCache.CreateImportErrorPlaceholder(
                            identity.CanonicalPath, identity.FileSize,
                            identity.ModifiedUtcTicks, cancellationToken);
                        using var store = _thumbnailCache.CreatePackStore();
                        var prepared = store.PrepareTiers(placeholder.Path, profile,
                            cancellationToken);
                        lock (CatalogRemovalService.CacheLock)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var locations = store.AppendPrepared(prepared, cancellationToken);
                            scanSession.Upsert(identity with
                            {
                                ThumbnailPath = placeholder.Path,
                                ImportError = ex.Message
                            });
                            scanSession.SaveBrowseLocations(identity.CanonicalPath,
                                identity.ModifiedUtcTicks, locations, profile.Tiers.Count);
                            scanSession.FlushPending();
                        }
                        currentThumbnailPath = placeholder.Path;
                        updated++;
                        if (firstFolderFile)
                        {
                            discoveredFolderId = identity.PhotoFolderId;
                            discoveredFolderPath = Path.GetDirectoryName(localPath);
                        }
                        if (publishCatalogChange)
                        {
                            CatalogChanges.PublishImageIndexed(identity.CanonicalPath,
                                identity.PhotoFolderId);
                            CatalogChanges.PublishImageCommitted();
                        }
                    }
                    else
                    {
                        // Transient source errors cannot acknowledge a
                        // complete directory or permanently suppress retry.
                        if (ex is IOException or UnauthorizedAccessException)
                            throw;
                        failedDirectories.Add(Path.GetDirectoryName(localPath) ?? scanRoot);
                    }
                    PCasaDiagnostics.WriteMessage("Scan.FileSkipped",
                        $"File={localPath}; {ex.GetType().Name}: {ex.Message}");
                    if (firstFailure is null)
                    {
                        firstFailure = ex;
                        firstFailurePath = localPath;
                    }
                }

                if (imageClock is not null)
                    PCasaDiagnostics.WriteMessage("Scan.ImageTiming",
                        $"File={localPath}; PreparationMs={sourcePreparationMs}; HashMs={hashMs}; " +
                        $"ThumbnailMs={thumbnailMs}; TiersMs={tierMs}; MetadataMs={metadataMs}; " +
                        $"CommitMs={commitMs}; TotalMs={imageClock.ElapsedMilliseconds}");

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

                // File errors are intentionally SKIPPED for this inventory.
                // Advance the durable checkpoint after the file has either
                // committed or had its failure recorded, so a single bad
                // image cannot rewind the next launch by thousands of files.
                // Database errors still abort before reaching this point.
                if (durableRefresh)
                {
                    // Directory receipts prove completeness; an unsorted
                    // filename is only the preferred restart directory.
                    var directory = Path.GetDirectoryName(localPath) ?? scanRoot;
                    if (!fileCursors!.ContainsKey(directory))
                    {
                        if (!_folderRepository.SaveRegularScanFile(durableScanKey!,
                            directory, Path.GetFileName(localPath), checkedAtLastDirectory,
                            generation, scanEpoch))
                            throw new OperationCanceledException("Scan receipt superseded.");
                        fileCursors[directory] = Path.GetFileName(localPath);
                    }
                }
                else if (persistentScan)
                {
                    var directory = Path.GetDirectoryName(localPath) ?? scanRoot;
                    var fileName = Path.GetFileName(localPath);
                    if (fileCursors is not null &&
                        (!fileCursors.TryGetValue(directory, out var previous) ||
                         StringComparer.OrdinalIgnoreCase.Compare(fileName, previous) > 0))
                    {
                        var committed = catalogImport
                            ? _folderRepository.SaveDirectoryFirstPass(folder.Id, generation, directory, fileName, completed: false)
                            : durableInitialImport
                            ? _folderRepository.SaveInitialScanFileCursor(
                                folder.Id, directory, fileName, generation, scanEpoch)
                            : _folderRepository.SaveRegularScanFile(
                                durableScanKey!, directory, fileName,
                                imageFilesSeen, generation, scanEpoch);
                        if (!committed)
                            throw new OperationCanceledException(
                                "Scan source or checkpoint superseded.");
                        if (!catalogImport && (durableInitialImport || preferFirstPass) &&
                            !_folderRepository.SaveDirectoryFirstPass(
                                folder.Id, generation, directory, fileName,
                                completed: false))
                            throw new OperationCanceledException(
                                "First-pass file checkpoint superseded.");
                        fileCursors[directory] = fileName;
                    }
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
                    discoveredFolderId, discoveredFolderPath, currentThumbnailPath);
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

        var completionClock = Stopwatch.StartNew();
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
        var removed = removedByCompletedDirectories;
        if (observedPaths is not null && !persistentScan && !detectDifferences)
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

        if (detectDifferences && recursive)
        {
            // Each enumerated directory has already reconciled its files.
            // Confirm only missing subtrees, never prune a skipped directory.
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var db = _catalog.Database.OpenConnection())
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT local_path FROM photo_folders WHERE monitored_folder_id=$id;";
                cmd.Parameters.AddWithValue("$id", folder.Id);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = FolderExclusionRules.Normalize(reader.GetString(0));
                    if (!FolderExclusionRules.IsAtOrBelow(candidate, scanRoot) ||
                        verifiedDirectories.Contains(candidate) ||
                        FolderExclusionRules.IsExcluded(candidate, exclusions, inclusions))
                        continue;
                    var parent = Path.GetDirectoryName(candidate);
                    while (parent is not null && FolderExclusionRules.IsAtOrBelow(parent, scanRoot) &&
                        !verifiedDirectories.Contains(FolderExclusionRules.Normalize(parent)))
                    {
                        candidate = parent;
                        parent = Path.GetDirectoryName(candidate);
                    }
                    if (parent is not null && verifiedDirectories.Contains(FolderExclusionRules.Normalize(parent)))
                        missing.Add(candidate);
                }
            }
            var cleanup = new CatalogRemovalService(_catalog.Database);
            foreach (var path in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = Path.GetDirectoryName(path);
                if (parent is null || !Directory.Exists(parent)) continue;
                bool absent = false;
                try { _ = File.GetAttributes(path); }
                catch (FileNotFoundException) { absent = true; }
                catch (DirectoryNotFoundException) { absent = Directory.Exists(parent); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (absent)
                    removed += cleanup.RemoveMissing(folder.Id, path,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        cancellationToken: cancellationToken, sourceCanonicalPath: folder.CanonicalPath,
                        expectedGeneration: generation, expectedScanEpoch: scanEpoch);
            }
        }

        // A completely unreadable set of picture files is not a reason
        // to replay the inventory. Each bad file was logged and skipped;
        // missing-source and directory-enumeration failures still throw.


        // Exactly the same complete scanner path clears the persistent
        // first-import request, whether invoked manually or by the monitor.
        // A partial/file scan or a failed enumeration never acknowledges it.
        if (specificFile is null && recursive &&
            string.Equals(FolderExclusionRules.Normalize(scanRoot),
                FolderExclusionRules.Normalize(folder.LocalPath),
                StringComparison.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _folderRepository.CompleteInitialScan(folder.Id, generation, scanEpoch);
        }

        // A fully enumerated scan is complete even if a picture was skipped.
        // Only an interrupted/unavailable source retains a restart cursor.
        if (durableScanKey is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _folderRepository.FinishRegularScan(durableScanKey, generation, scanEpoch);
        }
        if (detectDifferences)
            PCasaDiagnostics.WriteMessage("Scan.CompletionTiming",
                $"Root={scanRoot}; FinalizeMs={completionClock.ElapsedMilliseconds}; " +
                $"ScannerTotalMs={preparationClock.ElapsedMilliseconds}");
        PCasaDiagnostics.WriteMessage("Scan.DifferenceSummary",
            $"Root={scanRoot}; Checked={imageFilesSeen}; SourceProcessing={sourceProcessingCount}; " +
            $"Unchanged={unchanged}; Added={added}; Changed={changed}; Removed={removed}");
        return new ScanResult(imageFilesSeen, updated, unchanged, failed, removed, metadataUpdated)
        {
            SourceProcessingCount = sourceProcessingCount
        };

        void ReportProgress(string? currentFile, bool force = false,
            long? discoveredFolderId = null, string? discoveredFolderPath = null,
            string? localPreviewPath = null,
            string? verifiedDirectoryPath = null,
            bool previewCommitted = true)
        {
            if (!force && progressClock.ElapsedMilliseconds < 250)
            {
                return;
            }

            progressClock.Restart();
            if (catalogImport && progress is not null && (catalogCountClock.ElapsedMilliseconds >= 1000 ||
                (force && currentFile is null && verifiedDirectoryPath is null)))
            {
                catalogIndexed = _catalog.CountIndexedImages(folder.Id, scanRoot, recursive);
                catalogCountClock.Restart();
            }
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
                discoveredFolderPath,
                localPreviewPath,
                verifiedDirectoryPath,
                readingNew,
                previewCommitted, catalogImport ? catalogIndexed : null));
        }
    }

    private IEnumerable<string> EnumerateFirstImportFiles(
        MonitoredFolder folder, long generation, string scanRoot, bool recursive,
        IReadOnlyCollection<string> exclusions, IReadOnlyCollection<string> inclusions,
        IReadOnlySet<string> completed, IReadOnlyDictionary<string,string> cursors,
        Action<string> onDirectoryComplete)
    {
        var saved = recursive ? _folderRepository.GetImportFrontier(
            folder.Id, generation, scanRoot) : null;
        // Legacy upgrade: start in a known partial directory, then discover
        // missing siblings from the old state. Future jobs use saved traversal.
        var pending = new LinkedList<string>((IEnumerable<string>?)saved ??
            cursors.Keys.Where(path => !string.Equals(path, scanRoot,
                StringComparison.OrdinalIgnoreCase)).Append(scanRoot));
        void Persist()
        {
            if (recursive && !_folderRepository.SaveImportFrontier(
                folder.Id, generation, scanRoot, pending.ToArray()))
                throw new OperationCanceledException("Import frontier superseded.");
        }
        if (saved is null) Persist();
        var options = InventoryEnumerationOptions();
        while (pending.First is { } node)
        {
            yield return string.Empty;
            var directory = node.Value;
            var excluded = FolderExclusionRules.IsExcluded(directory, exclusions, inclusions);
            if (excluded && !FolderExclusionRules.MayContainIncludedDescendant(directory, inclusions))
            {
                pending.RemoveFirst();
                Persist();
                continue;
            }
            var wasComplete = completed.Contains(directory) ||
                _folderRepository.IsFirstPassDirectoryComplete(folder.Id, generation, directory);
            var readFiles = !excluded && !wasComplete;
            var knownPaths = readFiles ? CatalogInventoryCheck.ReadDirectory(_catalog.Database,
                folder.Id, directory, CancellationToken.None) : null;
            var children = new List<string>();
            if (readFiles || recursive)
                foreach (var entry in EnumerateDirectoryEntries(directory, options))
                {
                    if (entry is FileInfo file && readFiles)
                        yield return knownPaths!.ContainsKey(file.FullName) ? string.Empty : file.FullName;
                    else if (entry is DirectoryInfo child && recursive)
                    {
                        if (!FolderExclusionRules.IsExcluded(child.FullName, exclusions, inclusions) ||
                            FolderExclusionRules.MayContainIncludedDescendant(child.FullName, inclusions))
                            children.Add(child.FullName);
                        yield return string.Empty;
                    }
                    else
                        yield return string.Empty;
                }
            // A crash before saving the new frontier can only repeat listing,
            // never lose a discovered descendant or a committed image.
            if (!wasComplete && !excluded) onDirectoryComplete(directory);
            pending.RemoveFirst();
            for (var i = children.Count - 1; i >= 0; i--)
                if (!pending.Contains(children[i], StringComparer.OrdinalIgnoreCase))
                    pending.AddFirst(children[i]);
            Persist();
        }
    }

    internal static IEnumerable<string> EnumerateIncludedFiles(
        string scanRoot,
        bool recursive,
        IReadOnlyCollection<string> exclusions,
        IReadOnlyCollection<string> inclusions,
        CancellationToken token,
        IReadOnlySet<string>? completedDirectories = null,
        IReadOnlyDictionary<string, string>? fileCursors = null,
        IReadOnlySet<string>? alreadyIndexedPaths = null,
        bool catalogImport = false,
        Action<string>? onDirectoryComplete = null,
        IReadOnlyList<string>? preferredDirectories = null,
        Action<FileInfo>? onFileInfo = null,
        bool reportCompletedDirectories = false)
    {
        var pending = new Stack<string>();
        pending.Push(scanRoot);
        if (preferredDirectories is not null)
            foreach (var directory in preferredDirectories.Reverse())
                pending.Push(directory);
        var preferred = new HashSet<string>(preferredDirectories ?? [],
            StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = InventoryEnumerationOptions();

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            // The saved preference might have disappeared since first import.
            // It is not a proof of deletion or inaccessibility; the ordinary
            // traversal below will still verify the selected root and children.
            if (preferred.Remove(directory) &&
                !string.Equals(FolderExclusionRules.Normalize(directory),
                    FolderExclusionRules.Normalize(scanRoot), StringComparison.OrdinalIgnoreCase) &&
                !Directory.Exists(directory))
                continue;
            if (!visited.Add(FolderExclusionRules.Normalize(directory))) continue;
            yield return string.Empty;
            var directoryExcluded = FolderExclusionRules.IsExcluded(
                directory, exclusions, inclusions);
            if (directoryExcluded &&
                !FolderExclusionRules.MayContainIncludedDescendant(directory, inclusions))
                continue;

            var previouslyCompleted = completedDirectories?.Contains(directory) == true;
            var completedNewOnly = !directoryExcluded && previouslyCompleted &&
                alreadyIndexedPaths is not null && !catalogImport;
            var readFiles = !directoryExcluded && (!previouslyCompleted || completedNewOnly);
            // Legacy sorted filename cursors need deterministic file order.
            // All other scans stream FileInfo immediately. Child directories
            // come from the SAME native enumeration, never a second query.
            var sortedFiles = readFiles && !completedNewOnly && fileCursors is not null
                ? new List<FileInfo>() : null;
            var children = new List<string>();
            if (readFiles || recursive)
            {
                foreach (var entry in EnumerateDirectoryEntries(directory, options))
                {
                    token.ThrowIfCancellationRequested();
                    if (entry is DirectoryInfo child)
                    {
                        if (recursive) children.Add(child.FullName);
                        yield return string.Empty;
                    }
                    else if (entry is FileInfo file)
                    {
                        if (!readFiles) { yield return string.Empty; continue; }
                        if (sortedFiles is not null)
                        {
                            sortedFiles.Add(file);
                            yield return string.Empty;
                            continue;
                        }
                        if (completedNewOnly && alreadyIndexedPaths!.Contains(file.FullName))
                        {
                            yield return string.Empty;
                            continue;
                        }
                        onFileInfo?.Invoke(file);
                        yield return file.FullName;
                    }
                }
            }
            if (sortedFiles is not null)
            {
                sortedFiles.Sort((left, right) =>
                    StringComparer.OrdinalIgnoreCase.Compare(left.FullName, right.FullName));
                var last = fileCursors!.TryGetValue(directory, out var cursor) ? cursor : null;
                foreach (var file in sortedFiles)
                {
                    token.ThrowIfCancellationRequested();
                    if (catalogImport && alreadyIndexedPaths?.Contains(file.FullName) == true)
                    { yield return string.Empty; continue; }
                    if (!catalogImport && last is not null &&
                        StringComparer.OrdinalIgnoreCase.Compare(file.Name, last) <= 0)
                    { yield return string.Empty; continue; }
                    onFileInfo?.Invoke(file);
                    yield return file.FullName;
                }
            }
            // No missing-file reconciliation or durable receipt until the
            // single enumeration and all emitted image processing succeeded.
            if (!previouslyCompleted || reportCompletedDirectories)
                onDirectoryComplete?.Invoke(directory);
            for (var i = children.Count - 1; i >= 0; i--)
            {
                var child = children[i];
                if (!FolderExclusionRules.IsExcluded(child, exclusions, inclusions) ||
                    FolderExclusionRules.MayContainIncludedDescendant(child, inclusions))
                    pending.Push(child);
            }
        }
    }

    private static EnumerationOptions InventoryEnumerationOptions() => new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        BufferSize = 64 * 1024
    };

    private static IEnumerable<FileSystemInfo> EnumerateDirectoryEntries(
        string directory, EnumerationOptions options)
    {
        IEnumerator<FileSystemInfo> iterator;
        try
        {
            iterator = new DirectoryInfo(directory)
                .EnumerateFileSystemInfos("*", options).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Verzeichnis konnte nicht gelesen werden: {directory}", ex);
        }
        using (iterator)
        {
            while (true)
            {
                FileSystemInfo entry;
                try
                {
                    if (!iterator.MoveNext()) break;
                    entry = iterator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"Verzeichnis konnte nicht vollständig gelesen werden: {directory}", ex);
                }
                yield return entry;
            }
        }
    }

    private static void EnsureInsideMonitoredFolder(MonitoredFolder folder, string scanRoot)
    {
        if (!FolderExclusionRules.IsAtOrBelow(scanRoot, folder.LocalPath))
            throw new InvalidOperationException(
                $"Der Ordner liegt nicht innerhalb des überwachten Bereichs: {scanRoot}");
    }

    internal static string ResolveCanonicalFullPath(MonitoredFolder folder, string fullLocalPath)
        => CatalogPathPolicy.ResolveCanonicalFullPath(folder, fullLocalPath);
}
