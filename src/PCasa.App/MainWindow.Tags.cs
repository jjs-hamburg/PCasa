using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private IReadOnlyList<BrowseThumbnailIndexEntry> _displayedBrowseIndex =
        Array.Empty<BrowseThumbnailIndexEntry>();
    private CancellationTokenSource? _tagSearchCancellation;
    private CancellationTokenSource? _tagBackfillCancellation;
    private int _tagSearchVersion;
    private string _lastAppliedTagQuery = string.Empty;
    private bool _lastAppliedGpsAreaFilter;
    private bool _searchWithinTree;
    private bool _searchCurrentFolderOnly;
    private bool _searchFileNames;
    private bool _invertFilterResults;
    private string? _searchRootPath;

    private void StartTagBackfill()
    {
        if (_tagBackfillCancellation is not null) return;
        _tagBackfillCancellation = new CancellationTokenSource();
        var cancellation = _tagBackfillCancellation.Token;
        var repository = new ImageTagRepository(_database);
        _ = RunTagBackfillAsync(repository, cancellation);

        async Task RunTagBackfillAsync(ImageTagRepository tagRepository, CancellationToken token)
        {
            try
            {
                await _scheduler.EnqueueAsync("tags:backfill", SchedulerPriority.Background,
                    schedulerToken =>
                    {
                        tagRepository.IndexMissing(schedulerToken, count =>
                {
                        if (count % 512 != 0 || Dispatcher.HasShutdownStarted) return;
                        _ = Dispatcher.BeginInvoke(() =>
                            TagSearchBox.ToolTip = UiLanguage.Format("Tags suchen – {0:N0} bestehende Bilder geprüft", count));
                    });
                        return Task.CompletedTask;
                    }, cancellationToken: token);
                if (!Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        TagSearchBox.ToolTip = UiLanguage.T("Tags suchen (SQLite-Index)");
                        // An already visible search must pick up keywords indexed
                        // after its first query; do not wait for another keystroke.
                        if (!_browseInitialized || _browsePageCache is null ||
                            string.IsNullOrWhiteSpace(TagSearchBox.Text)) return;
                        try { await ApplyTagFilterAsync(); }
                        catch (Exception ex) { PCasaDiagnostics.Write("Tags.BackfillRefresh", ex); }
                    }));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PCasaDiagnostics.Write("Tags.Backfill", ex); }
        }
    }

    private async void TagSearchScope_Changed(object sender, RoutedEventArgs e)
    {
        if (SearchTreeScope is null || SearchAllScope is null ||
            SearchCurrentScope is null) return;
        _searchWithinTree = SearchTreeScope.IsChecked == true ||
            SearchCurrentScope.IsChecked == true;
        _searchCurrentFolderOnly = SearchCurrentScope.IsChecked == true;
        if (_searchWithinTree)
            CaptureTagSearchRoot();
        if (TagSearchBox is null || _browsePageCache is null)
            return;
        try
        {
            await ApplyTagFilterAsync(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A temporary database error must not escape an async void WPF
            // event and terminate the entire application.
            PCasaDiagnostics.Write("Tags.SearchScope", ex);
            StatusText.Text = UiLanguage.T("Tag-Suche fehlgeschlagen");
        }
    }

    private async void SearchMode_Changed(object sender, RoutedEventArgs e)
    {
        if (SearchFileNameMode is null || TagSearchBox is null) return;
        _searchFileNames = SearchFileNameMode.IsChecked == true;
        TagSearchBox.ToolTip = _searchFileNames ? UiLanguage.T("Dateinamen suchen") : UiLanguage.T("Tags suchen");
        if (_browsePageCache is null) return;
        try { await ApplyTagFilterAsync(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PCasaDiagnostics.Write("Search.Mode", ex); }
    }

    private async void FilterResultsInversion_Changed(object sender, RoutedEventArgs e)
    {
        if (InvertFilterToggle is null) return;
        _invertFilterResults = InvertFilterToggle.IsChecked == true;
        if (_browsePageCache is null || string.IsNullOrWhiteSpace(TagSearchBox.Text)) return;
        try { await ApplyTagFilterAsync(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PCasaDiagnostics.Write("Search.InvertResults", ex); }
    }

    private async void GalleryViewFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (GalleryViewFilterToggle is null) return;
        _appearance.SearchGalleryViewOnly = GalleryViewFilterToggle.IsChecked == true;
        SaveAppearance();
        if (_browsePageCache is null || TagSearchBox is null ||
            string.IsNullOrWhiteSpace(TagSearchBox.Text)) return;
        try { await ApplyTagFilterAsync(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PCasaDiagnostics.Write("Search.GalleryView", ex); }
    }

    private void ClearTagSearch_Click(object sender, RoutedEventArgs e)
    {
        TagSearchBox.Clear();
        TagSearchBox.Focus();
    }

    private async void TagSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // NOT is a persistent filter mode, not an action. Clearing a query
        // makes all images visible but must not silently uncheck the toggle.
        ClearTagSearchButton.Visibility = string.IsNullOrEmpty(TagSearchBox.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        if (!_browseInitialized || _browsePageCache is null)
            return;
        _tagSearchCancellation?.Cancel();
        _tagSearchCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _tagSearchCancellation = cancellation;
        try
        {
            await Task.Delay(180, cancellation.Token);
            await ApplyTagFilterAsync(true, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Tags.Search", ex);
            StatusText.Text = UiLanguage.T("Tag-Suche fehlgeschlagen");
        }
    }

    private async Task ApplyTagFilterAsync(
        bool scrollToTop = false,
        CancellationToken cancellationToken = default)
    {
        if (_browsePageCache is null) return;
        var version = ++_tagSearchVersion;
        var query = TagSearchBox.Text.Trim();
        HashSet<long>? imageIds = null;
        if (query.Length > 0 && !_searchFileNames)
        {
            imageIds = await _scheduler.RunAsync(
                $"catalog:tag-search:{version}", SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return new ImageTagRepository(_database).SearchImageIds(query);
                }, () => version == _tagSearchVersion,
                cancellationToken: cancellationToken,
                workKind: SchedulerWorkKind.CatalogRead);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (version != _tagSearchVersion ||
            !string.Equals(query, TagSearchBox.Text.Trim(), StringComparison.Ordinal))
            return;

        if (_searchWithinTree && (_searchRootPath is null ||
            (_lastAppliedTagQuery.Length == 0 && query.Length > 0)))
            CaptureTagSearchRoot();

        // Store the normal-gallery anchor once, before entering search mode.
        // Do not overwrite it as the user changes or scrolls search results.
        var wasFiltered = _lastAppliedTagQuery.Length > 0 || _lastAppliedGpsAreaFilter;
        var nowFiltered = query.Length > 0 || _gpsAreaFilterIds is not null;
        var enteringSearch = !wasFiltered && nowFiltered;
        var leavingSearch = wasFiltered && !nowFiltered;
        if (enteringSearch)
            _tagSearchNavigation.Begin(new GallerySearchNavigationSession.Snapshot(
                CaptureGalleryAnchor(),
                _activePhotoFolderId,
                (FoldersTree.SelectedItem as TreeViewItem)?.Tag is FolderTreeTag selected
                    ? selected.FullPath : null));

        // The chosen scope is the universe. Inverting search results
        // complements the match WITHIN that universe, never all folders.
        IReadOnlyList<BrowseThumbnailIndexEntry> scope = _browseIndex;
        if (query.Length > 0 && _searchWithinTree)
        {
            if (_searchRootPath is string rootPath)
            {
                var included = _photoFolders.Where(folder =>
                    (_searchCurrentFolderOnly
                        ? string.Equals(FolderExclusionRules.Normalize(folder.LocalPath),
                            FolderExclusionRules.Normalize(rootPath),
                            StringComparison.OrdinalIgnoreCase)
                        : IsPathWithinTagSearchRoot(folder.CanonicalPath, rootPath) ||
                          IsPathWithinTagSearchRoot(folder.LocalPath, rootPath)))
                    .Select(folder => folder.Id).ToHashSet();
                scope = _browseIndex.Where(image =>
                    included.Contains(image.PhotoFolderId)).ToArray();
            }
            else
                scope = Array.Empty<BrowseThumbnailIndexEntry>();
        }

        // GPS bounds are one criterion of the existing gallery filter,
        // not an independent list, renderer or thumbnail ownership.
        if (_gpsAreaFilterIds is { } area)
            scope = scope.Where(image => area.Contains(image.ImageId)).ToArray();

        // The selected WO? scope is narrowed to the folders which would
        // actually be displayed by the ordinary gallery (including its
        // "Eingeklappte Ordner ausblenden" option). NOT complements only
        // within this resulting universe, not the entire catalog.
        if (query.Length > 0 && GalleryViewFilterToggle.IsChecked == true)
        {
            var hiddenGalleryFolders = GetGalleryHiddenFolderIds();
            if (hiddenGalleryFolders.Count > 0)
                scope = scope.Where(image =>
                    !hiddenGalleryFolders.Contains(image.PhotoFolderId)).ToArray();
        }

        IReadOnlyList<BrowseThumbnailIndexEntry> desired =
            query.Length == 0 ? scope :
            _searchFileNames
                ? scope.Where(image => image.FileName.Contains(
                    query, StringComparison.OrdinalIgnoreCase)).ToArray()
                : scope.Where(image => imageIds!.Contains(image.ImageId)).ToArray();
        if (query.Length > 0 && _invertFilterResults)
        {
            var matchingIds = desired.Select(image => image.ImageId).ToHashSet();
            desired = scope.Where(image => !matchingIds.Contains(image.ImageId)).ToArray();
        }
        if (_displayedBrowseIndex.Count != desired.Count ||
            !_displayedBrowseIndex.SequenceEqual(desired))
        {
            _displayedBrowseIndex = desired;
            _browsePageCache.SetIndex(desired);
        }
        BrowseSurface.SetData(_displayedBrowseIndex, _photoFolders, _browsePageCache);
        // Ordinary search still ignores collapsed folders. The new optional
        // Galerieansicht mode limits the scope above, using the same
        // gallery mask; it never mutates the user's folder expansion state.
        UpdateGalleryFolderVisibility(preserveGalleryAnchor: false);
        BrowseSurface.SetSelection(_selectedImageIds);
        EmptyPanel.Visibility = desired.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanelHeading.Text = query.Length == 0
            ? UiLanguage.T("Noch keine Bilder im Katalog") :
                _invertFilterResults ? UiLanguage.T("Keine übrigen Bilder gefunden") :
                _searchFileNames ? UiLanguage.T("Keine passenden Dateinamen gefunden") : UiLanguage.T("Keine passenden Tags gefunden");
        EmptyPanelDescription.Text = query.Length == 0
            ? UiLanguage.T("Ordner im Menü einlesen, um die Galerie zu füllen.")
            : _searchFileNames ? UiLanguage.T("Gesucht wird in den katalogisierten Dateinamen.")
                : UiLanguage.T("Gesucht wird im lokalen Tag-Index. Bei der ersten Nutzung werden Tags noch im Hintergrund erfasst.");
        var searchStatus = query.Length > 0
            ? UiLanguage.Format("{0:N0} Bilder {1} {2}: {3}", desired.Count, _invertFilterResults ? UiLanguage.T("ohne") : UiLanguage.T("mit"), _searchFileNames ? UiLanguage.T("Dateiname") : UiLanguage.T("Tags"), query)
            : UiLanguage.Format("{0:N0} Bilder", _lastCatalogImageCount);
        // A search refresh is still allowed while Tag files are being
        // written. The manual progress presenter owns the status bar for the
        // duration of that work, so a late debounced search update must not
        // silently replace its live overall count with "24 Bilder".
        StatusText.Text = _visibleManualWindow is { IsManualPresentationActive: true } manual
            ? manual.ProgressStatusText : searchStatus;
        _lastAppliedTagQuery = query;
        _lastAppliedGpsAreaFilter = _gpsAreaFilterIds is not null;
        BrowseScrollViewer.UpdateLayout();
        if (leavingSearch)
        {
            if (_tagSearchNavigation.End() is { } original)
                RestoreTagSearchNavigation(original);
        }
        else if (scrollToTop && query.Length > 0)
            BrowseScrollViewer.ScrollToTop();
        BrowseSurface.SetViewport(BrowseScrollViewer.VerticalOffset,
            Math.Max(1, BrowseScrollViewer.ViewportHeight));
    }

    private static bool IsPathWithinTagSearchRoot(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
            return false;
        var cleanRoot = root.Replace('/', '\\').TrimEnd('\\');
        var cleanCandidate = candidate.Replace('/', '\\').TrimEnd('\\');
        return cleanCandidate.Equals(cleanRoot, StringComparison.OrdinalIgnoreCase) ||
            cleanCandidate.StartsWith(cleanRoot + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private void CaptureTagSearchRoot()
    {
        if (FoldersTree?.SelectedItem is TreeViewItem item &&
            item.Tag is PCasa.App.Models.FolderTreeTag folder)
            _searchRootPath = folder.FullPath;
        else if (_activePhotoFolderId is long id)
            _searchRootPath = _photoFolders.FirstOrDefault(folder => folder.Id == id)?.CanonicalPath;
        else
            _searchRootPath = null;
    }

    private int _tagSidebarRefreshVersion;
    private long[] _tagSidebarCountImageIds = Array.Empty<long>();
    private Dictionary<long, HashSet<string>> _tagSidebarOriginalTags = new();

    private async Task RefreshTagSidebarAsync()
    {
        var revision = ++_tagSidebarRefreshVersion;
        var selected = _selectedImageIds.ToArray();
        // Selection only changes the editor's projection; uncommitted edits
        // for other photos (and other folders) remain in the ONE draft.
        TagSelectionCaption.Text = UiLanguage.Format("Tags der Auswahl ({0} Bilder)", selected.Length);
        RefreshTagDraftControls();
        TagRowsPanel.Children.Clear();
        if (selected.Length == 0)
        {
            TagRowsPanel.Children.Add(new TextBlock { Text = UiLanguage.T("Bitte Bilder auswählen."),
                Margin = new Thickness(5), TextWrapping = TextWrapping.Wrap });
            return;
        }
        try
        {
            var counts = await _scheduler.RunAsync(
                $"catalog:tag-sidebar:{revision}", SchedulerPriority.Interactive,
                token =>
            {
                token.ThrowIfCancellationRequested();
                var repository = new ImageTagRepository(_database);
                var original = new Dictionary<long, HashSet<string>>();
                foreach (var id in selected)
                {
                    token.ThrowIfCancellationRequested();
                    var value = repository.GetKeywords(id) ?? string.Empty;
                    original[id] = value.Split(new[] { (char)10, (char)13 },
                        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                return original;
            }, () => revision == _tagSidebarRefreshVersion,
                workKind: SchedulerWorkKind.CatalogRead);
            if (revision != _tagSidebarRefreshVersion || _sidebarMode != "Tags") return;
            _tagSidebarCountImageIds = selected.OrderBy(id => id).ToArray();
            _tagSidebarOriginalTags = counts;
            RenderTagRows();
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Tags.Sidebar", ex);
            if (revision == _tagSidebarRefreshVersion)
                TagRowsPanel.Children.Add(new TextBlock { Text = UiLanguage.T("Tags konnten nicht gelesen werden.") });
        }
    }

    private void RenderTagRows()
    {
        if (TagRowsPanel is null) return;
        var selected = _selectedImageIds.OrderBy(id => id).ToArray();
        if (!_tagSidebarCountImageIds.SequenceEqual(selected)) return;
        var projected = _tagDraft.ProjectCounts(selected, _tagSidebarOriginalTags);
        TagRowsPanel.Children.Clear();
        var allOnly = TagLogicAnd.IsChecked == true;
        var filtered = projected
            .Where(pair => !allOnly || pair.Value == selected.Length)
            .OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        foreach (var pair in filtered)
        {
            var row = new DockPanel { Margin = new Thickness(3, 5, 3, 5) };
            var deleteGlyph = new ContentControl
            {
                Width = 20,
                Height = 20,
                Template = (ControlTemplate)Application.Current.FindResource(
                    "PcasaDeleteGlyphTemplate")
            };
            var remove = new Button
            {
                Content = deleteGlyph,
                ToolTip = UiLanguage.T("Tag aus allen ausgewählten Bildern entfernen"),
                Width = 22,
                Height = 22,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            var template = new ControlTemplate(typeof(Button));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty,
                new System.Windows.TemplateBindingExtension(
                    System.Windows.Controls.ContentControl.ContentProperty));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            template.VisualTree = presenter;
            remove.Template = template;
            remove.Click += (_, _) => StageTagChange(pair.Key, remove: true);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock {
                Text = $"🏷  {pair.Key}  [{pair.Value}]",
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = pair.Key
            });
            TagRowsPanel.Children.Add(row);
        }
    }

    private async void TagLogic_Changed(object sender, RoutedEventArgs e)
    {
        if (TagRowsPanel is not null && _sidebarMode == "Tags")
            await RefreshTagSidebarAsync();
    }

    private bool _tagWriteActive;

    private int _quickTagRefreshVersion;

    private async void RefreshQuickTags()
    {
        var revision = ++_quickTagRefreshVersion;
        IReadOnlyList<string> tags;
        try
        {
            tags = await _scheduler.RunAsync(
                $"catalog:quick-tags:{revision}", SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return new ImageTagRepository(_database).GetRecentTags();
                }, () => revision == _quickTagRefreshVersion,
                workKind: SchedulerWorkKind.CatalogRead);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { PCasaDiagnostics.Write("Tags.Quick", ex); return; }
        if (revision != _quickTagRefreshVersion || _sidebarMode != "Tags") return;
        TagQuickPanel.Children.Clear();
        foreach (var tag in tags)
        {
            var button = new Button {
                Content = tag, ToolTip = UiLanguage.Format("Tag '{0}' allen ausgewählten Bildern hinzufügen", tag),
                Margin = new Thickness(2), Padding = new Thickness(6, 3, 6, 3),
                Style = (Style)FindResource("PcasaStatusButtonStyle")
            };
            button.Click += (_, _) => StageTagChange(tag, remove: false);
            TagQuickPanel.Children.Add(button);
        }
    }

    private bool _tagRecoveryQueued;
    // V1 remains registered for durable packages written by older PCasa versions.
    // New recoveries use a SINGLE replayable step, with pending_tag_edits as truth.
    private const string TagRecoveryType = "tag-recovery-v2";

    // An immutable ordered image-ID snapshot is the package recipe. The
    // pending_tag_edits table remains the ONLY durable user-intent queue.
    // Replaying a step reads CURRENT revisions, so new tag intents always win.
    private SchedulerTaskPackage BuildTagRecoveryPackage(string key, string payload)
    {
        var ids = System.Text.Json.JsonSerializer.Deserialize<long[]>(payload)
            ?? throw new InvalidDataException("Ungültiger Wiederherstellungsauftrag.");
        var package = new SchedulerTaskPackage(key);
        for (var i = 0; i < ids.Length; i++)
        {
            var imageId = ids[i];
            var completed = i + 1;
            package.AddStep(token =>
            {
                token.ThrowIfCancellationRequested();
                var repo = new ImageTagRepository(_database);
                var edits = repo.PendingEditsForImage(imageId);
                if (edits.Count > 0)
                {
                    TryWriteOriginal(OriginalWriteFailureRepository.Tags,
                        key, imageId, GetOriginalImagePath(imageId), token, () =>
                    {
                        using var writer = new ExifToolKeywordWriter.Session();
                        repo.ApplyPendingImageEdits(imageId, edits, writer, token,
                            path => _scheduler.ReportProgress(key,
                                "Tags in Originaldateien schreiben",
                                completed - 1, ids.Length, path));
                    });
                }
                _scheduler.ReportProgress(key, "Tags wiederherstellen",
                    completed, ids.Length);
                if (!Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                        _ = RefreshPendingTagDotsAsync()));
                return Task.CompletedTask;
            });
        }
        return package;
    }

    // One immutable scheduler step, not one cursor step per image: every
    // successful file write removes only its confirmed revisions from
    // pending_tag_edits. If a NAS image is unavailable, a later replay starts
    // from that authoritative backlog without revisiting confirmed writes.
    // A failed image never becomes a completed scheduler step.
    private SchedulerTaskPackage BuildTagRecoveryPackageV2(string key, string payload)
    {
        if (payload != "{}")
            throw new InvalidDataException("Ungültiger Tag-Wiederherstellungsauftrag.");
        var package = new SchedulerTaskPackage(key);
        package.AddStep(token =>
        {
            token.ThrowIfCancellationRequested();
            var repo = new ImageTagRepository(_database);
            // Failed originals are parked for deliberate review. They stay
            // pending in SQLite, but must never spin through P0 recovery.
            var errorStore = _originalWriteFailures;
            var parked = errorStore.Unresolved()
                .Where(error => error.Kind == OriginalWriteFailureRepository.Tags)
                .Select(error => error.ImageId).ToHashSet();
            var images = repo.PendingImagePaths()
                .Where(image => !parked.Contains(image.ImageId)).ToArray();
            var failures = new List<string>();
            var unreachableRoots = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var confirmed = 0;
            _scheduler.ReportProgress(key, "Tags in Originaldateien schreiben",
                0, images.Length);
            foreach (var (imageId, originalPath) in images)
            {
                token.ThrowIfCancellationRequested();
                var root = Path.GetPathRoot(originalPath) ?? originalPath;
                // A single offline mapped NAS/share must not prevent the
                // remaining reachable originals (notably C:) from writing.
                // This is only a per-attempt shortcut: the scheduler alone
                // owns the durable retry and unchanged P0 priority.
                if (unreachableRoots.Contains(root))
                    continue;
                if (!_scheduler.SourceAvailability.CanAccess(originalPath, token))
                {
                    if (unreachableRoots.Add(root))
                        failures.Add("Quelle nicht erreichbar: " + root);
                    continue;
                }
                try
                {
                    var edits = repo.PendingEditsForImage(imageId);
                    if (edits.Count > 0)
                    {
                        TryWriteOriginal(OriginalWriteFailureRepository.Tags,
                            key, imageId, originalPath, token, () =>
                        {
                            using var writer = new ExifToolKeywordWriter.Session();
                            repo.ApplyPendingImageEdits(imageId, edits, writer, token,
                                path => _scheduler.ReportProgress(key,
                                    "Tags in Originaldateien schreiben",
                                    confirmed, images.Length, path));
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Never retire the durable job as Completed after an
                    // unsuccessful original write. Keep its original P0
                    // priority and pending DB intents for the next recovery.
                    failures.Add(imageId + ": " + ex.Message);
                    PCasaDiagnostics.Write("Tags.RecoveryWrite", ex);
                    if (ex is IOException or UnauthorizedAccessException &&
                        !_scheduler.SourceAvailability.CheckAfterSourceError(
                            originalPath, token) && unreachableRoots.Add(root))
                        PCasaDiagnostics.WriteMessage("Tags.RecoveryOfflineRoot",
                            $"Source={root}; remaining originals on this source " +
                            "skipped until the common scheduler gate retries.");
                }
                // Count only originals whose pending revisions have been
                // confirmed and removed from SQLite, not attempted writes.
                // An unreachable NAS file must never make the global counter
                // appear complete while its red pending marker remains.
                if (repo.PendingEditsForImage(imageId).Count == 0)
                    confirmed++;
                _scheduler.ReportProgress(key, "Tags wiederherstellen",
                    confirmed, images.Length);
                if (!Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                        _ = RefreshPendingTagDotsAsync()));
            }
            var parkedAfter = errorStore.Unresolved()
                .Where(error => error.Kind == OriginalWriteFailureRepository.Tags)
                .Select(error => error.ImageId).ToHashSet();
            var remaining = repo.PendingImagePaths()
                .Any(pending => !parkedAfter.Contains(pending.ImageId));
            if (remaining)
            {
                _scheduler.ReportProgress(key,
                    "Tag-Schreibfehler – Aufträge bleiben vorgemerkt",
                    confirmed, images.Length);
                throw new DurablePackageRetryException(
                    "Tag-Schreibaufträge weiterhin offen (" +
                    failures.Count + " Fehler). " +
                    string.Join("; ", failures.Take(3)));
            }
            return Task.CompletedTask;
        });
        return package;
    }

    // The scheduler journal provides identity/order. This is ONLY a WPF
    // projection of recovered user work, registered before the workers start
    // restored packages. The presenter does not own or execute the jobs.
    private readonly HashSet<string> _recoveredTagPresentationKeys =
        new(StringComparer.Ordinal);

    private void RestoreVisiblePendingTagPackages()
        => RestoreVisiblePendingTagPackagesCore(null);

    private void RestoreVisiblePendingTagPackage(string key)
        => RestoreVisiblePendingTagPackagesCore(key);

    private void RestoreVisiblePendingTagPackagesCore(string? onlyKey)
    {
        var pending = new DurablePackageJournal(_database).Pending()
            .Where(p => p.State != "deferred" &&
                (p.Type is "tag-edit-v1" or "tag-edit-v2" or
                    "tag-recovery-v1" or "tag-recovery-v2") &&
                (onlyKey is null || p.Key == onlyKey))
            .ToArray();
        if (pending.Length == 0) return;
        // The OLD UI multiplied the entire database backlog by the number
        // of recovered packages (e.g. 1,000 pending photos * 50 packages =
        // "50,000 writes"). A package's immutable recipe, NOT the global
        // pending table, defines that package's file-write steps.
        // Two genuinely separate packages for one image count twice.
        var pendingImageCount = new ImageTagRepository(_database)
            .PendingEdits(int.MaxValue).Select(p => p.ImageId).Distinct().Count();
        foreach (var record in pending)
        {
            int total;
            int confirmed;
            try
            {
                if (record.Type is "tag-edit-v1" or "tag-edit-v2")
                {
                    var recipe = System.Text.Json.JsonSerializer
                        .Deserialize<DurableTagEdit>(record.Payload)
                        ?? throw new InvalidDataException("Ungültiges Tag-Paket.");
                    total = recipe.ImageIds.Length;
                    // First persisted step commits SQLite, not a file write.
                    confirmed = Math.Clamp(record.CompletedSteps - 1, 0, total);
                }
                else if (record.Type == "tag-recovery-v1")
                {
                    total = System.Text.Json.JsonSerializer
                        .Deserialize<long[]>(record.Payload)?.Length
                        ?? throw new InvalidDataException("Ungültiges altes Tag-Paket.");
                    confirmed = Math.Clamp(record.CompletedSteps, 0, total);
                }
                else
                {
                    total = pendingImageCount;
                    confirmed = 0;
                }
            }
            catch (Exception ex)
            {
                // An unreadable recipe must remain in the durable scheduler
                // journal, but cannot justify an invented progress number.
                PCasaDiagnostics.Write("Tags.ProgressRecipe", ex);
                total = 0;
                confirmed = 0;
            }
            _recoveredTagPresentationKeys.Add(record.Key);
            BeginVisibleManualOperation(record.Key,
                "Tags in Originaldateien schreiben", total);
            SeedVisibleTagSeriesCheckpoint(record.Key, confirmed);
        }
    }

    private async Task ObserveRecoveredTagOperationAsync(Task completion, string key)
    {
        try { await completion; }
        catch (OperationCanceledException) { }
        catch (DurablePackageRetryException ex)
        {
            PCasaDiagnostics.Write("Tags.RecoveredDeferred", ex);
            // File-write failures are listed only under Extras > Schreibfehler.
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Tags.RecoveredWrite", ex);
            // File-write failures are listed only under Extras > Schreibfehler.
        }
        finally
        {
            _recoveredTagPresentationKeys.Remove(key);
            if (!Dispatcher.HasShutdownStarted)
            {
                EndVisibleManualOperation(key);
                _ = RefreshPendingTagDotsAsync();
                // Old V1/edited packages may have left file writes pending.
                // Reuse the one durable v2 recipe; never create a second worker.
                StartPendingTagDrain();
            }
        }
    }

    // A failed V2 recovery can remain queued in the durable ledger even
    // after a DIFFERENT later manual package has written its last pending
    // original. Since the V2 recipe is precisely "drain pending_tag_edits",
    // an empty authoritative backlog means that orphaned recipe is satisfied.
    // Never retire an actively scheduled recovery or any real user edit.
    private void ReconcileSatisfiedTagRecovery()
    {
        var journal = new DurablePackageJournal(_database);
        foreach (var record in journal.Pending().Where(p =>
                     p.Type == TagRecoveryType && p.Payload == "{}"))
        {
            if (_scheduler.HasLiveExecution(record.Key)) continue;
            journal.SetState(record.Key, "completed", completed: 1);
            _recoveredTagPresentationKeys.Remove(record.Key);
            EndVisibleManualOperation(record.Key);
            PCasaDiagnostics.WriteMessage("Tags.RecoverySatisfied",
                "Offener Wiederherstellungsauftrag durch bestätigte " +
                "SQLite-Originaldateiänderungen erledigt: " + record.Key);
        }
    }

    private void StartPendingTagDrain()
    {
        if (Dispatcher.HasShutdownStarted) return;
        var parked = _originalWriteFailures.Unresolved()
            .Where(error => error.Kind == OriginalWriteFailureRepository.Tags)
            .Select(error => error.ImageId).ToHashSet();
        var imageIds = new ImageTagRepository(_database).PendingEdits(int.MaxValue)
            .Select(edit => edit.ImageId).Distinct()
            .Where(id => !parked.Contains(id)).ToArray();
        if (imageIds.Length == 0)
        {
            ReconcileSatisfiedTagRecovery();
            return;
        }
        if (_tagRecoveryQueued ||
            _scheduler.HasActiveDurablePackageType(TagRecoveryType) ||
            _scheduler.HasActiveDurablePackageType("tag-recovery-v1") ||
            // First finish all genuine manually submitted edit packages.
            _scheduler.HasActiveDurablePackageType("tag-edit-v1") ||
            _scheduler.HasActiveDurablePackageType("tag-edit-v2")) return;
        _tagRecoveryQueued = true;
        var key = "tags:recover:" + Guid.NewGuid().ToString("N");
        try
        {
            const string payload = "{}";
            var package = BuildTagRecoveryPackageV2(key, payload);
            BeginVisibleManualOperation(key,
                "Tags in Originaldateien schreiben", imageIds.Length);
            var task = _scheduler.EnqueueDurablePackageAsync(package,
                TagRecoveryType, payload, SchedulerPriority.ManualAction);
            _ = ObserveTagRecoveryAsync(task, key);
            _ = RefreshPendingTagDotsAsync();
        }
        catch (Exception ex)
        {
            _tagRecoveryQueued = false;
            EndVisibleManualOperation(key);
            PCasaDiagnostics.Write("Tags.RecoveryQueue", ex);
        }
    }

    private async Task ObserveTagRecoveryAsync(Task completion, string key)
    {
        try { await completion; }
        catch (OperationCanceledException) { }
        catch (DurablePackageRetryException ex)
        {
            PCasaDiagnostics.Write("Tags.RecoveryDeferred", ex);
            // File-write failures are listed only under Extras > Schreibfehler.
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Tags.Recovery", ex);
            // File-write failures are listed only under Extras > Schreibfehler.
        }
        finally
        {
            _tagRecoveryQueued = false;
            if (!Dispatcher.HasShutdownStarted)
            {
                EndVisibleManualOperation(key);
                _ = RefreshPendingTagDotsAsync();
            }
        }
    }

    private readonly TagEditDraft _tagDraft = new();

    // Draft changes never mutate SQLite or the originals. One OK is one package.
    private void StageTagChange(string tag, bool remove)
    {
        tag = tag.Trim();
        if (tag.Length == 0 || _tagWriteActive) return;
        var ids = _selectedImageIds.ToArray();
        if (ids.Length == 0)
        {
            StatusText.Text = UiLanguage.T("Zuerst Bilder auswählen.");
            return;
        }
        _tagDraft.Stage(ids, tag, remove);
        if (!remove) TagEntryBox.Clear();
        RefreshTagDraftControls();
    }

    private void RefreshTagDraftControls()
    {
        // A single draft owns ALL selections. The current Tag list remains
        // the projection for the selected photos, not a second draft list.
        TagDraftOkButton.IsEnabled = _tagDraft.HasChanges && !_tagWriteActive;
        TagDraftCancelButton.IsEnabled = _tagDraft.HasChanges && !_tagWriteActive;
        TagDraftSummaryText.Text = _tagDraft.HasChanges
            ? UiLanguage.Format("{0:N0} Bilder · {1:N0} Änderungen vorgemerkt",
                _tagDraft.ImageCount, _tagDraft.ChangeCount)
            : string.Empty;
        TagDraftSummaryText.Visibility = _tagDraft.HasChanges
            ? Visibility.Visible : Visibility.Collapsed;
        RenderTagRows();
    }

    private async void TagDraftCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_tagWriteActive) return;
        _tagDraft.Clear();
        TagEntryBox.Clear();
        RefreshTagDraftControls();
        // Discard the entire draft and reload the unchanged, authoritative
        // tag list for the selected images. No SQLite or file mutation.
        await RefreshTagSidebarAsync();
    }

    private sealed record DurableTagChange(string Tag, bool Remove);
    private sealed record DurableTagImageEdit(long ImageId, DurableTagChange[] Changes);
    // Optional ImageEdits leaves old tag-edit-v1 recipes readable without
    // migrations. Newly submitted tag-edit-v2 recipes express per-image changes.
    private sealed record DurableTagEdit(long[] ImageIds, DurableTagChange[] Changes,
        DurableTagImageEdit[]? ImageEdits = null);

    // Exactly reproducible steps: one local commit, then one step per image.
    // A restart never needs an in-memory closure to reconstruct this package.
    private SchedulerTaskPackage BuildTagEditPackage(string key, string payload)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<DurableTagEdit>(payload)
            ?? throw new InvalidDataException("Ungültiger Tag-Auftrag.");
        var perImage = request.ImageEdits is { Length: > 0 }
            ? request.ImageEdits.Select(entry =>
                (ImageId: entry.ImageId, Edits: (IReadOnlyList<(string Tag, bool Remove)>)
                    entry.Changes.Select(edit => (edit.Tag, edit.Remove)).ToArray()))
                .ToArray()
            : request.ImageIds.Select(id =>
                (ImageId: id, Edits: (IReadOnlyList<(string Tag, bool Remove)>)
                    request.Changes.Select(edit => (edit.Tag, edit.Remove)).ToArray()))
                .ToArray();
        if (perImage.Length != request.ImageIds.Length ||
            !perImage.Select(entry => entry.ImageId).SequenceEqual(request.ImageIds) ||
            perImage.Any(entry => entry.Item2.Count == 0) ||
            perImage.Select(entry => entry.ImageId).Distinct().Count() != perImage.Length)
            throw new InvalidDataException("Ungültige bildbezogene Tag-Aufträge.");
        var package = new SchedulerTaskPackage(key);
        package.AddStep(token =>
        {
            token.ThrowIfCancellationRequested();
            // ONE atomic SQLite intent/projection commit for the entire
            // multi-selection draft. Only then begin per-original write steps.
            var count = new ImageTagRepository(_database)
                .QueueTagEdits(perImage);
            _scheduler.ReportProgress(key, "Tags lokal gesichert", 0,
                request.ImageIds.Length);
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(new Action(() =>
                    _ = PublishTagCommitAsync(key, count)));
            return Task.CompletedTask;
        });
        for (var i = 0; i < request.ImageIds.Length; i++)
        {
            var id = request.ImageIds[i];
            var completed = i + 1;
            package.AddStep(token =>
            {
                token.ThrowIfCancellationRequested();
                var repo = new ImageTagRepository(_database);
                var edits = repo.PendingEditsForImage(id);
                if (edits.Count > 0)
                {
                    TryWriteOriginal(OriginalWriteFailureRepository.Tags, key,
                        id, GetOriginalImagePath(id), token, () =>
                    {
                        using var writer = new ExifToolKeywordWriter.Session();
                        repo.ApplyPendingImageEdits(id, edits, writer, token,
                            path => _scheduler.ReportProgress(key,
                                "Tags in Originaldateien schreiben",
                                completed - 1, request.ImageIds.Length, path));
                    });
                }
                _scheduler.ReportProgress(key, "Originaldateien schreiben",
                    completed, request.ImageIds.Length);
                if (!Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                        _ = RefreshPendingTagDotsAsync()));
                return Task.CompletedTask;
            });
        }
        return package;
    }

    private void TagDraftOk_Click(object sender, RoutedEventArgs e)
    {
        if (_tagWriteActive || !_tagDraft.HasChanges) return;
        var snapshot = _tagDraft.Snapshot();
        var ids = snapshot.Select(entry => entry.ImageId).ToArray();
        var key = "tags:package:" + Guid.NewGuid().ToString("N");
        var payload = System.Text.Json.JsonSerializer.Serialize(new DurableTagEdit(
            ids, Array.Empty<DurableTagChange>(),
            snapshot.Select(entry => new DurableTagImageEdit(entry.ImageId,
                entry.Changes.Select(edit =>
                    new DurableTagChange(edit.Tag, edit.Remove)).ToArray())).ToArray()));
        var package = BuildTagEditPackage(key, payload);

        _tagWriteActive = true;
        try
        {
            BeginVisibleManualOperation(key,
                "Tags in Originaldateien schreiben", ids.Length);
            var completion = _scheduler.EnqueueDurablePackageAsync(package,
                "tag-edit-v2", payload, SchedulerPriority.ManualAction);
            _ = ObserveTagPackageAsync(completion, key);
            // The edit is accepted. The next manual action MUST be possible
            // immediately, even if this package later waits for slow I/O.
            _tagDraft.Clear();
            TagEntryBox.Clear();
            StatusText.Text = UiLanguage.T("Tag-Paket P0 vorgemerkt · SQLite und Originaldateien folgen");
        }
        catch (Exception ex)
        {
            EndVisibleManualOperation(key);
            PCasaDiagnostics.Write("Tags.PackageQueue", ex);
            StatusText.Text = UiLanguage.T("Tag-Paket konnte nicht eingeplant werden");
            MessageBox.Show(this, ex.Message, UiLanguage.T("Tag-Änderung fehlgeschlagen"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _tagWriteActive = false;
            RefreshTagDraftControls();
        }
    }

    private async Task PublishTagCommitAsync(string key, int changes)
    {
        StatusText.Text = UiLanguage.Format("Tag-Paket: {0} Änderungen lokal gesichert · Dateien folgen", changes);
        RefreshQuickTags();
        await RefreshTagSidebarAsync();
        if (_browseInitialized && _browsePageCache is not null &&
            !string.IsNullOrWhiteSpace(TagSearchBox.Text))
            await ApplyTagFilterAsync();
        _ = RefreshPendingTagDotsAsync();
    }

    private async Task ObserveTagPackageAsync(Task completion, string key)
    {
        try { await completion; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Tags.PackageWrite", ex);
            if (!Dispatcher.HasShutdownStarted)
            {
                StatusText.Text = UiLanguage.T("Tag-Schreibfehler – Änderungen in SQLite weiterhin offen");
                // Final reconciliation below handles both success and
                // failure after the last manual tag package has finished.
            }
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                EndVisibleManualOperation(key);
                _ = RefreshPendingTagDotsAsync();
                // Earlier failures may have been held behind other active
                // tag-edit-v1 jobs. Only the LAST completion can start the
                // one durable V2 drain; never create a second writer queue.
                StartPendingTagDrain();
            }
        }
    }

    private CancellationTokenSource? _tagSuggestionCancellation;
    private int _tagSuggestionVersion;
    private bool _acceptingTagSuggestion;

    private void TagEntryClear_Click(object sender, RoutedEventArgs e)
    {
        TagEntryBox.Clear();
        TagEntryBox.Focus();
    }

    private async void TagEntry_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TagEntryClearButton is not null)
            TagEntryClearButton.Visibility =
                string.IsNullOrEmpty(TagEntryBox.Text)
                    ? Visibility.Collapsed : Visibility.Visible;
        if (_acceptingTagSuggestion || TagSuggestionsPopup is null ||
            _scheduler is null || _database is null) return;
        var query = TagEntryBox.Text.Trim();
        var version = ++_tagSuggestionVersion;
        _tagSuggestionCancellation?.Cancel();
        _tagSuggestionCancellation?.Dispose();
        _tagSuggestionCancellation = null;
        TagSuggestionsPopup.IsOpen = false;
        TagSuggestionsList.ItemsSource = null;
        if (query.Length == 0 || _selectedImageIds.Count == 0) return;
        var cancellation = new CancellationTokenSource();
        _tagSuggestionCancellation = cancellation;
        try
        {
            await Task.Delay(150, cancellation.Token);
            var suggestions = await _scheduler.RunAsync(
                "catalog:tag-suggest:" + version, SchedulerPriority.Interactive,
                token => new ImageTagRepository(_database)
                    .SuggestExistingTags(query, 8, token),
                () => version == _tagSuggestionVersion,
                cancellationToken: cancellation.Token,
                workKind: SchedulerWorkKind.CatalogRead);
            if (cancellation.IsCancellationRequested ||
                version != _tagSuggestionVersion ||
                !string.Equals(TagEntryBox.Text.Trim(), query,
                    StringComparison.Ordinal)) return;
            TagSuggestionsList.ItemsSource = suggestions;
            TagSuggestionsList.SelectedIndex = suggestions.Count > 0 ? 0 : -1;
            TagSuggestionsPopup.IsOpen = suggestions.Count > 0 &&
                TagEntryBox.IsKeyboardFocusWithin;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Tags.Suggestions", ex);
        }
    }

    private void AcceptTagSuggestion(string value)
    {
        _tagSuggestionVersion++;
        _tagSuggestionCancellation?.Cancel();
        _acceptingTagSuggestion = true;
        try
        {
            TagSuggestionsPopup.IsOpen = false;
            // Selection stages the tag immediately; the existing OK button
            // remains responsible for committing the edits.
            StageTagChange(value, remove: false);
            TagEntryBox.Focus();
        }
        finally { _acceptingTagSuggestion = false; }
    }

    private void TagSuggestion_MouseUp(object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TagSuggestionsList.SelectedItem is string suggestion)
        {
            AcceptTagSuggestion(suggestion);
            e.Handled = true;
        }
    }

    private void TagAdd_Click(object sender, RoutedEventArgs e)
    {
        TagSuggestionsPopup.IsOpen = false;
        StageTagChange(TagEntryBox.Text, remove: false);
    }

    private void TagEntry_KeyDown(object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (TagSuggestionsPopup.IsOpen && TagSuggestionsList.Items.Count > 0)
        {
            if (e.Key is System.Windows.Input.Key.Down or
                System.Windows.Input.Key.Up)
            {
                var direction = e.Key == System.Windows.Input.Key.Down ? 1 : -1;
                TagSuggestionsList.SelectedIndex = Math.Clamp(
                    TagSuggestionsList.SelectedIndex + direction, 0,
                    TagSuggestionsList.Items.Count - 1);
                TagSuggestionsList.ScrollIntoView(TagSuggestionsList.SelectedItem);
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                TagSuggestionsPopup.IsOpen = false;
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.Enter &&
                TagSuggestionsList.SelectedItem is string selected)
            {
                AcceptTagSuggestion(selected);
                e.Handled = true;
                return;
            }
        }
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            StageTagChange(TagEntryBox.Text, remove: false);
        }
    }

    private void StopTagWork()
    {
        _tagSearchCancellation?.Cancel();
        _tagSearchCancellation?.Dispose();
        _tagSearchCancellation = null;
        _tagBackfillCancellation?.Cancel();
        _tagSuggestionCancellation?.Cancel();
        _tagSuggestionCancellation?.Dispose();
        _tagSuggestionCancellation = null;
        // The backfill worker owns its token until it exits; avoid disposing
        // the cancellation source while WaitHandle is still in use.
    }
}
