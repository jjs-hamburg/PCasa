using System.IO;
using System.Windows.Controls;
using PCasa.App.Models;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private void ShowFolderStatusLegend(System.Windows.Window owner)
    {
        System.Windows.MessageBox.Show(
            owner,
            UiLanguage.T("✓  Bekanntes Material vollständig verarbeitet") + "\n" +
            UiLanguage.T("↻  Aktualisierung läuft") + "\n" +
            UiLanguage.T("◷  Aktualisierung steht aus") + "\n" +
            UiLanguage.T("⊘  Ordner vorübergehend nicht erreichbar") + "\n" +
            UiLanguage.T("!  Fehler bei der Aktualisierung") + "\n" +
            UiLanguage.T("?  Prüfstatus noch unbekannt") + "\n\n" +
            UiLanguage.T("Die Formen unterscheiden die Zustände unabhängig von den Farben. " +
                "Ein Häkchen erscheint nur nach bestätigtem abgeschlossenem Scan.") + "\n" +
            UiLanguage.T("Im Modus 'Nur Aktivität und Probleme' sind Häkchen ausgeblendet.") + "\n" +
            UiLanguage.T("Der rote Punkt zeigt unabhängig davon noch offene Tag-Schreibvorgänge."),
            UiLanguage.T("PCasa – Ordnerstatus"),
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
        // Red dots belong to FOLDER nodes. The one durable truth is the
        // image-level pending_tag_edits table; no independent marker state.
        // Build direct/ancestor lookups once instead of scanning all pending
        // paths for EVERY tree node (quadratic on large NAS trees).
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in paths)
        {
            var own = Path.GetDirectoryName(file.Replace('/', '\\'))?.TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(own)) continue;
            direct.Add(own);
            var parent = Path.GetDirectoryName(own);
            while (!string.IsNullOrWhiteSpace(parent))
            {
                var normalized = parent.TrimEnd('\\');
                if (normalized.Length == 0 || !ancestors.Add(normalized))
                    break;
                parent = Path.GetDirectoryName(normalized);
            }
        }
        foreach (var root in FoldersTree.Items.OfType<TreeViewItem>())
            SetPendingTagDot(root, direct, ancestors);
    }

    private static void SetPendingTagDot(TreeViewItem item,
        IReadOnlySet<string> direct, IReadOnlySet<string> ancestors)
    {
        if (item.Tag is FolderTreeTag folder)
        {
            var path = folder.FullPath.Replace('/', '\\').TrimEnd('\\');
            folder.HasPendingTagWrites = direct.Contains(path);
            folder.HasPendingChildTagWrites =
                !folder.HasPendingTagWrites && ancestors.Contains(path);
        }
        foreach (var child in item.Items.OfType<TreeViewItem>())
            SetPendingTagDot(child, direct, ancestors);
    }

    private void ApplyFolderStatusMode()
    {
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
            tag.StatusDescription = UiLanguage.T("SMB-Ordner derzeit nicht erreichbar. Der Katalog bleibt erhalten.");
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
            "Ready" => state.UpdatedUtc == DateTime.MinValue
                ? UiLanguage.T("Vollständiger Erstscan früher bestätigt (Katalog)")
                : UiLanguage.T("Bekannte Änderungen verarbeitet: ") + state.UpdatedUtc.ToLocalTime().ToString("g"),
            "Working" => UiLanguage.T("Ordner wird gerade aktualisiert"),
            "Pending" => UiLanguage.T("Aktualisierung steht aus"),
            "Failed" => UiLanguage.T("Letzte Aktualisierung fehlgeschlagen"),
            "Offline" => UiLanguage.T("Netzwerkverbindung derzeit unterbrochen"),
            "Unknown" => UiLanguage.T("Noch kein Prüfstatus aus dieser Sitzung vorhanden"),
            _ => ""
        };
    }
}
