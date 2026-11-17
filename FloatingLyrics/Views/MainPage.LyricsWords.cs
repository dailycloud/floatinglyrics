using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace FloatingLyrics.Views;

/// <summary>
/// Word-by-word highlighting (Spicy Lyrics syllable timing) and text-contrast handling.
///
/// Words: unsung syllables of the active line are dimmed; the syllable being sung gets a soft
/// left-to-right gradient wipe at its exact playback progress; sung syllables are full colour.
/// The playback clock is interpolated between SMTC polls, and the overlay is redrawn every frame
/// only while a word-timed line is active and music is playing.
///
/// Contrast: "Auto" samples the wallpaper under the widget (blended with the glass tint) and
/// switches between light and dark text with hysteresis; "Invert" fills glyphs per pixel with
/// white over dark areas and black over light ones.
/// </summary>
public sealed partial class MainPage
{
    private const double UnsungWordAlpha = 0.38;
    private const float WipeFeather = 0.16f;
    private const double BackgroundVocalAlpha = 0.72;
    private const double BackgroundLineScale = 0.62;
    private static readonly TimeSpan ContrastSampleInterval = TimeSpan.FromMilliseconds(450);

    private bool isWordTickerActive;
    private readonly Stopwatch contrastSampleClock = Stopwatch.StartNew();

    // ------------------------------------------------------------------ words

    /// <summary>Styles the lead line of an item (word wipe while it is being sung).</summary>
    private void ApplyWordStyling(
        ICanvasResourceCreator creator,
        CanvasTextLayout layout,
        PhysicalLyricItem item,
        Color baseColor,
        List<IDisposable> resources)
    {
        var line = item.Line;
        var text = item.MainText.Text ?? string.Empty;
        var timed = line is { HasWordTiming: true } && string.Equals(line.Text, text, StringComparison.Ordinal);
        ApplySyllableStyling(creator, layout, text, timed ? line!.Syllables : null, item, baseColor, resources);
    }

    /// <summary>
    /// Word-by-word colouring of one layout from its syllable timings, exactly as authored:
    /// unsung syllables dimmed, the sung one wiped left to right, sung ones at full colour.
    /// Every call starts from a clean layout so no styling from an earlier frame survives.
    /// </summary>
    private void ApplySyllableStyling(
        ICanvasResourceCreator creator,
        CanvasTextLayout layout,
        string text,
        IReadOnlyList<LyricSyllable>? syllables,
        PhysicalLyricItem item,
        Color sung,
        List<IDisposable> resources)
    {
        if (text.Length == 0)
        {
            return;
        }

        layout.SetBrush(0, text.Length, null);
        var useWords = ViewModel.WordByWordHighlight &&
            syllables is { Count: > 0 } &&
            item.Active.Value > 0.02 &&
            !item.IsExiting;
        if (!useWords)
        {
            return;
        }

        var now = ViewModel.GetInterpolatedPlaybackPosition();
        var emphasis = Math.Clamp(item.Active.Value, 0, 1);
        var unsung = sung;
        unsung.A = (byte)Math.Round(sung.A * (1 - ((1 - UnsungWordAlpha) * emphasis)));

        foreach (var syllable in syllables!)
        {
            if (syllable.CharLength <= 0 || syllable.CharStart + syllable.CharLength > text.Length)
            {
                continue;
            }

            var duration = Math.Max(0.001, (syllable.End - syllable.Start).TotalSeconds);
            var progress = (float)Math.Clamp((now - syllable.Start).TotalSeconds / duration, 0, 1);
            if (progress >= 1f)
            {
                layout.SetColor(syllable.CharStart, syllable.CharLength, sung);
                continue;
            }

            if (progress <= 0f)
            {
                layout.SetColor(syllable.CharStart, syllable.CharLength, unsung);
                continue;
            }

            var bounds = UnionRegions(layout.GetCharacterRegions(syllable.CharStart, syllable.CharLength));
            if (bounds.Width < 0.5)
            {
                layout.SetColor(syllable.CharStart, syllable.CharLength, progress > 0.5f ? sung : unsung);
                continue;
            }

            // The soft edge travels from fully outside the left side (progress 0) to fully past
            // the right side (progress 1), so nothing lights up before the syllable is sung.
            var edge = (progress * (1f + (2f * WipeFeather))) - WipeFeather;
            var lead = Math.Clamp(edge - WipeFeather, 0f, 1f);
            var trail = Math.Clamp(edge + WipeFeather, 0f, 1f);
            var brush = new CanvasLinearGradientBrush(
                creator,
                [
                    new CanvasGradientStop { Position = 0f, Color = lead > 0f ? sung : unsung },
                    new CanvasGradientStop { Position = lead, Color = sung },
                    new CanvasGradientStop { Position = trail, Color = unsung },
                    new CanvasGradientStop { Position = 1f, Color = unsung }
                ])
            {
                StartPoint = new Vector2((float)bounds.X, 0),
                EndPoint = new Vector2((float)(bounds.X + bounds.Width), 0)
            };
            resources.Add(brush);
            layout.SetBrush(syllable.CharStart, syllable.CharLength, brush);
        }
    }

