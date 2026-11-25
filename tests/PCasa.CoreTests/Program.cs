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
        VipsImageSource.Resolve(jpg) != jpg)
        throw new Exception("Image processing still depends on the Windows host.");

    // These types must be delivered from the same WPF-free core assembly.
    var assembly = typeof(LocalDatabase).Assembly;
    if (assembly.GetName().Name != "PCasa.Core" ||
        typeof(SourceAvailabilityGate).Assembly != assembly ||
        typeof(SchedulerTaskPackage).Assembly != assembly ||
        typeof(PCasaScheduler).Assembly != assembly ||
        typeof(FolderScanner).Assembly != assembly ||
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
