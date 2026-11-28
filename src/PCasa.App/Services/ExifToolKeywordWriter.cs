using System.Diagnostics;
using System.IO;
using System.Text;

namespace PCasa.App.Services;

/// <summary>
/// Writes only XMP keywords through bundled ExifTool; original image pixels
/// are never decoded or recompressed. One session serves a complete user
/// selection instead of starting a native process for every photograph.
/// </summary>
public sealed class ExifToolStructuralException(string message) : IOException(message) { }

public static class ExifToolKeywordWriter
{
    private static readonly object ActiveGate = new();
    private static readonly HashSet<Session> ActiveSessions = new();
    private static int _activeWrites;
    // One shared, in-memory estimate per source root. It is not a new
    // scheduler, background service, retry policy or persistent queue.
    private static readonly AdaptiveMetadataWriteTimeout WriteTimeouts = new();

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

    // Drain continuously, not at session disposal: stay-open processes
    // otherwise hide the actual native failure behind the stdout summary.
    internal static async Task DrainDiagnosticsAsync(Process process, Func<string, bool>? capture = null)
    {
        var pid = process.Id;
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is string line)
                if (!string.IsNullOrWhiteSpace(line) && capture?.Invoke(line) != true)
                    PCasaDiagnostics.WriteMessage("ExifTool.Stderr", $"PID={pid}; {line}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { PCasaDiagnostics.Write("ExifTool.StderrDrain", ex); }
    }

    internal static bool IsStructuralFailure(string stderr) => stderr.Split('\n').Any(line =>
        line.TrimStart().StartsWith("Error:", StringComparison.OrdinalIgnoreCase) &&
        (line.Contains("Truncated ExifIFD directory", StringComparison.OrdinalIgnoreCase) ||
         line.Contains("Bad format (", StringComparison.OrdinalIgnoreCase) &&
         line.Contains("IFD", StringComparison.OrdinalIgnoreCase)));

    public sealed class Session : IDisposable
    {
        private readonly object _operationGate = new();
        private Process? _process;
        private Task? _errorDrain;
        private volatile NativeDiagnosticCapture? _nativeCapture;

        private sealed class NativeDiagnosticCapture
        {
            internal readonly string Start = "PCASA-BEGIN-" + Guid.NewGuid().ToString("N");
            internal readonly string End = "PCASA-END-" + Guid.NewGuid().ToString("N");
            internal readonly TaskCompletionSource<string> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly StringBuilder _lines = new();
            private bool _started;
            internal bool Accept(string line)
            {
                if (line == Start) { _started = true; return true; }
                if (line == End) { Finished.TrySetResult(_lines.ToString()); return true; }
                if (_started && _lines.Length < 32_000) _lines.AppendLine(line);
                return false; // also keep original diagnostics in the log
            }
        }
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
            // ExifTool's default JSON renders numeric-looking TEXT tags as
            // numbers. Preserve their exact spelling ("2025", "0012") so
            // ReadSnapshot never silently drops user keywords.
            process.StandardInput.WriteLine("-api");
            process.StandardInput.WriteLine("StructFormat=JSONQ");
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
            CancellationToken token, TimeSpan? timeout = null)
        {
            try { return ReadResponse(process, request, token,
                timeout ?? TimeSpan.FromSeconds(90)); }
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
            CancellationToken cancellationToken, TimeSpan timeout)
        {
            var response = new StringBuilder();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = process.StandardOutput.ReadLineAsync(cancellationToken)
                    .AsTask().WaitAsync(timeout, cancellationToken)
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
            CancellationToken cancellationToken = default,
            long? knownSourceLengthBytes = null)
        {
            lock (_operationGate)
            {
                Interlocked.Increment(ref _activeWrites);
                try { WriteCore(imagePath, keywords, cancellationToken,
                    knownSourceLengthBytes); }
                finally { Interlocked.Decrement(ref _activeWrites); }
            }
        }

        private void WriteCore(string imagePath, IReadOnlyList<string> keywords,
            CancellationToken cancellationToken, long? knownSourceLengthBytes)
        {
            // Videos are cataloged and played, never modified.
            if (VideoFormats.IsVideo(imagePath))
                throw new NotSupportedException("Videodateien werden in PCasa nicht bearbeitet.");
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
            ExecuteMetadataEdit(imagePath, commands, cancellationToken,
                knownSourceLengthBytes);
        }