    private static Rect UnionRegions(CanvasTextLayoutRegion[] regions)
    {
        if (regions.Length == 0)
        {
            return new Rect(0, 0, 0, 0);
        }

        var left = double.MaxValue;
        var top = double.MaxValue;
        var right = double.MinValue;
        var bottom = double.MinValue;
        foreach (var region in regions)
        {
            var r = region.LayoutBounds;
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.X + r.Width);
            bottom = Math.Max(bottom, r.Y + r.Height);
        }

        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>Redraws every frame only while a word-timed line is on screen and music plays.</summary>
    private void UpdateWordTicker()
    {
        var interlude = UpdateInterludeState();
        var wordLines = physicalLyricItems.Any(item => item.Line is { EndTime: not null } || item.Line is { HasWordTiming: true });
        var needed = physicalTrackModeActive &&
            ((ViewModel.IsPlaybackRunning && wordLines) || interlude);

        if (needed == isWordTickerActive)
        {
            return;
        }

        isWordTickerActive = needed;
        if (needed)
        {
            CompositionTarget.Rendering += OnWordFrame;
        }
        else
        {
            CompositionTarget.Rendering -= OnWordFrame;
            InvalidateLyricsText();
        }
    }

    private void StopWordTicker()
    {
        if (isWordTickerActive)
        {
            isWordTickerActive = false;
            CompositionTarget.Rendering -= OnWordFrame;
        }
    }

    private void OnWordFrame(object? sender, object e)
    {
        UpdateOverlapActivity();
        _ = UpdateInterludeState();
        InvalidateLyricsText();
    }

    // ------------------------------------------------------------------ contrast

