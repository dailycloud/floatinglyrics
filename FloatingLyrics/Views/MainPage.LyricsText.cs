using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace FloatingLyrics.Views;

/// <summary>
/// Sub-pixel text rendering for the synced lyrics track.
///
/// XAML TextBlocks snap glyphs to the pixel grid every time their transform changes, so slow
/// spring motion visibly steps from pixel to pixel. Here the lines are drawn with Direct2D
/// (Win2D) using <see cref="CanvasDrawTextOptions.NoPixelSnap"/> and grayscale antialiasing,
/// at the exact fractional position/scale produced by the springs. The XAML TextBlocks stay in
/// the tree (invisible) as the single source of truth for layout, measurement and alignment.
///
/// Each line also gets a soft drop shadow and a slight blur while inactive (Apple Music-style
/// focus on the current line), both driven by the same activeness spring as scale and colour.
/// </summary>
public sealed partial class MainPage
{
    private const float InactiveLineBlur = 0.9f;
    private const float LineShadowBlur = 2.4f;
    private CanvasControl? lyricsTextOverlay;

    private void InitializeLyricsTextOverlay()
    {
        if (lyricsTextOverlay is not null)
        {
            return;
        }

        lyricsTextOverlay = new CanvasControl
        {
            UseSharedDevice = true,
            ClearColor = Color.FromArgb(0, 0, 0, 0),
            IsHitTestVisible = false
        };
        lyricsTextOverlay.Draw += OnLyricsTextOverlayDraw;
        lyricsTextOverlay.CreateResources += OnLyricsTextOverlayCreateResources;

        // Directly above the (now text-less) XAML track surface, inside the clipped viewport.
        var index = LyricsCanvas.Children.IndexOf(LyricsTrackSurface);
        LyricsCanvas.Children.Insert(index + 1, lyricsTextOverlay);
        UpdateLyricsTextOverlayBounds();
    }

    private void OnLyricsTextOverlayCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        if (args.Reason != CanvasCreateResourcesReason.NewDevice)
        {
            return;
        }

