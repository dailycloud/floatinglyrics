namespace FloatingLyrics.Models;

public sealed record LyricsResult(
    IReadOnlyList<LyricLine> Lines,
    string Source,
    bool IsSynced,
    TimeSpan? SourceDuration = null,
    string? Attribution = null,
    string? AttributionUrl = null);
