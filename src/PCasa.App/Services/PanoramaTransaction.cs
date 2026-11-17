using System.IO;
using System.Security.Cryptography;

namespace PCasa.App.Services;

/// <summary>
/// A panorama uses immutable copies of multiple source images. Unlike an
/// in-place editor there is no source replacement, undo or resume/replay.
/// The external application owns only the local session folder.
/// </summary>
public sealed record PanoramaSession(
    Guid Id, long[] ImageIds, string[] Sources, string[] Inputs,
    string Directory, string[] Tags, double? Latitude, double? Longitude,
    bool IsPanorama = true, string[]? InputHashes = null);

// SourceIndex is always explicit: never associate the Nth result with the
// Nth original by filesystem enumeration order.
public sealed record ExternalBatchOutput(int SourceIndex, string Path);

public static class PanoramaTransaction
{
    public static string SessionRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "Panorama");

    public static PanoramaSession Begin(
        (long Id, string Path)[] images, string[] tags,
        double? latitude, double? longitude,
        CancellationToken token = default, Action<long, long>? progress = null,
        bool isPanorama = true)
    {
        if (images.Length < 2) throw new ArgumentException(
            "Für ein Panorama sind mindestens zwei Bilder erforderlich.");
        if (images.Any(image => VideoFormats.IsVideo(image.Path)))
            throw new NotSupportedException("Videos können keine Panoramabilder bilden.");
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        var directory = Path.Combine(SessionRoot, "session-" + id.ToString("N"));
        Directory.CreateDirectory(directory);
        var staged = new List<string>();
        var stagedHashes = new List<string>();
        try
        {
            var total = images.Sum(image => new FileInfo(image.Path).Length);
            long copied = 0;
            foreach (var (index, image) in images.Select((value, i) => (i, value)))
            {
                token.ThrowIfCancellationRequested();
                var original = Path.GetFullPath(image.Path);
                var input = Path.Combine(directory,
                    (index + 1).ToString("D4") + "-" + Path.GetFileName(original));
                using (SourceFileLocks.Enter(original, token))
                using (var source = new FileStream(original, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 1024 * 1024))
                using (var destination = new FileStream(input, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough))
                using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = source.Read(buffer)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        destination.Write(buffer, 0, read);
                        hasher.AppendData(buffer, 0, read);
                        copied += read;
                        progress?.Invoke(copied, total);
                    }
                    destination.Flush(true);
                    stagedHashes.Add(Convert.ToHexString(hasher.GetHashAndReset()));
                }
                staged.Add(input);
            }
            return new PanoramaSession(id, images.Select(x => x.Id).ToArray(),
                images.Select(x => Path.GetFullPath(x.Path)).ToArray(),
                staged.ToArray(), directory, tags, latitude, longitude,
                isPanorama, stagedHashes.ToArray());
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static string? FindResult(PanoramaSession session)
    {
        if (!Directory.Exists(session.Directory)) return null;
        var inputs = session.Inputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = Directory.EnumerateFiles(session.Directory)
            .Where(path => !inputs.Contains(path) &&
                EditableImageFormats.SupportsExternalResult(path) &&
                new FileInfo(path).Length > 0)
            .ToArray();
        if (results.Length > 1)
            throw new InvalidDataException(
                "Mehrere Panoramaergebnisse gefunden; keine automatische Zuordnung möglich.");
        return results.FirstOrDefault();
    }

    // A batch editor may change the working inputs in-place or create new
    // exports. Only outputs that can be unambiguously mapped to a source
    // filename are accepted. The user can choose exports from another folder.
    public static ExternalBatchOutput[] FindMultipleResults(PanoramaSession session)
    {
        if (session.IsPanorama)
            throw new InvalidOperationException("Panorama erwartet genau ein Ergebnis.");
        if (!Directory.Exists(session.Directory)) return [];
        var inputs = session.Inputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exports = Directory.EnumerateFiles(session.Directory)
            .Where(path => !inputs.Contains(path) &&
                (EditableImageFormats.SupportsExternalResult(path) ||
                 RawImageSource.IsRawPath(path)) &&
                new FileInfo(path).Length > 0)
            .Select(path => new ExternalBatchOutput(MatchSourceIndex(session, path), path))
            .ToList();

        for (var index = 0; index < session.Inputs.Length; index++)
        {
            if (exports.Any(result => result.SourceIndex == index))
                continue;
            var input = session.Inputs[index];
            if (!File.Exists(input)) continue;
            var previous = session.InputHashes is { } hashes && index < hashes.Length
                ? hashes[index] : null;
            if (previous is null) continue;
            using var source = new FileStream(input, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var current = Convert.ToHexString(SHA256.HashData(source));
            if (!current.Equals(previous, StringComparison.OrdinalIgnoreCase))
                exports.Add(new ExternalBatchOutput(index, input));
        }
        return exports.ToArray();
    }

    public static int MatchSourceIndex(PanoramaSession session, string result)
    {
        var stem = Path.GetFileNameWithoutExtension(result);
        static bool Match(string name, string expected) =>
            name.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            new[] { "-", "_", " (", "." }.Any(separator =>
                name.StartsWith(expected + separator, StringComparison.OrdinalIgnoreCase));

        // Local input names include a four-digit ordinal so identical
        // camera filenames from different folders remain distinguishable.
        var staged = Enumerable.Range(0, session.Inputs.Length)
            .Where(i => Match(stem, Path.GetFileNameWithoutExtension(session.Inputs[i])))
            .ToArray();
        if (staged.Length == 1) return staged[0];
        var originals = Enumerable.Range(0, session.Sources.Length)
            .Where(i => Match(stem, Path.GetFileNameWithoutExtension(session.Sources[i])))
            .ToArray();
        if (originals.Length == 1) return originals[0];
        throw new InvalidDataException(
            "Das externe Ergebnis kann keinem eindeutigen Ausgangsbild zugeordnet werden: " +
            Path.GetFileName(result));
    }

    // Autopano Giga and other panorama editors export to a user-selected
    // directory, not necessarily the process working directory. Capture only
    // the ONE result explicitly chosen by the user. Stage it locally so that
    // metadata writing and catalog import never change the external output.
    // We do not scan/watch arbitrary user folders or guess from timestamps.
    public static string StageExternalResult(PanoramaSession session,
        string selectedPath, CancellationToken token = default,
        Action<long, long>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(selectedPath);
        if (session.Inputs.Concat(session.Sources).Any(path =>
                Path.GetFullPath(path).Equals(source,
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "Ein Ausgangsbild darf nicht als Panoramaergebnis übernommen werden.");
        if (!EditableImageFormats.SupportsExternalResult(source))
            throw new NotSupportedException(
                "Dieses Panorama-Ausgabeformat kann PCasa nicht übernehmen.");
        if (!File.Exists(source))
            throw new FileNotFoundException("Panoramaergebnis nicht gefunden.", source);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var destination = Path.Combine(session.Directory,
            "panorama-output-" + Guid.NewGuid().ToString("N") + extension);
        var sourceInfo = new FileInfo(source);
        var originalLength = sourceInfo.Length;
        var originalWriteTicks = sourceInfo.LastWriteTimeUtc.Ticks;
        if (originalLength == 0)
            throw new InvalidDataException("Panoramaergebnis ist leer.");
        try
        {
            using (SourceFileLocks.Enter(source, token))
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 1024 * 1024))
            using (var output = new FileStream(destination, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 1024 * 1024,
                       FileOptions.WriteThrough))
            {
                var bytes = new byte[1024 * 1024];
                long copied = 0;
                int count;
                while ((count = input.Read(bytes)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(bytes, 0, count);
                    copied += count;
                    progress?.Invoke(copied, originalLength);
                }
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            sourceInfo.Refresh();
            if (sourceInfo.Length != originalLength ||
                sourceInfo.LastWriteTimeUtc.Ticks != originalWriteTicks)
                throw new IOException(
                    "Das Panoramaprogramm schreibt noch am Ergebnis. Bitte nach Abschluss erneut auswählen.");
            if (new FileInfo(destination).Length != originalLength)
                throw new IOException("Die Panorama-Arbeitskopie ist unvollständig.");
            EditableImageFormats.ValidateStillImage(destination);
            return destination;
        }
        catch
        {
            try { if (File.Exists(destination)) File.Delete(destination); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static string Import(PanoramaSession session, string result,
        CancellationToken token = default, Action<long, long>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var fullResult = Path.GetFullPath(result);
        if (!Path.GetDirectoryName(fullResult)!.Equals(session.Directory,
                StringComparison.OrdinalIgnoreCase) ||
            session.Inputs.Any(input => input.Equals(fullResult,
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Panoramaergebnis liegt nicht im Sitzungsordner.");
        EditableImageFormats.ValidateStillImage(fullResult);
        // Transfer complete capture metadata from the FIRST image. Then use
        // independent first-with-tags and first-with-GPS selections.
        ExternalResultMetadata.CopyAll(session.Sources[0], fullResult, token);
        if (session.Tags.Length > 0)
        {
            using var writer = new ExifToolKeywordWriter.Session();
            writer.Write(fullResult, session.Tags, token);
        }
        if (session.Latitude is double latitude && session.Longitude is double longitude)
        {
            using var writer = new ExifToolKeywordWriter.Session();
            writer.WriteGps(fullResult, latitude, longitude, token);
        }

        // Never overwrite an existing panorama or its source. A new output is
        // staged on the destination volume and renamed only when complete.
        var folder = Path.GetDirectoryName(session.Sources[0])!;
        var stem = Path.GetFileNameWithoutExtension(session.Sources[0]) +
            "-" + session.Inputs.Length + "px-Panorama";
        var extension = Path.GetExtension(fullResult).ToLowerInvariant();
        var temp = Path.Combine(folder, ".pcasa-panorama-" +
            Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var source = new FileStream(fullResult, FileMode.Open, FileAccess.Read,
                       FileShare.Read, 1024 * 1024))
            using (var destination = new FileStream(temp, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 1024 * 1024,
                       FileOptions.WriteThrough))
            {
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                int count;
                while ((count = source.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, count);
                    copied += count;
                    progress?.Invoke(copied, source.Length);
                }
                destination.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            for (var i = 1; i < 10000; i++)
            {
                var filename = stem + (i == 1 ? "" : "-" + i) + extension;
                var target = Path.Combine(folder, filename);
                try
                {
                    File.Move(temp, target);
                    return target;
                }
                catch (IOException) when (File.Exists(target)) { }
            }
            throw new IOException("Kein freier Dateiname für das Panorama.");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }


    public static string[] ImportMultiple(PanoramaSession session,
        IReadOnlyList<ExternalBatchOutput> results,
        CancellationToken token = default, Action<long, long>? progress = null)
    {
        if (session.IsPanorama)
            throw new InvalidOperationException("Panorama ist keine Mehrfachbearbeitung.");
        if (results.Count == 0)
            throw new InvalidOperationException("Keine Ergebnisse zur Übernahme vorhanden.");

        var planned = new List<(string Temp, string Target)>();
        var published = new List<string>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long copied = 0;
        long total = results.Sum(entry => new FileInfo(entry.Path).Length);
        try
        {
            // Validate and stage every result before publishing even one
            // file. A failed batch does not modify any camera original.
            foreach (var output in results)
            {
                token.ThrowIfCancellationRequested();
                if (output.SourceIndex < 0 || output.SourceIndex >= session.Sources.Length ||
                    !Path.GetDirectoryName(Path.GetFullPath(output.Path))!.Equals(
                        session.Directory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Ungültige Ergebnis-Zuordnung.");
                var source = session.Sources[output.SourceIndex];
                var modifiedUtc = File.GetLastWriteTimeUtc(output.Path);
                var createdUtc = File.GetCreationTimeUtc(source);
                if (RawImageSource.IsRawPath(output.Path))
                    ExternalEditTransaction.ValidateExternalRawResult(output.Path, token);
                else
                    EditableImageFormats.ValidateStillImage(output.Path);
                // RAW result containers may be camera negatives. Their
                // binary payload must not be rewritten by PCasa.
                if (!RawImageSource.IsRawPath(output.Path))
                    ExternalResultMetadata.CopyAll(source, output.Path, token);

                var desired = Path.Combine(Path.GetDirectoryName(source)!,
                    Path.GetFileNameWithoutExtension(source) + Path.GetExtension(output.Path));
                var destination = ExternalCopyNames.NextAvailable(desired, reserved);
                reserved.Add(destination);
                var temp = Path.Combine(Path.GetDirectoryName(destination)!,
                    ".pcasa-multiedit-" + Guid.NewGuid().ToString("N") + ".tmp");
                planned.Add((temp, destination));
                using (var input = new FileStream(output.Path, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var file = new FileStream(temp, FileMode.CreateNew,
                           FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[1024 * 1024];
                    int count;
                    while ((count = input.Read(buffer)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        file.Write(buffer, 0, count);
                        copied += count;
                        progress?.Invoke(copied, total);
                    }
                    file.Flush(flushToDisk: true);
                    if (file.Length != input.Length)
                        throw new IOException("Ein Mehrfachergebnis wurde unvollständig kopiert.");
                }
                File.SetCreationTimeUtc(temp, createdUtc);
                File.SetLastWriteTimeUtc(temp, modifiedUtc);
            }
            token.ThrowIfCancellationRequested();
            foreach (var (temp, target) in planned)
            {
                // A collision detected after preparation is an error, never
                // permission to replace a photo in a monitored folder.
                File.Move(temp, target, overwrite: false);
                published.Add(target);
            }
            return published.ToArray();
        }
        catch
        {
            // Best-effort rollback affects ONLY new files from this batch,
            // never any original or unrelated external result.
            foreach (var path in published)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
        finally
        {
            foreach (var (temp, _) in planned)
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static void Cleanup(PanoramaSession session)
    {
        try { if (Directory.Exists(session.Directory)) Directory.Delete(
            session.Directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
