using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;
using DrawingGraphics = System.Drawing.Graphics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace FloatingLyrics.ViewModels;

public partial class MainViewModel : BaseViewModel, IDisposable
{
    private const int FloatingOverlayHeight = 460;
    private const int OverlayMinWidth = 640;
    private const int OverlayMaxWidth = 7680;
    private static readonly TimeSpan WaitingDotsThreshold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CountdownThreshold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SessionLossGracePeriod = TimeSpan.FromSeconds(2.2);
    private const int MissingSnapshotStreakThreshold = 8;
    private const string WaitingDotsText = "\u2022 \u2022 \u2022";
    private static readonly bool UseRussianUi = string.Equals(
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
        "ru",
        StringComparison.OrdinalIgnoreCase);
    private static readonly IReadOnlyList<PositionPresetOption> PositionPresetOptionsList =
    [
        new(WindowPositionPreset.Manual, L("Manual", "Вручную")),
        new(WindowPositionPreset.TopLeft, L("Top Left", "Сверху слева")),
        new(WindowPositionPreset.TopCenter, L("Top Center", "Сверху по центру")),
        new(WindowPositionPreset.TopRight, L("Top Right", "Сверху справа")),
        new(WindowPositionPreset.MiddleLeft, L("Middle Left", "По центру слева")),
        new(WindowPositionPreset.MiddleCenter, L("Middle Center", "По центру")),
        new(WindowPositionPreset.MiddleRight, L("Middle Right", "По центру справа")),
        new(WindowPositionPreset.BottomLeft, L("Bottom Left", "Снизу слева")),
        new(WindowPositionPreset.BottomCenter, L("Bottom Center", "Снизу по центру")),
        new(WindowPositionPreset.BottomRight, L("Bottom Right", "Снизу справа"))
    ];

    private static readonly IReadOnlyList<ClickThroughModeOption> ClickThroughModeOptionsList =
    [
        new(ClickThroughMode.OutsideDragHandle, L("Only drag bar + settings", "Только drag-полоса и меню")),
        new(ClickThroughMode.Always, L("Always click-through", "Всегда click-through")),
        new(ClickThroughMode.Disabled, L("Disable click-through", "Отключить click-through"))
    ];

    private static readonly IReadOnlyList<LyricsColorPresetOption> LyricsColorPresetOptionsList =
    [
        new(LyricsColorPreset.ClassicWhite, L("Classic White", "Классический белый")),
        new(LyricsColorPreset.WarmAmber, L("Warm Amber", "Тёплый янтарный")),
        new(LyricsColorPreset.IceBlue, L("Ice Blue", "Ледяной синий")),
        new(LyricsColorPreset.Mint, L("Mint", "Мятный"))
    ];

    private static readonly IReadOnlyList<LyricsTextAlignmentOption> LyricsTextAlignmentOptionsList =
    [
        new(LyricsTextAlignmentMode.Left, L("Left", "Слева")),
        new(LyricsTextAlignmentMode.Center, L("Center", "По центру")),
        new(LyricsTextAlignmentMode.Right, L("Right", "Справа"))
    ];

    private readonly INowPlayingService nowPlayingService;
    private readonly ILyricsProvider lyricsProvider;
    private readonly IWindowModeService windowModeService;
    private readonly IOverlaySettingsStore settingsStore;
    private readonly IStartupRegistrationService startupRegistrationService;
    private readonly IUpdateService updateService;
    private readonly PlaybackVisibilityController playbackVisibilityController = new();
    private readonly DispatcherQueueTimer lineTimer;
    private readonly DispatcherQueueTimer playbackTimer;
    private readonly CancellationTokenSource disposeTokenSource = new();
    private readonly List<LyricLine> timeline = [];
    private readonly bool settingsOnlyMode;

    private int lineIndex;
    private bool isSyncedTimeline;
    private bool isPlaybackTickInFlight;
    private bool suppressModePropertyHandlers;
    private bool suppressAlwaysOnTopPropertyHandler;
    private bool suppressSettingsPropertyHandlers;
    private bool suppressCustomPresetSelectionHandler;
    private bool deferOverlayResize = true;
    private bool hideCoverForCurrentLine;
    private bool hasCoverArt;
    private string currentArtworkPath = string.Empty;
    private int unsyncedTrackPollCounter;
    private int missingSnapshotStreak;
    private string activeTrackKey = string.Empty;
    private DateTimeOffset lastSnapshotSeenAtUtc = DateTimeOffset.MinValue;
    private string lastSessionTraceState = string.Empty;
    private int? pendingLineIndexTarget;
    private DateTimeOffset lastLineAdvanceAtUtc = DateTimeOffset.MinValue;
    private bool hasArtworkTheme;
    private Color artworkDominantColor = Color.FromArgb(255, 121, 183, 244);
    private Color artworkLowAccentColor = Color.FromArgb(255, 255, 84, 84);
    private Color artworkMidAccentColor = Color.FromArgb(255, 121, 183, 244);
    private Color artworkHighAccentColor = Color.FromArgb(255, 74, 255, 142);
    private long lastAppliedSettingsWriteTicks;
    private string updateReleaseUrl = string.Empty;
    private readonly Version appVersion;

    public MainViewModel(
        INowPlayingService nowPlayingService,
        ILyricsProvider lyricsProvider,
        IWindowModeService windowModeService,
        IOverlaySettingsStore settingsStore,
        IStartupRegistrationService startupRegistrationService,
        IUpdateService updateService,
        bool settingsOnlyMode = false)
    {
        DebugTrace.Write("MainViewModel: ctor start");
        this.nowPlayingService = nowPlayingService;
        this.lyricsProvider = lyricsProvider;
        this.windowModeService = windowModeService;
        this.settingsStore = settingsStore;
        this.startupRegistrationService = startupRegistrationService;
        this.updateService = updateService;
        this.settingsOnlyMode = settingsOnlyMode;
        appVersion = typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);

        Title = "Floating Lyrics";
        TrackTitle = string.Empty;
        ArtistName = string.Empty;
        CurrentLine = string.Empty;
        NextLine = string.Empty;
        SourceText = string.Empty;
        StatusText = L("Ready", "Готово");
        SettingsFilePath = settingsStore.SettingsFilePath;
        SettingsDirectoryPath = settingsStore.SettingsDirectoryPath;
        CoverArtImage = CreateCoverArtImage(null);
        AppVersionText = $"v{appVersion}";
        UpdateStatusText = L("Updates are not checked yet.", "Обновления ещё не проверялись.");
        UpdateLastCheckedText = L("Last check: never", "Последняя проверка: никогда");

        CustomPositionPresets.CollectionChanged += OnCustomPositionPresetsChanged;

        var dispatcherQueue = DispatcherQueue.GetForCurrentThread() ??
            throw new InvalidOperationException("MainViewModel should be created on the UI thread.");

        lineTimer = dispatcherQueue.CreateTimer();
        lineTimer.Interval = TimeSpan.FromSeconds(2.4);
        lineTimer.IsRepeating = true;
        lineTimer.Tick += OnLineTimerTick;

        playbackTimer = dispatcherQueue.CreateTimer();
        playbackTimer.Interval = TimeSpan.FromMilliseconds(100);
        playbackTimer.IsRepeating = true;
        playbackTimer.Tick += OnPlaybackTimerTick;
        if (!settingsOnlyMode)
        {
            playbackTimer.Start();
        }

