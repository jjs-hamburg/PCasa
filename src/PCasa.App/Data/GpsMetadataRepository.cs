using PCasa.App.Services;

namespace PCasa.App.Data;

// The portable native metadata service owns transport selection for every edit.
public sealed class GpsMetadataRepository(LocalDatabase database)
    : GpsCatalogOperations(database)
{
}
