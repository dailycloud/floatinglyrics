namespace FloatingLyrics.Models;

/// <summary>When the liquid-glass plate behind the widget is shown.</summary>
public enum GlassVisibilityMode
{
    OnHover = 0,
    Always = 1,
    Hidden = 2
}

/// <summary>Tint applied on top of the refracted wallpaper.</summary>
public enum GlassTintPreset
{
    Clear = 0,
    Smoke = 1,
    Frost = 2,
    Artwork = 3,
    Ocean = 4,
    Rose = 5
}

public enum LyricsFontOption
{
    SegoeVariable = 0,
    SegoeDisplay = 1,
    Bahnschrift = 2,
    Georgia = 3,
    Consolas = 4
}

public enum LyricsFontWeightOption
{
    Light = 0,
    Regular = 1,
    Semibold = 2,
    Bold = 3
}

/// <summary>Easing family used for lyric line changes.</summary>
public enum LineAnimationStyle
{
    Smooth = 0,
    Spring = 1,
    Snappy = 2
}

/// <summary>How lyric text keeps contrast against the wallpaper.</summary>
public enum TextContrastMode
{
    /// <summary>Light or dark text, chosen from the brightness of the wallpaper behind the widget.</summary>
    Auto = 0,
    Light = 1,
    Dark = 2,
    /// <summary>Per-pixel: every part of a glyph is white over dark wallpaper and black over light.</summary>
    Invert = 3
}
