using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Discoteka.Desktop.Playback;
using Discoteka.Core.Database;
using Discoteka.Core.Jobs;

namespace Discoteka.Desktop.ViewModels;

/// <summary>
/// Coordinator view model. Owns the four sub-VMs (<see cref="Library"/>, <see cref="Playback"/>,
/// <see cref="Artists"/>, <see cref="Albums"/>), wires their events together, manages view-mode
/// switching, and routes status messages to the status bar.
/// All XAML bindings target this class via pass-through properties.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ILibraryImportJobs _importJobs;
    private readonly IBackgroundJobQueue? _jobQueue;

    private int _pendingJobs;
    private string _statusMessage = string.Empty;
    private CancellationTokenSource? _statusDecayCts;
    private LibraryViewMode _libraryViewMode = LibraryViewMode.AllMusic;

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

        Library = new LibraryViewModel(trackRepository ?? new TrackLibraryRepository());
        Playback = new PlaybackViewModel(playbackService, Library);
        Artists = new ArtistsBrowserViewModel(Library, tracks => Playback.PlayTrackSequence(tracks));
        Albums = new AlbumsBrowserViewModel(Library, tracks => Playback.PlayTrackSequence(tracks));

        Library.TracksReloaded += OnTracksReloaded;
        Library.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        Playback.StatusRequested += message => Dispatcher.UIThread.Post(() => UpdateStatusFromPending(message));
        Playback.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        Artists.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(e.PropertyName);
            if (e.PropertyName == nameof(ArtistsBrowserViewModel.ArtistGroups))
                OnPropertyChanged(nameof(LibrarySubtitleText));
        };
        Albums.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(e.PropertyName);
            if (e.PropertyName == nameof(AlbumsBrowserViewModel.AlbumGroups))
                OnPropertyChanged(nameof(LibrarySubtitleText));
        };

        if (_jobQueue != null)
        {
            _jobQueue.JobStatusChanged += OnJobStatusChanged;
        }
    }

    // ── Sub-VMs ────────────────────────────────────────────────────────────────

    public LibraryViewModel Library { get; }
    public PlaybackViewModel Playback { get; }
    public ArtistsBrowserViewModel Artists { get; }
    public AlbumsBrowserViewModel Albums { get; }

    // ── Pass-through properties (XAML binds here) ──────────────────────────────

    public string Greeting { get; } = "Welcome to Discoteka.Desktop!";

    // Library
    public BulkObservableCollection<TrackRowViewModel> Tracks => Library.Tracks;
    public string TrackCountText => Library.TrackCountText;

    // Artists browser
    public IReadOnlyList<ArtistGroupViewModel> ArtistGroups => Artists.ArtistGroups;
    public ArtistGroupViewModel? SelectedArtistGroup
    {
        get => Artists.SelectedArtistGroup;
        set => Artists.SelectedArtistGroup = value;
    }
    public bool HasSelectedArtistGroup => Artists.HasSelectedArtistGroup;

    // Albums browser
    public IReadOnlyList<AlbumBrowserItemViewModel> VisibleAlbumGroups => Albums.VisibleAlbumGroups;
    public IReadOnlyList<AlbumBrowserItemViewModel> AlbumGroups => Albums.AlbumGroups;
    public AlbumBrowserItemViewModel? SelectedAlbumsViewAlbum => Albums.SelectedAlbumsViewAlbum;
    public bool HasSelectedAlbumsViewAlbum => Albums.HasSelectedAlbumsViewAlbum;
    public bool CanLoadMoreAlbumGroups => Albums.CanLoadMoreAlbumGroups;
    public string AlbumGridStatusText => Albums.AlbumGridStatusText;

    // Playback
    public string NowPlayingTitle => Playback.NowPlayingTitle;
    public string NowPlayingArtist => Playback.NowPlayingArtist;
    public string NowPlayingTimeText => Playback.NowPlayingTimeText;
    public double PlaybackPositionSeconds => Playback.PlaybackPositionSeconds;
    public double PlaybackDurationSeconds => Playback.PlaybackDurationSeconds;
    public int Volume
    {
        get => Playback.Volume;
        set => Playback.Volume = value;
    }
    public bool IsPlaying => Playback.IsPlaying;
    public string PlayPauseButtonText => Playback.PlayPauseButtonText;
    public string ShuffleButtonText => Playback.ShuffleButtonText;
    public string RepeatButtonText => Playback.RepeatButtonText;

    // ── Coordinator-owned state ────────────────────────────────────────────────

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

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
        ? Library.TrackCountText
        : _libraryViewMode switch
        {
            LibraryViewMode.Artists => Artists.ArtistGroups.Count == 1 ? "1 artist" : $"{Artists.ArtistGroups.Count} artists",
            LibraryViewMode.Albums => Albums.AlbumGroups.Count == 1 ? "1 album" : $"{Albums.AlbumGroups.Count} albums",
            _ => string.Empty
        };

    // ── Initialisation ─────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        SetStatusWithDecay("Loading library...");
        await Library.LoadTracksAsync();
    }

    // ── Import / job queue ─────────────────────────────────────────────────────

    public async Task QueueAppleMusicImportAsync(string xmlPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(xmlPath)) return;
        await _importJobs.QueueAppleMusicImportAsync(xmlPath, cancellationToken);
        UpdateStatusFromPending("Import queued.");
    }

    public async Task QueueMediaScanAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return;
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

    // ── Library: sort / filter / view ─────────────────────────────────────────

    public void CycleSortByTitle() => Library.CycleSortByTitle();
    public void CycleSortByArtist() => Library.CycleSortByArtist();
    public void CycleSortByAlbum() => Library.CycleSortByAlbum();
    public void CycleSortByTime() => Library.CycleSortByTime();
    public void CycleSortByGenre() => Library.CycleSortByGenre();
    public void CycleSortByFormats() => Library.CycleSortByFormats();
    public void CycleSortByPlays() => Library.CycleSortByPlays();

    public void ShowAllMusicView() => SetLibraryViewMode(LibraryViewMode.AllMusic);
    public void ShowArtistsView() => SetLibraryViewMode(LibraryViewMode.Artists);
    public void ShowAlbumsView() => SetLibraryViewMode(LibraryViewMode.Albums);

    public void ClearSmartFilter()
    {
        Library.SetSmartFilter(SmartFilterMode.None);
        UpdateStatusFromPending("Smart filter: All Tracks.");
    }

    public void ShowAvailableLocallyFilter()
    {
        Library.SetSmartFilter(SmartFilterMode.AvailableLocally);
        UpdateStatusFromPending("Smart filter: Available Locally.");
    }

    public void ShowNoLocalFileFilter()
    {
        Library.SetSmartFilter(SmartFilterMode.NoLocalFile);
        UpdateStatusFromPending("Smart filter: No Local File.");
    }

    public void SetDefaultSort(DefaultSortOption option)
    {
        Library.SetDefaultSort(option);
        SetStatusWithDecay(option switch
        {
            DefaultSortOption.Title => "Default sort: Title (A-Z).",
            DefaultSortOption.Artist => "Default sort: Artist (A-Z).",
            DefaultSortOption.RecentlyAdded => "Default sort: Recently Added.",
            _ => string.Empty
        });
    }

    // ── Playback ───────────────────────────────────────────────────────────────

    public bool PlayTrackFromVisibleIndex(int index, out string? userError)
        => Playback.PlayTrackFromVisibleIndex(index, out userError);

    public bool TogglePlayPause(int preferredIndex, out string? userError)
        => Playback.TogglePlayPause(preferredIndex, out userError);

    public void PlayNext() => Playback.PlayNext();
    public void PlayPrevious() => Playback.PlayPrevious();
    public void SeekToSeconds(double seconds) => Playback.SeekToSeconds(seconds);
    public void ToggleShuffle() => Playback.ToggleShuffle();
    public void CycleRepeatMode() => Playback.CycleRepeatMode();

    // ── Browser actions ────────────────────────────────────────────────────────

    public Task ToggleArtistAlbumAsync(AlbumGroupViewModel album) => Artists.ToggleAlbumAsync(album);
    public Task<(bool Started, string? UserError)> PlayArtistAlbumAsync(AlbumGroupViewModel album) => Artists.PlayAlbumAsync(album);

    public Task ToggleAlbumsViewAlbumAsync(AlbumBrowserItemViewModel album) => Albums.ToggleAlbumAsync(album);
    public Task<(bool Started, string? UserError)> PlayAlbumsViewAlbumAsync(AlbumBrowserItemViewModel album) => Albums.PlayAlbumAsync(album);

    public void LoadMoreAlbumsViewPage() => Albums.LoadMorePage();

    // ── Metadata passthrough ───────────────────────────────────────────────────

    public Task<Discoteka.Core.Models.TrackMetadataSnapshot?> GetTrackMetadataSnapshotAsync(long trackId, CancellationToken cancellationToken = default)
        => Library.GetTrackMetadataSnapshotAsync(trackId, cancellationToken);

    public Task SaveTrackMetadataTabAsync(Discoteka.Core.Models.MetadataTabEntry tab, CancellationToken cancellationToken = default)
        => Library.SaveTrackMetadataTabAsync(tab, cancellationToken);

    // ── Private coordinator logic ──────────────────────────────────────────────

    private void OnTracksReloaded()
    {
        if (IsArtistsView)
        {
            RunFireAndForget(Artists.LoadAsync(), "Artists.LoadAsync");
        }
        else if (IsAlbumsView)
        {
            RunFireAndForget(Albums.LoadAsync(), "Albums.LoadAsync");
        }

        OnPropertyChanged(nameof(LibrarySubtitleText));
        UpdateStatusFromPending("Library loaded.");
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
                InvalidateIndexedBrowserCache();
                RunFireAndForget(Library.LoadTracksAsync(), "Library.LoadTracksAsync");
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

    private void SetLibraryViewMode(LibraryViewMode mode)
    {
        if (_libraryViewMode == mode) return;

        _libraryViewMode = mode;
        OnPropertyChanged(nameof(IsAllMusicView));
        OnPropertyChanged(nameof(IsArtistsView));
        OnPropertyChanged(nameof(IsAlbumsView));
        OnPropertyChanged(nameof(LibraryViewTitle));
        OnPropertyChanged(nameof(LibrarySubtitleText));
        UpdateStatusFromPending($"{LibraryViewTitle} view selected.");

        if (mode == LibraryViewMode.Artists)
        {
            Albums.Clear();
            RunFireAndForget(Artists.LoadAsync(), "Artists.LoadAsync");
        }
        else if (mode == LibraryViewMode.Albums)
        {
            Artists.Clear();
            RunFireAndForget(Albums.LoadAsync(), "Albums.LoadAsync");
        }
        else
        {
            Artists.Clear();
            Albums.Clear();
        }
    }

    public void Dispose()
    {
        _statusDecayCts?.Cancel();
        _statusDecayCts?.Dispose();
        _statusDecayCts = null;

        Library.TracksReloaded -= OnTracksReloaded;

        if (_jobQueue != null)
            _jobQueue.JobStatusChanged -= OnJobStatusChanged;

        Playback.Dispose();
    }

    private void InvalidateIndexedBrowserCache()
    {
        Library.InvalidateIndexedRowsCache();
        Artists.InvalidateCache();
        Albums.InvalidateCache();
    }

    /// <summary>
    /// Shows <paramref name="message"/> in the status bar with the standard auto-decay.
    /// Safe to call from any thread.
    /// </summary>
    public void PostStatus(string message) => UpdateStatusFromPending(message);

    private void UpdateStatusFromPending(string message)
    {
        var pending = Math.Max(0, Interlocked.CompareExchange(ref _pendingJobs, 0, 0));
        var finalMessage = pending > 0 ? $"{message} ({pending} job{(pending == 1 ? "" : "s")} queued)" : message;
        Dispatcher.UIThread.Post(() => SetStatusWithDecay(finalMessage));
    }

    private void SetStatusWithDecay(string message)
    {
        _statusDecayCts?.Cancel();
        _statusDecayCts?.Dispose();
        _statusDecayCts = new CancellationTokenSource();
        var token = _statusDecayCts.Token;
        StatusMessage = message;
        Task.Delay(10_000, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                Dispatcher.UIThread.Post(() => StatusMessage = string.Empty);
        }, TaskScheduler.Default);
    }

    private static void RunFireAndForget(Task task, string context)
    {
        task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception?.InnerException is not OperationCanceledException)
                Console.Error.WriteLine($"[{context}] Unhandled: {t.Exception!.InnerException}");
        }, TaskScheduler.Default);
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
