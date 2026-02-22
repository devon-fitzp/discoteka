using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibVLCSharp.Shared;

namespace discoteka.Playback;

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

        _mediaPlayer.Playing += (_, _) => EmitState();
        _mediaPlayer.Paused += (_, _) => EmitState();
        _mediaPlayer.Stopped += (_, _) => EmitState();
        _mediaPlayer.TimeChanged += (_, _) => EmitState();
        _mediaPlayer.LengthChanged += (_, _) => EmitState();
        _mediaPlayer.EndReached += (_, _) => PlaybackEnded?.Invoke();
        _mediaPlayer.EncounteredError += (_, _) => PlaybackError?.Invoke("Playback error.");
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

        QueueChanged?.Invoke(_queue, _currentQueueIndex);
    }

    public bool Play(PlaybackTrack track)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(track.FilePath))
        {
            PlaybackError?.Invoke("No local file!");
            return false;
        }

        if (!File.Exists(track.FilePath))
        {
            PlaybackError?.Invoke("No local file!");
            return false;
        }

        DisposeCurrentMedia();

        _currentMedia = new Media(_libVlc, new Uri(track.FilePath));
        var started = _mediaPlayer.Play(_currentMedia);
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

        if (index < 0 || index >= _queue.Count)
        {
            return false;
        }

        _currentQueueIndex = index;
        QueueChanged?.Invoke(_queue, _currentQueueIndex);
        return Play(_queue[index]);
    }

    public bool PlayNext()
    {
        ThrowIfDisposed();

        if (_queue.Count == 0)
        {
            return false;
        }

        if (_repeatMode == RepeatMode.Track && _currentQueueIndex >= 0 && _currentQueueIndex < _queue.Count)
        {
            return PlayAtIndex(_currentQueueIndex);
        }

        if (_shuffleEnabled && _queue.Count > 1)
        {
            var shuffleIndex = ShuffleChooseNextTrack(_currentQueueIndex, _queue.Count);
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
            }
            else
            {
                return false;
            }
        }

        return PlayAtIndex(nextIndex);
    }

    public bool PlayPrevious()
    {
        ThrowIfDisposed();

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
            }
            else
            {
                return false;
            }
        }

        return PlayAtIndex(previousIndex);
    }

    public void Pause()
    {
        ThrowIfDisposed();
        _mediaPlayer.Pause();
        EmitState();
    }

    public void Resume()
    {
        ThrowIfDisposed();
        _mediaPlayer.SetPause(false);
        EmitState();
    }

    public void Stop()
    {
        ThrowIfDisposed();
        _mediaPlayer.Stop();
        EmitState();
    }

    public void Seek(long positionMs)
    {
        ThrowIfDisposed();

        var duration = _mediaPlayer.Length;
        if (duration <= 0)
        {
            return;
        }

        _mediaPlayer.Time = Math.Clamp(positionMs, 0, duration);
        EmitState();
    }

    public void SetVolume(int volume)
    {
        ThrowIfDisposed();

        _mediaPlayer.Volume = Math.Clamp(volume, 0, 100);
        EmitState();
    }

    public void SetShuffle(bool enabled)
    {
        ThrowIfDisposed();

        _shuffleEnabled = enabled;
        EmitState();
    }

    public void SetRepeatMode(RepeatMode repeatMode)
    {
        ThrowIfDisposed();

        _repeatMode = repeatMode;
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

    private void EmitState()
    {
        PlaybackStateChanged?.Invoke(State);
    }

    // Extension point for future weighted shuffle logic.
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
        _currentMedia?.Dispose();
        _currentMedia = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MediaPlaybackService));
        }
    }
}
