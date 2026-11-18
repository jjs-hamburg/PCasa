using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCasa.App.Services;
using PCasa.App.Models;

namespace PCasa.App;

public partial class MainWindow
{
    private static bool ValidRenamePart(string text) =>
        !string.IsNullOrWhiteSpace(text) && text is not "." and not ".." &&
        text.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !text.EndsWith(' ') && !text.EndsWith('.');

    private string? AskRename(string original, string extension, bool folder)
    {
        var dialog = new Window {
            Owner = this, Width = 460, Height = 182,
            MinWidth = 380, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = UiLanguage.T(folder ? "Ordner umbenennen" : "Bild umbenennen")
        };
        dialog.SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        dialog.SetResourceReference(ForegroundProperty, "PcasaTextBrush");
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock {
            Text = UiLanguage.T("Neuer Name"), Margin = new Thickness(0,0,0,5)
        });
        var text = new TextBox { Text = original, MinHeight = 28 };
        root.Children.Add(text);
        if (!folder)
            root.Children.Add(new TextBlock {
                Text = UiLanguage.T("Dateiendung bleibt unverändert: ") + extension,
                FontSize = 11, Margin = new Thickness(0,5,0,0)
            });
        var row = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0,13,0,0)
        };
        var cancel = new Button { Content = UiLanguage.T("Abbrechen"),
            Width = 90, MinHeight = 28, IsCancel = true, Margin = new Thickness(0,0,8,0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var accept = new Button { Content = UiLanguage.T("Umbenennen"),
            Width = 106, MinHeight = 28, IsDefault = true };
        accept.Click += (_, _) => dialog.DialogResult = true;
        row.Children.Add(cancel); row.Children.Add(accept);
        root.Children.Add(row);
        dialog.Content = root;
        text.Loaded += (_, _) => { text.SelectAll(); text.Focus(); };
        if (dialog.ShowDialog() != true) return null;
        var candidate = text.Text.Trim();
        if (!ValidRenamePart(candidate))
        {
            PcasaDialog.Show(this, UiLanguage.T("Ungültiger Name."), "PCasa",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        return candidate;
    }

    private async Task<bool> ImageRenameAvailableAsync(long imageId)
    {
        try
        {
            var paths = await SelectedGalleryPathsAsync([imageId]);
            if (paths.Length != 1) return false;
            return await Task.Run(() => File.Exists(paths[0]));
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Rename.ImageAvailability", ex);
            return false;
        }
    }

    private async Task<bool> FolderRenameAvailableAsync(string path)
    {
        try
        {
            if (Path.GetPathRoot(path)?.TrimEnd('\\') ==
                path.TrimEnd('\\')) return false;
            return await Task.Run(() => Directory.Exists(path));
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Rename.FolderAvailability", ex);
            return false;
        }
    }

    private async Task RenameImageAsync(long imageId)
    {
        string source;
        try
        {
            var paths = await SelectedGalleryPathsAsync([imageId]);
            if (paths.Length != 1) return;
            source = paths[0];
        }
        catch (Exception ex) { ShowFileActionError(ex); return; }
        var ext = Path.GetExtension(source);
        var currentName = Path.GetFileNameWithoutExtension(source);
        var name = AskRename(currentName, ext, folder: false);
        if (name is null || name == currentName) return;
        var target = Path.Combine(Path.GetDirectoryName(source)!, name + ext);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return; // No overwrite or ambiguous case-only rename.
        var key = "image:rename:" + Guid.NewGuid().ToString("N");
        BeginVisibleManualOperation(key, UiLanguage.T("Bild umbenennen"), 1);
        try
        {
            await _scheduler.RunAsync(key, SchedulerPriority.ManualAction,
                token =>
                {
                    var root = _folderRepository.GetAll().Where(entry =>
                        FolderExclusionRules.IsAtOrBelow(source, entry.LocalPath))
                        .OrderByDescending(entry => entry.LocalPath.Length)
                        .FirstOrDefault() ?? throw new IOException(
                            "Bild liegt nicht in einem überwachten Ordner.");
                    using var guard = SourceFileLocks.EnterPair(source, target, token);
                    if (!File.Exists(source)) throw new FileNotFoundException(
                        "Originaldatei ist nicht erreichbar.", source);
                    if (File.Exists(target) || Directory.Exists(target))
                        throw new IOException("Zieldatei existiert bereits: " + target);
                    var intent = Guid.NewGuid().ToString("N");
                    _imageCatalog.BeginRelocationIntent(intent, imageId, source, target, root.Id);
                    File.Move(source, target);
                    // This existing relocation path preserves image identity,
                    // packed thumbnails, tags, GPS and every EXIF cache row.
                    if (!_imageCatalog.RelocateImage(imageId, source, target, root,
                            token, relocationIntentId: intent))
                        throw new IOException(
                            "Datei wurde umbenannt; Katalogaktualisierung ausstehend.");
                    try
                    {
                        new UndoHistoryService(UndoOptions.Load())
                            .RebaseSourcePaths(source, target, subtree: false);
                    }
                    catch (Exception ex)
                    { PCasaDiagnostics.Write("Rename.ImageUndo", ex); }
                    return true;
                }, workKind: SchedulerWorkKind.Catalog);
            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                warmViewport: false);
            _ = RefreshRestoreBadgesAsync();
            StatusText.Text = UiLanguage.T("Umbenannt: ") + Path.GetFileName(target);
        }
        catch (Exception ex) { ShowFileActionError(ex); }
        finally { EndVisibleManualOperation(key); }
    }

    private async Task RenameFolderAsync(string source)
    {
        if (Path.GetPathRoot(source)?.TrimEnd('\\') ==
            source.TrimEnd('\\')) return;
        var initialName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        var newName = AskRename(initialName, string.Empty, folder: true);
        if (newName is null || newName == initialName) return;
        var destination = Path.Combine(Path.GetDirectoryName(
            Path.TrimEndingDirectorySeparator(source))!, newName);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            return;

        // Stop stale producer callbacks; the source-generation fence below
        // independently protects committed SQLite state.
        _backgroundCurrentScanCancellation?.Cancel();
        _activeScanCancellation?.Cancel();
        _backgroundDebounceTimer?.Stop();
        if (_allFolderProjectionRootPath is not null)
            HideExplorerFolders();

        var key = "folder:rename:" + Guid.NewGuid().ToString("N");
        BeginVisibleManualOperation(key, UiLanguage.T("Ordner umbenennen"), 1);
        try
        {
            var (oldCanonical, newCanonical, renameReceiptId) =
                await _scheduler.RunAsync(
                key, SchedulerPriority.ManualAction, token =>
                {
                    token.ThrowIfCancellationRequested();
                    var roots = _folderRepository.GetAll();
                    var owner = roots.Where(entry =>
                        FolderExclusionRules.IsAtOrBelow(source, entry.LocalPath))
                        .OrderByDescending(entry => entry.LocalPath.Length)
                        .FirstOrDefault() ?? throw new IOException(
                            "Ordner liegt nicht innerhalb eines überwachten Bereichs.");
                    if (!Directory.Exists(source))
                        throw new DirectoryNotFoundException(
                            "Quellordner nicht erreichbar: " + source);
                    if (Directory.Exists(destination) || File.Exists(destination))
                        throw new IOException("Zielordner existiert bereits: " + destination);
                    var canonicalSource = FolderScanner.ResolveCanonicalFullPath(owner, source);
                    var canonicalTarget = CatalogRenameService.CanonicalTarget(
                        owner, source, destination);
                    var rename = new CatalogRenameService(_database);
                    var receiptId = Guid.NewGuid().ToString("N");
                    // A durable receipt is written BEFORE Directory.Move.
                    // A process crash after moving the folder but before
                    // the SQLite commit is reconciled at next startup.
                    rename.PrepareDirectoryRename(receiptId, source, destination,
                        canonicalSource, canonicalTarget);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        Directory.Move(source, destination);
                        // A single SQLite commit rebases all descendant
                        // paths and marks the receipt catalog-applied.
                        rename.RebaseDirectory(source, destination,
                            canonicalSource, canonicalTarget, receiptId);
                    }
                    catch
                    {
                        try
                        {
                            // If the physical move completed but SQLite
                            // failed, attempt to put the directory back.
                            // If rollback ALSO fails, retain the receipt:
                            // next startup will reconcile the moved tree.
                            if (!Directory.Exists(source) &&
                                Directory.Exists(destination))
                                Directory.Move(destination, source);
                            if (Directory.Exists(source) &&
                                !Directory.Exists(destination))
                                rename.AbortDirectoryRename(receiptId);
                        }
                        catch (Exception rollback)
                        { PCasaDiagnostics.Write("Rename.FolderRollback", rollback); }
                        throw;
                    }
                    return (canonicalSource, canonicalTarget, receiptId);
                }, workKind: SchedulerWorkKind.Catalog);
            RebaseRenamedTreePreferences(source, destination,
                oldCanonical, newCanonical);
            // Keep the receipt until the central Undo metadata has also been
            // moved. This path runs only once; interrupted completion is
            // replayed on next startup without touching thumbnail bytes.
            await Task.Run(() => new UndoHistoryService(UndoOptions.Load())
                .RebaseSourcePaths(source, destination, subtree: true));
            new CatalogRenameService(_database)
                .CompleteDirectoryRename(renameReceiptId);
            // The current WPF nodes still carry OLD paths. The new tree must
            // use the just-rebased persisted expansion set rather than taking
            // an obsolete snapshot of those old UI node identities.
            if (FoldersTree.SelectedItem is TreeViewItem selected &&
                selected.Tag is FolderTreeTag selectedTag &&
                FolderExclusionRules.IsAtOrBelow(selectedTag.FullPath, source))
                selected.Tag = CreateFolderTreeTag(
                    destination + selectedTag.FullPath[source.Length..],
                    selectedTag.PhotoFolderId);
            _folderTreeInitialized = false;
            _dropTargetProjection = await _scheduler.RunAsync(
                "folder:rename:configuration:" + Guid.NewGuid().ToString("N"),
                SchedulerPriority.Interactive,
                token => ReadFolderTreeConfigurationSnapshot(),
                workKind: SchedulerWorkKind.CatalogRead);
            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                warmViewport: false);
            RefreshFolderTree(preserveGalleryAnchor: true);
            RefreshBackgroundMonitor();
            _ = RefreshRestoreBadgesAsync();
            StatusText.Text = UiLanguage.T("Ordner umbenannt: ") + destination;
        }
        catch (Exception ex) { ShowFileActionError(ex); }
        finally { EndVisibleManualOperation(key); }
    }

    private void RebaseRenamedTreePreferences(string oldPath, string newPath,
        string oldCanonical, string newCanonical)
    {
        static string? Rewrite(string? value, string before, string after)
            => value is not null &&
               FolderExclusionRules.IsAtOrBelow(value, before)
                ? after + value[before.Length..] : value;

        _appearance.SelectedTreePath = Rewrite(
            _appearance.SelectedTreePath, oldPath, newPath);
        _appearance.ExpandedTreePaths = (_appearance.ExpandedTreePaths ?? [])
            .Select(path => Rewrite(path, oldPath, newPath)!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _appearance.LastFolderCanonicalPath = Rewrite(
            _appearance.LastFolderCanonicalPath, oldCanonical, newCanonical);
        var empties = _createdEmptyFolderPaths
            .Select(path => Rewrite(path, oldPath, newPath)!)
            .ToArray();
        _createdEmptyFolderPaths.Clear();
        foreach (var path in empties) _createdEmptyFolderPaths.Add(path);
        SaveAppearance();
    }
}
