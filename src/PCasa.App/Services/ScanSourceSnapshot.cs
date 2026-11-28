using System.IO;
using System.Security.Cryptography;

namespace PCasa.App.Services;

// A single local, short-lived copy of a network JPEG. Hash and copy use
// the same sequential read. The caller retains the original read lease.
internal sealed class ScanSourceSnapshot : IDisposable
{
    private static string Root => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "scan-source-snapshots");
    public string Path { get; }
    public byte[] Fingerprint { get; }

    private ScanSourceSnapshot(string path, byte[] fingerprint)
    {
        Path = path;
        Fingerprint = fingerprint;
    }

    public static ScanSourceSnapshot Create(Stream input, string original,
        long expectedLength, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Root);
        var path = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N") +
            System.IO.Path.GetExtension(original));
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var output = new FileStream(path, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.SequentialScan);
            var buffer = new byte[256 * 1024];
            long length = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var count = input.ReadAsync(buffer.AsMemory(), token)
                    .AsTask().GetAwaiter().GetResult();
                if (count == 0) break;
                hash.AppendData(buffer, 0, count);
                output.Write(buffer, 0, count);
                length += count;
            }
            token.ThrowIfCancellationRequested();
            if (length != expectedLength)
                throw new IOException("Source length changed during snapshot: " + original);
            return new ScanSourceSnapshot(path, hash.GetHashAndReset());
        }
        catch
        {
            Delete(path);
            throw;
        }
    }

    public void Dispose() => Delete(Path);

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PCasaDiagnostics.Write("Scan.SnapshotCleanup", ex); }
    }

    // Interactive startup owns the global single-instance mutex, so no
    // running import can own a leftover copy from a previous process.
    public static void CleanAbandoned()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var file in Directory.EnumerateFiles(Root))
                Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PCasaDiagnostics.Write("Scan.SnapshotCleanup", ex); }
    }
}
