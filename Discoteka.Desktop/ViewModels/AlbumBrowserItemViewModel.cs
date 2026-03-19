using System.Collections.Generic;

namespace Discoteka.Desktop.ViewModels;

public sealed class AlbumBrowserItemViewModel : ViewModelBase
{
    private int _trackCount;
    private bool _isExpanded;

    public AlbumBrowserItemViewModel(long albumId, string title, string artistName, int? releaseYear, int trackCount)
    {
        AlbumId = albumId;
        Title = string.IsNullOrWhiteSpace(title) ? "Unknown Album" : title;
        ArtistName = string.IsNullOrWhiteSpace(artistName) ? "Unknown Artist" : artistName;
        ReleaseYear = releaseYear;
        _trackCount = trackCount;
    }

    public long AlbumId { get; }
    public string Title { get; }
    public string ArtistName { get; }
    public int? ReleaseYear { get; }
    public string YearText => ReleaseYear?.ToString() ?? "-";
    public List<TrackRowViewModel> Tracks { get; } = new();
    public bool IsTracksLoaded { get; private set; }

    public int TrackCount
    {
        get => _trackCount;
        private set => SetProperty(ref _trackCount, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public void SetTracks(List<TrackRowViewModel> tracks)
    {
        Tracks.Clear();
        Tracks.AddRange(tracks);
        TrackCount = Tracks.Count;
        IsTracksLoaded = true;
        OnPropertyChanged(nameof(Tracks));
    }
}
