namespace FloatingLyrics.Models;

/// <summary>
/// A lyric line, timed exactly as the source (TTML / Spicy Lyrics) defines it: it is sung from
/// <see cref="Timestamp"/> until <see cref="EndTime"/>, independently of neighbouring lines (lines
/// may overlap). <see cref="Syllables"/> carries word/syllable timing; background vocals are a
/// separate smaller line (<see cref="BackgroundText"/>) with their own syllables;
/// <see cref="IsOppositeAligned"/> marks the second voice of a duet.
/// </summary>
public sealed record LyricLine(
    string Text,
    TimeSpan? Timestamp,
    IReadOnlyList<LyricSyllable>? Syllables = null,
    TimeSpan? EndTime = null,
    string? BackgroundText = null,
    IReadOnlyList<LyricSyllable>? BackgroundSyllables = null,
    bool IsOppositeAligned = false)
{
    public bool HasWordTiming => Syllables is { Count: > 0 };

    public bool HasBackgroundVocals => !string.IsNullOrWhiteSpace(BackgroundText);

    /// <summary>True while the line (lead or background) is being sung at <paramref name="position"/>.</summary>
    public bool IsSoundingAt(TimeSpan position) =>
        Timestamp is { } start && EndTime is { } end && position >= start && position < end;
}

/// <summary>
/// One timed piece of a line. <see cref="CharStart"/>/<see cref="CharLength"/> locate the syllable
/// inside its text (lead or background); the spaces between syllables belong to no syllable.
/// </summary>
public sealed record LyricSyllable(
    int CharStart,
    int CharLength,
    TimeSpan Start,
    TimeSpan End);
