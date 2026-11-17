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
    // The editor may replace BackupPath in place. This nested local copy
    // never goes to the editor or the result enumerator, and is the exact
    // input state that Before/After must display.
    public string BeforePath => Path.Combine(
        DirectoryPath, ".pcasa-before", Path.GetFileName(BackupPath));
    public string? ConfiguredWorkspaceRoot { get; init; }
}

public static class ExternalEditTransaction
{
    private static readonly object Gate = new();
    private static string? _testDirectory;
    private static Guid? _currentProcessSessionId;
    private const string RetiredWorkspaceMarker = ".pcasa-retired-session";
    private const string OwnedWorkspaceMarker = ".pcasa-owned-session";
    private const string RetiredLegacyMarker = ".pcasa-retired-legacy";
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

    // History is evidence and possibly an external application's only copy
    // of an export. It is deliberately NOT a runnable task, manifest or queue.
    private static string RetiredHistoryRoot => Path.Combine(
        Path.GetDirectoryName(DirectoryPath)!,
        "ExternalEditRetired");

    /// <summary>
    /// At application startup, retire the previous *process*'s external edit.
    /// Only scan jobs survive a restart. This never commits/cancels a file
    /// mutation, writes the original or deletes external work products.
    /// Keep the original manifest as inert evidence outside the active root.
    /// </summary>
    public static bool RetirePreviousProcessSession()
    {
        lock (Gate)
        {
            _currentProcessSessionId = null;
            if (!File.Exists(SessionFile)) return false;

            // Preserve the raw manifest even if a previous crash truncated it.
            // Unreadable manifests must never indefinitely pause the scheduler.
            ExternalEditSnapshot? previous = null;
            try { previous = ReadPending(); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException
                or IOException or ArgumentException)
            {
                PCasaDiagnostics.Write("ExternalEdit.RetireInvalidManifest", ex);
            }

            var manifestContent = File.ReadAllText(SessionFile);
            if (previous is not null)
            {
                // An old external process might still have photo.psd open.
                // Do not move/delete that workspace. Mark it exempt from
                // automatic P4 cleanup and leave the content retrievable.
                Directory.CreateDirectory(previous.DirectoryPath);
                var marker = previous.DirectoryPath.Equals(DirectoryPath,
                    StringComparison.OrdinalIgnoreCase)
                    ? RetiredLegacyMarker : RetiredWorkspaceMarker;
                File.WriteAllText(Path.Combine(previous.DirectoryPath, marker),
                    previous.Id.ToString("N"));
            }
            else
            {
                // A corrupt old manifest does not identify its workdir.
                // Conservatively preserve every possible workdir and old
                // flat-workspace file, rather than risking their deletion.
                foreach (var folder in Directory.EnumerateDirectories(
                    DirectoryPath, "session-*"))
                    File.WriteAllText(Path.Combine(folder, RetiredWorkspaceMarker),
                        "invalid prior manifest");
                File.WriteAllText(Path.Combine(DirectoryPath, RetiredLegacyMarker),
                    "invalid prior manifest");
            }

            Directory.CreateDirectory(RetiredHistoryRoot);
            var archive = Path.Combine(RetiredHistoryRoot,
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" +
                (previous?.Id.ToString("N") ?? Guid.NewGuid().ToString("N")) +
                ".json");
            // Same-volume rename detaches the ONLY active session manifest.
            // A crash before this boundary is safe to retry on next startup.
            File.Move(SessionFile, archive);
            PCasaDiagnostics.WriteMessage("ExternalEdit.RetiredAfterRestart",
                "Previous-process session retired without file writes. " +
                "Manifest: " + archive);
            return true;
        }
    }

