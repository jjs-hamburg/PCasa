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
        menu.Items.Add(new Separator());
        AddContextAction(menu, "Duplizieren", () => DuplicateImage(id));
        AddContextAction(menu, "Löschen", () => _ = DeleteSelectedImagesAsync());
        menu.IsOpen = true;
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
        MessageBox.Show(this, ex.Message, UiLanguage.T("Dateiaktion fehlgeschlagen"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
