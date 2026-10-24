using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // At narrower window widths only the NEW sort selector becomes
    // icon-only; the existing main menu and search controls never move.
    // The full criterion remains available via tooltip and dropdown.
    private void GallerySortWindow_SizeChanged(
        object sender, SizeChangedEventArgs e)
    {
        if (!IsInitialized || GallerySortSelectorButton is null) return;
        var width = e.NewSize.Width > 0 ? e.NewSize.Width : ActualWidth;
        var compact = width < 1680;
        GallerySortSelectorButton.Width = compact ? 35 : 191;
        GallerySortSelectorButton.Margin = compact
            ? new Thickness(0, 0, 1, 0)
            : new Thickness(2, 0, 8, 0);
        GallerySortSelectorButton.Padding = compact
            ? new Thickness(1, 0, 1, 0)
            : new Thickness(8, 0, 8, 0);
        GallerySortSelectedFieldLabel.Visibility = compact
            ? Visibility.Collapsed : Visibility.Visible;
        GallerySortIconPanel.Margin = compact
            ? new Thickness(0) : new Thickness(0, 0, 7, 0);
    }

    private long _gallerySortRequestVersion;
    // Describes the ordering of the CURRENT published index. It is not a
    // second user preference; it protects against a sort-menu change while
    // an older manual transfer is concurrently publishing image steps.
    private GallerySortOptions _publishedGallerySort = GallerySortOptions.Default;

    private GallerySortOptions CurrentGallerySort
    {
        get
        {
            var sortField = Enum.TryParse<GallerySortField>(_appearance.GallerySortBy,
                ignoreCase: true, out var parsedSortField) &&
                Enum.IsDefined(parsedSortField)
                ? parsedSortField : GallerySortField.Name;
            return new GallerySortOptions(sortField, _appearance.GallerySortDescending);
        }
    }

    private void SyncGallerySortMenu()
    {
        var option = CurrentGallerySort;
        GallerySortNameMenu.IsChecked = option.Field == GallerySortField.Name;
        GallerySortCapturedMenu.IsChecked = option.Field == GallerySortField.Captured;
        GallerySortCreatedMenu.IsChecked = option.Field == GallerySortField.Created;
        GallerySortModifiedMenu.IsChecked = option.Field == GallerySortField.Modified;
        GallerySortSizeMenu.IsChecked = option.Field == GallerySortField.Size;
        GallerySortDimensionsMenu.IsChecked = option.Field == GallerySortField.Dimensions;
        GallerySortAscendingMenu.IsChecked = !option.Descending;
        GallerySortDescendingMenu.IsChecked = option.Descending;
        // LAP-like bars with the user-approved direction:
        // ASCENDING grows as you go DOWN the list (↓);
        // DESCENDING shrinks as you go DOWN the list (↑).
        // Only the icon's geometry changes, never the sort semantics.
        GallerySortDirectionIcon.Data = Geometry.Parse(option.Descending
            ? "M 20,17 L 20,2 M 16,6 L 20,2 L 24,6"
            : "M 20,2 L 20,17 M 16,13 L 20,17 L 24,13");
        GallerySortSelectedFieldLabel.Text = option.Field switch
        {
            GallerySortField.Captured => "Aufnahmedatum",
            GallerySortField.Created => "Erstellungsdatum",
            GallerySortField.Modified => "Änderungsdatum",
            GallerySortField.Size => "Größe",
            GallerySortField.Dimensions => "Dimension",
            _ => "Name"
        };
        GallerySortSelectorButton.ToolTip =
            $"Sortierung: {GallerySortSelectedFieldLabel.Text}, " +
            (option.Descending ? "absteigend" : "aufsteigend");
    }

    private void GallerySortSelector_Click(object sender, RoutedEventArgs e)
    {
        if (GallerySortSelectorButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = GallerySortSelectorButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void GallerySort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key }) return;
        var current = CurrentGallerySort;
        var next = key switch
        {
            "Ascending" => current with { Descending = false },
            "Descending" => current with { Descending = true },
            _ when Enum.TryParse<GallerySortField>(key, out var field) &&
                   Enum.IsDefined(field) => current with { Field = field },
            _ => current
        };
        _appearance.GallerySortBy = next.Field.ToString();
        _appearance.GallerySortDescending = next.Descending;
        SyncGallerySortMenu();
        SaveAppearance();
        if (next == current || !_browseInitialized || _browseIndex.Count < 2)
            return;

        await ApplyGallerySortAsync(next);
    }

    private async Task ApplyGallerySortAsync(GallerySortOptions requested)
    {
        var generation = ++_gallerySortRequestVersion;
        // The index is a single immutable catalog snapshot. All comparison
        // inputs were materialized by the existing SQLite index read; a
        // potentially huge sort must not block WPF or touch the NAS.
        var original = _browseIndex;
        var positions = CaptureGalleryFolderOrder();
        IReadOnlyList<BrowseThumbnailIndexEntry> reordered;
        try
        {
            reordered = await _scheduler.RunAsync(
                $"gallery:sort:{generation}",
                SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return GalleryFolderOrdering.Order(original, positions, requested);
                }, () => generation == _gallerySortRequestVersion,
                workKind: SchedulerWorkKind.CatalogRead);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.Sort", ex);
            StatusText.Text = "Sortierung fehlgeschlagen";
            return;
        }
        if (generation != _gallerySortRequestVersion ||
            !ReferenceEquals(original, _browseIndex) ||
            requested != CurrentGallerySort ||
            Dispatcher.HasShutdownStarted)
            return; // A later request or incremental transfer owns the view.

        _browseRefreshRequests.Invalidate();
        QueueGalleryAnchorRestore();
        _browseIndex = reordered;
        _publishedGallerySort = requested;
        try
        {
            // Search, inversion, selection, anchors and the page-cache must
            // all observe the ONE established projection path.
            await ApplyTagFilterAsync();
            BrowseScrollViewer.UpdateLayout();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.SortProjection", ex);
        }
        finally
        {
            RestorePendingGalleryAnchor();
        }
    }
}
