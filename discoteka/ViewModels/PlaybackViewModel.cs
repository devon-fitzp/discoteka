using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using discoteka.Playback;

namespace discoteka.ViewModels;

/// <summary>
/// Owns all playback state and controls. Fires <see cref="StatusRequested"/> for messages
/// that the coordinator should route to the status bar.
/// </summary>
public sealed class PlaybackViewModel : ViewModelBase
{
    private readonly IMediaPlaybackService? _service;
    private readonly LibraryViewModel _library;

    private bool _suppressVolumeUpdate;
    private bool _shuffleEnabled;
    private RepeatMode _repeatMode = RepeatMode.Off;
    private long? _currentTrackId;
    private bool _currentTrackCounted;
    private string _nowPlayingTitle = "No track selected";
    private string _nowPlayingArtist = string.Empty;
    private string _nowPlayingTimeText = "0:00 / 0:00";
    private double _positionSeconds;
    private double _durationSeconds = 100;
    private int _volume = 70;
    private bool _isPlaying;

    public PlaybackViewModel(IMediaPlaybackService? service, LibraryViewModel library)
    {
        _service = service;
        _library = library;

        if (_service != null)
        {
            _service.PlaybackStateChanged += OnPlaybackStateChanged;
            _service.CurrentTrackChanged += OnCurrentTrackChanged;
            _service.PlaybackEnded += OnPlaybackEnded;
            _service.PlaybackError += OnPlaybackError;
        }
    }

