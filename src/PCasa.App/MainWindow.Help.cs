using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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
    private bool _updateDownloadActive;

    // Downloading a verified installer over a slow link should not compete
    // with running NAS scans, thumbnail building or source availability probes.
    // Preserve each durable scheduler job's exact cursor for user cancellation.
    private async Task<IReadOnlyList<string>> PauseReadsForUpdateAsync()
    {
        _updateDownloadActive = true;
        _backgroundDebounceTimer?.Stop();
        _backgroundMonitorRefreshTimer?.Stop();
        _cacheMaintenanceTimer?.Stop();
        _backgroundCurrentScanCancellation?.Cancel();
        _initialScanCancellation?.Cancel();
        _browseCacheCancellation?.Cancel();
        _cacheMaintenanceCancellation?.Cancel();

        var pausedByUpdate = new List<string>();
        try
        {
            foreach (var job in _scheduler.Snapshot().Where(job =>
                         job.State is SchedulerJobState.Queued or
                             SchedulerJobState.Running or SchedulerJobState.YieldRequested &&
                         job.WorkKind is SchedulerWorkKind.CatalogScan or
                             SchedulerWorkKind.InitialImport))
            {
                if (await _scheduler.PauseDurableTaskAsync(job.Key))
                    pausedByUpdate.Add(job.Key);
            }
            return pausedByUpdate;
        }
        catch
        {
            ResumeReadsAfterCanceledUpdate(pausedByUpdate);
            throw;
        }
    }

    private void ResumeReadsAfterCanceledUpdate(IReadOnlyList<string> pausedByUpdate)
    {
        foreach (var key in pausedByUpdate)
        {
            try { _scheduler.ResumeDurableTask(key); }
            catch (Exception ex) { PCasaDiagnostics.Write("Update.ResumeReads", ex); }
        }
        _updateDownloadActive = false;
        if (!_shutdownStarted && !Dispatcher.HasShutdownStarted)
        {
            _backgroundMonitorRefreshTimer?.Start();
            _cacheMaintenanceTimer?.Start();
            if (_backgroundPending.Count > 0 &&
                _backgroundScanSettings?.Enabled == true)
                _backgroundDebounceTimer?.Start();
            QueuePendingInitialScans();
        }
    }

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

        new PcasaAboutWindow(this, version, build).ShowDialog();
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
            string? installerUrl = null;
            string? installerDigest = null;
            long installerBytes = 0;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("draft", out var draft) &&
                    draft.ValueKind == JsonValueKind.True) continue;
                if (!item.TryGetProperty("tag_name", out var tag)) continue;
                var version = PcasaReleaseVersion.TryParse(tag.GetString());
                if (version is null || version.Value.CompareTo(installed.Value) <= 0)
                    continue;
                if (latest is not null && version.Value.CompareTo(latest.Value) <= 0)
                    continue;
                // A release is installable only if it contains the exact
                // versioned Windows installer and GitHub's SHA-256 digest.
                // Never use workflow artifacts, a redirect supplied by a
                // release description, or an unverified executable.
                if (!item.TryGetProperty("assets", out var assets) ||
                    assets.ValueKind != JsonValueKind.Array)
                    continue;
                string? candidateUrl = null;
                string? candidateHash = null;
                long candidateBytes = 0;
                foreach (var asset in assets.EnumerateArray())
                {
                    var expectedName = "PCasa-Setup-" + version.Value + ".exe";
                    if (!asset.TryGetProperty("name", out var name) ||
                        name.GetString() != expectedName ||
                        !asset.TryGetProperty("browser_download_url", out var download) ||
                        !Uri.TryCreate(download.GetString(), UriKind.Absolute, out var uri) ||
                        uri.Scheme != Uri.UriSchemeHttps ||
                        !string.Equals(uri.Host, "github.com",
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(uri.AbsolutePath,
                            "/jjs-hamburg/PCasa/releases/download/" +
                            Uri.EscapeDataString(tag.GetString() ?? "") + "/" + expectedName,
                            StringComparison.OrdinalIgnoreCase) ||
                        !asset.TryGetProperty("digest", out var digest) ||
                        digest.GetString() is not { } sha ||
                        !sha.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                        sha.Length != 71 ||
                        !sha[7..].All(Uri.IsHexDigit) ||
                        !asset.TryGetProperty("size", out var size) ||
                        !size.TryGetInt64(out var bytes) ||
                        bytes < 100_000 || bytes > 1_073_741_824)
                        continue;
                    candidateUrl = uri.AbsoluteUri;
                    candidateHash = sha[7..];
                    candidateBytes = bytes;
                    break;
                }
                if (candidateUrl is null) continue;
                latest = version;
                installerUrl = candidateUrl;
                installerDigest = candidateHash;
                installerBytes = candidateBytes;
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
                UiLanguage.T("Soll der geprüfte Installer heruntergeladen und nach dem Beenden von PCasa gestartet werden?") +
                Environment.NewLine +
                UiLanguage.T("Offene PCasa-Aufträge werden vorher geordnet beendet."),
                UiLanguage.T("Nach Updates suchen"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes && installerUrl is not null &&
                installerDigest is not null)
            {
                var paused = await PauseReadsForUpdateAsync();
                var installerStarted = false;
                try
                {
                var downloaded = await DownloadVerifiedInstallerAsync(
                    latest.Value.ToString(), installerUrl, installerDigest, installerBytes);
                if (downloaded is not null)
                {
                    // PowerShell is used only as an OS launcher. It waits for
                    // THIS process to release the SQLite catalog/scheduler
                    // before opening the downloaded, hash-verified installer.
                    var quoted = downloaded.Replace("'", "''", StringComparison.Ordinal);
                    var command = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; " +
                        $"Start-Process -FilePath '{quoted}'";
                    var start = new ProcessStartInfo("powershell.exe")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    start.ArgumentList.Add("-NoProfile");
                    start.ArgumentList.Add("-NonInteractive");
                    start.ArgumentList.Add("-WindowStyle");
                    start.ArgumentList.Add("Hidden");
                    start.ArgumentList.Add("-Command");
                    start.ArgumentList.Add(command);
                    using var helper = Process.Start(start) ??
                        throw new IOException("PCasa update launcher could not be started.");
                    installerStarted = true;
                    Close();
                }
                }
                finally
                {
                    if (!installerStarted)
                        ResumeReadsAfterCanceledUpdate(paused);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or CryptographicException)
        {
            PCasaDiagnostics.Write("Help.CheckForUpdates", ex);
            PcasaDialog.Show(this,
                UiLanguage.T("Die Updateprüfung ist momentan nicht möglich.") +
                Environment.NewLine + ex.Message +
                (ex.InnerException is null ? "" :
                    Environment.NewLine + ex.InnerException.Message),
                UiLanguage.T("Nach Updates suchen"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _checkingForUpdates = false;
            CheckForUpdatesMenuItem.IsEnabled = true;
        }
    }

    private async Task<string?> DownloadVerifiedInstallerAsync(
        string version, string url, string expectedDigest, long expectedBytes)
    {
        var downloadWindow = new UpdateDownloadWindow(this, version);
        var folder = Path.Combine(Path.GetTempPath(), "PCasa-Updates");
        var filename = "PCasa-Setup-" + version + "-" + Guid.NewGuid().ToString("N") + ".exe";
        var destination = Path.Combine(folder, filename);
        var partial = destination + ".part";
        downloadWindow.Show();
        try
        {
            Directory.CreateDirectory(folder);
            while (!downloadWindow.Cancellation.IsCancellationRequested)
            {
                try
                {
                // Keep previously received bytes across a recoverable interruption.
                // A server may decline Range; then restart safely from byte zero.
                long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > expectedBytes)
                {
                    File.Delete(partial);
                    offset = 0;
                }
                if (offset < expectedBytes)
                {
                    using var installerClient = new HttpClient
                    {
                        Timeout = TimeSpan.FromMinutes(30),
                        // Prefer modern CDN transport, but retain HTTP/1.1
                        // fallback for proxies and satellite/VPN connections.
                        DefaultRequestVersion = HttpVersion.Version20,
                        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                    };
                    installerClient.DefaultRequestHeaders.UserAgent.ParseAdd("PCasa-Windows/0.1");
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (offset > 0)
                        request.Headers.Range =
                            new System.Net.Http.Headers.RangeHeaderValue(offset, null);
                    using var response = await installerClient.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead,
                        downloadWindow.Cancellation);
                    response.EnsureSuccessStatusCode();

                    bool resumed = offset > 0 &&
                        response.StatusCode == HttpStatusCode.PartialContent &&
                        response.Content.Headers.ContentRange?.From == offset &&
                        response.Content.Headers.ContentRange?.Length == expectedBytes;
                    if (response.StatusCode == HttpStatusCode.PartialContent && !resumed)
                        throw new InvalidDataException("Invalid installer Range response.");
                    if (!resumed)
                    {
                        // The response is the complete resource, not a partial.
                        // Discard old bytes only after the new response is accepted.
                        offset = 0;
                    }

                    var remaining = expectedBytes - offset;
                    if (response.Content.Headers.ContentLength is { } length &&
                        length != remaining)
                        throw new InvalidDataException(
                            "Installer download size differs from GitHub release metadata.");

                    await using (var input = await response.Content.ReadAsStreamAsync(
                        downloadWindow.Cancellation))
                    await using (var output = new FileStream(partial,
                        resumed ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.None, 131072, useAsync: true))
                    {
                        var buffer = new byte[131072];
                        long transferred = offset;
                        downloadWindow.Update(transferred, expectedBytes);
                        int read;
                        while ((read = await input.ReadAsync(buffer,
                            downloadWindow.Cancellation)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read),
                                downloadWindow.Cancellation);
                            transferred += read;
                            downloadWindow.Update(transferred, expectedBytes);
                            if (transferred > expectedBytes)
                                throw new InvalidDataException(
                                    "Installer exceeds declared size.");
                        }
                        await output.FlushAsync(downloadWindow.Cancellation);
                        if (transferred != expectedBytes)
                            throw new InvalidDataException(
                                "Incomplete PCasa installer download.");
                    }
                }
            downloadWindow.Cancellation.ThrowIfCancellationRequested();
            await using (var verify = File.OpenRead(partial))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(
                    verify, downloadWindow.Cancellation));
                if (!string.Equals(actual, expectedDigest,
                    StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("Installer SHA-256 verification failed.");
            }
            File.Move(partial, destination);
                return destination;
                }
                catch (Exception ex) when (
                    !downloadWindow.Cancellation.IsCancellationRequested &&
                    ex is HttpRequestException or IOException or
                        CryptographicException or InvalidDataException or TaskCanceledException)
                {
                    PCasaDiagnostics.Write("Update.Download.Interrupted", ex);
                    // A failed attempt must not leave an executable or a stale
                    // partial. Reuse the vetted release URL and SHA-256.
                    // A failed SHA-256 digest invalidates the entire prefix;
                    // other transport failures can preserve verified byte count.
                    if (ex is CryptographicException && File.Exists(partial))
                        File.Delete(partial);
                    if (!await downloadWindow.AskRetryAsync(ex))
                        return null;
                }
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            downloadWindow.Finish();
            try { if (File.Exists(partial)) File.Delete(partial); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
