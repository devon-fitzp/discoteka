using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace discoteka.ViewModels;

/// <summary>
/// Owns the artist-index state for the Artists view: loading, caching, group list, and selection.
/// Play requests are delegated back to the coordinator via the <c>playTracks</c> callback.
/// </summary>
public sealed class ArtistsBrowserViewModel : ViewModelBase
{
    private readonly LibraryViewModel _library;
    private readonly Func<IEnumerable<TrackRowViewModel>, (bool Started, string? UserError)> _playTracks;

    private int _loadVersion;
    private IReadOnlyList<ArtistGroupViewModel>? _cachedGroups;
    private bool? _cachedGroupsRequireLocalFile;
    private ArtistGroupViewModel? _selectedGroup;
    private IReadOnlyList<ArtistGroupViewModel> _groups = Array.Empty<ArtistGroupViewModel>();

    public ArtistsBrowserViewModel(
        LibraryViewModel library,
        Func<IEnumerable<TrackRowViewModel>, (bool Started, string? UserError)> playTracks)
    {
        _library = library;
        _playTracks = playTracks;
    }

    public IReadOnlyList<ArtistGroupViewModel> ArtistGroups
    {
        get => _groups;
        private set
        {
            if (SetProperty(ref _groups, value))
            {
                if (_selectedGroup != null && !_groups.Contains(_selectedGroup))
                {
                    SelectedArtistGroup = null;
                }
            }
        }
    }

    public ArtistGroupViewModel? SelectedArtistGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                if (value != null)
                {
                    Console.WriteLine($"[Artists][Select] Artist '{value.Name}' (albums={value.AlbumCount})");
                    value.IsExpanded = true;
                }
                OnPropertyChanged(nameof(HasSelectedArtistGroup));
            }
        }
    }

    public bool HasSelectedArtistGroup => SelectedArtistGroup != null;

    public void InvalidateCache()
    {
        _cachedGroups = null;
        _cachedGroupsRequireLocalFile = null;
    }

    public void Clear()
    {
        SelectedArtistGroup = null;
        ArtistGroups = Array.Empty<ArtistGroupViewModel>();
    }

    public async Task LoadAsync()
    {
        var loadVersion = Interlocked.Increment(ref _loadVersion);
        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            Console.WriteLine($"[Artists][Load] Start (version={loadVersion}, filter={_library.CurrentSmartFilter}, mem={GC.GetTotalMemory(false) / (1024 * 1024)} MB)");
            var fetchStopwatch = Stopwatch.StartNew();
            var indexedRows = await _library.GetCachedIndexedRowsAsync();
            fetchStopwatch.Stop();
            Console.WriteLine($"[Artists][Load] Indexed rows fetched: {indexedRows.Count} in {fetchStopwatch.ElapsedMilliseconds} ms (mem={GC.GetTotalMemory(false) / (1024 * 1024)} MB)");

            var requireLocalFile = _library.MapSmartFilterToIndexedQuery();
            if (_cachedGroups != null && _cachedGroupsRequireLocalFile == requireLocalFile)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (loadVersion != Volatile.Read(ref _loadVersion)) return;
                    ArtistGroups = _cachedGroups;
                    Console.WriteLine($"[Artists][Load] Cache hit applied to UI: artists={ArtistGroups.Count} (version={loadVersion}, mem={GC.GetTotalMemory(false) / (1024 * 1024)} MB)");
                });
                totalStopwatch.Stop();
                Console.WriteLine($"[Artists][Load] Complete (cache hit) in {totalStopwatch.ElapsedMilliseconds} ms");
                return;
            }

            var buildStopwatch = Stopwatch.StartNew();
            var artists = new List<ArtistGroupViewModel>(Math.Min(indexedRows.Count, 4096));
            ArtistGroupViewModel? currentArtist = null;
            long currentArtistId = -1;
            var albumSeedCount = 0;

            foreach (var row in indexedRows)
            {
                if (currentArtist == null || row.ArtistId != currentArtistId)
                {
                    currentArtist = new ArtistGroupViewModel(row.ArtistId, row.ArtistName);
                    currentArtistId = row.ArtistId;
                    artists.Add(currentArtist);
                }
                currentArtist.AddAlbumSeed(row.AlbumId, row.AlbumTitle, row.AlbumTrackCount);
                albumSeedCount++;
            }
            buildStopwatch.Stop();
            Console.WriteLine($"[Artists][Load] Built artist tree: artists={artists.Count}, albumSeeds={albumSeedCount} in {buildStopwatch.ElapsedMilliseconds} ms (mem={GC.GetTotalMemory(false) / (1024 * 1024)} MB)");

            var uiStopwatch = Stopwatch.StartNew();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (loadVersion != Volatile.Read(ref _loadVersion))
                {
                    Console.WriteLine($"[Artists][Load] Skipped stale UI apply (version={loadVersion})");
                    return;
                }
                _cachedGroups = artists;
                _cachedGroupsRequireLocalFile = requireLocalFile;
                ArtistGroups = artists;
                Console.WriteLine($"[Artists][Load] UI apply complete: artists={ArtistGroups.Count} (version={loadVersion}, mem={GC.GetTotalMemory(false) / (1024 * 1024)} MB)");
            });
            uiStopwatch.Stop();
            totalStopwatch.Stop();
            Console.WriteLine($"[Artists][Load] Complete in {totalStopwatch.ElapsedMilliseconds} ms (ui={uiStopwatch.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Artists] Failed to load indexed artist view: {ex}");
        }
    }

    public async Task ToggleAlbumAsync(AlbumGroupViewModel album)
    {
        if (album?.Owner == null) return;
        await EnsureAlbumTracksLoadedAsync(album);
        album.Owner.ToggleSelectedAlbum(album);
    }

    public async Task<(bool Started, string? UserError)> PlayAlbumAsync(AlbumGroupViewModel album)
    {
        Console.WriteLine($"[PlaybackVM] PlayArtistAlbumAsync albumId={album.AlbumId}, title='{album.Title}', loaded={album.IsTracksLoaded}");
        await EnsureAlbumTracksLoadedAsync(album);
        Console.WriteLine($"[PlaybackVM] Artist album tracks ready count={album.Tracks.Count} for albumId={album.AlbumId}");
        var result = _playTracks(album.Tracks);
        Console.WriteLine($"[PlaybackVM] PlayArtistAlbumAsync result started={result.Started}, userError={result.UserError ?? "<null>"}");
        return result;
    }

    private async Task EnsureAlbumTracksLoadedAsync(AlbumGroupViewModel album)
    {
        if (album.IsTracksLoaded) return;
        try
        {
            var tracks = await _library.GetIndexedAlbumTracksAsync(album.AlbumId, _library.MapSmartFilterToIndexedQuery());
            album.SetTracks(tracks.Select(LibraryViewModel.MapTrack).ToList());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Artists] Failed to load tracks for album {album.AlbumId}: {ex}");
            album.SetTracks(new List<TrackRowViewModel>());
        }
    }
}