        foreach (var item in physicalLyricItems)
        {
            item.TextLayout?.Dispose();
            item.TextLayout = null;
            item.TextLayoutKey = null;
            item.BackgroundLayout?.Dispose();
            item.BackgroundLayout = null;
            item.BackgroundLayoutKey = null;
        }
    }

    private void TeardownLyricsTextOverlay()
    {
        if (lyricsTextOverlay is null)
        {
            return;
        }

        lyricsTextOverlay.Draw -= OnLyricsTextOverlayDraw;
        lyricsTextOverlay.CreateResources -= OnLyricsTextOverlayCreateResources;
        LyricsCanvas.Children.Remove(lyricsTextOverlay);
        lyricsTextOverlay.RemoveFromVisualTree();
        lyricsTextOverlay = null;
    }

    /// <summary>Keeps the overlay covering the whole (padded) track viewport.</summary>
    private void UpdateLyricsTextOverlayBounds()
    {
        if (lyricsTextOverlay is null)
        {
            return;
        }

        var band = Math.Max(24, GetLineBandHeight());
        var viewportHeight = LyricsCanvas.Height > 0 ? LyricsCanvas.Height : LyricsCanvas.ActualHeight;
        // Only the visible lane (plus room for the scaled-up active line) needs pixels.
        var laneWidth = LyricsCanvas.Width > 0 && double.IsFinite(LyricsCanvas.Width) ? LyricsCanvas.Width : LyricsCanvas.ActualWidth;
        var width = Math.Clamp(laneWidth + (PhysicalTrackClipHorizontalPadding * 2), 1, 6000);
        var height = Math.Max(1, viewportHeight + (band * 2));

        Canvas.SetLeft(lyricsTextOverlay, -PhysicalTrackClipHorizontalPadding);
        Canvas.SetTop(lyricsTextOverlay, -band);
        if (!double.IsFinite(lyricsTextOverlay.Width) || !double.IsFinite(lyricsTextOverlay.Height) ||
            Math.Abs(lyricsTextOverlay.Width - width) > 0.5 || Math.Abs(lyricsTextOverlay.Height - height) > 0.5)
        {
            lyricsTextOverlay.Width = width;
            lyricsTextOverlay.Height = height;
        }

        lyricsTextOverlay.Visibility = physicalTrackModeActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InvalidateLyricsText()
    {
        lyricsTextOverlay?.Invalidate();
    }

    private void OnLyricsTextOverlayDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!physicalTrackModeActive || physicalLyricItems.Count == 0)
        {
            return;
        }

        var ds = args.DrawingSession;
        ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;

        // Soft edges: instead of the viewport cutting glyphs in half, lines melt away over a
        // feathered band just outside the top and bottom of the lyrics viewport.
        using var edgeMask = CreateEdgeFadeMask(sender);
        using var masked = ds.CreateLayer(edgeMask);

        var frameResources = new List<IDisposable>();
        var invertFill = ViewModel.TextContrast == TextContrastMode.Invert
            ? CreateInvertFill(sender, frameResources)
            : null;
        try
        {
            DrawAllLines(sender, ds, invertFill);
            DrawInterlude(sender, ds);
        }
        finally
        {
            foreach (var resource in frameResources)
            {
                resource.Dispose();
            }
        }
    }

    private void DrawAllLines(CanvasControl sender, CanvasDrawingSession ds, ICanvasImage? invertFill)
    {
        foreach (var item in physicalLyricItems)
        {
            if (item.IsVisualizer || string.IsNullOrEmpty(item.MainText.Text))
            {
                continue;
            }

            try
            {
                DrawLyricLine(sender, ds, item, invertFill);
            }
            catch (Exception ex) when (sender.Device.IsDeviceLost(ex.HResult))
            {
                sender.Device.RaiseDeviceLost();
                return;
            }
            catch (Exception ex)
            {
                DebugTrace.Write($"LyricsText: draw failed {ex.Message}");
            }
        }
    }

    private CanvasLinearGradientBrush CreateEdgeFadeMask(CanvasControl sender)
    {
        var height = (float)Math.Max(1, sender.ActualHeight);
        var band = (float)Math.Max(24, GetLineBandHeight());
        var viewportHeight = (float)Math.Max(1, LyricsCanvas.Height > 0 ? LyricsCanvas.Height : LyricsCanvas.ActualHeight);
        var feather = Math.Clamp(band * 0.7f, 14f, 40f);

        // Overlay coordinates: the viewport starts at y = band (the overlay sits band above it).
        var top = band;
        var bottom = band + viewportHeight;
        static float N(float y, float h) => Math.Clamp(y / h, 0f, 1f);
        var stops = new[]
        {
            new CanvasGradientStop { Position = N(top - feather, height), Color = Color.FromArgb(0, 255, 255, 255) },
            new CanvasGradientStop { Position = N(top + (feather * 0.35f), height), Color = Color.FromArgb(255, 255, 255, 255) },
            new CanvasGradientStop { Position = N(bottom - (feather * 0.35f), height), Color = Color.FromArgb(255, 255, 255, 255) },
            new CanvasGradientStop { Position = N(bottom + feather, height), Color = Color.FromArgb(0, 255, 255, 255) }
        };

        return new CanvasLinearGradientBrush(sender, stops)
        {
            StartPoint = new Vector2(0, 0),
            EndPoint = new Vector2(0, height)
        };
    }

    private void DrawLyricLine(CanvasControl sender, CanvasDrawingSession ds, PhysicalLyricItem item, ICanvasImage? invertFill)
    {
        var opacity = (float)(item.ContentHost.Opacity * item.Layer.Opacity) * GetInterludeLineFade(item);
        if (opacity < 0.004f)
        {
            return;
        }

        var layout = EnsureTextLayout(sender, item);
        if (layout is null || lyricsTextOverlay is null)
        {
            return;
        }

        // Exact (fractional) position of the line, including the spring offset and scale.
        // Unscaled position of the text box (spring offset included), then scale around the
        // line's own alignment anchor so centred/right/duet lines grow in place.
        var hostOrigin = item.ContentHost.TransformToVisual(lyricsTextOverlay).TransformPoint(new Point(0, 0));
        var margin = item.MainText.Margin;
        var basePosition = new Vector2((float)(hostOrigin.X + margin.Left), (float)(hostOrigin.Y + margin.Top));
        var scale = (float)item.Scale.ScaleX;
        var textBounds = layout.LayoutBounds;
        var anchorX = item.MainText.TextAlignment switch
        {
            TextAlignment.Center => textBounds.X + (textBounds.Width / 2),
            TextAlignment.Right => textBounds.X + textBounds.Width,
            _ => textBounds.X
        };
        var transform =
            Matrix3x2.CreateScale(scale, new Vector2((float)anchorX, (float)textBounds.Y)) *
            Matrix3x2.CreateTranslation(basePosition);
        var exitScale = item.ExitScale * GetInterludeLineShrink(item);
        if (Math.Abs(exitScale - 1) > 0.0005)
        {
            // The outgoing line shrinks around the centre of its own text.
            var bounds = layout.LayoutBounds;
            var center = Vector2.Transform(
                new Vector2((float)(bounds.X + (bounds.Width / 2)), (float)(bounds.Y + (bounds.Height / 2))),
                transform);
            transform *= Matrix3x2.CreateScale((float)exitScale, center);
        }

        var baseColor = item.MainBrush.Color;
        baseColor.A = 255;
        var lineResources = new List<IDisposable>();
        try
        {
            ApplyWordStyling(sender, layout, item, baseColor, lineResources);

            // Background vocals: a smaller, quieter line right under the lead, with its own wipe.
            var backgroundLayout = EnsureBackgroundLayout(sender, item);
            var backgroundColor = baseColor;
            backgroundColor.A = (byte)Math.Round(255 * BackgroundVocalAlpha);
            if (backgroundLayout is not null)
            {
                ApplySyllableStyling(sender, backgroundLayout, item.Line!.BackgroundText!, item.Line.BackgroundSyllables, item, backgroundColor, lineResources);
            }

            using var glyphs = new CanvasCommandList(sender);
            using (var cds = glyphs.CreateDrawingSession())
            {
                cds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
                cds.Transform = transform;
                cds.DrawTextLayout(layout, 0, 0, baseColor);
                if (backgroundLayout is not null)
                {
                    var leadHeight = (float)ComputeLineHeight(Math.Max(1, item.MainText.FontSize));
                    cds.DrawTextLayout(backgroundLayout, 0, leadHeight * 0.96f, backgroundColor);
                }
            }

            ComposeLine(ds, glyphs, item, opacity, scale, invertFill);
        }
        finally
        {
            foreach (var resource in lineResources)
            {
                resource.Dispose();
            }
        }
    }

    private void ComposeLine(CanvasDrawingSession ds, CanvasCommandList glyphs, PhysicalLyricItem item, float opacity, float scale, ICanvasImage? invertFill)
    {
        var active = Math.Clamp(item.Active.Value, 0, 1);
        var blur = (float)((1 - active) * InactiveLineBlur);
        var shadowColor = item.ShadowBrush.Color;

        using var layer = ds.CreateLayer(Math.Min(1f, opacity));
        if (invertFill is not null)
        {
            // Per-pixel contrast: the glyph coverage (and word dimming) masks a fill that is
            // white over dark wallpaper and black over light wallpaper.
            using var inverted = new AlphaMaskEffect { Source = invertFill, AlphaMask = glyphs };
            if (blur > 0.12f)
            {
                using var softInverted = new GaussianBlurEffect { Source = inverted, BlurAmount = blur, Optimization = EffectOptimization.Speed };
                ds.DrawImage(softInverted);
            }
            else
            {
                ds.DrawImage(inverted);
            }

            return;
        }

        // Busy or mixed backdrops get a denser halo (see UpdateBackdropContrast).
        var haloBoost = (float)Math.Clamp(contrastHalo, 0, 1);
        if (haloBoost > 0.5f)
        {
            shadowColor.A = (byte)Math.Max(shadowColor.A, (byte)215);
        }

        if (shadowColor.A > 2)
        {
            using var shadow = new ShadowEffect
            {
                Source = glyphs,
                BlurAmount = LineShadowBlur + blur + (haloBoost * 3.5f),
                ShadowColor = shadowColor,
                Optimization = EffectOptimization.Speed
            };
            ds.DrawImage(shadow, 0, 1.5f * scale);
        }

        if (blur > 0.12f)
        {
            using var soft = new GaussianBlurEffect
            {
                Source = glyphs,
                BlurAmount = blur,
                BorderMode = EffectBorderMode.Soft,
                Optimization = EffectOptimization.Speed
            };
            ds.DrawImage(soft);
        }
        else
        {
            ds.DrawImage(glyphs);
        }
    }

    /// <summary>
    /// Width of the box the line is aligned in. Centre mode gives the text block a fixed width;
    /// left/right modes size it to the text and position it with its margin, so the box must be
    /// the text's own width (a huge fallback box pushed right-aligned text off screen).
    /// </summary>
    private static float GetTextBoxWidth(PhysicalLyricItem item)
    {
        if (double.IsFinite(item.MainText.Width) && item.MainText.Width > 0)
        {
            return (float)item.MainText.Width;
        }

        var measured = Math.Max(item.MainText.ActualWidth, item.MainText.DesiredSize.Width);
        return (float)Math.Max(1, double.IsFinite(measured) ? Math.Ceiling(measured) + 1 : 1);
    }

    /// <summary>Layout for the background-vocal line: same face/alignment, smaller size.</summary>
    private CanvasTextLayout? EnsureBackgroundLayout(ICanvasResourceCreator creator, PhysicalLyricItem item)
    {
        if (item.Line is not { HasBackgroundVocals: true } line ||
            !string.Equals(line.Text, item.MainText.Text, StringComparison.Ordinal))
        {
            return null;
        }

        var text = line.BackgroundText!;
        var fontSize = (float)Math.Max(1, item.MainText.FontSize * BackgroundLineScale);
        var boxWidth = GetTextBoxWidth(item);
        var alignment = item.MainText.TextAlignment;
        var key = $"{text}\u001F{fontSize}\u001F{currentLineFontFamily.Source}\u001F{lyricsFontWeight.Weight}\u001F{boxWidth}\u001F{alignment}";
        if (item.BackgroundLayout is not null && string.Equals(item.BackgroundLayoutKey, key, StringComparison.Ordinal))
        {
            return item.BackgroundLayout;
        }

        item.BackgroundLayout?.Dispose();
        var lineHeight = (float)ComputeLineHeight(fontSize);
        using var format = new CanvasTextFormat
        {
            FontFamily = currentLineFontFamily.Source,
            FontSize = fontSize,
            FontWeight = lyricsFontWeight,
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = alignment switch
            {
                TextAlignment.Center => CanvasHorizontalAlignment.Center,
                TextAlignment.Right => CanvasHorizontalAlignment.Right,
                _ => CanvasHorizontalAlignment.Left
            },
            LineSpacingMode = CanvasLineSpacingMode.Uniform,
            LineSpacing = lineHeight,
            LineSpacingBaseline = lineHeight * 0.8f,
            Options = CanvasDrawTextOptions.NoPixelSnap | CanvasDrawTextOptions.EnableColorFont
        };

        item.BackgroundLayout = new CanvasTextLayout(creator, text, format, boxWidth, lineHeight * 1.5f)
        {
            Options = CanvasDrawTextOptions.NoPixelSnap | CanvasDrawTextOptions.EnableColorFont
        };
        item.BackgroundLayoutKey = key;
        return item.BackgroundLayout;
    }

    private CanvasTextLayout? EnsureTextLayout(ICanvasResourceCreator creator, PhysicalLyricItem item)
    {
        var text = item.MainText.Text ?? string.Empty;
        var fontSize = (float)Math.Max(1, item.MainText.FontSize);
        var family = currentLineFontFamily.Source;
        var weight = lyricsFontWeight.Weight;
        var boxWidth = GetTextBoxWidth(item);
        var alignment = item.MainText.TextAlignment;
        var key = $"{text}\u001F{fontSize}\u001F{family}\u001F{weight}\u001F{boxWidth}\u001F{alignment}";
        if (item.TextLayout is not null && string.Equals(item.TextLayoutKey, key, StringComparison.Ordinal))
        {
            return item.TextLayout;
        }

        item.TextLayout?.Dispose();
        var lineHeight = (float)ComputeLineHeight(fontSize);
        using var format = new CanvasTextFormat
        {
            FontFamily = family,
            FontSize = fontSize,
            FontWeight = lyricsFontWeight,
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = alignment switch
            {
                TextAlignment.Center => CanvasHorizontalAlignment.Center,
                TextAlignment.Right => CanvasHorizontalAlignment.Right,
                _ => CanvasHorizontalAlignment.Left
            },
            LineSpacingMode = CanvasLineSpacingMode.Uniform,
            LineSpacing = lineHeight,
            LineSpacingBaseline = lineHeight * 0.8f,
            Options = CanvasDrawTextOptions.NoPixelSnap | CanvasDrawTextOptions.EnableColorFont
        };

        item.TextLayout = new CanvasTextLayout(creator, text, format, boxWidth, lineHeight * 1.5f)
        {
            Options = CanvasDrawTextOptions.NoPixelSnap | CanvasDrawTextOptions.EnableColorFont
        };
        item.TextLayoutKey = key;
        return item.TextLayout;
    }
}