        DebugTrace.Write("MainViewModel: applying persisted settings");
        ApplyPersistedSettings(settingsStore.Load(), applyStartupFallback: !settingsOnlyMode);
        lastAppliedSettingsWriteTicks = ReadSettingsWriteTicks();
        if (!settingsOnlyMode)
        {
            DebugTrace.Write("MainViewModel: initial refresh");
            _ = RefreshAsync();
            _ = RunStartupUpdateCheckAsync();
            StartPeriodicUpdateChecks(dispatcherQueue);
        }
        else
        {
            HasActiveSession = false;
            IsPlaybackVisible = true;
            StatusText = L("Settings mode", "Режим настроек");
        }
        DebugTrace.Write("MainViewModel: ctor done");
    }

    [ObservableProperty]
    private string trackTitle = string.Empty;

    [ObservableProperty]
    private string artistName = string.Empty;

    [ObservableProperty]
    private string currentLine = string.Empty;

    [ObservableProperty]
    private string previousLine = string.Empty;

    [ObservableProperty]
    private string nextLine = string.Empty;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string sourceText = string.Empty;

    /// <summary>Who provided (and synced) the lyrics shown now, for the on-widget credit.</summary>
    [ObservableProperty]
    private string lyricsAttribution = string.Empty;

    public string? LyricsAttributionUrl { get; private set; }

    private static string ShortSourceName(string source)
    {
        var dot = source.IndexOf(" · ", StringComparison.Ordinal);
        return dot > 0 ? source[..dot] : source;
    }

    [ObservableProperty]
    private BitmapImage coverArtImage = CreateCoverArtImage(null);

    [ObservableProperty]
    private int lineAnimationVersion;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool showLineTimings;

    [ObservableProperty]
    private bool showDebugHitboxes;

    [ObservableProperty]
    private bool showRuntimeDebugPanel;

    [ObservableProperty]
    private bool hideOnPause;

    [ObservableProperty]
    private double pauseHideDelaySeconds = 5.0;

    [ObservableProperty]
    private bool isPlaybackVisible;

    [ObservableProperty]
    private bool alwaysOnTop = true;

    [ObservableProperty]
    private bool desktopWidgetMode;

    [ObservableProperty]
    private string currentLineTiming = "--:--";

    [ObservableProperty]
    private string nextLineTiming = "--:--";

    [ObservableProperty]
    private bool isPreRollActive;

    [ObservableProperty]
    private bool isCountdownActive;

    [ObservableProperty]
    private bool isWaitingDotsActive;

    [ObservableProperty]
    private string preRollCountdownText = string.Empty;

    [ObservableProperty]
    private bool isLyricsSynced = true;

    [ObservableProperty]
    private bool hasLyricsContent;

    [ObservableProperty]
    private bool showLyricVisualizer;

    [ObservableProperty]
    private bool hasActiveSession;

    [ObservableProperty]
    private string audioSourceAppId = string.Empty;

    [ObservableProperty]
    private bool showDragHandle = true;

    [ObservableProperty]
    private bool settingsUseDarkTheme;

    [ObservableProperty]
    private WindowPositionPreset selectedPositionPreset = WindowPositionPreset.Manual;

    [ObservableProperty]
    private ClickThroughMode clickThroughMode = ClickThroughMode.OutsideDragHandle;

    [ObservableProperty]
    private LyricsColorPreset lyricsColorPreset = LyricsColorPreset.ClassicWhite;

    [ObservableProperty]
    private LyricsTextAlignmentMode lyricsTextAlignment = LyricsTextAlignmentMode.Left;

    [ObservableProperty]
    private bool autoLyricsTextAlignment;

    [ObservableProperty]
    private double overlayWidth = 1320;

    private double overlayContentOffsetX;
    private double overlayContentOffsetY;

    public double OverlayContentOffsetX => overlayContentOffsetX;
    public double OverlayContentOffsetY => overlayContentOffsetY;

    [ObservableProperty]
    private double lineAnimationDurationSeconds = 0.25;

    [ObservableProperty]
    private double oscilloscopeResponsiveness = 0.58;

    [ObservableProperty]
    private double oscilloscopeSensitivity = 1.3;

    [ObservableProperty]
    private double oscilloscopePeakDecay = 0.86;

    [ObservableProperty]
    private double oscilloscopeColorIntensity = 0.82;

    [ObservableProperty]
    private double oscilloscopeDetail = 240;

    [ObservableProperty]
    private double oscilloscopeLowFrequencyFocus = 0.55;

    [ObservableProperty]
    private bool oscilloscopeMirrorBars = true;

    [ObservableProperty]
    private bool appScopedAudioCapture = true;

    [ObservableProperty]
    private double viewportFadeZone = 58;

    [ObservableProperty]
    private double viewportFadeSoftness = 1.0;

    [ObservableProperty]
    private bool startInDesktopWidgetMode;

    [ObservableProperty]
    private bool runAtStartup;

    [ObservableProperty]
    private bool enableAutomaticUpdateChecks = true;

    [ObservableProperty]
    private double updateCheckIntervalHours = 12;

    [ObservableProperty]
    private string gitHubOwner = string.Empty;

    [ObservableProperty]
    private string gitHubRepository = string.Empty;

    [ObservableProperty]
    private string appVersionText = string.Empty;

    [ObservableProperty]
    private string updateStatusText = string.Empty;

    [ObservableProperty]
    private string updateLastCheckedText = string.Empty;

    [ObservableProperty]
    private bool isUpdateCheckInProgress;

    [ObservableProperty]
    private DateTimeOffset? lastUpdateCheckUtc;

    [ObservableProperty]
    private double titleFontSize = 32;

    [ObservableProperty]
    private double artistFontSize = 19;

    [ObservableProperty]
    private double currentLineFontSize = 47;

    [ObservableProperty]
    private double nextLineFontSize = 30;

    [ObservableProperty]
    private double nextLineTopMargin = 12;

    [ObservableProperty]
    private double textShadowOpacity = 0.58;

    [ObservableProperty]
    private bool showCoverArt = true;

    [ObservableProperty]
    private bool autoHideCoverOnLongLine = true;

    [ObservableProperty]
    private bool centerAlignmentShowCoverOnTop;

    [ObservableProperty]
    private double centerAlignmentCoverSize = 92;

    [ObservableProperty]
    private string customPresetName = string.Empty;

    [ObservableProperty]
    private CustomPositionPreset? selectedCustomPositionPreset;

    public ObservableCollection<CustomPositionPreset> CustomPositionPresets { get; } = [];
    public string SettingsFilePath { get; }
    public string SettingsDirectoryPath { get; }
    public string StartupTraceFilePath => DebugTrace.LogFilePath;

    public Visibility TimingsVisibility => ShowLineTimings && !ShowLyricVisualizer
        ? Visibility.Visible
        : Visibility.Collapsed;
    public string PauseHideDelayText => UseRussianUi
        ? $"Текущее значение: {PauseHideDelaySeconds:F1}с"
        : $"Current value: {PauseHideDelaySeconds:F1}s";
    public string PlaybackVisibilityDebugState =>
        playbackVisibilityController.GetDebugState(DateTimeOffset.UtcNow);

    public Visibility DebugOverlayVisibility => ShowDebugHitboxes
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RuntimeDebugPanelVisibility => ShowRuntimeDebugPanel
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility UnsyncedIndicatorVisibility => !IsLyricsSynced && HasLyricsContent && !ShowLyricVisualizer
        ? Visibility.Visible
        : Visibility.Collapsed;
    public string UnsyncedIndicatorToolTipText => L("Unsynced lyrics", "Текст не синхронизирован");

    public Visibility DragHandleVisibility => ShowDragHandle
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CoverVisibility => IsCoverVisible ? Visibility.Visible : Visibility.Collapsed;
    public double CoverOpacity => IsCoverVisible ? 1 : 0;
    public double CoverSize => IsCoverVisible
        ? (UseCenterTopCoverLayout ? Math.Clamp(CenterAlignmentCoverSize, 56, 240) : 92)
        : 0;
    public double CoverColumnSpacing => IsCoverVisible && !UseCenterTopCoverLayout ? 14 : 0;
    public bool UseCenterTopCoverLayout => IsManualCenterAlignment && CenterAlignmentShowCoverOnTop;
    public Visibility ArtistNameVisibility => IsManualCenterAlignment ? Visibility.Collapsed : Visibility.Visible;
    public string TrackTitleDisplay => BuildTrackTitleDisplayV2();

    public IReadOnlyList<PositionPresetOption> PositionPresetOptions => PositionPresetOptionsList;

    public IReadOnlyList<ClickThroughModeOption> ClickThroughModeOptions => ClickThroughModeOptionsList;

    public IReadOnlyList<LyricsColorPresetOption> LyricsColorPresetOptions => LyricsColorPresetOptionsList;

    public IReadOnlyList<LyricsTextAlignmentOption> LyricsTextAlignmentOptions => LyricsTextAlignmentOptionsList;

    public Visibility CustomPresetsVisibility => CustomPositionPresets.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility LyricsVisibility => !ShowLyricVisualizer
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility VisualizerVisibility => ShowLyricVisualizer
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PreRollVisibility => Visibility.Collapsed;

    public Visibility WaitingDotsVisibility => Visibility.Collapsed;

    public Visibility CountdownVisibility => Visibility.Collapsed;

    public bool AlwaysOnTopToggleEnabled => !DesktopWidgetMode;
    public bool IsGitHubUpdateConfigured =>
        !string.IsNullOrWhiteSpace(GitHubOwner) &&
        !string.IsNullOrWhiteSpace(GitHubRepository);
    public bool HasUpdateReleaseUrl => !string.IsNullOrWhiteSpace(GetUpdateReleaseUrl());
    public string UpdateRepositoryText => IsGitHubUpdateConfigured
        ? $"{GitHubOwner.Trim()}/{GitHubRepository.Trim()}"
        : L("Repository is not configured", "Репозиторий не настроен");
    public string UpdateCheckIntervalText => UseRussianUi
        ? $"Интервал: {UpdateCheckIntervalHours:F0} ч"
        : $"Interval: {UpdateCheckIntervalHours:F0}h";

    public IReadOnlyList<LyricLine> TimelineLines => timeline;

    public int ActiveLineIndex => lineIndex;

    public bool HasSyncedTimeline => isSyncedTimeline;

    public SolidColorBrush TrackTitleBrush => new(GetPalette().PrimaryText);
    public SolidColorBrush ArtistBrush => new(GetPalette().SecondaryText);
    public SolidColorBrush CurrentLineBrush => new(UseDarkText ? Color.FromArgb(255, 14, 16, 22) : Color.FromArgb(255, 255, 255, 255));
    public SolidColorBrush PreviousLineBrush => new(GetPalette().SecondaryText);
    public SolidColorBrush NextLineBrush => new(GetPalette().MutedText);
    public SolidColorBrush TimingBrush => new(GetPalette().TimingText);
    public SolidColorBrush TextShadowBrush => new(GetShadowColor());
    public Color OscilloscopeLowAccentColor => artworkLowAccentColor;
    public Color OscilloscopeMidAccentColor => artworkMidAccentColor;
    public Color OscilloscopeHighAccentColor => artworkHighAccentColor;
    public Color ArtworkAccentColor => artworkDominantColor;

    // ---- Liquid glass & typography customization ----

    [ObservableProperty]
    private GlassVisibilityMode glassVisibility = GlassVisibilityMode.OnHover;

    [ObservableProperty]
    private double glassBlur = 14;

    [ObservableProperty]
    private double glassRefraction = 0.65;

    [ObservableProperty]
    private double glassDispersion = 0.6;

    [ObservableProperty]
    private double glassSpecular = 0.8;

    [ObservableProperty]
    private GlassTintPreset glassTint = GlassTintPreset.Smoke;

    [ObservableProperty]
    private double glassTintStrength = 0.32;

    [ObservableProperty]
    private double glassCornerRadius = 30;

    [ObservableProperty]
    private double glassPadding = 22;

    [ObservableProperty]
    private bool glassCursorLight = true;

    [ObservableProperty]
    private bool useArtworkTextColors = true;

    [ObservableProperty]
    private LyricsFontOption lyricsFont = LyricsFontOption.SegoeVariable;

    [ObservableProperty]
    private LyricsFontWeightOption lyricsFontWeight = LyricsFontWeightOption.Semibold;

    [ObservableProperty]
    private LineAnimationStyle lineAnimationStyle = LineAnimationStyle.Spring;

    partial void OnGlassVisibilityChanged(GlassVisibilityMode value) => PersistSettingsIfReady();
    partial void OnGlassBlurChanged(double value) => PersistSettingsIfReady();
    partial void OnGlassRefractionChanged(double value) => PersistSettingsIfReady();
    partial void OnGlassDispersionChanged(double value) => PersistSettingsIfReady();
    partial void OnGlassSpecularChanged(double value) => PersistSettingsIfReady();
    partial void OnGlassTintChanged(GlassTintPreset value) => PersistSettingsIfReady();
    partial void OnGlassTintStrengthChanged(double value) => PersistSettingsIfReady();
    partial void OnGlassCornerRadiusChanged(double value) => PersistSettingsIfReady();
    partial void OnGlassPaddingChanged(double value) => PersistSettingsIfReady();
    partial void OnGlassCursorLightChanged(bool value) => PersistSettingsIfReady();
    partial void OnLyricsFontChanged(LyricsFontOption value) => PersistSettingsIfReady();
    partial void OnLyricsFontWeightChanged(LyricsFontWeightOption value) => PersistSettingsIfReady();
    partial void OnLineAnimationStyleChanged(LineAnimationStyle value) => PersistSettingsIfReady();

    // Kept only to round-trip the user's key through settings.json (never shown or logged).
    private string spicyLyricsApiKey = string.Empty;

    public bool HasSpicyLyricsKey => !string.IsNullOrWhiteSpace(spicyLyricsApiKey) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLOATING_LYRICS_SPICY_KEY"));

    /// <summary>Installed or user-supplied font family name; overrides <see cref="LyricsFont"/> when set.</summary>
    [ObservableProperty]
    private string customFontFamily = string.Empty;

    [ObservableProperty]
    private TextContrastMode textContrast = TextContrastMode.Auto;

    [ObservableProperty]
    private bool wordByWordHighlight = true;

    /// <summary>True when the backdrop behind the text is light (set by the view from the wallpaper).</summary>
    [ObservableProperty]
    private bool isBackdropLight;

    partial void OnCustomFontFamilyChanged(string value) => PersistSettingsIfReady();
    partial void OnWordByWordHighlightChanged(bool value) => PersistSettingsIfReady();

    partial void OnTextContrastChanged(TextContrastMode value)
    {
        NotifyPaletteChanged();
        PersistSettingsIfReady();
    }

    partial void OnIsBackdropLightChanged(bool value) => NotifyPaletteChanged();

    /// <summary>Dark text is used for Dark mode, and for Auto/Invert when the backdrop is light.</summary>
    public bool UseDarkText => TextContrast switch
    {
        TextContrastMode.Dark => true,
        TextContrastMode.Light => false,
        _ => IsBackdropLight
    };

    private void NotifyPaletteChanged()
    {
        OnPropertyChanged(nameof(UseDarkText));
        OnPropertyChanged(nameof(TrackTitleBrush));
        OnPropertyChanged(nameof(ArtistBrush));
        OnPropertyChanged(nameof(CurrentLineBrush));
        OnPropertyChanged(nameof(PreviousLineBrush));
        OnPropertyChanged(nameof(NextLineBrush));
        OnPropertyChanged(nameof(TimingBrush));
        OnPropertyChanged(nameof(TextShadowBrush));
    }

    // ---- playback clock for word-by-word highlighting ----

    private TimeSpan playbackAnchorPosition;
    private long playbackAnchorTimestamp;
    private bool playbackAnchorIsPlaying;

    /// <summary>
    /// Current playback position (with the user's sync offset), interpolated between SMTC polls.
    /// </summary>
    public TimeSpan GetInterpolatedPlaybackPosition() => GetRawPlaybackPosition() + LyricsOffset;

    private TimeSpan LyricsOffset => TimeSpan.FromMilliseconds(Math.Clamp(LyricsOffsetMs, -2000, 2000));

    private TimeSpan GetRawPlaybackPosition()
    {
        if (!playbackAnchorIsPlaying || playbackAnchorTimestamp == 0)
        {
            return playbackAnchorPosition;
        }

        var elapsed = Stopwatch.GetElapsedTime(playbackAnchorTimestamp);
        return playbackAnchorPosition + (elapsed > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : elapsed);
    }

    public bool IsPlaybackRunning => playbackAnchorIsPlaying;

    /// <summary>
    /// Phase-locked playback clock. SMTC positions are noisy (extrapolated from a coarse
    /// LastUpdatedTime); instead of jumping to each sample (or, worse, always taking the larger
    /// one, which runs ahead), the clock keeps ticking smoothly and is nudged toward every sample
    /// by a small fraction. Only real seeks (large errors) cause a hard re-sync.
    /// </summary>
    private void UpdatePlaybackAnchor(TimeSpan position, bool isPlaying)
    {
        var now = Stopwatch.GetTimestamp();
        if (!isPlaying || !playbackAnchorIsPlaying)
        {
            playbackAnchorPosition = position;
            playbackAnchorTimestamp = now;
            playbackAnchorIsPlaying = isPlaying;
            return;
        }

        var predicted = GetRawPlaybackPosition();
        var error = position - predicted;
        if (error.Duration() > TimeSpan.FromMilliseconds(700))
        {
            playbackAnchorPosition = position;
        }
        else
        {
            playbackAnchorPosition = predicted + TimeSpan.FromTicks((long)(error.Ticks * 0.12));
        }

        playbackAnchorTimestamp = now;
    }

    /// <summary>Lyrics sync offset in ms (positive = lyrics later, negative = earlier).</summary>
    [ObservableProperty]
    private double lyricsOffsetMs;

    partial void OnLyricsOffsetMsChanged(double value) => PersistSettingsIfReady();

    partial void OnUseArtworkTextColorsChanged(bool value)
    {
        OnPropertyChanged(nameof(TrackTitleBrush));
        OnPropertyChanged(nameof(ArtistBrush));
        OnPropertyChanged(nameof(CurrentLineBrush));
        OnPropertyChanged(nameof(PreviousLineBrush));
        OnPropertyChanged(nameof(NextLineBrush));
        OnPropertyChanged(nameof(TimingBrush));
        PersistSettingsIfReady();
    }

    partial void OnShowLineTimingsChanged(bool value)
    {
        OnPropertyChanged(nameof(TimingsVisibility));
        PersistSettingsIfReady();
    }

    partial void OnShowDebugHitboxesChanged(bool value)
    {
        OnPropertyChanged(nameof(DebugOverlayVisibility));
        PersistSettingsIfReady();
    }

    partial void OnShowRuntimeDebugPanelChanged(bool value)
    {
        OnPropertyChanged(nameof(RuntimeDebugPanelVisibility));
        PersistSettingsIfReady();
    }

    partial void OnPauseHideDelaySecondsChanged(double value)
    {
        var clamped = Math.Clamp(value, 1.0, 20.0);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            suppressSettingsPropertyHandlers = true;
            PauseHideDelaySeconds = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        OnPropertyChanged(nameof(PauseHideDelayText));
        PersistSettingsIfReady();
    }

    partial void OnHideOnPauseChanged(bool value)
    {
        PersistSettingsIfReady();
    }

    partial void OnIsPreRollActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(TimingsVisibility));
        OnPropertyChanged(nameof(LyricsVisibility));
        OnPropertyChanged(nameof(VisualizerVisibility));
        OnPropertyChanged(nameof(PreRollVisibility));
        OnPropertyChanged(nameof(WaitingDotsVisibility));
        OnPropertyChanged(nameof(CountdownVisibility));
    }

    partial void OnIsCountdownActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(WaitingDotsVisibility));
        OnPropertyChanged(nameof(CountdownVisibility));
    }

    partial void OnIsWaitingDotsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(WaitingDotsVisibility));
    }

    partial void OnIsLyricsSyncedChanged(bool value)
    {
        OnPropertyChanged(nameof(UnsyncedIndicatorVisibility));
    }

    partial void OnHasLyricsContentChanged(bool value)
    {
        OnPropertyChanged(nameof(UnsyncedIndicatorVisibility));
    }

    partial void OnShowLyricVisualizerChanged(bool value)
    {
        OnPropertyChanged(nameof(TimingsVisibility));
        OnPropertyChanged(nameof(LyricsVisibility));
        OnPropertyChanged(nameof(VisualizerVisibility));
        OnPropertyChanged(nameof(UnsyncedIndicatorVisibility));
    }

    partial void OnShowDragHandleChanged(bool value)
    {
        OnPropertyChanged(nameof(DragHandleVisibility));
        PersistSettingsIfReady();
    }

    partial void OnSettingsUseDarkThemeChanged(bool value)
    {
        PersistSettingsIfReady();
    }

    partial void OnCurrentLineChanged(string value)
    {
        UpdateCoverVisibility();
    }

    partial void OnSelectedPositionPresetChanged(WindowPositionPreset value)
    {
        if (!suppressSettingsPropertyHandlers)
        {
            if (value != WindowPositionPreset.Manual && !windowModeService.TryApplyPositionPreset(value))
            {
                StatusText = L("Unable to apply position preset.", "Не удалось применить пресет позиции.");
            }

            if (value != WindowPositionPreset.Manual && SelectedCustomPositionPreset is not null)
            {
                suppressCustomPresetSelectionHandler = true;
                SelectedCustomPositionPreset = null;
                suppressCustomPresetSelectionHandler = false;
            }
        }

        PersistSettingsIfReady();
    }

    partial void OnSelectedCustomPositionPresetChanged(CustomPositionPreset? value)
    {
        RemoveSelectedCustomPresetCommand.NotifyCanExecuteChanged();
        if (suppressCustomPresetSelectionHandler)
        {
            return;
        }

        if (value is not null)
        {
            if (!windowModeService.TryApplyRelativePosition(value.RelativeX, value.RelativeY))
            {
                StatusText = L("Unable to apply custom preset.", "Не удалось применить пользовательский пресет.");
            }
            else
            {
                suppressSettingsPropertyHandlers = true;
                SelectedPositionPreset = WindowPositionPreset.Manual;
                suppressSettingsPropertyHandlers = false;
            }
        }

        PersistSettingsIfReady();
    }

    partial void OnClickThroughModeChanged(ClickThroughMode value)
    {
        PersistSettingsIfReady();
    }

    partial void OnLyricsColorPresetChanged(LyricsColorPreset value)
    {
        OnPropertyChanged(nameof(TrackTitleBrush));
        OnPropertyChanged(nameof(ArtistBrush));
        OnPropertyChanged(nameof(CurrentLineBrush));
        OnPropertyChanged(nameof(PreviousLineBrush));
        OnPropertyChanged(nameof(NextLineBrush));
        OnPropertyChanged(nameof(TimingBrush));
        PersistSettingsIfReady();
    }

    partial void OnLyricsTextAlignmentChanged(LyricsTextAlignmentMode value)
    {
        if (!suppressSettingsPropertyHandlers && AutoLyricsTextAlignment)
        {
            suppressSettingsPropertyHandlers = true;
            AutoLyricsTextAlignment = false;
            suppressSettingsPropertyHandlers = false;
        }

        NotifyTrackMetaPresentationChanged();
        NotifyCoverPresentationChanged();
        PersistSettingsIfReady();
    }

    partial void OnAutoLyricsTextAlignmentChanged(bool value)
    {
        NotifyTrackMetaPresentationChanged();
        NotifyCoverPresentationChanged();
        PersistSettingsIfReady();
    }

    partial void OnOverlayWidthChanged(double value)
    {
        var clamped = Math.Clamp(value, OverlayMinWidth, OverlayMaxWidth);
        if (Math.Abs(clamped - value) > 0.001)
        {
            suppressSettingsPropertyHandlers = true;
            OverlayWidth = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        if (!suppressSettingsPropertyHandlers && !deferOverlayResize && !TryApplyOverlayResize())
        {
            StatusText = L("Unable to resize overlay window.", "Не удалось изменить размер окна оверлея.");
        }

        PersistSettingsIfReady();
    }

    partial void OnLineAnimationDurationSecondsChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.12, 1.2);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            suppressSettingsPropertyHandlers = true;
            LineAnimationDurationSeconds = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        PersistSettingsIfReady();
    }

    partial void OnOscilloscopeResponsivenessChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.08, 0.95);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            suppressSettingsPropertyHandlers = true;
            OscilloscopeResponsiveness = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        PersistSettingsIfReady();
    }

    partial void OnOscilloscopeSensitivityChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.5, 2.6);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            suppressSettingsPropertyHandlers = true;
            OscilloscopeSensitivity = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        PersistSettingsIfReady();
    }

    partial void OnOscilloscopePeakDecayChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.65, 0.99);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            suppressSettingsPropertyHandlers = true;
            OscilloscopePeakDecay = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        PersistSettingsIfReady();
    }

    partial void OnOscilloscopeColorIntensityChanged(double value)
    {
        var clamped = Math.Clamp(value, 0, 1);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            suppressSettingsPropertyHandlers = true;
            OscilloscopeColorIntensity = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        PersistSettingsIfReady();
    }

    partial void OnOscilloscopeDetailChanged(double value)
    {
        var clamped = Math.Clamp(value, 96, 320);
        if (Math.Abs(clamped - value) > 0.001)
        {
            suppressSettingsPropertyHandlers = true;
            OscilloscopeDetail = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        PersistSettingsIfReady();
    }

    partial void OnOscilloscopeLowFrequencyFocusChanged(double value)
    {
        var clamped = Math.Clamp(value, 0, 1);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            suppressSettingsPropertyHandlers = true;
            OscilloscopeLowFrequencyFocus = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        PersistSettingsIfReady();
    }

    partial void OnOscilloscopeMirrorBarsChanged(bool value)
    {
        PersistSettingsIfReady();
    }

    partial void OnAppScopedAudioCaptureChanged(bool value)
    {
        PersistSettingsIfReady();
    }

    partial void OnViewportFadeZoneChanged(double value)
    {
        var clamped = Math.Clamp(value, 0, 180);
        if (Math.Abs(clamped - value) > 0.001)
        {
            suppressSettingsPropertyHandlers = true;
            ViewportFadeZone = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        PersistSettingsIfReady();
    }

    partial void OnViewportFadeSoftnessChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.4, 2.2);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            suppressSettingsPropertyHandlers = true;
            ViewportFadeSoftness = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        PersistSettingsIfReady();
    }

    partial void OnStartInDesktopWidgetModeChanged(bool value)
    {
        if (!suppressSettingsPropertyHandlers && RunAtStartup)
        {
            _ = startupRegistrationService.TrySetEnabled(
                enabled: true,
                startInDesktopWidgetMode: ShouldUseDesktopWidgetStartupArgument());
        }

        PersistSettingsIfReady();
    }

    partial void OnRunAtStartupChanged(bool value)
    {
        if (suppressSettingsPropertyHandlers)
        {
            return;
        }

        if (!startupRegistrationService.TrySetEnabled(
                enabled: value,
                startInDesktopWidgetMode: ShouldUseDesktopWidgetStartupArgument()))
        {
            suppressSettingsPropertyHandlers = true;
            RunAtStartup = !value;
            suppressSettingsPropertyHandlers = false;
            StatusText = L("Unable to update startup registration.", "Не удалось обновить автозапуск.");
            return;
        }

        PersistSettingsIfReady();
    }

    partial void OnEnableAutomaticUpdateChecksChanged(bool value)
    {
        PersistSettingsIfReady();
    }

    partial void OnUpdateCheckIntervalHoursChanged(double value)
    {
        var clamped = Math.Clamp(value, 1, 168);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            suppressSettingsPropertyHandlers = true;
            UpdateCheckIntervalHours = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        OnPropertyChanged(nameof(UpdateCheckIntervalText));
        PersistSettingsIfReady();
    }

    partial void OnGitHubOwnerChanged(string value)
    {
        NormalizeUpdateRepositoryPart(ref gitHubOwner, value, nameof(GitHubOwner));
    }

    partial void OnGitHubRepositoryChanged(string value)
    {
        NormalizeUpdateRepositoryPart(ref gitHubRepository, value, nameof(GitHubRepository));
    }

    partial void OnIsUpdateCheckInProgressChanged(bool value)
    {
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
    }

    partial void OnLastUpdateCheckUtcChanged(DateTimeOffset? value)
    {
        UpdateLastCheckedText = value is null
            ? L("Last check: never", "Последняя проверка: никогда")
            : UseRussianUi
                ? $"Последняя проверка: {value.Value.ToLocalTime():g}"
                : $"Last check: {value.Value.ToLocalTime():g}";
        PersistSettingsIfReady();
    }

    partial void OnTitleFontSizeChanged(double value)
    {
        NormalizeFontSize(ref titleFontSize, value, nameof(TitleFontSize), 16, 54);
    }

    partial void OnArtistFontSizeChanged(double value)
    {
        NormalizeFontSize(ref artistFontSize, value, nameof(ArtistFontSize), 12, 32);
    }

    partial void OnCurrentLineFontSizeChanged(double value)
    {
        NormalizeFontSize(ref currentLineFontSize, value, nameof(CurrentLineFontSize), 20, 84);
    }

    partial void OnNextLineFontSizeChanged(double value)
    {
        NormalizeFontSize(ref nextLineFontSize, value, nameof(NextLineFontSize), 16, 54);
    }

    partial void OnNextLineTopMarginChanged(double value)
    {
        var clamped = Math.Clamp(value, 0, 48);
        if (Math.Abs(clamped - value) > 0.001)
        {
            suppressSettingsPropertyHandlers = true;
            NextLineTopMargin = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        PersistSettingsIfReady();
    }

    partial void OnTextShadowOpacityChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.05, 1);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            suppressSettingsPropertyHandlers = true;
            TextShadowOpacity = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        OnPropertyChanged(nameof(TextShadowBrush));
        PersistSettingsIfReady();
    }

    partial void OnShowCoverArtChanged(bool value)
    {
        UpdateCoverVisibility();
        PersistSettingsIfReady();
    }

    partial void OnAutoHideCoverOnLongLineChanged(bool value)
    {
        UpdateCoverVisibility();
        PersistSettingsIfReady();
    }

    partial void OnCenterAlignmentShowCoverOnTopChanged(bool value)
    {
        NotifyCoverPresentationChanged();
        PersistSettingsIfReady();
    }

    partial void OnCenterAlignmentCoverSizeChanged(double value)
    {
        var clamped = Math.Clamp(value, 56, 240);
        if (Math.Abs(clamped - value) > 0.001)
        {
            suppressSettingsPropertyHandlers = true;
            CenterAlignmentCoverSize = clamped;
            suppressSettingsPropertyHandlers = false;
            return;
        }

        NotifyCoverPresentationChanged();
        PersistSettingsIfReady();
    }

    partial void OnTrackTitleChanged(string value)
    {
        NotifyTrackMetaPresentationChanged();
    }

    partial void OnArtistNameChanged(string value)
    {
        NotifyTrackMetaPresentationChanged();
    }

    partial void OnAlwaysOnTopChanged(bool value)
    {
        if (suppressAlwaysOnTopPropertyHandler || suppressSettingsPropertyHandlers)
        {
            return;
        }

        if (DesktopWidgetMode && value)
        {
            suppressAlwaysOnTopPropertyHandler = true;
            AlwaysOnTop = false;
            suppressAlwaysOnTopPropertyHandler = false;
            StatusText = L("Always-on-top is unavailable in desktop widget mode.", "Режим «поверх всех окон» недоступен в режиме desktop-widget.");
            return;
        }

        if (!windowModeService.TrySetAlwaysOnTop(value))
        {
            StatusText = L("Unable to switch always-on-top mode.", "Не удалось переключить режим «поверх всех окон».");
            return;
        }

        StatusText = value
            ? L("Always-on-top mode enabled.", "Режим «поверх всех окон» включен.")
            : L("Always-on-top mode disabled.", "Режим «поверх всех окон» выключен.");
        PersistSettingsIfReady();
    }

    partial void OnDesktopWidgetModeChanged(bool value)
    {
        OnPropertyChanged(nameof(AlwaysOnTopToggleEnabled));
        if (suppressModePropertyHandlers || suppressSettingsPropertyHandlers)
        {
            return;
        }

        var targetMode = value
            ? WindowPresentationMode.Wallpaper
            : WindowPresentationMode.Floating;
        if (!windowModeService.TrySetMode(targetMode))
        {
            suppressModePropertyHandlers = true;
            DesktopWidgetMode = !value;
            suppressModePropertyHandlers = false;
            StatusText = value
                ? L("Unable to enable desktop widget mode.", "Не удалось включить режим desktop-widget.")
                : L("Unable to return to floating mode.", "Не удалось вернуться в плавающий режим.");
            return;
        }

        if (value && AlwaysOnTop)
        {
            suppressAlwaysOnTopPropertyHandler = true;
            AlwaysOnTop = false;
            suppressAlwaysOnTopPropertyHandler = false;
            _ = windowModeService.TrySetAlwaysOnTop(false);
        }

        StatusText = value
            ? L("Desktop widget mode enabled.", "Режим desktop-widget включен.")
            : L("Floating mode enabled.", "Плавающий режим включен.");

        if (!value)
        {
            ReapplyCurrentPositionPreset();
            _ = windowModeService.TryEnsureVisibleOnWorkArea();
        }

        PersistSettingsIfReady();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = L("Detecting active player...", "Поиск активного плеера...");

        try
        {
            var snapshot = await nowPlayingService.GetPlaybackSnapshotAsync(disposeTokenSource.Token);
            if (snapshot is null)
            {
                TraceSessionDecision("refresh-null");
                UpdatePlaybackVisibility(null);
                if (!HasActiveSession)
                {
                    StatusText = L("No player session found.", "Активная сессия плеера не найдена.");
                }

                if (ShouldHideForMissingSnapshot() && HasActiveSession)
                {
                    ApplyNoSessionState();
                    StatusText = L("No player session found.", "Активная сессия плеера не найдена.");
                }

                return;
            }

            MarkSnapshotSeen();
            UpdatePlaybackAnchor(snapshot.Position, snapshot.IsPlaying);
            HasActiveSession = true;
            var previousTrackKey = activeTrackKey;
            activeTrackKey = BuildTrackKey(snapshot.Track);
            UpdatePlaybackVisibility(snapshot);
            TraceSessionDecision("refresh-live", snapshot);
            StatusText = L("Player found. Fetching lyrics...", "Плеер найден. Загружаю текст...");
            ApplySnapshotTrackInfo(snapshot.Track);
            if (!string.IsNullOrEmpty(previousTrackKey) &&
                !string.Equals(previousTrackKey, activeTrackKey, StringComparison.Ordinal))
            {
                // Drop the previous song's lines right away (visualizer shows while the new
                // lyrics are fetched) instead of leaving stale lyrics under the new title.
                LoadTimeline(Array.Empty<LyricLine>(), snapshot.Position, providerSaysSynced: false);
            }

            var result = await lyricsProvider.GetLyricsAsync(snapshot.Track, disposeTokenSource.Token);
            LyricsAttribution = string.Empty;
            LyricsAttributionUrl = null;
            if (result is null)
            {
                SourceText = L("Source: unavailable", "Источник: недоступен");
                HasLyricsContent = false;
                ActivateVisualizerMode();
                StatusText = L("No synced lyrics found. Visualizer mode enabled.", "Синхронизированный текст не найден. Включен визуализатор.");
                return;
            }

            SourceText = UseRussianUi
                ? $"Источник: {result.Source}"
                : $"Source: {result.Source}";
            LyricsAttribution = result.Attribution ?? ShortSourceName(result.Source);
            LyricsAttributionUrl = result.AttributionUrl;
            if (!HasMeaningfulLyrics(result.Lines))
            {
                HasLyricsContent = false;
                ActivateVisualizerMode();
                StatusText = L("Instrumental or empty lyrics detected. Visualizer mode enabled.", "Инструментал или пустой текст. Включен визуализатор.");
                return;
            }

            HasLyricsContent = true;
            LoadTimeline(result.Lines, snapshot.Position, result.IsSynced);
            StatusText = ShowLyricVisualizer
                ? L("Unsynced or unavailable lyrics. Visualizer mode enabled.", "Текст без синхронизации или недоступен. Включен визуализатор.")
                : L("Lyrics synced to track timeline.", "Текст синхронизирован с таймлайном трека.");
        }
        catch (OperationCanceledException) when (disposeTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            HasActiveSession = true;
            HasLyricsContent = false;
            LoadTimeline([new LyricLine(L("Unable to load lyrics right now.", "Сейчас не удалось загрузить текст."), null)], TimeSpan.Zero, false);
            SourceText = L("Source: fallback", "Источник: fallback");
            StatusText = UseRussianUi
                ? $"Ошибка: {ex.Message}"
                : $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SetFloatingMode()
    {
        DesktopWidgetMode = false;
    }

    [RelayCommand]
    private void SetWallpaperMode()
    {
        DesktopWidgetMode = true;
    }

    [RelayCommand]
    private void SaveCurrentAsCustomPreset()
    {
        if (!windowModeService.TryGetCurrentRelativePosition(out var relativeX, out var relativeY))
        {
            StatusText = L("Unable to read current window position.", "Не удалось получить текущую позицию окна.");
            return;
        }

        var name = string.IsNullOrWhiteSpace(CustomPresetName)
            ? UseRussianUi
                ? $"Пользовательский {CustomPositionPresets.Count + 1}"
                : $"Custom {CustomPositionPresets.Count + 1}"
            : CustomPresetName.Trim();
        var preset = new CustomPositionPreset
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            RelativeX = relativeX,
            RelativeY = relativeY
        };

        CustomPositionPresets.Add(preset);
        suppressCustomPresetSelectionHandler = true;
        SelectedCustomPositionPreset = preset;
        suppressCustomPresetSelectionHandler = false;

        suppressSettingsPropertyHandlers = true;
        SelectedPositionPreset = WindowPositionPreset.Manual;
        suppressSettingsPropertyHandlers = false;

        CustomPresetName = string.Empty;
        StatusText = UseRussianUi
            ? $"Пользовательский пресет «{name}» сохранён."
            : $"Custom preset \"{name}\" saved.";
        OnPropertyChanged(nameof(CustomPresetsVisibility));
        PersistSettings();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedCustomPreset))]
    private void RemoveSelectedCustomPreset()
    {
        if (SelectedCustomPositionPreset is null)
        {
            return;
        }

        var removing = SelectedCustomPositionPreset;
        var removedName = removing.Name;
        var removedIndex = CustomPositionPresets.IndexOf(removing);
        CustomPositionPresets.Remove(removing);

        suppressCustomPresetSelectionHandler = true;
        SelectedCustomPositionPreset = removedIndex >= 0 && removedIndex < CustomPositionPresets.Count
            ? CustomPositionPresets[removedIndex]
            : null;
        suppressCustomPresetSelectionHandler = false;

        StatusText = UseRussianUi
            ? $"Пользовательский пресет «{removedName}» удалён."
            : $"Custom preset \"{removedName}\" removed.";
        OnPropertyChanged(nameof(CustomPresetsVisibility));
        PersistSettings();
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{SettingsDirectoryPath}\"",
                UseShellExecute = true
            });
            StatusText = L("Config folder opened.", "Папка с конфигами открыта.");
        }
        catch
        {
            StatusText = L("Unable to open config folder.", "Не удалось открыть папку с конфигами.");
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(DebugTrace.LogRootPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{DebugTrace.LogRootPath}\"",
                UseShellExecute = true
            });
            StatusText = L("Logs folder opened.", "Папка с логами открыта.");
        }
        catch
        {
            StatusText = L("Unable to open logs folder.", "Не удалось открыть папку с логами.");
        }
    }

    private const string DefaultUpdateOwner = "dailycloud";
    private const string DefaultUpdateRepository = "floatinglyrics";
    private DispatcherQueueTimer? updateTimer;

    /// <summary>The widget runs for days: re-check for updates every hour (respecting the interval).</summary>
    private void StartPeriodicUpdateChecks(DispatcherQueue dispatcherQueue)
    {
        updateTimer = dispatcherQueue.CreateTimer();
        updateTimer.Interval = TimeSpan.FromHours(1);
        updateTimer.IsRepeating = true;
        updateTimer.Tick += (_, _) => _ = RunStartupUpdateCheckAsync();
        updateTimer.Start();
    }

    private bool CanCheckForUpdates() => !IsUpdateCheckInProgress;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        if (IsUpdateCheckInProgress)
        {
            return;
        }

        var owner = GitHubOwner.Trim();
        var repository = GitHubRepository.Trim();
        if (owner.Length == 0 || repository.Length == 0)
        {
            UpdateStatusText = L(
                "Set GitHub owner and repository first.",
                "Сначала укажите владельца и репозиторий GitHub.");
            return;
        }

        IsUpdateCheckInProgress = true;
        UpdateStatusText = L("Checking GitHub releases...", "Проверяю релизы GitHub...");

        try
        {
            var result = await updateService.CheckForUpdatesAsync(
                owner,
                repository,
                appVersion,
                disposeTokenSource.Token);

            updateReleaseUrl = result.ReleaseUrl;
            OnPropertyChanged(nameof(HasUpdateReleaseUrl));
            OpenUpdateReleasePageCommand.NotifyCanExecuteChanged();
            LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            UpdateStatusText = LocalizeUpdateStatus(result);

            if (result.Status == UpdateCheckStatus.UpdateAvailable)
            {
                StatusText = UseRussianUi
                    ? $"Доступно обновление: v{result.LatestVersion}"
                    : $"Update available: v{result.LatestVersion}";

                if (!string.IsNullOrWhiteSpace(result.AssetUrl) && result.LatestVersion is { } latest)
                {
                    UpdateStatusText = L($"Downloading v{latest}...", $"Скачиваю v{latest}...");
                    try
                    {
                        if (await UpdateInstaller.DownloadAndScheduleAsync(result.AssetUrl, latest, disposeTokenSource.Token))
                        {
                            UpdateStatusText = L("Installing, the widget restarts...", "Устанавливаю, виджет перезапустится...");
                            PersistSettings();
                            Microsoft.UI.Xaml.Application.Current.Exit();
                            return;
                        }

                        UpdateStatusText = L("The release package is invalid.", "Пакет релиза повреждён.");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        DebugTrace.Write($"Update: install failed {ex}");
                        UpdateStatusText = UseRussianUi
                            ? $"Не удалось установить обновление: {ex.Message}"
                            : $"Unable to install the update: {ex.Message}";
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = L("Update check canceled.", "Проверка обновлений отменена.");
        }
        finally
        {
            IsUpdateCheckInProgress = false;
        }
    }

    private bool CanOpenUpdateReleasePage() => !string.IsNullOrWhiteSpace(GetUpdateReleaseUrl());

    [RelayCommand(CanExecute = nameof(CanOpenUpdateReleasePage))]
    private void OpenUpdateReleasePage()
    {
        var url = GetUpdateReleaseUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            UpdateStatusText = L(
                "There is no release page to open yet.",
                "Пока нечего открывать: страница релизов недоступна.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            StatusText = L("Release page opened.", "Страница релизов открыта.");
        }
        catch (Exception ex)
        {
            UpdateStatusText = UseRussianUi
                ? $"Не удалось открыть страницу релизов: {ex.Message}"
                : $"Unable to open the releases page: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenConfigFile()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectoryPath);
            if (!File.Exists(SettingsFilePath))
            {
                PersistSettings();
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{SettingsFilePath}\"",
                UseShellExecute = true
            });
            StatusText = L("Config file opened.", "Файл конфига открыт.");
        }
        catch (Exception ex)
        {
            StatusText = UseRussianUi
                ? $"Не удалось открыть файл конфига: {ex.Message}"
                : $"Unable to open config file: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ResetAllSettings()
    {
        ApplyPersistedSettings(new OverlayUserSettings(), applyStartupFallback: false);
        ReapplyCurrentPositionPreset();
        PersistSettings();
        StatusText = L("Settings reset to defaults.", "Настройки сброшены к значениям по умолчанию.");
    }

    [RelayCommand]
    private void ExportDebugSnapshot()
    {
        try
        {
            Directory.CreateDirectory(DebugTrace.LogRootPath);
            var filePath = Path.Combine(
                DebugTrace.LogRootPath,
                $"debug-snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            var snapshot = new
            {
                exportedAt = DateTimeOffset.Now,
                session = new
                {
                    hasActiveSession = HasActiveSession,
                    isPlaybackVisible = IsPlaybackVisible,
                    playbackDebug = PlaybackVisibilityDebugState,
                    activeTrackKey
                },
                track = new
                {
                    title = TrackTitle,
                    artist = ArtistName,
                    source = SourceText,
                    sourceAppId = AudioSourceAppId
                },
                lines = new
                {
                    previous = PreviousLine,
                    current = CurrentLine,
                    next = NextLine,
                    currentTiming = CurrentLineTiming,
                    nextTiming = NextLineTiming,
                    synced = IsLyricsSynced,
                    preRoll = IsPreRollActive,
                    countdown = IsCountdownActive,
                    waitingDots = IsWaitingDotsActive,
                    visualizer = ShowLyricVisualizer
                },
                settings = new
                {
                    hideOnPause = HideOnPause,
                    pauseHideDelaySeconds = PauseHideDelaySeconds,
                    lineAnimationDurationSeconds = LineAnimationDurationSeconds,
                    overlayWidth = OverlayWidth,
                    viewportFadeZone = ViewportFadeZone,
                    viewportFadeSoftness = ViewportFadeSoftness,
                    appScopedAudioCapture = AppScopedAudioCapture,
                    oscilloscopeLowFrequencyFocus = OscilloscopeLowFrequencyFocus,
                    clickThroughMode = ClickThroughMode.ToString(),
                    desktopWidgetMode = DesktopWidgetMode,
                    alwaysOnTop = AlwaysOnTop,
                    showDebugHitboxes = ShowDebugHitboxes
                },
                recentTrace = DebugTrace.GetSnapshot().TakeLast(120).ToArray()
            };

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(filePath, json, Encoding.UTF8);
            StatusText = UseRussianUi
                ? $"Снапшот отладки сохранён: {Path.GetFileName(filePath)}"
                : $"Debug snapshot saved: {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            StatusText = UseRussianUi
                ? $"Не удалось экспортировать снапшот отладки: {ex.Message}"
                : $"Unable to export debug snapshot: {ex.Message}";
        }
    }

    private async Task RunStartupUpdateCheckAsync()
    {
        if (settingsOnlyMode || !EnableAutomaticUpdateChecks || !IsGitHubUpdateConfigured)
        {
            return;
        }

        if (LastUpdateCheckUtc is DateTimeOffset lastCheckedUtc)
        {
            var minimumDelay = TimeSpan.FromHours(Math.Clamp(UpdateCheckIntervalHours, 1, 168));
            if ((DateTimeOffset.UtcNow - lastCheckedUtc) < minimumDelay)
            {
                return;
            }
        }

        await CheckForUpdatesAsync();
    }

    private string LocalizeUpdateStatus(UpdateCheckResult result)
    {
        return result.Status switch
        {
            UpdateCheckStatus.NotConfigured => L(
                "GitHub repository is not configured yet.",
                "GitHub-репозиторий пока не настроен."),
            UpdateCheckStatus.NoReleases => L(
                "The repository exists, but there are no published releases yet.",
                "Репозиторий есть, но опубликованных релизов пока нет."),
            UpdateCheckStatus.UpToDate => UseRussianUi
                ? $"У вас уже последняя версия: v{result.LatestVersion ?? result.CurrentVersion}."
                : $"You're already on the latest version: v{result.LatestVersion ?? result.CurrentVersion}.",
            UpdateCheckStatus.UpdateAvailable => UseRussianUi
                ? $"Доступно обновление: v{result.LatestVersion}."
                : $"Update available: v{result.LatestVersion}.",
            _ => UseRussianUi
                ? $"Проверка не удалась: {result.Message}"
                : $"Update check failed: {result.Message}"
        };
    }

    private string GetUpdateReleaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(updateReleaseUrl))
        {
            return updateReleaseUrl;
        }

        return GitHubUpdateService.BuildReleasesPageUrl(GitHubOwner, GitHubRepository);
    }

    private void NormalizeUpdateRepositoryPart(ref string backingField, string value, string propertyName)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (!string.Equals(backingField, normalized, StringComparison.Ordinal))
        {
            suppressSettingsPropertyHandlers = true;
            backingField = normalized;
            OnPropertyChanged(propertyName);
            suppressSettingsPropertyHandlers = false;
        }

        updateReleaseUrl = string.Empty;
        OnPropertyChanged(nameof(IsGitHubUpdateConfigured));
        OnPropertyChanged(nameof(UpdateRepositoryText));
        OnPropertyChanged(nameof(HasUpdateReleaseUrl));
        OpenUpdateReleasePageCommand.NotifyCanExecuteChanged();
        PersistSettingsIfReady();
    }

    private bool CanRemoveSelectedCustomPreset() => SelectedCustomPositionPreset is not null;

    private void LoadTimeline(IReadOnlyList<LyricLine> lines, TimeSpan playbackPosition, bool providerSaysSynced)
    {
        lineTimer.Stop();
        ClearPendingLineAdvance();
        timeline.Clear();
        OnPropertyChanged(nameof(TimelineLines));
        ClearPreRollState();
        ShowLyricVisualizer = false;
        PreviousLine = string.Empty;
        unsyncedTrackPollCounter = 0;

        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line.Text))
            {
                timeline.Add(line);
            }
        }

        if (timeline.Count == 0)
        {
            ActivateVisualizerMode();
            return;
        }

        if (!providerSaysSynced)
        {
            ActivateVisualizerMode();
            return;
        }

        var timestampedLines = timeline
            .Where(static x => x.Timestamp.HasValue)
            .OrderBy(static x => x.Timestamp)
            .ToList();

        isSyncedTimeline = timestampedLines.Count > 0 && (providerSaysSynced || timestampedLines.Count > 1);
        IsLyricsSynced = isSyncedTimeline;
        OnPropertyChanged(nameof(HasSyncedTimeline));
        if (!isSyncedTimeline)
        {
            ActivateVisualizerMode();
            return;
        }

        timeline.Clear();
        timeline.AddRange(timestampedLines);
        OnPropertyChanged(nameof(TimelineLines));
        lineIndex = 0;
        OnPropertyChanged(nameof(ActiveLineIndex));
        ApplySyncedPosition(playbackPosition, forceAnimation: true);
    }

    private void OnLineTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (isSyncedTimeline || timeline.Count <= 1)
        {
            return;
        }

        var nextIndex = (lineIndex + 1) % timeline.Count;
        SetLineIndex(nextIndex);
    }

    private void OnPlaybackTimerTick(DispatcherQueueTimer sender, object args)
    {
        _ = SynchronizeTimelineAsync();
    }

    private async Task SynchronizeTimelineAsync()
    {
        if (IsBusy || isPlaybackTickInFlight)
        {
            return;
        }

        if (!isSyncedTimeline)
        {
            unsyncedTrackPollCounter = (unsyncedTrackPollCounter + 1) % 5;
            if (unsyncedTrackPollCounter != 0)
            {
                return;
            }
        }

        isPlaybackTickInFlight = true;
        try
        {
            var snapshot = await nowPlayingService.GetPlaybackSnapshotAsync(disposeTokenSource.Token);
            if (snapshot is null)
            {
                TraceSessionDecision("sync-null");
                UpdatePlaybackVisibility(null);
                if (!HasActiveSession)
                {
                    StatusText = L("No player session found.", "Активная сессия плеера не найдена.");
                }

                if (ShouldHideForMissingSnapshot() && HasActiveSession)
                {
                    ApplyNoSessionState();
                    StatusText = L("No player session found.", "Активная сессия плеера не найдена.");
                }

                return;
            }

            MarkSnapshotSeen();
            UpdatePlaybackAnchor(snapshot.Position, snapshot.IsPlaying);
            if (!HasActiveSession)
            {
                HasActiveSession = true;
            }
            var snapshotTrackKey = BuildTrackKey(snapshot.Track);
            UpdatePlaybackVisibility(snapshot);

            TraceSessionDecision("sync-live", snapshot);

            if (!string.Equals(activeTrackKey, snapshotTrackKey, StringComparison.Ordinal))
            {
                await RefreshAsync();
                return;
            }

            ApplySnapshotTrackInfo(snapshot.Track);

            if (isSyncedTimeline)
            {
                ApplySyncedPosition(snapshot.Position, forceAnimation: false);
            }
        }
        catch (OperationCanceledException) when (disposeTokenSource.IsCancellationRequested)
        {
        }
        finally
        {
            isPlaybackTickInFlight = false;
        }
    }

    private void ApplySyncedPosition(TimeSpan playbackPosition, bool forceAnimation)
    {
        if (timeline.Count == 0)
        {
            ClearPendingLineAdvance();
            ClearPreRollState();
            PreviousLine = string.Empty;
            CurrentLine = string.Empty;
            NextLine = string.Empty;
            CurrentLineTiming = "--:--";
            NextLineTiming = "--:--";
            return;
        }

        // Source-timed lyrics (TTML / Spicy: every line has an end) are used exactly as authored.
        // Only plain LRC (start times only) gets a small lead so its transition lands on time.
        var hasExactTiming = timeline.Count > 0 && timeline[0].EndTime.HasValue;
        var animationLeadTime = hasExactTiming
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Min(LineAnimationDurationSeconds, 0.45));
        var adjustedPosition = playbackPosition + LyricsOffset + animationLeadTime;
        var firstTimestamp = timeline[0].Timestamp;
        if (firstTimestamp.HasValue && adjustedPosition < firstTimestamp.Value)
        {
            ClearPendingLineAdvance();
            var timeUntilFirstLine = firstTimestamp.Value - adjustedPosition;
            ApplyPreRollState(timeUntilFirstLine, firstTimestamp.Value);
            return;
        }

        var wasPreRollActive = IsPreRollActive;
        ClearPreRollState();
        var targetIndex = 0;
        for (var i = 0; i < timeline.Count; i++)
        {
            var timestamp = timeline[i].Timestamp ?? TimeSpan.MaxValue;
            if (timestamp <= adjustedPosition)
            {
                targetIndex = i;
                continue;
            }

            break;
        }

        if (hasExactTiming)
        {
            // A later line starting never cuts off one that is still being sung: the view stays
            // on the earliest line that has not reached its own end time (overlapping lines are
            // all highlighted by the view at once).
            for (var i = Math.Max(0, targetIndex - 6); i < targetIndex; i++)
            {
                if (timeline[i].EndTime is { } end && end > adjustedPosition)
                {
                    targetIndex = i;
                    break;
                }
            }
        }

        ClearPendingLineAdvance();
        SetLineIndex(targetIndex, forceAnimation || wasPreRollActive);
    }

    private void AdvancePendingLineIndex()
    {
        if (!pendingLineIndexTarget.HasValue || timeline.Count == 0)
        {
            return;
        }

        var target = Math.Clamp(pendingLineIndexTarget.Value, 0, timeline.Count - 1);
        if (target == lineIndex)
        {
            ClearPendingLineAdvance();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var minInterval = TimeSpan.FromSeconds(Math.Clamp(LineAnimationDurationSeconds * 0.9, 0.08, 0.35));
        if (lastLineAdvanceAtUtc != DateTimeOffset.MinValue && now - lastLineAdvanceAtUtc < minInterval)
        {
            return;
        }

        var direction = Math.Sign(target - lineIndex);
        var nextIndex = lineIndex + direction;
        SetLineIndex(nextIndex, forceAnimation: true);
        lastLineAdvanceAtUtc = now;
        if (lineIndex == target)
        {
            ClearPendingLineAdvance();
        }
    }

    private void ClearPendingLineAdvance()
    {
        pendingLineIndexTarget = null;
        lastLineAdvanceAtUtc = DateTimeOffset.MinValue;
    }

    private void ApplyPreRollState(TimeSpan timeUntilFirstLine, TimeSpan firstLineTimestamp)
    {
        IsPreRollActive = true;
        var nextTimingText = FormatTimestamp(firstLineTimestamp);
        var firstLyricText = timeline.Count > 0 ? timeline[0].Text : string.Empty;

        var showWaitingDots = timeUntilFirstLine > WaitingDotsThreshold;
        var showCountdown = !showWaitingDots && timeUntilFirstLine > TimeSpan.Zero && timeUntilFirstLine <= CountdownThreshold;
        IsCountdownActive = showCountdown;
        IsWaitingDotsActive = showWaitingDots;

        string virtualLineText;
        if (showCountdown)
        {
            var secondsLeft = (int)Math.Ceiling(timeUntilFirstLine.TotalSeconds);
            virtualLineText = Math.Clamp(secondsLeft, 1, (int)Math.Ceiling(CountdownThreshold.TotalSeconds)).ToString();
            PreRollCountdownText = virtualLineText;
        }
        else
        {
            virtualLineText = "• • •";
            PreRollCountdownText = string.Empty;
        }

        if (!showCountdown)
        {
            virtualLineText = WaitingDotsText;
            PreRollCountdownText = string.Empty;
        }

        var outgoingLine = CurrentLine;
        var shouldAnimate =
            !string.Equals(CurrentLine, virtualLineText, StringComparison.Ordinal) ||
            !string.Equals(NextLine, firstLyricText, StringComparison.Ordinal);

        PreviousLine = string.Equals(outgoingLine, virtualLineText, StringComparison.Ordinal)
            ? string.Empty
            : outgoingLine;
        CurrentLine = virtualLineText;
        NextLine = firstLyricText;
        CurrentLineTiming = "--:--";
        NextLineTiming = nextTimingText;

        if (shouldAnimate)
        {
            LineAnimationVersion++;
        }
    }

    private void ClearPreRollState()
    {
        IsPreRollActive = false;
        IsCountdownActive = false;
        IsWaitingDotsActive = false;
        if (!string.Equals(PreRollCountdownText, string.Empty, StringComparison.Ordinal))
        {
            PreRollCountdownText = string.Empty;
        }
    }

    private void ApplyLineState(bool forceAnimation = false)
    {
        SetLineIndex(lineIndex, forceAnimation);
    }

    private void SetLineIndex(int newIndex, bool forceAnimation = false)
    {
        if (timeline.Count == 0)
        {
            PreviousLine = string.Empty;
            CurrentLine = string.Empty;
            NextLine = string.Empty;
            CurrentLineTiming = "--:--";
            NextLineTiming = "--:--";
            return;
        }

        var clamped = Math.Clamp(newIndex, 0, timeline.Count - 1);
        if (!forceAnimation && clamped == lineIndex)
        {
            return;
        }

        var outgoingLine = CurrentLine;
        lineIndex = clamped;
        OnPropertyChanged(nameof(ActiveLineIndex));
        PreviousLine = string.Equals(outgoingLine, timeline[lineIndex].Text, StringComparison.Ordinal)
            ? string.Empty
            : outgoingLine;
        CurrentLine = timeline[lineIndex].Text;
        NextLine = lineIndex + 1 < timeline.Count
            ? timeline[lineIndex + 1].Text
            : string.Empty;
        CurrentLineTiming = FormatTimestamp(timeline[lineIndex].Timestamp);
        NextLineTiming = lineIndex + 1 < timeline.Count
            ? FormatTimestamp(timeline[lineIndex + 1].Timestamp)
            : "--:--";
        LineAnimationVersion++;
    }

    private void ActivateVisualizerMode()
    {
        lineTimer.Stop();
        timeline.Clear();
        isSyncedTimeline = false;
        HasLyricsContent = false;
        ClearPendingLineAdvance();
        lineIndex = 0;
        OnPropertyChanged(nameof(TimelineLines));
        OnPropertyChanged(nameof(HasSyncedTimeline));
        OnPropertyChanged(nameof(ActiveLineIndex));
        ClearPreRollState();
        ShowLyricVisualizer = true;
        IsLyricsSynced = false;
        PreviousLine = string.Empty;
        CurrentLine = string.Empty;
        NextLine = string.Empty;
        CurrentLineTiming = "--:--";
        NextLineTiming = "--:--";
    }

    private void ApplyNoSessionState()
    {
        TraceSessionDecision("apply-no-session");
        HasActiveSession = false;
        AudioSourceAppId = string.Empty;
        IsPlaybackVisible = false;
        activeTrackKey = string.Empty;
        lastSnapshotSeenAtUtc = DateTimeOffset.MinValue;
        missingSnapshotStreak = 0;
        playbackVisibilityController.Reset();
        lineTimer.Stop();
        timeline.Clear();
        isSyncedTimeline = false;
        ClearPendingLineAdvance();
        lineIndex = 0;
        unsyncedTrackPollCounter = 0;
        HasLyricsContent = false;
        OnPropertyChanged(nameof(TimelineLines));
        OnPropertyChanged(nameof(HasSyncedTimeline));
        OnPropertyChanged(nameof(ActiveLineIndex));
        ClearPreRollState();
        ShowLyricVisualizer = false;
        // Keep last rendered metadata/lines while overlay fades out.
        // This avoids visible rollback to empty state when player closes.
    }

    private void UpdatePlaybackVisibility(PlaybackSnapshot? snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var pauseGrace = TimeSpan.FromSeconds(Math.Clamp(PauseHideDelaySeconds, 1.0, 20.0));
        IsPlaybackVisible = playbackVisibilityController.IsVisible(
            snapshot,
            HasActiveSession,
            !string.IsNullOrWhiteSpace(TrackTitle) || !string.IsNullOrWhiteSpace(ArtistName),
            activeTrackKey,
            HideOnPause,
            pauseGrace,
            now);
        OnPropertyChanged(nameof(PlaybackVisibilityDebugState));
    }

    private void TraceSessionDecision(string reason, PlaybackSnapshot? snapshot = null)
    {
        var state = string.Join(
            "|",
            reason,
            HasActiveSession,
            missingSnapshotStreak,
            activeTrackKey,
            TrackTitle,
            ArtistName,
            CurrentLine,
            NextLine,
            ShowLyricVisualizer,
            IsPreRollActive,
            IsPlaybackVisible,
            snapshot?.Track.Title ?? "<null>",
            snapshot?.IsPlaying.ToString() ?? "<null>");

        if (string.Equals(lastSessionTraceState, state, StringComparison.Ordinal))
        {
            return;
        }

        lastSessionTraceState = state;
        DebugTrace.Write(
            $"VM: reason={reason}; active={HasActiveSession}; streak={missingSnapshotStreak}; trackKey={activeTrackKey}; " +
            $"title={TrackTitle}; artist={ArtistName}; current={CurrentLine}; next={NextLine}; viz={ShowLyricVisualizer}; " +
            $"preRoll={IsPreRollActive}; playbackVisible={IsPlaybackVisible}; snapshot={(snapshot is null ? "<null>" : $"{snapshot.Track.Title} | playing={snapshot.IsPlaying} | pos={snapshot.Position:mm\\:ss}")}");
    }

    private bool ShouldHideForMissingSnapshot()
    {
        missingSnapshotStreak++;

        if (!HasActiveSession)
        {
            return false;
        }

        if (lastSnapshotSeenAtUtc == DateTimeOffset.MinValue)
        {
            return missingSnapshotStreak >= MissingSnapshotStreakThreshold;
        }

        return missingSnapshotStreak >= MissingSnapshotStreakThreshold &&
            (DateTimeOffset.UtcNow - lastSnapshotSeenAtUtc) >= SessionLossGracePeriod;
    }

    private void MarkSnapshotSeen()
    {
        lastSnapshotSeenAtUtc = DateTimeOffset.UtcNow;
        missingSnapshotStreak = 0;
    }

    private static bool HasMeaningfulLyrics(IReadOnlyList<LyricLine> lines)
    {
        foreach (var line in lines)
        {
            if (IsMeaningfulLyricText(line.Text))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMeaningfulLyricText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return false;
        }

        if (normalized is "." or ".." or "..." or "…")
        {
            return false;
        }

        if (normalized.Contains("instrumental", StringComparison.Ordinal) ||
            normalized.Contains("инструментал", StringComparison.Ordinal) ||
            normalized.Contains("без слов", StringComparison.Ordinal) ||
            normalized.Contains("no online lyrics", StringComparison.Ordinal) ||
            normalized.Contains("no lyrics", StringComparison.Ordinal) ||
            normalized.Contains("lyrics unavailable", StringComparison.Ordinal) ||
            normalized.Contains("unable to load lyrics", StringComparison.Ordinal) ||
            normalized.Contains("try local .lrc", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Any(char.IsLetterOrDigit);
    }

    private static string FormatTimestamp(TimeSpan? timestamp)
    {
        if (!timestamp.HasValue)
        {
            return "--:--";
        }

        var value = timestamp.Value;
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");
    }

    private static BitmapImage CreateCoverArtImage(string? artworkPath)
    {
        var source = new BitmapImage
        {
            // Keep artwork decode lightweight and avoid growing image cache over long sessions.
            CreateOptions = BitmapCreateOptions.IgnoreImageCache,
            DecodePixelWidth = 192,
            DecodePixelHeight = 192
        };
        if (!string.IsNullOrWhiteSpace(artworkPath) && File.Exists(artworkPath))
        {
            source.UriSource = new Uri(artworkPath);
        }

        return source;
    }

    private void UpdateArtworkTheme(string? artworkPath)
    {
        if (TryExtractDominantArtworkColor(artworkPath, out var dominantColor))
        {
            hasArtworkTheme = true;
            artworkDominantColor = dominantColor;
        }
        else
        {
            hasArtworkTheme = false;
            artworkDominantColor = Color.FromArgb(255, 121, 183, 244);
        }

        BuildOscilloscopeAccentPalette(
            artworkDominantColor,
            out artworkLowAccentColor,
            out artworkMidAccentColor,
            out artworkHighAccentColor);

        OnPropertyChanged(nameof(OscilloscopeLowAccentColor));
        OnPropertyChanged(nameof(OscilloscopeMidAccentColor));
        OnPropertyChanged(nameof(OscilloscopeHighAccentColor));
        OnPropertyChanged(nameof(ArtworkAccentColor));
        OnPropertyChanged(nameof(TrackTitleBrush));
        OnPropertyChanged(nameof(ArtistBrush));
        OnPropertyChanged(nameof(CurrentLineBrush));
        OnPropertyChanged(nameof(PreviousLineBrush));
        OnPropertyChanged(nameof(NextLineBrush));
        OnPropertyChanged(nameof(TimingBrush));
    }

    private static bool TryExtractDominantArtworkColor(string? artworkPath, out Color dominantColor)
    {
        dominantColor = Color.FromArgb(255, 121, 183, 244);
        if (string.IsNullOrWhiteSpace(artworkPath) || !File.Exists(artworkPath))
        {
            return false;
        }

        try
        {
            using var source = new DrawingBitmap(artworkPath);
            var sampleSize = 56;
            using var sample = new DrawingBitmap(sampleSize, sampleSize);
            using (var graphics = DrawingGraphics.FromImage(sample))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(source, 0, 0, sampleSize, sampleSize);
            }

            const int hueBins = 72;
            var hueWeights = new double[hueBins];
            var hueSumR = new double[hueBins];
            var hueSumG = new double[hueBins];
            var hueSumB = new double[hueBins];

            for (var y = 0; y < sampleSize; y++)
            {
                for (var x = 0; x < sampleSize; x++)
                {
                    var pixel = sample.GetPixel(x, y);
                    if (pixel.A < 24)
                    {
                        continue;
                    }

                    var h = pixel.GetHue();
                    var s = pixel.GetSaturation();
                    var v = pixel.GetBrightness();
                    if (v < 0.07 || s < 0.2)
                    {
                        continue;
                    }

                    var weight = Math.Pow(s, 1.75) * (0.55 + (v * 0.45));
                    if (h is >= 18 and <= 50)
                    {
                        // Skin-tone / beige area: reduce dominance so accent is more expressive.
                        weight *= 0.6;
                    }

                    if (v > 0.96 && s < 0.42)
                    {
                        weight *= 0.45;
                    }

                    var hBin = ((int)Math.Floor((h / 360d) * hueBins) + hueBins) % hueBins;
                    hueWeights[hBin] += weight;
                    hueSumR[hBin] += pixel.R * weight;
                    hueSumG[hBin] += pixel.G * weight;
                    hueSumB[hBin] += pixel.B * weight;
                }
            }

            var bestHueIndex = -1;
            var bestWeight = 0d;
            for (var i = 0; i < hueBins; i++)
            {
                if (hueWeights[i] > bestWeight)
                {
                    bestWeight = hueWeights[i];
                    bestHueIndex = i;
                }
            }

            if (bestHueIndex < 0 || bestWeight <= 0.001)
            {
                return false;
            }

            var targetHue = ((bestHueIndex + 0.5) * 360d) / hueBins;
            var total = 0d;
            var sumR = 0d;
            var sumG = 0d;
            var sumB = 0d;
            for (var y = 0; y < sampleSize; y++)
            {
                for (var x = 0; x < sampleSize; x++)
                {
                    var pixel = sample.GetPixel(x, y);
                    if (pixel.A < 24)
                    {
                        continue;
                    }

                    var h = pixel.GetHue();
                    var s = pixel.GetSaturation();
                    var v = pixel.GetBrightness();
                    if (s < 0.22 || v < 0.08)
                    {
                        continue;
                    }

                    var hueDistance = GetHueDistance(h, targetHue);
                    if (hueDistance > 20)
                    {
                        continue;
                    }

                    var weight = (1 - (hueDistance / 20d)) * Math.Pow(s, 1.55) * (0.5 + (v * 0.5));
                    sumR += pixel.R * weight;
                    sumG += pixel.G * weight;
                    sumB += pixel.B * weight;
                    total += weight;
                }
            }

            if (total <= 0.001)
            {
                total = hueWeights[bestHueIndex];
                sumR = hueSumR[bestHueIndex];
                sumG = hueSumG[bestHueIndex];
                sumB = hueSumB[bestHueIndex];
            }

            var averageR = (byte)Math.Clamp(Math.Round(sumR / total), 0, 255);
            var averageG = (byte)Math.Clamp(Math.Round(sumG / total), 0, 255);
            var averageB = (byte)Math.Clamp(Math.Round(sumB / total), 0, 255);
            var averageColor = Color.FromArgb(255, averageR, averageG, averageB);
            ToHsv(averageColor, out var hue, out var saturation, out var value);
            saturation = Math.Clamp((saturation * 1.2) + 0.04, 0.36, 0.98);
            value = Math.Clamp(Math.Max(value, 0.6), 0.48, 0.97);
            dominantColor = ColorFromHsv(hue, saturation, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void BuildOscilloscopeAccentPalette(Color dominant, out Color lowAccent, out Color midAccent, out Color highAccent)
    {
        ToHsv(dominant, out var hue, out var sat, out var val);
        sat = Math.Clamp((sat * 1.1) + 0.04, 0.36, 0.98);
        val = Math.Clamp(Math.Max(val, 0.62), 0.48, 0.98);
        var accent = ColorFromHsv(hue, sat, val);
        lowAccent = accent;
        midAccent = Color.FromArgb(255, 255, 255, 255);
        highAccent = Color.FromArgb(255, 255, 255, 255);
    }

    private LyricsPalette GetArtworkAdaptivePalette()
    {
        var white = Color.FromArgb(255, 255, 255, 255);
        var accent = MakeTextTint(artworkLowAccentColor);
        return new LyricsPalette(
            LerpColor(white, accent, 0.76),
            LerpColor(white, accent, 0.45),
            LerpColor(white, accent, 0.3),
            LerpColor(white, accent, 0.34));
    }

    private static Color MakeTextTint(Color source)
    {
        ToHsv(source, out var hue, out var sat, out var val);
        var textSat = Math.Clamp((sat * 0.8) + 0.04, 0.18, 0.8);
        var textVal = Math.Clamp(Math.Max(val, 0.9), 0.82, 1);
        return ColorFromHsv(hue, textSat, textVal);
    }

    private static string BuildTrackKey(TrackInfo track)
    {
        var artist = track.Artist?.Trim().ToLowerInvariant() ?? string.Empty;
        var title = track.Title?.Trim().ToLowerInvariant() ?? string.Empty;
        return $"{artist}\u001f{title}";
    }

    private void ApplySnapshotTrackInfo(TrackInfo track)
    {
        TrackTitle = track.Title;
        ArtistName = string.IsNullOrWhiteSpace(track.Artist)
            ? L("Unknown artist", "Неизвестный исполнитель")
            : track.Artist;
        AudioSourceAppId = track.SourceAppId ?? string.Empty;
        UpdateCoverArt(track.ArtworkPath);
    }

    private void UpdateCoverArt(string? artworkPath)
    {
        var normalizedPath = !string.IsNullOrWhiteSpace(artworkPath) && File.Exists(artworkPath)
            ? artworkPath
            : null;
        var hasArtNow = normalizedPath is not null;
        if (string.Equals(currentArtworkPath, normalizedPath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        currentArtworkPath = normalizedPath ?? string.Empty;
        hasCoverArt = hasArtNow;
        CoverArtImage = CreateCoverArtImage(normalizedPath);
        UpdateArtworkTheme(normalizedPath);
        NotifyCoverPresentationChanged();
    }

    public void ApplyPositionPreset(WindowPositionPreset preset)
    {
        if (preset == SelectedPositionPreset)
        {
            if (preset != WindowPositionPreset.Manual)
            {
                _ = windowModeService.TryApplyPositionPreset(preset);
            }

            return;
        }

        SelectedPositionPreset = preset;
    }

    public void ApplyStartupWindowState()
    {
        ApplyWindowSettings();
    }

    public void SetOverlayResizeDeferred(bool value)
    {
        deferOverlayResize = value;
        if (!deferOverlayResize)
        {
            _ = TryApplyOverlayResize();
        }
    }

    public void RestoreOverlayVisibility()
    {
        // Desktop-only widget: re-attach to the shell desktop (e.g. after Explorer restart).
        _ = windowModeService.TrySetMode(WindowPresentationMode.Wallpaper);
        _ = TryApplyOverlayResize();
        StatusText = L("Widget restored on the desktop.", "Виджет восстановлен на рабочем столе.");
    }

    /// <summary>Re-attaches the widget to the desktop when Explorer recreated its windows.</summary>
    public bool EnsureDesktopAttachment() => windowModeService.EnsureWallpaperAttachment();

    public void Dispose()
    {
        disposeTokenSource.Cancel();

        lineTimer.Stop();
        lineTimer.Tick -= OnLineTimerTick;

        playbackTimer.Stop();
        playbackTimer.Tick -= OnPlaybackTimerTick;

        CustomPositionPresets.CollectionChanged -= OnCustomPositionPresetsChanged;
        disposeTokenSource.Dispose();
    }

    public void ReloadSettingsFromDisk()
    {
        var currentTicks = ReadSettingsWriteTicks();
        if (currentTicks == 0 || currentTicks <= lastAppliedSettingsWriteTicks)
        {
            return;
        }

        ApplyPersistedSettings(settingsStore.Load(), applyStartupFallback: false);
        lastAppliedSettingsWriteTicks = currentTicks;
        if (!settingsOnlyMode)
        {
            ApplyWindowSettings();
        }
    }

    private void ApplyPersistedSettings(OverlayUserSettings settings, bool applyStartupFallback)
    {
        suppressSettingsPropertyHandlers = true;
        suppressModePropertyHandlers = true;
        suppressAlwaysOnTopPropertyHandler = true;
        suppressCustomPresetSelectionHandler = true;

        ShowLineTimings = settings.ShowLineTimings;
        ShowDebugHitboxes = settings.ShowDebugHitboxes;
        ShowRuntimeDebugPanel = settings.ShowRuntimeDebugPanel;
        HideOnPause = settings.HideOnPause;
        PauseHideDelaySeconds = Math.Clamp(settings.PauseHideDelaySeconds, 1.0, 20.0);
        ShowDragHandle = settings.ShowDragHandle;
        SettingsUseDarkTheme = settings.SettingsUseDarkTheme;
        ClickThroughMode = settings.ClickThroughMode;
        LyricsColorPreset = settings.LyricsColorPreset;
        LyricsTextAlignment = settings.LyricsTextAlignment;
        AutoLyricsTextAlignment = settings.AutoLyricsTextAlignment;
        OverlayWidth = Math.Clamp(settings.OverlayWidth, OverlayMinWidth, OverlayMaxWidth);
        SetOverlayContentOffset(settings.OverlayContentOffsetX, settings.OverlayContentOffsetY, persist: false);
        LineAnimationDurationSeconds = Math.Clamp(settings.LineAnimationDurationSeconds, 0.12, 1.2);
        OscilloscopeResponsiveness = Math.Clamp(settings.OscilloscopeResponsiveness, 0.08, 0.95);
        OscilloscopeSensitivity = Math.Clamp(settings.OscilloscopeSensitivity, 0.5, 2.6);
        OscilloscopePeakDecay = Math.Clamp(settings.OscilloscopePeakDecay, 0.65, 0.99);
        OscilloscopeColorIntensity = Math.Clamp(settings.OscilloscopeColorIntensity, 0, 1);
        OscilloscopeDetail = Math.Clamp(settings.OscilloscopeDetail, 96, 320);
        OscilloscopeLowFrequencyFocus = Math.Clamp(settings.OscilloscopeLowFrequencyFocus, 0, 1);
        OscilloscopeMirrorBars = settings.OscilloscopeMirrorBars;
        AppScopedAudioCapture = settings.AppScopedAudioCapture;
        ViewportFadeZone = Math.Clamp(settings.ViewportFadeZone, 0, 180);
        ViewportFadeSoftness = Math.Clamp(settings.ViewportFadeSoftness, 0.4, 2.2);
        TitleFontSize = Math.Clamp(settings.TitleFontSize, 16, 54);
        ArtistFontSize = Math.Clamp(settings.ArtistFontSize, 12, 32);
        CurrentLineFontSize = Math.Clamp(settings.CurrentLineFontSize, 20, 84);
        NextLineFontSize = Math.Clamp(settings.NextLineFontSize, 16, 54);
        NextLineTopMargin = Math.Clamp(settings.NextLineTopMargin, 0, 48);
        TextShadowOpacity = Math.Clamp(settings.TextShadowOpacity, 0.05, 1);
        ShowCoverArt = settings.ShowCoverArt;
        AutoHideCoverOnLongLine = settings.AutoHideCoverOnLongLine;
        CenterAlignmentShowCoverOnTop = settings.CenterAlignmentShowCoverOnTop;
        CenterAlignmentCoverSize = Math.Clamp(settings.CenterAlignmentCoverSize, 56, 240);
        GlassVisibility = Enum.IsDefined(settings.GlassVisibility) ? settings.GlassVisibility : GlassVisibilityMode.OnHover;
        GlassBlur = Math.Clamp(settings.GlassBlur, 0, 40);
        GlassRefraction = Math.Clamp(settings.GlassRefraction, 0, 1);
        GlassDispersion = Math.Clamp(settings.GlassDispersion, 0, 1);
        GlassSpecular = Math.Clamp(settings.GlassSpecular, 0, 1);
        GlassTint = Enum.IsDefined(settings.GlassTint) ? settings.GlassTint : GlassTintPreset.Smoke;
        GlassTintStrength = Math.Clamp(settings.GlassTintStrength, 0, 0.9);
        GlassCornerRadius = Math.Clamp(settings.GlassCornerRadius, 8, 56);
        GlassPadding = Math.Clamp(settings.GlassPadding, 8, 56);
        GlassCursorLight = settings.GlassCursorLight;
        UseArtworkTextColors = settings.UseArtworkTextColors;
        LyricsFont = Enum.IsDefined(settings.LyricsFont) ? settings.LyricsFont : LyricsFontOption.SegoeVariable;
        LyricsFontWeight = Enum.IsDefined(settings.LyricsFontWeight) ? settings.LyricsFontWeight : LyricsFontWeightOption.Semibold;
        LineAnimationStyle = Enum.IsDefined(settings.LineAnimationStyle) ? settings.LineAnimationStyle : LineAnimationStyle.Spring;
        spicyLyricsApiKey = settings.SpicyLyricsApiKey?.Trim() ?? string.Empty;
        CustomFontFamily = settings.CustomFontFamily?.Trim() ?? string.Empty;
        TextContrast = Enum.IsDefined(settings.TextContrast) ? settings.TextContrast : TextContrastMode.Auto;
        WordByWordHighlight = settings.WordByWordHighlight;
        LyricsOffsetMs = Math.Clamp(settings.LyricsOffsetMs, -2000, 2000);

        CustomPositionPresets.Clear();
        foreach (var preset in settings.CustomPositionPresets)
        {
            if (string.IsNullOrWhiteSpace(preset.Id) || string.IsNullOrWhiteSpace(preset.Name))
            {
                continue;
            }

            var normalizedX = Math.Clamp(preset.RelativeX, 0, 1);
            var normalizedY = Math.Clamp(preset.RelativeY, 0, 1);
            CustomPositionPresets.Add(new CustomPositionPreset
            {
                Id = preset.Id,
                Name = preset.Name.Trim(),
                RelativeX = normalizedX,
                RelativeY = normalizedY
            });
        }

        SelectedPositionPreset = settings.SelectedPositionPreset;
        SelectedCustomPositionPreset = string.IsNullOrWhiteSpace(settings.SelectedCustomPresetId)
            ? null
            : CustomPositionPresets.FirstOrDefault(x => string.Equals(x.Id, settings.SelectedCustomPresetId, StringComparison.Ordinal));

        StartInDesktopWidgetMode = settings.StartInDesktopWidgetMode;
        // The widget lives on the desktop only: it is always hosted inside the shell
        // desktop window and never floats above other applications.
        DesktopWidgetMode = true;
        AlwaysOnTop = false;
        EnableAutomaticUpdateChecks = settings.EnableAutomaticUpdateChecks;
        UpdateCheckIntervalHours = Math.Clamp(settings.UpdateCheckIntervalHours, 1, 168);
        // Updates come from the project's own GitHub releases unless configured otherwise.
        GitHubOwner = string.IsNullOrWhiteSpace(settings.GitHubOwner) ? DefaultUpdateOwner : settings.GitHubOwner.Trim();
        GitHubRepository = string.IsNullOrWhiteSpace(settings.GitHubRepository) ? DefaultUpdateRepository : settings.GitHubRepository.Trim();
        LastUpdateCheckUtc = settings.LastUpdateCheckUtc;
        updateReleaseUrl = GitHubUpdateService.BuildReleasesPageUrl(GitHubOwner, GitHubRepository);

        var shouldRunAtStartup = settings.RunAtStartup;
        var startupEnabled = startupRegistrationService.IsEnabled();
        if (shouldRunAtStartup)
        {
            _ = startupRegistrationService.TrySetEnabled(
                enabled: true,
                startInDesktopWidgetMode: settings.StartInDesktopWidgetMode || settings.DesktopWidgetMode);
        }
        else if (startupEnabled)
        {
            _ = startupRegistrationService.TrySetEnabled(enabled: false, startInDesktopWidgetMode: false);
        }

        RunAtStartup = startupRegistrationService.IsEnabled();

        suppressCustomPresetSelectionHandler = false;
        suppressAlwaysOnTopPropertyHandler = false;
        suppressModePropertyHandlers = false;
        suppressSettingsPropertyHandlers = false;

        OnPropertyChanged(nameof(CustomPresetsVisibility));
        OnPropertyChanged(nameof(IsGitHubUpdateConfigured));
        OnPropertyChanged(nameof(UpdateRepositoryText));
        OnPropertyChanged(nameof(UpdateCheckIntervalText));
        OnPropertyChanged(nameof(HasUpdateReleaseUrl));
        OnPropertyChanged(nameof(TrackTitleBrush));
        OnPropertyChanged(nameof(ArtistBrush));
        OnPropertyChanged(nameof(CurrentLineBrush));
        OnPropertyChanged(nameof(PreviousLineBrush));
        OnPropertyChanged(nameof(NextLineBrush));
        OnPropertyChanged(nameof(TimingBrush));
        OnPropertyChanged(nameof(TextShadowBrush));
        UpdateCoverVisibility();
    }

    private void ApplyWindowSettings()
    {
        _ = TryApplyOverlayResize();
        var targetMode = DesktopWidgetMode ? WindowPresentationMode.Wallpaper : WindowPresentationMode.Floating;
        var modeApplied = windowModeService.TrySetMode(targetMode);
        if (!modeApplied && DesktopWidgetMode)
        {
            suppressModePropertyHandlers = true;
            DesktopWidgetMode = false;
            suppressModePropertyHandlers = false;
            StatusText = L("Unable to enable desktop widget mode.", "Не удалось включить режим desktop-widget.");
            PersistSettingsIfReady();
        }

        _ = windowModeService.TrySetAlwaysOnTop(!DesktopWidgetMode && AlwaysOnTop);
        if (!DesktopWidgetMode)
        {
            ReapplyCurrentPositionPreset();
            _ = windowModeService.TryEnsureVisibleOnWorkArea();
        }
    }

    private bool TryApplyOverlayResize()
    {
        var targetWidth = (int)Math.Round(OverlayWidth);
        var targetHeight = FloatingOverlayHeight;
        if (windowModeService.TryGetWorkAreaSize(out var workAreaWidth, out var workAreaHeight))
        {
            // Full-screen overlay strategy for all modes:
            // match monitor area to avoid edge clipping and corner resizing workflow.
            targetWidth = workAreaWidth;
            targetHeight = workAreaHeight;
        }

        var resized = windowModeService.TryResize(targetWidth, targetHeight);
        if (resized && !DesktopWidgetMode)
        {
            _ = windowModeService.TryEnsureVisibleOnWorkArea();
        }

        return resized;
    }

    public void SetOverlayContentOffset(double x, double y, bool persist)
    {
        var normalizedX = double.IsFinite(x) ? x : 0;
        var normalizedY = double.IsFinite(y) ? y : 0;
        var changed = Math.Abs(overlayContentOffsetX - normalizedX) > 0.01 ||
            Math.Abs(overlayContentOffsetY - normalizedY) > 0.01;
        if (!changed)
        {
            return;
        }

        overlayContentOffsetX = normalizedX;
        overlayContentOffsetY = normalizedY;
        OnPropertyChanged(nameof(OverlayContentOffsetX));
        OnPropertyChanged(nameof(OverlayContentOffsetY));
        if (persist)
        {
            PersistSettingsIfReady();
        }
    }

    private void ReapplyCurrentPositionPreset()
    {
        if (SelectedPositionPreset != WindowPositionPreset.Manual)
        {
            _ = windowModeService.TryApplyPositionPreset(SelectedPositionPreset);
            return;
        }

        if (SelectedCustomPositionPreset is not null)
        {
            _ = windowModeService.TryApplyRelativePosition(
                SelectedCustomPositionPreset.RelativeX,
                SelectedCustomPositionPreset.RelativeY);
        }
    }

    private void PersistSettingsIfReady()
    {
        if (suppressSettingsPropertyHandlers)
        {
            return;
        }

        PersistSettings();
    }

    private void PersistSettings()
    {
        settingsStore.Save(new OverlayUserSettings
        {
            ShowLineTimings = ShowLineTimings,
            ShowDebugHitboxes = ShowDebugHitboxes,
            ShowRuntimeDebugPanel = ShowRuntimeDebugPanel,
            HideOnPause = HideOnPause,
            PauseHideDelaySeconds = PauseHideDelaySeconds,
            AlwaysOnTop = AlwaysOnTop,
            DesktopWidgetMode = DesktopWidgetMode,
            ShowDragHandle = ShowDragHandle,
            SettingsUseDarkTheme = SettingsUseDarkTheme,
            StartInDesktopWidgetMode = StartInDesktopWidgetMode,
            RunAtStartup = RunAtStartup,
            EnableAutomaticUpdateChecks = EnableAutomaticUpdateChecks,
            UpdateCheckIntervalHours = UpdateCheckIntervalHours,
            GitHubOwner = GitHubOwner,
            GitHubRepository = GitHubRepository,
            LastUpdateCheckUtc = LastUpdateCheckUtc,
            SelectedPositionPreset = SelectedPositionPreset,
            ClickThroughMode = ClickThroughMode,
            LyricsColorPreset = LyricsColorPreset,
            LyricsTextAlignment = LyricsTextAlignment,
            AutoLyricsTextAlignment = AutoLyricsTextAlignment,
            OverlayWidth = OverlayWidth,
            OverlayContentOffsetX = OverlayContentOffsetX,
            OverlayContentOffsetY = OverlayContentOffsetY,
            LineAnimationDurationSeconds = LineAnimationDurationSeconds,
            OscilloscopeResponsiveness = OscilloscopeResponsiveness,
            OscilloscopeSensitivity = OscilloscopeSensitivity,
            OscilloscopePeakDecay = OscilloscopePeakDecay,
            OscilloscopeColorIntensity = OscilloscopeColorIntensity,
            OscilloscopeDetail = OscilloscopeDetail,
            OscilloscopeLowFrequencyFocus = OscilloscopeLowFrequencyFocus,
            OscilloscopeMirrorBars = OscilloscopeMirrorBars,
            AppScopedAudioCapture = AppScopedAudioCapture,
            ViewportFadeZone = ViewportFadeZone,
            ViewportFadeSoftness = ViewportFadeSoftness,
            TitleFontSize = TitleFontSize,
            ArtistFontSize = ArtistFontSize,
            CurrentLineFontSize = CurrentLineFontSize,
            NextLineFontSize = NextLineFontSize,
            NextLineTopMargin = NextLineTopMargin,
            TextShadowOpacity = TextShadowOpacity,
            ShowCoverArt = ShowCoverArt,
            AutoHideCoverOnLongLine = AutoHideCoverOnLongLine,
            CenterAlignmentShowCoverOnTop = CenterAlignmentShowCoverOnTop,
            CenterAlignmentCoverSize = CenterAlignmentCoverSize,
            GlassVisibility = GlassVisibility,
            GlassBlur = GlassBlur,
            GlassRefraction = GlassRefraction,
            GlassDispersion = GlassDispersion,
            GlassSpecular = GlassSpecular,
            GlassTint = GlassTint,
            GlassTintStrength = GlassTintStrength,
            GlassCornerRadius = GlassCornerRadius,
            GlassPadding = GlassPadding,
            GlassCursorLight = GlassCursorLight,
            UseArtworkTextColors = UseArtworkTextColors,
            LyricsFont = LyricsFont,
            LyricsFontWeight = LyricsFontWeight,
            LineAnimationStyle = LineAnimationStyle,
            SpicyLyricsApiKey = spicyLyricsApiKey,
            CustomFontFamily = CustomFontFamily,
            TextContrast = TextContrast,
            WordByWordHighlight = WordByWordHighlight,
            LyricsOffsetMs = LyricsOffsetMs,
            CustomPositionPresets = [.. CustomPositionPresets],
            SelectedCustomPresetId = SelectedCustomPositionPreset?.Id ?? string.Empty
        });
        lastAppliedSettingsWriteTicks = ReadSettingsWriteTicks();
    }

    private long ReadSettingsWriteTicks()
    {
        try
        {
            return File.Exists(SettingsFilePath)
                ? File.GetLastWriteTimeUtc(SettingsFilePath).Ticks
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void OnCustomPositionPresetsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RemoveSelectedCustomPresetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CustomPresetsVisibility));
        PersistSettingsIfReady();
    }

    private bool IsManualCenterAlignment => LyricsTextAlignment == LyricsTextAlignmentMode.Center && !AutoLyricsTextAlignment;

    // Album art is no longer drawn inside the desktop widget (it still drives the colours).
    private bool IsCoverVisible => false;

    private static string NormalizeTrackMetaPartV2(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        text = text
            .Replace("вЂ”", " ")
            .Replace("вЂ“", " ")
            .Replace("вЂў", " ")
            .Replace("В·", " ")
            .Trim();

        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        return text.Trim('|', '-', '\u2014', '\u2013', '\u00B7', '\u2022', ':', ';').Trim();
    }

    private string BuildTrackTitleDisplayV2()
    {
        if (!IsManualCenterAlignment)
        {
            return TrackTitle;
        }

        var artist = NormalizeTrackMetaPartV2(ArtistName);
        var title = NormalizeTrackMetaPartV2(TrackTitle);
        if (string.IsNullOrWhiteSpace(artist))
        {
            return title;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return artist;
        }

        return $"{artist} \u2014 {title}";
    }

    private static string NormalizeTrackMetaPart(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        // Clean accidental separator-only values (eg "|" from providers) and edge separators.
        text = text.Trim('|', '-', '—', '–', '·', '•').Trim();
        return text;
    }

    private string BuildTrackTitleDisplay()
    {
        if (!IsManualCenterAlignment)
        {
            return TrackTitle;
        }

        var artist = NormalizeTrackMetaPart(ArtistName);
        var title = NormalizeTrackMetaPart(TrackTitle);
        if (string.IsNullOrWhiteSpace(artist))
        {
            return title;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return artist;
        }

        return $"{artist} — {title}";
    }

    private void NotifyTrackMetaPresentationChanged()
    {
        OnPropertyChanged(nameof(TrackTitleDisplay));
        OnPropertyChanged(nameof(ArtistNameVisibility));
        OnPropertyChanged(nameof(UseCenterTopCoverLayout));
    }

    private void UpdateCoverVisibility()
    {
        var shouldHide = AutoHideCoverOnLongLine && CurrentLine.Length >= 58;
        if (hideCoverForCurrentLine == shouldHide)
        {
            return;
        }

        hideCoverForCurrentLine = shouldHide;
        NotifyCoverPresentationChanged();
    }

    private void NotifyCoverPresentationChanged()
    {
        NotifyTrackMetaPresentationChanged();
        OnPropertyChanged(nameof(CoverVisibility));
        OnPropertyChanged(nameof(CoverOpacity));
        OnPropertyChanged(nameof(CoverSize));
        OnPropertyChanged(nameof(CoverColumnSpacing));
    }

    private bool ShouldUseDesktopWidgetStartupArgument()
    {
        return StartInDesktopWidgetMode || DesktopWidgetMode;
    }

    private void NormalizeFontSize(ref double backingField, double value, string propertyName, double min, double max)
    {
        var clamped = Math.Clamp(value, min, max);
        if (Math.Abs(clamped - value) > 0.001)
        {
            suppressSettingsPropertyHandlers = true;
            backingField = clamped;
            OnPropertyChanged(propertyName);
            suppressSettingsPropertyHandlers = false;
            return;
        }

        PersistSettingsIfReady();
    }

    private Color GetShadowColor()
    {
        var alpha = (byte)Math.Round(Math.Clamp(TextShadowOpacity, 0.05, 1) * 255);
        // Dark text on a light backdrop gets a soft light halo instead of a dark shadow.
        return UseDarkText
            ? Color.FromArgb((byte)(alpha * 0.8), 255, 255, 255)
            : Color.FromArgb(alpha, 0, 0, 0);
    }

    private LyricsPalette GetPalette()
    {
        if (UseDarkText)
        {
            // Readable on light wallpapers / frosted glass: near-black with a hint of the artwork hue.
            var hue = hasArtworkTheme && UseArtworkTextColors ? artworkDominantColor : Color.FromArgb(255, 30, 34, 44);
            return new LyricsPalette(
                LerpColor(Color.FromArgb(255, 12, 14, 20), hue, 0.18),
                LerpColor(Color.FromArgb(255, 44, 48, 58), hue, 0.22),
                LerpColor(Color.FromArgb(255, 70, 74, 84), hue, 0.2),
                Color.FromArgb(255, 60, 64, 74));
        }

        if (hasArtworkTheme && UseArtworkTextColors)
        {
            return GetArtworkAdaptivePalette();
        }

        return LyricsColorPreset switch
        {
            LyricsColorPreset.WarmAmber => new LyricsPalette(
                Color.FromArgb(255, 255, 225, 186),
                Color.FromArgb(255, 244, 190, 120),
                Color.FromArgb(255, 232, 154, 83),
                Color.FromArgb(255, 195, 147, 90)),
            LyricsColorPreset.IceBlue => new LyricsPalette(
                Color.FromArgb(255, 230, 245, 255),
                Color.FromArgb(255, 179, 218, 246),
                Color.FromArgb(255, 146, 205, 240),
                Color.FromArgb(255, 125, 182, 220)),
            LyricsColorPreset.Mint => new LyricsPalette(
                Color.FromArgb(255, 227, 255, 246),
                Color.FromArgb(255, 160, 236, 212),
                Color.FromArgb(255, 122, 222, 192),
                Color.FromArgb(255, 109, 199, 173)),
            _ => new LyricsPalette(
                Color.FromArgb(255, 255, 255, 255),
                Color.FromArgb(255, 213, 213, 213),
                Color.FromArgb(255, 186, 186, 186),
                Color.FromArgb(255, 190, 190, 190))
        };
    }

    private static Color LerpColor(Color from, Color to, double t)
    {
        var clamped = Math.Clamp(t, 0, 1);
        var a = (byte)Math.Round(from.A + ((to.A - from.A) * clamped));
        var r = (byte)Math.Round(from.R + ((to.R - from.R) * clamped));
        var g = (byte)Math.Round(from.G + ((to.G - from.G) * clamped));
        var b = (byte)Math.Round(from.B + ((to.B - from.B) * clamped));
        return Color.FromArgb(a, r, g, b);
    }

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        var h = ((hue % 360) + 360) % 360;
        var s = Math.Clamp(saturation, 0, 1);
        var v = Math.Clamp(value, 0, 1);
        var c = v * s;
        var x = c * (1 - Math.Abs(((h / 60) % 2) - 1));
        var m = v - c;

        double rPrime;
        double gPrime;
        double bPrime;
        if (h < 60)
        {
            rPrime = c;
            gPrime = x;
            bPrime = 0;
        }
        else if (h < 120)
        {
            rPrime = x;
            gPrime = c;
            bPrime = 0;
        }
        else if (h < 180)
        {
            rPrime = 0;
            gPrime = c;
            bPrime = x;
        }
        else if (h < 240)
        {
            rPrime = 0;
            gPrime = x;
            bPrime = c;
        }
        else if (h < 300)
        {
            rPrime = x;
            gPrime = 0;
            bPrime = c;
        }
        else
        {
            rPrime = c;
            gPrime = 0;
            bPrime = x;
        }

        var r = (byte)Math.Round((rPrime + m) * 255);
        var g = (byte)Math.Round((gPrime + m) * 255);
        var b = (byte)Math.Round((bPrime + m) * 255);
        return Color.FromArgb(255, r, g, b);
    }

    private static void ToHsv(Color color, out double hue, out double saturation, out double value)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        if (delta < 0.00001)
        {
            hue = 0;
        }
        else if (Math.Abs(max - r) < 0.00001)
        {
            hue = 60 * (((g - b) / delta) % 6);
        }
        else if (Math.Abs(max - g) < 0.00001)
        {
            hue = 60 * (((b - r) / delta) + 2);
        }
        else
        {
            hue = 60 * (((r - g) / delta) + 4);
        }

        if (hue < 0)
        {
            hue += 360;
        }

        saturation = max <= 0 ? 0 : delta / max;
        value = max;
    }

    private static double LerpHue(double fromHue, double toHue, double t)
    {
        var clamped = Math.Clamp(t, 0, 1);
        var delta = ((toHue - fromHue + 540) % 360) - 180;
        var result = fromHue + (delta * clamped);
        if (result < 0)
        {
            result += 360;
        }

        if (result >= 360)
        {
            result -= 360;
        }

        return result;
    }

    private static double GetHueDistance(double a, double b)
    {
        var delta = Math.Abs(a - b) % 360;
        return delta > 180 ? 360 - delta : delta;
    }

    private static string L(string en, string ru)
    {
        return UseRussianUi ? ru : en;
    }

    private readonly record struct LyricsPalette(Color PrimaryText, Color SecondaryText, Color MutedText, Color TimingText);
}
