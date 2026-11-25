using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

/// <summary>
/// Full-resolution still-photo editing, performed only by the durable manual/P0
/// scheduler worker. Original bytes remain untouched until the central Undo
/// archive is verified and the staged result passes full decoding/metadata
/// validation. RAW never enters this writer; no sidecar is retained.
/// </summary>
public static class JpegCropWriter
{
    public static void Save(string originalPath, Rect normalized, CancellationToken token)
        => Save(originalPath, [new PhotoEditStep("crop", normalized.X, normalized.Y,
            normalized.Width, normalized.Height)], token);

    /// <summary>
    /// Atomic multi-step editor commit: replay the unsaved UI history once,
    /// at full resolution, retain source format/metadata and capture Undo
    /// before replacing the original. No sidecars.
    /// </summary>
    public static void Save(string originalPath,
        IReadOnlyList<PhotoEditStep> operations, CancellationToken token)
        => SaveCore(originalPath, null, operations, token);

    // The destination name is picked outside the UI dispatcher and persisted
    // in the same durable edit recipe as the source image and the steps.
    // No overwrite: even a retried durable job must not replace an existing
    // copy or accidentally modify the source.
    // JPEG quarter-turns use EXIF orientation; the compressed image data
    // remains untouched. Stage and verify before replacing the source.
    public static void RotateGalleryImage(string originalPath, int turns, CancellationToken token)
    {
        turns = ((turns % 4) + 4) % 4;
        if (turns == 0) return;
        if (Path.GetExtension(originalPath).ToLowerInvariant() is not (".jpg" or ".jpeg"))
        {
            Save(originalPath, [new PhotoEditStep("rotate", Degrees: turns * 90)], token);
            return;
        }

        var path = Path.GetFullPath(originalPath);
        using var lease = SourceFileLocks.Enter(path, token);
        var before = new FileInfo(path);
        var size = before.Length;
        var modified = before.LastWriteTimeUtc;
        var created = before.CreationTimeUtc;
        int orientation;
        using (var input = File.Open(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
            orientation = ExifOrientation.Read(input);
        var target = ExifOrientation.RotateQuarterTurns(orientation, turns);
        var staged = Path.Combine(Path.GetDirectoryName(path)!,
            ".pcasa-edit-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        try
        {
            token.ThrowIfCancellationRequested();
            File.Copy(path, staged);
            using (var writer = new ExifToolKeywordWriter.Session())
                writer.WriteOrientation(staged, target, token);
            using (var input = File.OpenRead(staged))
                if (ExifOrientation.Read(input) != target)
                    throw new IOException("JPEG orientation verification failed.");
            ExifDateMetadata.VerifyUnchanged(path, staged, token);
            new UndoHistoryService(UndoOptions.Load()).CaptureBeforeSave(path, token);
            ReplaceWithVerifiedSharingRetry(staged, path, token, size, modified);
            try
            {
                File.SetCreationTimeUtc(path, created);
                var now = DateTime.UtcNow;
                File.SetLastWriteTimeUtc(path, now > modified ? now : modified.AddMilliseconds(1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { PCasaDiagnostics.Write("Image.Rotate.Timestamp", ex); }
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { PCasaDiagnostics.Write("Image.Rotate.Cleanup", ex); }
        }
    }

    public static string GetAvailableCopyPath(string originalPath)
    {
        var source = Path.GetFullPath(originalPath);
        var directory = Path.GetDirectoryName(source)!;
        var baseName = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        var initial = Path.Combine(directory, baseName + "-Kopie" + extension);
        if (!File.Exists(initial)) return initial;
        for (var number = 2; number < 100000; number++)
        {
            var path = Path.Combine(directory,
                baseName + "-Kopie-" + number + extension);
            if (!File.Exists(path)) return path;
        }
        throw new IOException("Kein freier Dateiname für die Bildkopie.");
    }

    public static void SaveAsCopy(string originalPath, string copyPath,
        IReadOnlyList<PhotoEditStep> operations, CancellationToken token)
        => SaveCore(originalPath, copyPath, operations, token);

    // An external editor has not yet committed its result to the gallery.
    // Apply approved internal adjustments to that EXISTING isolated output,
    // reusing precisely the same full-resolution renderer, staging, format
    // encoder, metadata transfer and validated File.Replace as normal Save.
    // The central external transaction captures ONE Undo of the real
    // original when the user subsequently chooses Save; the temporary
    // workspace itself must never enter the user's Undo archive.
    internal static string EditExternalWorkspaceResult(
        string resultPath, string sessionDirectory,
        IReadOnlyList<PhotoEditStep> operations, CancellationToken token)
    {
        var result = Path.GetFullPath(resultPath);
        var workspace = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(sessionDirectory));
        if (!string.Equals(Path.GetDirectoryName(result), workspace,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Externe Bearbeitung außerhalb des isolierten Arbeitsverzeichnisses.");
        if (!EditableImageFormats.Supports(result))
            throw new NotSupportedException(
                "Das externe Ergebnisformat erlaubt keine interne Bildbearbeitung.");

        // Topaz/another editor may still hold its export open. Never replace
        // an editor-owned file: prepare the internal recipe on our private
        // copy, then submit that image to the existing external transaction.
        // The copy also prevents later external writes from undoing the
        // in-PCasa straighten/crop while Save is in progress.
        var privateDirectory = Path.Combine(workspace, ".pcasa-internal");
        Directory.CreateDirectory(privateDirectory);
        var candidate = Path.Combine(privateDirectory,
            "result" + Path.GetExtension(result).ToLowerInvariant());
        if (File.Exists(candidate))
            throw new IOException(
                "Interne Arbeitskopie ist bereits vorhanden; bitte den vorigen Auftrag abschließen.");
        try
        {
            using (var source = new FileStream(result, FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var target = new FileStream(candidate, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
                source.CopyTo(target);
            token.ThrowIfCancellationRequested();
            SaveCore(candidate, null, operations, token, captureUndo: false);
            return candidate;
        }
        catch
        {
            try { if (File.Exists(candidate)) File.Delete(candidate); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void SaveCore(string originalPath, string? copyPath,
        IReadOnlyList<PhotoEditStep> operations, CancellationToken token,
        bool captureUndo = true)
    {
        var extension = Path.GetExtension(originalPath);
        if (!EditableImageFormats.Supports(originalPath))
            throw new NotSupportedException(
                UiLanguage.T("Dieses Format kann nicht bearbeitet werden. RAW und Videos bleiben unverändert."));
        if (operations.Count == 0)
            throw new InvalidDataException("Keine Bildbearbeitung zu speichern.");
        var fullPath = Path.GetFullPath(originalPath);
        string? destination = copyPath is null ? null : Path.GetFullPath(copyPath);
        if (destination is not null)
        {
            // A copy is ALWAYS a sibling of the source and must use our
            // explicit -Kopie / -Kopie-2 naming scheme.
            var dir = Path.GetDirectoryName(fullPath)!;
            var stem = Path.GetFileNameWithoutExtension(fullPath) + "-Kopie";
            var suffix = Path.GetFileNameWithoutExtension(destination);
            var extra = suffix.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                ? suffix[stem.Length..] : "INVALID";
            if (!string.Equals(Path.GetDirectoryName(destination), dir,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(destination), extension,
                    StringComparison.OrdinalIgnoreCase) ||
                (extra.Length != 0 &&
                 !(extra.StartsWith("-", StringComparison.Ordinal) &&
                   int.TryParse(extra[1..], out var sequence) && sequence >= 2)))
                throw new InvalidDataException("Ungültiger Pfad für die Bildkopie.");
        }

        // One source lock and one validated temporary image for both save
        // modes. Preserve the original extension; avoid JPEG conversion.
        using var lease = SourceFileLocks.Enter(fullPath, token);
        var original = new FileInfo(fullPath);
        var originalWriteTime = original.LastWriteTimeUtc;
        var originalCreationTime = original.CreationTimeUtc;
        var staged = Path.Combine(Path.GetDirectoryName(fullPath)!,
            ".pcasa-edit-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            token.ThrowIfCancellationRequested();
            if (destination is not null && File.Exists(destination))
                throw new IOException("Die Bildkopie existiert bereits: " +
                    Path.GetFileName(destination));
            EditableImageFormats.ValidateStillImage(fullPath);
            var pixels = EditableImageFormats.DecodeForEdit(fullPath);
            var result = PhotoEditSession.Render(pixels, operations, token);
            var width = result.PixelWidth;
            var height = result.PixelHeight;
            token.ThrowIfCancellationRequested();
            EditableImageFormats.Encode(result, staged);
            // Preserve EXIF/IPTC/XMP/GPS from the original in BOTH modes.
            // Pixels are already upright; EXIF Orientation must reset to 1.
            CopyMetadata(fullPath, staged, width, height, token);
            // The generic ExifTool -TagsFromFile/-all:all transfer does not
            // always reproduce every camera's date-group and subsecond tag
            // verbatim (and the image encoder may have supplied its own).
            // Reconcile ONLY protected date tags on the STAGED image. Never
            // loosen their equality check or touch the original before the
            // candidate is fully verified.
            ExifDateMetadata.Preserve(fullPath, staged, token);
            // Validate before the atomic original replacement. No internal
            // crop, adjustment or duplicate may rewrite camera dates.
            ExifDateMetadata.VerifyUnchanged(fullPath, staged, token);
            var savedDimensions = EditableImageFormats.Dimensions(staged);
            if (savedDimensions.Width != width || savedDimensions.Height != height)
                throw new InvalidDataException(
                    UiLanguage.T("Die bearbeitete Bilddatei ist nicht vollständig."));
            using (var verify = File.OpenRead(staged))
                _ = SHA256.HashData(verify);
            token.ThrowIfCancellationRequested();
            if (destination is not null)
            {
                // File.Move never replaces an existing file. A racing
                // process or durable retry therefore cannot destroy a copy.
                try { File.Move(staged, destination); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FileLockDiagnostics.ReportFailure("Image.Edit.SaveCopy.Move:" +
                        string.Join(",", operations.Select(step => step.Kind)),
                        ex, fullPath, staged, destination);
                    throw;
                }
            }
            else
            {
                // Only in-place writes need a pre-save Undo snapshot.
                if (captureUndo)
                {
                    try
                    {
                        new UndoHistoryService(UndoOptions.Load())
                            .CaptureBeforeSave(fullPath, token);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        FileLockDiagnostics.ReportFailure("Image.Edit.UndoSnapshot:" +
                            string.Join(",", operations.Select(step => step.Kind)),
                            ex, fullPath, staged);
                        throw;
                    }
                }
                token.ThrowIfCancellationRequested();
                try { ReplaceWithSharingRetry(staged, fullPath, token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FileLockDiagnostics.ReportFailure("Image.Edit.Replace:" +
                        string.Join(",", operations.Select(step => step.Kind)),
                        ex, fullPath, staged);
                    throw;
                }
                // A successful pixel edit is a new file revision. Preserve
                // creation time, but advance modification time so OneDrive,
                // FileSystemWatcher and the packed-cache stamp can detect it.
                // The old timestamp caused a changed image to masquerade as
                // the previously cached original.
                try
                {
                    File.SetCreationTimeUtc(fullPath, originalCreationTime);
                    var now = DateTime.UtcNow;
                    File.SetLastWriteTimeUtc(fullPath, now > originalWriteTime
                        ? now : originalWriteTime.AddMilliseconds(1));
                }
                catch (IOException ex)
                { PCasaDiagnostics.Write("Image.Edit.Timestamp", ex); }
                catch (UnauthorizedAccessException ex)
                { PCasaDiagnostics.Write("Image.Edit.Timestamp", ex); }
            }
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // A foreground save must tolerate a short-lived Windows sharing lock
    // left by an overlapping reader (native codec, antivirus, thumbnailer).
    // Reuse the SAME fully validated staged image and the SAME Undo snapshot;
    // NEVER re-run edits or create a second durable file write. An actual
    // persistent foreign lock still fails after a bounded, cancelable wait.
    // Retain the three-argument entry point used by existing Crop callers
    // and Windows regression tests. Checked metadata commits use the same
    // implementation, with source-identity validation between retries.
    internal static void ReplaceWithSharingRetry(string staged, string original,
        CancellationToken token)
        => ReplaceWithSharingRetryCore(staged, original, token, null, null);

    internal static void ReplaceWithVerifiedSharingRetry(string staged, string original,
        CancellationToken token, long expectedSize, DateTime expectedWriteUtc)
        => ReplaceWithSharingRetryCore(staged, original, token,
            expectedSize, expectedWriteUtc);

    private static void ReplaceWithSharingRetryCore(string staged, string original,
        CancellationToken token, long? expectedSize, DateTime? expectedWriteUtc)
    {
        const int maxRetries = 16;
        for (var retry = 0; ; retry++)
        {
            token.ThrowIfCancellationRequested();
            // A foreign writer may alter the source while our validated
            // candidate waits for Windows to release a transient reader.
            // Never replace newer bytes with an obsolete candidate.
            if (expectedSize is long size && expectedWriteUtc is DateTime stamp)
            {
                var source = new FileInfo(original);
                if (source.Length != size || source.LastWriteTimeUtc != stamp)
                    throw new IOException(
                        "JPEG-Original während des Wartens auf Dateifreigabe verändert.");
            }
            try
            {
                File.Replace(staged, original, null);
                if (retry > 0)
                    PCasaDiagnostics.WriteMessage("Image.Edit.SharingRetry",
                        $"File.Replace succeeded after {retry} retry(s): {original}");
                return;
            }
            catch (IOException ex) when (
                (ex.HResult & 0xFFFF) is 32 or 33 or 1175 or 1176 or 1177 &&
                retry < maxRetries)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(400, 100 + retry * 35));
                // The scheduler owns this worker; never delay the WPF UI.
                if (token.WaitHandle.WaitOne(delay))
                    token.ThrowIfCancellationRequested();
            }
        }
    }

    private static void CopyMetadata(string original, string candidate,
        int width, int height, CancellationToken token)
    {
        var exifTool = Environment.GetEnvironmentVariable("PCASA_EXIFTOOL_PATH");
        if (string.IsNullOrWhiteSpace(exifTool))
            exifTool = Path.Combine(AppContext.BaseDirectory, "exiftool", "exiftool.exe");
        if (!File.Exists(exifTool))
            throw new FileNotFoundException(UiLanguage.T("ExifTool fehlt – Original nicht verändert."),
                exifTool);
        var start = new ProcessStartInfo(exifTool)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var value in new[]
        {
            "-overwrite_original", "-P", "-q", "-q",
            "-TagsFromFile", original, "-all:all",
            "-Orientation#=1", "-ExifImageWidth=" + width,
            "-ExifImageHeight=" + height,
            candidate
        })
            start.ArgumentList.Add(value);
        using var child = ExifToolProcessRegistry.Start(start);
        try
        {
            using var cancelled = token.Register(() =>
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            });
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            var finished = child.WaitForExit(120000);
            if (!finished)
            {
                try { child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            token.ThrowIfCancellationRequested();
            var detail = errors.GetAwaiter().GetResult().Trim();
            if (string.IsNullOrWhiteSpace(detail))
                detail = output.GetAwaiter().GetResult().Trim();
            if (!finished || child.ExitCode != 0)
                throw new IOException(
                    UiLanguage.T("ExifTool konnte die Metadaten nicht vollständig übertragen.") +
                    (string.IsNullOrWhiteSpace(detail) ? "" :
                        Environment.NewLine + detail[..Math.Min(detail.Length, 900)]));
        }
        finally { ExifToolProcessRegistry.Retire(child); }
    }
}
