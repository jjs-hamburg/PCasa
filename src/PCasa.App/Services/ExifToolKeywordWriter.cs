using System.Diagnostics;
using System.IO;
using System.Text;

namespace PCasa.App.Services;

/// <summary>
/// Writes only XMP keyword metadata through the bundled, lossless ExifTool.
/// JPEG pixels are never decoded or recompressed by this operation.
/// </summary>
public static class ExifToolKeywordWriter
{
    public static void Write(string imagePath, IReadOnlyList<string> keywords)
    {
        var executable = Environment.GetEnvironmentVariable("PCASA_EXIFTOOL_PATH");
        if (string.IsNullOrWhiteSpace(executable))
            executable = Path.Combine(AppContext.BaseDirectory, "exiftool", "exiftool.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException(
                "Die mit PCasa ausgelieferte Metadatenkomponente ExifTool fehlt.", executable);

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // ExifTool on Windows cannot reliably receive Unicode paths and tag
        // values through ordinary command-line arguments. Send a UTF-8 argfile
        // over stdin; this also activates Windows wide-character file I/O.
        start.ArgumentList.Add("-charset");
        start.ArgumentList.Add("filename=UTF8");
        start.ArgumentList.Add("-@");
        start.ArgumentList.Add("-");

        using var process = Process.Start(start)
            ?? throw new IOException("ExifTool konnte nicht gestartet werden.");
        // Drain both streams while the child is running to avoid pipe deadlocks.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.WriteLine("-overwrite_original");
        if (keywords.Count == 0)
            process.StandardInput.WriteLine("-XMP-dc:Subject=");
        foreach (var keyword in keywords)
        {
            if (keyword.Contains('\r') || keyword.Contains('\n'))
                throw new ArgumentException("Ein Tag darf keine Zeilenumbrüche enthalten.");
            // For ExifTool list tags, '=' replaces the existing list; repeated
            // '=' assignments create the requested list. Combining '=' with
            // '+=' instead preserves stale keywords and breaks selective delete.
            process.StandardInput.WriteLine("-XMP-dc:Subject=" + keyword);
        }
        process.StandardInput.WriteLine("--");
        process.StandardInput.WriteLine(imagePath);
        process.StandardInput.Close();
        if (!process.WaitForExit(90000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("ExifTool hat die Metadatenbearbeitung nicht rechtzeitig abgeschlossen.");
        }
        var stdout = output.GetAwaiter().GetResult();
        var stderr = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new IOException(
                "ExifTool: " + (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim());
    }
}
