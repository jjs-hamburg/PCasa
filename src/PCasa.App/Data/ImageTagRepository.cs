using System.IO;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

namespace PCasa.App.Data;

// Windows composition adapter. Native metadata transport and transactions
// are owned by the common portable catalog services.
public sealed class ImageTagRepository : TagCatalogOperations
{
    public ImageTagRepository(LocalDatabase database)
        : base(database, ReadKeywords) { }

    // Null means the source was unavailable or could not be decoded.
    // Empty string means it was inspected successfully and has no keywords.
    public static string? ReadKeywords(string path)
    {
        lock (SourceFileLocks.ForPath(path))
            return ReadKeywordsLocked(path);
    }

    private static string? ReadKeywordsLocked(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".arw", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new ExifToolKeywordWriter.Session();
                return string.Join("\n", reader.Read(path));
            }
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var keywords = (decoder.Frames[0].Metadata as BitmapMetadata)?.Keywords;
            return keywords is { Count: > 0 }
                ? string.Join("\n", keywords.Where(word => !string.IsNullOrWhiteSpace(word))
                    .Select(word => word.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                : string.Empty;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (Exception) { return string.Empty; } // Unsupported image metadata.
    }

}
