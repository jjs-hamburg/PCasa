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
    private const string OwnedWorkspaceMarker = ".pcasa-owned-session";
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

    /// <summary>
    /// Abandon edits from a previous PCasa process. No session archive and
    /// no post-crash recovery: only the original file must remain safe.
    /// </summary>
    public static bool RetirePreviousProcessSession()
    {
        lock (Gate)
        {
            _currentProcessSessionId = null;
            var found = File.Exists(SessionFile);
            ExternalEditSnapshot? previous = null;
            if (found)
            {
                try { previous = ReadPending(); }
                catch (Exception ex) when (ex is InvalidDataException or JsonException
                    or IOException or ArgumentException)
                { PCasaDiagnostics.Write("ExternalEdit.StaleManifest", ex); }
                File.Delete(SessionFile);
            }
            if (previous is not null) CleanupWorkspaceContents(previous.DirectoryPath);
            CleanupCompletedWorkspaces();
            return found;
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
        // Accept legacy nested manifests solely for stale-file cleanup.
        // New sessions always use their configured root directly.
        var root = pending.ConfiguredWorkspaceRoot is { Length: > 0 } chosen
            ? Path.GetFullPath(chosen) : Path.GetFullPath(DirectoryPath);
        var flat = Path.GetFullPath(pending.DirectoryPath).Equals(root,
            StringComparison.OrdinalIgnoreCase);
        var isolated = Path.GetFullPath(
                Path.GetDirectoryName(pending.DirectoryPath) ?? string.Empty).Equals(
                root, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(pending.DirectoryPath).Equals(
                "session-" + pending.Id.ToString("N"), StringComparison.OrdinalIgnoreCase);
        if (pending.Id == Guid.Empty || !(flat || isolated) ||
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


    // A configured custom exchange directory may also contain unrelated
    // subdirectories; remove only top-level exports and PCasa-owned folders.
    // An unmarked custom directory with existing loose files is rejected
    // rather than silently deleting user data.
    private const string FlatExchangeMarker = ".pcasa-exchange-root";

    private static bool IsCustomRoot(string root)
        => !Path.GetFullPath(root).Equals(
            Path.GetFullPath(DirectoryPath), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> KnownCustomRoots()
        => ExternalEditorSettings.Load().Programs
            .Select(p => p.ExchangeDirectory)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(root => File.Exists(Path.Combine(root, FlatExchangeMarker)));

    private static void CleanupWorkspaceContents(string root)
    {
        if (!Directory.Exists(root)) return;
        var defaultRoot = !IsCustomRoot(root);
        foreach (var file in Directory.EnumerateFiles(root))
        {
            var name = Path.GetFileName(file);
            if (defaultRoot && name.Equals("session.json",
                    StringComparison.OrdinalIgnoreCase) && File.Exists(SessionFile))
                continue;
            if (name.Equals(FlatExchangeMarker, StringComparison.OrdinalIgnoreCase))
                continue;
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { PCasaDiagnostics.Write("ExternalEdit.CleanupFile", ex); }
        }
        var before = Path.Combine(root, ".pcasa-before");
        if (Directory.Exists(before)) TryCleanupFolder(before);
        foreach (var folder in Directory.EnumerateDirectories(root, "session-*"))
        {
            var name = Path.GetFileName(folder);
            if (!Guid.TryParseExact(name["session-".Length..], "N", out _))
                continue;
            if (defaultRoot || File.Exists(Path.Combine(folder, OwnedWorkspaceMarker)))
                TryCleanupFolder(folder);
        }
    }

    private static int RemainingFiles(string root)
    {
        if (!Directory.Exists(root)) return 0;
        var files = Directory.EnumerateFiles(root).Count(file =>
            Path.GetFileName(file) is not
                ("session.json" or FlatExchangeMarker));
        var before = Directory.Exists(Path.Combine(root, ".pcasa-before")) ? 1 : 0;
        var sessions = Directory.EnumerateDirectories(root, "session-*")
            .Count(dir => Guid.TryParseExact(
                Path.GetFileName(dir)["session-".Length..], "N", out _));
        return files + before + sessions;
    }

    public static int PendingCleanupCount()
    {
        lock (Gate)
        {
            var active = ReadPending();
            return new[] { DirectoryPath }.Concat(KnownCustomRoots())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(root => active is null || !root.Equals(
                    active.DirectoryPath, StringComparison.OrdinalIgnoreCase))
                .Sum(RemainingFiles);
        }
    }

    public static int CleanupCompletedWorkspaces()
    {
        lock (Gate)
        {
            var active = ReadPending();
            foreach (var root in new[] { DirectoryPath }.Concat(KnownCustomRoots())
                .Distinct(StringComparer.OrdinalIgnoreCase))
                if (active is null || !root.Equals(
                    active.DirectoryPath, StringComparison.OrdinalIgnoreCase))
                    CleanupWorkspaceContents(root);
            return PendingCleanupCount();
        }
    }

    private static void TryCleanupFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PCasaDiagnostics.Write("ExternalEdit.CleanupPending", ex); }
    }

    private static void Finish(ExternalEditSnapshot session)
    {
        File.Delete(SessionFile);
        if (_currentProcessSessionId == session.Id) _currentProcessSessionId = null;
        CleanupWorkspaceContents(session.DirectoryPath);
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
            Directory.CreateDirectory(DirectoryPath);
            var root = string.IsNullOrWhiteSpace(workspaceRoot)
                ? DirectoryPath : Path.GetFullPath(workspaceRoot);
            if (root.Length == 0 || Path.GetPathRoot(root) == root)
                throw new ArgumentException("Ein Laufwerksstamm ist kein Austauschverzeichnis.");
            if (path.StartsWith(Path.GetFullPath(root).TrimEnd(
                    Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Das Original liegt im Austauschverzeichnis.");
            Directory.CreateDirectory(root);
            var marker = Path.Combine(root, FlatExchangeMarker);
            if (IsCustomRoot(root) && !File.Exists(marker) &&
                Directory.EnumerateFiles(root).Any())
                throw new IOException(
                    "Das Austauschverzeichnis enthält fremde Dateien; bitte einen eigenen Ordner wählen.");
            // Before every launch purge all old outputs; locked stale files
            // prevent reuse of photo.jpg rather than corrupting an edit.
            CleanupWorkspaceContents(root);
            if (RemainingFiles(root) != 0)
                throw new InvalidOperationException(
                    "Alte Austauschdateien sind gesperrt. Bitte das externe Programm schließen.");
            if (IsCustomRoot(root)) File.WriteAllText(marker, "PCasa external exchange");
            var id = Guid.NewGuid();
            var inputName = (VideoFormats.IsVideo(path) ? "video" : "photo") +
                Path.GetExtension(path).ToLowerInvariant();
            var work = Path.Combine(root, inputName);
            var beforePath = Path.Combine(root, ".pcasa-before", inputName);
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
                    // The video editor has its own review window and no
                    // still-image before/after control. Do NOT duplicate huge
                    // multi-gigabyte video files for a comparison they cannot
                    // display.
                    if (!VideoFormats.IsVideo(work))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(beforePath)!);
                        File.Copy(work, beforePath);
                        if (new FileInfo(beforePath).Length != originalLength ||
                            !Hash(beforePath).Equals(checksum, StringComparison.Ordinal))
                            throw new IOException(
                                "Die Vergleichssicherung stimmt nicht mit der Übergabedatei überein.");
                    }
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
                    CleanupWorkspaceContents(root);
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
