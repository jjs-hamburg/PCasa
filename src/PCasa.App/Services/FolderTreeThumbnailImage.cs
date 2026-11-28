using System.Windows;
using System.Windows.Controls;
using PCasa.App.Models;

namespace PCasa.App.Services;

// The tree retains its full logical ordering for gallery jumps, but only
// headers in an expanded, visible branch request a local folder preview.
public sealed class FolderTreeThumbnailImage : Image
{
    public static readonly DependencyProperty FolderProperty =
        DependencyProperty.Register(nameof(Folder), typeof(object),
            typeof(FolderTreeThumbnailImage),
            new PropertyMetadata(null, (owner, _) =>
                ((FolderTreeThumbnailImage)owner).RequestIfVisible()));

    public object? Folder
    {
        get => GetValue(FolderProperty);
        set => SetValue(FolderProperty, value);
    }

    public FolderTreeThumbnailImage()
    {
        Loaded += (_, _) => RequestIfVisible();
        IsVisibleChanged += (_, _) => RequestIfVisible();
    }

    private void RequestIfVisible()
    {
        if (IsLoaded && IsVisible && Folder is FolderTreeTag tag)
            tag.RequestThumbnail();
    }
}
