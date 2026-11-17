using FloatingLyrics.Services;

namespace FloatingLyrics.Models;

public sealed class OverlayUserSettings
{
    public bool ShowLineTimings { get; init; }
    public bool ShowDebugHitboxes { get; init; }
    public bool ShowRuntimeDebugPanel { get; init; }
    public bool HideOnPause { get; init; }
    public double PauseHideDelaySeconds { get; init; } = 5.0;
    public bool AlwaysOnTop { get; init; } = true;
    public bool DesktopWidgetMode { get; init; }
    public bool ShowDragHandle { get; init; } = true;
    public bool SettingsUseDarkTheme { get; init; }
    public bool StartInDesktopWidgetMode { get; init; }
    public bool RunAtStartup { get; init; }
    public bool EnableAutomaticUpdateChecks { get; init; } = true;
    public double UpdateCheckIntervalHours { get; init; } = 12;
    public string GitHubOwner { get; init; } = string.Empty;
    public string GitHubRepository { get; init; } = string.Empty;
    public DateTimeOffset? LastUpdateCheckUtc { get; init; }
    public WindowPositionPreset SelectedPositionPreset { get; init; } = WindowPositionPreset.Manual;
    public ClickThroughMode ClickThroughMode { get; init; } = ClickThroughMode.OutsideDragHandle;
    public LyricsColorPreset LyricsColorPreset { get; init; } = LyricsColorPreset.ClassicWhite;
    public LyricsTextAlignmentMode LyricsTextAlignment { get; init; } = LyricsTextAlignmentMode.Left;
    public bool AutoLyricsTextAlignment { get; init; }
    public double OverlayWidth { get; init; } = 1320;
    public double OverlayContentOffsetX { get; init; }
    public double OverlayContentOffsetY { get; init; }
    public double LineAnimationDurationSeconds { get; init; } = 0.25;
    public double OscilloscopeResponsiveness { get; init; } = 0.58;
    public double OscilloscopeSensitivity { get; init; } = 1.3;
    public double OscilloscopePeakDecay { get; init; } = 0.86;
    public double OscilloscopeColorIntensity { get; init; } = 0.82;
    public double OscilloscopeDetail { get; init; } = 240;
    public double OscilloscopeLowFrequencyFocus { get; init; } = 0.55;
    public bool OscilloscopeMirrorBars { get; init; } = true;
    public bool AppScopedAudioCapture { get; init; } = true;
    public double ViewportFadeZone { get; init; } = 58;
    public double ViewportFadeSoftness { get; init; } = 1.0;
    public double TitleFontSize { get; init; } = 32;
    public double ArtistFontSize { get; init; } = 19;
    public double CurrentLineFontSize { get; init; } = 47;
    public double NextLineFontSize { get; init; } = 30;
    public double NextLineTopMargin { get; init; } = 12;
    public double TextShadowOpacity { get; init; } = 0.58;
    public bool ShowCoverArt { get; init; } = true;
    public bool AutoHideCoverOnLongLine { get; init; } = true;
    public bool CenterAlignmentShowCoverOnTop { get; init; }
    public double CenterAlignmentCoverSize { get; init; } = 92;
    public GlassVisibilityMode GlassVisibility { get; init; } = GlassVisibilityMode.OnHover;
    public double GlassBlur { get; init; } = 14;
    public double GlassRefraction { get; init; } = 0.65;
    public double GlassDispersion { get; init; } = 0.6;
    public double GlassSpecular { get; init; } = 0.8;
    public GlassTintPreset GlassTint { get; init; } = GlassTintPreset.Smoke;
    public double GlassTintStrength { get; init; } = 0.32;
    public double GlassCornerRadius { get; init; } = 30;
    public double GlassPadding { get; init; } = 22;
    public bool GlassCursorLight { get; init; } = true;
    public bool UseArtworkTextColors { get; init; } = true;
    public LyricsFontOption LyricsFont { get; init; } = LyricsFontOption.SegoeVariable;
    public LyricsFontWeightOption LyricsFontWeight { get; init; } = LyricsFontWeightOption.Semibold;
    public LineAnimationStyle LineAnimationStyle { get; init; } = LineAnimationStyle.Spring;
    /// <summary>Personal Spicy Lyrics API key (local settings only, never shipped).</summary>
    public string SpicyLyricsApiKey { get; init; } = string.Empty;
    public string CustomFontFamily { get; init; } = string.Empty;
    public TextContrastMode TextContrast { get; init; } = TextContrastMode.Auto;
    public bool WordByWordHighlight { get; init; } = true;
    public double LyricsOffsetMs { get; init; }
    public IReadOnlyList<CustomPositionPreset> CustomPositionPresets { get; init; } = [];
    public string SelectedCustomPresetId { get; init; } = string.Empty;
}