        // GPS shares the EXACT same ExifTool session, source lock (in the
        // repository), atomic native commit and native-response validation
        // as keyword writes. Never add -o or create an XMP sidecar.
        public void WriteGps(string imagePath, double? latitude, double? longitude,
            CancellationToken cancellationToken = default,
            long? knownSourceLengthBytes = null)
        {
            if (latitude.HasValue != longitude.HasValue ||
                latitude is < -90 or > 90 || longitude is < -180 or > 180 ||
                (latitude is double lat && !double.IsFinite(lat)) ||
                (longitude is double lon && !double.IsFinite(lon)))
                throw new ArgumentOutOfRangeException(nameof(latitude),
                    "Ungültige GPS-Koordinaten.");
            var extension = Path.GetExtension(imagePath).ToLowerInvariant();
            if (extension is not (".jpg" or ".jpeg" or ".tif" or ".tiff"
                or ".dng" or ".nef" or ".arw" or ".cr2" or ".psd"))
                throw new NotSupportedException(
                    "GPS-Direktschreiben für " + extension +
                    " ist nicht verifiziert. Keine Sidecars werden erzeugt.");
            var commands = new List<string>();
            var psd = extension == ".psd";
            if (latitude is double latValue && longitude is double lonValue)
            {
                commands.Add((psd ? "-XMP-exif:GPSLatitude=" : "-EXIF:GPSLatitude=") +
                    Math.Abs(latValue).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                if (!psd) commands.Add("-EXIF:GPSLatitudeRef=" + (latValue < 0 ? "S" : "N"));
                commands.Add((psd ? "-XMP-exif:GPSLongitude=" : "-EXIF:GPSLongitude=") +
                    Math.Abs(lonValue).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                if (!psd) commands.Add("-EXIF:GPSLongitudeRef=" + (lonValue < 0 ? "W" : "E"));
                // Non-PSD originals use native EXIF as the authoritative GPS.
                // PSDs use XMP-exif; do not clear the values just written.
                if (!psd)
                {
                    commands.Add("-XMP-exif:GPSLatitude=");
                    commands.Add("-XMP-exif:GPSLongitude=");
                }
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
                try
                {
                    ExecuteMetadataEdit(imagePath, commands, cancellationToken,
                        knownSourceLengthBytes);
                }
                finally { Interlocked.Decrement(ref _activeWrites); }
            }
        }

        // Only the known capture/creation/modification date tags can enter
        // this stay-open command stream. Preserve file last-write time for
        // external results; user-initiated editing advances it independently.
        internal void WriteOrientation(string imagePath, int orientation, CancellationToken token)
        {
            if (orientation is < 1 or > 8 ||
                Path.GetExtension(imagePath).ToLowerInvariant() is not (".jpg" or ".jpeg"))
                throw new ArgumentException("Invalid JPEG orientation.");
            lock (_operationGate)
            {
                Interlocked.Increment(ref _activeWrites);
                try
                {
                    ExecuteMetadataEdit(imagePath,
                        ["-IFD0:Orientation#=" + orientation], token);
                }
                finally { Interlocked.Decrement(ref _activeWrites); }
            }
        }

        public void WriteExactExifDates(string imagePath,
            IReadOnlyDictionary<string, string?> values,
            CancellationToken token = default)
        {
            if (CatalogMediaFormats.IsRawPath(imagePath))
                throw new NotSupportedException(
                    "RAW-Metadaten dürfen von dieser Funktion nicht verändert werden.");
            if (values.Count == 0) return;
            var commands = new List<string>();
            foreach (var (tag, value) in values)
            {
                if (!ProtectedExifDateTags.IsAllowedTag(tag))
                    throw new ArgumentException("Nicht unterstütztes EXIF-Datumsfeld: " + tag);
                if (value is { Length: > 128 } || value?.Contains('\r') == true ||
                    value?.Contains('\n') == true)
                    throw new InvalidDataException(
                        "Exif-Datumswert enthält unzulässige Steuerzeichen.");
                commands.Add("-" + tag + "=" + (value ?? ""));
            }
            lock (_operationGate)
            {
                Interlocked.Increment(ref _activeWrites);
                try { ExecuteMetadataEdit(imagePath, commands, token); }
                finally { Interlocked.Decrement(ref _activeWrites); }
            }
        }

        // Only this narrowly scoped video mutation is supported. Preserve
        // the literal filename clock time without inferring a UTC offset.
        public void WriteMp4CaptureDate(string path, string date, CancellationToken token = default)
        {
            if (!Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("MP4 capture date requires an MP4 file.");
            if (!DateTime.TryParseExact(date, "yyyy:MM:dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
                throw new ArgumentException("Invalid capture date.", nameof(date));
            var normalized = parsed.ToString("yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            lock (_operationGate)
            {
                Interlocked.Increment(ref _activeWrites);
                try { ExecuteMetadataEdit(path, ["-XMP-exif:DateTimeOriginal=" + normalized], token); }
                finally { Interlocked.Decrement(ref _activeWrites); }
            }
        }

        // Shared native write lifecycle: a scheduler yield can interrupt
        // BEFORE the request, never during ExifTool's temporary-file swap.
        // All native metadata edits use this single transport policy. Non-JPEG
        // formats retain their established native path; benchmarks covered JPEG.
        internal bool ForceStagedJpegWrites { get; set; }
        internal ImageExifData? LastVerifiedExif { get; private set; }

        private void ExecuteMetadataEdit(string imagePath,
            IReadOnlyList<string> commands, CancellationToken cancellationToken,
            long? knownSourceLengthBytes = null)
        {
            LastVerifiedExif = null;
            if (Path.GetExtension(imagePath).ToLowerInvariant() is ".jpg" or ".jpeg" &&
                (ForceStagedJpegWrites || AdaptiveMetadataWriteTimeout.SourceKey(imagePath)
                    .StartsWith("SMB:", StringComparison.Ordinal)))
            {
                LastVerifiedExif = MetadataFileWriter.WriteStagedJpeg(imagePath, commands,
                    this, local => ExecuteNativeMetadataEdit(local, commands, cancellationToken),
                    cancellationToken);
                return;
            }
            ExecuteNativeMetadataEdit(imagePath, commands, cancellationToken, knownSourceLengthBytes);
        }

        private void ExecuteNativeMetadataEdit(string imagePath,
            IReadOnlyList<string> commands, CancellationToken cancellationToken,
            long? knownSourceLengthBytes = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (imagePath.Contains('\r') || imagePath.Contains('\n'))
                throw new ArgumentException("Dateipfade dürfen keine Zeilenumbrüche enthalten.");
            // Use a caller's already verified FileInfo.Length; only direct
            // standalone calls need an additional file-size lookup.
            var length = knownSourceLengthBytes ??
                new FileInfo(imagePath).Length;
            var sourceKey = AdaptiveMetadataWriteTimeout.SourceKey(imagePath);
            var writeTimeout = WriteTimeouts.GetTimeout(sourceKey, length);
            StartIfNeeded();
            var process = _process!;
            cancellationToken.ThrowIfCancellationRequested();
            TryRemoveAbandonedTemporaryOutput(imagePath, TimeSpan.FromMinutes(30));
            var temporaryExistedBeforeWrite =
                File.Exists(imagePath + "_exiftool_tmp");
            var request = ++_nextRequestId;
            var writeStart = Stopwatch.GetTimestamp();
            var diagnostics = new NativeDiagnosticCapture();
            _nativeCapture = diagnostics;
            process.StandardInput.WriteLine("-echo2");
            process.StandardInput.WriteLine(diagnostics.Start);
            process.StandardInput.WriteLine("-echo4");
            process.StandardInput.WriteLine(diagnostics.End);
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
                // Only writes use the shared adaptive deadline. Read-only
                // EXIF and keyword reads retain their unchanged 90s/30s
                // response limits. A native write remains noninterruptible
                // until its atomic file replacement has finished.
                response = ReadResponseOrReset(process, request,
                    CancellationToken.None, writeTimeout);
            }
            catch
            {
                _nativeCapture = null;
                if (!temporaryExistedBeforeWrite)
                    TryRemoveAbandonedTemporaryOutput(imagePath, TimeSpan.Zero);
                throw;
            }
            string stderr = "";
            try { stderr = diagnostics.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
            catch (TimeoutException) { PCasaDiagnostics.WriteMessage("ExifTool.StderrBoundary", "Native diagnostic boundary not received."); }
            finally { _nativeCapture = null; }
            if (!response.Contains("1 image files updated",
                StringComparison.Ordinal))
            {
                if (!temporaryExistedBeforeWrite)
                    TryRemoveAbandonedTemporaryOutput(imagePath, TimeSpan.Zero);
                var message = "ExifTool konnte die Datei nicht aktualisieren: " +
                    response.Trim() + "\n" + stderr.Trim();
                if (IsStructuralFailure(stderr)) throw new ExifToolStructuralException(message);
                throw new IOException(message);
            }
            // Learn ONLY from a successful native commit, never from a
            // canceled, failed or potentially disconnected source.
            WriteTimeouts.RecordSuccess(sourceKey, length,
                Stopwatch.GetElapsedTime(writeStart));
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
                executable = OperatingSystem.IsWindows()
                    ? Path.Combine(AppContext.BaseDirectory, "exiftool", "exiftool.exe")
                    : "exiftool";
            if ((OperatingSystem.IsWindows() || Path.IsPathFullyQualified(executable)) && !File.Exists(executable))
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
            var child = _process;
            _errorDrain = DrainDiagnosticsAsync(child, line =>
                ReferenceEquals(_process, child) && _nativeCapture?.Accept(line) == true);
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
