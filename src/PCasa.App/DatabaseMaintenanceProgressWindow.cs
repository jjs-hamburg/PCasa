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
    private readonly ProgressBar _progress;
    private readonly Button _restart;
    private readonly Button _close;
    private bool _working = true;

    public DatabaseMaintenanceProgressWindow()
    {
        Title = "PCasa – " + UiLanguage.T("Datenbankverwaltung");
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
            Text = UiLanguage.T("PCasa – Datenbankwartung"),
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

    public void Start(string path, int parentPid)
    {
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
        try
        {
            var result = await Task.Run(() =>
                DatabaseMaintenance.RunRequest(path, parentPid, report));
            _phase.Text = UiLanguage.T("Datenbankwartung abgeschlossen");
            _detail.Text = result;
            _progress.IsIndeterminate = false;
            _progress.Value = 100;
        }
        catch (Exception ex)
        {
            var error = ex.GetBaseException();
            PCasaDiagnostics.Write("Database.Maintenance", error);
            _phase.Text = UiLanguage.T("Datenbankwartung fehlgeschlagen");
            _detail.Text = error.ToString();
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
        }
    }

    private void Restart()
    {
        try
        {
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
            MessageBox.Show(this, ex.Message, "PCasa", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_working) e.Cancel = true; // Never abandon an in-flight catalog replacement.
        base.OnClosing(e);
    }
}
