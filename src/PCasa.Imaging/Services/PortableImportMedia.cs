using NetVips;

namespace PCasa.App.Services;

// A production composition for non-WPF hosts. A host may provide a video
// frame resolver; lack of one never causes an original video to be modified.
public sealed class PortableImportMedia : VipsThumbnailCache, IFolderImportMedia
{
    private readonly string? _packRoot;
    private readonly string? _psdRoot;
    private readonly Func<string, CancellationToken, string>? _videoSource;

    public PortableImportMedia(string? thumbnailRoot = null, string? packRoot = null,
        string? psdRoot = null, Func<string, CancellationToken, string>? videoSource = null)
        : base(thumbnailRoot)
    {
        _packRoot = packRoot;
        _psdRoot = psdRoot;
        _videoSource = videoSource;
    }

    public bool IsRawPath(string path) => CatalogMediaFormats.IsRawPath(path);

    public string ResolveSource(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (VideoFormats.IsVideo(path))
            return _videoSource?.Invoke(path, token) ??
                throw new NotSupportedException("Video frame decoder is not configured.");
        return VipsImageSource.IsPsdPath(path)
            ? PsdCompositeCache.Resolve(path, token, _psdRoot)
            : VipsImageSource.Resolve(path, token);
    }

    public IImportThumbnailStore CreatePackStore() => new PackedThumbnailStore(_packRoot);

    public string? ReadKeywords(string path)
    {
        lock (SourceFileLocks.ForPath(path))
        {
            try
            {
                using var session = new ExifToolKeywordWriter.Session();
                return string.Join("\n", session.Read(path));
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (System.Text.Json.JsonException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
            catch (NotSupportedException) { return string.Empty; }
        }
    }

    public bool IsDecodeFailure(Exception exception) => exception is VipsException;
}
