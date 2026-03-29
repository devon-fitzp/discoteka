using Discoteka.Core.Models;

namespace Discoteka.Desktop.ViewModels;

public sealed class PlaylistItemViewModel : ViewModelBase
{
    private bool _isSelected;

    public PlaylistItemViewModel(DynamicPlaylist playlist)
    {
        Name = playlist.Name;
        IsDynamic = true;
        DynamicPlaylist = playlist;
    }

    public PlaylistItemViewModel(StaticPlaylist playlist)
    {
        Name = playlist.Name;
        IsDynamic = false;
        StaticPlaylist = playlist;
    }

    public string Name { get; }
    public bool IsDynamic { get; }
    public DynamicPlaylist? DynamicPlaylist { get; }
    public StaticPlaylist? StaticPlaylist { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
