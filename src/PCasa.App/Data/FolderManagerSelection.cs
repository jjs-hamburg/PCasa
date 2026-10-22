namespace PCasa.App.Data;

// Entire folder-manager choice, not a sequence of live checkbox edits.
// The dialog creates this immutable snapshot; the catalog applies it as one
// scheduler-owned transaction only after the user confirms OK.
public sealed record FolderManagerSelection(
    IReadOnlyList<FolderManagerRootChoice> Roots,
    bool BackgroundScanEnabled);

public sealed record FolderManagerRootChoice(
    string LocalPath,
    IReadOnlyList<string> ExcludedPaths,
    bool ServerSyncEnabled);
