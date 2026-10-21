using System.Diagnostics;
using System.IO;
using PCasa.App.Data;
using PCasa.App.Models;

namespace PCasa.App.Services;

public sealed record BrowseCacheBuildProgress(
    int TotalImages,
    int ProcessedImages,
    int FailedImages,
    string? CurrentPath,
    string? CurrentThumbnailPath);

public sealed record BrowseCacheBuildResult(
    int TotalImages,
    int ProcessedImages,
    int FailedImages);

public sealed class BrowseCacheBuilder
{
    private const int CandidateBatchSize = 500;
    private const int LegacyThumbnailMaximumTier = 512;
    private const int SystemicFailureThreshold = 20;

    private readonly BrowseCacheRepository _repository;
    private readonly BrowseThumbnailProfileRepository _profileRepository;

    public BrowseCacheBuilder(
        BrowseCacheRepository repository,
        BrowseThumbnailProfileRepository profileRepository)
    {
        _repository = repository;
        _profileRepository = profileRepository;
    }

    public BrowseCacheBuildResult BuildMissing(
        IProgress<BrowseCacheBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        lock (CatalogRemovalService.CacheLock)
            return BuildMissingLocked(progress, cancellationToken);
    }

    private BrowseCacheBuildResult BuildMissingLocked(
        IProgress<BrowseCacheBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        var profile = _profileRepository.Get();
        var total = _repository.CountMissing(profile);
        var processed = 0;
        var failed = 0;
        var afterImageId = 0L;
        var progressClock = Stopwatch.StartNew();
        Exception? firstFailure = null;
        string? firstFailurePath = null;

        // A previous first-run failure may have written pack bytes before any index
        // entry was committed. If there is no usable index at all, those bytes are
        // unreachable and can be safely discarded before retrying.
        if (_repository.CountEntries() == 0)
        {
            PackedThumbnailStore.ResetPackFiles();
        }

        using var store = new PackedThumbnailStore();
        using var writeSession = _repository.CreateWriteSession();

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var candidates = _repository.GetMissingCandidates(
                    profile,
                    afterImageId,
                    CandidateBatchSize);
                if (candidates.Count == 0)
                {
                    break;
                }

                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    afterImageId = candidate.ImageId;

                    try
                    {
                        BuildCandidate(
                            candidate,
                            profile,
                            store,
                            writeSession,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        firstFailure ??= ex;
                        firstFailurePath ??= candidate.LocalPath;
                    }

                    processed++;
                    if (progressClock.ElapsedMilliseconds >= 250)
                    {
                        progressClock.Restart();
                        progress?.Report(new BrowseCacheBuildProgress(
                            total,
                            processed,
                            failed,
                            candidate.LocalPath,
                            candidate.ThumbnailPath));
                    }

                    if (processed >= SystemicFailureThreshold && failed == processed)
                    {
                        throw new InvalidOperationException(
                            $"Die ersten {processed:N0} Bilder konnten sämtlich nicht in den Browse-Cache konvertiert werden.\n\n" +
                            $"Erste fehlerhafte Datei:\n{firstFailurePath}\n\n" +
                            $"Ursache:\n{firstFailure?.Message}",
                            firstFailure);
                    }
                }

                writeSession.Commit();
            }

            writeSession.Commit();
        }
        catch
        {
            // Dispose must not commit an interrupted, unfinished SQLite batch.
            writeSession.RollbackPending();
            throw;
        }
        progress?.Report(new BrowseCacheBuildProgress(
            total,
            processed,
            failed,
            null,
            null));

        return new BrowseCacheBuildResult(total, processed, failed);
    }

    private static void BuildCandidate(
        BrowseCacheCandidate candidate,
        BrowseThumbnailProfile profile,
        PackedThumbnailStore store,
        BrowseCacheWriteSession writeSession,
        CancellationToken cancellationToken)
    {
        var missingTiers = profile.Tiers
            .Where(tier => !candidate.ExistingTiers.Contains(tier))
            .ToArray();
        if (missingTiers.Length == 0)
        {
            writeSession.MarkComplete(candidate.ImageId, candidate.ModifiedUtcTicks, profile);
            return;
        }

        var legacyThumbnailAvailable = File.Exists(candidate.ThumbnailPath);

        var legacyTiers = legacyThumbnailAvailable
            ? missingTiers.Where(tier => tier <= LegacyThumbnailMaximumTier).ToArray()
            : Array.Empty<int>();

        if (legacyTiers.Length > 0)
        {
            SaveLocations(
                store.CreateTiers(
                    candidate.ThumbnailPath,
                    new BrowseThumbnailProfile(legacyTiers),
                    cancellationToken));
        }

        var originalTiers = missingTiers
            .Where(tier => !legacyTiers.Contains(tier))
            .ToArray();

        if (originalTiers.Length > 0)
        {
            if (!File.Exists(candidate.LocalPath))
            {
                throw new FileNotFoundException(
                    "Original für die benötigte Browse-Thumbnail-Größe ist nicht verfügbar.",
                    candidate.LocalPath);
            }

            SaveLocations(
                store.CreateTiers(
                    candidate.LocalPath,
                    new BrowseThumbnailProfile(originalTiers),
                    cancellationToken));
        }

        // The persisted job is cleared only if every configured tier now
        // exists for this exact source version, in the same SQLite transaction.
        writeSession.MarkComplete(candidate.ImageId, candidate.ModifiedUtcTicks, profile);

        void SaveLocations(IReadOnlyList<PackedThumbnailLocation> locations)
        {
            foreach (var location in locations)
            {
                writeSession.Save(
                    candidate.ImageId,
                    candidate.ModifiedUtcTicks,
                    location);
            }
        }
    }
}
