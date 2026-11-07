using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _databaseMaintenancePending;
    private Task? _databaseBackupTask;
    private CancellationTokenSource? _databaseBackupCancellation;
    private string? _databaseBackupKey;

    private void DatabaseManagementMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_databaseMaintenancePending) return;
        var dialog = new DatabaseManagementWindow(
            new DatabaseMaintenance(), StartDatabaseMaintenance,
            StartDatabaseBackup) { Owner = this };
        dialog.ShowDialog();
    }

    private bool StartDatabaseBackup(string destination)
    {
        if (_shutdownStarted || _databaseMaintenancePending) return false;
        if (_databaseBackupTask is { IsCompleted: false })
        {
            MessageBox.Show(this, UiLanguage.T("Eine Datenbanksicherung läuft bereits."),
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var cancellation = new CancellationTokenSource();
        var key = "database:backup:" + Guid.NewGuid().ToString("N") + ":" +
            destination;
        var owner = new object();
        _databaseBackupCancellation = cancellation;
        _databaseBackupKey = key;
        var presenter = AcquireProgressPresenter(owner);
        presenter.BeginDatabaseBackup(owner);

        var progress = new Progress<DatabaseBackupProgress>(value =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            _scheduler.ReportProgress(key, value.Phase,
                value.ProcessedFiles, value.TotalFiles, value.CurrentFile);
            presenter.UpdateDatabaseBackup(owner, value);
        });
        try
        {
            // The user's backup click is a P0 manual PACKAGE, not a P2
            // maintenance request. Its own scheduler capacity lets it begin
            // even while a NAS directory scan is stuck in native I/O.
            // DatabaseMaintenance writes a temporary archive and publishes
            // the destination only after a complete, validated backup.
            var package = new SchedulerTaskPackage(key);
            package.AddStep(token => Task.Run(() =>
                new DatabaseMaintenance().Backup(destination, progress, token), token));
            var execution = _scheduler.EnqueuePackageAsync(
                package, SchedulerPriority.ManualAction,
                cancellationToken: cancellation.Token,
                workKind: SchedulerWorkKind.DatabaseBackup);
            _databaseBackupTask = execution;
            _taskManagerWindow?.RequestRefresh();
            _ = CompleteDatabaseBackupAsync(
                execution, owner, presenter, destination, cancellation);
            return true;
        }
        catch (Exception ex)
        {
            presenter.EndDatabaseBackup(owner);
            ReleaseProgressPresenter(owner);
            _databaseBackupCancellation = null;
            _databaseBackupKey = null;
            cancellation.Dispose();
            MessageBox.Show(this, ex.Message, "PCasa",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async Task CompleteDatabaseBackupAsync(Task execution, object owner,
        BrowseCacheProgressWindow presenter, string destination,
        CancellationTokenSource cancellation)
    {
        try
        {
            await execution;
            if (!Dispatcher.HasShutdownStarted)
            {
                // End the backup overlay before a modal success message.
                // Otherwise the completed backup stays at 100% for the
                // entire lifetime of that dialog instead of showing the
                // currently executing scan again.
                presenter.EndDatabaseBackup(owner);
                StatusText.Text = UiLanguage.T("Datenbanksicherung abgeschlossen: ") +
                    destination;
                MessageBox.Show(this,
                    UiLanguage.T("Datenbanksicherung erfolgreich abgeschlossen.") +
                    Environment.NewLine + destination, "PCasa",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            if (!Dispatcher.HasShutdownStarted)
                StatusText.Text = UiLanguage.T("Datenbanksicherung abgebrochen.");
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Database.Backup", ex);
            if (!Dispatcher.HasShutdownStarted)
            {
                StatusText.Text = UiLanguage.T("Datenbanksicherung fehlgeschlagen.");
                MessageBox.Show(this, ex.Message, "PCasa",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _databaseBackupKey = null;
            _databaseBackupCancellation = null;
            _databaseBackupTask = null;
            if (!Dispatcher.HasShutdownStarted)
            {
                presenter.EndDatabaseBackup(owner);
                ReleaseProgressPresenter(owner);
                _taskManagerWindow?.RequestRefresh();
            }
            cancellation.Dispose();
        }
    }

    private async Task CompleteMaintenanceHandoffAsync(Process child, string requestPath)
    {
        var ready = requestPath + ".ready";
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (started.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (child.HasExited)
                    throw new InvalidOperationException(
                        "Der separate Wartungsprozess wurde vor der Anzeige des Wartungsdialogs beendet.");
                try
                {
                    if (System.IO.File.Exists(ready) &&
                        System.IO.File.ReadAllText(ready).Trim() ==
                        child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        break;
                }
                catch (System.IO.IOException) { /* Child is still publishing ready marker. */ }
                await Task.Delay(50);
            }
            if (!System.IO.File.Exists(ready) ||
                System.IO.File.ReadAllText(ready).Trim() !=
                    child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new TimeoutException(
                    "Der Wartungsdialog wurde nicht sichtbar. PCasa bleibt geöffnet.");
            // The existing close path owns every scanner, writer, database
            // transaction and ExifTool child. No second shutdown route.
            if (!Dispatcher.HasShutdownStarted) Close();
        }
        catch (Exception ex)
        {
            _databaseMaintenancePending = false;
            PCasaDiagnostics.Write("Database.MaintenanceHandoff", ex);
            try { if (!child.HasExited) child.Kill(); }
            catch (Exception cleanupEx)
            {
                PCasaDiagnostics.Write("Database.MaintenanceHandoffCleanup", cleanupEx);
            }
            if (!Dispatcher.HasShutdownStarted)
                MessageBox.Show(this, ex.Message,
                    UiLanguage.T("Datenbankverwaltung"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            child.Dispose();
            try { System.IO.File.Delete(ready); }
            catch (System.IO.IOException) { }
            // The maintenance child removes the request when it starts work.
            // A failed handoff must not leave a replayable request behind.
            if (!_databaseMaintenancePending)
            {
                try { System.IO.File.Delete(requestPath); }
                catch (System.IO.IOException) { }
            }
        }
    }

    private bool StartDatabaseMaintenance(DatabaseMaintenance.Request request)
    {
        if (_databaseMaintenancePending || _shutdownStarted) return false;
        if (_databaseBackupTask is { IsCompleted: false })
        {
            MessageBox.Show(this,
                UiLanguage.T("Die Datenbanksicherung läuft noch. Bitte vor einer Wiederherstellung oder einem Zurücksetzen abschließen."),
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        // The existing close workflow also protects drafts. Ask before
        // launching a child process that will be waiting for us to exit.
        if (_tagDraft.HasChanges)
        {
            var choice = MessageBox.Show(this,
                UiLanguage.Format(
                    "{0:N0} Bilder mit {1:N0} noch nicht bestätigten Tag-Änderungen verwerfen?",
                    _tagDraft.ImageCount, _tagDraft.ChangeCount),
                UiLanguage.T("Nicht bestätigte Tag-Änderungen"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (choice != MessageBoxResult.Yes) return false;
            _tagDraft.Clear();
        }

        string? requestPath = null;
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("PCasa-Programmdatei nicht gefunden.");
            requestPath = DatabaseMaintenance.SaveRequest(request with {
                ParentProcessId = Environment.ProcessId
            });
            var info = new ProcessStartInfo(executable)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false
            };
            info.ArgumentList.Add("--database-maintenance");
            info.ArgumentList.Add(requestPath);
            info.ArgumentList.Add(Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            var process = Process.Start(info)
                ?? throw new InvalidOperationException(
                    "PCasa-Datenbankwartung konnte nicht gestartet werden.");
            _databaseMaintenancePending = true;
            // Handoff is asynchronous. Never make PCasa disappear before
            // the child WPF window has actually become visible.
            _ = CompleteMaintenanceHandoffAsync(process, requestPath);
            return true;
        }
        catch (Exception ex)
        {
            if (requestPath is not null && System.IO.File.Exists(requestPath))
                System.IO.File.Delete(requestPath);
            MessageBox.Show(this, ex.Message,
                UiLanguage.T("Datenbankverwaltung"), MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }
}
