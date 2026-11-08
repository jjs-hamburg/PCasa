using System.Windows;
using System.Windows.Controls;
using PCasa.App.Services;

namespace PCasa.App;

/// <summary>Only presents and cancels a user-confirmed installer download.</summary>
internal sealed class UpdateDownloadWindow : Window
{
    private readonly TextBlock _status;
    private readonly ProgressBar _bar;
    private readonly CancellationTokenSource _cancel = new();
    private bool _finished;

    internal CancellationToken Cancellation => _cancel.Token;

    internal UpdateDownloadWindow(Window owner, string version)
    {
        Owner = owner;
        Title = "PCasa – " + UiLanguage.T("Nach Updates suchen");
        Width = 475;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");

        var content = new StackPanel { Margin = new Thickness(18, 15, 18, 15) };
        Content = content;
        content.Children.Add(new TextBlock
        {
            Text = "PCasa " + version + " – " + UiLanguage.T("Download"),
            FontSize = 17, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 13)
        });
        _status = new TextBlock
        {
            Text = UiLanguage.T("Installer wird heruntergeladen …"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        content.Children.Add(_status);
        _bar = new ProgressBar { Height = 12, IsIndeterminate = true, Minimum = 0, Maximum = 100 };
        content.Children.Add(_bar);
        var cancel = new Button
        {
            Content = UiLanguage.T("Abbrechen"),
            MinWidth = 94, Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 13, 0, 0)
        };
        cancel.Click += (_, _) => Close();
        content.Children.Add(cancel);
        Closing += (_, e) =>
        {
            if (!_finished) _cancel.Cancel();
        };
    }

    internal void Update(long transferred, long? total)
    {
        if (_finished) return;
        if (total is > 0)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = Math.Clamp(100.0 * transferred / total.Value, 0, 100);
            _status.Text = $"{transferred / 1048576.0:N1} / {total.Value / 1048576.0:N1} MB";
        }
        else
        {
            _status.Text = $"{transferred / 1048576.0:N1} MB";
        }
    }

    internal void Finish()
    {
        _finished = true;
        Close();
        _cancel.Dispose();
    }
}
