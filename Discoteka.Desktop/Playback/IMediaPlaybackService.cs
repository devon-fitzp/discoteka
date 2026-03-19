using System;
using System.Collections.Generic;

namespace Discoteka.Desktop.Playback;

/// <summary>
/// Abstraction over the audio playback engine (implemented by <see cref="MediaPlaybackService"/>).
/// Manages a track queue, playback controls, shuffle/repeat modes, volume, and seek.
/// <para>
/// All events may fire on a background thread — subscribers must marshal to the UI thread
/// (e.g. <c>Dispatcher.UIThread.Post</c>) before touching Avalonia controls.
/// </para>
/// </summary>
public interface IMediaPlaybackService : IDisposable
{
    /// <summary>Fires whenever playback position, play/pause state, volume, or duration changes.</summary>
    event Action<PlaybackState>? PlaybackStateChanged;

    /// <summary>Fires when the active track changes, including when playback stops (passes null).</summary>
    event Action<PlaybackTrack?>? CurrentTrackChanged;

    /// <summary>Fires when the queue is replaced via <see cref="SetQueue"/>.</summary>
    event Action<IReadOnlyList<PlaybackTrack>, int>? QueueChanged;

    /// <summary>Fires when the media player reaches the end of a track (before auto-advance logic runs).</summary>
    event Action? PlaybackEnded;

    /// <summary>Fires when the media player encounters an error. The string is a human-readable description.</summary>
    event Action<string>? PlaybackError;

    /// <summary>The track currently loaded in the player, or null if none.</summary>
    PlaybackTrack? CurrentTrack { get; }

    /// <summary>A snapshot of current playback state (position, duration, volume, shuffle, repeat).</summary>
    PlaybackState State { get; }

    /// <summary>The full ordered queue of tracks.</summary>
    IReadOnlyList<PlaybackTrack> Queue { get; }

    /// <summary>Zero-based index of the currently playing track in <see cref="Queue"/>, or -1 if none.</summary>
    int CurrentQueueIndex { get; }

    /// <summary>
    /// Replaces the playback queue with <paramref name="tracks"/> and positions the cursor at
    /// <paramref name="startIndex"/> without starting playback.
    /// </summary>
    void SetQueue(IReadOnlyList<PlaybackTrack> tracks, int startIndex = 0);

    /// <summary>
    /// Loads and starts playing <paramref name="track"/> immediately, bypassing queue logic.
    /// Returns false if the track has no local file or the file does not exist.
    /// </summary>
    bool Play(PlaybackTrack track);

    /// <summary>Plays the track at <paramref name="index"/> in the current queue.</summary>
    /// <returns>False if <paramref name="index"/> is out of range or the track cannot be played.</returns>
    bool PlayAtIndex(int index);

    /// <summary>
    /// Advances to the next track, honouring shuffle and repeat mode.
    /// Returns false if the end of the queue is reached with no wrap.
    /// </summary>
    bool PlayNext();

    /// <summary>
    /// Goes back to the previous track, honouring repeat mode.
    /// Returns false if already at the start with no wrap.
    /// </summary>
    bool PlayPrevious();

    /// <summary>Pauses playback. Has no effect if already paused or stopped.</summary>
    void Pause();

    /// <summary>Resumes a paused track. Has no effect if playing or stopped.</summary>
    void Resume();

    /// <summary>Stops playback and resets position to zero.</summary>
    void Stop();

    /// <summary>Seeks to <paramref name="positionMs"/> milliseconds. Clamped to [0, duration].</summary>
    void Seek(long positionMs);

    /// <summary>Sets the volume. <paramref name="volume"/> is clamped to [0, 100].</summary>
    void SetVolume(int volume);

    /// <summary>Enables or disables shuffle mode. Affects <see cref="PlayNext"/> behaviour.</summary>
    void SetShuffle(bool enabled);

    /// <summary>Sets the repeat mode (Off, Track, or Playlist).</summary>
    void SetRepeatMode(RepeatMode repeatMode);
}
