using System.Collections.Generic;

namespace Discoteka.Desktop.ViewModels;

public sealed class AlbumGroupViewModel : ViewModelBase
{
    private int _trackCount;

    public AlbumGroupViewModel(ArtistGroupViewModel owner, long albumId, string title, int trackCount)
    {
        Owner = owner;
        AlbumId = albumId;
        Title = title;
        _trackCount = trackCount;
    }

    public ArtistGroupViewModel Owner { get; }
    public long AlbumId { get; }
    public string Title { get; }
    public List<TrackRowViewModel> Tracks { get; } = new();
    public int TrackCount
    {
        get => _trackCount;
        private set => SetProperty(ref _trackCount, value);
    }
    public bool IsTracksLoaded { get; private set; }

    public void SetTracks(List<TrackRowViewModel> tracks)
    {
        Tracks.Clear();
        Tracks.AddRange(tracks);
        TrackCount = Tracks.Count;
        IsTracksLoaded = true;
        OnPropertyChanged(nameof(Tracks));
    }
}
