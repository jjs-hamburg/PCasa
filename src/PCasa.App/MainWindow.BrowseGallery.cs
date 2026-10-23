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
        Closed += BrowseWindow_Closed;

        // Recovery is a scheduler operation, not an alternative scanner.
        // It may perform slow SMB probes without blocking WPF startup.
        // If any durable move is recovered, the SAME gallery publication
        // owner will bring the new catalog state into the current view.
        _ = RecoverPendingFileTransfersAsync();

        // A persistent user Add must outrank all startup cache preparation.
        // Start its scheduler work BEFORE any background cache repair.
        QueuePendingInitialScans();
        await EnsureBrowseCacheAndRefreshAsync();
        StartTagBackfill();
    }

    private async Task RecoverPendingFileTransfersAsync()
    {
        try
        {
            var result = await _scheduler.RunAsync(
                "catalog:recover-file-transfers",
                SchedulerPriority.ManualAction,
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
        var imageCount = 0;
        await _scheduler.EnqueueAsync(
            $"catalog:initial-summary:{Guid.NewGuid():N}", SchedulerPriority.Interactive,
            token =>
            {
                token.ThrowIfCancellationRequested();
                initialFolders = _imageCatalog.GetPhotoFolders();
                imageCount = _imageCatalog.CountAll();
                return Task.CompletedTask;
            }, workKind: SchedulerWorkKind.CatalogRead);
        if (!_browseInitialized || _folderRemovalPending) return;
        _photoFolders = initialFolders;
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
        _photoFolders = freshFolders;
        _lastCatalogImageCount = imageCount;
        // A newly imported catalog folder may not yet have been reported by
        // incremental scan progress (notably Explorer drops). Add ONLY its
        // tree node before calculating gallery order; existing nodes remain
        // untouched, and gallery data is still loaded independently.
        foreach (var discovered in freshFolders)
        {
            if (_folderTreeItems.ContainsKey(discovered.Id)) continue;
            ShowScannedPhotoFolder(new ScanProgress(0, 0, 0, 0, null,
                DiscoveredPhotoFolderId: discovered.Id,
                DiscoveredPhotoFolderPath: discovered.LocalPath));
        }
        // Gallery catalog reloads are independent of the folder TreeView.
        // The tree has its own targeted additions and explicit refreshes;
        // rebuilding it here also invalidates the gallery's scroll geometry.
        UpdateGalleryFolderVisibility(preserveGalleryAnchor: false);

        if (imageCount == 0)
        {
            System.Threading.Volatile.Write(ref _galleryHasIndexedImages, 0);
            _browseIndex = Array.Empty<BrowseThumbnailIndexEntry>();
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

            // The visual folder tree determines Picasa gallery order.
            // Database canonical/UNC names are storage identity, not order.
            index = GalleryFolderOrdering.Order(index, CaptureGalleryFolderOrder());

            // The database may have been checked without any change to this
            // tier. Reusing the old index keeps decoded thumbnail pages alive.
            var indexChanged = _activeBrowseTier != tier ||
                _browseIndex.Count != index.Count ||
                !_browseIndex.SequenceEqual(index);
            if (indexChanged)
            {
                _browseIndex = index;
            }
            _activeBrowseTier = tier;
            if (_browseIndex.Count > 0)
                System.Threading.Volatile.Write(ref _galleryHasIndexedImages, 1);
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

    // The gallery listens to committed catalog data, NOT to any scanner,
    // progress window or folder-tree callback. The first stored picture
    // wakes an empty gallery immediately, without recurring reflows during
    // a long scan. The existing gallery stays fully interactive throughout.
    private int _galleryFirstImageSignalPending;
    private int _galleryHasIndexedImages;

    private void OnCatalogImageCommitted()
    {
        if (System.Threading.Volatile.Read(ref _galleryHasIndexedImages) != 0 ||
            System.Threading.Interlocked.Exchange(
                ref _galleryFirstImageSignalPending, 1) != 0)
            return;

        // The catalog publication can originate from a scheduler worker.
        // Only our own Gallery view is updated on WPF's dispatcher.
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (_browseInitialized && !_folderManagerOpen &&
                    !_folderRemovalPending && _browseIndex.Count == 0)
                    await RefreshBrowseGalleryAsync(forceIndexReload: true,
                        warmViewport: false);
            }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("Gallery.CatalogPublication", ex);
            }
            finally
            {
                System.Threading.Volatile.Write(ref _galleryFirstImageSignalPending, 0);
            }
        }), DispatcherPriority.Background);
    }

    private bool _manualScanAllActive;

    private async void BrowseScanAll_Click(object sender, RoutedEventArgs e)
    {
        if (_manualScanAllActive || _folderRemovalPending)
            return;

        if (_folderRepository.GetAll().Count == 0)
        {
            MessageBox.Show(
                this,
                "Im Ordnermanager sind noch keine überwachten Ordner definiert.",
                "PCasa",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Do not await background pack maintenance before submitting a
        // foreground operation. The scheduler preempts its idle lane.
        _cacheMaintenanceCancellation?.Cancel();
        _backgroundCurrentScanCancellation?.Cancel();
        _browseCacheCancellation?.Cancel();
        _manualScanAllActive = true;
        _manualScanRunning = true;
        _manualFolderScanWaiting = true;
        _isScanning = true;
        UpdateManualScanControls();
        var failures = new List<string>();
        var processedFolderIds = new HashSet<long>();
        CancellationTokenSource? ownedScanCancellation = null;

        try
        {
            while (!_folderRemovalPending)
            {
                var currentFolders = _folderRepository.GetAll();
                var folder = currentFolders.FirstOrDefault(
                    candidate => !processedFolderIds.Contains(candidate.Id));
                if (folder is null)
                {
                    break;
                }

                processedFolderIds.Add(folder.Id);
                using var cancellation = new CancellationTokenSource();
                ownedScanCancellation = cancellation;
                _activeScanCancellation = cancellation;
                _activeScanFolderId = folder.Id;
                StatusText.Text = $"Einlesen: {folder.LocalPath}";

                var progress = new Progress<ScanProgress>(scan =>
                {
                    ShowScannedPhotoFolder(scan);
                    StatusText.Text =
                        $"Einlesen: {folder.LocalPath} · {scan.ImageFilesSeen:N0} Bilder · {scan.Failed:N0} Fehler";
                });

                try
                {
                    await ScheduleCompleteFolderScanAsync(
                        folder.LocalPath, SchedulerPriority.ManualAction,
                        progress, cancellation.Token, folder.Id);
                }
                catch (OperationCanceledException)
                    when (_folderRemovalPending ||
                          !_folderRepository.GetAll().Any(candidate => candidate.Id == folder.Id))
                {
                }
                catch (Exception ex)
                {
                    if (!_folderRemovalPending)
                        failures.Add($"{folder.LocalPath}: {ex.Message}");
                }
                finally
                {
                    if (ReferenceEquals(_activeScanCancellation, cancellation))
                    {
                        _activeScanCancellation = null;
                        _activeScanFolderId = null;
                    }
                }
            }
        }
        finally
        {
            // Never clear an overlapping job's cancellation token.
            if (ReferenceEquals(_activeScanCancellation, ownedScanCancellation))
            {
                _activeScanCancellation = null;
                _activeScanFolderId = null;
            }
            _manualScanAllActive = false;
            _manualScanRunning = _initialScanActive || _manualContextScanActive;
            _manualFolderScanWaiting = _manualScanRunning;
            _isScanning = _backgroundWorkerRunning || _manualScanRunning;
            UpdateManualScanControls();
        }

        if (_folderRemovalPending)
            return;

        await EnsureBrowseCacheAndRefreshAsync();

        if (failures.Count > 0)
        {
            MessageBox.Show(
                this,
                "Einige Ordner konnten nicht eingelesen werden:\n\n" + string.Join("\n", failures),
                "PCasa",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void BrowseFolderManager_Click(object sender, RoutedEventArgs e)
    {
        if (_folderManagerOpen) return;
        var window = new FolderManagerWindow(
            _folderRepository,
            _scheduler,
            serverDatabaseConfigured: false)
        {
            Owner = this
        };

        _folderManagerOpen = true;
        FolderManagerSelection? selection = null;
        try
        {
            // No UI checkbox may save to SQLite, cancel an active scan or
            // launch an import. The user makes ONE decision on OK.
            if (window.ShowDialog() == true)
                selection = window.SelectedConfiguration;
        }
        finally
        {
            _folderManagerOpen = false;
        }

        if (selection is null) return; // Abbruch / X: zero persisted changes.

        try
        {
            var modified = false;
            await _scheduler.EnqueueAsync(
                "folder-manager:commit-selection",
                SchedulerPriority.Interactive,
                token =>
                {
                    modified = new FolderManagerSelectionService(_database)
                        .Apply(selection, token);
                    return Task.CompletedTask;
                });

            // Settings are saved only after the catalog transaction commits.
            // An unrelated menu or dialog never cancels manual scan work.
            if (BackgroundScanSettings.Load().Enabled != selection.BackgroundScanEnabled)
                new BackgroundScanSettings { Enabled = selection.BackgroundScanEnabled }.Save();

            if (modified)
            {
                _initialScanAttempted.Clear();
                RefreshFolderTree(preserveGalleryAnchor: true);
                StatusText.Text = "Ordnerauswahl übernommen · Einlesen mit Benutzerpriorität";
                // The scanner is a SINGLE durable user-priority scheduler
                // operation and is launched only AFTER this dialog closes.
                QueuePendingInitialScans();
                await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false);
            }
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("FolderManager.Commit", ex);
            MessageBox.Show(this,
                "Die Ordnerauswahl konnte nicht übernommen werden.\n\n" + ex.Message,
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseStatus_Click(object sender, MouseButtonEventArgs e)
    {
        if (_browseProgressWindow is not { } window) return;
        if (!window.IsVisible) window.Show();
        window.Activate();
    }

    private void BrowseCleanupStatusChanged(string message)
        => StatusText.Text = message;

    private void OnFolderExclusionCommitted(long folderId, string path, bool excluded)
    {
        // Stop any running scan of this monitored root before modifying its
        // stored index; without cancellation it could re-add excluded photos.
        if (_isScanning && _activeScanFolderId == folderId)
        {
            _activeScanCancellation?.Cancel();
        }

        _backgroundExcludedPaths[folderId] =
            _folderRepository.GetExcludedPaths(folderId);
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
            _ = NavigateToPhotoFolderAsync(item, folderId);
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
