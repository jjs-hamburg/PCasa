using System.Diagnostics;
using System.IO;
using System.Text;

namespace PCasa.App.Services;

/// <summary>
/// Writes only XMP keywords through bundled ExifTool; original image pixels
/// are never decoded or recompressed. One session serves a complete user
/// selection instead of starting a native process for every photograph.
/// </summary>
public static class ExifToolKeywordWriter
{
    private static readonly object ActiveGate = new();
    private static readonly HashSet<Session> ActiveSessions = new();
    private static int _activeWrites;

    public static bool HasActiveWrites => Volatile.Read(ref _activeWrites) != 0;

    public static bool HasLiveSessions
    {
        get { lock (ActiveGate) return ActiveSessions.Count != 0; }
    }

    // Only ExifTool processes owned by this application are affected.
    public static void StopAll()
    {
        Session[] sessions;
        lock (ActiveGate) sessions = ActiveSessions.ToArray();
        foreach (var session in sessions)
            session.Dispose();
    }

    public static void Write(string imagePath, IReadOnlyList<string> keywords)
    {
        using var session = new Session();
        session.Write(imagePath, keywords);
    }

    public sealed class Session : IDisposable
    {
        private readonly object _operationGate = new();
        private Process? _process;
        private Task<string>? _errorDrain;
        private int _nextRequestId;
        private bool _disposed;

        public sealed record KeywordSnapshot(IReadOnlyList<string> Xmp, IReadOnlyList<string> Iptc)
        {
            public IReadOnlyList<string> Combined => Xmp.Concat(Iptc)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        // Read both keyword fields without decoding image pixels.
        // JSON preserves list items (including commas) without guesswork.
        public IReadOnlyList<string> Read(string imagePath,
            CancellationToken cancellationToken = default)
            => ReadSnapshot(imagePath, cancellationToken).Combined;

        public KeywordSnapshot ReadSnapshot(string imagePath,
            CancellationToken cancellationToken = default)
        {
            lock (_operationGate)
                return ReadSnapshotCore(imagePath, cancellationToken);
        }

        private KeywordSnapshot ReadSnapshotCore(string imagePath,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (imagePath.Contains('\r') || imagePath.Contains('\n'))
                throw new ArgumentException("Dateipfade dürfen keine Zeilenumbrüche enthalten.");
            StartIfNeeded();
            var process = _process!;
            using var cancellation = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            var request = ++_nextRequestId;
            process.StandardInput.WriteLine("-j");
            process.StandardInput.WriteLine("-XMP-dc:Subject");
            process.StandardInput.WriteLine("-IPTC:Keywords");
            process.StandardInput.WriteLine(imagePath);
            process.StandardInput.WriteLine("-execute" + request);
            process.StandardInput.Flush();
            var response = ReadResponseOrReset(process, request, cancellationToken);
            using var document = System.Text.Json.JsonDocument.Parse(response);
            var entry = document.RootElement.EnumerateArray().FirstOrDefault();
            if (entry.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new IOException("ExifTool lieferte keine lesbaren Metadaten.");
            if (entry.TryGetProperty("Error", out var error))
                throw new IOException("ExifTool: " + error.ToString());
            static IReadOnlyList<string> Extract(
                System.Text.Json.JsonElement entry, string field)
            {
                if (!entry.TryGetProperty(field, out var value))
                    return Array.Empty<string>();
                var values = value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Array => value.EnumerateArray()
                        .Where(v => v.ValueKind == System.Text.Json.JsonValueKind.String)
                        .Select(v => v.GetString() ?? "").ToArray(),
                    System.Text.Json.JsonValueKind.String =>
                        new[] { value.GetString() ?? "" },
                    _ => Array.Empty<string>()
                };
                return values.Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            return new KeywordSnapshot(Extract(entry, "Subject"), Extract(entry, "Keywords"));
        }

        // A timeout leaves ReadLineAsync outstanding. Never reuse that stream.
        private string ReadResponseOrReset(Process process, int request,
            CancellationToken token)
        {
            try { return ReadResponse(process, request, token); }
            catch
            {
                ResetFailedProcess(process);
                throw;
            }
        }

        private void ResetFailedProcess(Process failed)
        {
            if (ReferenceEquals(_process, failed)) _process = null;
            try
            {
                if (!failed.HasExited)
                {
                    failed.Kill(entireProcessTree: true);
                    failed.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                lock (ActiveGate) ActiveSessions.Remove(this);
                ExifToolProcessRegistry.Retire(failed);
                _errorDrain = null;
            }
        }

        private static string ReadResponse(Process process, int request,
            CancellationToken cancellationToken)
        {
            var response = new StringBuilder();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = process.StandardOutput.ReadLineAsync(cancellationToken)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(90), cancellationToken)
                    .GetAwaiter().GetResult();
                if (line is null)
                    throw new IOException("ExifTool wurde vor der Bestätigung beendet.");
                if (line.Trim() == "{ready" + request + "}") break;
                response.AppendLine(line);
                if (response.Length > 64_000)
                    throw new InvalidDataException("Unerwartet große ExifTool-Antwort.");
            }
            return response.ToString();
        }

