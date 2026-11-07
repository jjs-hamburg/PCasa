using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PCasa.App.Services;

namespace PCasa.App;

// The ONLY window in the isolated maintenance process. The original
// PCasa instance must exit before its SQLite catalog may be touched.
public sealed class DatabaseMaintenanceProgressWindow : Window
{
    private readonly TextBlock _phase;
    private readonly TextBlock _detail;
    private readonly string _finishedPhase;
    private readonly string _failedPhase;
    private readonly ProgressBar _progress;
    private readonly Button _restart;
    private readonly Button _close;
    private bool _working = true;
    private int _parentProcessId;

    public DatabaseMaintenanceProgressWindow(string operation = "")
    {
        var operationTitle = operation switch
        {
            "restore" => "Datenbank wiederherstellen",
            "optimize" => "Datenbank optimieren",
            "reset" => "Datenbank zurücksetzen",
            "backup" => "Datenbank sichern",
            _ => "Datenbankverwaltung"
        };
        _finishedPhase = operation switch
        {
            "restore" => "Wiederherstellung abgeschlossen",
            "optimize" => "Optimierung abgeschlossen",
            "reset" => "Zurücksetzen abgeschlossen",
            _ => "Datenbankwartung abgeschlossen"
        };
        _failedPhase = operation switch
        {
            "restore" => "Wiederherstellung fehlgeschlagen",
            "optimize" => "Optimierung fehlgeschlagen",
            "reset" => "Zurücksetzen fehlgeschlagen",
            _ => "Datenbankwartung fehlgeschlagen"
        };
        Title = "PCasa – " + UiLanguage.T(operationTitle);
        Width = 565;
        Height = 255;
        MinWidth = 450;
        MinHeight = 230;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanMinimize;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");
        var root = new DockPanel { Margin = new Thickness(18) };
        Content = root;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        _restart = new Button
        {
            Content = UiLanguage.T("PCasa neu starten"), MinWidth = 150,
            Height = 32, IsEnabled = false, Margin = new Thickness(0, 0, 8, 0)
        };
        _restart.Click += (_, _) => Restart();
        actions.Children.Add(_restart);
        _close = new Button
        {
            Content = UiLanguage.T("Schließen"), MinWidth = 95,
            Height = 32, IsEnabled = false
        };
        _close.Click += (_, _) => Application.Current.Shutdown();
        actions.Children.Add(_close);
        var content = new StackPanel();
        root.Children.Add(content);
        content.Children.Add(new TextBlock
        {
            Text = UiLanguage.T(operationTitle),
            FontSize = 20, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 16)
        });
        _phase = new TextBlock
        {
            Text = UiLanguage.T("PCasa wird beendet – warte auf Freigabe der Datenbank"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        content.Children.Add(_phase);
        _progress = new ProgressBar
        {
            Minimum = 0, Maximum = 100,
            Height = 14, IsIndeterminate = true,
            Margin = new Thickness(0, 0, 0, 12)
        };
        content.Children.Add(_progress);
        _detail = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 65
        };
        content.Children.Add(_detail);
    }

    public void ShowStartupFailure(Exception error)
    {
        _working = false;
        PCasaDiagnostics.Write("Database.MaintenanceStartup", error);
        _phase.Text = UiLanguage.T("Datenbankwartung konnte nicht gestartet werden");
        _detail.Text = error.Message;
        _detail.ToolTip = error.ToString();
        _progress.IsIndeterminate = false;
        _progress.Value = 0;
        _restart.IsEnabled = false;
        _close.IsEnabled = true;
    }

    public void Start(string path, int parentPid)
    {
        _parentProcessId = parentPid;
        var report = new Progress<DatabaseMaintenanceOperationProgress>(value =>
        {
            if (!_working || Dispatcher.HasShutdownStarted) return;
            _phase.Text = UiLanguage.T(value.Phase);
            _detail.Text = value.Total > 0
                ? UiLanguage.Format("{0:N0} / {1:N0} Dateien", value.Completed, value.Total) +
                  (string.IsNullOrWhiteSpace(value.Detail) ? "" :
                    Environment.NewLine + value.Detail)
                : value.Detail ?? string.Empty;
            if (value.Total > 0)
            {
                _progress.IsIndeterminate = false;
                _progress.Value = 100.0 * Math.Clamp(value.Completed, 0, value.Total) /
                    value.Total;
            }
            else
                _progress.IsIndeterminate = true;
        });
        _ = RunAsync(path, parentPid, report);
    }

    private async Task RunAsync(string path, int parentPid,
        IProgress<DatabaseMaintenanceOperationProgress> report)
    {
        var succeeded = false;
        try
        {
            var result = await Task.Run(() =>
                DatabaseMaintenance.RunRequest(path, parentPid, report));
            succeeded = true;
            _phase.Text = UiLanguage.T(_finishedPhase);
            _detail.Text = result;
            _progress.IsIndeterminate = false;
            _progress.Value = 100;
        }
        catch (Exception ex)
        {
            var error = ex.GetBaseException();
            PCasaDiagnostics.Write("Database.Maintenance", error);
            _phase.Text = UiLanguage.T(_failedPhase);
            _detail.Text = error.Message;
            _detail.ToolTip = error.ToString();
            _progress.IsIndeterminate = false;
            _progress.Value = 0;
        }
        finally
        {
            _working = false;
            _restart.IsEnabled = true;
            _close.IsEnabled = true;
            Activate();
            if (succeeded && !Dispatcher.HasShutdownStarted)
            {
                // Restart only after the offline operation succeeded and
                // its commit/rollback boundary completed. Failure leaves
                // this window open so the actual error remains visible.
                _phase.Text = UiLanguage.T(_finishedPhase) + " – " +
                    UiLanguage.T("PCasa wird gestartet");
                Dispatcher.BeginInvoke(new Action(Restart),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }

    private void Restart()
    {
        _restart.IsEnabled = false;
        try
        {
            // A failed parent handoff can leave the old PCasa instance
            // shutting down. Never start a second catalog owner over it.
            if (_parentProcessId > 0)
            {
                try
                {
                    using var oldInstance = Process.GetProcessById(_parentProcessId);
                    if (!oldInstance.HasExited)
                        throw new InvalidOperationException(
                            "PCasa ist noch nicht vollständig beendet. Ein Neustart ist erst danach sicher.");
                }
                catch (ArgumentException) { /* Old process has exited. */ }
            }
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("PCasa-Programmdatei nicht gefunden.");
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("PCasa konnte nicht gestartet werden.");
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Database.MaintenanceRestart", ex);
            _phase.Text = UiLanguage.T("PCasa konnte nicht neu gestartet werden");
            _detail.Text = ex.Message;
            _detail.ToolTip = ex.ToString();
            _restart.IsEnabled = true;
            PcasaDialog.Show(this, ex.Message, "PCasa", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_working) e.Cancel = true; // Never abandon an in-flight catalog replacement.
        base.OnClosing(e);
    }
}
