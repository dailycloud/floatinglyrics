namespace FloatingLyrics.Services;

public interface ILyricsProvider
{
    Task<LyricsResult?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken);
}
