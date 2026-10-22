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
        BrowseSurface.ImageOpened += BrowseSurface_ImageOpened;
        BrowseSurface.ImageContextRequested += BrowseSurface_ImageContextRequested;
        BrowseSurface.LayoutReflowed += BrowseSurface_LayoutReflowed;
        Closed += BrowseWindow_Closed;

        // A persistent user Add must outrank all startup cache preparation.
        // Start its scheduler work BEFORE any background cache repair.
        QueuePendingInitialScans();
        await EnsureBrowseCacheAndRefreshAsync();
        StartTagBackfill();
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
        // The foreground initial import owns the status/progress display.
        // Never launch a competing startup cache build that can hide it.
        if (_manualScanRunning) return;
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

    private async Task RefreshBrowseGalleryAsync(
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
        RefreshFolderTree(preserveGalleryAnchor: false);

        if (imageCount == 0)
        {
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

    // No timer-based gallery reflows during scanning. The scanner reports
    // status/progress without replacing the current thumbnail layout. The
    // gallery/index is refreshed once when a scan operation completes.

    private async void BrowseScanAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning || _browseCacheBuilding || _folderRemovalPending)
        {
            return;
        }

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

        _cacheMaintenanceCancellation?.Cancel();
        while (_cacheMaintenanceRunning && !Dispatcher.HasShutdownStarted)
            await Task.Delay(40);
        if (_folderRemovalPending) return;

        _isScanning = true;
        ScanAllMenuItem.IsEnabled = false;
        var failures = new List<string>();
        var processedFolderIds = new HashSet<long>();

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
                _activeScanCancellation = cancellation;
                _activeScanFolderId = folder.Id;
                StatusText.Text = $"Einlesen: {folder.LocalPath}";

                var progress = new Progress<ScanProgress>(scan =>
                {
                    StatusText.Text =
                        $"Einlesen: {folder.LocalPath} · {scan.ImageFilesSeen:N0} Bilder · {scan.Failed:N0} Fehler";
                });

                try
                {
                    var scanGeneration = await Task.Run(
                        () => _folderRepository.BeginManualScan(folder.CanonicalPath));
                    await _scheduler.EnqueueAsync(
                        $"scan-all:{folder.Id}:{scanGeneration}",
                        SchedulerPriority.UserAction,
                        token =>
                        {
                            _folderScanner.Scan(folder, progress, token);
                            return Task.CompletedTask;
                        },
                        () => _folderRepository.IsCurrentSourceGeneration(
                            folder.CanonicalPath, scanGeneration, true),
                        cancellation.Token);
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
            _activeScanCancellation = null;
            _activeScanFolderId = null;
            _isScanning = false;
            ScanAllMenuItem.IsEnabled = true;
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

    private void BrowseFoldersTree_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingFolderSelection ||
            e.NewValue is not TreeViewItem item ||
            item.Tag is not FolderTreeTag tag)
        {
            return;
        }

        if (tag.PhotoFolderId is long photoFolderId)
        {
            _activePhotoFolderId = photoFolderId;
            var offset = BrowseSurface.GetFolderOffset(photoFolderId);
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

    private async void BrowseThumbnailSizeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_browseInitialized || _browseCacheBuilding)
        {
            return;
        }

        _thumbnailSizeCancellation?.Cancel();
        _thumbnailSizeCancellation?.Dispose();
        _thumbnailSizeCancellation = new CancellationTokenSource();
        var cancellation = _thumbnailSizeCancellation;

        try
        {
            await Task.Delay(120, cancellation.Token);
            if (cancellation.IsCancellationRequested || _browseProfileRepository is null)
            {
                return;
            }

            var profile = _browseProfileRepository.Get();
            var desiredTier = profile.ChooseForDisplayWidth(ThumbnailSizeSlider.Value);
            // Changing the thumbnail size must preserve the actual image,
            // not jump back to the beginning of its folder.
            QueueGalleryAnchorRestore();

            if (_activeBrowseTier != desiredTier)
            {
                await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: true);
            }
            else
            {
                BrowseSurface.SetThumbnailWidth(ThumbnailSizeSlider.Value);
                await Dispatcher.InvokeAsync(
                    () => BrowseScrollViewer.UpdateLayout(),
                    DispatcherPriority.Loaded);
            }

            BrowseScrollViewer.UpdateLayout();
            RestorePendingGalleryAnchor();

            BrowseSurface.SetViewport(
                BrowseScrollViewer.VerticalOffset,
                Math.Max(1, BrowseScrollViewer.ViewportHeight));
        }
        catch (OperationCanceledException)
        {
        }
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

    private void BrowseSurface_ImageClicked(
        object? sender,
        BrowseImageClickedEventArgs e)
    {
        var additive = (e.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        if (!additive)
        {
            _selectedImageIds.Clear();
        }

        if (additive && _selectedImageIds.Contains(e.ImageId))
        {
            _selectedImageIds.Remove(e.ImageId);
        }
        else
        {
            _selectedImageIds.Add(e.ImageId);
        }

        BrowseSurface.SetSelection(_selectedImageIds);

        // Clicking a thumbnail also identifies its folder in the tree, even
        // after scrolling far from the previously selected folder.
        var selectedImage = _browseIndex.FirstOrDefault(entry => entry.ImageId == e.ImageId);
        if (selectedImage is not null)
        {
            _activePhotoFolderId = selectedImage.PhotoFolderId;
            UpdateActiveFolderDisplay(selectedImage.PhotoFolderId, updateTreeSelection: true);
        }
        SelectionText.Text = $"Auswahl: {_selectedImageIds.Count:N0}";
        UpdateComparisonButtons();
        _ = UpdateSidebarContentAsync();
    }

    private void BrowseWindow_Closed(object? sender, EventArgs e)
    {
        SaveGalleryPosition();
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
