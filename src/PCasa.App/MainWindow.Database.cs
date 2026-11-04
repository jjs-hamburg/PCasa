using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _databaseMaintenancePending;

    private void DatabaseManagementMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_databaseMaintenancePending) return;
        var dialog = new DatabaseManagementWindow(
            new DatabaseMaintenance(), StartDatabaseMaintenance) { Owner = this };
        dialog.ShowDialog();
    }

    private bool StartDatabaseMaintenance(DatabaseMaintenance.Request request)
    {
        if (_databaseMaintenancePending || _shutdownStarted) return false;
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
