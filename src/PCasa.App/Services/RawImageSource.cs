using System.Diagnostics;
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
        if (process.ExitCode != 0 && new FileInfo(destination).Length == 0)
            throw new IOException("ExifTool konnte keine RAW-Vorschau liefern: " + errors.Trim());
    }
}
