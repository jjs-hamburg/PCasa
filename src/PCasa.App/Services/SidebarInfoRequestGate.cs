namespace PCasa.App.Services;

/// <summary>
/// One authority for deciding whether an asynchronous Info/GPS read may
/// update the sidebar. Success and failure use the same request identity.
/// </summary>
public sealed class SidebarInfoRequestGate
{
    public readonly record struct Request(int Revision, string Mode, long? ImageId);

    private int _revision;

    public Request Begin(string mode, IReadOnlyCollection<long> selectedImageIds)
    {
        ArgumentNullException.ThrowIfNull(selectedImageIds);
        var imageId = selectedImageIds.Count == 1
            ? selectedImageIds.First() : (long?)null;
        return new Request(++_revision, mode, imageId);
    }

    public void Invalidate() => ++_revision;

    public bool IsCurrent(Request request, string? mode,
        IReadOnlyCollection<long> selectedImageIds)
        => request.Revision == _revision &&
           string.Equals(request.Mode, mode, StringComparison.Ordinal) &&
           request.ImageId is long imageId &&
           selectedImageIds.Count == 1 &&
           selectedImageIds.Contains(imageId);
}
