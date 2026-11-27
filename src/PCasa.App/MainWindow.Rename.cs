using System.IO;
using System.Text.Json;
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
            var request = new ImageRenameRecipe(imageId, source, target);
            await _scheduler.EnqueueDurableRecipeAsync(key, "image-rename-v1",
                JsonSerializer.Serialize(request), SchedulerPriority.ManualAction);
            if (_shutdownStarted || Dispatcher.HasShutdownStarted) return;
            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                warmViewport: false);
            _ = RefreshRestoreBadgesAsync();
            StatusText.Text = UiLanguage.T("Umbenannt: ") + Path.GetFileName(target);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_shutdownStarted) ShowFileActionError(ex); }
        finally { EndVisibleManualOperation(key); }
    }

    private sealed record ImageRenameRecipe(long ImageId, string Path,
        string Destination, string? OriginalHash = null);

    private SchedulerTaskPackage BuildImageRenamePackage(string key, string payload)
    {
        var request = JsonSerializer.Deserialize<ImageRenameRecipe>(payload)
            ?? throw new InvalidDataException("Invalid image rename recipe.");
        var package = new SchedulerTaskPackage(key);
        package.AddStep(token =>
        {
            token.ThrowIfCancellationRequested();
            var source = request.Path;
            var target = request.Destination;
            _scheduler.ReportProgress(key, UiLanguage.T("Bild umbenennen"), 0, 1, source);
            var root = _folderRepository.GetAll().Where(entry =>
                FolderExclusionRules.IsAtOrBelow(source, entry.LocalPath))
                .OrderByDescending(entry => entry.LocalPath.Length)
                .FirstOrDefault() ?? throw new IOException(
                    "Bild liegt nicht in einem überwachten Ordner.");
            using var guard = SourceFileLocks.EnterPair(source, target, token);
            var sourcePresence = SourceFilePresence.Probe(source);
            var targetPresence = SourceFilePresence.Probe(target);
            var intent = key;
            if (sourcePresence == FilePresence.Present &&
                targetPresence == FilePresence.ConfirmedMissing &&
                CatalogImageIdAtPath(source) == request.ImageId)
            {
                var hash = WriteHash(source);
                if (request.OriginalHash is not null && hash != request.OriginalHash)
                    throw new IOException("Rename source changed after preparation.");
                request = request with { OriginalHash = hash };
                PersistWriteRecipe(key, request);
                if (!_imageCatalog.PendingRelocationIds().Contains(intent))
                    _imageCatalog.BeginRelocationIntent(intent, request.ImageId, source, target, root.Id);
                File.Move(source, target);
            }
            else if (sourcePresence != FilePresence.ConfirmedMissing ||
                targetPresence != FilePresence.Present || request.OriginalHash is null ||
                WriteHash(target) != request.OriginalHash)
                throw new IOException("Unklarer oder inzwischen veränderter Dateizustand: " + source);

            if (CatalogImageIdAtPath(target) != request.ImageId &&
                !_imageCatalog.RelocateImage(request.ImageId, source, target, root,
                    token, relocationIntentId: intent))
                throw new IOException("Datei wurde umbenannt; Katalogaktualisierung ausstehend.");
            new UndoHistoryService(UndoOptions.Load())
                .RebaseSourcePaths(source, target, subtree: false);
            _scheduler.ReportProgress(key, UiLanguage.T("Bild umbenennen"), 1, 1, target);
            return Task.CompletedTask;
        });
        return package;
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
            await _scheduler.EnqueueDurableRecipeAsync(key, "folder-rename-v1",
                JsonSerializer.Serialize(new FolderRenameRecipe(source, destination)),
                SchedulerPriority.ManualAction);
            if (_shutdownStarted || Dispatcher.HasShutdownStarted) return;
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

    private sealed record FolderRenameRecipe(string Path, string Destination,
        string? CanonicalSource = null, string? CanonicalTarget = null);

    private SchedulerTaskPackage BuildFolderRenamePackage(string key, string payload)
    {
        var request = JsonSerializer.Deserialize<FolderRenameRecipe>(payload)
            ?? throw new InvalidDataException("Invalid folder rename recipe.");
        var package = new SchedulerTaskPackage(key);
        package.AddStep(token =>
        {
            token.ThrowIfCancellationRequested();
            var source = request.Path;
            var target = request.Destination;
            var rename = new CatalogRenameService(_database);
            var receipt = rename.PendingRenames().FirstOrDefault(row => row.Id == key);
            var sourcePresence = SourceFilePresence.Probe(source);
            var targetPresence = SourceFilePresence.Probe(target);
            if (sourcePresence == FilePresence.Present &&
                targetPresence == FilePresence.ConfirmedMissing &&
                receipt?.CatalogApplied != true)
            {
                if (!Directory.Exists(source))
                    throw new IOException("Rename source is not a directory.");
                var owner = _folderRepository.GetAll().Where(entry =>
                    FolderExclusionRules.IsAtOrBelow(source, entry.LocalPath))
                    .OrderByDescending(entry => entry.LocalPath.Length)
                    .FirstOrDefault() ?? throw new IOException(
                        "Ordner liegt nicht innerhalb eines überwachten Bereichs.");
                var canonicalSource = FolderScanner.ResolveCanonicalFullPath(owner, source);
                var canonicalTarget = CatalogRenameService.CanonicalTarget(owner, source, target);
                if (request.CanonicalSource is not null &&
                    (!string.Equals(request.CanonicalSource, canonicalSource, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(request.CanonicalTarget, canonicalTarget, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Folder rename source changed after preparation.");
                request = request with { CanonicalSource = canonicalSource, CanonicalTarget = canonicalTarget };
                PersistWriteRecipe(key, request);
                if (receipt is null)
                    rename.PrepareDirectoryRename(key, source, target, canonicalSource, canonicalTarget);
                token.ThrowIfCancellationRequested();
                Directory.Move(source, target);
                rename.RebaseDirectory(source, target, canonicalSource, canonicalTarget, key);
            }
            else if (sourcePresence == FilePresence.ConfirmedMissing &&
                targetPresence == FilePresence.Present && Directory.Exists(target) &&
                receipt is not null)
            {
                if (!receipt.CatalogApplied)
                    rename.RebaseDirectory(source, target, receipt.CanonicalSource, receipt.CanonicalTarget, key);
                request = request with { CanonicalSource = receipt.CanonicalSource,
                    CanonicalTarget = receipt.CanonicalTarget };
                PersistWriteRecipe(key, request);
            }
            else throw new IOException("Unklarer oder inzwischen veränderter Ordnerzustand: " + source);
            _scheduler.ReportProgress(key, UiLanguage.T("Ordner umbenennen"), 1, 2, target);
            return Task.CompletedTask;
        });
        package.AddStep(token =>
        {
            token.ThrowIfCancellationRequested();
            if (request.CanonicalSource is not { } oldCanonical ||
                request.CanonicalTarget is not { } newCanonical)
                throw new IOException("Folder rename receipt incomplete.");
            new UndoHistoryService(UndoOptions.Load())
                .RebaseSourcePaths(request.Path, request.Destination, subtree: true);
            Dispatcher.Invoke(() => RebaseRenamedTreePreferences(request.Path,
                request.Destination, oldCanonical, newCanonical));
            var rename = new CatalogRenameService(_database);
            if (rename.PendingRenames().Any(row => row.Id == key && row.CatalogApplied))
                rename.CompleteDirectoryRename(key);
            _scheduler.ReportProgress(key, UiLanguage.T("Ordner umbenennen"), 2, 2, request.Destination);
            return Task.CompletedTask;
        });
        return package;
    }

    private async Task ObserveRecoveredFolderMutationAsync(Task completion, string key)
    {
        try
        {
            await completion;
            if (!_shutdownStarted && !Dispatcher.HasShutdownStarted)
                await RefreshFolderMutationProjectionAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PCasaDiagnostics.Write("Folder.Mutation.Recovery", ex); }
        finally
        {
            if (!_shutdownStarted && !Dispatcher.HasShutdownStarted)
                EndVisibleManualOperation(key);
        }
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
