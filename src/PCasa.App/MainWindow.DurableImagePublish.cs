using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using PCasa.App.Services;
using PCasa.App.Data;

namespace PCasa.App;

public partial class MainWindow
{
    private sealed record WriteFingerprint(string OriginalHash, string? CandidateHash = null);

    private void PersistWriteRecipe<T>(string key, T recipe)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduler_packages SET payload_json=$payload
             WHERE package_key=$key AND state IN ('queued','running','paused','deferred');
            """;
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(recipe));
        cmd.Parameters.AddWithValue("$key", key);
        if (cmd.ExecuteNonQuery() != 1)
            throw new IOException("Write intent could not be recorded.");
    }

    private static string WriteHash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    // Record the verified candidate BEFORE atomic publication. A crash after
    // publication but before the journal cursor cannot apply a relative edit
    // or restore twice, and an unrelated new file is never overwritten.
    private static void PublishDurableImage(string source, string destination,
        WriteFingerprint? fingerprint, Action<WriteFingerprint> persist,
        Action<Action<string>> publish, CancellationToken token)
    {
        using var guard = SourceFileLocks.EnterPair(source, destination, token);
        bool copy = !string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);
        var state = fingerprint;
        if (state?.CandidateHash is { } candidate &&
            SourceFilePresence.Probe(destination) == FilePresence.Present &&
            WriteHash(destination) == candidate)
            return;
        var sourceHash = WriteHash(source);
        if (state is not null && sourceHash != state.OriginalHash)
            throw new IOException("Image changed since the write was prepared: " + source);
        if (copy && SourceFilePresence.Probe(destination) != FilePresence.ConfirmedMissing)
            throw new IOException("Image copy destination is not confirmed free: " + destination);
        state ??= new WriteFingerprint(sourceHash);
        persist(state);
        publish(staged =>
        {
            state = state! with { CandidateHash = WriteHash(staged) };
            persist(state);
        });
    }

    private void ReindexDurableImage(long imageId, long folderId, string path)
    {
        new BrowseCacheRepository(_database).InvalidateImage(imageId);
        var root = _folderRepository.GetAll().FirstOrDefault(folder => folder.Id == folderId)
            ?? throw new IOException("Monitored folder unavailable.");
        var result = _folderScanner.ScanFile(root, path,
            publishCatalogChange: false, forceExifRefresh: true);
        if (result.Failed > 0 || result.ImageFilesSeen == 0)
            throw new IOException("Image catalog reconciliation failed: " + path);
    }
}
