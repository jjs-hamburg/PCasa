using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PCasa.App.Services;

public enum ExternalEditDecision { Cancel, SaveCopy, Save }

public sealed record ExternalEditOutputStamp(
    string Path, long Length, DateTime LastWriteUtc);

// One durable transaction for the entire PCasa process, not a folder per edit.
// BackupPath is the single working IMAGE ("photo.jpg", "video.mp4", etc.).
// It is NEVER a copy of a modified PCasa original. The source stays untouched
// until an explicit Save, which uses the existing central Undo archive.
public sealed record ExternalEditSnapshot(
    Guid Id, string OriginalPath, string BackupPath,
    string OriginalSha256, long OriginalLength,
    DateTime OriginalWriteUtc, DateTime StartedUtc,
    ExternalEditOutputStamp[] ExistingOutputs,
    ExternalEditDecision? Finalizing = null, string? FinalTarget = null)
{
    public string DirectoryPath => Path.GetDirectoryName(BackupPath)!;
}

public static class ExternalEditTransaction
{
    private static readonly object Gate = new();
    private static string? _testDirectory;
    public static string DirectoryPath => _testDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "ExternalEdit");

    // Test isolation: never erase or create sessions in the user's real
    // %LocalAppData% when running the Windows smoke regression executable.
    public static IDisposable UseTestDirectory(string directory)
    {
        lock (Gate)
        {
            if (_testDirectory is not null)
                throw new InvalidOperationException("A test directory is already active.");
            _testDirectory = Path.GetFullPath(directory);
            return new TestDirectoryLease();
        }
    }

    private sealed class TestDirectoryLease : IDisposable
    {
        public void Dispose()
        {
            lock (Gate) _testDirectory = null;
        }
    }
    private static string SessionFile => Path.Combine(DirectoryPath, "session.json");

    private static string Hash(string path)
    {
        using var input = File.Open(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static ExternalEditSnapshot? ReadPending()
    {
        if (!File.Exists(SessionFile)) return null;
        var pending = JsonSerializer.Deserialize<ExternalEditSnapshot>(
            File.ReadAllText(SessionFile))
            ?? throw new InvalidDataException("ExternalEdit: session.json ist ungültig.");
        if (pending.Id == Guid.Empty ||
            !Path.GetFullPath(pending.DirectoryPath).Equals(
                Path.GetFullPath(DirectoryPath), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(pending.BackupPath).StartsWith(
                VideoFormats.IsVideo(pending.OriginalPath) ? "video." : "photo.",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ExternalEdit: ungültiger Sitzungsstatus.");
        return pending;
    }

    public static ExternalEditSnapshot? FindPending(string? original = null)
    {
        lock (Gate)
        {
            var session = ReadPending();
            return session is not null &&
                (original is null || Path.GetFullPath(original).Equals(
                    session.OriginalPath, StringComparison.OrdinalIgnoreCase))
                ? session : null;
        }
    }

    private static void WritePending(ExternalEditSnapshot session)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = SessionFile + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(session));
            File.Move(temporary, SessionFile, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static ExternalEditSnapshot Begin(string original, CancellationToken token = default,
        Action<long, long>? progress = null)
    {
        var path = Path.GetFullPath(original);
        if (RawImageSource.IsRawPath(path))
            throw new NotSupportedException(
                UiLanguage.T("RAW-Dateien können nicht extern bearbeitet werden."));
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(SessionFile))
                throw new InvalidOperationException(
                    UiLanguage.T("Bitte die laufende externe Bearbeitung zuerst abschließen."));
            // Nothing can be removed while a persisted session exists.
            // A leftover directory without a manifest is a previously
            // completed/incomplete staging attempt, not an active edit.
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
            Directory.CreateDirectory(DirectoryPath);

            var inputName = (VideoFormats.IsVideo(path) ? "video" : "photo") +
                Path.GetExtension(path).ToLowerInvariant();
            var work = Path.Combine(DirectoryPath, inputName);
            try
            {
                using (SourceFileLocks.Enter(path, token))
                {
                    var before = new FileInfo(path);
                    if (!before.Exists)
                        throw new FileNotFoundException(
                            UiLanguage.T("Originalbild nicht gefunden."), path);
                    var originalLength = before.Length;
                    var originalWrite = before.LastWriteTimeUtc;
                    string checksum;
                    using (var reader = new FileStream(path, FileMode.Open,
                               FileAccess.Read, FileShare.Read, 1024 * 1024))
                    using (var output = new FileStream(work, FileMode.CreateNew,
                               FileAccess.Write, FileShare.None, 1024 * 1024,
                               FileOptions.WriteThrough))
                    using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buffer = new byte[1024 * 1024];
                        long copied = 0;
                        while (true)
                        {
                            token.ThrowIfCancellationRequested();
                            var count = reader.Read(buffer);
                            if (count == 0) break;
                            output.Write(buffer, 0, count);
                            hasher.AppendData(buffer, 0, count);
                            copied += count;
                            if (copied == originalLength || copied % (4 * 1024 * 1024) < count)
                                progress?.Invoke(copied, originalLength);
                        }
                        output.Flush(true);
                        if (copied != originalLength)
                            throw new IOException(UiLanguage.T(
                                "Das Original wurde während der Sicherung verändert."));
                        checksum = Convert.ToHexString(hasher.GetHashAndReset());
                    }
                    if (new FileInfo(path).LastWriteTimeUtc != originalWrite ||
                        new FileInfo(path).Length != originalLength)
                        throw new IOException(UiLanguage.T(
                            "Das Original wurde während der Sicherung verändert."));
                    // A broken source is rejected before launching an editor.
                    if (!VideoFormats.IsVideo(work))
                        EditableImageFormats.ValidateStillImage(work);
                    var session = new ExternalEditSnapshot(Guid.NewGuid(), path,
                        work, checksum, originalLength, originalWrite,
                        DateTime.UtcNow, []);
                    WritePending(session);
                    return session;
                }
            }
            catch
            {
                // A published manifest means recovery must be possible.
                if (!File.Exists(SessionFile))
                    try { Directory.Delete(DirectoryPath, recursive: true); }
                    catch (IOException) { }
                throw;
            }
        }
    }

    public static bool OriginalChanged(ExternalEditSnapshot session)
        => !File.Exists(session.OriginalPath) ||
           new FileInfo(session.OriginalPath).Length != session.OriginalLength ||
           Hash(session.OriginalPath) != session.OriginalSha256;

    public static string? DetectResult(ExternalEditSnapshot session)
    {
        lock (Gate)
        {
            CheckIdentity(session);
            var candidates = Directory.EnumerateFiles(DirectoryPath)
                .Where(path => !path.Equals(SessionFile,
                    StringComparison.OrdinalIgnoreCase) &&
                    !path.Equals(session.BackupPath,
                        StringComparison.OrdinalIgnoreCase) &&
                    (VideoFormats.IsVideo(session.OriginalPath)
                        ? VideoFormats.IsVideo(path)
                        : EditableImageFormats.Supports(path)))
                .Where(path => new FileInfo(path).Length > 0)
                .ToArray();
            if (candidates.Length > 1)
                throw new InvalidDataException(UiLanguage.T(
                    "Mehrere externe Ergebnisse gefunden; keine automatische Zuordnung möglich."));
            if (candidates.Length == 1) return candidates[0];
            if (!File.Exists(session.BackupPath))
                throw new FileNotFoundException("ExternalEdit: Arbeitsdatei fehlt.",
                    session.BackupPath);
            return Hash(session.BackupPath) == session.OriginalSha256
                ? null : session.BackupPath;
        }
    }

    private static void CheckIdentity(ExternalEditSnapshot session)
    {
        var active = ReadPending();
        if (active is null || active.Id != session.Id ||
            !active.OriginalPath.Equals(session.OriginalPath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Die externe Sitzung stimmt nicht mit dem gespeicherten Status überein.");
    }

    private static string UniqueCopyDestination(string source, string original)
    {
        var folder = Path.GetDirectoryName(original)!;
        var basename = Path.GetFileNameWithoutExtension(original);
        var extension = Path.GetExtension(source);
        for (var i = 1; i < 100000; i++)
        {
            var suffix = i == 1 ? "-Kopie" : "-Kopie-" + i;
            var destination = Path.Combine(folder, basename + suffix + extension);
            if (!File.Exists(destination)) return destination;
        }
        throw new IOException(UiLanguage.T("Kein freier Dateiname für die Kopie."));
    }

    private static void CopyAtomic(string source, string target,
        bool replace, CancellationToken token,
        Action<long, long>? progress = null)
    {
        // Stage on the TARGET volume, then rename. A crashed copy never
        // writes half an image directly over a photo (also over SMB).
        var temp = Path.Combine(Path.GetDirectoryName(target)!,
            ".pcasa-external-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = File.Open(source, FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(temp, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                var size = input.Length;
                var bytes = new byte[1024 * 1024];
                long copied = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var count = input.Read(bytes, 0, bytes.Length);
                    if (count == 0) break;
                    output.Write(bytes, 0, count);
                    copied += count;
                    if (copied == size || copied % (4 * 1024 * 1024) < count)
                        progress?.Invoke(copied, size);
                }
                output.Flush(true);
                if (output.Length != input.Length)
                    throw new IOException(UiLanguage.T("Kopie ist unvollständig."));
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: replace);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static string? Commit(ExternalEditSnapshot requested, string? result,
        ExternalEditDecision decision, CancellationToken token = default,
        Action<long, long>? progress = null, UndoOptions? undoOptions = null)
    {
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            CheckIdentity(requested);
            var session = ReadPending()!;
            if (session.Finalizing is { } choice && choice != decision)
                throw new InvalidOperationException(
                    "Die externe Bearbeitung wird bereits abgeschlossen.");
            if (decision == ExternalEditDecision.Cancel)
            {
                // Nothing has ever changed the source image. No restore and
                // no Undo entry are needed.
                var finalizing = session with { Finalizing = decision };
                WritePending(finalizing);
                Directory.Delete(DirectoryPath, recursive: true);
                return null;
            }

            var source = session.FinalTarget is null
                ? result : result ?? DetectResult(session);
            if (source is null || !File.Exists(source))
                throw new FileNotFoundException(UiLanguage.T(
                    "Das externe Programm hat kein Ergebnis gespeichert."), source);
            source = Path.GetFullPath(source);
            if (!Path.GetDirectoryName(source)!.Equals(
                    DirectoryPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "ExternalEdit: Ergebnis liegt nicht im Arbeitsverzeichnis.");
            if (RawImageSource.IsRawPath(source) ||
                (VideoFormats.IsVideo(session.OriginalPath)
                    ? !VideoFormats.IsVideo(source)
                    : !EditableImageFormats.Supports(source)))
                throw new NotSupportedException(UiLanguage.T(
                    "Dieses Format kann nicht bearbeitet werden. RAW und Videos bleiben unverändert."));
            if (new FileInfo(source).Length == 0)
                throw new InvalidDataException(UiLanguage.T("Das externe Ergebnis ist leer."));
            if (!VideoFormats.IsVideo(source))
                EditableImageFormats.ValidateStillImage(source);

            using (SourceFileLocks.Enter(session.OriginalPath, token))
            {
                if (decision == ExternalEditDecision.Save)
                {
                    if (!Path.GetExtension(source).Equals(
                            Path.GetExtension(session.OriginalPath),
                            StringComparison.OrdinalIgnoreCase))
                        throw new NotSupportedException(UiLanguage.T(
                            "Anderes Ausgabeformat: bitte 'Als Kopie speichern' wählen."));
                    var finalizing = session with
                    {
                        Finalizing = decision, FinalTarget = session.OriginalPath
                    };
                    if (session.Finalizing is null) WritePending(finalizing);
                    // A crash after the replace but before cleanup must NOT
                    // produce a second Undo copy of the edited file.
                    if (Hash(session.OriginalPath) != Hash(source))
                    {
                        if (OriginalChanged(session))
                            throw new IOException(
                                "Das Original wurde außerhalb von PCasa verändert.");
                        // Save uses exactly the central undo/recycle mechanism.
                        new UndoHistoryService(undoOptions ?? UndoOptions.Load())
                            .CaptureBeforeSave(session.OriginalPath, token);
                        CopyAtomic(source, session.OriginalPath, replace: true, token, progress);
                    }
                    if (Hash(session.OriginalPath) != Hash(source))
                        throw new IOException("ExternalEdit: Speichern nicht überprüfbar.");
                    Directory.Delete(DirectoryPath, recursive: true);
                    return session.OriginalPath;
                }

                var destination = session.FinalTarget ??
                    UniqueCopyDestination(source, session.OriginalPath);
                var copying = session with
                {
                    Finalizing = decision, FinalTarget = destination
                };
                if (session.Finalizing is null) WritePending(copying);
                if (!File.Exists(destination))
                    CopyAtomic(source, destination, replace: false, token, progress);
                else if (Hash(destination) != Hash(source))
                    throw new IOException("ExternalEdit: Kopie besitzt einen anderen Inhalt.");
                Directory.Delete(DirectoryPath, recursive: true);
                return destination;
            }
        }
    }
}
