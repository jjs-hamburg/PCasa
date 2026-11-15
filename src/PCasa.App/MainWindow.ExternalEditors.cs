using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private ExternalEditorSettings _externalEditors = ExternalEditorSettings.Load();

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
        IReadOnlyList<ExternalEditorProgram> programs = sourceName is null
            ? [] : _externalEditors.ForFile(sourceName);
        foreach (var program in programs)
        {
            var current = program;
            var item = new MenuItem { Header = current.Name };
            item.Click += (_, _) => _ = StartExternalEditorAsync(id, current);
            menu.Items.Add(item);
        }
        if (programs.Count > 0) menu.Items.Add(new Separator());
        var manage = new MenuItem
        {
            Header = UiLanguage.T("Programme verwalten …")
        };
        manage.Click += (_, _) => ConfigureExternalEditors();
        menu.Items.Add(manage);
        // The unconfigured system fallback deliberately remains last.
        var windows = new MenuItem
        {
            Header = UiLanguage.T("Windows: Öffnen mit …")
        };
        windows.Click += (_, _) => _ = StartExternalEditorAsync(id, null);
        menu.Items.Add(windows);
    }

    private void ExternalEditQuick_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerImageInfo is not { } info) return;
        var context = new ContextMenu
        {
            PlacementTarget = ExternalEditQuickButton,
            Placement = PlacementMode.Bottom
        };
        PopulateExternalEditorMenu(context, info.Id, info.LocalPath);
        context.IsOpen = true;
    }

    private void ConfigureExternalEditors()
    {
        var dialog = new ExternalEditorsWindow(_externalEditors) { Owner = this };
        if (dialog.ShowDialog() == true &&
            dialog.SavedSettings is { } saved)
            _externalEditors = saved;
    }

    private async Task StartExternalEditorAsync(long id, ExternalEditorProgram? editor)
    {
        try
        {
            // Only the clicked/visible image, never a stale selection or a
            // whole 500k-image index. Catalog lookup stays off WPF.
            var source = (await SelectedGalleryPathsAsync([id])).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(source)) return;
            if (editor is not null && !editor.Matches(source))
                throw new InvalidOperationException(
                    UiLanguage.T("Dieses Programm ist für das Dateiformat nicht konfiguriert."));
            StatusText.Text = UiLanguage.T("Lokale Arbeitskopie erstellen …");
            var progress = new Progress<(long Done, long Total)>(data =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                StatusText.Text = UiLanguage.Format(
                    "Arbeitskopie: {0:0} %", data.Total == 0 ? 0d :
                        data.Done * 100d / data.Total);
            });
            var stage = await ExternalEditorWorkspace.StageAsync(source, progress);
            if (Dispatcher.HasShutdownStarted) return;

            await Task.Run(() =>
            {
                ProcessStartInfo start;
                if (editor is null)
                {
                    // Use the native Windows program chooser. Still hand it
                    // the protected working copy rather than the original.
                    start = new ProcessStartInfo
                    {
                        FileName = stage.WorkingCopy,
                        Verb = "openas",
                        UseShellExecute = true,
                        WorkingDirectory = stage.Folder
                    };
                }
                else
                {
                    if (!File.Exists(editor.ExecutablePath))
                        throw new FileNotFoundException(
                            UiLanguage.T("Programmdatei nicht gefunden: ") +
                            editor.ExecutablePath);
                    start = new ProcessStartInfo
                    {
                        FileName = editor.ExecutablePath,
                        WorkingDirectory = stage.Folder,
                        UseShellExecute = false
                    };
                    // ArgumentList handles spaces, umlauts and shell
                    // metacharacters without manual quoting or injection.
                    start.ArgumentList.Add(stage.WorkingCopy);
                }
                // Windows shell associations may legitimately return no
                // Process handle even after a successful OpenWith handoff.
                var process = Process.Start(start);
                if (editor is not null && process is null)
                    throw new InvalidOperationException(
                        "Das externe Programm konnte nicht gestartet werden.");
            });

            if (Dispatcher.HasShutdownStarted) return;
            var originalDirectory = Path.GetDirectoryName(Path.GetFullPath(source))!;
            var session = new ExternalEditSessionWindow(
                editor?.Name ?? UiLanguage.T("Windows: Öffnen mit …"),
                source, stage.Folder, stage.WorkingCopy,
                async outputs => await TransferGalleryFilesCoreAsync(
                    outputs, originalDirectory, move: false,
                    neverReplace: true, throwOnFailure: true))
            { Owner = this };
            session.Show();
            StatusText.Text = UiLanguage.T("Externes Programm gestartet.");
        }
        catch (Exception ex)
        {
            ShowFileActionError(ex);
        }
    }
}
