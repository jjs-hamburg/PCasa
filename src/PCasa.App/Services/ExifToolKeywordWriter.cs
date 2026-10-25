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

        // Read only the current XMP keyword field, never decode image pixels.
        // JSON preserves list items (including commas) without guesswork.
        public IReadOnlyList<string> Read(string imagePath,
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
            if (!entry.TryGetProperty("Subject", out var subject))
                return Array.Empty<string>();
            if (subject.ValueKind == System.Text.Json.JsonValueKind.Array)
                return subject.EnumerateArray().Select(x => x.GetString() ?? "")
                    .Where(x => x.Length > 0).ToArray();
            if (subject.ValueKind == System.Text.Json.JsonValueKind.String)
                return new[] { subject.GetString()! };
            return Array.Empty<string>();
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
                if (!failed.HasExited) failed.Kill(entireProcessTree: true);
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
            using var cancellation = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });

            // ExifTool -stay_open consumes one argument per line. -executeNN
            // delimits each complete write and its {readyNN} acknowledgment.
            var request = ++_nextRequestId;
            // A keyword-only edit must not change the file's modification
            // timestamp and trigger another full thumbnail/EXIF scan.
            process.StandardInput.WriteLine("-P");
            process.StandardInput.WriteLine("-overwrite_original");
            if (keywords.Count == 0)
                process.StandardInput.WriteLine("-XMP-dc:Subject=");
            foreach (var keyword in keywords)
                process.StandardInput.WriteLine("-XMP-dc:Subject=" + keyword);
            // The executable receives absolute paths from the catalogue.
            // Do not send '--' in stay-open mode: the subsequent -executeN
            // must remain recognizable as a control command.
            process.StandardInput.WriteLine(imagePath);
            process.StandardInput.WriteLine("-execute" + request);
            process.StandardInput.Flush();

            var response = ReadResponseOrReset(process, request, cancellationToken);

            // Stay-open mode does not return a separate exit code per request.
            // The response acknowledges the write; uncertain results remain errors.
            if (!response.ToString().Contains("1 image files updated",
                StringComparison.Ordinal))
                throw new IOException("ExifTool konnte die Datei nicht aktualisieren: " +
                    response.ToString().Trim());
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
