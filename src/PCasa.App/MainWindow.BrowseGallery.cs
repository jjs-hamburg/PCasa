using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCasa.App.Controls;
using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private BrowseThumbnailProfileRepository? _browseProfileRepository;
    private BrowseCacheRepository? _browseCacheRepository;
    private BrowseCacheBuilder? _browseCacheBuilder;
    private BrowseThumbnailPageCache? _browsePageCache;
    private IReadOnlyList<BrowseThumbnailIndexEntry> _browseIndex =
        Array.Empty<BrowseThumbnailIndexEntry>();
    private CancellationTokenSource? _browseCacheCancellation;
    private CancellationTokenSource? _thumbnailSizeCancellation;
    private int? _activeBrowseTier;
    private readonly LatestViewRequest _browseRefreshRequests = new();
    private bool _browseInitialized;
    private bool _browseCacheBuilding;
    private bool _folderManagerOpen;
    private bool _folderManagerCommitPending;
    private BrowseCacheProgressWindow? _browseProgressWindow;

    private async void BrowseSurface_Loaded(object sender, RoutedEventArgs e)
    {
        if (_browseInitialized)
        {
            return;
        }

        _browseInitialized = true;
        _browseProfileRepository = new BrowseThumbnailProfileRepository(_database);
        _browseCacheRepository = new BrowseCacheRepository(_database);
        _browseCacheBuilder = new BrowseCacheBuilder(
            _browseCacheRepository,
            _browseProfileRepository);
        _browsePageCache = new BrowseThumbnailPageCache(_scheduler);
        BrowseSurface.ImageClicked += BrowseSurface_ImageClicked;
        BrowseSurface.EmptyAreaClicked += BrowseSurface_EmptyAreaClicked;
        BrowseSurface.ImageOpened += BrowseSurface_ImageOpened;
        BrowseSurface.ImageContextRequested += BrowseSurface_ImageContextRequested;
        BrowseSurface.ImagesDragRequested += BrowseSurface_ImagesDragRequested;
        BrowseSurface.LayoutReflowed += BrowseSurface_LayoutReflowed;
        CatalogChanges.ImageCommitted += OnCatalogImageCommitted;
        CatalogChanges.ImageIndexed += OnCatalogImageIndexed;
        Closed += BrowseWindow_Closed;

        // Recovery is a scheduler operation, not an alternative scanner.
        // It may perform slow SMB probes without blocking WPF startup.
        // If any durable move is recovered, the SAME gallery publication
        // owner will bring the new catalog state into the current view.
        _ = RecoverPendingFileTransfersAsync();

        // Durable user imports enter the scheduler BEFORE background legacy
        // repair. Both use the SAME catalog and generation guards.
        QueuePendingInitialScans();
        _ = ReconcilePersistedExclusionsAsync();
        await EnsureBrowseCacheAndRefreshAsync();
        StartTagBackfill();
    }


    private async Task ReconcilePersistedExclusionsAsync()
    {
        try
        {
            var removed = await _scheduler.RunAsync(
                "catalog:reconcile-persisted-exclusions",
                SchedulerPriority.Background,
                token => new FolderManagerSelectionService(_database)
                    .ReconcilePersistedExclusions(token),
                workKind: SchedulerWorkKind.Catalog);
            if (removed > 0 && !Dispatcher.HasShutdownStarted)
            {
                await RefreshBrowseGalleryAsync(forceIndexReload: true);
                StatusText.Text =
                    $"{removed:N0} verwaiste Einträge ausgeschlossener Ordner bereinigt";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Catalog.ExclusionRecovery", ex);
        }
    }

    private async Task RecoverPendingFileTransfersAsync()
    {
        try
        {
            // Recovery may encounter a disconnected NAS. It runs in the
            // cancellable background lane; a fresh MANUAL operation can
            // preempt it. Startup recovery is never allowed to occupy the
            // sole manual executor while an unreachable SMB path times out.
            var result = await _scheduler.RunAsync(
                "catalog:recover-file-transfers",
                SchedulerPriority.Background,
                token => _imageCatalog.RecoverRelocations(
                    _folderRepository.GetAll(), token),
                workKind: SchedulerWorkKind.Catalog);
            if (result.Recovered > 0)
                await RefreshBrowseGalleryAsync(forceIndexReload: true);
            if (result.Unresolved > 0)
            {
                StatusText.Text =
                    $"{result.Unresolved:N0} Dateioperation(en) benötigen Prüfung " +
                    "(Quell-/Zielzustand unklar)";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Catalog.TransferRecovery", ex);
            StatusText.Text = "Ausstehende Dateioperationen konnten nicht geprüft werden";
        }
    }

    private async Task EnsureBrowseCacheAndRefreshAsync()
    {
        if (!_browseInitialized ||
            _browseProfileRepository is null ||
            _browseCacheRepository is null ||
            _browseCacheBuilder is null ||
            _browsePageCache is null ||
            _browseCacheBuilding ||
            _folderRemovalPending)
        {
            return;
        }

        IReadOnlyList<PhotoFolderSummary> initialFolders = Array.Empty<PhotoFolderSummary>();
        IReadOnlyList<(MonitoredFolder Folder, IReadOnlyList<string> ExcludedPaths)> initialConfiguration =
            Array.Empty<(MonitoredFolder, IReadOnlyList<string>)>();
        IReadOnlyList<string> createdDirectories = Array.Empty<string>();
        var imageCount = 0;
        await _scheduler.EnqueueAsync(
            $"catalog:initial-summary:{Guid.NewGuid():N}", SchedulerPriority.Interactive,
            token =>
            {
                token.ThrowIfCancellationRequested();
                initialFolders = _imageCatalog.GetPhotoFolders();
                imageCount = _imageCatalog.CountAll();
                initialConfiguration = ReadFolderTreeConfigurationSnapshot();
                createdDirectories = _folderRepository.GetCreatedDirectories();
                return Task.CompletedTask;
            }, workKind: SchedulerWorkKind.CatalogRead);
        if (!_browseInitialized || _folderRemovalPending) return;
        _photoFolders = initialFolders;
        _dropTargetProjection = initialConfiguration;
        _createdEmptyFolderPaths.Clear();
        _createdEmptyFolderPaths.UnionWith(createdDirectories);
        _dragTargetValidity.Clear();
        RefreshFolderTree(preserveGalleryAnchor: false);
        _lastCatalogImageCount = imageCount;
        EmptyPanel.Visibility = imageCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        // A foreground scan must NOT suppress the independent gallery.
        // Display everything already committed even if a newly added NAS root
        // is still being scanned (or is blocked/offline). Defer expensive
        // missing-cache repair, but never defer showing existing thumbnails.
        if (_manualScanRunning)
        {
            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                warmViewport: false);
            return;
        }
        if (imageCount == 0)
        {
            StatusText.Text = "0 Bilder";
            return;
        }

        var profile = _browseProfileRepository.Get();
        var missing = _browseCacheRepository.CountMissing(profile);
        BrowseCacheBuildResult? result = null;

        if (missing > 0)
        {
            _browseCacheBuilding = true;
            _browseCacheCancellation?.Cancel();
            _browseCacheCancellation?.Dispose();
            _browseCacheCancellation = new CancellationTokenSource();

            // This is intentionally SHOW(), never ShowDialog(): the thumbnail
            // preparation must not dim, disable or block the browsing window.
            _browseProgressWindow?.Finish();
            var progressWindow = new BrowseCacheProgressWindow { Owner = this };
            _browseProgressWindow = progressWindow;
            progressWindow.Show();
            StatusText.Text = $"Vorschaudaten vorbereiten: 0 / {missing:N0}";

            var progress = new Progress<BrowseCacheBuildProgress>(value =>
            {
                progressWindow.Update(value);
                StatusText.Text =
                    $"Vorschaudaten: {value.ProcessedImages:N0} / {value.TotalImages:N0}";
            });

            try
            {
                await _scheduler.EnqueueAsync(
                    "browse-cache:foreground",
                    SchedulerPriority.Background,
                    token =>
                    {
                        result = _browseCacheBuilder.BuildMissing(progress, token);
                        return Task.CompletedTask;
                    },
                    cancellationToken: _browseCacheCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("BrowseCache.BuildMissing", ex);
                StatusText.Text = "Vorschaudaten: Konvertierung fehlgeschlagen – Details im PCasa-Fehlerprotokoll";
                return;
            }
            finally
            {
                _browseCacheBuilding = false;
                progressWindow.Finish();
                if (ReferenceEquals(_browseProgressWindow, progressWindow))
                    _browseProgressWindow = null;
            }
        }

        await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: true);

        if (result is { FailedImages: > 0 })
        {
            StatusText.Text =
                $"{imageCount:N0} Bilder · {result.FailedImages:N0} Vorschaudaten konnten nicht erzeugt werden";
        }
        else
        {
            if (!_isScanning && !_manualScanRunning)
            StatusText.Text = $"{imageCount:N0} Bilder";
        }
    }

    // Exactly one publisher at a time. The pending bit AND the publisher
    // lifetime change under the SAME gate. A Task.IsCompleted check was
    // insufficient: a request between the last pending check and task
    // completion could disappear forever, leaving moved images on screen.
    private readonly object _galleryRefreshGate = new();
    private bool _galleryRefreshRunning;
    private TaskCompletionSource? _galleryRefreshCompletion;
    private bool _galleryRefreshPending;
    private bool _galleryRefreshForceIndex;
    private bool _galleryRefreshWarmViewport;

    private Task RefreshBrowseGalleryAsync(
        bool forceIndexReload,
        bool warmViewport = false)
    {
        lock (_galleryRefreshGate)
        {
            _galleryRefreshPending = true;
            _galleryRefreshForceIndex |= forceIndexReload;
            _galleryRefreshWarmViewport |= warmViewport;
            if (!_galleryRefreshRunning)
            {
                _galleryRefreshRunning = true;
                _galleryRefreshCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _ = DrainGalleryRefreshAsync(_galleryRefreshCompletion);
            }
            return _galleryRefreshCompletion!.Task;
        }
    }

    private async Task DrainGalleryRefreshAsync(TaskCompletionSource completion)
    {
        // Never execute publication inside the caller's gate.
        await Task.Yield();
        try
        {
            while (!Dispatcher.HasShutdownStarted)
            {
                bool force = false, warm = false, finished = false;
                lock (_galleryRefreshGate)
                {
                    if (!_galleryRefreshPending)
                    {
                        // This is the only legal idle transition. A new
                        // request from now on creates its OWN publisher.
                        _galleryRefreshRunning = false;
                        _galleryRefreshCompletion = null;
                        finished = true;
                    }
                    else
                    {
                        force = _galleryRefreshForceIndex;
                        warm = _galleryRefreshWarmViewport;
                        _galleryRefreshPending = false;
                        _galleryRefreshForceIndex = false;
                        _galleryRefreshWarmViewport = false;
                    }
                }
                if (finished)
                {
                    completion.TrySetResult();
                    return;
                }

                try
                {
                    await RefreshBrowseGalleryCoreAsync(force, warm);
                }
                catch (OperationCanceledException) when (!Dispatcher.HasShutdownStarted)
                {
                    // Filter/input versions can cancel an individual
                    // projection; the latest confirmed catalog still needs
                    // a publication. Retry after newer UI changes settle.
                    lock (_galleryRefreshGate)
                    {
                        _galleryRefreshPending = true;
                        _galleryRefreshForceIndex = true;
                    }
                    await Task.Yield();
                }
            }

            lock (_galleryRefreshGate)
            {
                if (ReferenceEquals(_galleryRefreshCompletion, completion))
                {
                    _galleryRefreshRunning = false;
                    _galleryRefreshCompletion = null;
                }
            }
            completion.TrySetResult(); // Owner window is shutting down.
        }
        catch (Exception ex)
        {
            lock (_galleryRefreshGate)
            {
                if (ReferenceEquals(_galleryRefreshCompletion, completion))
                {
                    _galleryRefreshRunning = false;
                    _galleryRefreshCompletion = null;
                }
            }
            completion.TrySetException(ex);
        }
    }

    private async Task RefreshBrowseGalleryCoreAsync(
        bool forceIndexReload,
        bool warmViewport = false)
    {
        if (!_browseInitialized ||
            _browseProfileRepository is null ||
            _browseCacheRepository is null ||
            _browsePageCache is null)
        {
            return;
        }

        var refresh = _browseRefreshRequests.Begin();

        // Keep the old image visible until the new catalog index and layout
        // are applied. A tree reflow during the database await must not
        // consume this pending anchor.
        QueueGalleryAnchorRestore();
        IReadOnlyList<PhotoFolderSummary> freshFolders = Array.Empty<PhotoFolderSummary>();
        var imageCount = 0;
        try
        {
            await _scheduler.EnqueueAsync(
                $"catalog:gallery-summary:{refresh}", SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    freshFolders = _imageCatalog.GetPhotoFolders();
                    imageCount = _imageCatalog.CountAll();
                    return Task.CompletedTask;
                }, () => _browseRefreshRequests.IsCurrent(refresh),
                workKind: SchedulerWorkKind.CatalogRead);
        }
        catch (OperationCanceledException) { return; }
        if (!_browseRefreshRequests.IsCurrent(refresh)) return;
        // Do not publish a new folder summary while the corresponding
        // thumbnail index is still queued behind SQLite/network work.
        // The last fully published gallery remains available during a move.
        var nextFolders = freshFolders
            .Where(folder => !IsPendingFolderRemovalHidden(folder.LocalPath))
            .ToArray();

        if (imageCount == 0)
        {
            PublishPhotoFolderSnapshot(nextFolders, imageCount);
            System.Threading.Volatile.Write(ref _galleryHasIndexedImages, 0);
            _browseIndex = Array.Empty<BrowseThumbnailIndexEntry>();
            _dragSelectionPathCache.Clear();
            _displayedBrowseIndex = _browseIndex;
            _browsePageCache.SetIndex(_browseIndex);
            BrowseSurface.SetData(_browseIndex, _photoFolders, _browsePageCache);
            _selectedImageIds.Clear();
            _activePhotoFolderId = null;
            BrowseSurface.SetSelection(_selectedImageIds);
            SelectionText.Text = "Auswahl: 0";
            UpdateComparisonButtons();
            _ = UpdateSidebarContentAsync();
            EmptyPanel.Visibility = Visibility.Visible;
            StatusText.Text = "0 Bilder";
            RestorePendingGalleryAnchor();
            return;
        }

        var profile = _browseProfileRepository.Get();
        var tier = profile.ChooseForDisplayWidth(ThumbnailSizeSlider.Value);
        IReadOnlyList<BrowseThumbnailIndexEntry>? nextIndex = null;

        if (forceIndexReload || _activeBrowseTier != tier || _browseIndex.Count == 0)
        {
            if (!_isScanning && !_manualScanRunning)
                StatusText.Text = $"Galerieindex {tier}px laden...";
            IReadOnlyList<BrowseThumbnailIndexEntry> index = Array.Empty<BrowseThumbnailIndexEntry>();
            try
            {
                await _scheduler.EnqueueAsync(
                    $"catalog:gallery-index:{refresh}:{tier}", SchedulerPriority.Interactive,
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        index = _browseCacheRepository.GetIndex(tier);
                        return Task.CompletedTask;
                    }, () => _browseRefreshRequests.IsCurrent(refresh),
                    workKind: SchedulerWorkKind.CatalogRead);
            }
            catch (OperationCanceledException) { return; }
            if (!_browseRefreshRequests.IsCurrent(refresh))
            {
                return;
            }

            // Keep the newly read index PRIVATE until the last awaited
            // catalog read. Neither index nor folders may be published alone.
            nextIndex = index;
        }

        if (_selectedImageIds.Count > 0)
        {
            // A removed/unchecked folder can leave deleted image IDs selected.
            // The catalog, not the currently available thumbnail tier, decides
            // whether a selection is still valid.
            var checkedSelection = _selectedImageIds.ToHashSet();
            HashSet<long> existing = new();
            try
            {
                await _scheduler.EnqueueAsync(
                    $"catalog:selection:{refresh}", SchedulerPriority.Interactive,
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        existing = _imageCatalog.GetExistingImageIds(checkedSelection);
                        return Task.CompletedTask;
                    }, () => _browseRefreshRequests.IsCurrent(refresh),
                    workKind: SchedulerWorkKind.CatalogRead);
            }
            catch (OperationCanceledException) { return; }
            if (!_browseRefreshRequests.IsCurrent(refresh))
                return;
            if (_selectedImageIds.RemoveWhere(id =>
                    checkedSelection.Contains(id) && !existing.Contains(id)) > 0)
            {
                UpdateComparisonButtons();
                _ = UpdateSidebarContentAsync();
            }
        }

        // One WPF publication boundary. The summary and index have both
        // finished loading, and the last source-selection/catalog read has
        // validated this refresh request. Consumers cannot observe a newer
        // folder list paired with the previous gallery index.
        if (!_browseRefreshRequests.IsCurrent(refresh)) return;
        PublishPhotoFolderSnapshot(nextFolders, imageCount);
        if (nextIndex is null && _publishedGallerySort != CurrentGallerySort)
            nextIndex = _browseIndex;
        if (nextIndex is not null)
        {
            IReadOnlyList<BrowseThumbnailIndexEntry> ordered = nextIndex;
            if (_pendingFolderRemovalPath is not null)
            {
                var visibleIds = nextFolders.Select(folder => folder.Id).ToHashSet();
                ordered = ordered.Where(image =>
                    visibleIds.Contains(image.PhotoFolderId)).ToArray();
            }
            ordered = GalleryFolderOrdering.Order(ordered,
                CaptureGalleryFolderOrder(), CurrentGallerySort);
            if (_activeBrowseTier != tier ||
                _browseIndex.Count != ordered.Count ||
                !_browseIndex.SequenceEqual(ordered))
            {
                _browseIndex = ordered;
                _dragSelectionPathCache.Clear();
            }
            _activeBrowseTier = tier;
            _publishedGallerySort = CurrentGallerySort;
        }
        System.Threading.Volatile.Write(ref _galleryHasIndexedImages,
            _browseIndex.Count > 0 ? 1 : 0);

        await ApplyTagFilterAsync();
        if (!_browseRefreshRequests.IsCurrent(refresh))
            return;

        BrowseSurface.SetThumbnailWidth(ThumbnailSizeSlider.Value);
        BrowseSurface.SetSelection(_selectedImageIds);
        EmptyPanel.Visibility = _displayedBrowseIndex.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        await Dispatcher.InvokeAsync(
            () =>
            {
                if (!_browseRefreshRequests.IsCurrent(refresh))
                    return;
                BrowseScrollViewer.UpdateLayout();
                RestoreGalleryPositionOnce();
                RestorePendingGalleryAnchor();
                BrowseSurface.SetViewport(
                    BrowseScrollViewer.VerticalOffset,
                    Math.Max(1, BrowseScrollViewer.ViewportHeight));
            },
            DispatcherPriority.Loaded);

        if (!_browseRefreshRequests.IsCurrent(refresh))
            return;

        if (warmViewport)
        {
            // The visible area can contain multiple disjoint index ranges
            // when intervening photo folders are collapsed.
            await BrowseSurface.WarmVisibleAsync();
            if (!_browseRefreshRequests.IsCurrent(refresh))
                return;
            BrowseSurface.InvalidateVisual();
        }

        if (string.IsNullOrWhiteSpace(TagSearchBox.Text))
            if (!_isScanning && !_manualScanRunning)
            StatusText.Text = $"{imageCount:N0} Bilder";
        SelectionText.Text = $"Auswahl: {_selectedImageIds.Count:N0}";
    }

    // Called only on Dispatcher, with a complete paired gallery snapshot.
    // Numeric counts and discovered tree nodes are incremental; never rebuild
    // the entire TreeView merely because a photo moved.
    private void PublishPhotoFolderSnapshot(
        IReadOnlyList<PhotoFolderSummary> folders, int imageCount)
    {
        _photoFolders = folders;
        _lastCatalogImageCount = imageCount;
        foreach (var discovered in folders)
        {
            if (_folderTreeItems.ContainsKey(discovered.Id)) continue;
            ShowScannedPhotoFolder(new ScanProgress(0, 0, 0, 0, null,
                DiscoveredPhotoFolderId: discovered.Id,
                DiscoveredPhotoFolderPath: discovered.LocalPath));
        }
        RefreshVisibleFolderCounts();
        UpdateGalleryFolderVisibility(preserveGalleryAnchor: false);
    }

    // Every committed image (not just the FIRST image) may change a visible
    // folder's count and the selected gallery. Coalesce notifications rather
    // than rebuilding the index 500,000 times during a bulk scan.
    private int _catalogPublicationPending;
    private bool _catalogPublicationBusy;
    private DispatcherTimer? _catalogPublicationTimer;
    private int _galleryHasIndexedImages;

    private void OnCatalogImageCommitted()
    {
        // A running scan must not repeatedly rebuild the whole gallery.
        // A targeted committed-image projection will handle live ingestion.
        // Until that path is wired, preserve the stable existing gallery.
        if (_isScanning &&
            System.Threading.Volatile.Read(ref _galleryHasIndexedImages) != 0)
            return;
        if (Dispatcher.HasShutdownStarted) return;
        if (System.Threading.Interlocked.Exchange(
            ref _catalogPublicationPending, 1) != 0) return;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Dispatcher.HasShutdownStarted || !_browseInitialized) return;
            if (_catalogPublicationTimer is null)
            {
                _catalogPublicationTimer = new DispatcherTimer(
                    DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(750)
                };
                _catalogPublicationTimer.Tick += CatalogPublicationTimer_Tick;
            }
            if (!_catalogPublicationTimer.IsEnabled)
                _catalogPublicationTimer.Start();
        }), DispatcherPriority.Background);
    }

    private async void CatalogPublicationTimer_Tick(object? sender, EventArgs e)
    {
        _catalogPublicationTimer?.Stop();
        if (_catalogPublicationBusy)
        {
            _catalogPublicationTimer?.Start();
            return;
        }
        if (System.Threading.Interlocked.Exchange(
            ref _catalogPublicationPending, 0) == 0) return;

        _catalogPublicationBusy = true;
        try
        {
            if (_browseInitialized && !_folderManagerOpen &&
                !_folderRemovalPending && !Dispatcher.HasShutdownStarted)
                await RefreshBrowseGalleryAsync(
                    forceIndexReload: true, warmViewport: false);
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.CatalogPublication", ex);
        }
        finally
        {
            _catalogPublicationBusy = false;
            if (System.Threading.Volatile.Read(ref _catalogPublicationPending) != 0 &&
                !Dispatcher.HasShutdownStarted)
                _catalogPublicationTimer?.Start();
        }
    }

    private bool _manualScanAllActive;

    private async void BrowseScanAll_Click(object sender, RoutedEventArgs e)
    {
        if (_manualScanAllActive || _folderRemovalPending)
            return;

        var roots = _dropTargetProjection.Select(entry => entry.Folder)
            .DistinctBy(folder => folder.Id).ToArray();
        if (roots.Length == 0)
        {
            MessageBox.Show(
                this,
                "Im Ordnermanager sind noch keine überwachten Ordner definiert.",
                "PCasa",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Scan All is ONE manual package, not a chain of independent priority
        // jobs. Every directory keeps its own resumable scanner checkpoint;
        // the package cursor advances only after that directory is complete.
        var key = $"catalog:scan-all:{Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource();
        var scans = new List<CompleteFolderScanStep>();
        var failures = new List<string>();
        var package = new SchedulerTaskPackage(key);
        _manualScanAllActive = true;
        _manualScanRunning = true;
        _manualFolderScanWaiting = true;
        _isScanning = true;
        _activeScanCancellation = cancellation;
        _activeScanFolderId = null;
        _cacheMaintenanceCancellation?.Cancel();
        _browseCacheCancellation?.Cancel();
        UpdateManualScanControls();
        BeginVisibleManualOperation(key, "Alle Ordner einlesen", 0);

        try
        {
            for (var index = 0; index < roots.Length; index++)
            {
                var root = roots[index];
                var position = index + 1;
                var display = new Progress<ScanProgress>(scan =>
                {
                    ShowScannedPhotoFolder(scan);
                    if (_visibleManualActiveKey == key)
                        StatusText.Text = $"Einlesen {position}/{roots.Length}: " +
                            $"{root.LocalPath} · {scan.ImageFilesSeen:N0} geprüft";
                });
                var report = new ScanJobProgress(_scheduler, key, display);
                var step = new CompleteFolderScanStep(this,
                    root.LocalPath, report, root.Id);
                scans.Add(step);
                package.AddStep(async token =>
                {
                    // The root may have been removed while this manual packet
                    // was paused beneath a newer one. Skip only that root.
                    if (!_folderRepository.GetAll().Any(folder => folder.Id == root.Id))
                    {
                        step.Dispose();
                        return;
                    }

                    try
                    {
                        await step.ExecuteAsync(token);
                        step.Dispose();
                    }
                    catch (OperationCanceledException) when (
                        step.SourceChanged ||
                        !_folderRepository.GetAll().Any(folder => folder.Id == root.Id))
                    {
                        // The newest folder-selection change supersedes only
                        // this root, not the remainder of Scan All.
                        step.Dispose();
                    }
                    catch (OperationCanceledException)
                    {
                        // Scheduler yield: retain the SAME scanner enumerator.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{root.LocalPath}: {ex.Message}");
                        PCasaDiagnostics.Write("Catalog.ScanAll", ex);
                        step.Dispose();
                    }
                });
            }

            await _scheduler.EnqueuePackageAsync(package,
                SchedulerPriority.ManualAction, cancellation.Token,
                workKind: SchedulerWorkKind.CatalogScan);
        }
        catch (OperationCanceledException) when (_folderRemovalPending ||
            cancellation.IsCancellationRequested)
        {
            StatusText.Text = "Gesamtscan abgebrochen · abgeschlossene Ordner bleiben katalogisiert";
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Catalog.ScanAll", ex);
            failures.Add(ex.Message);
        }
        finally
        {
            foreach (var scan in scans)
                scan.Dispose();
            EndVisibleManualOperation(key);
            if (ReferenceEquals(_activeScanCancellation, cancellation))
            {
                _activeScanCancellation = null;
                _activeScanFolderId = null;
            }
            _manualScanAllActive = false;
            _manualScanRunning = _initialScanActive || _manualContextScanActive;
            _manualFolderScanWaiting = _manualScanRunning;
            _isScanning = _backgroundWorkerRunning || _manualScanRunning;
            UpdateManualScanControls();
            QueuePendingInitialScans();
            if (_backgroundPending.Count > 0 &&
                _backgroundScanSettings?.Enabled == true)
                _backgroundDebounceTimer?.Start();
        }

        if (_folderRemovalPending)
            return;
        await EnsureBrowseCacheAndRefreshAsync();

        if (failures.Count > 0)
        {
            MessageBox.Show(
                this,
                "Einige Ordner konnten nicht eingelesen werden:\n\n" +
                    string.Join("\n", failures),
                "PCasa",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void BrowseFolderManager_Click(object sender, RoutedEventArgs e)
    {
        if (_folderManagerOpen || _folderManagerCommitPending) return;
        _folderManagerOpen = true;
        FolderManagerSelection? selection = null;
        try
        {
            // Opening the dialog is a scheduled catalog read. No WPF
            // constructor or checkbox may open a SQLite connection.
            var configuration = await _scheduler.RunAsync(
                $"catalog:folder-manager-configuration:{Guid.NewGuid():N}",
                SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return ReadFolderTreeConfigurationSnapshot();
                }, workKind: SchedulerWorkKind.CatalogRead);
            if (Dispatcher.HasShutdownStarted) return;
            var window = new FolderManagerWindow(
                configuration, _scheduler, serverDatabaseConfigured: false)
            {
                Owner = this
            };
            // No checkbox mutates SQLite. Cancel closes without side effects.
            if (window.ShowDialog() == true)
                selection = window.SelectedConfiguration;
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("FolderManager.Open", ex);
            MessageBox.Show(this, "Der Ordnermanager konnte nicht geöffnet werden.\n\n" +
                ex.Message, "PCasa", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            _folderManagerOpen = false;
        }

        if (selection is null) return; // Abbruch / X: zero persisted changes.

        var durableCommitted = false;
        const string operationKey = "folder-manager:commit-selection";
        _folderManagerCommitPending = true;
        BeginVisibleManualOperation(operationKey, "Ordnerauswahl übernehmen", 1);
        try
        {
            // One user operation owns the persisted decision, the updated
            // folder-tree snapshot AND publication of its catalog view.
            await _scheduler.EnqueueAsync(
                operationKey, SchedulerPriority.ManualAction,
                async token =>
                {
                    _scheduler.ReportProgress(operationKey,
                        "Ordnerauswahl übernehmen", 0, 1);
                    var modified = new FolderManagerSelectionService(_database)
                        .Apply(selection, token);
                    durableCommitted = true;
                    IReadOnlyList<(MonitoredFolder Folder, IReadOnlyList<string> ExcludedPaths)> snapshot =
                        Array.Empty<(MonitoredFolder, IReadOnlyList<string>)>();
                    if (modified)
                        snapshot = ReadFolderTreeConfigurationSnapshot();

                    // Preferences persist only after the catalog decision.
                    // No I/O is performed on the WPF dispatcher.
                    if (BackgroundScanSettings.Load().Enabled != selection.BackgroundScanEnabled)
                        new BackgroundScanSettings
                        {
                            Enabled = selection.BackgroundScanEnabled
                        }.Save();

                    if (modified)
                    {
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            _initialScanAttempted.Clear();
                            _dropTargetProjection = snapshot;
                            _dragTargetValidity.Clear();
                            RefreshFolderTree(preserveGalleryAnchor: true);
                            StatusText.Text =
                                "Ordnerauswahl übernommen · Einlesen mit Benutzerpriorität";
                            // Durable initial scans can enter their own
                            // scheduler lane, but cannot overtake this
                            // still-running manual catalog publication.
                            QueuePendingInitialScans();
                            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                                warmViewport: false);
                        }).Task.Unwrap();
                    }
                    _scheduler.ReportProgress(operationKey,
                        "Ordnerauswahl übernommen", 1, 1);
                });
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("FolderManager.Commit", ex);
            MessageBox.Show(this,
                durableCommitted
                    ? "Die Ordnerauswahl wurde gespeichert, aber die Aktualisierung " +
                      "der Ansicht oder Einstellungen ist fehlgeschlagen.\n\n" + ex.Message
                    : "Die Ordnerauswahl konnte nicht übernommen werden.\n\n" +
                      ex.Message,
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndVisibleManualOperation(operationKey);
            _folderManagerCommitPending = false;
        }
    }

    private void BrowseStatus_Click(object sender, MouseButtonEventArgs e)
    {
        _browseProgressWindow?.ShowFromStatus();
    }

    private void BrowseCleanupStatusChanged(string message)
        => StatusText.Text = message;

    private void OnFolderExclusionCommitted(long folderId, string path,
        bool excluded, IReadOnlyList<string>? committedRules = null)
    {
        // Stop any running scan of this monitored root before modifying its
        // stored index; without cancellation it could re-add excluded photos.
        if (_isScanning && _activeScanFolderId == folderId)
        {
            _activeScanCancellation?.Cancel();
        }

        // The scheduler has already read this committed snapshot. Do not
        // block WPF on a second SQLite read during publication.
        var rules = committedRules ?? _folderRepository.GetExcludedPaths(folderId);
        _backgroundExcludedPaths[folderId] = rules;
        _dropTargetProjection = _dropTargetProjection
            .Select(entry => entry.Folder.Id == folderId
                ? (entry.Folder, ExcludedPaths: rules)
                : entry)
            .ToArray();
        _dragTargetValidity.Clear();
        if (excluded)
        {
            ForgetFolderActivity(path);
            // Drop any stale watcher events queued while the path was included.
            foreach (var key in _backgroundPending.Keys.ToArray())
            {
                if (FolderExclusionRules.IsAtOrBelow(
                    _backgroundPending[key].Path, path))
                {
                    _backgroundPending.Remove(key);
                }
            }
        }
        else
        {
            // Re-inclusion must actively repopulate the folder, even if the
            // parent has already been monitored for months.
            QueueBackgroundChange(new BackgroundFolderChange(
                folderId, path,
                BackgroundFolderChangeKind.ScanDirectory,
                Recursive: true));
        }
    }

    private async void BrowseFoldersTree_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingFolderSelection ||
            e.NewValue is not TreeViewItem item ||
            item.Tag is not FolderTreeTag tag)
        {
            return;
        }

        MarkExplicitGalleryNavigation();
        _appearance.SelectedTreePath = tag.FullPath;
        SaveAppearance();

        // A deliberate click changes the active search root in the two
        // folder-scoped modes. The previous implementation captured it only
        // once, so after moving filtered pictures away ALL other tree clicks
        // appeared to do nothing because no folder had an offset in the
        // stale result set. The single existing search pipeline owns reflow.
        if (_searchWithinTree && !string.IsNullOrWhiteSpace(TagSearchBox?.Text))
        {
            CaptureTagSearchRoot();
            try { await ApplyTagFilterAsync(scrollToTop: true); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("Search.FolderSelection", ex);
                StatusText.Text = "Ordnerfilter konnte nicht aktualisiert werden.";
                return;
            }
            // The filtered result already presents this folder's content.
            // Do not start a second catalog refresh for an empty result.
            if (tag.PhotoFolderId is long filteredId &&
                BrowseSurface.GetFolderOffset(filteredId) is double filteredTop)
                BrowseScrollViewer.ScrollToVerticalOffset(filteredTop);
            return;
        }

        if (tag.PhotoFolderId is long photoFolderId)
            await NavigateToPhotoFolderAsync(item, photoFolderId);
    }

    private void FoldersTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_syncingFolderSelection || _rebuildingFolderTree) return;
        var source = e.OriginalSource as DependencyObject;
        if (source is null ||
            FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(source) is not null)
            return;
        var item = FindAncestor<TreeViewItem>(source);
        if (item?.IsSelected == true && item.Tag is FolderTreeTag { PhotoFolderId: long folderId })
        {
            MarkExplicitGalleryNavigation();
            _ = NavigateToPhotoFolderAsync(item, folderId);
        }
    }

    private async Task NavigateToPhotoFolderAsync(TreeViewItem item, long photoFolderId)
    {
            _activePhotoFolderId = photoFolderId;
            var offset = BrowseSurface.GetFolderOffset(photoFolderId);
            if (offset is null && _browseInitialized && !_folderRemovalPending)
            {
                // A newly committed folder can be selected while its parent
                // scan is still running. The gallery fetches its own current
                // catalog snapshot on USER navigation; this never restarts or
                // waits for the scanner, nor does it rebuild the folder tree.
                await RefreshBrowseGalleryAsync(forceIndexReload: true,
                    warmViewport: false);
                if ((FoldersTree.SelectedItem as TreeViewItem)?.Tag is not
                    FolderTreeTag { PhotoFolderId: long current } || current != photoFolderId)
                    return;
                offset = BrowseSurface.GetFolderOffset(photoFolderId);
            }
            if (offset is null && _appearance.HideCollapsedFolders &&
                string.IsNullOrWhiteSpace(TagSearchBox.Text))
            {
                // A hidden folder becomes navigable by opening its actual tree
                // ancestors, not by creating a permanent visibility override.
                _bulkFolderExpansion = true;
                try { FolderTreeNavigation.ExpandAncestors(item); }
                finally { _bulkFolderExpansion = false; }
                UpdateGalleryFolderVisibility(preserveGalleryAnchor: false);
                BrowseScrollViewer.UpdateLayout();
                offset = BrowseSurface.GetFolderOffset(photoFolderId);
            }

            // With a tag filter the folder may simply have no matching images.
            // Do not expose unrelated thumbnails in order to manufacture an
            // offset for a folder that has no search results.
            if (offset is not null)
            {
                BrowseScrollViewer.ScrollToVerticalOffset(offset.Value);
                UpdateActiveFolderDisplay(photoFolderId, updateTreeSelection: false);
            }
            else if (!string.IsNullOrWhiteSpace(TagSearchBox?.Text))
            {
                StatusText.Text = "Keine Filtertreffer in diesem Ordner. " +
                    "Suchbereich ändern oder Filter löschen.";
            }
    }

    // One wheel event moves the gallery exactly once. No acceleration,
    // inertia, accumulated target position, timer, or delayed scroll work.
    private void BrowseScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            return;

        const double pixelsPerWheelNotch = 96.0;
        var movement = e.Delta / 120.0 * pixelsPerWheelNotch;
        var target = Math.Clamp(
            BrowseScrollViewer.VerticalOffset - movement,
            0.0,
            BrowseScrollViewer.ScrollableHeight);
        BrowseScrollViewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    private void BrowseScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_browseInitialized)
        {
            return;
        }

        BrowseSurface.SetViewport(
            e.VerticalOffset,
            Math.Max(1, e.ViewportHeight));

        // A filtered search list and a sidebar resize must not change
        // the user's folder-tree focus. They change gallery geometry,
        // not the selected source folder.
        if (e.VerticalChange == 0 || _pendingGalleryAnchor is not null ||
            _restoringGalleryLayoutAnchor ||
            !string.IsNullOrWhiteSpace(TagSearchBox?.Text))
        {
            return;
        }

        var folderId = BrowseSurface.GetFolderIdAtOffset(e.VerticalOffset + 4);
        if (folderId is null || _activePhotoFolderId == folderId)
        {
            return;
        }

        _activePhotoFolderId = folderId;
        // Scrolling changes only which gallery section is visible. It must
        // NEVER select, expand or scroll the independent folder tree. Only a
        // deliberate thumbnail/folder click may change tree selection.
        UpdateActiveFolderDisplay(folderId.Value, updateTreeSelection: false);
    }

    private bool _thumbnailSizePreviewActive;
    private bool _thumbnailSizeFrameScheduled;

    private void ThumbnailSizeThumb_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        if (!_browseInitialized || _thumbnailSizePreviewActive) return;
        _thumbnailSizePreviewActive = true;
        _thumbnailSizeCancellation?.Cancel();
        ReflowSpacingPreservingScroll(BrowseSurface.BeginSizePreview);
    }

    private void ThumbnailSizeThumb_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (_thumbnailSizePreviewActive)
        {
            _thumbnailSizePreviewActive = false;
            ReflowSpacingPreservingScroll(BrowseSurface.EndSizePreview);
        }
        SaveFooterSettingsNow();
        ScheduleThumbnailTierRefresh();
    }

    private void BrowseThumbnailSizeSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_browseInitialized) return;
        _appearance.ThumbnailWidth = Math.Clamp(e.NewValue, 72, 600);
        if (!_thumbnailSizePreviewActive) ScheduleFooterSettingsSave();

        // Immediate reflow from already decoded pages. The cache tier is
        // NOT reloaded during mouse dragging. Limit layout to one WPF frame.
        if (!_thumbnailSizeFrameScheduled)
        {
            _thumbnailSizeFrameScheduled = true;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _thumbnailSizeFrameScheduled = false;
                if (!_browseInitialized || Dispatcher.HasShutdownStarted) return;
                ReflowSpacingPreservingScroll(() =>
                    BrowseSurface.SetThumbnailWidth(ThumbnailSizeSlider.Value));
            }), DispatcherPriority.Render);
        }
        if (!_thumbnailSizePreviewActive) ScheduleThumbnailTierRefresh();
    }

    private void ScheduleThumbnailTierRefresh()
    {
        _thumbnailSizeCancellation?.Cancel();
        _thumbnailSizeCancellation?.Dispose();
        _thumbnailSizeCancellation = new CancellationTokenSource();
        if (_thumbnailSizePreviewActive || _browseCacheBuilding) return;
        _ = RefreshThumbnailTierAfterIdleAsync(_thumbnailSizeCancellation.Token);
    }

    private async Task RefreshThumbnailTierAfterIdleAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(260, cancellation);
            if (cancellation.IsCancellationRequested || _thumbnailSizePreviewActive ||
                !_browseInitialized || _browseCacheBuilding ||
                _browseProfileRepository is null)
                return;

            var profile = await _scheduler.RunAsync(
                $"catalog:slider-profile:{Guid.NewGuid():N}",
                SchedulerPriority.Interactive,
                token => { token.ThrowIfCancellationRequested(); return _browseProfileRepository.Get(); },
                cancellationToken: cancellation,
                workKind: SchedulerWorkKind.CatalogRead);

            if (cancellation.IsCancellationRequested || _thumbnailSizePreviewActive)
                return;
            var nextTier = profile.ChooseForDisplayWidth(ThumbnailSizeSlider.Value);
            if (_activeBrowseTier != nextTier)
                await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PCasaDiagnostics.Write("Gallery.ThumbnailTierAfterIdle", ex); }
    }

    private void BrowseGalleryViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_browseInitialized)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            RestorePendingGalleryAnchor();
            BrowseSurface.SetViewport(
                BrowseScrollViewer.VerticalOffset,
                Math.Max(1, BrowseScrollViewer.ViewportHeight));
        }, DispatcherPriority.Loaded);
    }

    private readonly GallerySelection _gallerySelection = new();

    private void BrowseSurface_EmptyAreaClicked(object? sender, EventArgs e)
    {
        if (_viewerOpen || _selectedImageIds.Count == 0) return;
        _selectedImageIds.Clear();
        _gallerySelection.Reset();
        ApplyGallerySelection();
    }

    private void BrowseSurface_ImageClicked(
        object? sender,
        BrowseImageClickedEventArgs e)
    {
        _gallerySelection.Click(e.ImageId, e.Modifiers,
            _displayedBrowseIndex, _selectedImageIds, BrowseSurface.HiddenFolderIds);
        ApplyGallerySelection();

        // Explicit thumbnail selection identifies its source folder. Ordinary
        // scroll does NOT select a different folder in the independent tree.
        var selectedImage = _displayedBrowseIndex.FirstOrDefault(entry =>
            entry.ImageId == e.ImageId);
        if (selectedImage is not null)
        {
            _activePhotoFolderId = selectedImage.PhotoFolderId;
            UpdateActiveFolderDisplay(selectedImage.PhotoFolderId, updateTreeSelection: true);
        }
        Keyboard.Focus(BrowseSurface);
    }

    private void ApplyGallerySelection()
    {
        BrowseSurface.SetSelection(_selectedImageIds);
        SelectionText.Text = $"Auswahl: {_selectedImageIds.Count:N0}";
        UpdateComparisonButtons();
        _ = UpdateSidebarContentAsync();
    }

    private void BrowseWindow_Closed(object? sender, EventArgs e)
    {
        _appearance.ThumbnailWidth = ThumbnailSizeSlider.Value;
        SaveGalleryPosition();
        SaveAppearance();
        CatalogChanges.ImageCommitted -= OnCatalogImageCommitted;
        CatalogChanges.ImageIndexed -= OnCatalogImageIndexed;
        _catalogPublicationTimer?.Stop();
        _browseProgressWindow?.Finish();
        _browseProgressWindow = null;
        _thumbnailSizeCancellation?.Cancel();
        _thumbnailSizeCancellation?.Dispose();
        _browseRefreshRequests.Invalidate();
        _browseCacheCancellation?.Cancel();
        _browseCacheCancellation?.Dispose();
        StopTagWork();
        _browsePageCache?.Dispose();
    }
}
