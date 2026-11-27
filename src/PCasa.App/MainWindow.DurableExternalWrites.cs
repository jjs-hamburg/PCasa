using System.IO;
using System.Text.Json;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private const string ExternalCommitType = "external-commit-v1";
    private const string ExternalBatchCommitType = "external-batch-commit-v1";

    private bool HasPendingExternalWrites()
        => new DurablePackageJournal(_database).Pending().Any(row =>
            row.Type is ExternalCommitType or ExternalBatchCommitType);

    private T ReadWriteRecipe<T>(string key) where T : class
    {
        using var db = _database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT payload_json FROM scheduler_packages WHERE package_key=$key";
        command.Parameters.AddWithValue("$key", key);
        var payload = command.ExecuteScalar() as string
            ?? throw new IOException("Accepted write recipe is missing.");
        return JsonSerializer.Deserialize<T>(payload)
            ?? throw new IOException("Accepted write recipe is invalid.");
    }

    private sealed record ExternalCommitRecipe(long ImageId, ExternalEditSnapshot Snapshot,
        ExternalEditDecision Decision, string ResultPath, long ResultSize,
        DateTime ResultWriteUtc, PhotoEditStep[] InternalSteps,
        string? StagedPath = null, string? Target = null, string? CandidateHash = null);

    private SchedulerTaskPackage BuildExternalCommitPackage(string key, string payload)
    {
        var request = JsonSerializer.Deserialize<ExternalCommitRecipe>(payload)
            ?? throw new InvalidDataException("Invalid external edit recipe.");
        var package = new SchedulerTaskPackage(key);
        package.AddStep(token =>
        {
            token.ThrowIfCancellationRequested();
            _scheduler.ReportProgress(key, UiLanguage.T("Externe Bearbeitung"), 0, 100,
                request.Snapshot.OriginalPath);
            if (request.Target is { } published && request.CandidateHash is { } hash &&
                SourceFilePresence.Probe(published) == FilePresence.Present &&
                WriteHash(published) == hash)
            {
                ExternalEditTransaction.FinishPublished(request.Snapshot);
            }
            else
            {
                if (request.StagedPath is null)
                {
                    var result = new FileInfo(request.ResultPath);
                    if (!result.Exists || result.Length != request.ResultSize ||
                        result.LastWriteTimeUtc != request.ResultWriteUtc)
                        throw new IOException("Das externe Ergebnis wurde nach der internen Bearbeitung geändert. Bitte Ergebnis erneut prüfen.");
                    var directory = Path.Combine(request.Snapshot.DirectoryPath, ".pcasa-internal");
                    var staged = Path.Combine(directory, "result" + result.Extension.ToLowerInvariant());
                    if (!string.Equals(staged, request.ResultPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(directory);
                        // Only our private staging path is disposable; the
                        // editor's exact confirmed result remains untouched.
                        if (File.Exists(staged)) File.Delete(staged);
                        if (request.InternalSteps.Length > 0)
                            staged = JpegCropWriter.EditExternalWorkspaceResult(
                                request.ResultPath, request.Snapshot.DirectoryPath,
                                request.InternalSteps, token);
                        else
                        {
                            using var input = new FileStream(request.ResultPath, FileMode.Open,
                                FileAccess.Read, FileShare.Read);
                            using var output = new FileStream(staged, FileMode.CreateNew,
                                FileAccess.Write, FileShare.None, 131072, FileOptions.WriteThrough);
                            input.CopyTo(output);
                            output.Flush(true);
                        }
                    }
                    request = request with { StagedPath = staged };
                    PersistWriteRecipe(key, request);
                }
                var committed = ExternalEditTransaction.Commit(request.Snapshot,
                    request.StagedPath, request.Decision, token,
                    (done, total) => _scheduler.ReportProgress(key,
                        UiLanguage.T("Externe Bearbeitung"),
                        total <= 0 ? 100 : (int)(done * 100 / total), 100,
                        request.Snapshot.OriginalPath),
                    beforePublish: (target, candidate) =>
                    {
                        if (request.Target is not null &&
                            !string.Equals(request.Target, target, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("External result target changed during recovery.");
                        request = request with { Target = target, CandidateHash = candidate };
                        PersistWriteRecipe(key, request);
                    });
                if (committed is null || request.Target is null)
                    throw new IOException("External edit did not publish its accepted result.");
            }
            _scheduler.ReportProgress(key, UiLanguage.T("Externe Bearbeitung"), 100, 100,
                request.Target);
            return Task.CompletedTask;
        });
        package.AddStep(token =>
        {
            if (request.Target is not { } target)
                throw new IOException("External result receipt is incomplete.");
            IndexAcceptedExternalResult(target, token);
            return Task.CompletedTask;
        });
        return package;
    }

    private void IndexAcceptedExternalResult(string path, CancellationToken token)
    {
        var root = FolderExclusionRules.FindOwner(path, _folderRepository.GetAll())
            ?? throw new IOException("Ausgabeverzeichnis nicht im Katalog: " + path);
        if (CatalogImageIdAtPath(path) is long existing)
            new BrowseCacheRepository(_database).InvalidateImage(existing);
        var result = _folderScanner.ScanFile(root, path,
            cancellationToken: token, publishCatalogChange: false, forceExifRefresh: true);
        if (result.Failed > 0 || result.ImageFilesSeen == 0 ||
            CatalogImageIdAtPath(path) is not long imageId)
            throw new IOException("Gespeichert, aber Katalogisierung fehlgeschlagen: " + path);
        new BrowseCacheBuilder(new BrowseCacheRepository(_database),
            new BrowseThumbnailProfileRepository(_database)).RebuildSingleImage(imageId, token);
    }

    private async Task ObserveRecoveredExternalWriteAsync(Task task, string key)
    {
        try
        {
            await task;
            if (_shutdownStarted || Dispatcher.HasShutdownStarted) return;
            ResumeSchedulerAfterExternalEdit();
            if (_externalEditReview is { } review && key.StartsWith("external:commit:", StringComparison.Ordinal))
            {
                var request = ReadWriteRecipe<ExternalCommitRecipe>(key);
                if (request.Snapshot.Id == review.Snapshot.Id) _externalEditReview = null;
            }
            _viewerOriginalCache.Clear();
            await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false);
            _ = RefreshRestoreBadgesAsync();
            UpdateEditControls();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PCasaDiagnostics.Write("ExternalEdit.Recovery", ex); }
        finally
        {
            if (!_shutdownStarted && !Dispatcher.HasShutdownStarted)
                EndVisibleManualOperation(key);
        }
    }
}
