namespace FloatingLyrics.Services;

public sealed class CompositeLyricsProvider(params ILyricsProvider[] providers) : ILyricsProvider
{
    private readonly IReadOnlyList<ILyricsProvider> providers = providers;

    public async Task<LyricsResult?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        foreach (var provider in providers)
        {
            try
            {
                var result = await provider.GetLyricsAsync(track, cancellationToken);
                if (result is not null && result.Lines.Count > 0)
                {
                    return result;
                }
            }
            catch
            {
                // Intentionally continue to the next provider in the chain.
            }
        }

        return null;
    }
}
