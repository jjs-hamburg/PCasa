using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.VisualBasic.FileIO;
using PCasa.App.Controls;
using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private void BrowseSurface_ImageContextRequested(
        object? sender, BrowseImageClickedEventArgs e)
    {
        if (_viewerOpen) return;
        if (!_selectedImageIds.Contains(e.ImageId))
            BrowseSurface_ImageClicked(sender, new BrowseImageClickedEventArgs(e.ImageId, ModifierKeys.None));

        var id = e.ImageId;
        var menu = new ContextMenu { PlacementTarget = BrowseSurface, Placement = PlacementMode.MousePoint };
        AddContextAction(menu, "Im Explorer anzeigen", () => ShowFileInExplorer(id));
        AddContextAction(menu, "Dateipfad kopieren",
            () => _ = CopyImagePathToClipboardAsync(id));
        AddContextAction(menu, "Eigenschaften", () => ShowFileProperties(id));
        if (_displayedBrowseIndex.Any(image => image.ImageId == id && image.HasImportError))
        {
            AddContextAction(menu, "Einlesefehler anzeigen",
                () => _ = ShowImportErrorAsync(id));
            AddContextAction(menu, "Mit Standardprogramm öffnen",
                () => _ = OpenImportErrorOriginalAsync(id));
        }
        // Available for EVERY photograph and video, regardless of whether
        // the cached thumbnail is already present. The selection is captured
        // once on right-click; no new gallery scan or additional selection.
        var thumbnailIds = SelectThumbnailIds(id, _selectedImageIds,
            _displayedBrowseIndex);
        var validThumbnailIds = thumbnailIds.Where(imageId =>
            !_displayedBrowseIndex.Any(image => image.ImageId == imageId &&
                image.HasImportError)).ToArray();
        if (validThumbnailIds.Length > 0)
        {
            var captured = validThumbnailIds;
            AddContextAction(menu, captured.Length > 1
                ? "Vorschaubilder aktualisieren"
                : "Vorschaubild aktualisieren",
                () => _ = RefreshThumbnailsAsync(captured));
        }
        // Panorama is a multi-selection action only. The menu item does not
        // exist when no panorama-capable program was configured.
        if (thumbnailIds.Length >= 2 && _externalEditors.ForPanorama().Count > 0)
        {
            var selected = thumbnailIds.ToHashSet();
            var ordered = _displayedBrowseIndex
                .Where(item => selected.Contains(item.ImageId))
                .Select(item => item.ImageId).ToArray();
            if (ordered.Length >= 2)
                AddContextAction(menu, "Panorama erstellen",
                    () => _ = StartPanoramaSelectionAsync(ordered));
        }
        // Multiple selected photos go through one batch session. Do not
        // silently edit only the clicked image when several are selected.
        var orderedSelection = _displayedBrowseIndex
            .Where(item => thumbnailIds.Contains(item.ImageId))
            .Select(item => item.ImageId).ToArray();
        if (orderedSelection.Length >= 2)
        {
            var sourceNames = orderedSelection
                .Select(CachedExternalEditorSource).ToArray();
            if (sourceNames.All(path => path is not null))
            {
                var multiPrograms = _externalEditors.ForMultiple(
                    sourceNames.Select(path => path!).ToArray());
                if (multiPrograms.Count > 0)
                {
                    var external = new MenuItem
                    {
                        Header = UiLanguage.T("Bearbeiten mit …")
                    };
                    foreach (var program in multiPrograms)
                    {
                        var chosen = program;
                        var item = new MenuItem { Header = chosen.Name };
                        item.Click += (_, _) =>
                            _ = StartMultipleEditorAsync(orderedSelection, chosen);
                        external.Items.Add(item);
                    }
                    menu.Items.Add(external);
                }
            }
        }
        else
        {
            var editorSource = CachedExternalEditorSource(id);
            if (editorSource is not null &&
                _externalEditors.ForFile(editorSource).Count > 0)
            {
                var external = new MenuItem
                {
                    Header = UiLanguage.T("Bearbeiten mit …")
                };
                PopulateExternalEditorMenu(external, id, editorSource);
                menu.Items.Add(external);
            }
        }
        var restore = new MenuItem { Header = UiLanguage.T("Bild wiederherstellen"),
            IsEnabled = false };
        restore.Click += (_, _) => UndoRestoreImage(id);
        menu.Items.Add(restore);
        _ = UpdateRestoreContextAsync(restore, id);
        menu.Items.Add(new Separator());
        if (thumbnailIds.Length == 1)
        {
            var rename = new MenuItem {
                Header = UiLanguage.T("Umbenennen"), IsEnabled = false };
            rename.Click += (_, _) => _ = RenameImageAsync(id);
            menu.Items.Add(rename);
            _ = EnableImageRenameIfAvailableAsync(menu, rename, id);
        }
        AddContextAction(menu, "Duplizieren", () => DuplicateImage(id));
        AddContextAction(menu, "Löschen", () => _ = DeleteSelectedImagesAsync());
        menu.IsOpen = true;
    }

    private async Task EnableImageRenameIfAvailableAsync(
        ContextMenu parent, MenuItem item, long id)
    {
        var available = await ImageRenameAvailableAsync(id);
        if (!Dispatcher.HasShutdownStarted &&
            parent.Items.Contains(item) && !_viewerOpen)
            item.IsEnabled = available;
    }

    internal static long[] SelectThumbnailIds(long clickedId,
        IReadOnlyCollection<long> selectedIds,
        IReadOnlyList<BrowseThumbnailIndexEntry> index)
    {
        var galleryIds = index.Select(image => image.ImageId).ToHashSet();
        // Right-click preserves a multi-selection only if the clicked image
        // belongs to it. Otherwise the single clicked image is authoritative.
        IEnumerable<long> candidates = selectedIds.Contains(clickedId)
            ? selectedIds : new[] { clickedId };
        return candidates.Where(galleryIds.Contains).Distinct().ToArray();
    }

    private async Task RefreshThumbnailsAsync(long[] imageIds)
    {
        if (imageIds.Length == 0 || _activeBrowseTier is not int tier) return;
        const string label = "Vorschaubilder aktualisieren";
        string? key = null;
        try
        {
            // A single immutable snapshot, one scheduler P0 package, and one
            // targeted publication. Nothing touches the other gallery images.
            var selected = (await SelectedGalleryImagePathsAsync(imageIds))
                .DistinctBy(item => item.Id).ToArray();
            if (selected.Length == 0 || Dispatcher.HasShutdownStarted) return;
            key = "thumbnail:refresh:" + Guid.NewGuid().ToString("N");
            BeginVisibleManualOperation(key, UiLanguage.T(label), selected.Length);
            var refreshed = new Dictionary<long, BrowseThumbnailIndexEntry>();
            var decoded = new Dictionary<long, System.Windows.Media.Imaging.BitmapSource>();
            var failures = new List<(string Name, string Error)>();
            await _scheduler.EnqueueAsync(key, SchedulerPriority.ManualAction,
                token =>
                {
                    var repository = new BrowseCacheRepository(_database);
                    var builder = _browseCacheBuilder ?? new BrowseCacheBuilder(
                        repository, new BrowseThumbnailProfileRepository(_database));
                    using var reader = new PackedThumbnailReader();
                    for (var index = 0; index < selected.Length; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        var item = selected[index];
                        try
                        {
                            if (VideoFormats.IsVideo(item.Path))
                                VideoThumbnailSource.Resolve(item.Path, token,
                                    forceRefresh: true);
                            // Unlike "build missing", a refresh re-encodes
                            // every configured tier from the ORIGINAL. Keep
                            // the last good DB row if the source is offline.
                            builder.RebuildSingleImage(item.Id, token);
                            var entry = repository.GetIndexEntry(tier, item.Id)
                                ?? throw new InvalidDataException(
                                    "Kein neues Vorschaubild im Cache.");
                            var bitmap = reader.Load(entry, token)
                                ?? throw new InvalidDataException(
                                    "Neu erzeugtes Vorschaubild nicht lesbar.");
                            refreshed[item.Id] = entry;
                            decoded[item.Id] = bitmap;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            failures.Add((Path.GetFileName(item.Path), ex.Message));
                            PCasaDiagnostics.Write("Gallery.ThumbnailRefresh", ex);
                        }
                        _scheduler.ReportProgress(key, UiLanguage.T(label),
                            index + 1, selected.Length);
                    }
                    return Task.CompletedTask;
                }, workKind: SchedulerWorkKind.Catalog);

            if (Dispatcher.HasShutdownStarted) return;
            if (refreshed.Count > 0 && _activeBrowseTier == tier &&
                _browsePageCache is not null)
            {
                // A different image orientation can alter row height. Keep
                // the user's exact gallery anchor; ordinary refreshes do
                // not reflow or reindex the entire gallery.
                var changesGeometry = ThumbnailRefreshProjection.ChangesLayout(
                    _displayedBrowseIndex, refreshed);
                if (changesGeometry) QueueGalleryAnchorRestore();

                _browseIndex = ThumbnailRefreshProjection.Replace(
                    _browseIndex, refreshed);
                _displayedBrowseIndex = ThumbnailRefreshProjection.Replace(
                    _displayedBrowseIndex, refreshed);
                foreach (var entry in refreshed.Values)
                    _dragPublishedImages[entry.ImageId] = entry;
                _browsePageCache.ReplaceThumbnailEntries(refreshed, decoded);
                BrowseSurface.ReplaceThumbnailEntries(refreshed);
                if (changesGeometry)
                {
                    BrowseSurface.SetViewport(BrowseScrollViewer.VerticalOffset,
                        Math.Max(1, BrowseScrollViewer.ViewportHeight));
                    RestorePendingGalleryAnchor();
                }
            }
            StatusText.Text = UiLanguage.Format(
                "{0:N0} Vorschaubilder aktualisiert, {1:N0} Fehler.",
                refreshed.Count, failures.Count);
            if (failures.Count > 0)
                PcasaDialog.Show(this,
                    string.Join("\n", failures.Take(12).Select(item =>
                        item.Name + ": " + item.Error)),
                    UiLanguage.T(label), MessageBoxButton.OK,
                    MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowFileActionError(ex); }
        finally
        {
            if (key is not null && !Dispatcher.HasShutdownStarted)
                EndVisibleManualOperation(key);
        }
    }

    private async Task ShowImportErrorAsync(long id)
    {
        try
        {
            var detail = await _galleryCatalogReader.ReadAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                using var db = _database.OpenConnection();
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    SELECT local_path, import_error, import_attempt_utc FROM images
                    WHERE id=$id AND import_error IS NOT NULL;
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                using var row = cmd.ExecuteReader();
                if (!row.Read()) return (Path: "", Error: "", Attempt: "");
                return (Path: row.GetString(0), Error: row.GetString(1),
                    Attempt: row.IsDBNull(2) ? "" : row.GetString(2));
            });
            if (Dispatcher.HasShutdownStarted || detail.Error.Length == 0) return;
            var attempt = DateTimeOffset.TryParse(detail.Attempt, out var time)
                ? time.ToLocalTime().ToString("g") : detail.Attempt;
            PcasaDialog.Show(this, detail.Path + Environment.NewLine +
                UiLanguage.Format("Letzter Einleseversuch: {0}", attempt) +
                Environment.NewLine + Environment.NewLine + detail.Error,
                UiLanguage.T("Einlesefehler"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowFileActionError(ex); }
    }

    private async Task OpenImportErrorOriginalAsync(long id)
    {
        try
        {
            var paths = await SelectedGalleryPathsAsync([id]);
            if (paths.Length == 0 || Dispatcher.HasShutdownStarted) return;
            Process.Start(new ProcessStartInfo { FileName = paths[0], UseShellExecute = true });
        }
        catch (Exception ex) { ShowFileActionError(ex); }
    }

    private async Task UpdateRestoreContextAsync(MenuItem menu, long imageId)
    {
        try
        {
            var available = await Task.Run(() => HasRestorableVersion(imageId));
            if (!Dispatcher.HasShutdownStarted) menu.IsEnabled = available;
        }
        catch (Exception ex) { PCasaDiagnostics.Write("Undo.ContextAvailability", ex); }
    }

    private static void AddContextAction(ContextMenu menu, string title, Action action)
    {
        var item = new MenuItem { Header = UiLanguage.T(title) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private async void ShowFileInExplorer(long id)
    {
        try
        {
            var path = await SelectedGalleryPathsAsync([id]);
            if (path.Length == 0 || Dispatcher.HasShutdownStarted) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + path[0] + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { ShowFileActionError(ex); }
    }

    // Unlike launching the image with verb "properties", this Windows shell
    // API opens file properties even when no app is associated with .jpg/.raw.
    private const uint ShopFilePath = 0x00000002;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(
        IntPtr owner, uint objectType, string filePath, string? propertyPage);

    private async void ShowFileProperties(long id)
    {
        try
        {
            var paths = await SelectedGalleryPathsAsync([id]);
            if (paths.Length == 0 || Dispatcher.HasShutdownStarted) return;
            var path = paths[0];
            var exists = await _scheduler.RunAsync(
                $"image:properties-probe:{id}:{Guid.NewGuid():N}",
                SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return File.Exists(path);
                }, workKind: SchedulerWorkKind.SourceProbe);
            if (!exists)
                throw new FileNotFoundException("Die Datei ist nicht mehr erreichbar.", path);
            if (!SHObjectProperties(new WindowInteropHelper(this).Handle,
                    ShopFilePath, path, null))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Der Windows-Eigenschaftendialog konnte nicht geöffnet werden.");
        }
        catch (Exception ex) { ShowFileActionError(ex); }
    }

    private async Task CopyImagePathToClipboardAsync(long id)
    {
        try
        {
            var paths = await SelectedGalleryPathsAsync([id]);
            if (paths.Length > 0 && !Dispatcher.HasShutdownStarted)
                Clipboard.SetText(paths[0]);
        }
        catch (Exception ex) { ShowFileActionError(ex); }
    }

    private async void DuplicateImage(long id)
    {
        // Use the ONE gallery transfer implementation for duplicates as well.
        // It already owns name collision handling, file locks, catalog import,
        // progress presentation and gallery refresh.
        try
        {
            var paths = await SelectedGalleryPathsAsync([id]);
            if (paths.Length == 0 || Dispatcher.HasShutdownStarted) return;
            var source = paths[0];
            var directory = Path.GetDirectoryName(source)
                ?? throw new IOException("Der Quellordner ist nicht bekannt.");
            await TransferGalleryFilesAsync([source], directory, move: false);
        }
        catch (Exception ex) { ShowFileActionError(ex); }
    }

    private void ShowFileActionError(Exception ex)
    {
        PCasaDiagnostics.Write("Context.FileAction", ex);
        PcasaDialog.Show(this, ex.Message, UiLanguage.T("Dateiaktion fehlgeschlagen"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
