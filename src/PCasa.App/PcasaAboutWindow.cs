using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

namespace PCasa.App;

/// <summary>A compact product About dialog, distinct from warning/confirmation dialogs.</summary>
internal sealed class PcasaAboutWindow : Window
{
    public PcasaAboutWindow(Window owner, string version, string build)
    {
        Owner = owner;
        Title = UiLanguage.T("Über PCasa");
        Width = 460;
        Height = 294;
        MinWidth = 460;
        MinHeight = 294;
        MaxWidth = 460;
        MaxHeight = 294;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(Control.BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(Control.ForegroundProperty, "PcasaTextBrush");

        ImageSource? logo = null;
        try
        {
            var frames = BitmapDecoder.Create(
                new Uri("pack://application:,,,/PCasa.App;component/Assets/PCasa.ico"),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames;
            // A default ICO decoder selects the tiny 16 px frame, which becomes
            // blurry when enlarged in About. Select the largest image explicitly.
            logo = frames.OrderByDescending(frame => frame.PixelWidth).FirstOrDefault();
            Icon = logo;
        }
        catch (Exception ex) when (ex is System.IO.IOException or
            NotSupportedException or UriFormatException)
        {
            // Local source builds without the generated icon still show About.
            Services.PCasaDiagnostics.Write("About.Icon", ex);
        }

        var layout = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = layout;

        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(main, 0);
        layout.Children.Add(main);
        if (logo is not null)
        {
            var image = new Image
            {
                Source = logo,
                Width = 64,
                Height = 64,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 9, 0)
            };
            main.Children.Add(image);
        }

        var details = new StackPanel();
        Grid.SetColumn(details, 1);
        main.Children.Add(details);
        details.Children.Add(new TextBlock
        {
            Text = "PCasa", FontSize = 22, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        });
        foreach (var line in new[]
        {
            $"{UiLanguage.T("Version")}: {version}",
            $"{UiLanguage.T("Build")}: {build}",
            "Windows x64 · .NET 10",
        })
            details.Children.Add(new TextBlock { Text = line, FontSize = 13,
                Margin = new Thickness(0, 0, 0, 3) });

        var legal = new StackPanel { Margin = new Thickness(84, 12, 0, 0) };
        Grid.SetRow(legal, 1);
        layout.Children.Add(legal);
        legal.Children.Add(new TextBlock { Text = "© 2026 Jens-Jürgen Schäfer", FontSize = 13,
            Margin = new Thickness(0, 0, 0, 4) });
        legal.Children.Add(new TextBlock
        {
            Text = "GNU General Public License v3.0",
            FontSize = 13
        });
        // The published installer places all third-party notices next to
        // PCasa.App.exe. Do not send users to GitHub as a substitute for
        // license information delivered with their installed application.
        var licenseButton = new Button
        {
            Content = UiLanguage.T("Lizenzen und Dritthinweise öffnen"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(10, 5, 10, 5),
            MinHeight = 30
        };
        if (Application.Current.TryFindResource("PcasaStatusButtonStyle") is Style licenseStyle)
            licenseButton.Style = licenseStyle;
        licenseButton.Click += (_, _) =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "Licenses");
            // Opening the folder works even when Windows has no Markdown
            // file association or text editor configured.
            if (!Directory.Exists(folder))
            {
                PcasaDialog.Show(this,
                    UiLanguage.T("Lizenzinformationen fehlen in dieser Installation."),
                    UiLanguage.T("Über PCasa"), MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("About.OpenLicenses", ex);
                PcasaDialog.Show(this, ex.Message,
                    UiLanguage.T("Über PCasa"), MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        };
        legal.Children.Add(licenseButton);

        var ok = new Button
        {
            Content = "OK", MinWidth = 94, Height = 32,
            HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true,
            IsCancel = true
        };
        if (Application.Current.TryFindResource("PcasaStatusButtonStyle") is Style style)
            ok.Style = style;
        ok.Click += (_, _) => Close();
        Grid.SetRow(ok, 2);
        layout.Children.Add(ok);
    }
}
