namespace PCasa.App.Models;

public sealed record CatalogImageState(
    long FileSize,
    long ModifiedUtcTicks,
    string ThumbnailPath,
    long? PhotoFolderId,
    string LocalPath,
    bool HasCurrentExif = false);

public sealed record CatalogImageData(
    long FolderId,
    long PhotoFolderId,
    string LocalPath,
    string CanonicalPath,
    string FileName,
    string Extension,
    long FileSize,
    long ModifiedUtcTicks,
    int Width,
    int Height,
    string ThumbnailPath,
    long? CreatedUtcTicks = null);

public sealed record PhotoFolderSummary(
    long Id,
    long MonitoredFolderId,
    string LocalPath,
    string CanonicalPath,
    string DisplayName,
    int ImageCount);

public sealed record GalleryImage(
    long Id,
    long PhotoFolderId,
    string LocalPath,
    string CanonicalPath,
    string FileName,
    int Width,
    int Height,
    string ThumbnailPath);
