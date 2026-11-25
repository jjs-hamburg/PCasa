using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

var directory = Path.Combine(Path.GetTempPath(), "PCasa.CoreTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var database = new LocalDatabase(Path.Combine(directory, "catalog.db"));
    database.Initialize();
    database.Initialize(); // migrations must be idempotent on every host
    using (var connection = database.OpenConnection())
    using (var command = connection.CreateCommand())
    {
        command.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('pending_tag_edits')
            WHERE name IN ('owner_key','base_present');
            """;
        if (Convert.ToInt32(command.ExecuteScalar()) != 2)
            throw new Exception("Tag migration did not preserve both cancellation fields.");
    }

    var reachable = false;
    var probes = 0;
    var clock = DateTimeOffset.UtcNow;
    var gate = new SourceAvailabilityGate(_ => { probes++; return reachable; },
        () => clock, offlineInterval: TimeSpan.FromSeconds(10));
    if (gate.CanAccess(directory) || gate.CanAccess(directory) || probes != 1)
        throw new Exception("Offline source probe was not coalesced.");
    reachable = true;
    clock = clock.AddSeconds(11);
    if (!gate.CanAccess(directory) || probes != 2)
        throw new Exception("Source did not recover after its retry interval.");

    // Exercise the actual production scheduler and durable journal on
    // both non-Windows hosts, including preemption and saved step order.
    var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
    await using (var scheduler = new PCasaScheduler())
    {
        scheduler.EnableDurablePackages(database);
        // The durable API requires the original reconstructable recipe type.
        scheduler.RegisterDurablePackageType("manual-folder-scan-v1",
            (key, _) => new SchedulerTaskPackage(key));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var older = new SchedulerTaskPackage("portable:older");
        older.AddStep(async token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            order.Enqueue("older");
        });
        var first = scheduler.EnqueueDurablePackageAsync(older,
            "manual-folder-scan-v1", "{}", SchedulerPriority.ManualAction,
            SchedulerWorkKind.CatalogScan);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newer = new SchedulerTaskPackage("portable:newer");
        newer.AddStep(_ => { order.Enqueue("newer"); return Task.CompletedTask; });
        var second = scheduler.EnqueueDurablePackageAsync(newer,
            "manual-folder-scan-v1", "{}", SchedulerPriority.ManualAction,
            SchedulerWorkKind.CatalogScan);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        if (!order.SequenceEqual(new[] { "newer", "older" }) ||
            attempts != 2 || new DurablePackageJournal(database).Pending().Count != 0)
            throw new Exception("Portable scheduler did not preserve LIFO and durable completion.");
    }

    var dateImage = new BrowseThumbnailIndexEntry(1, 1, 96, "", 0, 0, 1, 1,
        TakenUtcTicks: new DateTime(1985, 12, 31, 23, 59, 59).Ticks,
        CreatedUtcTicks: new DateTime(2026, 1, 1).Ticks);
    if (!GalleryFolderOrdering.MatchesCaptureDate(dateImage,
            new DateTime(1985, 1, 1), new DateTime(1985, 12, 31)) ||
        GalleryFolderOrdering.MatchesCaptureDate(dateImage,
            new DateTime(1986, 1, 1), null) ||
        !GalleryFolderOrdering.MatchesCaptureDate(dateImage with { TakenUtcTicks = null },
            new DateTime(2026, 1, 1), null))
        throw new Exception("Gallery date filtering lost inclusive days or capture-date precedence.");

    var dateRows = new[] {
        dateImage with { ModifiedUtcTicks = new DateTime(2020, 6, 15, 12, 0, 0, DateTimeKind.Utc).Ticks },
        dateImage with { ImageId = 2, TakenUtcTicks = GalleryFolderOrdering.ParseCaptureDateTicks("1985/07/15 14:32:10") },
        dateImage with { ImageId = 3, TakenUtcTicks = null, HasInvalidCaptureDate = true },
        dateImage with { ImageId = 4, TakenUtcTicks = GalleryFolderOrdering.ParseCaptureDateTicks("1990:06:01 12:00:00+02:00") }
    };
    if (!GalleryFolderOrdering.FilterDateRange(dateRows, GallerySortField.Captured,
            new DateTime(1985,1,1), new DateTime(1985,12,31), false).Select(x=>x.ImageId).SequenceEqual(new long[]{1,2}) ||
        !GalleryFolderOrdering.FilterDateRange(dateRows, GallerySortField.Created,
            new DateTime(2025,12,30), new DateTime(2026,1,2), false).Select(x=>x.ImageId).SequenceEqual(new long[]{1,2,3,4}) ||
        !GalleryFolderOrdering.FilterDateRange(dateRows, GallerySortField.Modified,
            new DateTime(2020,6,14), new DateTime(2020,6,16), false).Select(x=>x.ImageId).SequenceEqual(new long[]{1}) ||
        !GalleryFolderOrdering.FilterDateRange(dateRows, GallerySortField.Captured,
            new DateTime(1985,1,1), new DateTime(1985,12,31), true).Select(x=>x.ImageId).SequenceEqual(new long[]{3,4}))
        throw new Exception("Date projection failed to respect field selection, formats, invalid dates or inversion.");

    // The inventory must recognize images without loading a decoder.
    var media = Path.Combine(directory, "photos");
    Directory.CreateDirectory(media);
    var jpg = Path.Combine(media, "sample.jpg");
    File.WriteAllText(jpg, "not a decoded image");
    File.WriteAllText(Path.Combine(media, "ignore.txt"), "ignored");
    var folders = new FolderRepository(database);
    folders.Add(media);
    var root = folders.GetAll().Single();
    var check = new CatalogInventoryCheck(database).Check(root,
        Array.Empty<string>(), Array.Empty<string>(), CancellationToken.None);
    if (check.CheckedFiles != 1 || check.Changes.Count != 1 ||
        check.Changes[0].Path != jpg ||
        check.Changes[0].Kind != BackgroundFolderChangeKind.ScanFile ||
        !FolderExclusionRules.IsAtOrBelow(jpg, media) ||
        FolderExclusionRules.IsAtOrBelow(media + "-other", media))
        throw new Exception("Portable inventory or path boundary failed.");
    if (CatalogPathPolicy.ResolveCanonicalFullPath(root, jpg) != jpg)
        throw new Exception("Canonical path changed native directory separators.");
    if (typeof(FolderRepository).Assembly != typeof(LocalDatabase).Assembly ||
        typeof(ImageCatalogRepository).Assembly != typeof(LocalDatabase).Assembly ||
        typeof(CatalogInventoryCheck).Assembly != typeof(LocalDatabase).Assembly ||
        typeof(BackgroundFolderMonitor).Assembly != typeof(LocalDatabase).Assembly)
        throw new Exception("Catalog or inventory still depends on the Windows host.");

    // Exercise the production import transaction without WPF, libvips or
    // ExifTool. Only the media/metadata adapters are test doubles.
    var catalog = new ImageCatalogRepository(database);
    var importMedia = new TestImportMedia(directory);
    var scanner = new FolderScanner(catalog, importMedia, folders,
        new BrowseThumbnailProfileRepository(database), () => new TestExifReader());
    var imported = scanner.Scan(root);
    if (imported.Updated != 1 || imported.Failed != 0 ||
        catalog.GetImageId(jpg) is null || importMedia.Decodes != 1)
        throw new Exception("Portable scanner did not commit the new image.");
    var unchanged = scanner.Scan(root);
    if (unchanged.Unchanged != 1 || unchanged.Updated != 0 || importMedia.Decodes != 1)
        throw new Exception("Portable scanner decoded an unchanged image again.");

    // Real tag intent and search transactions must work without WPF or
    // any source-file access. No native write is needed for the projection.
    var photoId = catalog.GetImageId(jpg)!.Value;
    var tagOperations = new TagCatalogOperations(database);
    if (tagOperations.QueueTagEdit(new[] { photoId }, "portable tag", false) != 1 ||
        tagOperations.GetKeywords(photoId) != "portable tag" ||
        !tagOperations.SearchImageIds("portable tag").Contains(photoId) ||
        tagOperations.QueueTagEdit(new[] { photoId }, "portable tag", true) != 1 ||
        tagOperations.SearchImageIds("portable tag").Contains(photoId))
        throw new Exception("Portable tag projection or search failed.");

    // RAW GPS edits are catalog-only, including on non-Windows hosts.
    var rawPath = Path.Combine(media, "portable.arw");
    var originalRaw = new byte[] { 3, 1, 4, 1, 5 };
    File.WriteAllBytes(rawPath, originalRaw);
    var rawStamp = File.GetLastWriteTimeUtc(rawPath);
    var rawImport = scanner.ScanFile(root, rawPath);
    var rawId = catalog.GetImageId(rawPath);
    if (rawImport.Updated != 1 || rawId is null)
        throw new Exception("RAW test fixture was not indexed.");
    var gpsOperations = new GpsCatalogOperations(database);
    gpsOperations.Apply(rawId.Value, 48.5, -9.25,
        GpsCatalogOperations.EditMode.Replace, CancellationToken.None);
    using (var connection = database.OpenConnection())
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT gps_latitude,gps_longitude FROM raw_gps_overrides WHERE image_id=$id";
        command.Parameters.AddWithValue("$id", rawId.Value);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetDouble(0) != 48.5 || reader.GetDouble(1) != -9.25)
            throw new Exception("RAW GPS override was not persisted.");
    }
    if (!File.ReadAllBytes(rawPath).SequenceEqual(originalRaw) ||
        File.GetLastWriteTimeUtc(rawPath) != rawStamp ||
        Directory.EnumerateFiles(media).Any(path => Path.GetExtension(path) == ".xmp"))
        throw new Exception("RAW GPS unexpectedly modified the original or created a sidecar.");

    // Metadata-only confirmation is committed with the catalog fingerprint.
    // It survives database reopen, but never claims a different file stamp.
    using (var connection = database.OpenConnection())
    using (var transaction = connection.BeginTransaction())
    {
        OriginalFileMutationReceipts.RecordConfirmed(connection, transaction,
            photoId, new FileInfo(jpg));
        transaction.Commit();
    }
    var reopened = new LocalDatabase(Path.Combine(directory, "catalog.db"));
    reopened.Initialize();
    if (!OriginalFileMutationReceipts.IsConfirmedNotification(reopened, jpg))
        throw new Exception("Committed metadata notification was not recognized after reopen.");
    if (BackgroundFolderMonitor.IsRelevantRemoval(reopened, root.Id,
            Path.Combine(media, "sample.jpg_exiftool_tmp")) ||
        !BackgroundFolderMonitor.IsRelevantRemoval(reopened, root.Id, media) ||
        !BackgroundFolderMonitor.IsRelevantRemoval(reopened, root.Id, jpg))
        throw new Exception("Temporary writer removal became a photo job, or real removal was lost.");
    File.AppendAllText(jpg, "external change");
    if (OriginalFileMutationReceipts.IsConfirmedNotification(reopened, jpg))
        throw new Exception("Metadata receipt hid a subsequent external modification.");
    scanner.ScanFile(root, jpg);
    if (catalog.CountIndexedImages(root.Id, media, true) != 2)
        throw new Exception("Import progress did not count committed catalog images.");

    // Simulate upgrading a populated Alpha.63 catalog. The upgrade must
    // remove old tasks once, not on every future application launch.
    var resetJournal = new DurablePackageJournal(database);
    resetJournal.Put("alpha63:obsolete", "gps-edit-v2", "{}",
        SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
    folders.AcceptAutomaticChange("alpha63:automatic",
        new BackgroundFolderChange(root.Id, jpg, BackgroundFolderChangeKind.ScanFile, false));
    using (var connection = database.OpenConnection())
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "UPDATE monitored_folders SET initial_scan_pending=1, initial_scan_deleted=0; DELETE FROM catalog_migrations WHERE migration_key='alpha64-task-reset';";
        command.ExecuteNonQuery();
    }
    database.Initialize();
    if (resetJournal.Pending().Count != 0 || folders.GetPendingAutomaticChanges().Count != 0 ||
        catalog.CountIndexedImages(root.Id, media, true) != 2 ||
        catalog.GetImageId(jpg) != photoId || catalog.GetImageId(rawPath) != rawId)
        throw new Exception("Alpha.64 reset retained old work or destroyed catalog progress.");
    // Reset must not turn an unfinished first import into a completed one,
    // nor immediately recreate it as thousands of P4 file tasks. A new
    // explicit scan completes the source and makes it eligible for review.
    if (folders.GetPendingInitialScans().Any(f => f.Id == root.Id) ||
        folders.GetDueRootReviewIds(DateTime.UtcNow).Contains(root.Id) ||
        !folders.HasIncompleteImport(root.Id))
        throw new Exception("Reset resurrected a removed first import or claimed it completed.");
    scanner.Scan(root);
    if (folders.HasIncompleteImport(root.Id) ||
        !folders.GetDueRootReviewIds(DateTime.UtcNow).Contains(root.Id) ||
        catalog.CountIndexedImages(root.Id, media, true) != 2)
        throw new Exception("Explicit scan did not resume the reset source from its catalog.");
    resetJournal.Put("alpha64:new", "gps-edit-v2", "{}",
        SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
    resetJournal.SetState("alpha64:new", "running", 1);
    database.Initialize();
    if (resetJournal.Pending().Single().CompletedSteps != 1)
        throw new Exception("Alpha.64 reset ran more than once.");
    resetJournal.SetState("alpha64:new", "completed", 2);

    // The actual folder-rename transaction retains identities, including
    // child images, and uses the native path separator instead of backslash.
    var renamed = Path.Combine(directory, "renamed-photos");
    var renameService = new CatalogRenameService(database);
    var renameId = Guid.NewGuid().ToString("N");
    renameService.PrepareDirectoryRename(renameId, media, renamed, media, renamed);
    Directory.Move(media, renamed);
    renameService.RebaseDirectory(media, renamed, media, renamed, renameId);
    if (catalog.GetImageId(Path.Combine(renamed, "sample.jpg")) != photoId ||
        catalog.GetImageId(Path.Combine(renamed, "portable.arw")) != rawId ||
        catalog.GetImageId(jpg) is not null ||
        !renameService.PendingRenames().Single().CatalogApplied)
        throw new Exception("Folder rename changed catalog identities or failed to rebase paths.");
    renameService.CompleteDirectoryRename(renameId);
    if (renameService.PendingRenames().Count != 0)
        throw new Exception("Folder rename receipt was not completed.");

    // Test the physical rename separately from the catalog. Existing targets
    // must survive conflicts; cancelled operations must leave both paths alone.
    var moveSource = Path.Combine(directory, "move-source.jpg");
    var moveTarget = Path.Combine(directory, "move-target.jpg");
    File.WriteAllText(moveSource, "original");
    File.WriteAllText(moveTarget, "existing");
    try
    {
        ServerSideFileMover.Move(moveSource, moveTarget);
        throw new Exception("No-replace move overwrote an existing destination.");
    }
    catch (IOException) { }
    if (File.ReadAllText(moveSource) != "original" ||
        File.ReadAllText(moveTarget) != "existing")
        throw new Exception("Conflicting move damaged source or target.");
    using (var cancelledMove = new CancellationTokenSource())
    {
        cancelledMove.Cancel();
        try
        {
            ServerSideFileMover.Move(moveSource, moveTarget, replace: true,
                cancellationToken: cancelledMove.Token);
            throw new Exception("Move ignored cancellation.");
        }
        catch (OperationCanceledException) { }
    }
    if (File.ReadAllText(moveSource) != "original" ||
        File.ReadAllText(moveTarget) != "existing")
        throw new Exception("Cancelled move damaged source or target.");
    File.Delete(moveTarget);
    if (ServerSideFileMover.Move(moveSource, moveTarget) != ServerSideFileMover.MoveMethod.Rename ||
        File.Exists(moveSource) || File.ReadAllText(moveTarget) != "original")
        throw new Exception("Same-volume move did not use native rename.");

    // Manual backup and restore use the same catalog/archive implementation
    // on both platforms, including hash-bucket thumbnail relocation.
    var maintenanceRoot = Path.Combine(directory, "maintenance-source");
    Directory.CreateDirectory(maintenanceRoot);
    var maintenanceDb = new LocalDatabase(Path.Combine(maintenanceRoot, "pcasa.db"));
    maintenanceDb.Initialize();
    new FolderRepository(maintenanceDb).Add(renamed);
    var cacheBucket = Path.Combine(maintenanceRoot, "thumbnails", "ab");
    Directory.CreateDirectory(cacheBucket);
    File.WriteAllBytes(Path.Combine(cacheBucket, "thumb.jpg"), new byte[] { 1, 2, 3 });
    var maintenance = new DatabaseMaintenance(maintenanceRoot);
    var archive = Path.Combine(directory, "manual-backup.zip");
    maintenance.Backup(archive);
    var backupRoot = maintenance.InspectBackup(archive).Single();
    var restoreRoot = Path.Combine(directory, "maintenance-restored");
    var restored = new DatabaseMaintenance(restoreRoot);
    restored.Restore(archive, new[] { new DatabaseMaintenance.FolderChoice(
        backupRoot.Id, backupRoot.LocalPath, backupRoot.CanonicalPath, renamed, true) });
    if (restored.ReadSummary().Roots != 1 ||
        !File.ReadAllBytes(Path.Combine(restoreRoot, "thumbnails", "ab", "thumb.jpg"))
            .SequenceEqual(new byte[] { 1, 2, 3 }) ||
        DatabaseMaintenance.RehomeThumbnailPath(@"C:\old\thumbnails\ab\thumb.jpg",
            Path.Combine(restoreRoot, "thumbnails")) !=
            Path.Combine(restoreRoot, "thumbnails", "ab", "thumb.jpg"))
        throw new Exception("Portable backup/restore failed.");

    // Test the real video cache lifecycle with a codec-independent adapter.
    var video = Path.Combine(directory, "clip.mp4");
    File.WriteAllBytes(video, new byte[] { 1, 2, 3 });
    var videoRoot = Path.Combine(directory, "video-cache");
    var encodes = 0;
    void EncodeVideo(string source, string target, bool refresh, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source != video) throw new Exception("Wrong video source.");
        File.WriteAllBytes(target, new byte[] { (byte)++encodes });
    }
    var preview = VideoPreviewCache.Resolve(video, EncodeVideo, cacheRoot: videoRoot);
    if (VideoPreviewCache.Resolve(video, EncodeVideo, cacheRoot: videoRoot) != preview ||
        encodes != 1 ||
        VideoPreviewCache.Resolve(video, EncodeVideo, forceRefresh: true, cacheRoot: videoRoot) != preview ||
        encodes != 2 || File.ReadAllBytes(preview).Single() != 2)
        throw new Exception("Video cache did not preserve identity or explicit refresh.");
    try
    {
        VideoPreviewCache.Resolve(video, (_, temp, _, _) =>
        {
            File.WriteAllBytes(temp, new byte[] { 9 });
            throw new IOException("Simulated codec failure");
        }, forceRefresh: true, cacheRoot: videoRoot);
        throw new Exception("Video cache swallowed codec failure.");
    }
    catch (IOException) { }
    if (File.ReadAllBytes(preview).Single() != 2 ||
        Directory.EnumerateFiles(videoRoot, "*.tmp", SearchOption.AllDirectories).Any())
        throw new Exception("Failed refresh destroyed the last valid preview.");
    if (typeof(ExifToolKeywordWriter).Assembly != typeof(LocalDatabase).Assembly ||
        typeof(AdaptiveMetadataWriteTimeout).Assembly != typeof(LocalDatabase).Assembly ||
        !CatalogMediaFormats.IsRawPath("test.ARW") ||
        !AdaptiveMetadataWriteTimeout.SourceKey(jpg).StartsWith("LOCAL:"))
        throw new Exception("Portable metadata write boundary failed.");

    // Exercise the actual PSD decoder with no Windows bitmap or native library.
    // The asymmetric portrait fixture detects swapped dimensions/channels.
    PortablePsdRegression.Run(directory);
    if (args.Contains("--native-imaging", StringComparer.Ordinal))
        PortableNativeImagingRegression.Run(directory);

    // Real packed-cache append and offsets are platform-independent, even
    // without an installed native decoder. Exercise the actual store.
    var packRoot = Path.Combine(directory, "packs");
    using (IImportThumbnailStore packStore = new PackedThumbnailStore(packRoot))
    {
        var firstBytes = new byte[] { 10, 20, 30 };
        var secondBytes = new byte[] { 40, 50 };
        var first = packStore.AppendPrepared(new[] {
            new ImportThumbnailTier(96, firstBytes, 32, 24) }).Single();
        var second = packStore.AppendPrepared(new[] {
            new ImportThumbnailTier(96, secondBytes, 32, 24) }).Single();
        if (first.PackFile != second.PackFile || first.ByteOffset != 0 ||
            second.ByteOffset != firstBytes.Length ||
            !File.ReadAllBytes(Path.Combine(packRoot, first.PackFile))
                .SequenceEqual(firstBytes.Concat(secondBytes)))
            throw new Exception("Portable pack append did not preserve offsets or bytes.");
    }
    var imaging = typeof(VipsImageSource).Assembly;
    if (imaging.GetName().Name != "PCasa.Imaging" ||
        typeof(VipsThumbnailCache).Assembly != imaging ||
        typeof(PackedThumbnailStore).Assembly != imaging ||
        typeof(NativeImageCachePolicy).Assembly != imaging ||
        imaging.GetReferencedAssemblies().Any(reference =>
            reference.Name is "PresentationFramework" or "PresentationCore" or
                "WindowsBase" or "PCasa.App") ||
        !VipsImageSource.IsRawPath("test.ARW") ||
        VipsImageSource.Resolve(Path.Combine(renamed, "sample.jpg")) != Path.Combine(renamed, "sample.jpg"))
        throw new Exception("Image processing still depends on the Windows host.");

    // These types must be delivered from the same WPF-free core assembly.
    var assembly = typeof(LocalDatabase).Assembly;
    if (assembly.GetName().Name != "PCasa.Core" ||
        typeof(SourceAvailabilityGate).Assembly != assembly ||
        typeof(SchedulerTaskPackage).Assembly != assembly ||
        typeof(PCasaScheduler).Assembly != assembly ||
        typeof(FolderScanner).Assembly != assembly ||
        typeof(TagCatalogOperations).Assembly != assembly ||
        typeof(GpsCatalogOperations).Assembly != assembly ||
        typeof(CatalogRenameService).Assembly != assembly ||
        typeof(ServerSideFileMover).Assembly != assembly ||
        typeof(DatabaseMaintenance).Assembly != assembly ||
        typeof(FolderScanStatusTracker).Assembly != assembly ||
        typeof(DurablePackageJournal).Assembly != assembly ||
        typeof(SchedulerTaskOrderStore).Assembly != assembly ||
        typeof(DurableGalleryTransferJournal).Assembly != assembly ||
        typeof(OriginalWriteFailureRepository).Assembly != assembly ||
        assembly.GetReferencedAssemblies().Any(reference =>
            reference.Name is "PresentationFramework" or "PresentationCore" or
                "WindowsBase" or "PCasa.App"))
        throw new Exception("The portable core depends on the Windows presentation layer.");
    Console.WriteLine("PASS: portable SQLite migrations, source recovery and assembly boundary.");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(directory, recursive: true);
}

sealed class TestExifReader : IImportExifReader
{
    public ImageExifData Read(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ImageExifData("{}", null, null, null, null, null, null);
    }
    public void Dispose() { }
}

sealed class TestImportMedia(string directory) : IFolderImportMedia
{
    public int Decodes { get; private set; }
    public bool IsRawPath(string path) => false;
    public string ResolveSource(string path, CancellationToken token) => path;
    public ThumbnailResult CreateOrUpdate(string sourcePath, string canonicalPath,
        long fileSize, long modifiedUtcTicks, string? originalSourcePath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Decodes++;
        var path = Path.Combine(directory, "test-thumbnail.jpg");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return new ThumbnailResult(path, 32, 24);
    }
    public ThumbnailResult CreateImportErrorPlaceholder(string canonicalPath,
        long fileSize, long modifiedUtcTicks, CancellationToken token)
        => throw new NotSupportedException();
    public IImportThumbnailStore CreatePackStore() => new TestImportPack();
    public string? ReadKeywords(string path) => null;
    public bool IsDecodeFailure(Exception exception) => false;
}

sealed class TestImportPack : IImportThumbnailStore
{
    public IReadOnlyList<ImportThumbnailTier> PrepareTiers(string sourcePath,
        BrowseThumbnailProfile profile, CancellationToken cancellationToken = default,
        string? originalSourcePath = null)
        => profile.Tiers.Select(t => new ImportThumbnailTier(t, new byte[] { 1 }, 32, 24)).ToArray();

    public IReadOnlyList<PackedThumbnailLocation> AppendPrepared(
        IReadOnlyList<ImportThumbnailTier> prepared, CancellationToken cancellationToken = default)
        => prepared.Select(t => new PackedThumbnailLocation(t.Tier, "test.pack",
            0, t.Bytes.Length, t.Width, t.Height)).ToArray();

    public void Dispose() { }
}
