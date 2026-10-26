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
                            TagSearchBox.ToolTip = $"Tags suchen – {count:N0} bestehende Bilder geprüft");
                    });
                        return Task.CompletedTask;
                    }, cancellationToken: token);
                if (!Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        TagSearchBox.ToolTip = "Tags suchen (SQLite-Index)";
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
            StatusText.Text = "Tag-Suche fehlgeschlagen";
        }
    }

    private async void SearchMode_Changed(object sender, RoutedEventArgs e)
    {
        if (SearchFileNameMode is null || TagSearchBox is null) return;
        _searchFileNames = SearchFileNameMode.IsChecked == true;
        TagSearchBox.ToolTip = _searchFileNames ? "Dateinamen suchen" : "Tags suchen";
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
            StatusText.Text = "Tag-Suche fehlgeschlagen";
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
        var enteringSearch = _lastAppliedTagQuery.Length == 0 && query.Length > 0;
        var leavingSearch = _lastAppliedTagQuery.Length > 0 && query.Length == 0;
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

        IReadOnlyList<BrowseThumbnailIndexEntry> desired =
            query.Length == 0 ? _browseIndex :
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
        BrowseSurface.SetSelection(_selectedImageIds);
        EmptyPanel.Visibility = desired.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanelHeading.Text = query.Length == 0
            ? "Noch keine Bilder im Katalog" :
                _invertFilterResults ? "Keine übrigen Bilder gefunden" :
                _searchFileNames ? "Keine passenden Dateinamen gefunden" : "Keine passenden Tags gefunden";
        EmptyPanelDescription.Text = query.Length == 0
            ? "Ordner im Menü einlesen, um die Galerie zu füllen."
            : _searchFileNames ? "Gesucht wird in den katalogisierten Dateinamen."
                : "Gesucht wird im lokalen Tag-Index. Bei der ersten Nutzung werden Tags noch im Hintergrund erfasst.";
        StatusText.Text = query.Length > 0
            ? $"{desired.Count:N0} Bilder {(_invertFilterResults ? "ohne" : "mit")} {(_searchFileNames ? "Dateiname" : "Tags")}: {query}"
            : $"{_lastCatalogImageCount:N0} Bilder";
        _lastAppliedTagQuery = query;
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
    private KeyValuePair<string, int>[] _tagSidebarOriginalCounts =
        Array.Empty<KeyValuePair<string, int>>();

    private async Task RefreshTagSidebarAsync()
    {
        var revision = ++_tagSidebarRefreshVersion;
        var selected = _selectedImageIds.ToArray();
        // A draft belongs to one selection. Never carry it into another
        // folder or make the next folder's tag editor unusable.
        var selectedSorted = selected.OrderBy(id => id).ToArray();
        if (_tagDraftImageIds is not null &&
            !_tagDraftImageIds.SequenceEqual(selectedSorted))
        {
            _tagDraft.Clear();
            _tagDraftImageIds = null;
            if (TagEntryBox is not null) TagEntryBox.Clear();
            if (TagDraftOkButton is not null) TagDraftOkButton.IsEnabled = false;
            if (TagDraftCancelButton is not null) TagDraftCancelButton.IsEnabled = false;
        }
        TagSelectionCaption.Text = $"Tags der Auswahl ({selected.Length} Bilder)";
        TagRowsPanel.Children.Clear();
        if (selected.Length == 0)
        {
            TagRowsPanel.Children.Add(new TextBlock { Text = "Bitte Bilder auswählen.",
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
                var dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in selected)
                {
                    var value = repository.GetKeywords(id);
                    if (value is null) continue;
                    foreach (var tag in value.Split(new[] { (char)10, (char)13 }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                        dictionary[tag] = dictionary.GetValueOrDefault(tag) + 1;
                }
                return dictionary.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }, () => revision == _tagSidebarRefreshVersion,
                workKind: SchedulerWorkKind.CatalogRead);
            if (revision != _tagSidebarRefreshVersion || _sidebarMode != "Tags") return;
            _tagSidebarCountImageIds = selected.OrderBy(id => id).ToArray();
            _tagSidebarOriginalCounts = counts;
            RenderTagRows();
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Tags.Sidebar", ex);
            if (revision == _tagSidebarRefreshVersion)
                TagRowsPanel.Children.Add(new TextBlock { Text = "Tags konnten nicht gelesen werden." });
        }
    }

    private void RenderTagRows()
    {
        if (TagRowsPanel is null) return;
        var selected = _selectedImageIds.OrderBy(id => id).ToArray();
        if (!_tagSidebarCountImageIds.SequenceEqual(selected)) return;
        var projected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _tagSidebarOriginalCounts)
            projected[pair.Key] = pair.Value;
        if (_tagDraftImageIds is not null && _tagDraftImageIds.SequenceEqual(selected))
            foreach (var (tag, remove) in _tagDraft)
            {
                if (remove) projected.Remove(tag);
                else projected[tag] = selected.Length;
            }
        TagRowsPanel.Children.Clear();
        var allOnly = TagLogicAnd.IsChecked == true;
        var filtered = projected
            .Where(pair => !allOnly || pair.Value == selected.Length)
            .OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        foreach (var pair in filtered)
        {
            var row = new DockPanel { Margin = new Thickness(3, 5, 3, 5) };
            var deleteGlyph = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse("M 4,4 L 16,16 M 16,4 L 4,16"),
                Stroke = System.Windows.Media.Brushes.IndianRed,
                StrokeThickness = 1.8,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                Width = 12,
                Height = 12,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            var remove = new Button
            {
                Content = deleteGlyph,
                ToolTip = "Tag aus allen ausgewählten Bildern entfernen",
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
                Content = tag, ToolTip = $"Tag '{tag}' allen ausgewählten Bildern hinzufügen",
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
                    try
                    {
                        using var writer = new ExifToolKeywordWriter.Session();
                        repo.ApplyPendingImageEdits(imageId, edits, writer, token,
                            path => _scheduler.ReportProgress(key,
                                "Tags in Originaldateien schreiben",
                                completed - 1, ids.Length, path));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Failure remains in SQLite; continue other images
                        // and make it visible in scheduler diagnostics.
                        PCasaDiagnostics.Write("Tags.RecoveryWrite", ex);
                    }
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
            var ids = repo.PendingEdits(int.MaxValue).Select(edit => edit.ImageId)
                .Distinct().ToArray();
            var failures = new List<string>();
            var confirmed = 0;
            _scheduler.ReportProgress(key, "Tags in Originaldateien schreiben",
                0, ids.Length);
            for (var i = 0; i < ids.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var imageId = ids[i];
                try
                {
                    var edits = repo.PendingEditsForImage(imageId);
                    if (edits.Count > 0)
                    {
                        using var writer = new ExifToolKeywordWriter.Session();
                        repo.ApplyPendingImageEdits(imageId, edits, writer, token,
                            path => _scheduler.ReportProgress(key,
                                "Tags in Originaldateien schreiben",
                                confirmed, ids.Length, path));
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
                }
                // Count only originals whose pending revisions have been
                // confirmed and removed from SQLite, not attempted writes.
                // An unreachable NAS file must never make the global counter
                // appear complete while its red pending marker remains.
                if (repo.PendingEditsForImage(imageId).Count == 0)
                    confirmed++;
                _scheduler.ReportProgress(key, "Tags wiederherstellen",
                    confirmed, ids.Length);
                if (!Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                        _ = RefreshPendingTagDotsAsync()));
            }
            var remaining = repo.PendingEdits(1).Count > 0;
            if (remaining)
            {
                _scheduler.ReportProgress(key,
                    "Tag-Schreibfehler – Aufträge bleiben vorgemerkt",
                    confirmed, ids.Length);
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
    {
        var pending = new DurablePackageJournal(_database).Pending()
            .Where(p => p.Type is "tag-edit-v1" or
                "tag-recovery-v1" or "tag-recovery-v2")
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
                if (record.Type == "tag-edit-v1")
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
            if (!Dispatcher.HasShutdownStarted)
                StatusText.Text =
                    "Tag-Aufträge weiter vorgemerkt – nicht alle Originaldateien erreichbar";
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Tags.RecoveredWrite", ex);
            if (!Dispatcher.HasShutdownStarted)
                StatusText.Text =
                    "Wiederhergestellter Tag-Auftrag fehlgeschlagen – offene Tags bleiben sichtbar";
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

    private void StartPendingTagDrain()
    {
        if (_tagRecoveryQueued || Dispatcher.HasShutdownStarted ||
            _scheduler.HasActiveDurablePackageType(TagRecoveryType) ||
            _scheduler.HasActiveDurablePackageType("tag-recovery-v1") ||
            // First finish the real user-written packages; recovery is for
            // leftover pending edits, not a duplicate pass over their work.
            _scheduler.HasActiveDurablePackageType("tag-edit-v1")) return;
        var imageIds = new ImageTagRepository(_database).PendingEdits(int.MaxValue)
            .Select(edit => edit.ImageId).Distinct().ToArray();
        if (imageIds.Length == 0) return;
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
            if (!Dispatcher.HasShutdownStarted)
                StatusText.Text =
                    "Tags noch nicht in allen Originaldateien – Auftrag mit P0 für Wiederanlauf erhalten";
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Tags.Recovery", ex);
            if (!Dispatcher.HasShutdownStarted)
                StatusText.Text = "Tag-Wiederherstellung fehlgeschlagen – DB-Aufträge bleiben erhalten";
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

    private readonly Dictionary<string, bool> _tagDraft =
        new(StringComparer.OrdinalIgnoreCase);
    private long[]? _tagDraftImageIds;

    // Draft changes never mutate SQLite or the originals. One OK is one package.
    private void StageTagChange(string tag, bool remove)
    {
        tag = tag.Trim();
        if (tag.Length == 0 || _tagWriteActive) return;
        var ids = _selectedImageIds.OrderBy(id => id).ToArray();
        if (ids.Length == 0)
        {
            StatusText.Text = "Zuerst Bilder auswählen.";
            return;
        }
        if (_tagDraftImageIds is not null && !_tagDraftImageIds.SequenceEqual(ids))
        {
            // Changing the selected images starts a fresh draft. Pending
            // unconfirmed edits were never committed and are discarded.
            _tagDraft.Clear();
            _tagDraftImageIds = null;
        }
        _tagDraftImageIds ??= ids;
        _tagDraft[tag] = remove;
        if (!remove) TagEntryBox.Clear();
        RefreshTagDraftControls();
    }

    private void RefreshTagDraftControls()
    {
        // The existing tag list IS the editor; no second staging list.
        TagDraftOkButton.IsEnabled = _tagDraft.Count > 0 && !_tagWriteActive;
        TagDraftCancelButton.IsEnabled = _tagDraft.Count > 0 && !_tagWriteActive;
        RenderTagRows();
    }

    private async void TagDraftCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_tagWriteActive) return;
        _tagDraft.Clear();
        _tagDraftImageIds = null;
        TagEntryBox.Clear();
        RefreshTagDraftControls();
        // Discard the entire draft and reload the unchanged, authoritative
        // tag list for the selected images. No SQLite or file mutation.
        await RefreshTagSidebarAsync();
    }

    private sealed record DurableTagChange(string Tag, bool Remove);
    private sealed record DurableTagEdit(long[] ImageIds, DurableTagChange[] Changes);

    // Exactly reproducible steps: one local commit, then one step per image.
    // A restart never needs an in-memory closure to reconstruct this package.
    private SchedulerTaskPackage BuildTagEditPackage(string key, string payload)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<DurableTagEdit>(payload)
            ?? throw new InvalidDataException("Ungültiger Tag-Auftrag.");
        var package = new SchedulerTaskPackage(key);
        package.AddStep(token =>
        {
            token.ThrowIfCancellationRequested();
            var edits = request.Changes.Select(e => (e.Tag, e.Remove)).ToArray();
            var count = new ImageTagRepository(_database)
                .QueueTagEdits(request.ImageIds, edits);
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
                    using var writer = new ExifToolKeywordWriter.Session();
                    repo.ApplyPendingImageEdits(id, edits, writer, token,
                        path => _scheduler.ReportProgress(key,
                            "Tags in Originaldateien schreiben",
                            completed - 1, request.ImageIds.Length, path));
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
        if (_tagWriteActive || _tagDraft.Count == 0 || _tagDraftImageIds is null) return;
        var ids = _tagDraftImageIds.ToArray();
        var edits = _tagDraft.Select(x => (Tag: x.Key, Remove: x.Value)).ToArray();
        var key = "tags:package:" + Guid.NewGuid().ToString("N");
        var payload = System.Text.Json.JsonSerializer.Serialize(new DurableTagEdit(
            ids, edits.Select(e => new DurableTagChange(e.Tag, e.Remove)).ToArray()));
        var package = BuildTagEditPackage(key, payload);

        _tagWriteActive = true;
        try
        {
            BeginVisibleManualOperation(key,
                "Tags in Originaldateien schreiben", ids.Length);
            var completion = _scheduler.EnqueueDurablePackageAsync(package,
                "tag-edit-v1", payload, SchedulerPriority.ManualAction);
            _ = ObserveTagPackageAsync(completion, key);
            // The edit is accepted. The next manual action MUST be possible
            // immediately, even if this package later waits for slow I/O.
            _tagDraft.Clear();
            _tagDraftImageIds = null;
            TagEntryBox.Clear();
            StatusText.Text = "Tag-Paket P0 vorgemerkt · SQLite und Originaldateien folgen";
        }
        catch (Exception ex)
        {
            EndVisibleManualOperation(key);
            PCasaDiagnostics.Write("Tags.PackageQueue", ex);
            StatusText.Text = "Tag-Paket konnte nicht eingeplant werden";
            MessageBox.Show(this, ex.Message, "Tag-Änderung fehlgeschlagen",
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
        StatusText.Text = $"Tag-Paket: {changes} Änderungen lokal gesichert · Dateien folgen";
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
                StatusText.Text = "Tag-Schreibfehler – Änderungen in SQLite weiterhin offen";
                // Hand the still-persisted intents to the one durable P0
                // recovery package, without a second queue or worker.
                StartPendingTagDrain();
            }
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                EndVisibleManualOperation(key);
                _ = RefreshPendingTagDotsAsync();
            }
        }
    }

    private CancellationTokenSource? _tagSuggestionCancellation;
    private int _tagSuggestionVersion;
    private bool _acceptingTagSuggestion;

    private async void TagEntry_TextChanged(object sender, TextChangedEventArgs e)
    {
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
            TagEntryBox.Text = value;
            TagEntryBox.CaretIndex = value.Length;
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
