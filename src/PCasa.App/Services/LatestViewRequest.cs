namespace PCasa.App.Services;

/// <summary>
/// A view-changing asynchronous request may publish results only while it is
/// the latest request. Work can finish out of order without reverting the UI.
/// </summary>
public sealed class LatestViewRequest
{
    private long _revision;

    public long Begin() => Interlocked.Increment(ref _revision);

    public bool IsCurrent(long request) =>
        request == Interlocked.Read(ref _revision);

    public void Invalidate() => Interlocked.Increment(ref _revision);
}
