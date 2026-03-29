using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Discoteka.Core.Database;
using Discoteka.Core.Utils;

namespace Discoteka.Desktop.ViewModels;

/// <summary>
/// Owns playlist list state, selection, and track loading for the Playlists view.
/// Play requests are delegated to the coordinator via the <c>playTracks</c> callback.
/// </summary>
public sealed class PlaylistsBrowserViewModel : ViewModelBase
{
    private readonly LibraryViewModel _library;
    private readonly IDynamicPlaylistRepository _dynamicRepo;
    private readonly M3uPlaylistService _staticService;
    private readonly Func<IEnumerable<TrackRowViewModel>, (bool Started, string? UserError)> _playTracks;

    private int _loadVersion;
    private PlaylistItemViewModel? _selectedPlaylist;
    private string _trackCountText = "0 tracks";

    public PlaylistsBrowserViewModel(
        LibraryViewModel library,
        IDynamicPlaylistRepository dynamicRepo,
        M3uPlaylistService staticService,
        Func<IEnumerable<TrackRowViewModel>, (bool Started, string? UserError)> playTracks)
    {
        _library = library;
        _dynamicRepo = dynamicRepo;
        _staticService = staticService;
        _playTracks = playTracks;
    }

    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = new();
    public BulkObservableCollection<TrackRowViewModel> PlaylistTracks { get; } = new();

    public PlaylistItemViewModel? SelectedPlaylist
    {
        get => _selectedPlaylist;
        private set => SetProperty(ref _selectedPlaylist, value);
    }

    public string TrackCountText
    {
        get => _trackCountText;
        private set => SetProperty(ref _trackCountText, value);
    }

    public void Clear()
    {
        SelectedPlaylist = null;
        foreach (var p in Playlists)
        {
            p.IsSelected = false;
        }

        Dispatcher.UIThread.Post(() =>
        {
            PlaylistTracks.ResetWith(Array.Empty<TrackRowViewModel>());
            TrackCountText = "0 tracks";
        });
    }

    public async Task LoadAsync()
    {
        var loadVersion = Interlocked.Increment(ref _loadVersion);
        try
        {
            var dynamic = await _dynamicRepo.GetAllAsync();
            var staticPlaylists = _staticService.GetAll();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (loadVersion != Volatile.Read(ref _loadVersion)) return;

                Playlists.Clear();
                foreach (var p in dynamic)
                {
                    Playlists.Add(new PlaylistItemViewModel(p));
                }

                foreach (var p in staticPlaylists)
                {
                    Playlists.Add(new PlaylistItemViewModel(p));
                }

                // Restore selection state
                if (_selectedPlaylist != null)
                {
                    var match = Playlists.FirstOrDefault(p =>
                        p.IsDynamic == _selectedPlaylist.IsDynamic && p.Name == _selectedPlaylist.Name);
                    if (match != null)
                    {
                        match.IsSelected = true;
                        SelectedPlaylist = match;
                    }
                    else
                    {
                        SelectedPlaylist = null;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Playlists] Failed to load playlists: {ex}");
        }
    }

    public async Task SelectPlaylistAsync(PlaylistItemViewModel item)
    {
        // Update selection highlight
        foreach (var p in Playlists)
        {
            p.IsSelected = p == item;
        }

        SelectedPlaylist = item;

        try
        {
            IReadOnlyList<TrackRowViewModel> tracks;
            if (item.IsDynamic && item.DynamicPlaylist != null)
            {
                var dbTracks = await _dynamicRepo.EvaluateAsync(item.DynamicPlaylist);
                tracks = dbTracks.Select(LibraryViewModel.MapTrack).ToList();
            }
            else if (!item.IsDynamic && item.StaticPlaylist != null)
            {
                var paths = _staticService.LoadPaths(item.StaticPlaylist);
                var pathSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                // Join against in-memory library tracks by FilePath, preserving M3U order
                var pathList = paths.ToList();
                tracks = _library.Tracks
                    .Where(t => t.FilePath != null && pathSet.Contains(t.FilePath))
                    .OrderBy(t => pathList.IndexOf(t.FilePath!))
                    .ToList();
            }
            else
            {
                tracks = Array.Empty<TrackRowViewModel>();
            }

            var count = tracks.Count;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PlaylistTracks.ResetWith(tracks);
                TrackCountText = count == 1 ? "1 track" : $"{count} tracks";
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Playlists] Failed to load tracks for '{item.Name}': {ex}");
        }
    }

    public async Task<(bool Started, string? UserError)> PlayPlaylistAsync(PlaylistItemViewModel item)
    {
        await SelectPlaylistAsync(item);
        if (PlaylistTracks.Count == 0)
        {
            return (false, null);
        }

        return _playTracks(PlaylistTracks);
    }

    public (bool Started, string? UserError) PlayFromIndex(int index)
    {
        if (index < 0 || index >= PlaylistTracks.Count)
        {
            return (false, null);
        }

        return _playTracks(PlaylistTracks.Skip(index).Concat(PlaylistTracks.Take(index)));
    }
}