    /// <summary>Raised on the UI thread when a status message should be shown by the coordinator.</summary>
    public event Action<string>? StatusRequested;

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
        get => _positionSeconds;
        private set => SetProperty(ref _positionSeconds, value);
    }

    public double PlaybackDurationSeconds
    {
        get => _durationSeconds;
        private set => SetProperty(ref _durationSeconds, value);
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
                _service?.SetVolume(clamped);
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
    public string RepeatButtonText => _repeatMode switch
    {
        RepeatMode.Track => "Repeat: Track",
        RepeatMode.Playlist => "Repeat: Playlist",
        _ => "Repeat: Off"
    };

    public bool PlayTrackFromVisibleIndex(int index, out string? userError)
    {
        userError = null;
        Console.WriteLine($"[PlaybackVM] PlayTrackFromVisibleIndex requested index={index}, visibleCount={_library.Tracks.Count}");
        if (_service == null)
        {
            userError = "Playback unavailable.";
            Console.Error.WriteLine("[PlaybackVM] Playback unavailable in PlayTrackFromVisibleIndex");
            return false;
        }

        if (index < 0 || index >= _library.Tracks.Count)
        {
            Console.Error.WriteLine($"[PlaybackVM] Visible index out of range: {index}");
            return false;
        }

        var visible = _library.Tracks.ToList();
        var selected = visible[index];
        if (!LibraryViewModel.HasLocalFile(selected.FilePath))
        {
            userError = "No local file!";
            Console.Error.WriteLine($"[PlaybackVM] Selected visible track has no local file: id={selected.TrackId}, title='{selected.Title}'");
            return false;
        }

        var queue = visible.Select(MapPlaybackTrack).ToList();
        _service.SetQueue(queue, index);
        var started = _service.PlayAtIndex(index);
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
        if (_service == null)
        {
            userError = "Playback unavailable.";
            Console.Error.WriteLine("[PlaybackVM] Playback unavailable in TogglePlayPause");
            return false;
        }

        if (_service.CurrentTrack == null)
        {
            var startIndex = preferredIndex >= 0 && preferredIndex < _library.Tracks.Count ? preferredIndex : 0;
            Console.WriteLine($"[PlaybackVM] No current track, starting from visible index {startIndex}");
            return PlayTrackFromVisibleIndex(startIndex, out userError);
        }

        if (_service.State.IsPlaying)
        {
            Console.WriteLine("[PlaybackVM] Current state is playing, issuing Pause");
            _service.Pause();
        }
        else
        {
            Console.WriteLine("[PlaybackVM] Current state is not playing, issuing Resume");
            _service.Resume();
        }

        return true;
    }

    public void PlayNext()
    {
        if (_service == null) return;
        Console.WriteLine("[PlaybackVM] PlayNext requested by UI");
        TryPlayDirection(skipMissingFiles: true, forward: true);
    }

    public void PlayPrevious()
    {
        if (_service == null) return;
        Console.WriteLine("[PlaybackVM] PlayPrevious requested by UI");
        TryPlayDirection(skipMissingFiles: true, forward: false);
    }

    public void SeekToSeconds(double seconds)
    {
        _service?.Seek((long)Math.Max(0, seconds * 1000.0));
    }

    public void ToggleShuffle()
    {
        if (_service == null) return;
        _service.SetShuffle(!_shuffleEnabled);
    }

    public void CycleRepeatMode()
    {
        if (_service == null) return;
        var next = _repeatMode switch
        {
            RepeatMode.Off => RepeatMode.Track,
            RepeatMode.Track => RepeatMode.Playlist,
            _ => RepeatMode.Off
        };
        _service.SetRepeatMode(next);
    }

    /// <summary>Queues a sequence of tracks for playback. Returns whether it started and any user-facing error.</summary>
    internal (bool Started, string? UserError) PlayTrackSequence(IEnumerable<TrackRowViewModel> rows)
    {
        Console.WriteLine("[PlaybackVM] PlayTrackSequence start");
        if (_service == null)
        {
            Console.Error.WriteLine("[PlaybackVM] PlayTrackSequence aborted: playback unavailable");
            return (false, "Playback unavailable.");
        }

        var queue = rows.Select(MapPlaybackTrack).ToList();
        var localCount = queue.Count(t => LibraryViewModel.HasLocalFile(t.FilePath));
        Console.WriteLine($"[PlaybackVM] PlayTrackSequence queue built count={queue.Count}, localCount={localCount}");

        if (queue.Count == 0)
        {
            Console.WriteLine("[PlaybackVM] PlayTrackSequence aborted: empty queue");
            return (false, null);
        }

        var startIndex = queue.FindIndex(t => LibraryViewModel.HasLocalFile(t.FilePath));
        if (startIndex < 0)
        {
            Console.Error.WriteLine("[PlaybackVM] PlayTrackSequence aborted: no local tracks in queue");
            return (false, "No local file!");
        }

        Console.WriteLine($"[PlaybackVM] PlayTrackSequence startIndex={startIndex}, startTrack={queue[startIndex].TrackId}:{queue[startIndex].Title}");
        _service.SetQueue(queue, startIndex);
        var started = _service.PlayAtIndex(startIndex);
        Console.WriteLine($"[PlaybackVM] PlayTrackSequence PlayAtIndex returned {started}");
        return started ? (true, null) : (false, "No local file!");
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

            var repeatChanged = _repeatMode != state.RepeatMode;
            _repeatMode = state.RepeatMode;
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
        _currentTrackId = track?.TrackId;
        _currentTrackCounted = false;

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
                StatusRequested?.Invoke("Reached end of queue.");
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
        StatusRequested?.Invoke(message);
    }

    private void TryRecordPlaybackThreshold(PlaybackState state)
    {
        if (!state.IsPlaying || _currentTrackCounted || !_currentTrackId.HasValue)
        {
            return;
        }

        if (state.DurationMs <= 0 || state.PositionMs <= 0 || state.PositionMs * 2 <= state.DurationMs)
        {
            return;
        }

        var trackId = _currentTrackId.Value;
        if (trackId <= 0) return;

        _currentTrackCounted = true;
        Console.WriteLine($"[Playback] Track {trackId} crossed 50% threshold. Recording play + recent activity.");
        _ = Task.Run(async () =>
        {
            try
            {
                await _library.IncrementPlayCountAsync(trackId);
                await _library.InsertRecentActivityAsync(trackId, DateTime.UtcNow);
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
        if (_service == null)
        {
            Console.Error.WriteLine("[PlaybackVM] TryPlayDirection aborted: playback service unavailable");
            return false;
        }

        Console.WriteLine($"[PlaybackVM] TryPlayDirection start forward={forward}, skipMissing={skipMissingFiles}, queueCount={_service.Queue.Count}, currentIdx={_service.CurrentQueueIndex}");
        var attempts = 0;
        var maxAttempts = Math.Max(1, _service.Queue.Count);
        while (attempts < maxAttempts)
        {
            var before = _service.CurrentQueueIndex;
            var moved = forward ? _service.PlayNext() : _service.PlayPrevious();
            Console.WriteLine($"[PlaybackVM] TryPlayDirection attempt={attempts + 1}/{maxAttempts}, before={before}, moved={moved}, after={_service.CurrentQueueIndex}");
            if (moved)
            {
                Console.WriteLine("[PlaybackVM] TryPlayDirection success");
                return true;
            }

            var after = _service.CurrentQueueIndex;
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
        => new PlaybackTrack(row.TrackId, row.Title, row.Artist, row.FilePath);

    private static string FormatTime(long milliseconds)
    {
        if (milliseconds <= 0) return "0:00";
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss")
            : span.ToString(@"m\:ss");
    }
}
