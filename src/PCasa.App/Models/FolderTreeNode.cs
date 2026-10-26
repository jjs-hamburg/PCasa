using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

namespace PCasa.App.Models;

internal sealed class FolderTreeNode
{
    public FolderTreeNode(string displayName, string fullPath)
    {
        DisplayName = displayName;
        FullPath = fullPath;
    }

    public string DisplayName { get; }
    public string FullPath { get; }
    public string? CanonicalPath { get; set; }
    public long? PhotoFolderId { get; set; }
    public int DirectImageCount { get; set; }
    public int TotalImageCount { get; set; }
    public List<FolderTreeNode> Children { get; } = new();
}

internal sealed class FolderTreeTag : INotifyPropertyChanged
{
    private BitmapSource? _thumbnailSource;

    public FolderTreeTag(string fullPath, long? photoFolderId)
    {
        FullPath = fullPath;
        PhotoFolderId = photoFolderId;

        if (photoFolderId is long id)
        {
            _ = LoadThumbnailAsync(id);
        }
    }

    public string FullPath { get; }
    public long? PhotoFolderId { get; }

    private bool _hasPendingTagWrites;
    public bool HasPendingTagWrites
    {
        get => _hasPendingTagWrites;
        set
        {
            if (_hasPendingTagWrites == value) return;
            _hasPendingTagWrites = value;
            OnPropertyChanged();
        }
    }

    private string _statusCode = "None";
    private string _statusDescription = "";

    public string StatusCode
    {
        get => _statusCode;
        set
        {
            if (_statusCode == value) return;
            _statusCode = value;
            OnPropertyChanged();
        }
    }

    public string StatusDescription
    {
        get => _statusDescription;
        set
        {
            if (_statusDescription == value) return;
            _statusDescription = value;
            OnPropertyChanged();
        }
    }

    public BitmapSource? ThumbnailSource
    {
        get => _thumbnailSource;
        private set
        {
            if (ReferenceEquals(_thumbnailSource, value))
            {
                return;
            }

            _thumbnailSource = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async Task LoadThumbnailAsync(long photoFolderId)
    {
        var thumbnail = await FolderTreeThumbnailService.GetAsync(photoFolderId);
        ThumbnailSource = thumbnail;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
