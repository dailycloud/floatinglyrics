namespace FloatingLyrics.Models;

public sealed record TrackInfo(
    string Title,
    string Artist,
    string? ArtworkPath = null,
    string? SourceAppId = null,
    TimeSpan? Duration = null)
{
    public static TrackInfo CreateDemo() => new("Nightcall", "Kavinsky", Duration: TimeSpan.FromMinutes(3));
}
