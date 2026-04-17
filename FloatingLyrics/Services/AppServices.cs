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

    public void Dispose()
    {
        MainViewModel.Dispose();
        httpClient.Dispose();
    }
}
