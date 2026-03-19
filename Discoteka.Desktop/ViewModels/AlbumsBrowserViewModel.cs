using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Discoteka.Desktop.ViewModels;

/// <summary>
/// Owns the album-grid state for the Albums view: loading, caching, paging, and selection.
/// Play requests are delegated back to the coordinator via the <c>playTracks</c> callback.
/// </summary>
public sealed class AlbumsBrowserViewModel : ViewModelBase
{
    private readonly LibraryViewModel _library;
    private readonly Func<IEnumerable<TrackRowViewModel>, (bool Started, string? UserError)> _playTracks;
    private const int PageSize = 200;

    private int _loadVersion;
    private IReadOnlyList<AlbumBrowserItemViewModel>? _cachedGroups;
    private bool? _cachedGroupsRequireLocalFile;
    private AlbumBrowserItemViewModel? _selectedAlbum;
    private IReadOnlyList<AlbumBrowserItemViewModel> _groups = Array.Empty<AlbumBrowserItemViewModel>();
    private IReadOnlyList<AlbumBrowserItemViewModel> _visibleGroups = Array.Empty<AlbumBrowserItemViewModel>();

    public AlbumsBrowserViewModel(
        LibraryViewModel library,
        Func<IEnumerable<TrackRowViewModel>, (bool Started, string? UserError)> playTracks)
    {
        _library = library;
        _playTracks = playTracks;
    }

    public IReadOnlyList<AlbumBrowserItemViewModel> AlbumGroups
    {
        get => _groups;
        private set
        {
            if (SetProperty(ref _groups, value))
            {
                ResetVisiblePage();
            }
        }
    }

    public IReadOnlyList<AlbumBrowserItemViewModel> VisibleAlbumGroups
    {
        get => _visibleGroups;
        private set
        {
            if (SetProperty(ref _visibleGroups, value))
            {
                OnPropertyChanged(nameof(CanLoadMoreAlbumGroups));
                OnPropertyChanged(nameof(AlbumGridStatusText));
            }
        }
    }

    public AlbumBrowserItemViewModel? SelectedAlbumsViewAlbum
    {
        get => _selectedAlbum;
        private set
        {
            if (SetProperty(ref _selectedAlbum, value))
            {
                OnPropertyChanged(nameof(HasSelectedAlbumsViewAlbum));
            }
        }
    }

    public bool HasSelectedAlbumsViewAlbum => SelectedAlbumsViewAlbum != null;
    public bool CanLoadMoreAlbumGroups => VisibleAlbumGroups.Count < AlbumGroups.Count;
    public string AlbumGridStatusText => AlbumGroups.Count == 0
        ? "No albums"
        : $"{VisibleAlbumGroups.Count} of {AlbumGroups.Count} albums";

    public void InvalidateCache()
    {
        _cachedGroups = null;
        _cachedGroupsRequireLocalFile = null;
    }

    public void Clear()
    {
        AlbumGroups = Array.Empty<AlbumBrowserItemViewModel>();
        VisibleAlbumGroups = Array.Empty<AlbumBrowserItemViewModel>();
        SelectedAlbumsViewAlbum = null;
    }

    public void LoadMorePage()
    {
        if (!CanLoadMoreAlbumGroups) return;
        var nextCount = Math.Min(AlbumGroups.Count, VisibleAlbumGroups.Count + PageSize);
        VisibleAlbumGroups = AlbumGroups.Take(nextCount).ToList();
    }

    public async Task LoadAsync()
    {
        var loadVersion = Interlocked.Increment(ref _loadVersion);
        try
        {
            var indexedRows = await _library.GetCachedIndexedRowsAsync();
            var requireLocalFile = _library.MapSmartFilterToIndexedQuery();

            if (_cachedGroups != null && _cachedGroupsRequireLocalFile == requireLocalFile)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (loadVersion != Volatile.Read(ref _loadVersion)) return;
                    AlbumGroups = _cachedGroups;
                    SelectedAlbumsViewAlbum = null;
                });
                return;
            }

            var albums = indexedRows
                .GroupBy(row => row.AlbumId)
                .Select(group =>
                {
                    var first = group.First();
                    var artistName = string.IsNullOrWhiteSpace(first.AlbumArtistName)
                        ? group.Select(g => g.ArtistName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Unknown Artist"
                        : first.AlbumArtistName;
                    return new AlbumBrowserItemViewModel(first.AlbumId, first.AlbumTitle, artistName, first.ReleaseYear, group.Max(g => g.AlbumTrackCount));
                })
                .OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.ArtistName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (loadVersion != Volatile.Read(ref _loadVersion)) return;
                _cachedGroups = albums;
                _cachedGroupsRequireLocalFile = requireLocalFile;
                AlbumGroups = albums;
                SelectedAlbumsViewAlbum = null;
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Albums] Failed to load indexed album view: {ex}");
        }
    }

    public async Task ToggleAlbumAsync(AlbumBrowserItemViewModel album)
    {
        await EnsureAlbumTracksLoadedAsync(album);
        foreach (var other in AlbumGroups)
        {
            if (!ReferenceEquals(other, album) && other.IsExpanded)
                other.IsExpanded = false;
        }
        var willExpand = !album.IsExpanded;
        album.IsExpanded = willExpand;
        SelectedAlbumsViewAlbum = willExpand ? album : null;
    }

    public async Task<(bool Started, string? UserError)> PlayAlbumAsync(AlbumBrowserItemViewModel album)
    {
        Console.WriteLine($"[PlaybackVM] PlayAlbumsViewAlbumAsync albumId={album.AlbumId}, title='{album.Title}', loaded={album.IsTracksLoaded}");
        await EnsureAlbumTracksLoadedAsync(album);
        Console.WriteLine($"[PlaybackVM] Albums view album tracks ready count={album.Tracks.Count} for albumId={album.AlbumId}");
        var result = _playTracks(album.Tracks);
        Console.WriteLine($"[PlaybackVM] PlayAlbumsViewAlbumAsync result started={result.Started}, userError={result.UserError ?? "<null>"}");
        return result;
    }

    private void ResetVisiblePage()
    {
        VisibleAlbumGroups = _groups.Count == 0
            ? Array.Empty<AlbumBrowserItemViewModel>()
            : _groups.Take(Math.Min(_groups.Count, PageSize)).ToList();
    }

    private async Task EnsureAlbumTracksLoadedAsync(AlbumBrowserItemViewModel album)
    {
        if (album.IsTracksLoaded) return;
        try
        {
            var tracks = await _library.GetIndexedAlbumTracksAsync(album.AlbumId, _library.MapSmartFilterToIndexedQuery());
            album.SetTracks(tracks.Select(LibraryViewModel.MapTrack).ToList());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Albums] Failed to load tracks for album {album.AlbumId}: {ex}");
            album.SetTracks(new List<TrackRowViewModel>());
        }
    }
}
