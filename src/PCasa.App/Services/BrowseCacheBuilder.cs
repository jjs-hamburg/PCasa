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
        var profile = _profileRepository.Get();
        var total = _repository.CountMissing(profile);
        var processed = 0;
        var failed = 0;
        var afterImageId = 0L;
        var progressClock = Stopwatch.StartNew();
        Exception? firstFailure = null;
        string? firstFailurePath = null;

        // A first-run reset is protected by the same lock as pack append and
        // SQLite index commit. Never reset a pack that has live index rows.
        lock (CatalogRemovalService.CacheLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_repository.CountEntries() == 0)
                PackedThumbnailStore.ResetPackFiles();
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = _repository.GetMissingCandidates(
                profile, afterImageId, CandidateBatchSize);
            if (candidates.Count == 0) break;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                afterImageId = candidate.ImageId;
                try
                {
                    BuildCandidate(candidate, profile, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
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
                        total, processed, failed, candidate.LocalPath,
                        candidate.ThumbnailPath));
                }
                if (processed >= SystemicFailureThreshold && failed == processed)
                    throw new InvalidOperationException(
                        $"Die ersten {processed:N0} Bilder konnten sämtlich nicht in den Browse-Cache konvertiert werden.\n\n" +
                        $"Erste fehlerhafte Datei:\n{firstFailurePath}\n\n" +
                        $"Ursache:\n{firstFailure?.Message}", firstFailure);
            }
        }
        progress?.Report(new BrowseCacheBuildProgress(
            total, processed, failed, null, null));
        return new BrowseCacheBuildResult(total, processed, failed);
    }

    private void BuildCandidate(
        BrowseCacheCandidate candidate,
        BrowseThumbnailProfile profile,
        CancellationToken cancellationToken)
    {
        var missing = profile.Tiers
            .Where(tier => !candidate.ExistingTiers.Contains(tier)).ToArray();

        // Open and close a pack writer for THIS candidate only. Holding one
        // handle across hundreds of images would allow a compactor to replace
        // a pack while its old stream is still being appended to.
        using var store = new PackedThumbnailStore();
        var preparedLegacy = Array.Empty<PackedThumbnailStore.PreparedThumbnailTier>()
            .AsEnumerable();
        var preparedOriginal = Array.Empty<PackedThumbnailStore.PreparedThumbnailTier>()
            .AsEnumerable();

        if (missing.Length > 0)
        {
            var available = File.Exists(candidate.ThumbnailPath);
            var legacyTiers = available
                ? missing.Where(tier => tier <= LegacyThumbnailMaximumTier).ToArray()
                : Array.Empty<int>();
            var originalTiers = missing
                .Where(tier => !legacyTiers.Contains(tier)).ToArray();
            if (legacyTiers.Length > 0)
                preparedLegacy = store.PrepareTiers(candidate.ThumbnailPath,
                    new BrowseThumbnailProfile(legacyTiers), cancellationToken);
            if (originalTiers.Length > 0)
            {
                if (!File.Exists(candidate.LocalPath))
                    throw new FileNotFoundException(
                        "Original für die benötigte Browse-Thumbnail-Größe ist nicht verfügbar.",
                        candidate.LocalPath);
                var displayPath = RawImageSource.Resolve(candidate.LocalPath,
                    cancellationToken);
                preparedOriginal = store.PrepareTiers(displayPath,
                    new BrowseThumbnailProfile(originalTiers), cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (CatalogRemovalService.CacheLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The connection has no open write transaction during slow
            // decoding; every candidate is one short, atomic index commit.
            using var writeSession = _repository.CreateWriteSession();
            try
            {
                foreach (var loc in store.AppendPrepared(preparedLegacy.ToArray(), cancellationToken))
                    writeSession.Save(candidate.ImageId, candidate.ModifiedUtcTicks, loc);
                foreach (var loc in store.AppendPrepared(preparedOriginal.ToArray(), cancellationToken))
                    writeSession.Save(candidate.ImageId, candidate.ModifiedUtcTicks, loc);
                writeSession.MarkComplete(candidate.ImageId, candidate.ModifiedUtcTicks, profile);
                cancellationToken.ThrowIfCancellationRequested();
                writeSession.Commit();
                store.Dispose();
            }
            catch
            {
                writeSession.RollbackPending();
                throw;
            }
        }
    }
}
