using PCasa.App.Services;

namespace PCasa.App.Data;

// Retain the Windows JPEG/SMB optimization. All GPS catalog rules and
// the verified ExifTool fallback live in PCasa.Core.
public sealed class GpsMetadataRepository(LocalDatabase database)
    : GpsCatalogOperations(database, JpegMetadataOriginalWriter.TryWriteGps)
{
}
