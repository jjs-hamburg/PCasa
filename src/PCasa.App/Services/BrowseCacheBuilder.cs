using PCasa.App.Data;

namespace PCasa.App.Services;

// Windows composition adapter; cache rebuilding and commits are shared.
public sealed class BrowseCacheBuilder(
    BrowseCacheRepository repository,
    BrowseThumbnailProfileRepository profileRepository)
    : BrowseCacheBuildOperations(repository, profileRepository,
        (path, token) => VideoFormats.IsVideo(path)
            ? VideoThumbnailSource.Resolve(path, token)
            : RawImageSource.Resolve(path, token))
{
}
