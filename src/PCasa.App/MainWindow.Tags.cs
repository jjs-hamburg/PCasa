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

    private void ClearTagSearch_Click(object sender, RoutedEventArgs e)
    {
        TagSearchBox.Clear();
        TagSearchBox.Focus();
    }

    private async void TagSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
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

        IReadOnlyList<BrowseThumbnailIndexEntry> desired =
            query.Length == 0 ? _browseIndex :
            _searchFileNames
                ? _browseIndex.Where(image => image.FileName.Contains(
                    query, StringComparison.OrdinalIgnoreCase)).ToArray()
                : _browseIndex.Where(image => imageIds!.Contains(image.ImageId)).ToArray();
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
                desired = desired.Where(image => included.Contains(image.PhotoFolderId)).ToArray();
            }
            else
                desired = Array.Empty<BrowseThumbnailIndexEntry>();
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
                _searchFileNames ? "Keine passenden Dateinamen gefunden" : "Keine passenden Tags gefunden";
        EmptyPanelDescription.Text = query.Length == 0
            ? "Ordner im Menü einlesen, um die Galerie zu füllen."
            : _searchFileNames ? "Gesucht wird in den katalogisierten Dateinamen."
                : "Gesucht wird im lokalen Tag-Index. Bei der ersten Nutzung werden Tags noch im Hintergrund erfasst.";
        StatusText.Text = query.Length > 0
            ? $"{desired.Count:N0} Bilder mit {(_searchFileNames ? "Dateiname" : "Tags")}: {query}"
            : $"{_imageCatalog.CountAll():N0} Bilder";
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
                var remove = new Button {
                    // Use geometry rather than a font glyph. The dark WPF
                    // control template may not render the previous red glyph.
                    Content = new System.Windows.Shapes.Path {
                        Data = System.Windows.Media.Geometry.Parse("M 3,3 L 13,13 M 13,3 L 3,13"),
                        Stroke = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(221, 61, 61)),
                        StrokeThickness = 2,
                        Width = 16,
                        Height = 16,
                        Stretch = System.Windows.Media.Stretch.Uniform
                    },
                    ToolTip = "Tag aus allen ausgewählten Bildern entfernen",
                    Width = 23, Height = 23, Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0)
                };
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

    private async Task ChangeSelectedTagsAsync(string tag, bool remove)
    {
        tag = tag.Trim();
        if (tag.Length == 0 || _tagWriteActive) return;
        if (_selectedImageIds.Count == 0)
        {
            StatusText.Text = "Zuerst Bilder auswählen.";
            return;
        }
        var ids = _selectedImageIds.ToArray();
        _tagWriteActive = true;
        try
        {
            var succeeded = 0;
            var errors = new List<string>();
            await _scheduler.EnqueueAsync(
                $"tags:write:{Guid.NewGuid():N}", SchedulerPriority.Interactive,
                token =>
                {
                    var repo = new ImageTagRepository(_database);
                    foreach (var id in ids)
                    {
                        token.ThrowIfCancellationRequested();
                        try { repo.ChangeKeywords(id, tag, remove); succeeded++; }
                        catch (Exception ex) { errors.Add($"Bild {id}: {ex.Message}"); }
                    }
                    if (!remove && succeeded > 0)
                        repo.RememberTag(tag);
                    return Task.CompletedTask;
                });
            var result = (succeeded, errors);
            if (!remove && result.succeeded > 0)
            {
                TagEntryBox.Clear();
            }
            RefreshQuickTags();
            await RefreshTagSidebarAsync();
            // Writing the originals also updates the FTS index. Keep any
            // active gallery search in sync with that index immediately.
            if (result.succeeded > 0 && _browseInitialized &&
                _browsePageCache is not null &&
                !string.IsNullOrWhiteSpace(TagSearchBox.Text))
                await ApplyTagFilterAsync();
            StatusText.Text = result.errors.Count == 0
                ? $"Tag '{tag}' in {result.succeeded} Bildern {(remove ? "entfernt" : "gespeichert")}."
                : $"{result.succeeded} Bilder bearbeitet, {result.errors.Count} fehlgeschlagen.";
            if (result.errors.Count > 0)
                MessageBox.Show(this, string.Join("\n", result.errors.Take(8)),
                    "Tag-Metadaten nicht geschrieben", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            // Exceptions outside the per-image writer (e.g. SQLite failure
            // while loading recent tags) must not escape async void handlers.
            PCasaDiagnostics.Write("Tags.ChangeSelected", ex);
            StatusText.Text = "Tag-Änderung fehlgeschlagen";
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
