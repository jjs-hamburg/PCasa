using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using PCasa.App.Services;

namespace PCasa.App;

/// <summary>
/// Release checks are strictly opt-in. GitHub Releases are the only source;
/// no build artifacts, repository commits or guessed versions count as an update.
/// This is the discovery step, not the install/update process.
/// </summary>
public partial class MainWindow
{
    private static readonly HttpClient ReleaseClient = CreateReleaseClient();
    private bool _checkingForUpdates;

    private static HttpClient CreateReleaseClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCasa-Windows/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static string CurrentProductVersion =>
        typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

    private void HelpAbout_Click(object sender, RoutedEventArgs e)
    {
        var version = CurrentProductVersion;
        string build = UiLanguage.T("Nicht verfügbar");
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "BUILD-INFO.txt");
            if (File.Exists(file))
            {
                var line = File.ReadLines(file)
                    .FirstOrDefault(value => value.StartsWith(
                        "Commit=", StringComparison.OrdinalIgnoreCase));
                if (line is not null && line.Length > 7)
                    build = line[7..].Trim()[..Math.Min(12, line[7..].Trim().Length)];
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        PcasaDialog.Show(this,
            $"PCasa{Environment.NewLine}" +
            $"{UiLanguage.T("Version")}: {version}{Environment.NewLine}" +
            $"{UiLanguage.T("Build")}: {build}{Environment.NewLine}" +
            $"Windows x64 · .NET 10{Environment.NewLine}{Environment.NewLine}" +
            UiLanguage.T("Fotoverwaltung nach dem Vorbild von Picasa."),
            UiLanguage.T("Über PCasa"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void HelpCheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingForUpdates) return;
        _checkingForUpdates = true;
        CheckForUpdatesMenuItem.IsEnabled = false;
        try
        {
            var installed = PcasaReleaseVersion.TryParse(CurrentProductVersion);
            if (installed is null)
            {
                PcasaDialog.Show(this,
                    UiLanguage.T("Die installierte Programmversion konnte nicht erkannt werden."),
                    UiLanguage.T("Nach Updates suchen"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using var response = await ReleaseClient.GetAsync(
                "https://api.github.com/repos/jjs-hamburg/PCasa/releases?per_page=40");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("GitHub Releases response is not an array.");

            PcasaReleaseVersion? latest = null;
            string? releaseUrl = null;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("draft", out var draft) &&
                    draft.ValueKind == JsonValueKind.True) continue;
                if (!item.TryGetProperty("tag_name", out var tag)) continue;
                var version = PcasaReleaseVersion.TryParse(tag.GetString());
                if (version is null || version.CompareTo(installed.Value) <= 0)
                    continue;
                if (latest is not null && version.CompareTo(latest.Value) <= 0)
                    continue;
                if (!item.TryGetProperty("html_url", out var url) ||
                    !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(uri.Host, "github.com",
                        StringComparison.OrdinalIgnoreCase) ||
                    !uri.AbsolutePath.StartsWith("/jjs-hamburg/PCasa/releases/",
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                latest = version;
                releaseUrl = uri.AbsoluteUri;
            }

            if (latest is null)
            {
                PcasaDialog.Show(this,
                    UiLanguage.T("Es ist keine neuere PCasa-Version als GitHub Release verfügbar.") +
                    Environment.NewLine +
                    $"{UiLanguage.T("Installiert")}: {CurrentProductVersion}",
                    UiLanguage.T("Nach Updates suchen"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = PcasaDialog.Show(this,
                UiLanguage.T("Eine neuere PCasa-Version ist auf GitHub verfügbar.") +
                Environment.NewLine +
                $"{UiLanguage.T("Installiert")}: {CurrentProductVersion}" +
                Environment.NewLine +
                $"{UiLanguage.T("Verfügbar")}: {latest.Value}" +
                Environment.NewLine + Environment.NewLine +
                UiLanguage.T("Soll die GitHub-Release-Seite geöffnet werden?") +
                Environment.NewLine +
                UiLanguage.T("Die Installation erfolgt nicht automatisch."),
                UiLanguage.T("Nach Updates suchen"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes && releaseUrl is not null)
                Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException or IOException or System.ComponentModel.Win32Exception)
        {
            PCasaDiagnostics.Write("Help.CheckForUpdates", ex);
            PcasaDialog.Show(this,
                UiLanguage.T("Die Updateprüfung ist momentan nicht möglich.") +
                Environment.NewLine + ex.Message,
                UiLanguage.T("Nach Updates suchen"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _checkingForUpdates = false;
            CheckForUpdatesMenuItem.IsEnabled = true;
        }
    }
}

/// <summary>
/// User-visible PCasa release syntax: Major.Minor.Alpha|Beta|RC.Build.
/// Stable releases may be named Major.Minor. Numeric Windows resource versions
/// are separate and must never influence the release ordering.
/// </summary>
public readonly record struct PcasaReleaseVersion(
    int Major, int Minor, int Phase, int Build) : IComparable<PcasaReleaseVersion>
{
    public static PcasaReleaseVersion? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var source = value.Trim().TrimStart('v', 'V');
        var parts = source.Split('.');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var majorStable) && majorStable >= 0 &&
            int.TryParse(parts[1], out var minorStable) && minorStable >= 0)
            return new PcasaReleaseVersion(majorStable, minorStable, 3, 0);
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], out var major) || major < 0 ||
            !int.TryParse(parts[1], out var minor) || minor < 0 ||
            !int.TryParse(parts[3], out var build) || build < 1)
            return null;
        var phase = parts[2].ToLowerInvariant() switch
        {
            "alpha" => 0,
            "beta" => 1,
            "rc" => 2,
            _ => -1
        };
        return phase < 0 ? null :
            new PcasaReleaseVersion(major, minor, phase, build);
    }

    public int CompareTo(PcasaReleaseVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0) return minor;
        var phase = Phase.CompareTo(other.Phase);
        return phase != 0 ? phase : Build.CompareTo(other.Build);
    }

    public override string ToString() =>
        Phase == 3 ? $"{Major}.{Minor}" :
        $"{Major}.{Minor}.{(Phase == 0 ? "Alpha" : Phase == 1 ? "Beta" : "RC")}.{Build}";
}
