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
            // The SINGLE scheduler is authoritative for queued/running
            // status. P2 backup waits for P0 manual writes, but the
            // management dialog may close immediately after admission.
            var execution = _scheduler.EnqueueAsync(
                key, SchedulerPriority.UserAction,
                token => Task.Run(() =>
                    new DatabaseMaintenance().Backup(destination, progress, token), token),
                cancellationToken: cancellation.Token,
                workKind: SchedulerWorkKind.Catalog);
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
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException(
                    "PCasa-Datenbankwartung konnte nicht gestartet werden.");
            _databaseMaintenancePending = true;
            // Shut down through the SINGLE existing scheduler cleanup. The
            // child will touch SQLite only after THIS process has exited.
            Dispatcher.BeginInvoke(new Action(Close),
                DispatcherPriority.Background);
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
