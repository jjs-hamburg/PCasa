namespace PCasa.App.Data;

/// <summary>
/// One catalog-level notification; it has no dependency on WPF, folder-tree
/// state or scan progress. Only a fully committed image may be published.
/// Consumers choose independently when and how to update their own views.
/// </summary>
public static class CatalogChanges
{
    public static event Action? ImageCommitted;
    public static event Action<string, long>? ImageIndexed;

    public static void PublishImageIndexed(string canonicalPath, long photoFolderId)
    {
        var listeners = ImageIndexed;
        if (listeners is null) return;
        foreach (Action<string, long> listener in listeners.GetInvocationList())
        {
            try { listener(canonicalPath, photoFolderId); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }


    public static void PublishImageCommitted()
    {
        // Subscribers are observers, not part of the SQLite commit.
        // A view failure must not roll back or interrupt an image scan.
        var listeners = ImageCommitted;
        if (listeners is null) return;
        foreach (Action listener in listeners.GetInvocationList())
        {
            try { listener(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }
}
