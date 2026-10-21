using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private FolderTreeTag? ContextFolder()
        => (FoldersTree.SelectedItem as TreeViewItem)?.Tag as FolderTreeTag;

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
        var root = ResolveMonitoredFolder(tag.FullPath);
        var folders = _photoFolders.Where(f => IsInside(f.LocalPath, tag.FullPath)).ToArray();
        var total = folders.Sum(f => f.ImageCount);
        var excluded = root is not null &&
            FolderExclusionRules.IsExcluded(tag.FullPath,
                _folderRepository.GetExcludedPaths(root.Id));
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
        var root = ResolveMonitoredFolder(tag.FullPath);
        if (root is null)
        {
            MessageBox.Show(this, "Dieser Ordner liegt außerhalb der PCasa-Überwachung.",
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var path = FolderExclusionRules.Normalize(tag.FullPath);
        if (FolderExclusionRules.IsExcluded(path,
                _folderRepository.GetExcludedPaths(root.Id)))
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
