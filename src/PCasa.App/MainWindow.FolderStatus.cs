using System.IO;
using System.Windows.Controls;
using PCasa.App.Models;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private void FolderStatusLegend_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            this,
            "✓  Bekanntes Material vollständig verarbeitet\n" +
            "↻  Aktualisierung läuft\n" +
            "◷  Aktualisierung steht aus\n" +
            "⊘  Ordner vorübergehend nicht erreichbar\n" +
            "!  Fehler bei der Aktualisierung\n" +
            "?  Prüfstatus noch unbekannt\n\n" +
            "Die Formen unterscheiden die Zustände unabhängig von den Farben. " +
            "Ein grünes Häkchen bestätigt nur die bekannten Änderungen.",
            "PCasa – Ordnerstatus",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private readonly FolderScanStatusTracker _folderScanStatuses = new();

    private FolderTreeTag CreateFolderTreeTag(string path, long? photoFolderId)
    {
        var tag = new FolderTreeTag(path, photoFolderId);
        ApplyFolderStatus(tag);
        return tag;
    }

    private int _pendingTagDisplayVersion;

    private async Task RefreshPendingTagDotsAsync()
    {
        if (Dispatcher.HasShutdownStarted || FoldersTree is null) return;
        var version = ++_pendingTagDisplayVersion;
        IReadOnlyList<string> paths;
        try
        {
            paths = await _scheduler.RunAsync(
                "catalog:pending-tag-dots:" + version,
                SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return new ImageTagRepository(_database).PendingTagPaths();
                },
                () => version == _pendingTagDisplayVersion,
                workKind: SchedulerWorkKind.CatalogRead);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { PCasaDiagnostics.Write("Tags.PendingDots", ex); return; }
        if (version != _pendingTagDisplayVersion || Dispatcher.HasShutdownStarted) return;
        foreach (var root in FoldersTree.Items.OfType<TreeViewItem>())
            SetPendingTagDot(root, paths);
    }

    private static void SetPendingTagDot(TreeViewItem item, IReadOnlyList<string> paths)
    {
        if (item.Tag is FolderTreeTag folder)
        {
            var directory = folder.FullPath.Replace('/', '\\').TrimEnd('\\') + "\\";
            folder.HasPendingTagWrites = paths.Any(path =>
                path.Replace('/', '\\').StartsWith(directory,
                    StringComparison.OrdinalIgnoreCase));
        }
        foreach (var child in item.Items.OfType<TreeViewItem>())
            SetPendingTagDot(child, paths);
    }

    private void ApplyFolderStatusMode()
    {
        FolderStatusAlwaysMenu.IsChecked = _appearance.FolderStatusMode == "Always";
        FolderStatusProblemsMenu.IsChecked = _appearance.FolderStatusMode is not ("Always" or "None");
        FolderStatusNeverMenu.IsChecked = _appearance.FolderStatusMode == "None";
        RefreshFolderStatusVisuals();
    }

    private void TrackFolderActivity(string path, string code, bool recursive = false)
    {
        try { _folderScanStatuses.Set(path, code, recursive); }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
    }

    private void ForgetFolderActivity(string path)
    {
        try
        {
            _folderScanStatuses.Forget(path);
            RefreshFolderStatusVisuals();
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
    }

    private void RefreshFolderStatusVisuals()
    {
        if (FoldersTree is null) return;
        foreach (var root in FoldersTree.Items.OfType<TreeViewItem>())
            RefreshFolderStatusVisuals(root);
    }

    private void RefreshFolderStatusVisuals(TreeViewItem item)
    {
        if (item.Tag is FolderTreeTag tag)
            ApplyFolderStatus(tag);
        foreach (var child in item.Items.OfType<TreeViewItem>())
            RefreshFolderStatusVisuals(child);
    }

    private void ApplyFolderStatus(FolderTreeTag tag)
    {
        if (_appearance.FolderStatusMode == "None")
        {
            tag.StatusCode = "None";
            tag.StatusDescription = "";
            return;
        }

        if (IsPathKnownOffline(tag.FullPath))
        {
            tag.StatusCode = "Offline";
            tag.StatusDescription = "SMB-Ordner derzeit nicht erreichbar. Der Katalog bleibt erhalten.";
            return;
        }

        FolderScanStatusTracker.State? tracked;
        try { tracked = _folderScanStatuses.Get(tag.FullPath); }
        catch (ArgumentException) { tracked = null; }
        catch (NotSupportedException) { tracked = null; }
        var state = tracked ?? default;
        var code = state.Code ?? "Unknown";
        if (_appearance.FolderStatusMode != "Always" && code is "Ready" or "Unknown")
            code = "None";

        tag.StatusCode = code;
        tag.StatusDescription = code switch
        {
            "Ready" => "Bekannte Änderungen verarbeitet: " + state.UpdatedUtc.ToLocalTime().ToString("g"),
            "Working" => "Ordner wird gerade aktualisiert",
            "Pending" => "Aktualisierung steht aus",
            "Failed" => "Letzte Aktualisierung fehlgeschlagen",
            "Offline" => "Netzwerkverbindung derzeit unterbrochen",
            "Unknown" => "Noch kein Prüfstatus aus dieser Sitzung vorhanden",
            _ => ""
        };
    }
}
