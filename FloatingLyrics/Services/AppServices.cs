namespace FloatingLyrics.Services;

public sealed class AppServices : IDisposable
{
    private readonly HttpClient httpClient = new();
    private readonly IOverlaySettingsStore settingsStore = new OverlaySettingsStore();
    private readonly IStartupRegistrationService startupRegistrationService = new StartupRegistrationService();
    private readonly IUpdateService updateService;

    public MainViewModel MainViewModel { get; }

    public AppServices(Window window, bool settingsOnlyMode = false)
    {
        updateService = new GitHubUpdateService(httpClient);
        INowPlayingService nowPlayingService;
        ILyricsProvider lyricsProvider;
        IWindowModeService windowModeService;

        if (settingsOnlyMode)
        {
            nowPlayingService = new NoOpNowPlayingService();
            lyricsProvider = new NoOpLyricsProvider();
            windowModeService = new NoOpWindowModeService();
        }
        else
        {
            nowPlayingService = new SmtcNowPlayingService();
            lyricsProvider = new CompositeLyricsProvider(
                // Word-by-word lyrics first; the key lives in the user's local settings (or env).
                new SpicyLyricsProvider(httpClient, ResolveSpicyLyricsKey),
                new LrcLibLyricsProvider(httpClient),
                new MusixmatchLyricsProvider(httpClient),
                new LocalLrcLyricsProvider(),
                new FallbackLyricsProvider());
            windowModeService = new WindowModeService(window);
        }

        MainViewModel = new MainViewModel(
            nowPlayingService,
            lyricsProvider,
            windowModeService,
            settingsStore,
            startupRegistrationService,
            updateService,
            settingsOnlyMode);
    }

    /// <summary>
    /// Public client key of this app (a "client access" key: meant to ship inside the app,
    /// rate-limited per user and revocable). A personal key from the environment or
    /// settings.json takes precedence.
    /// </summary>
    private const string SpicyLyricsClientKey = "sl_pk_PmCceFpBMN-RkuhwJ69s6mqNNecpr6kVKtExWlXxpe0";

    private string? ResolveSpicyLyricsKey()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("FLOATING_LYRICS_SPICY_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var fromSettings = settingsStore.Load().SpicyLyricsApiKey;
        return !string.IsNullOrWhiteSpace(fromSettings) ? fromSettings : SpicyLyricsClientKey;
    }

    public void Dispose()
    {
        MainViewModel.Dispose();
        httpClient.Dispose();
    }
}