    /// <summary>
    /// The Taskmanager must show only an edit created in THIS process.
    /// A historic session.json on disk is never proof of a live task.
    /// </summary>
    public static ExternalEditSnapshot? FindCurrentProcessSession()
    {
        lock (Gate)
        {
            if (_currentProcessSessionId is not Guid id) return null;
            var pending = ReadPending();
            return pending?.Id == id ? pending : null;
        }
    }

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
        // Alpha.33 stored the work image directly in ExternalEdit. Continue
        // accepting that manifest so upgrades never discard a recoverable edit.
        // New sessions have their own directory; a locked old photo must not
        // prevent the next independent session from starting.
        var legacy = pending.ConfiguredWorkspaceRoot is null &&
            Path.GetFullPath(pending.DirectoryPath).Equals(
                Path.GetFullPath(DirectoryPath), StringComparison.OrdinalIgnoreCase);
        var sessionRoot = pending.ConfiguredWorkspaceRoot is { Length: > 0 } chosen
            ? Path.GetFullPath(chosen) : Path.GetFullPath(DirectoryPath);
        var isolated = Path.GetFullPath(
                Path.GetDirectoryName(pending.DirectoryPath) ?? string.Empty).Equals(
                sessionRoot, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(pending.DirectoryPath).Equals(
                "session-" + pending.Id.ToString("N"), StringComparison.OrdinalIgnoreCase);
        if (pending.Id == Guid.Empty || !(legacy || isolated) ||
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


    // Files from a committed session can remain open in Topaz/PhotoCraft.
    // They are garbage to collect, not an active editing transaction. Never
    // delete an active manifest or reuse an old editor's fixed filename.
    private static IEnumerable<string> CustomOwnedWorkspaceDirectories()
    {
        // Only folders carrying PCasa's own marker may ever be recursively
        // deleted from a USER-CHOSEN exchange root. Other user files,
        // including arbitrarily named session-* directories, are untouchable.
        foreach (var root in ExternalEditorSettings.Load().Programs
            .Select(p => p.ExchangeDirectory)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (root is null || !Directory.Exists(root)) continue;
            string[] candidates;
            try { candidates = Directory.GetDirectories(root, "session-*"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PCasaDiagnostics.Write("ExternalEdit.CustomCleanupList", ex);
                continue;
            }
            foreach (var candidate in candidates)
                if (File.Exists(Path.Combine(candidate, OwnedWorkspaceMarker)))
                    yield return candidate;
        }
    }

    public static int PendingCleanupCount()
    {
        lock (Gate)
        {
            if (!Directory.Exists(DirectoryPath)) return 0;
            var active = ReadPending();
            var directories = Directory.EnumerateDirectories(DirectoryPath, "session-*")
                .Count(dir => !dir.Equals(active?.DirectoryPath,
                    StringComparison.OrdinalIgnoreCase));
            var legacy = active is not null &&
                active.DirectoryPath.Equals(DirectoryPath, StringComparison.OrdinalIgnoreCase);
            var unprotectedDirectories = Directory.EnumerateDirectories(
                DirectoryPath, "session-*")
                .Count(dir => !dir.Equals(active?.DirectoryPath,
                    StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(Path.Combine(dir, RetiredWorkspaceMarker)));
            // A retired legacy workspace is kept as evidence only; no
            // task/cleanup row should be created for its data.
            var hasLegacyArchive = File.Exists(
                Path.Combine(DirectoryPath, RetiredLegacyMarker));
            var customPending = CustomOwnedWorkspaceDirectories().Count(dir =>
                !dir.Equals(active?.DirectoryPath,
                    StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(Path.Combine(dir, RetiredWorkspaceMarker)));
            return unprotectedDirectories + customPending +
                (legacy || hasLegacyArchive ? 0 :
                    Directory.EnumerateFiles(DirectoryPath).Count(file =>
                        Path.GetFileName(file) is not
                            ("session.json" or "session.json.tmp")));
        }
    }

    public static int CleanupCompletedWorkspaces()
    {
        lock (Gate)
        {
            if (!Directory.Exists(DirectoryPath)) return 0;
            var active = ReadPending();
            foreach (var custom in CustomOwnedWorkspaceDirectories())
            {
                if (custom.Equals(active?.DirectoryPath,
                        StringComparison.OrdinalIgnoreCase) ||
                    File.Exists(Path.Combine(custom, RetiredWorkspaceMarker)))
                    continue;
                TryCleanupFolder(custom);
            }
            foreach (var folder in Directory.EnumerateDirectories(DirectoryPath, "session-*"))
            {
                if (folder.Equals(active?.DirectoryPath, StringComparison.OrdinalIgnoreCase) ||
                    File.Exists(Path.Combine(folder, RetiredWorkspaceMarker)))
                    continue;
                TryCleanupFolder(folder);
            }
            // Also clean up pre-Alpha.34 flat working images once no legacy
            // transaction owns them. A locked image stays for the next attempt.
            if ((active is null || !active.DirectoryPath.Equals(
                    DirectoryPath, StringComparison.OrdinalIgnoreCase)) &&
                !File.Exists(Path.Combine(DirectoryPath, RetiredLegacyMarker)))
                foreach (var file in Directory.EnumerateFiles(DirectoryPath))
                {
                    if (Path.GetFileName(file) is "session.json" or "session.json.tmp")
                        continue;
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { PCasaDiagnostics.Write("ExternalEdit.CleanupPending", ex); }
                }
            return PendingCleanupCount();
        }
    }

    private static void TryCleanupFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The export/launcher may still hold photo.jpg. This must never
            // turn an already committed Save or Cancel into a failed action.
            PCasaDiagnostics.Write("ExternalEdit.CleanupPending", ex);
        }
    }

    private static void Finish(ExternalEditSnapshot session)
    {
        // The durable decision is committed BEFORE any best-effort cleanup.
        // If a crash follows this deletion, abandoned files remain isolated
        // and never masquerade as a live transaction.
        File.Delete(SessionFile);
        if (_currentProcessSessionId == session.Id) _currentProcessSessionId = null;
        if (!session.DirectoryPath.Equals(DirectoryPath, StringComparison.OrdinalIgnoreCase))
            TryCleanupFolder(session.DirectoryPath);
        else
            CleanupCompletedWorkspaces();
    }

    public static ExternalEditSnapshot Begin(string original, CancellationToken token = default,
        Action<long, long>? progress = null, string? workspaceRoot = null)
    {
        var path = Path.GetFullPath(original);
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(SessionFile))
                throw new InvalidOperationException(
                    UiLanguage.T("Bitte die laufende externe Bearbeitung zuerst abschließen."));
            // Never spend a manual P0 editor launch on old large exports.
            // Cleanup is a separate P4 scheduler job and each session has
            // its own unique work directory, so no collision is possible.
            Directory.CreateDirectory(DirectoryPath);
            var root = string.IsNullOrWhiteSpace(workspaceRoot)
                ? DirectoryPath : Path.GetFullPath(workspaceRoot);
            if (root.Length == 0 || Path.GetPathRoot(root) == root)
                throw new ArgumentException("Ein Laufwerksstamm ist kein Austauschverzeichnis.");
            Directory.CreateDirectory(root);
            var id = Guid.NewGuid();
            var sessionDirectory = Path.Combine(root, "session-" + id.ToString("N"));
            Directory.CreateDirectory(sessionDirectory);
            File.WriteAllText(Path.Combine(sessionDirectory, OwnedWorkspaceMarker),
                id.ToString("N"));
            var inputName = (VideoFormats.IsVideo(path) ? "video" : "photo") +
                Path.GetExtension(path).ToLowerInvariant();
            var work = Path.Combine(sessionDirectory, inputName);
            var beforePath = Path.Combine(sessionDirectory,
                ".pcasa-before", inputName);
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
                    // Preserve the exact handed-off bytes before the external
                    // editor can overwrite photo.jpg IN PLACE. Keep this
                    // immutable reference below a nested folder, never among
                    // top-level export candidates. The NAS original is read
                    // only once; this duplicate is entirely local.
                    Directory.CreateDirectory(Path.GetDirectoryName(beforePath)!);
                    File.Copy(work, beforePath);
                    if (new FileInfo(beforePath).Length != originalLength ||
                        !Hash(beforePath).Equals(checksum, StringComparison.Ordinal))
                        throw new IOException(
                            "Die Vergleichssicherung stimmt nicht mit der Übergabedatei überein.");
                    // A broken source is rejected before launching an editor.
                    if (!VideoFormats.IsVideo(work) &&
                        !RawImageSource.IsRawPath(work))
                        EditableImageFormats.ValidateStillImage(work);
                    // RAW is staged byte-for-byte; no pixel converter may
                    // process or rewrite the original or working input.
                    var session = new ExternalEditSnapshot(id, path,
                        work, checksum, originalLength, originalWrite,
                        DateTime.UtcNow, [])
                    {
                        ConfiguredWorkspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
                            ? null : root
                    };
                    WritePending(session);
                    _currentProcessSessionId = session.Id;
                    return session;
                }
            }
            catch
            {
                // A published manifest means recovery must be possible.
                if (!File.Exists(SessionFile))
                    TryCleanupFolder(sessionDirectory);
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
            var candidates = Directory.EnumerateFiles(session.DirectoryPath)
                .Where(path => !path.Equals(SessionFile,
                    StringComparison.OrdinalIgnoreCase) &&
                    !path.Equals(session.BackupPath,
                        StringComparison.OrdinalIgnoreCase) &&
                    (VideoFormats.IsVideo(session.OriginalPath)
                        ? VideoFormats.IsVideo(path)
                        : EditableImageFormats.SupportsExternalResult(path) ||
                          RawImageSource.IsRawPath(path)))
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

    // Never trust a RAW filename alone. ExifTool reads the container bytes,
    // not the suffix; this rejects a JPEG renamed to .ARW before an atomic
    // replacement could destroy the original camera negative.
    internal static void ValidateExternalRawResult(string path,
        CancellationToken token)
    {
        using var reader = new ImageExifReader();
        var exif = reader.Read(path, token);
        using var json = JsonDocument.Parse(exif.Json);
        var entry = json.RootElement;
        if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() == 1)
            entry = entry[0];
        if (entry.ValueKind != JsonValueKind.Object ||
            !(entry.TryGetProperty("File:FileType", out var type) ||
              entry.TryGetProperty("FileType", out type)) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() is not { } actual ||
            !SupportedRawFileTypes.Contains(actual))
            throw new InvalidDataException(
                "Das externe RAW-Ergebnis ist kein erkanntes RAW-Format.");
    }

    private static readonly HashSet<string> SupportedRawFileTypes =
        new(StringComparer.OrdinalIgnoreCase)
        { "ARW", "NEF", "NRW", "CR2", "CR3", "DNG",
          "RAF", "RW2", "ORF", "PEF", "SRW", "CRW" };

    // Same container/extension may be deliberately saved into the original
    // (with a central Undo snapshot). A format change is ALWAYS a new image.
    // In particular an exported JPEG/PSD/TIFF cannot overwrite a RAW or
    // replace a layered PSD under its old extension.
    public static bool RequiresNewFile(string original, string result)
        => !Path.GetExtension(original).Equals(Path.GetExtension(result),
            StringComparison.OrdinalIgnoreCase);

    private static string UniqueCopyDestination(string source, string original)
    {
        var desired = Path.Combine(Path.GetDirectoryName(original)!,
            Path.GetFileNameWithoutExtension(original) + Path.GetExtension(source));
        return ExternalCopyNames.NextAvailable(desired);
    }

    private static void CopyAtomic(string source, string target,
        bool replace, CancellationToken token,
        Action<long, long>? progress = null,
        DateTime? creationUtc = null, DateTime? modifiedUtc = null)
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
            // Apply dates to the staged file BEFORE the atomic publication.
            // The result's editing timestamp was captured before ExifTool
            // touched its metadata, and Windows creation follows the source.
            if (creationUtc is DateTime created)
                File.SetCreationTimeUtc(temp, created);
            if (modifiedUtc is DateTime modified)
                File.SetLastWriteTimeUtc(temp, modified);
            try { File.Move(temp, target, overwrite: replace); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLockDiagnostics.ReportFailure(replace
                    ? "ExternalEdit.Replace" : "ExternalEdit.SaveCopy",
                    ex, source, target, temp);
                throw;
            }
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
            if (session.Finalizing is { } choice && choice != decision &&
                !(choice == ExternalEditDecision.SaveCopy &&
                  decision == ExternalEditDecision.Save))
                throw new InvalidOperationException(
                    "Die externe Bearbeitung wird bereits abgeschlossen.");
            if (decision == ExternalEditDecision.Cancel)
            {
                // Nothing has ever changed the source image. No restore and
                // no Undo entry are needed.
                var finalizing = session with { Finalizing = decision };
                WritePending(finalizing);
                Finish(finalizing);
                return null;
            }

            var source = session.FinalTarget is null
                ? result : result ?? DetectResult(session);
            if (source is null || !File.Exists(source))
                throw new FileNotFoundException(UiLanguage.T(
                    "Das externe Programm hat kein Ergebnis gespeichert."), source);
            source = Path.GetFullPath(source);
            var sourceModifiedUtc = File.GetLastWriteTimeUtc(source);
            var originalCreatedUtc = File.GetCreationTimeUtc(session.OriginalPath);
            // A layered PSD exported from a JPEG is always a NEW photo. Never
            // replace the JPEG, even if the user clicked ordinary Save.
            if (decision == ExternalEditDecision.Save &&
                RequiresNewFile(session.OriginalPath, source))
                throw new NotSupportedException(UiLanguage.T(
                    "Anderes Ausgabeformat: bitte 'Als Kopie speichern' wählen."));
            if (!Path.GetDirectoryName(source)!.Equals(
                    session.DirectoryPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "ExternalEdit: Ergebnis liegt nicht im Arbeitsverzeichnis.");
            if (VideoFormats.IsVideo(session.OriginalPath)
                    ? !VideoFormats.IsVideo(source)
                    : !EditableImageFormats.SupportsExternalResult(source) &&
                      !(RawImageSource.IsRawPath(session.OriginalPath) &&
                        RawImageSource.IsRawPath(source)))
                throw new NotSupportedException(UiLanguage.T(
                    "Dieses Format kann nicht bearbeitet werden. RAW und Videos bleiben unverändert."));
            if (new FileInfo(source).Length == 0)
                throw new InvalidDataException(UiLanguage.T("Das externe Ergebnis ist leer."));
            if (RawImageSource.IsRawPath(source))
            {
                // A native RAW may be replaced ONLY by an externally
                // produced, readable RAW; never accept a renamed JPG.
                ValidateExternalRawResult(source, token);
            }
            else if (!VideoFormats.IsVideo(source))
                EditableImageFormats.ValidateStillImage(source);

            using (SourceFileLocks.Enter(session.OriginalPath, token))
            {
                if (decision == ExternalEditDecision.Save)
                {
                    if (RequiresNewFile(session.OriginalPath, source))
                        throw new NotSupportedException(UiLanguage.T(
                            "Anderes Ausgabeformat: bitte 'Als Kopie speichern' wählen."));
                    var finalizing = session with
                    {
                        Finalizing = decision, FinalTarget = session.OriginalPath
                    };
                    if (session.Finalizing is null) WritePending(finalizing);
                    // An external PSD editor may drop camera EXIF on re-save.
                    // Restore capture metadata on its staged result before
                    // changing the original layered file. RAW bytes are never
                    // touched by PCasa's metadata writer here.
                    if (RawImageSource.IsPsdPath(source) &&
                        session.Finalizing is null)
                        ExternalResultMetadata.CopyAll(session.OriginalPath, source, token);
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
                        CopyAtomic(source, session.OriginalPath, replace: true, token,
                            progress, originalCreatedUtc, sourceModifiedUtc);
                    }
                    if (Hash(session.OriginalPath) != Hash(source))
                        throw new IOException("ExternalEdit: Speichern nicht überprüfbar.");
                    Finish(session);
                    return session.OriginalPath;
                }

                // Transfer the source capture date, camera settings, XMP,
                // IPTC and GPS to the layered PSD BEFORE its atomic copy.
                // This never re-encodes the Photoshop pixels or changes the JPEG.
                if ((RawImageSource.IsPsdPath(source) ||
                     (RawImageSource.IsRawPath(session.OriginalPath) &&
                      !RawImageSource.IsRawPath(source))) &&
                    session.Finalizing is null)
                    ExternalResultMetadata.CopyAll(session.OriginalPath, source, token);
                var destination = session.FinalTarget ??
                    UniqueCopyDestination(source, session.OriginalPath);
                var copying = session with
                {
                    Finalizing = decision, FinalTarget = destination
                };
                if (session.Finalizing is null) WritePending(copying);
                if (!File.Exists(destination))
                    CopyAtomic(source, destination, replace: false, token,
                        progress, originalCreatedUtc, sourceModifiedUtc);
                else if (Hash(destination) != Hash(source))
                    throw new IOException("ExternalEdit: Kopie besitzt einen anderen Inhalt.");
                Finish(session);
                return destination;
            }
        }
    }
}