        public void Write(string imagePath, IReadOnlyList<string> keywords,
            CancellationToken cancellationToken = default)
        {
            lock (_operationGate)
            {
                Interlocked.Increment(ref _activeWrites);
                try { WriteCore(imagePath, keywords, cancellationToken); }
                finally { Interlocked.Decrement(ref _activeWrites); }
            }
        }

        private void WriteCore(string imagePath, IReadOnlyList<string> keywords,
            CancellationToken cancellationToken)
        {
            foreach (var keyword in keywords)
                if (keyword.Contains('\r') || keyword.Contains('\n'))
                    throw new ArgumentException("Ein Tag darf keine Zeilenumbrüche enthalten.");
            var isSonyArw = Path.GetExtension(imagePath).Equals(".arw",
                StringComparison.OrdinalIgnoreCase);
            var commands = new List<string>();
            if (keywords.Count == 0)
            {
                commands.Add("-XMP-dc:Subject=");
                if (!isSonyArw) commands.Add("-IPTC:Keywords=");
            }
            else
            {
                foreach (var keyword in keywords)
                {
                    if (!isSonyArw && Encoding.UTF8.GetByteCount(keyword) > 64)
                        throw new InvalidDataException(
                            "IPTC-Schlagwort überschreitet 64 UTF-8-Bytes: " + keyword);
                    commands.Add("-XMP-dc:Subject=" + keyword);
                    if (!isSonyArw) commands.Add("-IPTC:Keywords=" + keyword);
                }
                if (!isSonyArw) commands.Add("-IPTC:CodedCharacterSet=UTF8");
            }
            ExecuteMetadataEdit(imagePath, commands, cancellationToken);
        }

