using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{

    // Input handlers use the already published folder-tree snapshot.
    // Only the scheduler may perform authoritative SQLite revalidation.
    private MonitoredFolder? DisplayedMonitoredRoot(string path)
        => FolderExclusionRules.FindOwner(
            path, _dropTargetProjection.Select(entry => entry.Folder));

    private IReadOnlyList<string> DisplayedExcludedPaths(long folderId)
        => _dropTargetProjection.FirstOrDefault(entry =>
            entry.Folder.Id == folderId).ExcludedPaths ?? Array.Empty<string>();

    private FolderTreeTag? ContextFolder()
        => (FoldersTree.SelectedItem as TreeViewItem)?.Tag as FolderTreeTag;

    private async void FolderContext_CreateDirectory_Click(object sender, RoutedEventArgs e)
    {
        var selected = FoldersTree.SelectedItem as TreeViewItem;
        if (selected?.Tag is not FolderTreeTag tag) return;
        if (!ValidDragTarget(tag.FullPath))
        {
            MessageBox.Show(this, "Der Zielordner wird nicht überwacht.", "PCasa",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new Window
        {
            Title = "Neuen Unterordner anlegen", Owner = this,
            Width = 355, Height = 142, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "PcasaPanelBrush");
        var panel = new StackPanel { Margin = new Thickness(14) };
        var nameField = new TextBox { Text = "", Margin = new Thickness(0, 0, 0, 10) };
        var accept = new Button { Content = "Anlegen", Width = 85,
            Style = (Style)FindResource("PcasaStatusButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        panel.Children.Add(nameField);
        panel.Children.Add(accept);
        dialog.Content = panel;
        accept.Click += (_, _) => dialog.DialogResult = true;
        nameField.Loaded += (_, _) => { nameField.SelectAll(); nameField.Focus(); };
        if (dialog.ShowDialog() != true) return;
        var name = nameField.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith(' ') || name.EndsWith('.'))
        {
            MessageBox.Show(this, "Ungültiger Ordnername.", "PCasa",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var destination = Path.Combine(tag.FullPath, name);
        var key = $"folder:create:{Guid.NewGuid():N}";
        // Publish the user's request immediately, before SMB/SQLite work.
        // The scheduler remains the sole authority for physical creation.
        var pendingAdded = _createdEmptyFolderPaths.Add(destination);
        if (FindFolderTreeItemByPath(FoldersTree, destination) is null)
        {
            var item = new TreeViewItem
            {
                Header = name, Tag = CreateFolderTreeTag(destination, null),
                ToolTip = destination, AllowDrop = true
            };
            selected.Items.Add(item);
            selected.IsExpanded = true;
        }
        StatusText.Text = "Ordner anlegen … " + name;
        BeginVisibleManualOperation(key, "Ordner anlegen", 1);
        try
        {
            await CreateGallerySubfolderAsync(destination, key);
            StatusText.Text = "Unterordner angelegt: " + destination;
        }
        catch (Exception ex)
        {
            if (pendingAdded)
            {
                _createdEmptyFolderPaths.Remove(destination);
                // Rebuild only the cached tree projection: no SQLite/NAS
                // reads on the dispatcher; rollback the optimistic node.
                RefreshFolderTree(preserveGalleryAnchor: true);
            }
            PCasaDiagnostics.Write("FolderContext.Create", ex);
            MessageBox.Show(this, ex.Message, "Ordner anlegen",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndVisibleManualOperation(key);
        }
    }

    private Task CreateGallerySubfolderAsync(string destination, string key)
        => _scheduler.EnqueueAsync(key, SchedulerPriority.ManualAction, token =>
            {
                token.ThrowIfCancellationRequested();
                _scheduler.ReportProgress(key, "Ordner auf Datenträger anlegen", 0, 1,
                    destination);
                var owner = FolderExclusionRules.FindOwner(
                    destination, _folderRepository.GetAll());
                if (owner is null || FolderExclusionRules.IsExcluded(
                    destination, _folderRepository.GetExcludedPaths(owner.Id)))
                    throw new IOException("Zielordner wird nicht überwacht.");
                if (Directory.Exists(destination) || File.Exists(destination))
                    throw new IOException("Der Ordner existiert bereits.");
                Directory.CreateDirectory(destination);
                try
                {
                    _folderRepository.RegisterCreatedDirectory(owner.Id, destination);
                }
                catch
                {
                    // Either both the new empty directory and its durable
                    // tree registration exist, or neither does. Never report
                    // a successful NAS mutation which vanishes on restart.
                    if (Directory.Exists(destination) &&
                        !Directory.EnumerateFileSystemEntries(destination).Any())
                        Directory.Delete(destination);
                    throw;
                }
                _scheduler.ReportProgress(key, "Ordner angelegt", 1, 1, destination);
                return Task.CompletedTask;
            }, workKind: SchedulerWorkKind.SourceProbe);

    private async void FolderContext_DeletePhysicalDirectory_Click(
        object sender, RoutedEventArgs e)
    {
        var tag = ContextFolder();
        if (tag is null) return;
        var path = FolderExclusionRules.Normalize(tag.FullPath);
        var owner = DisplayedMonitoredRoot(path);
        if (owner is null ||
            !FolderExclusionRules.IsAtOrBelow(path, owner.LocalPath))
        {
            MessageBox.Show(this, "Nur überwachte Ordner können hier gelöscht werden.",
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // A monitored root can contain another independently monitored root.
        // Never delete a tree that crosses such a boundary.
        if (_dropTargetProjection.Any(entry => entry.Folder.Id != owner.Id &&
            FolderExclusionRules.IsAtOrBelow(entry.Folder.LocalPath, path)))
        {
            MessageBox.Show(this,
                "Dieser Ordner enthält einen weiteren überwachten Stammordner. " +
                "Die Ordner müssen zuerst getrennt werden.",
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var count = _photoFolders.Where(f =>
            FolderExclusionRules.IsAtOrBelow(f.LocalPath, path))
            .Sum(f => f.ImageCount);
        if (MessageBox.Show(this,
            "Ordner einschließlich sämtlicher Unterordner und Dateien " +
            "DAUERHAFT auf dem Datenträger löschen?\n\n" +
            path + "\n\n" +
            $"{count:N0} katalogisierte Bilder sind betroffen. " +
            "Zusätzlich werden alle anderen Dateien in diesem Ordner gelöscht.\n\n" +
            "Dies ist NICHT nur ein Entfernen aus PCasa. " +
            "Auf Netzlaufwerken ist möglicherweise kein Papierkorb verfügbar.",
            "Ordner endgültig löschen", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var root = string.Equals(path,
            FolderExclusionRules.Normalize(owner.LocalPath),
            StringComparison.OrdinalIgnoreCase);
        var key = $"folder:filesystem-delete:{Guid.NewGuid():N}";
        try
        {
            // The existing removal owner invalidates ongoing catalog scans,
            // detaches the catalog and publishes the change. Never create
            // a parallel SQLite deletion implementation.
            await RemoveFolderFromPcasaAsync(owner.Id, path, root);
            BeginVisibleManualOperation(key, "Ordner auf Datenträger löschen", 1);
            try
            {
                await _scheduler.EnqueueAsync(key, SchedulerPriority.ManualAction,
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        _scheduler.ReportProgress(key, "Dateisystemordner löschen", 0, 1, path);
                        // Recheck without trusting a stale TreeView selection.
                        if (Path.GetPathRoot(path) is string driveRoot &&
                            string.Equals(path.TrimEnd('\\'),
                                driveRoot.TrimEnd('\\'),
                                StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Laufwerks- und Freigabewurzeln dürfen nicht gelöscht werden.");
                        if (Directory.Exists(path))
                            Directory.Delete(path, recursive: true);
                        _scheduler.ReportProgress(key, "Datenträgerordner gelöscht", 1, 1, path);
                        return Task.CompletedTask;
                    }, workKind: SchedulerWorkKind.SourceProbe);
            }
            finally
            {
                EndVisibleManualOperation(key);
            }
            StatusText.Text = "Ordner dauerhaft gelöscht: " + path;
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("FolderContext.PhysicalDelete", ex);
            MessageBox.Show(this,
                "Der Dateisystemordner konnte nicht vollständig gelöscht werden. " +
                "Die PCasa-Katalogentfernung wurde möglicherweise bereits durchgeführt.\n\n" +
                ex.Message, "Ordner löschen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FolderContext_OpenExplorer_Click(object sender, RoutedEventArgs e)
    {
        var tag = ContextFolder();
        if (tag is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = "\"" + tag.FullPath + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Windows Explorer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void FolderContext_Info_Click(object sender, RoutedEventArgs e)
    {
        var tag = ContextFolder();
        if (tag is null) return;
        var root = DisplayedMonitoredRoot(tag.FullPath);
        var folders = _photoFolders.Where(f => IsInside(f.LocalPath, tag.FullPath)).ToArray();
        var total = folders.Sum(f => f.ImageCount);
        var excluded = root is not null &&
            FolderExclusionRules.IsExcluded(tag.FullPath,
                DisplayedExcludedPaths(root.Id));
        var state = root is null ? "Nicht überwacht" :
            excluded ? "Im Ordnermanager deaktiviert" : "Überwacht";
        MessageBox.Show(this,
            $"Pfad: {tag.FullPath}\n\n" +
            $"Bilder (einschließlich Unterordner): {total:N0}\n" +
            $"Bildordner: {folders.Length:N0}\n" +
            $"Status: {state}",
            "PCasa – Ordnerinformationen", MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void FolderContext_Exclude_Click(object sender, RoutedEventArgs e)
    {
        var tag = ContextFolder();
        if (tag is null) return;
        var root = DisplayedMonitoredRoot(tag.FullPath);
        if (root is null)
        {
            MessageBox.Show(this, "Dieser Ordner liegt außerhalb der PCasa-Überwachung.",
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var path = FolderExclusionRules.Normalize(tag.FullPath);
        if (FolderExclusionRules.IsExcluded(path,
                DisplayedExcludedPaths(root.Id)))
        {
            MessageBox.Show(this, "Der Ordner ist bereits deaktiviert.",
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var removeMonitoredRoot = string.Equals(
            path, FolderExclusionRules.Normalize(root.LocalPath),
            StringComparison.OrdinalIgnoreCase);
        var count = _photoFolders.Where(f => IsInside(f.LocalPath, path))
            .Sum(f => f.ImageCount);
        var actionDescription = removeMonitoredRoot
            ? "Der überwachte Ordner wird auch aus dem Ordnermanager entfernt."
            : "Der Unterordner bleibt im Ordnermanager deaktiviert auswählbar.";
        var answer = MessageBox.Show(this,
            $"Ordner einschließlich Unterordnern aus PCasa entfernen?\n\n{path}\n\n" +
            $"{count:N0} katalogisierte Bilder verschwinden sofort aus PCasa.\n" +
            "Die Originaldateien bleiben unverändert. " + actionDescription,
            "Aus PCasa entfernen", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await RemoveFolderFromPcasaAsync(root.Id, path, removeMonitoredRoot);
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("FolderContext.Remove", ex);
            MessageBox.Show(this, "Die Bereinigung konnte nicht abgeschlossen werden.\n\n" +
                ex.Message, "PCasa", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
