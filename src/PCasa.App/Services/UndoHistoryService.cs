using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCasa.App.Services;

public sealed class UndoOptions
{
    private static readonly string SettingsPath = Path.Combine(
        PcasaDataPaths.SharedDataRoot, "undo-options.json");

    public string DirectoryPath { get; set; } =
        Path.Combine(PcasaDataPaths.SharedDataRoot, "Undo");
    public int MaximumVersionsPerImage { get; set; } = 5;
    public long MaximumBytes { get; set; } = 50L * 1024 * 1024 * 1024;
    public int? MaximumAgeDays { get; set; }

    public static UndoOptions Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var value = JsonSerializer.Deserialize<UndoOptions>(
                    File.ReadAllText(SettingsPath));
                if (value is not null && value.IsValid()) return value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException) { }
        return new UndoOptions();
    }

    public bool IsValid() => DirectoryPath.Length > 0 &&
        Path.IsPathFullyQualified(DirectoryPath) &&
        MaximumVersionsPerImage is >= 1 and <= 100 &&
        MaximumBytes is >= 1048576 and <= 100000000000000 &&
        (MaximumAgeDays is null || MaximumAgeDays is >= 1 and <= 36500);

    public void Save()
    {
        if (!IsValid()) throw new ArgumentException(UiLanguage.T("Ungültige Undo-Einstellungen."));
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, SettingsPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed record UndoVersion(Guid Id, string SourcePath, string SnapshotPath,
    DateTimeOffset SavedAtUtc, long ByteCount, string Sha256);

// Single central archive. No sidecars and no hidden folders beside originals.
// A version is a complete pre-save file state, never an individual edit step.
public sealed class UndoHistoryService
{
    private static readonly object Gate = new();
    private readonly UndoOptions _options;
    public UndoHistoryService(UndoOptions options)
    {
        if (!options.IsValid()) throw new ArgumentException(UiLanguage.T("Ungültige Undo-Einstellungen."));
        _options = options;
    }

    private static bool IsEditableImage(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";

    private string ArchiveRoot => Path.Combine(_options.DirectoryPath, "Versions");

    public IReadOnlyList<UndoVersion> GetVersions(string sourcePath)
    {
        lock (Gate)
            return EnumerateVersions().Where(v => string.Equals(v.SourcePath,
                    Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v.SavedAtUtc).ToArray();
    }

    private static bool IsVerifiedVersion(UndoVersion version, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using var data = File.OpenRead(version.SnapshotPath);
            if (data.Length != version.ByteCount) return false;
            var hash = Convert.ToHexString(SHA256.HashData(data));
            return string.Equals(hash, version.Sha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return false; }
    }

    // Identical availability policy for Edit, context menus and gallery icons.
    // Only the local Undo archive is read; source images, including SMB/NAS,
    // are never opened for an availability check.
    public bool HasRestorableVersion(string sourcePath)
    {
        return GetVersions(sourcePath).Any(version =>
            IsVerifiedVersion(version, CancellationToken.None));
    }

    public IReadOnlySet<string> GetVerifiedRestorablePaths(CancellationToken token)
    {
        lock (Gate)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var version in EnumerateVersions())
            {
                token.ThrowIfCancellationRequested();
                if (!result.Contains(version.SourcePath) &&
                    IsVerifiedVersion(version, token))
                    result.Add(version.SourcePath);
            }
            return result;
        }
    }

    private List<UndoVersion> EnumerateVersions()
    {
        if (!Directory.Exists(ArchiveRoot)) return [];
        var results = new List<UndoVersion>();
        foreach (var metadata in Directory.EnumerateFiles(ArchiveRoot,
                     "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var version = JsonSerializer.Deserialize<UndoVersion>(
                    File.ReadAllText(metadata));
                if (version is not null &&
                    Path.GetDirectoryName(Path.GetFullPath(version.SnapshotPath)) ==
                        Path.GetFullPath(ArchiveRoot) &&
                    File.Exists(version.SnapshotPath))
                    results.Add(version);
            }
            catch (Exception ex) when (ex is IOException or JsonException
                or UnauthorizedAccessException or ArgumentException) { }
        }
        return results;
    }

    // Must run with SourceFileLocks held by the writer BEFORE any image mutation.
    // Failure is fatal to the edit: never modify an image without a verified backup.
    public UndoVersion CaptureBeforeSave(string originalPath, CancellationToken token)
    {
        if (!IsEditableImage(originalPath))
            throw new NotSupportedException(UiLanguage.T("RAW und andere Nicht-JPEG-Dateien können nicht bearbeitet werden."));
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(originalPath);
            var size = new FileInfo(path).Length;
            if (size > _options.MaximumBytes)
                throw new IOException(UiLanguage.T("Die Undo-Kapazität ist kleiner als das Originalfoto."));
            Directory.CreateDirectory(ArchiveRoot);
            var id = Guid.NewGuid();
            var data = Path.Combine(ArchiveRoot, id.ToString("N") + ".bin");
            var temp = data + ".tmp";
            var meta = Path.Combine(ArchiveRoot, id.ToString("N") + ".json");
            try
            {
                string hash;
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.Read, 131072, FileOptions.SequentialScan))
                using (var output = new FileStream(temp, FileMode.CreateNew,
                           FileAccess.Write, FileShare.None, 131072, FileOptions.WriteThrough))
                using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[131072];
                    long copied = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var count = input.Read(buffer);
                        if (count == 0) break;
                        hasher.AppendData(buffer, 0, count);
                        output.Write(buffer, 0, count);
                        copied += count;
                    }
                    output.Flush(flushToDisk: true);
                    if (copied != size)
                        throw new IOException(UiLanguage.T("Das Original wurde während der Sicherung verändert."));
                    hash = Convert.ToHexString(hasher.GetHashAndReset());
                }
                File.Move(temp,data);
                // A fresh read from local backup verifies real stored bytes.
                using var verified = File.OpenRead(data);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(verified)),
                        hash,StringComparison.OrdinalIgnoreCase))
                    throw new IOException(UiLanguage.T("Die Undo-Sicherung hat eine ungültige Prüfsumme."));
                var version = new UndoVersion(id,path,data,DateTimeOffset.UtcNow,size,hash);
                File.WriteAllText(meta,JsonSerializer.Serialize(version));
                Prune(protectedId:id);
                return version;
            }
            catch
            {
                if (File.Exists(temp)) File.Delete(temp);
                if (File.Exists(data) && !File.Exists(meta)) File.Delete(data);
                throw;
            }
        }
    }

    private void Prune(Guid protectedId)
    {
        var versions = EnumerateVersions()
            .OrderBy(v => v.SavedAtUtc).ToList();
        var keep = new HashSet<Guid>();
        var cutoff = _options.MaximumAgeDays is int age
            ? DateTimeOffset.UtcNow.AddDays(-age) : DateTimeOffset.MinValue;
        foreach (var group in versions.GroupBy(v => v.SourcePath,
                     StringComparer.OrdinalIgnoreCase))
            foreach (var value in group.OrderByDescending(v => v.SavedAtUtc)
                         .Take(_options.MaximumVersionsPerImage))
                if (value.SavedAtUtc >= cutoff || value.Id == protectedId)
                    keep.Add(value.Id);
        var total = versions.Where(v => keep.Contains(v.Id)).Sum(v => v.ByteCount);
        foreach (var v in versions.Where(v => keep.Contains(v.Id) && v.Id != protectedId))
        {
            if (total <= _options.MaximumBytes) break;
            keep.Remove(v.Id);
            total -= v.ByteCount;
        }
        if (total > _options.MaximumBytes)
            throw new IOException(UiLanguage.T("Der Undo-Speicher ist voll."));
        foreach (var version in versions.Where(v => !keep.Contains(v.Id)))
        {
            File.Delete(Path.Combine(ArchiveRoot, version.Id.ToString("N")+".json"));
            File.Delete(version.SnapshotPath);
        }
    }

    public void Restore(string originalPath, Guid versionId, CancellationToken token)
    {
        var path = Path.GetFullPath(originalPath);
        if (!IsEditableImage(path))
            throw new NotSupportedException(UiLanguage.T("RAW kann nicht als bearbeitetes Bild wiederhergestellt werden."));
        using (SourceFileLocks.Enter(path,token))
        {
            var version = GetVersions(path).FirstOrDefault(v => v.Id == versionId)
                ?? throw new FileNotFoundException(UiLanguage.T("Die Undo-Version ist nicht mehr vorhanden."));
            using (var data = File.OpenRead(version.SnapshotPath))
                if (Convert.ToHexString(SHA256.HashData(data)) != version.Sha256)
                    throw new InvalidDataException(UiLanguage.T("Die Undo-Sicherung hat eine ungültige Prüfsumme."));
            // Restoration itself becomes undoable.
            CaptureBeforeSave(path,token);
            var temp = Path.Combine(Path.GetDirectoryName(path)!,
                ".pcasa-undo-"+Guid.NewGuid().ToString("N")+".jpg");
            try
            {
                File.Copy(version.SnapshotPath,temp);
                PreserveCurrentGpsAndTags(path,temp,token);
                token.ThrowIfCancellationRequested();
                File.Move(temp,path,true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    private static void PreserveCurrentGpsAndTags(
        string source, string restoredCopy, CancellationToken token)
    {
        var exifTool = Environment.GetEnvironmentVariable("PCASA_EXIFTOOL_PATH");
        if (string.IsNullOrWhiteSpace(exifTool))
            exifTool = Path.Combine(AppContext.BaseDirectory,"exiftool","exiftool.exe");
        if (!File.Exists(exifTool))
            throw new FileNotFoundException(UiLanguage.T("Zum Erhalten der Metadaten wird ExifTool benötigt."),
                exifTool);
        var start = new System.Diagnostics.ProcessStartInfo(exifTool)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Clear archived GPS/keywords before copying the CURRENT values.
        foreach (var arg in new[] { "-overwrite_original", "-P",
                     "-GPS:all=", "-IPTC:Keywords=", "-XMP:Subject=",
                     "-XMP:HierarchicalSubject=", "-TagsFromFile", source,
                     "-GPS:all", "-IPTC:Keywords", "-XMP:Subject",
                     "-XMP:HierarchicalSubject", restoredCopy })
            start.ArgumentList.Add(arg);
        using var child = ExifToolProcessRegistry.Start(start);
        try
        {
            using var cancelled = token.Register(() =>
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree:true); }
                catch (InvalidOperationException) { }
            });
            if (!child.WaitForExit(120000) || child.ExitCode != 0)
                throw new IOException(UiLanguage.T("ExifTool konnte GPS und Tags nicht erhalten."));
        }
        finally { ExifToolProcessRegistry.Retire(child); }
    }
}
