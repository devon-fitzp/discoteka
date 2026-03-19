using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Discoteka.Desktop.ViewModels;

public sealed class ArtistGroupViewModel : ViewModelBase
{
    private readonly List<ArtistAlbumSeed> _albumSeeds = new();
    private ObservableCollection<AlbumGroupViewModel>? _albums;
    private bool _isExpanded;
    private AlbumGroupViewModel? _selectedAlbum;
    private static readonly IReadOnlyList<AlbumGroupViewModel> EmptyAlbums = System.Array.Empty<AlbumGroupViewModel>();

    public ArtistGroupViewModel(long artistId, string name)
    {
        ArtistId = artistId;
        Name = name;
    }

    public long ArtistId { get; }
    public string Name { get; }
    public ObservableCollection<AlbumGroupViewModel> Albums
    {
        get
        {
            EnsureAlbumsMaterialized();
            return _albums!;
        }
    }
    public int AlbumCount => _albumSeeds.Count;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                if (value)
                {
                    EnsureAlbumsMaterialized();
                }
                OnPropertyChanged(nameof(VisibleAlbums));
            }
        }
    }

    public AlbumGroupViewModel? SelectedAlbum
    {
        get => _selectedAlbum;
        private set
        {
            if (SetProperty(ref _selectedAlbum, value))
            {
                OnPropertyChanged(nameof(HasSelectedAlbum));
            }
        }
    }

    public bool HasSelectedAlbum => SelectedAlbum != null;
    public IReadOnlyList<AlbumGroupViewModel> VisibleAlbums => IsExpanded ? Albums : EmptyAlbums;

    public void AddAlbumSeed(long albumId, string title, int trackCount)
    {
        _albumSeeds.Add(new ArtistAlbumSeed(albumId, title, trackCount));
    }

    public void ToggleSelectedAlbum(AlbumGroupViewModel album)
    {
        IsExpanded = true;
        SelectedAlbum = ReferenceEquals(SelectedAlbum, album) ? null : album;
    }

    private void EnsureAlbumsMaterialized()
    {
        if (_albums != null)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        System.Console.WriteLine($"[Artists][Expand] Materializing albums for '{Name}' (artistId={ArtistId}, count={_albumSeeds.Count})");
        _albums = new ObservableCollection<AlbumGroupViewModel>();
        foreach (var seed in _albumSeeds)
        {
            _albums.Add(new AlbumGroupViewModel(this, seed.AlbumId, seed.Title, seed.TrackCount));
        }
        stopwatch.Stop();
        System.Console.WriteLine($"[Artists][Expand] Materialized albums for '{Name}' in {stopwatch.ElapsedMilliseconds} ms (mem={System.GC.GetTotalMemory(false) / (1024 * 1024)} MB)");
    }

    private readonly record struct ArtistAlbumSeed(long AlbumId, string Title, int TrackCount);
}
