using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private string? GetOriginalImagePath(long imageId)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT local_path FROM images WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", imageId);
        return cmd.ExecuteScalar() as string;
    }

    // Every original-file edit shares this ONE error/retry boundary.
    // The existing scheduler still owns priority, package cursor and retry.
    private bool TryWriteOriginal(string kind, string packageKey, long imageId,
        string? path, CancellationToken token, Action write)
    {
        var errors = _originalWriteFailures;
        if (errors.IsParked(kind, imageId,
                kind == OriginalWriteFailureRepository.Gps ? packageKey : null))
            return false;
        try
        {
            write();
            // A tag write returning with pending newer revisions is not
            // necessarily a completed intent; do not clear its error then.
            if (kind == OriginalWriteFailureRepository.Gps)
                errors.ClearIfOwned(kind, imageId, packageKey);
            else if (new ImageTagRepository(_database)
                .PendingEditsForImage(imageId).Count == 0)
                errors.Clear(kind, imageId);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (DurablePackageRetryException) { throw; }
        catch (Exception ex)
        {
            // Every non-cancellation exception from reading, ExifTool,
            // verification, or catalog commit belongs to THIS original.
            // Previously only five exception classes were recorded; other
            // failures deferred the whole durable package forever without
            // ever entering the shared Schreibfehler list. Genuine scheduler
            // yields/cancellation stay outside the per-file attempt counter.
            token.ThrowIfCancellationRequested();
            // A newer GPS package supersedes the failed immutable intent.
            if (kind == OriginalWriteFailureRepository.Gps &&
                errors.HasNewerGpsIntent(packageKey, imageId))
            {
                errors.ClearIfOwned(kind, imageId, packageKey);
                return false;
            }
            // This is the root availability gate, not an individual-file
            // probe loop. A disconnected share does not consume 3 attempts.
            if (!string.IsNullOrWhiteSpace(path) &&
                (ex is IOException or UnauthorizedAccessException) &&
                !_scheduler.SourceAvailability.CheckAfterSourceError(path, token))
                throw new DurablePackageRetryException(
                    "Original-Quelle vorübergehend offline: " +
                    SourceAvailabilityGate.RootOf(path));
            var attempt = errors.RecordFailure(kind, packageKey, imageId, ex,
                permanent: ex is NotSupportedException);
            PCasaDiagnostics.Write("OriginalWrite." + kind + ":" + imageId, ex);
            if (attempt < OriginalWriteFailureRepository.MaximumAttempts)
                throw new DurablePackageRetryException(
                    "Originaldatei " + imageId + ": Versuch " + attempt +
                    "/" + OriginalWriteFailureRepository.MaximumAttempts +
                    " fehlgeschlagen. Erneuter Versuch durch den Scheduler.");
            // This original is parked for explicit manual action. Returning
            // advances only this step, never marks the failed file as written.
            return false;
        }
    }

    private void WriteErrorsMenu_Click(object sender, RoutedEventArgs e)
        => ShowWriteErrorsDialog();

    private void ShowWriteErrorsDialog()
    {
        var errors = _originalWriteFailures;
        var dialog = new Window
        {
            Owner = this,
            Title = UiLanguage.T("Schreibfehler"),
            Width = 1020, Height = 580,
            MinWidth = 650, MinHeight = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "PcasaWindowBrush");
        dialog.SetResourceReference(Window.ForegroundProperty, "PcasaTextBrush");
        var root = new Grid { Margin = new Thickness(15) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var caption = new TextBlock { Margin = new Thickness(0,0,0,12) };
        caption.SetResourceReference(TextBlock.ForegroundProperty,"PcasaTextBrush");
        Grid.SetRow(caption, 0);
        root.Children.Add(caption);

        var table = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow
        };
        table.Columns.Add(new DataGridTextColumn
        { Header=UiLanguage.T("Art"), Binding=new Binding("Kind"), Width=new DataGridLength(80) });
        table.Columns.Add(new DataGridTextColumn
        { Header=UiLanguage.T("Datei"), Binding=new Binding("Path"), Width=new DataGridLength(3, DataGridLengthUnitType.Star) });
        table.Columns.Add(new DataGridTextColumn
        { Header=UiLanguage.T("Versuche"), Binding=new Binding("Attempts"), Width=new DataGridLength(85) });
        table.Columns.Add(new DataGridTextColumn
        { Header=UiLanguage.T("Letzter Fehler"), Binding=new Binding("Error"), Width=new DataGridLength(2, DataGridLengthUnitType.Star) });
        table.Columns.Add(new DataGridTextColumn
        { Header=UiLanguage.T("Zeitpunkt"), Binding=new Binding("LastAttemptUtc"), Width=new DataGridLength(185) });
        table.SetResourceReference(DataGrid.BackgroundProperty, "PcasaPanelBrush");
        table.SetResourceReference(DataGrid.ForegroundProperty, "PcasaTextBrush");
        Grid.SetRow(table, 1);
        root.Children.Add(table);

        var buttons = new StackPanel
        { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
          Margin = new Thickness(0,12,0,0) };
        Button Add(string label)
        {
            var b = new Button { Content = UiLanguage.T(label), MinWidth = 125,
                Margin = new Thickness(7,0,0,0), Padding = new Thickness(9,5,9,5) };
            buttons.Children.Add(b);
            return b;
        }
        var retrySelected = Add("Ausgewählte erneut versuchen");
        var retryAll = Add("Alle erneut versuchen");
        var discardSelected = Add("Ausgewählte verwerfen");
        var discardAll = Add("Alle verwerfen");
        var close = Add("Schließen");
        close.Click += (_,_) => dialog.Close();
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        dialog.Content = root;

        void Refresh()
        {
            var list = errors.Unresolved(includeRetrying: true).ToArray();
            table.ItemsSource = list;
            caption.Text = UiLanguage.Format("{0} Schreibfehler (GPS und Tags)", list.Length) +
                " · " + UiLanguage.T("Bis zum dritten Versuch erfolgt die Wiederholung automatisch.");
            retrySelected.IsEnabled = discardSelected.IsEnabled = list.Any(entry =>
                entry.Attempts >= OriginalWriteFailureRepository.MaximumAttempts);
            retryAll.IsEnabled = discardAll.IsEnabled = retrySelected.IsEnabled;
        }
        OriginalWriteFailureRepository.FailedFile[] Selected(bool all) =>
            (all ? errors.Unresolved(includeRetrying: true)
                : table.SelectedItems.Cast<OriginalWriteFailureRepository.FailedFile>())
            .Where(entry => entry.Attempts >= OriginalWriteFailureRepository.MaximumAttempts)
            .ToArray();

        async Task Perform(bool retry, bool all)
        {
            var selected = Selected(all);
            if (selected.Length == 0) return;
            if (!retry && MessageBox.Show(dialog,
                    UiLanguage.Format("{0} ausstehende Änderungen wirklich verwerfen?", selected.Length),
                    UiLanguage.T("Schreibfehler"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            retrySelected.IsEnabled = retryAll.IsEnabled =
                discardSelected.IsEnabled = discardAll.IsEnabled = false;
            var errorsDuringReview = new List<string>();
            foreach (var entry in selected)
            {
                try
                {
                    if (retry) await RetryOriginalWriteAsync(entry);
                    else await DiscardOriginalWriteAsync(entry);
                }
                catch (Exception ex)
                {
                    PCasaDiagnostics.Write("OriginalWrite.ManualReview", ex);
                    errorsDuringReview.Add(entry.Path + ": " + ex.Message);
                }
            }
            Refresh();
            if (errorsDuringReview.Count > 0)
                MessageBox.Show(dialog,
                    string.Join(Environment.NewLine, errorsDuringReview.Take(12)),
                    UiLanguage.T("Schreibfehler"), MessageBoxButton.OK,
                    MessageBoxImage.Warning);
        }
        retrySelected.Click += async (_,_) => await Perform(true,false);
        retryAll.Click += async (_,_) => await Perform(true,true);
        discardSelected.Click += async (_,_) => await Perform(false,false);
        discardAll.Click += async (_,_) => await Perform(false,true);
        Refresh();
        dialog.ShowDialog();
    }

    private async Task RetryOriginalWriteAsync(OriginalWriteFailureRepository.FailedFile issue)
    {
        var errors = _originalWriteFailures;
        if (!errors.Unresolved().Any(entry => entry.Kind == issue.Kind &&
            entry.ImageId == issue.ImageId && entry.PackageKey == issue.PackageKey))
            return; // The row changed while the review dialog was open.
        if (issue.Kind == OriginalWriteFailureRepository.Tags)
        {
            // The latest pending_tag_edits revisions are the only intent.
            // Old retry recipes are never recreated from an obsolete tag set.
            errors.ClearIfOwned(issue.Kind, issue.ImageId, issue.PackageKey);
            StartPendingTagDrain();
            return;
        }

        if (errors.HasNewerGpsIntent(issue.PackageKey, issue.ImageId))
        {
            errors.ClearIfOwned(issue.Kind, issue.ImageId, issue.PackageKey);
            return; // The newer request already superseded this one.
        }
        var recipe = errors.OriginalRecipe(issue.PackageKey);
        if (recipe is null || recipe.Value.Type != "gps-edit-v1")
            throw new InvalidDataException("Originaler GPS-Auftrag nicht mehr vorhanden.");
        var original = JsonSerializer.Deserialize<GpsEditRecipe>(recipe.Value.Payload)
            ?? throw new InvalidDataException("GPS-Rezept nicht lesbar.");
        var retry = new GpsEditRecipe([issue.ImageId], original.Latitude,
            original.Longitude, original.Mode);
        var payload = JsonSerializer.Serialize(retry);
        var key = "gps:retry:" + Guid.NewGuid().ToString("N");
        var task = _scheduler.EnqueueDurablePackageAsync(
            BuildGpsEditPackage(key, payload), "gps-edit-v1", payload,
            SchedulerPriority.ManualAction);
        errors.ClearIfOwned(issue.Kind, issue.ImageId, issue.PackageKey);
        BeginVisibleManualOperation(key, "GPS in Originaldateien schreiben", 1);
        _ = ObserveGpsEditAsync(task, key);
        await Task.CompletedTask;
    }

    private async Task DiscardOriginalWriteAsync(
        OriginalWriteFailureRepository.FailedFile issue)
    {
        var errors = _originalWriteFailures;
        if (!errors.Unresolved().Any(entry => entry.Kind == issue.Kind &&
            entry.ImageId == issue.ImageId && entry.PackageKey == issue.PackageKey))
            return;
        if (issue.Kind == OriginalWriteFailureRepository.Tags)
        {
            // Tag intents have already changed the DB's gallery projection.
            // Restore the ORIGINAL's keywords before discarding the pending
            // revisions. Never silently leave a false tag in the catalog.
            await _scheduler.RunAsync(
                "tags:discard:" + issue.ImageId + ":" + Guid.NewGuid().ToString("N"),
                SchedulerPriority.ManualAction,
                token => new ImageTagRepository(_database)
                    .DiscardPendingImageEdits(issue.ImageId, token),
                workKind: SchedulerWorkKind.Catalog);
            _ = RefreshPendingTagDotsAsync();
        }
        errors.ClearIfOwned(issue.Kind, issue.ImageId, issue.PackageKey);
    }
}
