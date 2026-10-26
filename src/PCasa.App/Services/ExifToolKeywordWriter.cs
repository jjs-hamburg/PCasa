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
    public static void Write(string imagePath, IReadOnlyList<string> keywords)
    {
        using var session = new Session();
        session.Write(imagePath, keywords);
    }

    public sealed class Session : IDisposable
    {
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
                failed.Dispose();
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
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (imagePath.Contains('\r') || imagePath.Contains('\n'))
                throw new ArgumentException("Dateipfade dürfen keine Zeilenumbrüche enthalten.");
            foreach (var keyword in keywords)
                if (keyword.Contains('\r') || keyword.Contains('\n'))
                    throw new ArgumentException("Ein Tag darf keine Zeilenumbrüche enthalten.");

            StartIfNeeded();
            var process = _process!;
            // Unlike metadata reads, a write MUST NOT be killed by an
            // ordinary scheduler yield. ExifTool creates a temporary file
            // before atomically replacing the original; terminating the
            // process mid-transaction leaves an *_exiftool_tmp orphan.
            // Check cancellation before sending the complete write request,
            // then let the in-flight native transaction finish or hit the
            // bounded response timeout.
            cancellationToken.ThrowIfCancellationRequested();
            TryRemoveAbandonedTemporaryOutput(imagePath, TimeSpan.FromMinutes(30));

            // ExifTool -stay_open consumes one argument per line. -executeNN
            // delimits each complete write and its {readyNN} acknowledgment.
            var request = ++_nextRequestId;
            // A keyword-only edit must not change the file's modification
            // timestamp and trigger another full thumbnail/EXIF scan.
            process.StandardInput.WriteLine("-P");
            process.StandardInput.WriteLine("-overwrite_original");
            var isSonyArw = Path.GetExtension(imagePath).Equals(".arw",
                StringComparison.OrdinalIgnoreCase);
            // ExifTool treats repeated '=' for a list-type tag as one
            // replacement list. Write both interoperable Picasa fields in
            // the same file transaction; remove both when the list is empty.
            if (keywords.Count == 0)
            {
                process.StandardInput.WriteLine("-XMP-dc:Subject=");
                if (!isSonyArw) process.StandardInput.WriteLine("-IPTC:Keywords=");
            }
            else
            {
                // Repeated '=' for ExifTool list tags defines one replacement
                // list. Mixing '=' and '+=' would change its semantics.
                foreach (var keyword in keywords)
                {
                    if (!isSonyArw && Encoding.UTF8.GetByteCount(keyword) > 64)
                        throw new InvalidDataException(
                            "IPTC-Schlagwort überschreitet 64 UTF-8-Bytes: " + keyword);
                    process.StandardInput.WriteLine("-XMP-dc:Subject=" + keyword);
                    if (!isSonyArw) process.StandardInput.WriteLine("-IPTC:Keywords=" + keyword);
                }
                if (!isSonyArw) process.StandardInput.WriteLine("-IPTC:CodedCharacterSet=UTF8");
            }
            // The executable receives absolute paths from the catalogue.
            // Do not send '--' in stay-open mode: the subsequent -executeN
            // must remain recognizable as a control command.
            process.StandardInput.WriteLine(imagePath);
            process.StandardInput.WriteLine("-execute" + request);
            process.StandardInput.Flush();

            // Do not pass the yield token once -execute was committed:
            // only the protocol timeout may terminate this process. The
            // calling package checkpoints its successful file write before
            // acknowledging a yield.
            string response;
            try
            {
                response = ReadResponseOrReset(process, request, CancellationToken.None);
            }
            catch
            {
                // ReadResponseOrReset has terminated the owned ExifTool
                // process. Never touch a temp file unless the full original
                // still exists; its contents remain the only safe source.
                TryRemoveAbandonedTemporaryOutput(imagePath, TimeSpan.Zero);
                throw;
            }

            // Stay-open mode does not return a separate exit code per request.
            // The response acknowledges the write; uncertain results remain errors.
            if (!response.ToString().Contains("1 image files updated",
                StringComparison.Ordinal))
                throw new IOException("ExifTool konnte die Datei nicht aktualisieren: " +
                    response.ToString().Trim());
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
            _process = Process.Start(start)
                ?? throw new IOException("ExifTool konnte nicht gestartet werden.");
            // Always drain stderr concurrently so a warning cannot deadlock
            // a subsequent request. The final metadata read verifies success.
            _errorDrain = _process.StandardError.ReadToEndAsync();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_process is null) return;
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.WriteLine("-stay_open");
                    _process.StandardInput.WriteLine("False");
                    _process.StandardInput.Flush();
                    if (!_process.WaitForExit(2000))
                    {
                        _process.Kill(entireProcessTree: true);
                        _process.WaitForExit(1500);
                    }
                }
                // Observe the error reader after the child has exited.
                if (_errorDrain?.IsCompletedSuccessfully == true)
                    _ = _errorDrain.Result;
            }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }
    }
}