        // GPS shares the EXACT same ExifTool session, source lock (in the
        // repository), atomic native commit and native-response validation
        // as keyword writes. Never add -o or create an XMP sidecar.
        public void WriteGps(string imagePath, double? latitude, double? longitude,
            CancellationToken cancellationToken = default)
        {
            if (latitude.HasValue != longitude.HasValue ||
                latitude is < -90 or > 90 || longitude is < -180 or > 180 ||
                (latitude is double lat && !double.IsFinite(lat)) ||
                (longitude is double lon && !double.IsFinite(lon)))
                throw new ArgumentOutOfRangeException(nameof(latitude),
                    "Ungültige GPS-Koordinaten.");
            var extension = Path.GetExtension(imagePath).ToLowerInvariant();
            if (extension is not (".jpg" or ".jpeg" or ".tif" or ".tiff"
                or ".dng" or ".nef" or ".arw" or ".cr2"))
                throw new NotSupportedException(
                    "GPS-Direktschreiben für " + extension +
                    " ist nicht verifiziert. Keine Sidecars werden erzeugt.");
            var commands = new List<string>();
            if (latitude is double latValue && longitude is double lonValue)
            {
                commands.Add("-EXIF:GPSLatitude=" +
                    Math.Abs(latValue).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                commands.Add("-EXIF:GPSLatitudeRef=" + (latValue < 0 ? "S" : "N"));
                commands.Add("-EXIF:GPSLongitude=" +
                    Math.Abs(lonValue).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                commands.Add("-EXIF:GPSLongitudeRef=" + (lonValue < 0 ? "W" : "E"));
                // Remove stale XMP coordinates so one photo has one position.
                commands.Add("-XMP-exif:GPSLatitude=");
                commands.Add("-XMP-exif:GPSLongitude=");
            }
            else
            {
                foreach (var tag in new[]
                {
                    "GPSLatitude", "GPSLatitudeRef", "GPSLongitude", "GPSLongitudeRef",
                    "GPSAltitude", "GPSAltitudeRef", "GPSMapDatum", "GPSPosition",
                    "GPSDateStamp", "GPSTimeStamp"
                })
                    commands.Add("-EXIF:" + tag + "=");
                foreach (var tag in new[] { "GPSLatitude", "GPSLongitude",
                    "GPSAltitude", "GPSMapDatum" })
                    commands.Add("-XMP-exif:" + tag + "=");
            }
            lock (_operationGate)
            {
                Interlocked.Increment(ref _activeWrites);
                try { ExecuteMetadataEdit(imagePath, commands, cancellationToken); }
                finally { Interlocked.Decrement(ref _activeWrites); }
            }
        }

        // Shared native write lifecycle: a scheduler yield can interrupt
        // BEFORE the request, never during ExifTool's temporary-file swap.
        private void ExecuteMetadataEdit(string imagePath,
            IReadOnlyList<string> commands, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (imagePath.Contains('\r') || imagePath.Contains('\n'))
                throw new ArgumentException("Dateipfade dürfen keine Zeilenumbrüche enthalten.");
            StartIfNeeded();
            var process = _process!;
            cancellationToken.ThrowIfCancellationRequested();
            TryRemoveAbandonedTemporaryOutput(imagePath, TimeSpan.FromMinutes(30));
            var temporaryExistedBeforeWrite =
                File.Exists(imagePath + "_exiftool_tmp");
            var request = ++_nextRequestId;
            process.StandardInput.WriteLine("-P");
            process.StandardInput.WriteLine("-overwrite_original");
            foreach (var command in commands)
                process.StandardInput.WriteLine(command);
            process.StandardInput.WriteLine(imagePath);
            process.StandardInput.WriteLine("-execute" + request);
            process.StandardInput.Flush();
            string response;
            try
            {
                response = ReadResponseOrReset(process, request, CancellationToken.None);
            }
            catch
            {
                if (!temporaryExistedBeforeWrite)
                    TryRemoveAbandonedTemporaryOutput(imagePath, TimeSpan.Zero);
                throw;
            }
            if (!response.Contains("1 image files updated",
                StringComparison.Ordinal))
            {
                if (!temporaryExistedBeforeWrite)
                    TryRemoveAbandonedTemporaryOutput(imagePath, TimeSpan.Zero);
                throw new IOException("ExifTool konnte die Datei nicht aktualisieren: " +
                    response.Trim());
            }
        }

        // ExifTool's precise, deterministic temp name for this original.
        // Never scan directories or touch unrelated files. A stale orphan is
        // safe to discard ONLY if the untouched original still exists, the
        // temp is old enough, and an exclusive open proves no writer owns it.
        // Current in-flight output is left alone; the caller can retry the
        // unchanged pending_tag_edits after a failed native transaction.
        private static void TryRemoveAbandonedTemporaryOutput(
            string imagePath, TimeSpan minimumAge)
        {
            try
            {
                if (!File.Exists(imagePath)) return;
                var temporary = imagePath + "_exiftool_tmp";
                var info = new FileInfo(temporary);
                if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc < minimumAge)
                    return;
                var size = info.Length;
                var stamp = info.LastWriteTimeUtc;
                using (var exclusivelyHeld = new FileStream(temporary,
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (exclusivelyHeld.Length != size ||
                        File.GetLastWriteTimeUtc(temporary) != stamp ||
                        !File.Exists(imagePath))
                        return;
                }
                // The source image was never removed by this cleanup.
                File.Delete(temporary);
            }
            catch (IOException) { } // NAS offline, file in use or transient.
            catch (UnauthorizedAccessException) { }
        }

        private void StartIfNeeded()
        {
            if (_process is not null) return;
            var executable = Environment.GetEnvironmentVariable("PCASA_EXIFTOOL_PATH");
            if (string.IsNullOrWhiteSpace(executable))
                executable = Path.Combine(AppContext.BaseDirectory, "exiftool", "exiftool.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException(
                    "Die mit PCasa ausgelieferte Metadatenkomponente ExifTool fehlt.", executable);

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in new[] { "-charset", "filename=UTF8",
                "-stay_open", "True", "-@", "-" })
                start.ArgumentList.Add(arg);
            _process = ExifToolProcessRegistry.Start(start);
            lock (ActiveGate) ActiveSessions.Add(this);
            // Always drain stderr concurrently so a warning cannot deadlock
            // a subsequent request. The final metadata read verifies success.
            _errorDrain = _process.StandardError.ReadToEndAsync();
        }

        public void Dispose()
        {
            lock (_operationGate)
            {
                if (_disposed) return;
                _disposed = true;
                var process = _process;
                _process = null;
                try
                {
                    // All reads/writes own this gate. No transaction can be
                    // interrupted by normal Session disposal.
                    if (process is not null)
                        ExifToolProcessRegistry.Retire(process);
                }
                finally
                {
                    lock (ActiveGate) ActiveSessions.Remove(this);
                    _errorDrain = null;
                }
            }
        }
    }
}