    /// <summary>
    /// White-or-black fill aligned to the overlay: per pixel, white where the backdrop is dark and
    /// black where it is light (with a soft crossover), taking the glass tint into account.
    /// </summary>
    private ICanvasImage? CreateInvertFill(CanvasControl sender, List<IDisposable> resources)
    {
        var wallpaper = DesktopWallpaperService.Instance.Bitmap;
        if (wallpaper is null || wallpaper.Device != sender.Device || lyricsTextOverlay is null)
        {
            return null;
        }

        var overlayRoot = lyricsTextOverlay.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0));
        var overlayScreen = RootToScreen(new Rect(overlayRoot.X, overlayRoot.Y, 1, 1));
        var origin = DesktopWallpaperService.Instance.Origin;
        var scale = (float)Math.Max(0.25, rasterScale);

        var aligned = new Transform2DEffect
        {
            Source = wallpaper,
            TransformMatrix =
                Matrix3x2.CreateTranslation(origin.X - overlayScreen.X, origin.Y - overlayScreen.Y) *
                Matrix3x2.CreateScale(1f / scale),
            BorderMode = EffectBorderMode.Hard
        };
        var blurred = new GaussianBlurEffect { Source = aligned, BlurAmount = 5f, BorderMode = EffectBorderMode.Hard };

        // Luminance, then blended with the glass tint that sits between wallpaper and text.
        GetGlassTintOverlay(out var tintAlpha, out var tintLuminance);
        var keep = 1f - tintAlpha;
        var add = tintLuminance * tintAlpha;
        var luminance = new ColorMatrixEffect
        {
            Source = blurred,
            ColorMatrix = new Matrix5x4
            {
                M11 = 0.2126f * keep, M12 = 0.2126f * keep, M13 = 0.2126f * keep,
                M21 = 0.7152f * keep, M22 = 0.7152f * keep, M23 = 0.7152f * keep,
                M31 = 0.0722f * keep, M32 = 0.0722f * keep, M33 = 0.0722f * keep,
                M44 = 1,
                M51 = add, M52 = add, M53 = add
            }
        };

        // Dark backdrop → white text, light backdrop → black text, soft crossover around 0.55.
        float[] table = [1f, 1f, 1f, 1f, 1f, 0.92f, 0.08f, 0f, 0f, 0f, 0f];
        var contrast = new TableTransferEffect
        {
            Source = luminance,
            RedTable = table,
            GreenTable = table,
            BlueTable = table,
            AlphaTable = [1f, 1f],
            ClampOutput = true
        };

        resources.Add(aligned);
        resources.Add(blurred);
        resources.Add(luminance);
        resources.Add(contrast);
        return contrast;
    }

    /// <summary>The glass tint currently between the wallpaper and the text (0 when hidden).</summary>
    private void GetGlassTintOverlay(out float alpha, out float luminance)
    {
        var preset = ViewModel.GlassTint;
        var color = ResolveTintColor(preset);
        var strength = (float)Math.Clamp(ViewModel.GlassTintStrength, 0, 0.9);
        var opacity = preset == GlassTintPreset.Clear ? strength * 0.35f : strength;
        alpha = opacity * (float)Math.Clamp(platePresence.Value, 0, 1);
        luminance = ((0.2126f * color.R) + (0.7152f * color.G) + (0.0722f * color.B)) / 255f;
    }

    /// <summary>Samples the wallpaper under the widget and tells the view model if it is light.</summary>
    private void UpdateBackdropContrast()
    {
        if (contrastSampleClock.Elapsed < ContrastSampleInterval)
        {
            return;
        }

        contrastSampleClock.Restart();
        if (ViewModel.TextContrast is TextContrastMode.Light or TextContrastMode.Dark)
        {
            return;
        }

        var plate = OverlayRectToRoot(ComputePlateTargetRect());
        var scale = Math.Max(0.25, rasterScale);
        var topLeft = RootToScreen(plate);
        var screenRect = new Rect(topLeft.X, topLeft.Y, plate.Width * scale, plate.Height * scale);
        var sampled = DesktopWallpaperService.Instance.SampleLuminanceStats(screenRect);
        if (sampled is not { } stats)
        {
            return;
        }

        // The glass tint is taken into account only when the glass is always visible: a plate
        // that appears on hover must never flip the text colour back and forth.
        GetGlassTintOverlay(out var tintAlpha, out var tintLuminance);
        if (ViewModel.GlassVisibility != GlassVisibilityMode.Always)
        {
            tintAlpha = 0;
        }

        var keep = 1 - tintAlpha;
        var mean = (stats.Mean * keep) + (tintLuminance * tintAlpha);
        var lightShare = stats.LightFraction * keep;
        var darkShare = stats.DarkFraction * keep;

        // Mostly light: dark text. Mostly dark: light text. Mixed: keep the current choice
        // (hysteresis) and turn on a strong contrasting halo so every part stays readable.
        var isLight = ViewModel.IsBackdropLight;
        if (lightShare >= 0.6 || mean > 0.66)
        {
            isLight = true;
        }
        else if (darkShare >= 0.6 || mean < 0.36)
        {
            isLight = false;
        }

        var mixed = Math.Min(lightShare, darkShare) >= 0.2 || (mean > 0.42 && mean < 0.6);
        var halo = mixed ? 1.0 : 0.35;
        if (ViewModel.TextContrast == TextContrastMode.Invert)
        {
            halo = Math.Max(halo, 0.6);
        }

        if (isLight != ViewModel.IsBackdropLight)
        {
            ViewModel.IsBackdropLight = isLight;
        }

        if (Math.Abs(halo - contrastHalo) > 0.01)
        {
            contrastHalo = halo;
            InvalidateLyricsText();
        }

        UpdateTitleHalos();
    }

    // ------------------------------------------------------------------ title halo

    private double contrastHalo = 0.35;
    private Microsoft.UI.Composition.SpriteVisual? titleHaloVisual;
    private Microsoft.UI.Composition.SpriteVisual? artistHaloVisual;

    /// <summary>
    /// Text-masked composition drop shadows behind the (XAML) title and artist, in the colour
    /// opposite to the text. Strength follows contrastHalo: subtle on uniform wallpapers, dense
    /// on busy or mixed ones.
    /// </summary>
    private void UpdateTitleHalos()
    {
        try
        {
            titleHaloVisual ??= CreateHalo(TitleHaloHost, TrackTitleText);
            artistHaloVisual ??= CreateHalo(ArtistHaloHost, ArtistNameText);
            var haloColor = ViewModel.UseDarkText
                ? Color.FromArgb(255, 255, 255, 255)
                : Color.FromArgb(255, 0, 0, 0);
            ApplyHalo(titleHaloVisual, haloColor);
            ApplyHalo(artistHaloVisual, haloColor);
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Contrast: halo update failed {ex.Message}");
        }
    }

    private static Microsoft.UI.Composition.SpriteVisual CreateHalo(UIElement host, TextBlock text)
    {
        var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(host).Compositor;
        var sprite = compositor.CreateSpriteVisual();
        sprite.Brush = compositor.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
        var shadow = compositor.CreateDropShadow();
        shadow.Mask = text.GetAlphaMask();
        shadow.Offset = new Vector3(0, 1, 0);
        sprite.Shadow = shadow;
        // Always exactly the size of the host cell (= the text block). A fixed size went stale
        // when the title changed and the live alpha mask was stretched into a shifted ghost.
        sprite.RelativeSizeAdjustment = Vector2.One;
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(host, sprite);
        return sprite;
    }

    private void ApplyHalo(Microsoft.UI.Composition.SpriteVisual sprite, Color color)
    {
        if (sprite.Shadow is Microsoft.UI.Composition.DropShadow shadow)
        {
            shadow.Color = color;
            shadow.BlurRadius = (float)(6 + (10 * contrastHalo));
            shadow.Opacity = (float)Math.Clamp(0.35 + (0.6 * contrastHalo), 0, 0.95);
        }
    }
}
