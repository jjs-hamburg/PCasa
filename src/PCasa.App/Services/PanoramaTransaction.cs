using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// A panorama uses immutable copies of multiple source images. Unlike an
/// in-place editor there is no source replacement, undo or resume/replay.
/// The external application owns only the local session folder.
/// </summary>
public sealed record PanoramaSession(
    Guid Id, long[] ImageIds, string[] Sources, string[] Inputs,
    string Directory, string[] Tags, double? Latitude, double? Longitude);

public static class PanoramaTransaction
{
    public static string SessionRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "Panorama");

    public static PanoramaSession Begin(
        (long Id, string Path)[] images, string[] tags,
        double? latitude, double? longitude,
        CancellationToken token = default, Action<long, long>? progress = null)
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
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = source.Read(buffer)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        destination.Write(buffer, 0, read);
                        copied += read;
                        progress?.Invoke(copied, total);
                    }
                    destination.Flush(true);
                }
                staged.Add(input);
            }
            return new PanoramaSession(id, images.Select(x => x.Id).ToArray(),
                images.Select(x => Path.GetFullPath(x.Path)).ToArray(),
                staged.ToArray(), directory, tags, latitude, longitude);
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

    public static void Cleanup(PanoramaSession session)
    {
        try { if (Directory.Exists(session.Directory)) Directory.Delete(
            session.Directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
