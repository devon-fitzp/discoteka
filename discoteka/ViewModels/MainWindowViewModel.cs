using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private int _albumIndexLoadVersion;
    private IReadOnlyList<discoteka_cli.Models.IndexedArtistAlbumEntry>? _cachedIndexedArtistAlbumRows;
    private bool? _cachedIndexedArtistAlbumRowsRequireLocalFile;
    private IReadOnlyList<ArtistGroupViewModel>? _cachedArtistBrowserGroups;
    private bool? _cachedArtistBrowserGroupsRequireLocalFile;
    private IReadOnlyList<AlbumBrowserItemViewModel>? _cachedAlbumBrowserGroups;
    private bool? _cachedAlbumBrowserGroupsRequireLocalFile;
    private bool _suppressVolumeUpdate;
    private bool _shuffleEnabled;
    private RepeatMode _playerRepeatMode = RepeatMode.Off;
    private long? _currentPlaybackTrackId;
    private bool _currentPlaybackTrackCounted;
    private string _nowPlayingTitle = "No track selected";
    private string _nowPlayingArtist = string.Empty;
    private string _nowPlayingTimeText = "0:00 / 0:00";
    private double _playbackPositionSeconds;
    private double _playbackDurationSeconds = 100;
    private int _volume = 70;
    private bool _isPlaying;
    private string _statusMessage = string.Empty;
    private CancellationTokenSource? _statusDecayCts;
    private int _trackCount;
    private ArtistGroupViewModel? _selectedArtistGroup;
    private AlbumBrowserItemViewModel? _selectedAlbumsViewAlbum;
    private IReadOnlyList<ArtistGroupViewModel> _artistGroups = Array.Empty<ArtistGroupViewModel>();
    private IReadOnlyList<AlbumBrowserItemViewModel> _albumGroups = Array.Empty<AlbumBrowserItemViewModel>();
    private IReadOnlyList<AlbumBrowserItemViewModel> _visibleAlbumGroups = Array.Empty<AlbumBrowserItemViewModel>();
    private const int AlbumGridPageSize = 200;
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
    public IReadOnlyList<ArtistGroupViewModel> ArtistGroups
    {
        get => _artistGroups;
        private set
        {
            if (SetProperty(ref _artistGroups, value))
            {
                if (_selectedArtistGroup != null && !_artistGroups.Contains(_selectedArtistGroup))
                {
                    SelectedArtistGroup = null;
                }
            }
        }
    }
    public ArtistGroupViewModel? SelectedArtistGroup
    {
        get => _selectedArtistGroup;
        set
        {
            if (SetProperty(ref _selectedArtistGroup, value))
            {
                if (value != null)
                {
                    Console.WriteLine($"[Artists][Select] Artist '{value.Name}' (albums={value.AlbumCount})");
                    value.IsExpanded = true; // reuses lazy album materialization
                }

                OnPropertyChanged(nameof(HasSelectedArtistGroup));
            }
        }
    }
    public bool HasSelectedArtistGroup => SelectedArtistGroup != null;
    public IReadOnlyList<AlbumBrowserItemViewModel> AlbumGroups
    {
        get => _albumGroups;
        private set
        {
            if (SetProperty(ref _albumGroups, value))
            {
                ResetVisibleAlbumGroups();
            }
        }
    }
    public IReadOnlyList<AlbumBrowserItemViewModel> VisibleAlbumGroups
    {
        get => _visibleAlbumGroups;
        private set
        {
            if (SetProperty(ref _visibleAlbumGroups, value))
            {
                OnPropertyChanged(nameof(CanLoadMoreAlbumGroups));
                OnPropertyChanged(nameof(AlbumGridStatusText));
            }
        }
    }
    public AlbumBrowserItemViewModel? SelectedAlbumsViewAlbum
    {
        get => _selectedAlbumsViewAlbum;
        private set
        {
            if (SetProperty(ref _selectedAlbumsViewAlbum, value))
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

    public string NowPlayingArtist
    {
        get => _nowPlayingArtist;
        private set => SetProperty(ref _nowPlayingArtist, value);
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
            LibraryViewMode.Albums => AlbumGroups.Count == 1 ? "1 album" : $"{AlbumGroups.Count} albums",
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
        Console.WriteLine($"[PlaybackVM] PlayTrackFromVisibleIndex requested index={index}, visibleCount={Tracks.Count}");
        if (_playbackService == null)
        {
            userError = "Playback unavailable.";
            Console.Error.WriteLine("[PlaybackVM] Playback unavailable in PlayTrackFromVisibleIndex");
            return false;
        }

        if (index < 0 || index >= Tracks.Count)
        {
            Console.Error.WriteLine($"[PlaybackVM] Visible index out of range: {index}");
            return false;
        }

        var visible = Tracks.ToList();
        var selected = visible[index];
        if (!HasLocalFile(selected.FilePath))
        {
            userError = "No local file!";
            Console.Error.WriteLine($"[PlaybackVM] Selected visible track has no local file: id={selected.TrackId}, title='{selected.Title}'");
            return false;
        }

        var queue = visible
            .Select(MapPlaybackTrack)
            .ToList();
        _playbackService.SetQueue(queue, index);
        var started = _playbackService.PlayAtIndex(index);
        Console.WriteLine($"[PlaybackVM] Play from visible index {index}: {(started ? "started" : "failed")} (queueCount={queue.Count})");
        if (!started)
        {
            userError = "No local file!";
        }

        return started;
    }

    public bool TogglePlayPause(int preferredIndex, out string? userError)
    {
        userError = null;
        Console.WriteLine($"[PlaybackVM] TogglePlayPause preferredIndex={preferredIndex}");
        if (_playbackService == null)
        {
            userError = "Playback unavailable.";
            Console.Error.WriteLine("[PlaybackVM] Playback unavailable in TogglePlayPause");
            return false;
        }

        if (_playbackService.CurrentTrack == null)
        {
            var startIndex = preferredIndex >= 0 && preferredIndex < Tracks.Count ? preferredIndex : 0;
            Console.WriteLine($"[PlaybackVM] No current track, starting from visible index {startIndex}");
            return PlayTrackFromVisibleIndex(startIndex, out userError);
        }

        if (_playbackService.State.IsPlaying)
        {
            Console.WriteLine("[PlaybackVM] Current state is playing, issuing Pause");
            _playbackService.Pause();
        }
        else
        {
            Console.WriteLine("[PlaybackVM] Current state is not playing, issuing Resume");
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

        Console.WriteLine("[PlaybackVM] PlayNext requested by UI");
        TryPlayDirection(skipMissingFiles: true, forward: true);
    }

    public void PlayPrevious()
    {
        if (_playbackService == null)
        {
            return;
        }

        Console.WriteLine("[PlaybackVM] PlayPrevious requested by UI");
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

        Console.WriteLine($"[PlaybackVM] PlayArtistAlbumAsync albumId={album.AlbumId}, title='{album.Title}', loaded={album.IsTracksLoaded}");
        await EnsureAlbumTracksLoadedAsync(album);
        Console.WriteLine($"[PlaybackVM] Artist album tracks ready count={album.Tracks.Count} for albumId={album.AlbumId}");
        var started = PlayTrackSequence(album.Tracks, out var userError);
        Console.WriteLine($"[PlaybackVM] PlayArtistAlbumAsync result started={started}, userError={userError ?? "<null>"}");
        return (started, userError);
    }

    public async Task ToggleAlbumsViewAlbumAsync(AlbumBrowserItemViewModel? album)
    {
        if (album == null)
        {
            return;
        }

        await EnsureAlbumTracksLoadedAsync(album);
        foreach (var other in AlbumGroups)
        {
            if (!ReferenceEquals(other, album) && other.IsExpanded)
            {
                other.IsExpanded = false;
            }
        }

        var willExpand = !album.IsExpanded;
        album.IsExpanded = willExpand;
        SelectedAlbumsViewAlbum = willExpand ? album : null;
    }

    public async Task<(bool Started, string? UserError)> PlayAlbumsViewAlbumAsync(AlbumBrowserItemViewModel? album)
    {
        if (album == null)
        {
            return (false, null);
        }

        Console.WriteLine($"[PlaybackVM] PlayAlbumsViewAlbumAsync albumId={album.AlbumId}, title='{album.Title}', loaded={album.IsTracksLoaded}");
        await EnsureAlbumTracksLoadedAsync(album);
        Console.WriteLine($"[PlaybackVM] Albums view album tracks ready count={album.Tracks.Count} for albumId={album.AlbumId}");
        var started = PlayTrackSequence(album.Tracks, out var userError);
        Console.WriteLine($"[PlaybackVM] PlayAlbumsViewAlbumAsync result started={started}, userError={userError ?? "<null>"}");
        return (started, userError);
    }

    public void LoadMoreAlbumsViewPage()
    {
        if (!CanLoadMoreAlbumGroups)
        {
            return;
        }

        var nextCount = Math.Min(AlbumGroups.Count, VisibleAlbumGroups.Count + AlbumGridPageSize);
        VisibleAlbumGroups = AlbumGroups.Take(nextCount).ToList();
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
        Dispatcher.UIThread.Post(() => SetStatusWithDecay(finalMessage));
    }

    private void SetStatusWithDecay(string message)
    {
        _statusDecayCts?.Cancel();
        _statusDecayCts = new CancellationTokenSource();
        var token = _statusDecayCts.Token;
        StatusMessage = message;
        Task.Delay(10_000, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                Dispatcher.UIThread.Post(() => StatusMessage = string.Empty);
        }, TaskScheduler.Default);
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
        InvalidateIndexedBrowserCache();

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
        else if (IsAlbumsView)
        {
            _ = LoadAlbumIndexAsync();
        }
        ApplyCurrentSort();
    }

    private async Task LoadArtistIndexAsync()
    {
        var loadVersion = Interlocked.Increment(ref _artistIndexLoadVersion);
        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            Console.WriteLine($"[Artists][Load] Start (version={loadVersion}, filter={_smartFilterMode}, mem={GC.GetTotalMemory(false) / (1024 * 1024)} MB)");
            var fetchStopwatch = Stopwatch.StartNew();
            var indexedRows = await GetIndexedArtistAlbumRowsAsync();
            fetchStopwatch.Stop();
            Console.WriteLine($"[Artists][Load] Indexed rows fetched: {indexedRows.Count} in {fetchStopwatch.ElapsedMilliseconds} ms (mem={GC.GetTotalMemory(false) / (1024 * 1024)} MB)");
            var requireLocalFile = MapSmartFilterToIndexedQuery();
            if (_cachedArtistBrowserGroups != null && _cachedArtistBrowserGroupsRequireLocalFile == requireLocalFile)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (loadVersion != Volatile.Read(ref _artistIndexLoadVersion))
                    {
                        return;
                    }

                    ArtistGroups = _cachedArtistBrowserGroups;
                    OnPropertyChanged(nameof(LibrarySubtitleText));
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

            // SQL already orders by artist/album, so build the tree in a single pass to avoid LINQ grouping allocations.
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
                if (loadVersion != Volatile.Read(ref _artistIndexLoadVersion))
                {
                    Console.WriteLine($"[Artists][Load] Skipped stale UI apply (version={loadVersion})");
                    return;
                }

                _cachedArtistBrowserGroups = artists;
                _cachedArtistBrowserGroupsRequireLocalFile = requireLocalFile;
                ArtistGroups = artists;

                OnPropertyChanged(nameof(LibrarySubtitleText));
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

    private async Task LoadAlbumIndexAsync()
    {
        var loadVersion = Interlocked.Increment(ref _albumIndexLoadVersion);
        try
        {
            var indexedRows = await GetIndexedArtistAlbumRowsAsync();
            var requireLocalFile = MapSmartFilterToIndexedQuery();
            if (_cachedAlbumBrowserGroups != null && _cachedAlbumBrowserGroupsRequireLocalFile == requireLocalFile)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (loadVersion != Volatile.Read(ref _albumIndexLoadVersion))
                    {
                        return;
                    }

                    AlbumGroups = _cachedAlbumBrowserGroups;
                    SelectedAlbumsViewAlbum = null;
                    OnPropertyChanged(nameof(LibrarySubtitleText));
                });
                return;
            }

            var albums = indexedRows
                .GroupBy(row => row.AlbumId)
                .Select(group =>
                {
                    var first = group.First();
                    var artistName = string.IsNullOrWhiteSpace(first.AlbumArtistName)
                        ? group.Select(g => g.ArtistName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Unknown Artist"
                        : first.AlbumArtistName;
                    var maxTracks = group.Max(g => g.AlbumTrackCount);

                    return new AlbumBrowserItemViewModel(first.AlbumId, first.AlbumTitle, artistName, first.ReleaseYear, maxTracks);
                })
                .OrderBy(album => album.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(album => album.ArtistName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (loadVersion != Volatile.Read(ref _albumIndexLoadVersion))
                {
                    return;
                }

                _cachedAlbumBrowserGroups = albums;
                _cachedAlbumBrowserGroupsRequireLocalFile = requireLocalFile;
                AlbumGroups = albums;
                SelectedAlbumsViewAlbum = null;

                OnPropertyChanged(nameof(LibrarySubtitleText));
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Albums] Failed to load indexed album view: {ex}");
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
            ClearAlbumBrowserState();
        }
        else if (mode == LibraryViewMode.Albums)
        {
            ClearArtistBrowserState();
        }
        else
        {
            ClearArtistBrowserState();
            ClearAlbumBrowserState();
        }

        if (mode == LibraryViewMode.Artists)
        {
            _ = LoadArtistIndexAsync();
        }
        else if (mode == LibraryViewMode.Albums)
        {
            _ = LoadAlbumIndexAsync();
        }
    }

    private void SetSmartFilter(SmartFilterMode mode)
    {
        if (_smartFilterMode == mode)
        {
            return;
        }

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

    private async Task<IReadOnlyList<discoteka_cli.Models.IndexedArtistAlbumEntry>> GetIndexedArtistAlbumRowsAsync()
    {
        var requireLocalFile = MapSmartFilterToIndexedQuery();
        if (_cachedIndexedArtistAlbumRows != null && _cachedIndexedArtistAlbumRowsRequireLocalFile == requireLocalFile)
        {
            return _cachedIndexedArtistAlbumRows;
        }

        var rows = await _trackRepository.GetIndexedArtistAlbumsAsync(requireLocalFile);
        _cachedIndexedArtistAlbumRows = rows;
        _cachedIndexedArtistAlbumRowsRequireLocalFile = requireLocalFile;
        return rows;
    }

    private void InvalidateIndexedBrowserCache()
    {
        _cachedIndexedArtistAlbumRows = null;
        _cachedIndexedArtistAlbumRowsRequireLocalFile = null;
        _cachedArtistBrowserGroups = null;
        _cachedArtistBrowserGroupsRequireLocalFile = null;
        _cachedAlbumBrowserGroups = null;
        _cachedAlbumBrowserGroupsRequireLocalFile = null;
    }

    private void ClearArtistBrowserState()
    {
        SelectedArtistGroup = null;
        ArtistGroups = Array.Empty<ArtistGroupViewModel>();
        OnPropertyChanged(nameof(LibrarySubtitleText));
    }

    private void ClearAlbumBrowserState()
    {
        AlbumGroups = Array.Empty<AlbumBrowserItemViewModel>();
        VisibleAlbumGroups = Array.Empty<AlbumBrowserItemViewModel>();
        SelectedAlbumsViewAlbum = null;
        OnPropertyChanged(nameof(LibrarySubtitleText));
    }

    private void ResetVisibleAlbumGroups()
    {
        if (AlbumGroups.Count == 0)
        {
            VisibleAlbumGroups = Array.Empty<AlbumBrowserItemViewModel>();
            return;
        }

        VisibleAlbumGroups = AlbumGroups.Take(Math.Min(AlbumGroups.Count, AlbumGridPageSize)).ToList();
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

    private async Task EnsureAlbumTracksLoadedAsync(AlbumBrowserItemViewModel album)
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
            Console.Error.WriteLine($"[Albums] Failed to load tracks for album {album.AlbumId}: {ex}");
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
        Console.WriteLine(track == null
            ? "[PlaybackVM] OnCurrentTrackChanged -> <null>"
            : $"[PlaybackVM] OnCurrentTrackChanged -> id={track.TrackId}, title='{track.Title}', artist='{track.Artist}'");
        _currentPlaybackTrackId = track?.TrackId;
        _currentPlaybackTrackCounted = false;

        Dispatcher.UIThread.Post(() =>
        {
            NowPlayingTitle = track == null ? "No track selected" : track.Title;
            NowPlayingArtist = track == null ? string.Empty : (track.Artist ?? string.Empty);
        });
    }

    private void OnPlaybackEnded()
    {
        Console.WriteLine("[PlaybackVM] OnPlaybackEnded received. Scheduling auto-advance.");
        Dispatcher.UIThread.Post(() =>
        {
            Console.WriteLine("[PlaybackVM] Auto-advance executing on UI thread.");
            if (!TryPlayDirection(skipMissingFiles: true, forward: true))
            {
                Console.WriteLine("[PlaybackVM] Auto-advance failed. Marking queue ended.");
                IsPlaying = false;
                UpdateStatusFromPending("Reached end of queue.");
            }
            else
            {
                Console.WriteLine("[PlaybackVM] Auto-advance succeeded.");
            }
        });
    }

    private void OnPlaybackError(string message)
    {
        Console.Error.WriteLine($"[PlaybackVM] Error: {message}");
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
            Console.Error.WriteLine("[PlaybackVM] TryPlayDirection aborted: playback service unavailable");
            return false;
        }

        Console.WriteLine($"[PlaybackVM] TryPlayDirection start forward={forward}, skipMissing={skipMissingFiles}, queueCount={_playbackService.Queue.Count}, currentIdx={_playbackService.CurrentQueueIndex}");
        var attempts = 0;
        var maxAttempts = Math.Max(1, _playbackService.Queue.Count);
        while (attempts < maxAttempts)
        {
            var before = _playbackService.CurrentQueueIndex;
            var moved = forward ? _playbackService.PlayNext() : _playbackService.PlayPrevious();
            Console.WriteLine($"[PlaybackVM] TryPlayDirection attempt={attempts + 1}/{maxAttempts}, before={before}, moved={moved}, after={_playbackService.CurrentQueueIndex}");
            if (moved)
            {
                Console.WriteLine("[PlaybackVM] TryPlayDirection success");
                return true;
            }

            var after = _playbackService.CurrentQueueIndex;
            if (!skipMissingFiles || after == before)
            {
                Console.WriteLine($"[PlaybackVM] TryPlayDirection stopping (skipMissing={skipMissingFiles}, after==before={after == before})");
                break;
            }

            attempts++;
        }

        Console.WriteLine("[PlaybackVM] TryPlayDirection failed");
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
        Console.WriteLine("[PlaybackVM] PlayTrackSequence start");
        if (_playbackService == null)
        {
            userError = "Playback unavailable.";
            Console.Error.WriteLine("[PlaybackVM] PlayTrackSequence aborted: playback unavailable");
            return false;
        }

        var queue = rows.Select(MapPlaybackTrack).ToList();
        var localCount = queue.Count(track => HasLocalFile(track.FilePath));
        Console.WriteLine($"[PlaybackVM] PlayTrackSequence queue built count={queue.Count}, localCount={localCount}");
        if (queue.Count == 0)
        {
            Console.WriteLine("[PlaybackVM] PlayTrackSequence aborted: empty queue");
            return false;
        }

        var startIndex = queue.FindIndex(track => HasLocalFile(track.FilePath));
        if (startIndex < 0)
        {
            userError = "No local file!";
            Console.Error.WriteLine("[PlaybackVM] PlayTrackSequence aborted: no local tracks in queue");
            return false;
        }

        Console.WriteLine($"[PlaybackVM] PlayTrackSequence startIndex={startIndex}, startTrack={queue[startIndex].TrackId}:{queue[startIndex].Title}");
        _playbackService.SetQueue(queue, startIndex);
        var started = _playbackService.PlayAtIndex(startIndex);
        Console.WriteLine($"[PlaybackVM] PlayTrackSequence PlayAtIndex returned {started}");
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
        private readonly List<ArtistAlbumSeed> _albumSeeds = new();
        private ObservableCollection<AlbumGroupViewModel>? _albums;
        private bool _isExpanded;
        private AlbumGroupViewModel? _selectedAlbum;
        private static readonly IReadOnlyList<AlbumGroupViewModel> EmptyAlbums = Array.Empty<AlbumGroupViewModel>();

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
            Console.WriteLine($"[Artists][Expand] Materializing albums for '{Name}' (artistId={ArtistId}, count={_albumSeeds.Count})");
            _albums = new ObservableCollection<AlbumGroupViewModel>();
            foreach (var seed in _albumSeeds)
            {
                _albums.Add(new AlbumGroupViewModel(this, seed.AlbumId, seed.Title, seed.TrackCount));
            }
            stopwatch.Stop();
            Console.WriteLine($"[Artists][Expand] Materialized albums for '{Name}' in {stopwatch.ElapsedMilliseconds} ms (mem={GC.GetTotalMemory(false) / (1024 * 1024)} MB)");
        }

        private readonly record struct ArtistAlbumSeed(long AlbumId, string Title, int TrackCount);
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
