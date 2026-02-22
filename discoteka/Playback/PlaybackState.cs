namespace discoteka.Playback;

public sealed record PlaybackState(
    bool IsPlaying,
    bool IsPaused,
    long PositionMs,
    long DurationMs,
    int Volume,
    bool ShuffleEnabled,
    RepeatMode RepeatMode
);
