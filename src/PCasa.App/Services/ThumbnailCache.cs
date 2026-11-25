using System.IO;
using NetVips;

namespace PCasa.App.Services;

public sealed class ThumbnailCache : VipsThumbnailCache, IFolderImportMedia
{
    public ThumbnailCache() : base(PsdCompositePreview.GetDimensions, WritePlaceholder) { }

    bool IFolderImportMedia.IsRawPath(string path) => RawImageSource.IsRawPath(path);

    string IFolderImportMedia.ResolveSource(string path, CancellationToken token)
        => VideoFormats.IsVideo(path)
            ? VideoThumbnailSource.Resolve(path, token)
            : RawImageSource.Resolve(path, token);

    IImportThumbnailStore IFolderImportMedia.CreatePackStore() => new PackedThumbnailStore();

    string? IFolderImportMedia.ReadKeywords(string path)
        => PCasa.App.Data.ImageTagRepository.ReadKeywords(path);

    bool IFolderImportMedia.IsDecodeFailure(Exception exception)
        => exception is VipsException;

    // Preserve the Windows JPEG encoder and its existing output.
    private static void WritePlaceholder(string temporary, byte[] pixels, int size,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            size, size, 96, 96, System.Windows.Media.PixelFormats.Rgb24,
            null, pixels, size * 3);
        bitmap.Freeze();
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder
            { QualityLevel = 85 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var output = File.Create(temporary)) encoder.Save(output);
    }
}
