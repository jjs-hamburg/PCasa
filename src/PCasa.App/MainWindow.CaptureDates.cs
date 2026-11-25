using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private sealed record CaptureDateRecipe(long[] ImageIds, string[] Dates, bool MissingOnly);
    private sealed record CaptureDateCandidate(long ImageId, string Date, bool HasCapture);
    private sealed record CaptureDatePlan(int Selected, CaptureDateCandidate[] Candidates,
        int DateOnly, int NoDate, int Unsupported);
    private bool _captureDatePlanOpen;

    private async Task EditCaptureDatesFromNamesAsync(long[] ids)
    {
        if (_shutdownStarted || _captureDatePlanOpen || ids.Length == 0) return;
        _captureDatePlanOpen = true;
        try
        {
            // Preview reads only local catalog rows in bounded SQL batches.
            var plan = await Task.Run(() => LoadCaptureDatePlan(ids));
            if (_shutdownStarted || Dispatcher.HasShutdownStarted) return;
            var dialog = new Window
            {
                Title = UiLanguage.T("Aufnahmedatum aus Dateinamen übernehmen"),
                Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, Background = TryFindResource("PcasaPanelBrush") as Brush ?? Brushes.DimGray,
                Foreground = TryFindResource("PcasaTextBrush") as Brush ?? Brushes.White
            };
            var root = new StackPanel { Margin = new Thickness(12), MinWidth = 335 };
            dialog.Content = root;
            void Count(string text, int count)
            {
                var row = new DockPanel { Margin = new Thickness(0,2,0,2), LastChildFill = true };
                var number = new TextBlock { Text = count.ToString("N0"),
                    Width = 80, TextAlignment = TextAlignment.Right };
                DockPanel.SetDock(number, Dock.Right);
                row.Children.Add(number);
                row.Children.Add(new TextBlock { Text = UiLanguage.T(text) });
                root.Children.Add(row);
            }
            var missing = plan.Candidates.Count(item => !item.HasCapture);
            Count("Ausgewählte Bilder", plan.Selected);
            Count("Datum und Uhrzeit erkannt", plan.Candidates.Length);
            Count("Davon ohne EXIF-Aufnahmedatum", missing);
            Count("Nur Datum erkannt", plan.DateOnly);
            Count("Kein Datum erkannt", plan.NoDate);
            if (plan.Unsupported > 0) Count("Nicht unterstützte Dateien", plan.Unsupported);
            var missingOnly = new CheckBox { Content = UiLanguage.T("Nur fehlende Aufnahmedaten ergänzen"),
                IsChecked = true, Margin = new Thickness(0,8,0,8) };
            root.Children.Add(missingOnly);
            var actions = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(new Button { Content = UiLanguage.T("Abbrechen"), Width = 100,
                Height = 30, IsCancel = true, Margin = new Thickness(0,0,8,0) });
            var accept = new Button { Content = UiLanguage.T("Übernehmen"), Width = 110, Height = 30,
                IsDefault = true, IsEnabled = missing > 0 };
            accept.Click += (_, _) => dialog.DialogResult = true;
            missingOnly.Checked += (_, _) => accept.IsEnabled = missing > 0;
            missingOnly.Unchecked += (_, _) => accept.IsEnabled = plan.Candidates.Length > 0;
            actions.Children.Add(accept);
            root.Children.Add(actions);
            if (dialog.ShowDialog() != true) return;
            var onlyMissing = missingOnly.IsChecked == true;
            var items = plan.Candidates.Where(item => !onlyMissing || !item.HasCapture).ToArray();
            if (items.Length == 0) return;
            QueueCaptureDateRecipe(new CaptureDateRecipe(items.Select(item => item.ImageId).ToArray(),
                items.Select(item => item.Date).ToArray(), onlyMissing));
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("CaptureDate.Plan", ex);
            if (!_shutdownStarted && !Dispatcher.HasShutdownStarted)
                PcasaDialog.Show(this, ex.Message, UiLanguage.T("Aufnahmedatum aus Dateinamen übernehmen"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _captureDatePlanOpen = false; }
    }

    private CaptureDatePlan LoadCaptureDatePlan(long[] ids)
    {
        var distinct = ids.Distinct().ToArray();
        var candidates = new List<CaptureDateCandidate>();
        int dateOnly = 0, noDate = 0, unsupported = 0, found = 0;
        using var db = _database.OpenConnection();
        foreach (var chunk in distinct.Chunk(400))
        {
            using var cmd = db.CreateCommand();
            var args = chunk.Select((_, i) => "$id" + i).ToArray();
            cmd.CommandText = """
                SELECT i.id,i.file_name,x.exif_json FROM images i
                LEFT JOIN image_exif x ON x.image_id=i.id
                  AND x.source_modified_utc_ticks=i.modified_utc_ticks
                  AND x.source_file_size=i.file_size
                WHERE i.id IN (
                """ + string.Join(",", args) + ");";
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(args[i], chunk[i]);
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                found++;
                var name = rows.GetString(1);
                if (!CanManuallyEditExifDates(name)) { unsupported++; continue; }
                var recognized = PhotoDateFromFilename.Recognize(name);
                if (recognized is null) { noDate++; continue; }
                if (recognized.Time is null) { dateOnly++; continue; }
                var hasCapture = !rows.IsDBNull(2) && FilenameCaptureDateWriter.HasCaptureDate(rows.GetString(2));
                candidates.Add(new CaptureDateCandidate(rows.GetInt64(0), recognized.AsExifDateTime, hasCapture));
            }
        }
        var order = distinct.Select((id,i) => (id,i)).ToDictionary(x => x.id, x => x.i);
        return new CaptureDatePlan(distinct.Length, candidates.OrderBy(item => order[item.ImageId]).ToArray(),
            dateOnly, noDate, unsupported + distinct.Length - found);
    }

    private void QueueCaptureDateRecipe(CaptureDateRecipe recipe)
    {
        var key = "capture-date:" + Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(recipe);
        BeginVisibleManualOperation(key, "Aufnahmedatum aus Dateinamen übernehmen", recipe.ImageIds.Length);
        try
        {
            var task = _scheduler.EnqueueDurableRecipeAsync(key, "capture-date-from-name-v1", payload,
                SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
            _taskManagerWindow?.RequestRefresh();
            _ = ObserveCaptureDatePackageAsync(task, key);
        }
        catch { EndVisibleManualOperation(key); throw; }
    }

    private SchedulerTaskPackage BuildCaptureDatePackage(string key, string payload)
    {
        var recipe = JsonSerializer.Deserialize<CaptureDateRecipe>(payload)
            ?? throw new InvalidDataException("Ungültiger Datumsauftrag.");
        if (recipe.ImageIds.Length != recipe.Dates.Length)
            throw new InvalidDataException("Unvollständiger Datumsauftrag.");
        var writer = new FilenameCaptureDateWriter(_database);
        var package = new SchedulerTaskPackage(key);
        for (var i = 0; i < recipe.ImageIds.Length; i++)
        {
            var index = i;
            package.AddStep(token =>
            {
                token.ThrowIfCancellationRequested();
                var id = recipe.ImageIds[index];
                var path = GetOriginalImagePath(id);
                _scheduler.ReportProgress(key, "Aufnahmedatum aus Dateinamen übernehmen",
                    index, recipe.ImageIds.Length, path);
                if (path is not null)
                    TryWriteOriginal(OriginalWriteFailureRepository.CaptureDate, key, id, path, token,
                        () => writer.Apply(id, recipe.Dates[index], recipe.MissingOnly, token));
                _scheduler.ReportProgress(key, "Aufnahmedatum aus Dateinamen übernehmen",
                    index + 1, recipe.ImageIds.Length, path);
                return Task.CompletedTask;
            });
        }
        return package;
    }

    private async Task ObserveCaptureDatePackageAsync(Task task, string key)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PCasaDiagnostics.Write("CaptureDate.Package", ex); }
        finally
        {
            if (!_shutdownStarted && !Dispatcher.HasShutdownStarted)
            {
                EndVisibleManualOperation(key);
                _taskManagerWindow?.RequestRefresh();
                try { await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PCasaDiagnostics.Write("CaptureDate.Refresh", ex); }
            }
        }
    }
}
