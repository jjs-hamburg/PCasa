using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using System.ComponentModel;

using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private ExternalEditorSettings _externalEditors = ExternalEditorSettings.Load();
    private readonly Dictionary<string, ImageSource> _externalEditorIconCache =
        new(StringComparer.OrdinalIgnoreCase);
    private long _externalEditorDisplayedImageId = -1;
    private string? _externalEditorDisplayedFileName;

    // The cards match the exact geometry of Crop / Straighten tool buttons.
    // Only applications configured for this image format are shown.
    private void RefreshExternalEditorToolButtons(long imageId, string fileName)
    {
        _externalEditorDisplayedImageId = imageId;
        _externalEditorDisplayedFileName = fileName;
        if (!_cropToolsReady) return;
        ExternalEditorToolsList.Children.Clear();

        foreach (var configured in _externalEditors.ForFile(fileName))
        {
            if (!File.Exists(configured.ExecutablePath)) continue;
            var editor = configured; // Capture immutable program per card.
            var button = new Button
            {
                Width = 74,
                Height = 49,
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = editor.Name,
                Content = CreateExternalEditorIcon(editor.ExecutablePath)
            };
            button.Click += (_, _) => _ = StartExternalEditorAsync(imageId, editor);
            var caption = new TextBlock
            {
                Text = editor.Name,
                Width = 82,
                Height = 31,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 0, 0),
                ToolTip = editor.Name
            };
            var card = new StackPanel
            {
                Width = 86,
                Margin = new Thickness(1, 2, 1, 3)
            };
            card.Children.Add(button);
            card.Children.Add(caption);
            ExternalEditorToolsList.Children.Add(card);
        }
    }

    private UIElement CreateExternalEditorIcon(string executable)
    {
        try
        {
            if (!_externalEditorIconCache.TryGetValue(executable, out var source))
            {
                using var extracted = System.Drawing.Icon.ExtractAssociatedIcon(executable)
                    ?? throw new InvalidDataException("No icon in program.");
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(extracted.Handle,
                    Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(30, 30));
                bitmap.Freeze();
                source = bitmap;
                _externalEditorIconCache[executable] = source;
            }
            return new Image
            {
                Source = source,
                Width = 30, Height = 30,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or Win32Exception or InvalidDataException
            or NotSupportedException)
        {
            PCasaDiagnostics.Write("ExternalEditor.Icon", ex);
            return new TextBlock
            {
                Text = "↗", FontSize = 24,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
    }


    private string? CachedExternalEditorSource(long id)
    {
        if (_dragPublishedImages.TryGetValue(id, out var entry))
        {
            if (_dragPublishedFolders.TryGetValue(entry.PhotoFolderId, out var folder))
                return Path.Combine(folder, entry.FileName);
            return entry.FileName; // Extension is enough to filter programs.
        }
        return _dragSelectionPathCache.TryGetValue(id, out var path)
            ? path : null;
    }

    // Works for a nested gallery context menu AND for the viewer dropdown.
    // No SQLite or SMB calls when opening the menu.
    private void PopulateExternalEditorMenu(ItemsControl menu, long id,
        string? sourceName)
    {
        // Do not offer external editors OR Windows Open With for RAW.
        if (sourceName is null || RawImageSource.IsRawPath(sourceName)) return;
        IReadOnlyList<ExternalEditorProgram> programs = sourceName is null
            ? [] : _externalEditors.ForFile(sourceName);
        foreach (var program in programs)
        {
            var current = program;
            var item = new MenuItem { Header = current.Name };
            item.Click += (_, _) => _ = StartExternalEditorAsync(id, current);
            menu.Items.Add(item);
        }
        // Configuring arbitrary shell associations under Windows Open With
        // has no reliably trackable exit. It cannot support PCasa's 3-button
        // rollback decision. Offer only explicitly configured executables;
        // application configuration lives under Options.
    }

    private void ExternalEditorsOptions_Click(object sender, RoutedEventArgs e)
        => ConfigureExternalEditors();

    private void ConfigureExternalEditors()
    {
        var dialog = new ExternalEditorsWindow(_externalEditors) { Owner = this };
        if (dialog.ShowDialog() == true &&
            dialog.SavedSettings is { } saved)
        {
            _externalEditors = saved;
            _externalEditorIconCache.Clear();
            if (_externalEditorDisplayedImageId > 0 &&
                _externalEditorDisplayedFileName is { } fileName)
                RefreshExternalEditorToolButtons(_externalEditorDisplayedImageId,
                    fileName);
        }
    }

    private sealed class ExternalEditReview(ExternalEditSnapshot snapshot,
        long imageId, string name, bool video)
    {
        public ExternalEditSnapshot Snapshot { get; } = snapshot;
        public long ImageId { get; } = imageId;
        public string Name { get; } = name;
        public bool IsVideo { get; } = video;
        public string? ResultPath { get; set; }
        public bool Ready { get; set; }
        public bool Busy { get; set; }
    }

    private ExternalEditReview? _externalEditReview;

    private async Task StartExternalEditorAsync(long id, ExternalEditorProgram? editor)
    {
        try
        {
            if (_externalEditReview is not null)
                throw new InvalidOperationException(UiLanguage.T(
                    "Bitte die laufende externe Bearbeitung zuerst abschließen."));
            var source = (await SelectedGalleryPathsAsync([id])).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(source)) return;
            if (RawImageSource.IsRawPath(source))
                throw new NotSupportedException(UiLanguage.T(
                    "RAW-Dateien können nicht extern bearbeitet werden."));
            if (editor is null)
                throw new InvalidOperationException(UiLanguage.T(
                    "Bitte zuerst unter Optionen ein externes Programm konfigurieren."));
            if (!editor.Matches(source))
                throw new InvalidOperationException(UiLanguage.T(
                    "Dieses Programm ist für das Dateiformat nicht konfiguriert."));
            if (_cropSavePending || _pendingEditImageIds.Contains(id))
                throw new InvalidOperationException(
                    UiLanguage.T("Für dieses Bild wird noch ein Speicherauftrag ausgeführt."));
            // Never discard unsaved PCasa edits by launching another editor.
            if (_viewerOpen && !ConfirmCropExit()) return;

            // A previous application crash leaves a local, verified snapshot.
            // Resume that transaction, do not launch over a possibly edited
            // original and never overwrite the emergency backup.
            var pending = await Task.Run(
                () => ExternalEditTransaction.FindPending(source));
            if (pending is not null)
            {
                var recovered = new ExternalEditReview(pending, id,
                    editor?.Name ?? "Externe Bearbeitung", VideoFormats.IsVideo(source));
                _externalEditReview = recovered;
                await PresentExternalResultAsync(recovered);
                return;
            }

            StatusText.Text = UiLanguage.T("Original für externe Bearbeitung sichern …");
            var snapshot = await Task.Run(() => ExternalEditTransaction.Begin(source));
            if (Dispatcher.HasShutdownStarted) return; // Snapshot survives app exit.

            var review = new ExternalEditReview(snapshot, id,
                editor?.Name ?? UiLanguage.T("Windows: Öffnen mit …"),
                VideoFormats.IsVideo(source));
            _externalEditReview = review;
            UpdateEditControls();

            Process? process = null;
            try
            {
                // The editor gets the ACTUAL original path. There is no local
                // working photo, no import dialog and no sidecar.
                if (!File.Exists(editor.ExecutablePath))
                    throw new FileNotFoundException(UiLanguage.T(
                        "Programmdatei nicht gefunden: ") + editor.ExecutablePath);
                var start = new ProcessStartInfo
                {
                    FileName = editor.ExecutablePath,
                    WorkingDirectory = Path.GetDirectoryName(source)!,
                    UseShellExecute = false
                };
                // ArgumentList handles spaces/umlauts without shell quoting.
                start.ArgumentList.Add(source);

                process = await Task.Run(() => Process.Start(start))
                    ?? throw new InvalidOperationException(UiLanguage.T(
                        UiLanguage.T("Das externe Programm hat keinen beobachtbaren Prozess zurückgegeben.")));
                StatusText.Text = UiLanguage.T("Externes Programm läuft: ") + review.Name;
                // A tracked process returns control to PCasa after termination.
                // Never wait synchronously on the WPF UI thread.
                await process.WaitForExitAsync();
                if (Dispatcher.HasShutdownStarted || !ReferenceEquals(
                        _externalEditReview, review)) return;
                await PresentExternalResultAsync(review);
            }
            catch (Exception)
            {
                // An abnormal return must NEVER discard the only backup.
                // If the process was launched, leave the review recoverable.
                if (process is null && ReferenceEquals(_externalEditReview, review))
                {
                    await Task.Run(() => ExternalEditTransaction.Commit(
                        snapshot, null, ExternalEditDecision.Cancel));
                    _externalEditReview = null;
                    UpdateEditControls();
                }
                throw;
            }
            finally { process?.Dispose(); }
        }
        catch (Exception ex) { ShowFileActionError(ex); }
    }

    private async Task PresentExternalResultAsync(ExternalEditReview review)
    {
        if (!ReferenceEquals(_externalEditReview, review)) return;
        try
        {
            review.ResultPath = await Task.Run(() =>
                ExternalEditTransaction.DetectResult(review.Snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException)
        {
            // Ambiguous exports are never guessed. Keep rollback accessible.
            PCasaDiagnostics.Write("ExternalEdit.AmbiguousOutput", ex);
            PcasaDialog.Show(this, ex.Message, UiLanguage.T(
                "Externe Bearbeitung"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        review.Ready = true;
        if (review.IsVideo)
        {
            var dialog = new ExternalVideoReviewWindow(
                Path.GetFileName(review.Snapshot.OriginalPath),
                decision => CompleteExternalReviewAsync(decision),
                review.ResultPath is not null) { Owner = this };
            dialog.Show();
        }
        else
        {
            if (!_viewerOpen)
                BrowseSurface_ImageOpened(BrowseSurface,
                    new PCasa.App.Controls.BrowseImageClickedEventArgs(
                        review.ImageId, System.Windows.Input.ModifierKeys.None));
            if (!_viewerOpen ||
                _viewerSequence is null || _viewerCurrentIndex < 0 ||
                _viewerSequence.At(_viewerCurrentIndex).ImageId != review.ImageId)
            {
                // The gallery may have been filtered/rebuilt during a long
                // external edit. Still expose the SAME three PCasa choices;
                // never strand the backup merely because the viewer is hidden.
                new ExternalVideoReviewWindow(
                    Path.GetFileName(review.Snapshot.OriginalPath),
                    decision => CompleteExternalReviewAsync(decision),
                    review.ResultPath is not null) { Owner = this }.Show();
                return;
            }

            if (review.ResultPath is { } output)
            {
                try
                {
                    var required = GetViewerRequiredPixelSize();
                    var bitmap = await Task.Run(() =>
                    {
                        var (width, height) = EditableImageFormats.Dimensions(output);
                        var info = new PhotoViewerImageInfo(review.ImageId, output,
                            Path.GetFileName(output), width, height);
                        return PhotoViewerImageLoader.Load(info, required,
                            CancellationToken.None);
                    });
                    if (!ReferenceEquals(_externalEditReview, review)) return;
                    // Fence the previous asynchronous original decode, which
                    // otherwise could replace the new result on-screen.
                    _viewerDecodeCancellation?.Cancel();
                    ++_viewerImageRequest;
                    _viewerImageInfo = new PhotoViewerImageInfo(review.ImageId,
                        review.Snapshot.OriginalPath,
                        Path.GetFileName(review.Snapshot.OriginalPath),
                        bitmap.PixelWidth, bitmap.PixelHeight);
                    _photoEditSession = null;
                    SetViewerBitmap(bitmap);
                    ApplyViewerImageLayout();
                }
                catch (Exception ex)
                {
                    // If an external output cannot be decoded, keep the
                    // verified backup and allow Cancel. Never save blindly.
                    PCasaDiagnostics.Write("ExternalEdit.ResultPreview", ex);
                    review.ResultPath = null;
                    PcasaDialog.Show(this, ex.Message, UiLanguage.T(
                        "Externe Bearbeitung"), MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }
        StatusText.Text = review.ResultPath is null
            ? UiLanguage.T("Externes Programm beendet – keine Änderungen gefunden.")
            : UiLanguage.T("Externe Bearbeitung: Abbrechen / Als Kopie speichern / Speichern");
        UpdateEditControls();
    }

    private async Task<bool> CompleteExternalReviewAsync(ExternalEditDecision decision)
    {
        var review = _externalEditReview;
        if (review is null || !review.Ready || review.Busy) return false;
        review.Busy = true;
        UpdateEditControls();
        try
        {
            var committed = await Task.Run(() =>
                ExternalEditTransaction.Commit(review.Snapshot,
                    review.ResultPath, decision));
            // The file transaction is now complete, even if a subsequent
            // catalog refresh fails. It must not be submitted a second time.
            _externalEditReview = null;
            UpdateEditControls();

            try
            {
                await RefreshExternalEditCatalogAsync(review, committed);
            }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("ExternalEdit.CatalogRefresh", ex);
                PcasaDialog.Show(this, UiLanguage.T(
                    "Die Dateientscheidung wurde gespeichert, aber die Galerieaktualisierung ist fehlgeschlagen: ") +
                    ex.Message, UiLanguage.T("Externe Bearbeitung"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            StatusText.Text = decision switch
            {
                ExternalEditDecision.Cancel => UiLanguage.T("Original wiederhergestellt."),
                ExternalEditDecision.SaveCopy => UiLanguage.T("Kopie gespeichert: ") +
                    Path.GetFileName(committed),
                _ => UiLanguage.T("Externe Bearbeitung gespeichert.")
            };
            return true;
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("ExternalEdit.Commit", ex);
            PcasaDialog.Show(this, ex.Message,
                UiLanguage.T("Externe Bearbeitung"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            review.Busy = false;
            if (!Dispatcher.HasShutdownStarted) UpdateEditControls();
        }
    }

    private async Task RefreshExternalEditCatalogAsync(
        ExternalEditReview review, string? committed)
    {
        var original = review.Snapshot.OriginalPath;
        var folderId = await _galleryCatalogReader.ReadAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            using var db = _database.OpenConnection();
            using var command = db.CreateCommand();
            command.CommandText =
                "SELECT folder_id FROM images WHERE id=$id LIMIT 1";
            command.Parameters.AddWithValue("$id", review.ImageId);
            return command.ExecuteScalar() is { } v
                ? Convert.ToInt64(v) : 0L;
        });
        var label = UiLanguage.T("Externe Bearbeitung katalogisieren");
        var key = "external:catalog:" + Guid.NewGuid().ToString("N");
        BeginVisibleManualOperation(key, label, committed is not null &&
            !committed.Equals(original, StringComparison.OrdinalIgnoreCase) ? 2 : 1);
        try
        {
            await _scheduler.EnqueueAsync(key, SchedulerPriority.ManualAction,
                token =>
                {
                    var root = _folderRepository.GetAll().FirstOrDefault(
                        f => f.Id == folderId)
                        ?? throw new IOException(
                            UiLanguage.T("Der Katalogordner der externen Bearbeitung fehlt."));
                    new BrowseCacheRepository(_database).InvalidateImage(review.ImageId);
                    var res = _folderScanner.ScanFile(root, original,
                        cancellationToken: token,
                        publishCatalogChange: false, forceExifRefresh: true);
                    if (res.Failed > 0)
                        throw new IOException(UiLanguage.T("Original konnte nicht katalogisiert werden."));
                    if (committed is { } copy &&
                        !copy.Equals(original, StringComparison.OrdinalIgnoreCase))
                    {
                        var result = _folderScanner.ScanFile(root, copy,
                            cancellationToken: token,
                            publishCatalogChange: false, forceExifRefresh: true);
                        if (result.Failed > 0 || result.ImageFilesSeen == 0)
                            throw new IOException(UiLanguage.T("Kopie konnte nicht katalogisiert werden."));
                    }
                    return Task.CompletedTask;
                });
        }
        finally { EndVisibleManualOperation(key); }

        _viewerOriginalCache.Remove(review.ImageId);
        if (committed is { } copyPath &&
            !copyPath.Equals(original, StringComparison.OrdinalIgnoreCase))
        {
            // The same viewer activation path as Crop -> Save as Copy.
            await ActivateSavedCopyAsync(new EditWriteRecipe(review.ImageId,
                folderId, original, [], copyPath));
        }
        else
        {
            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                warmViewport: false);
            if (_viewerOpen && _viewerImageInfo?.Id == review.ImageId)
                ShowViewerImage();
        }
    }
}
