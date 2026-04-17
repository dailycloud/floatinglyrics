namespace FloatingLyrics.Models;

public sealed record PlaybackSnapshot(TrackInfo Track, TimeSpan Position, bool IsPlaying);
