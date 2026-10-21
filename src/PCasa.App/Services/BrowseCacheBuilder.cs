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
        lock (CatalogRemovalService.CacheLock)
        {
            if (_repository.CountEntries() == 0)
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
        var missing = profile.Tiers
            .Where(tier => !candidate.ExistingTiers.Contains(tier)).ToArray();
        if (missing.Length == 0)
        {
            lock (CatalogRemovalService.CacheLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writeSession.MarkComplete(candidate.ImageId, candidate.ModifiedUtcTicks, profile);
                writeSession.Commit();
            }
            return;
        }

        // Encode outside the pack lock, including potentially blocking SMB IO.
        var available = File.Exists(candidate.ThumbnailPath);
        var legacyTiers = available
            ? missing.Where(tier => tier <= LegacyThumbnailMaximumTier).ToArray()
            : Array.Empty<int>();
        var originalTiers = missing
            .Where(tier => !legacyTiers.Contains(tier)).ToArray();
        var preparedLegacy = legacyTiers.Length > 0
            ? store.PrepareTiers(candidate.ThumbnailPath,
                new BrowseThumbnailProfile(legacyTiers), cancellationToken)
            : Array.Empty<PackedThumbnailStore.PreparedThumbnailTier>();
        if (originalTiers.Length > 0 && !File.Exists(candidate.LocalPath))
            throw new FileNotFoundException(
                "Original für die benötigte Browse-Thumbnail-Größe ist nicht verfügbar.",
                candidate.LocalPath);
        var preparedOriginal = originalTiers.Length > 0
            ? store.PrepareTiers(candidate.LocalPath,
                new BrowseThumbnailProfile(originalTiers), cancellationToken)
            : Array.Empty<PackedThumbnailStore.PreparedThumbnailTier>();

        lock (CatalogRemovalService.CacheLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var location in store.AppendPrepared(preparedLegacy, cancellationToken))
                writeSession.Save(candidate.ImageId, candidate.ModifiedUtcTicks, location);
            foreach (var location in store.AppendPrepared(preparedOriginal, cancellationToken))
                writeSession.Save(candidate.ImageId, candidate.ModifiedUtcTicks, location);
            writeSession.MarkComplete(candidate.ImageId, candidate.ModifiedUtcTicks, profile);
            // A pack cannot be considered disposable until its index rows
            // have committed; keep these two short operations in one lock.
            writeSession.Commit();
        }
    }
}
