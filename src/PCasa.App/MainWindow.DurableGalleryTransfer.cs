using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private sealed record DurableGalleryTransferRecipe(
        string[] Sources, string Destination, bool Move);

    // This factory is called both for new manual drops and, with exactly the
    // same fixed step layout, by the scheduler after an application restart.
    private SchedulerTaskPackage BuildDurableGalleryTransferPackage(
        string key, string payload)
        => BuildDurableGalleryTransferPackage(key, payload, recovered: true);

    private SchedulerTaskPackage BuildDurableGalleryTransferPackage(
        string key, string payload, bool recovered)
    {
        var recipe = JsonSerializer.Deserialize<DurableGalleryTransferRecipe>(payload)
            ?? throw new InvalidDataException("Ungültiges Dateiübertragungspaket.");
        if (recipe.Sources is null || recipe.Sources.Length == 0 ||
            string.IsNullOrWhiteSpace(recipe.Destination))
            throw new InvalidDataException("Leeres Dateiübertragungspaket.");
        var journal = new DurableGalleryTransferJournal(_database);
        var package = new SchedulerTaskPackage(key);
        var phase = recipe.Move ? "Bilder verschieben" : "Bilder kopieren";
        var fencedEpochs = new Dictionary<string, long>(
            StringComparer.OrdinalIgnoreCase);

        MonitoredFolder OwnerOf(string target)
        {
            var owner = FolderExclusionRules.FindOwner(
                target, _folderRepository.GetAll());
            if (owner is null || !IsMonitoredGalleryDestination(target))
                throw new IOException(
                    "Ziel ist nicht mehr Teil eines überwachten Ordners: " + target);
            return owner;
        }

        void FenceInventory(MonitoredFolder root)
        {
            var epoch = _folderRepository.GetScanEpoch(root.CanonicalPath);
            if (fencedEpochs.TryGetValue(root.CanonicalPath, out var previous) &&
                previous == epoch) return;
            fencedEpochs[root.CanonicalPath] =
                _folderRepository.BeginManualScan(root.CanonicalPath);
        }

        package.AddStep(token =>
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(recipe.Destination))
                throw new IOException("Zielordner nicht erreichbar: " + recipe.Destination);
            _ = OwnerOf(recipe.Destination);
            _scheduler.ReportProgress(key, phase, 0, recipe.Sources.Length);
            return Task.CompletedTask;
        });

        for (var index = 0; index < recipe.Sources.Length; index++)
        {
            var ordinal = index;
            var source = recipe.Sources[ordinal];

            // Step A: persist the chosen physical destination BEFORE mutation,
            // and acknowledge the physical result before the next step.
            package.AddStep(async token =>
            {
                token.ThrowIfCancellationRequested();
                var receipt = journal.Get(key, ordinal);
                if (receipt is null)
                {
                    var proposedDestination = Path.Combine(
                        recipe.Destination, Path.GetFileName(source));
                    var destination = proposedDestination;
                    var replace = false;
                    var reserved = journal.ReservedDestinations(key);
                    var samePath = string.Equals(
                        source, proposedDestination, StringComparison.OrdinalIgnoreCase);
                    if (samePath && recipe.Move)
                    {
                        journal.Prepare(key, ordinal, new GalleryTransferReceipt(
                            source, destination, recipe.Move, false, 0,
                            null, null, null, "skipped"));
                        return;
                    }
                    if (reserved.Contains(destination))
                        destination = UniqueCopyPath(destination, reserved);
                    else if (samePath || File.Exists(destination))
                    {
                        var choice = samePath ? NameConflict.KeepBoth :
                            await Dispatcher.InvokeAsync(
                                () => ResolveFileConflict(proposedDestination));
                        token.ThrowIfCancellationRequested();
                        if (choice == NameConflict.Cancel)
                            throw new OperationCanceledException(
                                "Dateiübertragung auf Benutzerwunsch abgebrochen.");
                        if (choice == NameConflict.Skip)
                        {
                            journal.Prepare(key, ordinal, new GalleryTransferReceipt(
                                source, destination, recipe.Move, false, 0,
                                null, null, null, "skipped"));
                            return;
                        }
                        if (choice == NameConflict.Replace && !samePath)
                            replace = true;
                        else destination = UniqueCopyPath(destination, reserved);
                    }

                    // Identity and root decisions belong to the same physical
                    // file gate. No second worker is allowed to change paths
                    // while we materialize the receipt.
                    using (SourceFileLocks.EnterPair(source, destination, token))
                    {
                        token.ThrowIfCancellationRequested();
                        var root = OwnerOf(destination);
                        var fromId = recipe.Move ? CatalogImageIdAtPath(source) : null;
                        var targetId = replace ? CatalogImageIdAtPath(destination) : null;
                        // This stable ID owns the EXISTING relocation intent,
                        // rather than spawning a second recovery subsystem.
                        var intentId = recipe.Move && fromId.HasValue
                            ? "gallery-transfer:" + key + ":" + ordinal
                            : null;
                        receipt = journal.Prepare(key, ordinal,
                            new GalleryTransferReceipt(source, destination,
                                recipe.Move, replace, root.Id, fromId, targetId,
                                intentId, "planned"));
                    }
                }

                if (receipt.Phase is "skipped" or "indexed" or "materialized")
                {
                    _scheduler.ReportProgress(key, phase, ordinal + 1,
                        recipe.Sources.Length, receipt.Destination);
                    return;
                }

                using (SourceFileLocks.EnterPair(
                    receipt.Source, receipt.Destination, token))
                {
                    token.ThrowIfCancellationRequested();
                    var owner = OwnerOf(receipt.Destination);
                    if (owner.Id != receipt.TargetRootId)
                        throw new IOException(
                            "Überwachter Zielordner wurde zwischenzeitlich geändert.");
                    var initialAttempt = receipt.Phase == "planned";
                    if (initialAttempt)
                    {
                        // Fence running inventory scans for both involved roots.
                        FenceInventory(owner);
                        if (receipt.Move)
                        {
                            var sourceRoot = FolderExclusionRules.FindOwner(
                                receipt.Source, _folderRepository.GetAll());
                            if (sourceRoot is not null) FenceInventory(sourceRoot);
                        }
                        // This intent precedes any physical move.
                        journal.EnsureRelocationIntent(receipt);
                        journal.Advance(key, ordinal, "planned", "executing");
                    }
                    else if (receipt.Phase != "executing")
                        throw new InvalidOperationException(
                            "Unerwarteter Datei-Checkpoint: " + receipt.Phase);

                    var sourcePresence = SourceFilePresence.Probe(receipt.Source);
                    var destinationPresence =
                        SourceFilePresence.Probe(receipt.Destination);
                    if (sourcePresence == FilePresence.Unknown ||
                        destinationPresence == FilePresence.Unknown)
                        throw new IOException(
                            "Dateizustand unklar (NAS/VPN offline). Keine Wiederholung: " +
                            receipt.Source);

                    if (!initialAttempt &&
                        sourcePresence == FilePresence.ConfirmedMissing &&
                        destinationPresence == FilePresence.Present &&
                        receipt.Move)
                    {
                        // A move completed immediately before process death.
                        // The next fixed step will reconcile its existing intent.
                        journal.Advance(key, ordinal, "executing", "materialized");
                    }
                    else if (!initialAttempt &&
                        !receipt.Move &&
                        sourcePresence == FilePresence.Present &&
                        destinationPresence == FilePresence.Present &&
                        FilesHaveEqualContent(receipt.Source, receipt.Destination, token))
                    {
                        // An atomic staged copy reached its final destination
                        // but died before SQLite acknowledged it. Never copy twice.
                        journal.Advance(key, ordinal, "executing", "materialized");
                    }
                    else
                    {
                        if (sourcePresence != FilePresence.Present)
                            throw new IOException(
                                "Quelldatei nicht erreichbar: " + receipt.Source);
                        if (destinationPresence == FilePresence.Present &&
                            (!initialAttempt || !receipt.Replace))
                            throw new IOException(
                                "Zielzustand nach Unterbrechung nicht eindeutig. " +
                                "Keine automatische Überschreibung: " +
                                receipt.Destination);

                        if (receipt.Move)
                            _ = ServerSideFileMover.Move(
                                receipt.Source, receipt.Destination,
                                receipt.Replace);
                        else
                            StageCopyToDestination(
                                key, ordinal, receipt, token);
                        // Cancellation is checked only AFTER this receipt:
                        // a completed filesystem change must not be forgotten.
                        journal.Advance(key, ordinal, "executing", "materialized");
                    }
                }
                _scheduler.ReportProgress(key, phase, ordinal + 1,
                    recipe.Sources.Length, receipt.Destination);
            });

            // Step B: catalog reconciliation; idempotent when the SQLite
            // relocation committed before its scheduler cursor was saved.
            package.AddStep(token =>
            {
                token.ThrowIfCancellationRequested();
                var receipt = journal.Get(key, ordinal) ??
                    throw new IOException("Datei-Checkpoint fehlt: " + ordinal);
                if (receipt.Phase is "skipped" or "indexed")
                    return Task.CompletedTask;
                if (receipt.Phase != "materialized")
                    throw new IOException(
                        "Datei wurde nicht bestätigt: " + receipt.Destination);
                var root = OwnerOf(receipt.Destination);
                if (root.Id != receipt.TargetRootId)
                    throw new IOException("Zielwurzel geändert; Katalog nicht aktualisiert.");
                if (SourceFilePresence.Probe(receipt.Destination) != FilePresence.Present)
                    throw new IOException(
                        "Zieldatei nicht sicher erreichbar: " + receipt.Destination);

                using (SourceFileLocks.EnterPair(
                    receipt.Source, receipt.Destination, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (receipt.Move && receipt.SourceImageId is long imageId)
                    {
                        if (CatalogImageIdAtPath(receipt.Destination) != imageId &&
                            !_imageCatalog.RelocateImage(imageId,
                                receipt.Source, receipt.Destination, root,
                                CancellationToken.None, receipt.RelocationId) &&
                            CatalogImageIdAtPath(receipt.Destination) != imageId)
                            throw new IOException(
                                "Verschiebung nicht im Katalog bestätigt: " +
                                receipt.Destination);
                    }
                    else
                    {
                        // Never delete a newly indexed replacement on retry:
                        // detach ONLY the originally observed destination ID.
                        if (receipt.Replace &&
                            receipt.ReplacedImageId is long oldId &&
                            CatalogImageIdAtPath(receipt.Destination) == oldId)
                            new CatalogRemovalService(_database)
                                .DetachImage(oldId, receipt.Destination);
                        var scan = _folderScanner.ScanFile(root,
                            receipt.Destination, cancellationToken: token,
                            publishCatalogChange: false);
                        if (scan.Failed > 0)
                            throw new IOException(
                                "Katalogaufnahme fehlgeschlagen: " +
                                receipt.Destination);
                    }
                    journal.Advance(key, ordinal, "materialized", "indexed");
                }
                _scheduler.ReportProgress(key, "Bild katalogisiert",
                    ordinal + 1, recipe.Sources.Length, receipt.Destination);
                return Task.CompletedTask;
            });
        }

        // Step C: publication is part of the SAME durable package, and
        // rerunning it after a crash only reloads the authoritative catalog.
        package.AddStep(async token =>
        {
            token.ThrowIfCancellationRequested();
            _scheduler.ReportProgress(key, "Galerie aktualisieren",
                recipe.Sources.Length, recipe.Sources.Length);
            await Dispatcher.InvokeAsync(async () =>
            {
                await RefreshBrowseGalleryAsync(
                    forceIndexReload: true, warmViewport: false);
                if (!recovered)
                {
                    var movedIds = journal.MovedImageIds(key);
                    _selectedImageIds.Clear();
                    _selectedImageIds.UnionWith(movedIds);
                    if (_selectedImageIds.Count == 0)
                        _gallerySelection.Reset();
                    ApplyGallerySelection();
                    var first = _displayedBrowseIndex.FirstOrDefault(
                        entry => movedIds.Contains(entry.ImageId));
                    if (first is not null)
                        ScrollGalleryImageIntoView(first.ImageId);
                }
                StatusText.Text = recipe.Sources.Length.ToString("N0") +
                    " Datei(en) verarbeitet";
            }).Task.Unwrap();
            _scheduler.ReportProgress(key, "Galerie aktualisiert",
                recipe.Sources.Length, recipe.Sources.Length);
        });
        return package;
    }

    private static bool FilesHaveEqualContent(
        string source, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (new FileInfo(source).Length != new FileInfo(destination).Length)
            return false;
        using var a = File.OpenRead(source);
        using var b = File.OpenRead(destination);
        var sourceHash = SHA256.HashData(a);
        token.ThrowIfCancellationRequested();
        var destinationHash = SHA256.HashData(b);
        token.ThrowIfCancellationRequested();
        return sourceHash.AsSpan().SequenceEqual(destinationHash);
    }

    private static void StageCopyToDestination(string key, int ordinal,
        GalleryTransferReceipt item, CancellationToken token)
    {
        // The stage belongs uniquely to this immutable package and file.
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(key + ":" + ordinal)))[..16];
        var stage = item.Destination + ".pcasa-" + fingerprint + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested();
            // A prior interrupted copy may have left OUR stage. It is never
            // mistaken for a final target; rebuild from the intact source.
            if (File.Exists(stage)) File.Delete(stage);
            File.Copy(item.Source, stage, overwrite: false);
            if (new FileInfo(item.Source).Length != new FileInfo(stage).Length)
                throw new IOException("Kopierter Datenbestand ist unvollständig.");
            token.ThrowIfCancellationRequested();
            File.Move(stage, item.Destination, overwrite: item.Replace);
        }
        finally
        {
            if (File.Exists(stage)) File.Delete(stage);
        }
    }
}
