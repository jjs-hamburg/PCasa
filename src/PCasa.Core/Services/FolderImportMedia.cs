using PCasa.App.Models;

namespace PCasa.App.Services;

// Platform boundary for image ingestion. The scanner owns enumeration,
// checkpointing, source locks and catalog transactions; the host owns
// decoding and encoding. No UI or native decoder types cross this boundary.
public sealed record ThumbnailResult(string Path, int Width, int Height);

public sealed record ImportThumbnailTier(int Tier, byte[] Bytes, int Width, int Height);

public interface IFolderImportMedia
{
    bool IsRawPath(string path);
    string ResolveSource(string path, CancellationToken token);
    ThumbnailResult CreateOrUpdate(string sourcePath, string canonicalPath,
        long fileSize, long modifiedUtcTicks, string? originalSourcePath = null,
        CancellationToken cancellationToken = default);
    ThumbnailResult CreateImportErrorPlaceholder(string canonicalPath,
        long fileSize, long modifiedUtcTicks, CancellationToken token);
    IImportThumbnailStore CreatePackStore();
    string? ReadKeywords(string path);
    bool IsDecodeFailure(Exception exception);
}

public interface IImportThumbnailStore : IDisposable
{
    IReadOnlyList<ImportThumbnailTier> PrepareTiers(string sourcePath,
        BrowseThumbnailProfile profile, CancellationToken cancellationToken = default,
        string? originalSourcePath = null);
    IReadOnlyList<PackedThumbnailLocation> AppendPrepared(
        IReadOnlyList<ImportThumbnailTier> prepared,
        CancellationToken cancellationToken = default);
}

public interface IImportExifReader : IDisposable
{
    ImageExifData Read(string path, CancellationToken cancellationToken = default);
}
