using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibVLCSharp.Shared;

namespace discoteka.Playback;

/// <summary>
/// LibVLCSharp-backed implementation of <see cref="IMediaPlaybackService"/>.
/// <para>
/// LibVLC is initialized with <c>--no-video --quiet</c> and hardware decoding enabled.
/// A single <c>MediaPlayer</c> instance is reused across tracks; media objects are
/// disposed before each new play to avoid resource leaks.
/// </para>
/// <para>
/// All LibVLC events fire on a VLC internal thread. The VM layer (MainWindowViewModel)
/// is responsible for marshalling event callbacks onto the Avalonia UI thread.
/// </para>
/// </summary>
public sealed class MediaPlaybackService : IMediaPlaybackService
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly List<PlaybackTrack> _queue = new();
    private readonly Random _random = new();

    private Media? _currentMedia;
    private PlaybackTrack? _currentTrack;
    private int _currentQueueIndex = -1;
    private bool _disposed;
    private bool _shuffleEnabled;
    private RepeatMode _repeatMode = RepeatMode.Off;

    public MediaPlaybackService()
    {
        LibVlcNativeResolver.Register();
        Core.Initialize();

        _libVlc = new LibVLC(
            "--no-video",
            "--quiet");
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.EnableHardwareDecoding = true;
        _mediaPlayer.Volume = 70;

        _mediaPlayer.Playing += (_, _) =>
        {
            Console.WriteLine($"[PlaybackSvc] Event Playing (idx={_currentQueueIndex}, track={DescribeTrack(_currentTrack)})");
            EmitState();
        };
        _mediaPlayer.Paused += (_, _) =>
        {
            Console.WriteLine($"[PlaybackSvc] Event Paused (idx={_currentQueueIndex}, track={DescribeTrack(_currentTrack)})");
            EmitState();
        };
        _mediaPlayer.Stopped += (_, _) =>
        {
            Console.WriteLine($"[PlaybackSvc] Event Stopped (idx={_currentQueueIndex}, track={DescribeTrack(_currentTrack)})");
            EmitState();
        };
        _mediaPlayer.TimeChanged += (_, _) => EmitState();
        _mediaPlayer.LengthChanged += (_, _) => EmitState();
        _mediaPlayer.EndReached += (_, _) =>
        {
            Console.WriteLine($"[PlaybackSvc] Event EndReached (idx={_currentQueueIndex}, track={DescribeTrack(_currentTrack)})");
            PlaybackEnded?.Invoke();
        };
        _mediaPlayer.EncounteredError += (_, _) =>
        {
            Console.Error.WriteLine($"[PlaybackSvc] Event EncounteredError (idx={_currentQueueIndex}, track={DescribeTrack(_currentTrack)})");
            PlaybackError?.Invoke("Playback error.");
        };
    }

    public event Action<PlaybackState>? PlaybackStateChanged;
    public event Action<PlaybackTrack?>? CurrentTrackChanged;
    public event Action<IReadOnlyList<PlaybackTrack>, int>? QueueChanged;
    public event Action? PlaybackEnded;
    public event Action<string>? PlaybackError;

    public PlaybackTrack? CurrentTrack => _currentTrack;

    public PlaybackState State => new(
        IsPlaying: _mediaPlayer.IsPlaying,
        IsPaused: !_mediaPlayer.IsPlaying && _mediaPlayer.Time > 0,
        PositionMs: _mediaPlayer.Time,
        DurationMs: _mediaPlayer.Length,
        Volume: _mediaPlayer.Volume,
        ShuffleEnabled: _shuffleEnabled,
        RepeatMode: _repeatMode);

    public IReadOnlyList<PlaybackTrack> Queue => _queue;
    public int CurrentQueueIndex => _currentQueueIndex;

    public void SetQueue(IReadOnlyList<PlaybackTrack> tracks, int startIndex = 0)
    {
        ThrowIfDisposed();

        _queue.Clear();
        _queue.AddRange(tracks);

        _currentQueueIndex = _queue.Count == 0
            ? -1
            : Math.Clamp(startIndex, 0, _queue.Count - 1);

        Console.WriteLine($"[PlaybackSvc] SetQueue count={_queue.Count}, requestedStart={startIndex}, actualStart={_currentQueueIndex}");
        if (_queue.Count > 0)
        {
            Console.WriteLine($"[PlaybackSvc] Queue head={DescribeTrack(_queue[0])}, current={DescribeTrack(_queue[_currentQueueIndex])}");
        }
        QueueChanged?.Invoke(_queue, _currentQueueIndex);
    }

    public bool Play(PlaybackTrack track)
    {
        ThrowIfDisposed();
        Console.WriteLine($"[PlaybackSvc] Play requested: {DescribeTrack(track)}");

        if (string.IsNullOrWhiteSpace(track.FilePath))
        {
            Console.Error.WriteLine($"[PlaybackSvc] Play rejected (no file path): {DescribeTrack(track)}");
            PlaybackError?.Invoke("No local file!");
            return false;
        }

        if (!File.Exists(track.FilePath))
        {
            Console.Error.WriteLine($"[PlaybackSvc] Play rejected (missing file): {track.FilePath}");
            PlaybackError?.Invoke("No local file!");
            return false;
        }

        Console.WriteLine($"[PlaybackSvc] Disposing current media before play");
        DisposeCurrentMedia();

        _currentMedia = new Media(_libVlc, new Uri(track.FilePath));
        var started = _mediaPlayer.Play(_currentMedia);
        Console.WriteLine($"[PlaybackSvc] MediaPlayer.Play returned {started} for {DescribeTrack(track)}");
        if (!started)
        {
            PlaybackError?.Invoke("Unable to start playback.");
            return false;
        }

        _currentTrack = track;
        CurrentTrackChanged?.Invoke(_currentTrack);
        EmitState();
        return true;
    }

    public bool PlayAtIndex(int index)
    {
        ThrowIfDisposed();
        Console.WriteLine($"[PlaybackSvc] PlayAtIndex requested index={index} queueCount={_queue.Count}");

        if (index < 0 || index >= _queue.Count)
        {
            Console.Error.WriteLine($"[PlaybackSvc] PlayAtIndex rejected index={index}");
            return false;
        }

        _currentQueueIndex = index;
        Console.WriteLine($"[PlaybackSvc] Current queue index set to {_currentQueueIndex} ({DescribeTrack(_queue[index])})");
        QueueChanged?.Invoke(_queue, _currentQueueIndex);
        return Play(_queue[index]);
    }

    public bool PlayNext()
    {
        ThrowIfDisposed();
        Console.WriteLine($"[PlaybackSvc] PlayNext (queueCount={_queue.Count}, currentIdx={_currentQueueIndex}, shuffle={_shuffleEnabled}, repeat={_repeatMode})");

        if (_queue.Count == 0)
        {
            return false;
        }

        if (_repeatMode == RepeatMode.Track && _currentQueueIndex >= 0 && _currentQueueIndex < _queue.Count)
        {
            Console.WriteLine("[PlaybackSvc] PlayNext using Repeat.Track");
            return PlayAtIndex(_currentQueueIndex);
        }

        if (_shuffleEnabled && _queue.Count > 1)
        {
            var shuffleIndex = ShuffleChooseNextTrack(_currentQueueIndex, _queue.Count);
            Console.WriteLine($"[PlaybackSvc] PlayNext shuffle chose index={shuffleIndex}");
            if (shuffleIndex >= 0 && shuffleIndex < _queue.Count)
            {
                return PlayAtIndex(shuffleIndex);
            }
        }

        var nextIndex = _currentQueueIndex + 1;
        if (nextIndex >= _queue.Count)
        {
            if (_repeatMode == RepeatMode.Playlist)
            {
                nextIndex = 0;
                Console.WriteLine("[PlaybackSvc] PlayNext wrapped to start due to Repeat.Playlist");
            }
            else
            {
                Console.WriteLine("[PlaybackSvc] PlayNext reached end of queue with no wrap");
                return false;
            }
        }

        Console.WriteLine($"[PlaybackSvc] PlayNext moving to index={nextIndex}");
        return PlayAtIndex(nextIndex);
    }

    public bool PlayPrevious()
    {
        ThrowIfDisposed();
        Console.WriteLine($"[PlaybackSvc] PlayPrevious (queueCount={_queue.Count}, currentIdx={_currentQueueIndex}, repeat={_repeatMode})");

        if (_queue.Count == 0)
        {
            return false;
        }

        var previousIndex = _currentQueueIndex - 1;
        if (previousIndex < 0)
        {
            if (_repeatMode == RepeatMode.Playlist)
            {
                previousIndex = _queue.Count - 1;
                Console.WriteLine("[PlaybackSvc] PlayPrevious wrapped to end due to Repeat.Playlist");
            }
            else
            {
                Console.WriteLine("[PlaybackSvc] PlayPrevious reached start of queue with no wrap");
                return false;
            }
        }

        Console.WriteLine($"[PlaybackSvc] PlayPrevious moving to index={previousIndex}");
        return PlayAtIndex(previousIndex);
    }

    public void Pause()
    {
        ThrowIfDisposed();
        Console.WriteLine($"[PlaybackSvc] Pause requested (idx={_currentQueueIndex}, track={DescribeTrack(_currentTrack)})");
        _mediaPlayer.Pause();
        EmitState();
    }

    public void Resume()
    {
        ThrowIfDisposed();
        Console.WriteLine($"[PlaybackSvc] Resume requested (idx={_currentQueueIndex}, track={DescribeTrack(_currentTrack)})");
        _mediaPlayer.SetPause(false);
        EmitState();
    }

    public void Stop()
    {
        ThrowIfDisposed();
        Console.WriteLine($"[PlaybackSvc] Stop requested (idx={_currentQueueIndex}, track={DescribeTrack(_currentTrack)})");
        _mediaPlayer.Stop();
        EmitState();
    }

    public void Seek(long positionMs)
    {
        ThrowIfDisposed();

        var duration = _mediaPlayer.Length;
        Console.WriteLine($"[PlaybackSvc] Seek requested to {positionMs} ms (duration={duration})");
        if (duration <= 0)
        {
            Console.WriteLine("[PlaybackSvc] Seek ignored (duration unavailable)");
            return;
        }

        _mediaPlayer.Time = Math.Clamp(positionMs, 0, duration);
        EmitState();
    }

    public void SetVolume(int volume)
    {
        ThrowIfDisposed();

        Console.WriteLine($"[PlaybackSvc] SetVolume {volume}");
        _mediaPlayer.Volume = Math.Clamp(volume, 0, 100);
        EmitState();
    }

    public void SetShuffle(bool enabled)
    {
        ThrowIfDisposed();

        _shuffleEnabled = enabled;
        Console.WriteLine($"[PlaybackSvc] SetShuffle {_shuffleEnabled}");
        EmitState();
    }

    public void SetRepeatMode(RepeatMode repeatMode)
    {
        ThrowIfDisposed();

        _repeatMode = repeatMode;
        Console.WriteLine($"[PlaybackSvc] SetRepeatMode {_repeatMode}");
        EmitState();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mediaPlayer.Dispose();
        DisposeCurrentMedia();
        _libVlc.Dispose();
    }

    /// <summary>Fires <see cref="PlaybackStateChanged"/> with the current state snapshot.</summary>
    private void EmitState()
    {
        PlaybackStateChanged?.Invoke(State);
    }

    /// <summary>
    /// Picks a random queue index that is not equal to <paramref name="currentIndex"/>.
    /// Currently uniform random — extension point for weighted/history-aware shuffle.
    /// </summary>
    private int ShuffleChooseNextTrack(int currentIndex, int queueCount)
    {
        if (queueCount <= 0)
        {
            return -1;
        }

        if (queueCount == 1)
        {
            return 0;
        }

        var candidates = Enumerable.Range(0, queueCount)
            .Where(index => index != currentIndex)
            .ToArray();

        if (candidates.Length == 0)
        {
            return currentIndex;
        }

        return candidates[_random.Next(candidates.Length)];
    }

    private void DisposeCurrentMedia()
    {
        if (_currentMedia != null)
        {
            Console.WriteLine("[PlaybackSvc] Disposing current media");
        }
        _currentMedia?.Dispose();
        _currentMedia = null;
    }

    private static string DescribeTrack(PlaybackTrack? track)
    {
        if (track == null)
        {
            return "<null>";
        }

        return $"id={track.TrackId}, title='{track.Title}', artist='{track.Artist}', path={(string.IsNullOrWhiteSpace(track.FilePath) ? "<none>" : track.FilePath)}";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MediaPlaybackService));
        }
    }
}
