using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PCasa.App.Models;

public sealed class MonitoredFolder : INotifyPropertyChanged
{
    private bool _serverSyncEnabled;

    public long Id { get; init; }
    public required string LocalPath { get; init; }
    public required string CanonicalPath { get; init; }
    public bool IsNetwork { get; init; }

    public bool CanServerSync => IsNetwork && CanonicalPath.StartsWith(@"\\", StringComparison.Ordinal);

    public string LocationType => IsNetwork ? "Netzwerk" : "Lokal";

    public bool ServerSyncEnabled
    {
        get => _serverSyncEnabled;
        set
        {
            var effectiveValue = CanServerSync && value;
            if (_serverSyncEnabled == effectiveValue)
            {
                return;
            }

            _serverSyncEnabled = effectiveValue;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
