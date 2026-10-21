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

        _photoFolders = _imageCatalog.GetPhotoFolders();
        RefreshFolderTree(preserveGalleryAnchor: false);

        var imageCount = _imageCatalog.CountAll();
        _lastCatalogImageCount = imageCount;
        EmptyPanel.Visibility = imageCount == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        _photoFolders = _imageCatalog.GetPhotoFolders();
        var imageCount = _imageCatalog.CountAll();
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
            StatusText.Text = $"Galerieindex {tier}px laden...";
            var index = await Task.Run(() => _browseCacheRepository.GetIndex(tier));
            if (!_browseRefreshRequests.IsCurrent(refresh))
            {
                return;
            }

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
            var existing = await Task.Run(
                () => _imageCatalog.GetExistingImageIds(checkedSelection));
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
            StatusText.Text = $"{imageCount:N0} Bilder";
        SelectionText.Text = $"Auswahl: {_selectedImageIds.Count:N0}";
    }

    private DateTime _lastImportPreviewUtc = DateTime.MinValue;
    private bool _importPreviewLoading;

    private async Task RefreshImportedPreviewAsync()
    {
        if (_importPreviewLoading || !_browseInitialized) return;
        _importPreviewLoading = true;
        try
        {
            await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false);
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Browse.ImportPreview", ex);
        }
        finally
        {
            _importPreviewLoading = false;
        }
    }

    private void QueueImportedPreview(ScanProgress scan)
    {
        if (scan.Added <= 0 || _importPreviewLoading ||
            DateTime.UtcNow - _lastImportPreviewUtc < TimeSpan.FromSeconds(3))
            return;
        _lastImportPreviewUtc = DateTime.UtcNow;
        _ = RefreshImportedPreviewAsync();
    }

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
                    QueueImportedPreview(scan);
                });

                try
                {
                    var sourceState = _folderRepository.GetSourceState(folder.CanonicalPath);
                    if (sourceState is not { DesiredPresent: true })
                        continue;
                    await _scheduler.EnqueueAsync(
                        $"scan-all:{folder.Id}:{sourceState.Value.Generation}",
                        SchedulerPriority.UserAction,
                        token =>
                        {
                            _folderScanner.Scan(folder, progress, token);
                            return Task.CompletedTask;
                        },
                        () => _folderRepository.IsCurrentSourceGeneration(
                            folder.CanonicalPath, sourceState.Value.Generation, true),
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

    private void BrowseFolderManager_Click(object sender, RoutedEventArgs e)
    {
        var window = new FolderManagerWindow(
            _folderRepository,
            serverDatabaseConfigured: false,
            removeFolderAsync: RemoveFolderFromPcasaAsync,
            scheduler: _scheduler,
            addFolderAsync: AddFolderFromManagerAsync)
        {
            Owner = this
        };

        window.CleanupStatusChanged += BrowseCleanupStatusChanged;
        window.FolderExclusionChanged += BrowseFolderManager_ExclusionChanged;
        // During a modal manager session, do not rebuild the owner's very
        // large photo tree / packed-gallery index after each checkbox edit.
        // That UI-thread work was responsible for the white/blank dialog.
        // A single refresh after closing reflects all completed changes.
        _folderManagerOpen = true;
        try
        {
            window.ShowDialog();
        }
        finally
        {
            _folderManagerOpen = false;
            window.CleanupStatusChanged -= BrowseCleanupStatusChanged;
            window.FolderExclusionChanged -= BrowseFolderManager_ExclusionChanged;
        }

        _ = EnsureBrowseCacheAndRefreshAsync();
    }

    private void BrowseStatus_Click(object sender, MouseButtonEventArgs e)
    {
        if (_browseProgressWindow is not { } window) return;
        if (!window.IsVisible) window.Show();
        window.Activate();
    }

    private void BrowseCleanupStatusChanged(string message)
        => StatusText.Text = message;

    private void BrowseFolderManager_ExclusionChanged(
        object? sender, FolderExclusionChangedEventArgs e)
    {
        // Stop any running scan of this monitored root before modifying its
        // stored index; without cancellation it could re-add excluded photos.
        if (_isScanning && _activeScanFolderId == e.FolderId)
        {
            _activeScanCancellation?.Cancel();
        }

        _backgroundExcludedPaths[e.FolderId] =
            _folderRepository.GetExcludedPaths(e.FolderId);
        if (e.Excluded)
        {
            ForgetFolderActivity(e.Path);
            // Drop any stale watcher events queued while the path was included.
            foreach (var key in _backgroundPending.Keys.ToArray())
            {
                if (FolderExclusionRules.IsAtOrBelow(
                    _backgroundPending[key].Path, e.Path))
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
                e.FolderId, e.Path,
                BackgroundFolderChangeKind.ScanDirectory,
                Recursive: true));
        }
    }

    private async void BrowseFolderManager_FoldersChanged(object? sender, EventArgs e)
    {
        // Folder removal already updated the owner's live view through the
        // common coordinator. Other manager edits are accumulated while the
        // modal dialog is open; one refresh on close is enough.
        if (_folderManagerOpen)
            return;

        if (_browseInitialized && !_browseCacheBuilding)
            await RefreshBrowseGalleryAsync(forceIndexReload: true);
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
        // Do not BringIntoView here. Picasa's folder pane must not shift horizontally
        // or chase the gallery while the user is rapidly browsing.
        UpdateActiveFolderDisplay(folderId.Value, updateTreeSelection: true);
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
