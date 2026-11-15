using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PCasa.App.Services;

public enum ExternalEditDecision { Cancel, SaveCopy, Save }

public sealed record ExternalEditOutputStamp(
    string Path, long Length, DateTime LastWriteUtc);

public sealed record ExternalEditSnapshot(
    Guid Id, string OriginalPath, string BackupPath,
    string OriginalSha256, long OriginalLength, DateTime OriginalWriteUtc,
    DateTime StartedUtc, ExternalEditOutputStamp[] ExistingOutputs)
{
    public string DirectoryPath => Path.GetDirectoryName(BackupPath)!;
}

/// <summary>
/// Transaction boundary for independent editors. Only the original filename
/// reaches the editor; a verified rollback copy is kept privately under PCasa.
/// No sidecar and no working image is placed near the original.
/// A session remains recoverable after a crash until PCasa commits one of the
/// three explicit user decisions.
/// </summary>
public static class ExternalEditTransaction
{
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "ExternalEditBackups");

    private static string Sha256(string file)
    {
        using var stream = File.Open(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static ExternalEditSnapshot? FindPending(string source)
    {
        if (!Directory.Exists(Root)) return null;
        var target = Path.GetFullPath(source);
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            try
            {
                var metadata = Path.Combine(folder, "session.json");
                if (!File.Exists(metadata)) continue;
                var s = JsonSerializer.Deserialize<ExternalEditSnapshot>(
                    File.ReadAllText(metadata));
                if (s is not null &&
                    s.OriginalPath.Equals(target, StringComparison.OrdinalIgnoreCase))
                    return s;
            }
            catch (Exception ex) when (ex is IOException or
                UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // Leave other transactions untouched. No destructive cleanup.
            }
        }
        return null;
    }

    public static ExternalEditSnapshot Begin(string original,
        CancellationToken token = default)
    {
        var path = Path.GetFullPath(original);
        if (RawImageSource.IsRawPath(path))
            throw new NotSupportedException(
                "RAW-Dateien können nicht extern bearbeitet werden.");
        if (FindPending(path) is not null)
            throw new InvalidOperationException(
                UiLanguage.T("Für diese Datei existiert noch eine nicht abgeschlossene externe Bearbeitung."));

        var id = Guid.NewGuid();
        var directory = Path.Combine(Root, id.ToString("N"));
        var backup = Path.Combine(directory, "original.bin");
        using (SourceFileLocks.Enter(path, token))
        {
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException(
                UiLanguage.T("Originalbild nicht gefunden."), path);
            Directory.CreateDirectory(directory);
            try
            {
                using (var input = new FileStream(path, FileMode.Open,
                           FileAccess.Read, FileShare.Read, 1024 * 1024))
                using (var output = new FileStream(backup, FileMode.CreateNew,
                           FileAccess.Write, FileShare.None, 1024 * 1024,
                           FileOptions.WriteThrough))
                {
                    input.CopyTo(output, 1024 * 1024);
                    output.Flush(flushToDisk: true);
                }
                if (new FileInfo(backup).Length != file.Length)
                    throw new IOException(
                        UiLanguage.T("Das Original wurde während der Sicherung verändert."));
                var checksum = Sha256(backup);
                if (Sha256(path) != checksum)
                    throw new IOException(
                        UiLanguage.T("Original und Sicherheitskopie stimmen nicht überein."));
                // Snapshot the *existing* same-name exports. A timestamp
                // alone is unsafe when two edits occur within a second and
                // could cause Cancel to delete a previous output.
                var existing = ExistingExports(path);
                var snapshot = new ExternalEditSnapshot(
                    id, path, backup, checksum, file.Length,
                    file.LastWriteTimeUtc, DateTime.UtcNow, existing);
                var meta = Path.Combine(directory, "session.json");
                File.WriteAllText(meta, JsonSerializer.Serialize(snapshot));
                return snapshot;
            }
            catch
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                throw;
            }
        }
    }

    private static void VerifySnapshot(ExternalEditSnapshot s)
    {
        if (!File.Exists(s.BackupPath) ||
            new FileInfo(s.BackupPath).Length != s.OriginalLength ||
            Sha256(s.BackupPath) != s.OriginalSha256)
            throw new IOException(UiLanguage.T("Sicherheitskopie ist nicht mehr gültig."));
    }

    public static bool OriginalChanged(ExternalEditSnapshot s)
        => !File.Exists(s.OriginalPath) ||
           new FileInfo(s.OriginalPath).Length != s.OriginalLength ||
           Sha256(s.OriginalPath) != s.OriginalSha256;

    private static ExternalEditOutputStamp[] ExistingExports(string original)
    {
        var parent = Path.GetDirectoryName(original)!;
        var prefix = Path.GetFileNameWithoutExtension(original);
        return Directory.EnumerateFiles(parent, prefix + "*",
                SearchOption.TopDirectoryOnly)
            .Where(p => !p.Equals(original, StringComparison.OrdinalIgnoreCase) &&
                FolderScanner.IsSupportedImagePath(p))
            .Select(p => new FileInfo(p))
            .Where(f => f.Exists)
            .Select(f => new ExternalEditOutputStamp(
                f.FullName, f.Length, f.LastWriteTimeUtc))
            .ToArray();
    }

    private static bool IsNewOrModified(ExternalEditSnapshot s,
        ExternalEditOutputStamp stamp)
        => !s.ExistingOutputs.Any(old =>
            old.Path.Equals(stamp.Path, StringComparison.OrdinalIgnoreCase) &&
            old.Length == stamp.Length &&
            old.LastWriteUtc == stamp.LastWriteUtc);

    public static string? DetectResult(ExternalEditSnapshot s)
    {
        if (OriginalChanged(s)) return s.OriginalPath;
        var exports = ExistingExports(s.OriginalPath)
            .Where(stamp => stamp.Length > 0 && IsNewOrModified(s, stamp))
            .OrderByDescending(stamp => stamp.LastWriteUtc)
            .ToArray();
        if (exports.Length > 1)
            throw new InvalidDataException(
                UiLanguage.T("Mehrere externe Ergebnisse gefunden; keine automatische Zuordnung möglich."));
        return exports.Length == 1 ? exports[0].Path : null;
    }

    private static bool IsSessionExport(ExternalEditSnapshot s, string file)
    {
        var full = Path.GetFullPath(file);
        if (full.Equals(s.OriginalPath, StringComparison.OrdinalIgnoreCase))
            return false;
        return Path.GetDirectoryName(full)!.Equals(
                   Path.GetDirectoryName(s.OriginalPath),
                   StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileName(full).StartsWith(
                   Path.GetFileNameWithoutExtension(s.OriginalPath),
                   StringComparison.OrdinalIgnoreCase) &&
               File.Exists(full) &&
               // Never delete a file that predated this edit even if Topaz
               // overwrote it; PCasa has no snapshot of that other file.
               !s.ExistingOutputs.Any(previous =>
                   previous.Path.Equals(full, StringComparison.OrdinalIgnoreCase));
    }

    private static string MakeCopy(string source, string original,
        CancellationToken token)
    {
        var parent = Path.GetDirectoryName(original)!;
        var basename = Path.GetFileNameWithoutExtension(original);
        var extension = Path.GetExtension(source);
        for (var n = 1; n < 10000; n++)
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(parent,
                basename + (n == 1 ? "-Kopie" : "-Kopie-" + n) + extension);
            if (File.Exists(target)) continue;
            try
            {
                using var input = File.Open(source, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = File.Open(target, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None);
                try
                {
                    input.CopyTo(output);
                    output.Flush(true);
                    if (output.Length != input.Length)
                        throw new IOException(UiLanguage.T("Kopie ist unvollständig."));
                }
                catch
                {
                    output.Dispose();
                    File.Delete(target);
                    throw;
                }
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                // A different operation reserved this filename first.
                continue;
            }
        }
        throw new IOException(UiLanguage.T("Kein freier Dateiname für die Kopie."));
    }

    private static void ReplaceSafely(string source, string target,
        CancellationToken token)
    {
        // Temp sits on the target volume, making the last rename atomic
        // for a regular NTFS/SMB file. Original backup stays local until
        // complete verification.
        var staging = Path.Combine(Path.GetDirectoryName(target)!,
            ".pcasa-external-" + Guid.NewGuid().ToString("N") +
            Path.GetExtension(target));
        try
        {
            using (var input = File.Open(source, FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = File.Open(staging, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                token.ThrowIfCancellationRequested();
                input.CopyTo(output);
                output.Flush(true);
                if (output.Length != input.Length)
                    throw new IOException(UiLanguage.T("Dateiübernahme unvollständig."));
            }
            token.ThrowIfCancellationRequested();
            File.Move(staging, target, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static void Restore(ExternalEditSnapshot s, CancellationToken token)
    {
        ReplaceSafely(s.BackupPath, s.OriginalPath, token);
        File.SetLastWriteTimeUtc(s.OriginalPath, s.OriginalWriteUtc);
        if (OriginalChanged(s))
            throw new IOException(UiLanguage.T("Original konnte nicht überprüft wiederhergestellt werden."));
    }

    /// <returns>Committed file path, or null when canceled.</returns>
    public static string? Commit(ExternalEditSnapshot s, string? output,
        ExternalEditDecision decision, CancellationToken token = default)
    {
        VerifySnapshot(s);
        var result = output is null ? null : Path.GetFullPath(output);
        using (SourceFileLocks.Enter(s.OriginalPath, token))
        {
            token.ThrowIfCancellationRequested();
            if (decision == ExternalEditDecision.Cancel)
            {
                if (OriginalChanged(s)) Restore(s, token);
                if (result is not null && IsSessionExport(s, result))
                    File.Delete(result);
                Directory.Delete(s.DirectoryPath, recursive: true);
                return null;
            }
            if (result is null || !File.Exists(result))
                throw new FileNotFoundException(
                    UiLanguage.T("Das externe Programm hat kein Ergebnis gespeichert."), result);
            // Validate BEFORE Save / Save Copy could delete the rollback.
            // A RAW result is explicitly out of scope for Alpha.32.
            if (RawImageSource.IsRawPath(result))
                throw new NotSupportedException(UiLanguage.T("RAW-Ausgaben werden nicht bearbeitet."));
            if (new FileInfo(result).Length == 0)
                throw new InvalidDataException(UiLanguage.T("Das externe Ergebnis ist leer."));
            if (!VideoFormats.IsVideo(result))
                EditableImageFormats.ValidateStillImage(result);

            if (decision == ExternalEditDecision.SaveCopy)
            {
                var copy = MakeCopy(result, s.OriginalPath, token);
                try
                {
                    if (OriginalChanged(s)) Restore(s, token);
                }
                catch
                {
                    // The new copy remains safe; the backup also remains for
                    // recovery. Never delete either after a failed restore.
                    throw;
                }
                if (IsSessionExport(s, result)) File.Delete(result);
                Directory.Delete(s.DirectoryPath, recursive: true);
                return copy;
            }

            // Preserve JPG pre-launch pixels in the EXISTING central Undo
            // archive before accepting any external replacement. No second
            // user-visible backup and no sidecar is created.
            if (Path.GetExtension(s.OriginalPath).ToLowerInvariant() is
                    ".jpg" or ".jpeg")
                new UndoHistoryService(UndoOptions.Load())
                    .CaptureExternalOriginal(s, token);

            if (!result.Equals(s.OriginalPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!Path.GetExtension(result).Equals(
                    Path.GetExtension(s.OriginalPath),
                    StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException(
                        UiLanguage.T("Anderes Ausgabeformat: bitte 'Als Kopie speichern' wählen."));
                ReplaceSafely(result, s.OriginalPath, token);
                if (IsSessionExport(s, result)) File.Delete(result);
            }
            if (!File.Exists(s.OriginalPath) ||
                new FileInfo(s.OriginalPath).Length == 0)
                throw new IOException(UiLanguage.T("Bearbeitetes Original fehlt oder ist leer."));
            Directory.Delete(s.DirectoryPath, recursive: true);
            return s.OriginalPath;
        }
    }
}
