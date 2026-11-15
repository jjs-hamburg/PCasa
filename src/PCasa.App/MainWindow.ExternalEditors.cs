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
        if (programs.Count > 0) menu.Items.Add(new Separator());
        // Program configuration is exclusively under Options.
        // The unconfigured system fallback deliberately remains last.
        var windows = new MenuItem
        {
            Header = UiLanguage.T("Windows: Öffnen mit …")
        };
        windows.Click += (_, _) => _ = StartExternalEditorAsync(id, null);
        menu.Items.Add(windows);
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

    private async Task StartExternalEditorAsync(long id, ExternalEditorProgram? editor)
    {
        try
        {
            // Only the clicked/visible image, never a stale selection or a
            // whole 500k-image index. Catalog lookup stays off WPF.
            var source = (await SelectedGalleryPathsAsync([id])).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(source)) return;
            // Block launch even if a stale UI entry was clicked.
            if (RawImageSource.IsRawPath(source))
                throw new NotSupportedException(UiLanguage.T(
                    "RAW-Dateien können nicht extern bearbeitet werden."));
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
