using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NetVips;

namespace PCasa.App.Services;

/// <summary>
/// One source resolver for RAW images used by the existing scan, browse-cache
/// and viewer pipelines. Prefer native libvips/LibRaw demosaicing; if the
/// packaged native build has no RAW decoder, extract an embedded JPEG preview
/// with the already bundled ExifTool into PCasa's LOCAL cache. Neither path
/// changes the original RAW, writes beside it, or creates a second scanner.
/// </summary>
public static class RawImageSource
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".arw", ".nef", ".nrw", ".cr2", ".cr3", ".dng",
        ".raf", ".rw2", ".orf", ".pef", ".srw", ".crw"
    };

    public static bool IsRawPath(string path) => Extensions.Contains(Path.GetExtension(path));

    public static string Resolve(string path, CancellationToken cancellationToken = default)
    {
        if (!IsRawPath(path)) return path;
        cancellationToken.ThrowIfCancellationRequested();

        // Native libvips 8.18+ has a libraw loader when the native build
        // includes libraw. Probe the file once; this does not decode or alter
        // the complete sensor image. In that case the existing thumbnail and
        // viewer paths can render the original RAW directly.
        try
        {
            using var header = Image.NewFromFile(path, access: Enums.Access.Sequential);
            if (header.Width > 0 && header.Height > 0)
                return path;
        }
        catch (VipsException)
        {
            // A camera RAW may not be supported by this native build.
            // The contained JPEG preview is still useful for photo browsing.
        }

        return ExtractEmbeddedPreview(path, cancellationToken);
    }

    private static readonly ConcurrentDictionary<string,
        (long Length, long Ticks, int Orientation)> OrientationCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// RAW previews sometimes carry no EXIF orientation of their own. Read the
    /// orientation from the RAW container, disable automatic libvips rotation
    /// and apply it exactly once to both gallery and viewer. Normal JPEG/PNG
    /// paths keep the stable built-in libvips rotation.
    /// </summary>
    public static Image LoadDisplayThumbnail(string renderedPath, string originalPath,
        int width, int height, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!IsRawPath(originalPath))
            return Image.Thumbnail(renderedPath, width, height: height,
                size: Enums.Size.Down);

        var orientation = GetRawOrientation(originalPath, token);
        using var pixels = Image.Thumbnail(renderedPath, width, height: height,
            size: Enums.Size.Down, noRotate: true);
        token.ThrowIfCancellationRequested();
        return ApplyOrientation(pixels, orientation);
    }

    public static int GetRawOrientation(string path,
        CancellationToken token = default)
    {
        if (!IsRawPath(path)) return 1;
        var info = new FileInfo(path);
        if (OrientationCache.TryGetValue(path, out var cached) &&
            cached.Length == info.Length && cached.Ticks == info.LastWriteTimeUtc.Ticks)
            return cached.Orientation;

        var executable = Environment.GetEnvironmentVariable("PCASA_EXIFTOOL_PATH");
        if (string.IsNullOrWhiteSpace(executable))
            executable = Path.Combine(AppContext.BaseDirectory, "exiftool", "exiftool.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("ExifTool für RAW-Orientierung fehlt.", executable);

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-charset");
        start.ArgumentList.Add("filename=UTF8");
        start.ArgumentList.Add("-@");
        start.ArgumentList.Add("-");
        using var process = Process.Start(start)
            ?? throw new IOException("ExifTool für RAW-Orientierung konnte nicht starten.");
        using var stop = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var errors = process.StandardError.ReadToEndAsync();
        var result = process.StandardOutput.ReadToEndAsync();
        process.StandardInput.WriteLine("-s3");
        process.StandardInput.WriteLine("-n");
        process.StandardInput.WriteLine("-Orientation");
        process.StandardInput.WriteLine("--");
        process.StandardInput.WriteLine(path);
        process.StandardInput.Close();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("ExifTool konnte RAW-Orientierung nicht lesen.");
        }
        token.ThrowIfCancellationRequested();
        var stderr = errors.GetAwaiter().GetResult();
        var value = result.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
            throw new IOException("RAW-Orientierung nicht lesbar: " + stderr.Trim());
        var orientation = int.TryParse(value, out var parsed) && parsed is >= 1 and <= 8
            ? parsed : 1;
        if (OrientationCache.Count >= 1024) OrientationCache.Clear();
        OrientationCache[path] = (info.Length, info.LastWriteTimeUtc.Ticks, orientation);
        return orientation;
    }

    public static Image ApplyOrientation(Image pixels, int orientation)
    {
        // Only used on a noRotate:true input. Returning a new image keeps the
        // caller's lifetime independent of the temporarily loaded source.
        return orientation switch
        {
            2 => pixels.Flip(Enums.Direction.Horizontal),
            3 => pixels.Rot(Enums.Angle.D180),
            4 => pixels.Flip(Enums.Direction.Vertical),
            5 => RotateAndFlip(pixels, Enums.Angle.D90, Enums.Direction.Horizontal),
            6 => pixels.Rot(Enums.Angle.D90),
            7 => RotateAndFlip(pixels, Enums.Angle.D90, Enums.Direction.Vertical),
            8 => pixels.Rot(Enums.Angle.D270),
            _ => pixels.Copy()
        };
    }

    private static Image RotateAndFlip(Image pixels, Enums.Angle angle,
        Enums.Direction direction)
    {
        using var rotated = pixels.Rot(angle);
        return rotated.Flip(direction);
    }

    private static string ExtractEmbeddedPreview(string path, CancellationToken token)
    {
        var file = new FileInfo(path);
        var identity = $"{Path.GetFullPath(path)}\n{file.Length}\n{file.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        var destination = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCasa", "raw-previews", hash[..2], hash + ".jpg");
        lock (SourceFileLocks.ForPath(path))
        {
            if (File.Exists(destination) && new FileInfo(destination).Length > 1024)
                return destination;

            var executable = Environment.GetEnvironmentVariable("PCASA_EXIFTOOL_PATH");
            if (string.IsNullOrWhiteSpace(executable))
                executable = Path.Combine(AppContext.BaseDirectory, "exiftool", "exiftool.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException(
                    "ExifTool für die eingebettete RAW-Vorschau fehlt.", executable);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            foreach (var tag in new[] { "JpgFromRaw", "PreviewImage", "OtherImage", "ThumbnailImage" })
            {
                token.ThrowIfCancellationRequested();
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Extract(executable, path, tag, temporary, token);
                    using (var input = File.OpenRead(temporary))
                    {
                        if (input.Length < 1024 || input.ReadByte() != 0xFF ||
                            input.ReadByte() != 0xD8)
                            continue;
                    }
                    File.Move(temporary, destination, overwrite: true);
                    return destination;
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }

        throw new NotSupportedException(
            "RAW-Datei lässt sich weder direkt decodieren, noch enthält sie eine " +
            "verwendbare eingebettete JPEG-Vorschau: " + path);
    }

    private static void Extract(string executable, string original, string tag,
        string destination, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // UTF-8 argument file is necessary for non-ASCII network paths.
        start.ArgumentList.Add("-charset");
        start.ArgumentList.Add("filename=UTF8");
        start.ArgumentList.Add("-@");
        start.ArgumentList.Add("-");
        using var process = Process.Start(start)
            ?? throw new IOException("ExifTool konnte die RAW-Datei nicht lesen.");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.WriteLine("-b");
        process.StandardInput.WriteLine("-" + tag);
        process.StandardInput.WriteLine("--");
        process.StandardInput.WriteLine(original);
        process.StandardInput.Close();

        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
            process.StandardOutput.BaseStream.CopyTo(output);

        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("ExifTool RAW-Vorschau-Extraktion dauerte zu lange.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var errors = stderr.GetAwaiter().GetResult();
        // A missing optional preview tag is expected on many camera models.
        // Try the next tag instead of failing the entire import immediately.
        if (process.ExitCode != 0 && new FileInfo(destination).Length == 0)
            return;
    }
}
