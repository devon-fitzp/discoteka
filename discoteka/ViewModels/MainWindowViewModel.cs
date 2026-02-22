using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using discoteka.Playback;
using discoteka_cli.Database;
using discoteka_cli.Jobs;

namespace discoteka.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public enum DefaultSortOption
    {
        Title,
        Artist,
        RecentlyAdded
    }

    public enum LibraryViewMode
    {
        AllMusic,
        Artists,
        Albums
    }

    public enum SmartFilterMode
    {
        None,
        AvailableLocally,
        NoLocalFile
    }

    private enum SortColumn
    {
        None,
        Title,
        Artist,
        Album,
        Time,
        Genre,
        Formats,
        Plays
    }

    private enum SortDirection
    {
        None,
        Ascending,
        Descending
    }

    private readonly ILibraryImportJobs _importJobs;
    private readonly IBackgroundJobQueue? _jobQueue;
    private readonly ITrackLibraryRepository _trackRepository;
    private readonly IMediaPlaybackService? _playbackService;
    private readonly List<TrackRowViewModel> _libraryRows = new();
    private readonly List<TrackRowViewModel> _allTracks = new();
    private int _pendingJobs;
    private int _loadVersion;
    private int _artistIndexLoadVersion;
    private bool _suppressVolumeUpdate;
    private bool _shuffleEnabled;
    private RepeatMode _playerRepeatMode = RepeatMode.Off;
    private long? _currentPlaybackTrackId;
    private bool _currentPlaybackTrackCounted;
    private string _nowPlayingTitle = "No track selected";
    private string _nowPlayingTimeText = "0:00 / 0:00";
    private double _playbackPositionSeconds;
    private double _playbackDurationSeconds = 100;
    private int _volume = 70;
    private bool _isPlaying;
    private string _statusMessage = "Ready.";
    private int _trackCount;
    private DefaultSortOption _defaultSort = DefaultSortOption.Title;
    private LibraryViewMode _libraryViewMode = LibraryViewMode.AllMusic;
    private SmartFilterMode _smartFilterMode = SmartFilterMode.None;
    private SortColumn _activeSortColumn = SortColumn.None;
    private SortDirection _activeSortDirection = SortDirection.None;

    public MainWindowViewModel()
        : this(new NoOpLibraryImportJobs(), null, null, null)
    {
    }

    public MainWindowViewModel(
        ILibraryImportJobs importJobs,
        IBackgroundJobQueue? jobQueue,
        ITrackLibraryRepository? trackRepository,
        IMediaPlaybackService? playbackService)
    {
        _importJobs = importJobs;
        _jobQueue = jobQueue;
        _trackRepository = trackRepository ?? new TrackLibraryRepository();
        _playbackService = playbackService;

        if (_jobQueue != null)
        {
            _jobQueue.JobStatusChanged += OnJobStatusChanged;
        }

        if (_playbackService != null)
        {
            _playbackService.PlaybackStateChanged += OnPlaybackStateChanged;
            _playbackService.CurrentTrackChanged += OnCurrentTrackChanged;
            _playbackService.PlaybackEnded += OnPlaybackEnded;
            _playbackService.PlaybackError += OnPlaybackError;
        }
    }

    public string Greeting { get; } = "Welcome to discoteka!";
    public ObservableCollection<TrackRowViewModel> Tracks { get; } = new();
    public ObservableCollection<ArtistGroupViewModel> ArtistGroups { get; } = new();

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string TrackCountText => _trackCount == 1 ? "1 track" : $"{_trackCount} tracks";
    public string NowPlayingTitle
    {
        get => _nowPlayingTitle;
        private set => SetProperty(ref _nowPlayingTitle, value);
    }

    public string NowPlayingTimeText
    {
        get => _nowPlayingTimeText;
        private set => SetProperty(ref _nowPlayingTimeText, value);
    }

    public double PlaybackPositionSeconds
    {
        get => _playbackPositionSeconds;
        private set => SetProperty(ref _playbackPositionSeconds, value);
    }

    public double PlaybackDurationSeconds
    {
        get => _playbackDurationSeconds;
        private set => SetProperty(ref _playbackDurationSeconds, value);
    }

    public int Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _volume, clamped))
            {
                return;
            }

            if (!_suppressVolumeUpdate)
            {
                _playbackService?.SetVolume(clamped);
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseButtonText));
            }
        }
    }

    public string PlayPauseButtonText => IsPlaying ? "Pause" : "Play";
    public string ShuffleButtonText => _shuffleEnabled ? "Shuffle: On" : "Shuffle: Off";
    public string RepeatButtonText => _playerRepeatMode switch
    {
        RepeatMode.Track => "Repeat: Track",
        RepeatMode.Playlist => "Repeat: Playlist",
        _ => "Repeat: Off"
    };
    public bool IsAllMusicView => _libraryViewMode == LibraryViewMode.AllMusic;
    public bool IsArtistsView => _libraryViewMode == LibraryViewMode.Artists;
    public bool IsAlbumsView => _libraryViewMode == LibraryViewMode.Albums;
    public string LibraryViewTitle => _libraryViewMode switch
    {
        LibraryViewMode.Artists => "Artists",
        LibraryViewMode.Albums => "Albums",
        _ => "All Music"
    };
    public string LibrarySubtitleText => IsAllMusicView
        ? TrackCountText
        : _libraryViewMode switch
        {
            LibraryViewMode.Artists => ArtistGroups.Count == 1 ? "1 artist" : $"{ArtistGroups.Count} artists",
            LibraryViewMode.Albums => "Album browser (v1 framework in progress)",
            _ => string.Empty
        };

    public async Task QueueAppleMusicImportAsync(string xmlPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(xmlPath))
        {
            return;
        }

        await _importJobs.QueueAppleMusicImportAsync(xmlPath, cancellationToken);
        UpdateStatusFromPending("Import queued.");
    }

    public async Task QueueMediaScanAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        await _importJobs.QueueMediaScanAsync(rootPath, cancellationToken);
        UpdateStatusFromPending("Scan queued.");
    }

    public async Task QueueCleanupAsync(double minConfidence, CancellationToken cancellationToken = default)
    {
        await _importJobs.QueueCleanupAsync(minConfidence, cancellationToken);
        UpdateStatusFromPending("Cleanup queued.");
    }

    public async Task QueueMatchRescanAsync(double minScore, CancellationToken cancellationToken = default)
    {
        await _importJobs.QueueMatchRescanAsync(minScore, cancellationToken);
        UpdateStatusFromPending("Match rescan queued.");
    }

    public async Task QueueRebuildIndexAsync(CancellationToken cancellationToken = default)
    {
        await _importJobs.QueueRebuildIndexAsync(cancellationToken);
        UpdateStatusFromPending("Rebuild index queued.");
    }

    public async Task InitializeAsync()
    {
        await LoadTracksAsync();
    }

    public void CycleSortByTitle() => CycleSort(SortColumn.Title);
    public void CycleSortByArtist() => CycleSort(SortColumn.Artist);
    public void CycleSortByAlbum() => CycleSort(SortColumn.Album);
    public void CycleSortByTime() => CycleSort(SortColumn.Time);
    public void CycleSortByGenre() => CycleSort(SortColumn.Genre);
    public void CycleSortByFormats() => CycleSort(SortColumn.Formats);
    public void CycleSortByPlays() => CycleSort(SortColumn.Plays);

    public void ShowAllMusicView() => SetLibraryViewMode(LibraryViewMode.AllMusic);
    public void ShowArtistsView() => SetLibraryViewMode(LibraryViewMode.Artists);
    public void ShowAlbumsView() => SetLibraryViewMode(LibraryViewMode.Albums);

    public void ClearSmartFilter() => SetSmartFilter(SmartFilterMode.None);
    public void ShowAvailableLocallyFilter() => SetSmartFilter(SmartFilterMode.AvailableLocally);
    public void ShowNoLocalFileFilter() => SetSmartFilter(SmartFilterMode.NoLocalFile);

    public void SetDefaultSort(DefaultSortOption option)
    {
        _defaultSort = option;
        _activeSortColumn = SortColumn.None;
        _activeSortDirection = SortDirection.None;
        ApplyCurrentSort();
        StatusMessage = option switch
        {
            DefaultSortOption.Title => "Default sort: Title (A-Z).",
            DefaultSortOption.Artist => "Default sort: Artist (A-Z).",
            DefaultSortOption.RecentlyAdded => "Default sort: Recently Added.",
            _ => StatusMessage
        };
    }

    public bool PlayTrackFromVisibleIndex(int index, out string? userError)
    {
        userError = null;
        if (_playbackService == null)
        {
            userError = "Playback unavailable.";
            return false;
        }

        if (index < 0 || index >= Tracks.Count)
        {
            return false;
        }

        var visible = Tracks.ToList();
        var selected = visible[index];
        if (!HasLocalFile(selected.FilePath))
        {
            userError = "No local file!";
            return false;
        }

        var queue = visible
            .Select(MapPlaybackTrack)
            .ToList();
        _playbackService.SetQueue(queue, index);
        var started = _playbackService.PlayAtIndex(index);
        Console.WriteLine($"[Playback] Play from visible index {index}: {(started ? "started" : "failed")}");
        if (!started)
        {
            userError = "No local file!";
        }

        return started;
    }

    public bool TogglePlayPause(int preferredIndex, out string? userError)
    {
        userError = null;
        if (_playbackService == null)
        {
            userError = "Playback unavailable.";
            return false;
        }

        if (_playbackService.CurrentTrack == null)
        {
            var startIndex = preferredIndex >= 0 && preferredIndex < Tracks.Count ? preferredIndex : 0;
            return PlayTrackFromVisibleIndex(startIndex, out userError);
        }

        if (_playbackService.State.IsPlaying)
        {
            _playbackService.Pause();
        }
        else
        {
            _playbackService.Resume();
        }

        return true;
    }

    public void PlayNext()
    {
        if (_playbackService == null)
        {
            return;
        }

        TryPlayDirection(skipMissingFiles: true, forward: true);
    }

    public void PlayPrevious()
    {
        if (_playbackService == null)
        {
            return;
        }

        TryPlayDirection(skipMissingFiles: true, forward: false);
    }

    public void SeekToSeconds(double seconds)
    {
        _playbackService?.Seek((long)Math.Max(0, seconds * 1000.0));
    }

    public async Task ToggleArtistAlbumAsync(AlbumGroupViewModel? album)
    {
        if (album?.Owner == null)
        {
            return;
        }

        await EnsureAlbumTracksLoadedAsync(album);
        album.Owner.ToggleSelectedAlbum(album);
    }

    public async Task<(bool Started, string? UserError)> PlayArtistAlbumAsync(AlbumGroupViewModel? album)
    {
        if (album == null)
        {
            return (false, null);
        }

        await EnsureAlbumTracksLoadedAsync(album);
        var started = PlayTrackSequence(album.Tracks, out var userError);
        return (started, userError);
    }

    public Task<discoteka_cli.Models.TrackMetadataSnapshot?> GetTrackMetadataSnapshotAsync(long trackId, CancellationToken cancellationToken = default)
    {
        return _trackRepository.GetTrackMetadataSnapshotAsync(trackId, cancellationToken);
    }

    public Task SaveTrackMetadataTabAsync(discoteka_cli.Models.MetadataTabEntry tab, CancellationToken cancellationToken = default)
    {
        return _trackRepository.SaveMetadataTabAsync(tab, cancellationToken);
    }

    public void ToggleShuffle()
    {
        if (_playbackService == null)
        {
            return;
        }

        _playbackService.SetShuffle(!_shuffleEnabled);
    }

    public void CycleRepeatMode()
    {
        if (_playbackService == null)
        {
            return;
        }

        var next = _playerRepeatMode switch
        {
            RepeatMode.Off => RepeatMode.Track,
            RepeatMode.Track => RepeatMode.Playlist,
            _ => RepeatMode.Off
        };

        _playbackService.SetRepeatMode(next);
    }

    private async Task LoadTracksAsync()
    {
        var loadVersion = Interlocked.Increment(ref _loadVersion);
        Console.WriteLine($"[Library] Load {loadVersion} started.");
        await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "Loading library...");
        var tracks = await _trackRepository.GetAllAsync();
        var rows = tracks.Select(MapTrack).ToList();
        Console.WriteLine($"[Library] Load {loadVersion} fetched {rows.Count} row(s).");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (loadVersion != Volatile.Read(ref _loadVersion))
            {
                Console.WriteLine($"[Library] Load {loadVersion} skipped (newer load exists).");
                return;
            }

            _libraryRows.Clear();
            _libraryRows.AddRange(rows);
            _allTracks.Clear();
            RebuildVisibleTracks();
            UpdateStatusFromPending("Library loaded.");
            Console.WriteLine($"[Library] Load {loadVersion} applied to UI.");
        });
    }

    private void OnJobStatusChanged(object? sender, BackgroundJobStatusChangedEventArgs e)
    {
        Console.WriteLine($"[Jobs] {e.Job.Name}: {e.State}");
        switch (e.State)
        {
            case BackgroundJobState.Queued:
                Interlocked.Increment(ref _pendingJobs);
                UpdateStatusFromPending($"{e.Job.Name} queued.");
                break;
            case BackgroundJobState.Running:
                UpdateStatusFromPending($"{e.Job.Name} running...");
                break;
            case BackgroundJobState.Completed:
                Interlocked.Decrement(ref _pendingJobs);
                _ = LoadTracksAsync();
                UpdateStatusFromPending($"{e.Job.Name} completed.");
                break;
            case BackgroundJobState.Failed:
                Interlocked.Decrement(ref _pendingJobs);
                if (e.Error != null)
                {
                    Console.Error.WriteLine($"[Jobs] {e.Job.Name} failed: {e.Error}");
                }
                UpdateStatusFromPending($"{e.Job.Name} failed.");
                break;
            case BackgroundJobState.Canceled:
                Interlocked.Decrement(ref _pendingJobs);
                UpdateStatusFromPending($"{e.Job.Name} canceled.");
                break;
        }
    }

    private void UpdateStatusFromPending(string message)
    {
        var pending = Math.Max(0, Interlocked.CompareExchange(ref _pendingJobs, 0, 0));
        var finalMessage = pending > 0 ? $"{message} ({pending} job{(pending == 1 ? "" : "s")} queued)" : message;
        Dispatcher.UIThread.Post(() => StatusMessage = finalMessage);
    }

    private static TrackRowViewModel MapTrack(discoteka_cli.Models.TrackLibraryTrack track)
    {
        var trackId = 0L;
        if (!string.IsNullOrWhiteSpace(track.TrackId))
        {
            long.TryParse(track.TrackId, out trackId);
        }

        var title = string.IsNullOrWhiteSpace(track.TrackTitle) ? "Untitled" : track.TrackTitle!;
        var artist = string.IsNullOrWhiteSpace(track.TrackArtist) ? "Unknown Artist" : track.TrackArtist!;
        var album = string.IsNullOrWhiteSpace(track.AlbumTitle) ? "Unknown Album" : track.AlbumTitle!;
        var genre = string.IsNullOrWhiteSpace(track.Genre) ? "-" : track.Genre!;

        var durationText = "-";
        if (track.Duration.HasValue)
        {
            var span = TimeSpan.FromSeconds(track.Duration.Value);
            durationText = span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss")
                : span.ToString(@"m\:ss");
        }

        var plays = track.Plays;
        var playsText = plays.HasValue ? plays.Value.ToString() : "-";
        var formats = "-";
        if (!string.IsNullOrWhiteSpace(track.FilePath))
        {
            var ext = Path.GetExtension(track.FilePath).TrimStart('.').ToUpperInvariant();
            formats = string.IsNullOrWhiteSpace(ext) ? "-" : ext;
        }

        var subtitle = FormatSubtitle(track.DjTags);
        return new TrackRowViewModel(trackId, title, subtitle, artist, album, track.TrackNumber, durationText, track.Duration, genre, formats, playsText, plays, track.FilePath);
    }

    private void CycleSort(SortColumn column)
    {
        var firstDirection = column == SortColumn.Plays ? SortDirection.Descending : SortDirection.Ascending;
        var secondDirection = firstDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;

        if (_activeSortColumn != column)
        {
            _activeSortColumn = column;
            _activeSortDirection = firstDirection;
        }
        else if (_activeSortDirection == firstDirection)
        {
            _activeSortDirection = secondDirection;
        }
        else if (_activeSortDirection == secondDirection)
        {
            _activeSortColumn = SortColumn.None;
            _activeSortDirection = SortDirection.None;
        }
        else
        {
            _activeSortDirection = firstDirection;
        }

        ApplyCurrentSort();
    }

    private void ApplyCurrentSort()
    {
        IEnumerable<TrackRowViewModel> sorted;
        if (_activeSortColumn == SortColumn.None || _activeSortDirection == SortDirection.None)
        {
            sorted = ApplyDefaultSort(_allTracks);
        }
        else
        {
            sorted = ApplyColumnSort(_allTracks, _activeSortColumn, _activeSortDirection);
        }

        Tracks.Clear();
        foreach (var row in sorted)
        {
            Tracks.Add(row);
        }

        _trackCount = Tracks.Count;
        OnPropertyChanged(nameof(TrackCountText));
        OnPropertyChanged(nameof(LibrarySubtitleText));
    }

    private void RebuildVisibleTracks()
    {
        IEnumerable<TrackRowViewModel> filtered = _libraryRows;
        filtered = _smartFilterMode switch
        {
            SmartFilterMode.AvailableLocally => filtered.Where(row => HasLocalFile(row.FilePath)),
            SmartFilterMode.NoLocalFile => filtered.Where(row => !HasLocalFile(row.FilePath)),
            _ => filtered
        };

        _allTracks.Clear();
        _allTracks.AddRange(filtered);
        if (IsArtistsView)
        {
            _ = LoadArtistIndexAsync();
        }
        ApplyCurrentSort();
    }

    private async Task LoadArtistIndexAsync()
    {
        var loadVersion = Interlocked.Increment(ref _artistIndexLoadVersion);
        try
        {
            var indexedRows = await _trackRepository.GetIndexedArtistAlbumsAsync(MapSmartFilterToIndexedQuery());
            var artists = indexedRows
                .GroupBy(row => (row.ArtistId, row.ArtistName))
                .OrderBy(group => group.Key.ArtistName, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var artist = new ArtistGroupViewModel(group.Key.ArtistId, group.Key.ArtistName);
                    foreach (var albumRow in group.OrderBy(a => a.AlbumTitle, StringComparer.OrdinalIgnoreCase))
                    {
                        artist.Albums.Add(new AlbumGroupViewModel(
                            artist,
                            albumRow.AlbumId,
                            albumRow.AlbumTitle,
                            albumRow.AlbumTrackCount));
                    }

                    return artist;
                })
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (loadVersion != Volatile.Read(ref _artistIndexLoadVersion))
                {
                    return;
                }

                ArtistGroups.Clear();
                foreach (var artist in artists)
                {
                    ArtistGroups.Add(artist);
                }

                OnPropertyChanged(nameof(LibrarySubtitleText));
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Artists] Failed to load indexed artist view: {ex}");
        }
    }

    private IEnumerable<TrackRowViewModel> ApplyDefaultSort(IEnumerable<TrackRowViewModel> rows)
    {
        return _defaultSort switch
        {
            DefaultSortOption.Title => rows
                .OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Artist, StringComparer.OrdinalIgnoreCase),
            DefaultSortOption.Artist => rows
                .OrderBy(row => row.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase),
            DefaultSortOption.RecentlyAdded => rows
                .OrderByDescending(row => row.TrackId),
            _ => rows
        };
    }

    private static IEnumerable<TrackRowViewModel> ApplyColumnSort(IEnumerable<TrackRowViewModel> rows, SortColumn column, SortDirection direction)
    {
        return column switch
        {
            SortColumn.Title => Order(rows, row => row.Title, direction),
            SortColumn.Artist => Order(rows, row => row.Artist, direction),
            SortColumn.Album => Order(rows, row => row.Album, direction),
            SortColumn.Time => Order(rows, row => row.DurationSeconds ?? int.MinValue, direction),
            SortColumn.Genre => Order(rows, row => row.Genre, direction),
            SortColumn.Formats => Order(rows, row => row.Formats, direction),
            SortColumn.Plays => Order(rows, row => row.Plays ?? int.MinValue, direction),
            _ => rows
        };
    }

    private static IOrderedEnumerable<TrackRowViewModel> Order<T>(IEnumerable<TrackRowViewModel> rows, Func<TrackRowViewModel, T> keySelector, SortDirection direction)
    {
        return direction == SortDirection.Descending
            ? rows.OrderByDescending(keySelector).ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(keySelector).ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase);
    }

    private void SetLibraryViewMode(LibraryViewMode mode)
    {
        if (_libraryViewMode == mode)
        {
            return;
        }

        _libraryViewMode = mode;
        OnPropertyChanged(nameof(IsAllMusicView));
        OnPropertyChanged(nameof(IsArtistsView));
        OnPropertyChanged(nameof(IsAlbumsView));
        OnPropertyChanged(nameof(LibraryViewTitle));
        OnPropertyChanged(nameof(LibrarySubtitleText));
        UpdateStatusFromPending($"{LibraryViewTitle} view selected.");
        if (mode == LibraryViewMode.Artists)
        {
            _ = LoadArtistIndexAsync();
        }
    }

    private void SetSmartFilter(SmartFilterMode mode)
    {
        _smartFilterMode = mode;
        Console.WriteLine($"[Library] Smart filter set to {_smartFilterMode}.");
        RebuildVisibleTracks();
        var label = mode switch
        {
            SmartFilterMode.AvailableLocally => "Available Locally",
            SmartFilterMode.NoLocalFile => "No Local File",
            _ => "All Tracks"
        };
        UpdateStatusFromPending($"Smart filter: {label}.");
    }

    private bool? MapSmartFilterToIndexedQuery()
    {
        return _smartFilterMode switch
        {
            SmartFilterMode.AvailableLocally => true,
            SmartFilterMode.NoLocalFile => false,
            _ => null
        };
    }

    private async Task EnsureAlbumTracksLoadedAsync(AlbumGroupViewModel album)
    {
        if (album.IsTracksLoaded)
        {
            return;
        }

        try
        {
            var tracks = await _trackRepository.GetIndexedAlbumTracksAsync(album.AlbumId, MapSmartFilterToIndexedQuery());
            var rows = tracks.Select(MapTrack).ToList();
            album.SetTracks(rows);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Artists] Failed to load tracks for album {album.AlbumId}: {ex}");
            album.SetTracks(new List<TrackRowViewModel>());
        }
    }

    private void OnPlaybackStateChanged(PlaybackState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = state.IsPlaying;
            PlaybackDurationSeconds = Math.Max(1, state.DurationMs / 1000.0);
            PlaybackPositionSeconds = Math.Clamp(state.PositionMs / 1000.0, 0, PlaybackDurationSeconds);
            NowPlayingTimeText = $"{FormatTime(state.PositionMs)} / {FormatTime(state.DurationMs)}";

            _suppressVolumeUpdate = true;
            Volume = state.Volume;
            _suppressVolumeUpdate = false;

            var shuffleChanged = _shuffleEnabled != state.ShuffleEnabled;
            _shuffleEnabled = state.ShuffleEnabled;
            if (shuffleChanged)
            {
                OnPropertyChanged(nameof(ShuffleButtonText));
            }

            var repeatChanged = _playerRepeatMode != state.RepeatMode;
            _playerRepeatMode = state.RepeatMode;
            if (repeatChanged)
            {
                OnPropertyChanged(nameof(RepeatButtonText));
            }
        });

        TryRecordPlaybackThreshold(state);
    }

    private void OnCurrentTrackChanged(PlaybackTrack? track)
    {
        _currentPlaybackTrackId = track?.TrackId;
        _currentPlaybackTrackCounted = false;

        Dispatcher.UIThread.Post(() =>
        {
            NowPlayingTitle = track == null
                ? "No track selected"
                : string.IsNullOrWhiteSpace(track.Artist)
                    ? track.Title
                    : $"{track.Title} - {track.Artist}";
        });
    }

    private void OnPlaybackEnded()
    {
        Console.WriteLine("[Playback] Track ended. Moving to next track...");
        Dispatcher.UIThread.Post(() =>
        {
            if (!TryPlayDirection(skipMissingFiles: true, forward: true))
            {
                IsPlaying = false;
                UpdateStatusFromPending("Reached end of queue.");
            }
        });
    }

    private void OnPlaybackError(string message)
    {
        Console.Error.WriteLine($"[Playback] Error: {message}");
        UpdateStatusFromPending(message);
    }

    private void TryRecordPlaybackThreshold(PlaybackState state)
    {
        if (!state.IsPlaying)
        {
            return;
        }

        if (_currentPlaybackTrackCounted || !_currentPlaybackTrackId.HasValue)
        {
            return;
        }

        if (state.DurationMs <= 0 || state.PositionMs <= 0)
        {
            return;
        }

        if (state.PositionMs * 2 <= state.DurationMs)
        {
            return;
        }

        var trackId = _currentPlaybackTrackId.Value;
        if (trackId <= 0)
        {
            return;
        }
        _currentPlaybackTrackCounted = true;
        Console.WriteLine($"[Playback] Track {trackId} crossed 50% threshold. Recording play + recent activity.");
        _ = Task.Run(async () =>
        {
            try
            {
                await _trackRepository.IncrementPlayCountAsync(trackId);
                await _trackRepository.InsertRecentActivityAsync(trackId, DateTime.UtcNow);
                Console.WriteLine($"[Playback] Recorded play and recent activity for track {trackId}.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Playback] Failed to record history for track {trackId}: {ex}");
            }
        });
    }

    private bool TryPlayDirection(bool skipMissingFiles, bool forward)
    {
        if (_playbackService == null)
        {
            return false;
        }

        var attempts = 0;
        var maxAttempts = Math.Max(1, _playbackService.Queue.Count);
        while (attempts < maxAttempts)
        {
            var before = _playbackService.CurrentQueueIndex;
            var moved = forward ? _playbackService.PlayNext() : _playbackService.PlayPrevious();
            if (moved)
            {
                return true;
            }

            var after = _playbackService.CurrentQueueIndex;
            if (!skipMissingFiles || after == before)
            {
                break;
            }

            attempts++;
        }

        return false;
    }

    private static PlaybackTrack MapPlaybackTrack(TrackRowViewModel row)
    {
        return new PlaybackTrack(row.TrackId, row.Title, row.Artist, row.FilePath);
    }

    private static bool HasLocalFile(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private bool PlayTrackSequence(IEnumerable<TrackRowViewModel> rows, out string? userError)
    {
        userError = null;
        if (_playbackService == null)
        {
            userError = "Playback unavailable.";
            return false;
        }

        var queue = rows.Select(MapPlaybackTrack).ToList();
        if (queue.Count == 0)
        {
            return false;
        }

        var startIndex = queue.FindIndex(track => HasLocalFile(track.FilePath));
        if (startIndex < 0)
        {
            userError = "No local file!";
            return false;
        }

        _playbackService.SetQueue(queue, startIndex);
        var started = _playbackService.PlayAtIndex(startIndex);
        if (!started)
        {
            userError = "No local file!";
        }

        return started;
    }

    private static string FormatTime(long milliseconds)
    {
        if (milliseconds <= 0)
        {
            return "0:00";
        }

        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss")
            : span.ToString(@"m\:ss");
    }

    private static string FormatSubtitle(string? djTagsJson)
    {
        if (string.IsNullOrWhiteSpace(djTagsJson))
        {
            return "-";
        }

        try
        {
            var tags = JsonSerializer.Deserialize<string[]>(djTagsJson);
            if (tags == null || tags.Length == 0)
            {
                return "-";
            }

            var values = tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .ToArray();
            return values.Length == 0 ? "-" : string.Join(", ", values);
        }
        catch
        {
            return djTagsJson;
        }
    }

    public sealed class ArtistGroupViewModel : ViewModelBase
    {
        private bool _isExpanded;
        private AlbumGroupViewModel? _selectedAlbum;

        public ArtistGroupViewModel(long artistId, string name)
        {
            ArtistId = artistId;
            Name = name;
        }

        public long ArtistId { get; }
        public string Name { get; }
        public ObservableCollection<AlbumGroupViewModel> Albums { get; } = new();

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
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

        public void ToggleSelectedAlbum(AlbumGroupViewModel album)
        {
            IsExpanded = true;
            SelectedAlbum = ReferenceEquals(SelectedAlbum, album) ? null : album;
        }
    }

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

    private sealed class NoOpLibraryImportJobs : ILibraryImportJobs
    {
        public ValueTask QueueAppleMusicImportAsync(string xmlPath, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask QueueMediaScanAsync(string rootPath, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask QueueCleanupAsync(double minConfidence, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask QueueMatchRescanAsync(double minScore, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask QueueNormalizeAndMatchAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask QueueRebuildIndexAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
