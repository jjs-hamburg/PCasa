namespace PCasa.App.Services;

/// <summary>
/// One uncommitted editing session, keyed by stable catalog image IDs.
/// A new gallery selection changes only the view into this draft, never the
/// staged intent. No SQLite writes or filesystem changes occur here.
/// </summary>
public sealed record TagImageChange(long ImageId,
    IReadOnlyList<(string Tag, bool Remove)> Changes);

public sealed class TagEditDraft
{
    private readonly Dictionary<long, Dictionary<string, bool>> _byImage = new();

    public int ImageCount => _byImage.Count;
    public int ChangeCount => _byImage.Values.Sum(changes => changes.Count);
    public bool HasChanges => _byImage.Count > 0;

    public void Stage(IEnumerable<long> imageIds, string tag, bool remove)
    {
        tag = tag.Trim();
        if (tag.Length == 0) throw new ArgumentException("Tag fehlt.", nameof(tag));
        foreach (var id in imageIds.Distinct())
        {
            if (!_byImage.TryGetValue(id, out var changes))
            {
                changes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                _byImage.Add(id, changes);
            }
            // The most recently staged operation for this image/tag wins.
            // Different selected images retain their OWN staged changes.
            changes[tag] = remove;
        }
    }

    public IReadOnlyList<TagImageChange> Snapshot() =>
        _byImage.OrderBy(pair => pair.Key)
            .Select(pair => new TagImageChange(pair.Key,
                pair.Value.OrderBy(change => change.Key,
                    StringComparer.OrdinalIgnoreCase)
                    .Select(change => (change.Key, change.Value)).ToArray()))
            .ToArray();

    public IReadOnlyDictionary<string, int> ProjectCounts(
        IEnumerable<long> selectedImageIds,
        IReadOnlyDictionary<long, HashSet<string>> existingTags)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var imageId in selectedImageIds.Distinct())
        {
            var effective = existingTags.TryGetValue(imageId, out var original)
                ? new HashSet<string>(original, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_byImage.TryGetValue(imageId, out var edits))
            {
                foreach (var (tag, remove) in edits)
                {
                    if (remove) effective.Remove(tag);
                    else effective.Add(tag);
                }
            }
            foreach (var tag in effective)
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
        }
        return counts;
    }

    public void Clear() => _byImage.Clear();
}
