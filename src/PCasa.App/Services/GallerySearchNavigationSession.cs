namespace PCasa.App.Services;

/// <summary>
/// The original gallery position and folder-tree focus form a single return
/// snapshot. Search-results navigation must not overwrite this snapshot.
/// </summary>
public sealed class GallerySearchNavigationSession
{
    public readonly record struct Snapshot(
        (long ImageId, double RelativeY)? ImageAnchor,
        long? ActivePhotoFolderId,
        string? SelectedTreePath);

    private Snapshot? _original;

    public bool IsActive => _original.HasValue;

    public void Begin(Snapshot state)
    {
        _original ??= state;
    }

    public Snapshot? End()
    {
        var original = _original;
        _original = null;
        return original;
    }
}
