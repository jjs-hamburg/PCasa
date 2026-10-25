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

    private async Task RefreshTagSidebarAsync()
    {
        var revision = ++_tagSidebarRefreshVersion;
        var selected = _selectedImageIds.ToArray();
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
            var allOnly = TagLogicAnd.IsChecked == true;
            var filtered = counts.Where(pair => !allOnly || pair.Value == selected.Length).ToArray();
            foreach (var pair in filtered)
            {
                var row = new DockPanel { Margin = new Thickness(3, 5, 3, 5) };
                // Use a self-contained template so global button styles cannot
                // hide the delete glyph in either light or dark appearance.
                var deleteGlyph = new System.Windows.Shapes.Path
                {
                    Data = System.Windows.Media.Geometry.Parse("M 4,4 L 16,16 M 16,4 L 4,16"),
                    Stroke = System.Windows.Media.Brushes.IndianRed,
                    StrokeThickness = 2.4,
                    StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                    StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                    Width = 20,
                    Height = 20,
                    Stretch = System.Windows.Media.Stretch.Uniform
                };
                var remove = new Button
                {
                    Content = deleteGlyph,
                    ToolTip = "Tag aus allen ausgewählten Bildern entfernen",
                    Width = 28,
                    Height = 28,
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
                remove.Click += async (_, _) => await ChangeSelectedTagsAsync(pair.Key, remove: true);
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                var label = new TextBlock {
                    Text = $"🏷  {pair.Key}  [{pair.Value}]",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = pair.Key
                };
                row.Children.Add(label);
                TagRowsPanel.Children.Add(row);
            }
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Tags.Sidebar", ex);
            if (revision == _tagSidebarRefreshVersion)
                TagRowsPanel.Children.Add(new TextBlock { Text = "Tags konnten nicht gelesen werden." });
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
            button.Click += async (_, _) => await ChangeSelectedTagsAsync(tag, remove: false);
            TagQuickPanel.Children.Add(button);
        }
    }

    private bool _tagDrainActive;
    private readonly HashSet<string> _tagDrainFailures = new(StringComparer.Ordinal);

    // The durable table is the ONLY backlog. Scheduler executes it; the UI
    // never waits for a source file to be copied across a VPN.
    private void StartPendingTagDrain()
    {
        if (_tagDrainActive || Dispatcher.HasShutdownStarted) return;
        _tagDrainActive = true;
        _ = DrainPendingTagsAsync();
    }

    private async Task DrainPendingTagsAsync()
    {
        var interrupted = false;
        try
        {
            await _scheduler.EnqueueAsync("tags:durable-drain",
                SchedulerPriority.Background, token =>
                {
                    var repo = new ImageTagRepository(_database);
                    using var writer = new ExifToolKeywordWriter.Session();
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var pending = repo.PendingEdits();
                        if (pending.Count == 0) break;
                        var processed = 0;
                        foreach (var group in pending.GroupBy(item => item.ImageId))
                        {
                            token.ThrowIfCancellationRequested();
                            var identity = group.Key.ToString();
                            if (_tagDrainFailures.Contains(identity)) continue;
                            try
                            {
                                repo.ApplyPendingImageEdits(group.Key, group.ToArray(), writer, token);
                                processed++;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                _tagDrainFailures.Add(identity);
                                PCasaDiagnostics.Write("Tags.DurableWrite", ex);
                            }
                        }
                        // Every failed record stays on disk. Do not spin forever
                        // against an unavailable NAS or a permanently bad file.
                        if (processed == 0) break;
                    }
                    return Task.CompletedTask;
                });
        }
        catch (OperationCanceledException) { interrupted = true; }
        catch (Exception ex) { PCasaDiagnostics.Write("Tags.Drain", ex); }
        finally
        {
            _tagDrainActive = false;
            // A higher-priority scheduler job may yield the background drain.
            // Resume from SQLite when the scheduler permits; failed source
            // writes, by contrast, do not trigger a tight retry loop.
            if (interrupted && !Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(new Action(StartPendingTagDrain));
        }
    }

    private async Task ChangeSelectedTagsAsync(string tag, bool remove)
    {
        tag = tag.Trim();
        if (tag.Length == 0 || _tagWriteActive) return;
        var ids = _selectedImageIds.ToArray();
        if (ids.Length == 0)
        {
            StatusText.Text = "Zuerst Bilder auswählen.";
            return;
        }
        _tagWriteActive = true;
        try
        {
            var changed = await _scheduler.RunAsync(
                "tags:queue:" + Guid.NewGuid().ToString("N"),
                SchedulerPriority.ManualAction,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return new ImageTagRepository(_database).QueueTagEdit(ids, tag, remove);
                }, workKind: SchedulerWorkKind.Catalog);
            if (!remove) TagEntryBox.Clear();
            StatusText.Text = $"Tag '{tag}': {changed} Änderungen lokal gesichert · Dateischreiben im Hintergrund";
            RefreshQuickTags();
            await RefreshTagSidebarAsync();
            if (_browseInitialized && _browsePageCache is not null &&
                !string.IsNullOrWhiteSpace(TagSearchBox.Text))
                await ApplyTagFilterAsync();
            _tagDrainFailures.Clear();
            StartPendingTagDrain();
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Tags.Queue", ex);
            StatusText.Text = "Tag-Änderung konnte nicht gespeichert werden";
            MessageBox.Show(this, ex.Message, "Tag-Änderung fehlgeschlagen",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _tagWriteActive = false; }
    }

    private async void TagAdd_Click(object sender, RoutedEventArgs e)
        => await ChangeSelectedTagsAsync(TagEntryBox.Text, remove: false);

    private async void TagEntry_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            await ChangeSelectedTagsAsync(TagEntryBox.Text, remove: false);
        }
    }

    private void StopTagWork()
    {
        _tagSearchCancellation?.Cancel();
        _tagSearchCancellation?.Dispose();
        _tagSearchCancellation = null;
        _tagBackfillCancellation?.Cancel();
        // The backfill worker owns its token until it exits; avoid disposing
        // the cancellation source while WaitHandle is still in use.
    }
}
