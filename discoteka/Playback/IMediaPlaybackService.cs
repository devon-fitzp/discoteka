using System;
using System.Collections.Generic;

namespace discoteka.Playback;

public interface IMediaPlaybackService : IDisposable
{
    event Action<PlaybackState>? PlaybackStateChanged;
    event Action<PlaybackTrack?>? CurrentTrackChanged;
    event Action<IReadOnlyList<PlaybackTrack>, int>? QueueChanged;
    event Action? PlaybackEnded;
    event Action<string>? PlaybackError;

    PlaybackTrack? CurrentTrack { get; }
    PlaybackState State { get; }
    IReadOnlyList<PlaybackTrack> Queue { get; }
    int CurrentQueueIndex { get; }

    void SetQueue(IReadOnlyList<PlaybackTrack> tracks, int startIndex = 0);
    bool Play(PlaybackTrack track);
    bool PlayAtIndex(int index);
    bool PlayNext();
    bool PlayPrevious();
    void Pause();
    void Resume();
    void Stop();
    void Seek(long positionMs);
    void SetVolume(int volume);
    void SetShuffle(bool enabled);
    void SetRepeatMode(RepeatMode repeatMode);
}
