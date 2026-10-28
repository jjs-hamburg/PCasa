using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCasa.App.Models;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _manualScanRunning;

    private void FoldersTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not TreeViewItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private bool _manualContextScanActive;
    private int _manualContextScanCount;
    private CancellationTokenSource? _manualContextCancellation;

    private async void BrowseScanCurrentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_folderRemovalPending)
        {
            StatusText.Text = "Ordner wird gerade entfernt – Scan nicht gestartet.";
            return;
        }

        if (FoldersTree.SelectedItem is not TreeViewItem item ||
            item.Tag is not FolderTreeTag tag)
        {
            StatusText.Text = "Bitte zuerst den einzulesenden Ordner auswählen.";
            return;
        }

        // No existence probe, source-generation update or scan is performed
        // outside the scheduler. Even an unreachable SMB path is checked by
        // the priority worker, not by the UI event handler.
        var directory = Path.GetFullPath(tag.FullPath).Replace('/', '\\');
        // Manual requests form a LIFO stack. Do NOT cancel the predecessor:
        // its scanner cursor and progress remain valid for later resumption.
        _manualContextScanCount++;
        _manualContextScanActive = true;
        _manualScanRunning = true;
        _manualFolderScanWaiting = true;
        // Manual work preempts through the scheduler yield token. Do not
        // permanently cancel and dispose the background scanner's cursor.
        _cacheMaintenanceCancellation?.Cancel();
        _browseCacheCancellation?.Cancel();
        _isScanning = true;
        UpdateManualScanControls();

        using var cancellation = new CancellationTokenSource();
        _manualContextCancellation = cancellation;
        _activeScanCancellation = cancellation;
        _activeScanFolderId = null;
        // Requesting work is not the same as executing it. A newer P0
        // package may still be ahead of this scan on the LIFO stack.
        TrackFolderActivity(directory, "Pending", recursive: true);
        RefreshFolderStatusVisuals();

        var operationKey = $"catalog:complete-scan:{Guid.NewGuid():N}:{directory}";
        BeginVisibleManualOperation(operationKey, "Ordner aktualisieren", 0);
        StatusText.Text = $"Manueller Scan angefordert (höchste Priorität): {directory}";
        var scanActive = false;
        var progress = new Progress<ScanProgress>(scan =>
        {
            if (cancellation.IsCancellationRequested || !scanActive) return;
            if (_folderScanStatuses.Get(directory)?.Code != "Working")
            {
                TrackFolderActivity(directory, "Working", recursive: true);
                RefreshFolderStatusVisuals();
            }
            ShowScannedPhotoFolder(scan);
            StatusText.Text =
                $"Ordner aktualisieren: {scan.ImageFilesSeen:N0} geprüft · " +
                $"{scan.Added:N0} neu · {scan.Changed:N0} geändert · " +
                $"{scan.CacheRepaired:N0} Cache · {scan.Failed:N0} Fehler";
        });

        try
        {
            // Explorer-originated manual scanning may register a previously
            // inactive folder, but ONLY through Ordnermanager's transactional
            // FolderManagerSelectionService.Apply. The scheduler owns that
            // root transition and the subsequent durable manual scan.
            bool newRoot = false;
            bool acknowledgeTargetedPending = false;
            long? expectedRootId = null;
            if (_allFolderProjectionRootPath is not null)
            {
                // The *persisted* catalog selection is authoritative here.
                // Never decide a root's activation from a stale UI snapshot.
                var registration = await _scheduler.RunAsync(
                    "folder-manager:explorer-register:" + Guid.NewGuid().ToString("N"),
                    SchedulerPriority.ManualAction,
                    token =>
                    {
                        var registration = new FolderManagerSelectionService(_database)
                            .EnsureMonitoredFolder(directory, token);
                        var snapshot = ReadFolderTreeConfigurationSnapshot();
                        var root = FolderExclusionRules.FindOwner(directory,
                            snapshot.Select(candidate => candidate.Folder))
                            ?? throw new InvalidOperationException(
                                "Der Ordner konnte nicht als Bildordner registriert werden.");
                        return (registration.Changed, registration.CreatedRoot,
                            registration.AcknowledgeTargetedPending,
                            RootId: root.Id, Snapshot: snapshot);
                    }, cancellationToken: cancellation.Token,
                    workKind: SchedulerWorkKind.Catalog);

                newRoot = registration.CreatedRoot;
                acknowledgeTargetedPending = registration.AcknowledgeTargetedPending;
                expectedRootId = registration.RootId;
                if (registration.Changed)
                {
                    _dropTargetProjection = registration.Snapshot;
                    _dragTargetValidity.Clear();
                    // Newly activated subfolders also become permanent
                    // catalog entries after the scan; never hide them when
                    // Explorer Ansicht is turned off.
                    PromoteExplorerMonitoredPath(directory);
                    StatusText.Text = newRoot
                        ? "Neuen Bildordner registriert · Manueller Scan gestartet"
                        : "Unterordner im Ordnermanager aktiviert · Manueller Scan gestartet";
                }
            }
            else
            {
                var owner = DisplayedMonitoredRoot(directory);
                if (owner is null ||
                    FolderExclusionRules.IsExcluded(directory,
                        DisplayedExcludedPaths(owner.Id)))
                    throw new InvalidOperationException(
                        "Dieser Ordner muss zuerst im Ordnermanager ausgewählt werden.");
            }

            scanActive = true;
            var result = await ScheduleCompleteFolderScanAsync(
                directory, SchedulerPriority.ManualAction, progress, cancellation.Token,
                expectedMonitoredFolderId: expectedRootId,
                requirePendingInitialImport: newRoot,
                operationKey: operationKey,
                acknowledgeTargetedPending: acknowledgeTargetedPending);
            scanActive = false;
            TrackFolderActivity(directory,
                result.Failed > 0 ? "Failed" : "Ready", recursive: true);
            RefreshFolderStatusVisuals();
            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                warmViewport: true);
            // Explorer must not rebuild its one existing TreeView, even after
            // a newly registered folder commits images to the catalog.
            if (_allFolderProjectionRootPath is null)
                RefreshFolderTree();
            else
            {
                RefreshExplorerIndexedFolders();
                if (FoldersTree.SelectedItem is TreeViewItem
                    { Tag: FolderTreeTag selected } &&
                    string.Equals(selected.FullPath, directory,
                        StringComparison.OrdinalIgnoreCase) &&
                    selected.PhotoFolderId is long newFolderId)
                    await NavigateToPhotoFolderAsync(
                        (TreeViewItem)FoldersTree.SelectedItem, newFolderId);
            }
            StatusText.Text = result.Failed == 0
                ? $"Scan abgeschlossen: {result.Updated:N0} aktualisiert · " +
                  $"{result.Removed:N0} verwaiste Einträge entfernt"
                : $"Scan unvollständig: {result.Failed:N0} Fehler; " +
                  "verwaiste Einträge wurden aus Sicherheitsgründen nicht gelöscht.";
        }
        catch (OperationCanceledException)
        {
            scanActive = false;
            TrackFolderActivity(directory, "Pending", recursive: true);
            RefreshFolderStatusVisuals();
            StatusText.Text = $"Manueller Scan abgebrochen: {directory}";
        }
        catch (Exception ex)
        {
            scanActive = false;
            TrackFolderActivity(directory, "Failed", recursive: true);
            RefreshFolderStatusVisuals();
            PCasaDiagnostics.Write("Catalog.ManualFolderScan", ex);
            MessageBox.Show(this, $"Der Ordner konnte nicht eingelesen werden.\n\n{ex.Message}",
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndVisibleManualOperation(operationKey);
            if (ReferenceEquals(_activeScanCancellation, cancellation))
            {
                _activeScanCancellation = null;
                _activeScanFolderId = null;
            }
            // Only the last outstanding context scan may clear the busy
            // flags. A newer scan may finish while this older scan is paused.
            _manualContextScanCount--;
            if (ReferenceEquals(_manualContextCancellation, cancellation))
                _manualContextCancellation = null;
            _manualContextScanActive = _manualContextScanCount > 0;
            _manualScanRunning = _manualContextScanActive ||
                _initialScanActive || _manualScanAllActive;
            _manualFolderScanWaiting = _manualScanRunning;
            _isScanning = _backgroundWorkerRunning || _manualScanRunning;
            UpdateManualScanControls();
            QueuePendingInitialScans();
        }
    }

    private sealed record ManualScanRecipe(string Directory,
        long? MonitoredRootId, bool RequirePendingInitialImport = false,
        bool AcknowledgeTargetedPending = false);

    private SchedulerTaskPackage BuildManualFolderScanPackage(string key, string payload)
    {
        var recipe = System.Text.Json.JsonSerializer.Deserialize<ManualScanRecipe>(payload)
            ?? throw new InvalidDataException("Ungültiger manueller Scan-Auftrag.");
        var step = new CompleteFolderScanStep(this, recipe.Directory,
            new ScanJobProgress(_scheduler, key, null), recipe.MonitoredRootId,
            recipe.RequirePendingInitialImport,
            recipe.AcknowledgeTargetedPending);
        var package = new SchedulerTaskPackage(key);
        package.AddStep(async token =>
        {
            try { await step.ExecuteAsync(token); }
            catch (OperationCanceledException) { throw; }
            finally
            {
                // Do not discard the in-memory scan enumerator on a yield.
                if (!token.IsCancellationRequested) step.Dispose();
            }
        });
        return package;
    }

    // One shared entrance for every complete user-initiated directory scan.
    // The scheduler owns validation, generation advancement, scan, thumbnail
    // indexing and reconciliation; the entry points only present UI progress.
    private async Task<ScanResult> ScheduleCompleteFolderScanAsync(
        string directory, SchedulerPriority priority,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken,
        long? expectedMonitoredFolderId = null,
        bool requirePendingInitialImport = false,
        string? operationKey = null,
        bool acknowledgeTargetedPending = false)
    {
        var key = operationKey ?? $"catalog:complete-scan:{Guid.NewGuid():N}:{directory}";
        var schedulerProgress = new ScanJobProgress(_scheduler, key, progress);
        using var step = new CompleteFolderScanStep(this, directory,
            schedulerProgress, expectedMonitoredFolderId, requirePendingInitialImport,
            acknowledgeTargetedPending);
        if (priority == SchedulerPriority.ManualAction)
        {
            var package = new SchedulerTaskPackage(key);
            package.AddStep(step.ExecuteAsync);
            var payload = System.Text.Json.JsonSerializer.Serialize(
                new ManualScanRecipe(directory, expectedMonitoredFolderId,
                    requirePendingInitialImport, acknowledgeTargetedPending));
            await _scheduler.EnqueueDurablePackageAsync(package,
                "manual-folder-scan-v1", payload, priority,
                SchedulerWorkKind.CatalogScan, cancellationToken);
        }
        else
        {
            await _scheduler.EnqueueAsync(key, priority, step.ExecuteAsync,
                cancellationToken: cancellationToken,
                workKind: requirePendingInitialImport &&
                    priority == SchedulerPriority.Background
                    ? SchedulerWorkKind.InitialImport
                    : SchedulerWorkKind.CatalogScan);
        }
        return step.Result ?? throw new OperationCanceledException();
    }

    // All complete scans use this ONE scanner step: explicit one-folder,
    // Scan All, and first import. Its iterator and generation checkpoint
    // belong to the step for the entire lifetime of its parent package.
    private sealed class CompleteFolderScanStep(
        MainWindow owner, string directory, IProgress<ScanProgress>? progress,
        long? expectedMonitoredFolderId = null,
        bool requirePendingInitialImport = false,
        bool acknowledgeTargetedPending = false) : IDisposable
    {
        private readonly FolderScanner.ScanResumeState _resume = new();
        private bool _manualGenerationStarted;
        private long? _manualScanEpoch;
        private long? _initialSourceGeneration;
        private string? _sourceCanonicalPath;

        public ScanResult? Result { get; private set; }
        public bool SourceChanged => _initialSourceGeneration is long earlier &&
            _sourceCanonicalPath is string path &&
            !owner._folderRepository.IsCurrentSourceGeneration(path, earlier, true);

        public Task ExecuteAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var folder = owner.ResolveMonitoredFolder(directory)
                ?? throw new InvalidOperationException(
                    "Dieser Ordner liegt außerhalb der PCasa-Überwachung.");
            if (expectedMonitoredFolderId is long expected && folder.Id != expected)
                throw new OperationCanceledException(
                    "Ein inzwischen ersetzter Überwachungsordner darf nicht gescannt werden.");
            var currentSource = owner._folderRepository.GetSourceState(folder.CanonicalPath);
            if (currentSource is not { DesiredPresent: true } ||
                (_initialSourceGeneration is long previous &&
                 previous != currentSource.Value.Generation))
                throw new OperationCanceledException(
                    "Die Ordnerauswahl wurde geändert; alte Bestandsaufnahme verworfen.");
            _initialSourceGeneration ??= currentSource.Value.Generation;
            _sourceCanonicalPath ??= folder.CanonicalPath;
            if (requirePendingInitialImport &&
                !owner._folderRepository.GetPendingInitialScans().Any(candidate =>
                    candidate.Id == folder.Id))
                throw new OperationCanceledException(
                    "Der Erstimport wurde bereits durch einen neueren Scan erledigt.");
            if (FolderExclusionRules.IsExcluded(directory,
                owner._folderRepository.GetExcludedPaths(folder.Id)))
                throw new InvalidOperationException(
                    "Dieser Ordner ist im Ordnermanager ausgeschlossen.");
            if (!owner._scheduler.SourceAvailability.CanAccess(directory, token))
                throw new DurablePackageRetryException(
                    "Quelle vorübergehend offline; später erneut prüfen: " +
                    SourceAvailabilityGate.RootOf(directory));
            if (!Directory.Exists(directory))
            {
                owner._scheduler.SourceAvailability.CheckAfterSourceError(
                    directory, token);
                throw new DirectoryNotFoundException(
                    $"Der Ordner ist nicht erreichbar: {directory}");
            }
            if (!_manualGenerationStarted)
            {
                // An automatic first import must retain its durable scan epoch
                // after restart; explicit manual rescans still supersede it.
                if (!requirePendingInitialImport)
                    _manualScanEpoch =
                        owner._folderRepository.BeginManualScan(folder.CanonicalPath);
                _manualGenerationStarted = true;
            }

            // No duplicate import or cleanup path. The scanner owns all EXIF,
            // cache, catalog, and safe reconciliation transitions.
            try
            {
                Result = owner._folderScanner.ScanDirectory(
                    folder, directory, recursive: true, progress, token, _resume,
                    durableInitialImport: requirePendingInitialImport);
                owner._scheduler.SourceAvailability.ConfirmAvailable(directory);
            }
            catch (IOException)
            {
                owner._scheduler.SourceAvailability.CheckAfterSourceError(
                    directory, token);
                throw;
            }
            if (Result.Failed == 0 && acknowledgeTargetedPending &&
                _initialSourceGeneration is long expectedGeneration &&
                _manualScanEpoch is long scanEpoch)
            {
                token.ThrowIfCancellationRequested();
                // Only the pending obligation created by reactivating this
                // child can be acknowledged. Generation and epoch CAS keep
                // older packets from erasing a newer root's pending work.
                owner._folderRepository.CompleteInitialScan(
                    folder.Id, expectedGeneration, scanEpoch);
            }
            if (Result.Failed == 0 && !requirePendingInitialImport)
            {
                var noLongerExisting = owner._folderRepository
                    .PruneMissingCreatedDirectories(folder.Id, directory, token);
                if (noLongerExisting.Count > 0 && !owner.Dispatcher.HasShutdownStarted)
                {
                    // Tree changes are a projection of the scanner's confirmed
                    // physical inventory, not a second filesystem job.
                    _ = owner.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        foreach (var path in noLongerExisting)
                            owner._createdEmptyFolderPaths.Remove(path);
                        owner.RefreshFolderTree(preserveGalleryAnchor: true);
                    }));
                }
            }
            return Task.CompletedTask;
        }

        public void Dispose() => _resume.Dispose();
    }

    // A synchronous progress relay: scheduler state is committed on the
    // worker before posting any optional UI presentation callback.
    private sealed class ScanJobProgress(
        PCasaScheduler scheduler, string key, IProgress<ScanProgress>? display)
        : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            scheduler.ReportProgress(key, "Bilder einlesen",
                value.ImageFilesSeen, 0, value.CurrentFile);
            display?.Report(value);
        }
    }

    private void UpdateManualScanControls()
        => ScanAllMenuItem.IsEnabled = !_manualScanAllActive;

    private MonitoredFolder? ResolveMonitoredFolder(string path)
    {
        var normalized = Path.GetFullPath(path)
            .Replace('/', '\\')
            .TrimEnd('\\');

        return FolderExclusionRules.FindOwner(
            normalized, _folderRepository.GetAll());
    }

    private static bool IsInside(string normalizedPath, string rootPath)
    {
        var root = Path.GetFullPath(rootPath)
            .Replace('/', '\\')
            .TrimEnd('\\');

        return string.Equals(normalizedPath, root, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
