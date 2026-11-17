using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.UI;

namespace FloatingLyrics.Controls;

/// <summary>
/// Instrumental-mode visualizer rendered with Win2D: a smooth, mirrored "liquid" spectrum
/// (Catmull-Rom curve through the frequency bins) filled with the artwork's accent gradient,
/// with a soft bloom behind it, a bright rim along the crest, floating peak sparks, and a slow
/// breathing wave so it never looks dead during quiet passages.
/// </summary>
public sealed partial class SpectrumVisualizer : Grid
{
    private readonly CanvasControl canvas;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private float[] levels = [];
    private float[] peaks = [];
    private float[] ratios = [];
    private Color low = Color.FromArgb(255, 255, 84, 84);
    private Color mid = Color.FromArgb(255, 121, 183, 244);
    private Color high = Color.FromArgb(255, 74, 255, 142);

    public SpectrumVisualizer()
    {
        IsHitTestVisible = false;
        canvas = new CanvasControl
        {
            UseSharedDevice = true,
            ClearColor = Color.FromArgb(0, 0, 0, 0),
            IsHitTestVisible = false
        };
        canvas.Draw += OnDraw;
        Children.Add(canvas);
    }

    /// <summary>True on light wallpapers: adds a dark shadow and rim so light colours stay visible.</summary>
    public bool OnLightBackdrop { get; set; }

    /// <summary>0..1 overall intensity (used to fade the whole visualizer in and out).</summary>
    public float Intensity { get; set; } = 1f;

    public void SetFrame(float[] binLevels, float[] binPeaks, float[] binRatios, Color lowAccent, Color midAccent, Color highAccent, bool mirror)
    {
        levels = binLevels;
        peaks = binPeaks;
        ratios = binRatios;
        low = lowAccent;
        mid = midAccent;
        high = highAccent;
        _ = mirror;
        canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try
        {
            Draw(sender, args.DrawingSession);
        }
        catch (Exception ex) when (sender.Device.IsDeviceLost(ex.HResult))
        {
            sender.Device.RaiseDeviceLost();
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Spectrum: draw failed {ex.Message}");
        }
    }

    private void Draw(CanvasControl sender, CanvasDrawingSession ds)
    {
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        var count = Math.Min(levels.Length, Math.Min(peaks.Length, ratios.Length));
        if (width < 16 || height < 8 || count < 4)
        {
            return;
        }


        // Fewer, smoother control points read as "liquid"; resample the bins down.
        // Dense enough for the round end caps to stay smooth.
        var pointCount = Math.Clamp((int)(width / 6f), 24, 140);
        var t = (float)clock.Elapsed.TotalSeconds;
        var centerY = height * 0.5f;
        var maxHalf = Math.Max(4f, (height * 0.5f) - 14f); // headroom for the bloom
        // Room for the bloom/shadow on both sides so they are never clipped by the canvas edge.
        var inset = Math.Min(34f, width * 0.14f);
        var usable = width - (inset * 2f);

        var heights = new float[pointCount];
        var peakHeights = new float[pointCount];

        // Contrast shaping: each point relative to the loudest one in this frame (raised to a
        // power so peaks stand out), scaled by overall loudness. Without it a dense mix fills
        // every bin evenly and the spectrum collapses into a flat capsule.
        var frameMax = 0.0001f;
        for (var i = 0; i < count; i++)
        {
            frameMax = Math.Max(frameMax, float.IsFinite(levels[i]) ? levels[i] : 0f);
        }

        var loudness = MathF.Pow(Math.Clamp(frameMax, 0f, 1f), 0.55f);
        for (var i = 0; i < pointCount; i++)
        {
            var u = i / (float)(pointCount - 1);
            var level = MathF.Pow(Math.Clamp(SampleBins(levels, u, count) / frameMax, 0f, 1f), 2.2f) * loudness;
            var peak = MathF.Pow(Math.Clamp(SampleBins(peaks, u, count) / frameMax, 0f, 1f), 2.2f) * loudness;

            // Gentle breathing wave keeps quiet passages alive.
            var breath = 0.06f + (0.035f * MathF.Sin((t * 1.7f) + (u * 9f))) + (0.02f * MathF.Sin((t * 3.1f) - (u * 15f)));
            var curved = MathF.Pow(Math.Clamp(level, 0f, 1f), 0.8f);
            // No taper toward the ends: the low end (left) must be free to swell with the bass.
            // The ends are closed by real semicircles sized by the level there (see the shape).
            heights[i] = Math.Max(2.5f, ((curved * 0.94f) + breath) * maxHalf * Intensity);
            peakHeights[i] = Math.Max(heights[i], MathF.Pow(Math.Clamp(peak, 0f, 1f), 0.8f) * maxHalf * Intensity);
        }

        // Light smoothing along the crest (the curve is dense now): liquid, not jagged.
        heights = SmoothCrest(heights);
        using var shape = BuildMirroredShape(sender, heights, inset, usable, centerY);
        using var fill = BuildGradient(sender, inset, usable, count);

        // Bloom behind the shape.
        using (var glowSource = new CanvasCommandList(sender))
        {
            using (var gds = glowSource.CreateDrawingSession())
            {
                gds.FillGeometry(shape, fill);
            }

            using var bloom = new GaussianBlurEffect { Source = glowSource, BlurAmount = 12f, BorderMode = EffectBorderMode.Soft };
            using (ds.CreateLayer(0.55f * Intensity))
            {
                ds.DrawImage(bloom);
            }
        }

        // Soft shadow under the shape keeps light accents readable on bright or busy wallpapers.
        {
            using var silhouette = new CanvasCommandList(sender);
            using (var sds = silhouette.CreateDrawingSession())
            {
                sds.FillGeometry(shape, Microsoft.UI.Colors.Black);
            }

            using var shadow = new ShadowEffect { Source = silhouette, BlurAmount = 5f, ShadowColor = Color.FromArgb((byte)((OnLightBackdrop ? 120 : 80) * Intensity), 0, 0, 0) };
            ds.DrawImage(shadow, 0, 1.5f);
        }

        using (ds.CreateLayer(0.92f * Intensity))
        {
            ds.FillGeometry(shape, fill);
        }

        // Vertical sheen inside the shape: brighter crest, softer centre line.
        using (var sheen = new CanvasLinearGradientBrush(
                   sender,
                   [
                       new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(120, 255, 255, 255) },
                       new CanvasGradientStop { Position = 0.42f, Color = Color.FromArgb(0, 255, 255, 255) },
                       new CanvasGradientStop { Position = 0.58f, Color = Color.FromArgb(0, 255, 255, 255) },
                       new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(90, 255, 255, 255) }
                   ])
               {
                   StartPoint = new Vector2(0, centerY - maxHalf),
                   EndPoint = new Vector2(0, centerY + maxHalf)
               })
        using (ds.CreateLayer(Intensity, shape))
        {
            ds.FillGeometry(shape, sheen);
        }

        // Crisp rim along the crest.
        var rimTone = OnLightBackdrop ? Color.FromArgb((byte)(120 * Intensity), 20, 22, 28) : Color.FromArgb((byte)(150 * Intensity), 255, 255, 255);
        ds.DrawGeometry(shape, rimTone, 1.1f);

        // Thin centre line, like a waveform baseline.
        var lineTone = OnLightBackdrop ? Color.FromArgb((byte)(70 * Intensity), 20, 22, 28) : Color.FromArgb((byte)(60 * Intensity), 255, 255, 255);
        ds.DrawLine(inset, centerY, inset + usable, centerY, lineTone, 1f);

        // Peak sparks hovering above the crest.
        for (var i = 1; i < pointCount - 1; i += 2)
        {
            var gap = peakHeights[i] - heights[i];
            if (gap < 2.5f)
            {
                continue;
            }

            var x = inset + (usable * i / (pointCount - 1));
            var alpha = (byte)Math.Clamp(Math.Min(1f, gap / 10f) * 200f * Intensity, 0, 255);
            ds.FillCircle(x, centerY - peakHeights[i], 1.6f, Color.FromArgb(alpha, 255, 255, 255));
            ds.FillCircle(x, centerY + peakHeights[i], 1.6f, Color.FromArgb((byte)(alpha / 2), 255, 255, 255));
        }
    }

    private static float[] SmoothCrest(float[] values)
    {
        var result = new float[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (i == 0 || i == values.Length - 1)
            {
                result[i] = values[i];
                continue;
            }

            var left = values[Math.Max(0, i - 2)];
            var right = values[Math.Min(values.Length - 1, i + 2)];
            result[i] = (values[i - 1] * 0.22f) + (values[i] * 0.36f) + (values[i + 1] * 0.22f) + ((left + right) * 0.1f);
        }

        return result;
    }

    private static float SampleBins(float[] data, float u, int count)
    {
        var position = u * (count - 1);
        var index = (int)position;
        var frac = position - index;
        var a = data[Math.Clamp(index, 0, count - 1)];
        var b = data[Math.Clamp(index + 1, 0, count - 1)];
        // Average a small neighbourhood so the curve is not jittery.
        var left = data[Math.Clamp(index - 1, 0, count - 1)];
        var right = data[Math.Clamp(index + 2, 0, count - 1)];
        var value = ((a + ((b - a) * frac)) * 0.7f) + (((left + right) * 0.5f) * 0.3f);
        return float.IsFinite(value) ? value : 0f;
    }

    private static CanvasGeometry BuildMirroredShape(ICanvasResourceCreator creator, float[] heights, float inset, float usable, float centerY)
    {
        var n = heights.Length;
        var top = new Vector2[n];
        for (var i = 0; i < n; i++)
        {
            top[i] = new Vector2(inset + (usable * i / (n - 1)), centerY - heights[i]);
        }

        var bottom = new Vector2[n];
        for (var i = 0; i < n; i++)
        {
            var source = top[n - 1 - i];
            bottom[i] = new Vector2(source.X, centerY + (centerY - source.Y));
        }

        // Each end is closed by a round cap whose height is the spectrum level at that end, so
        // it is always round (never a point or a flat cut) and swells with the bass. The cap
        // width is limited by the inset (room for the bloom); a tall cap becomes an ellipse.
        var maxCapWidth = Math.Max(2f, inset - 8f);
        var leftRadius = heights[0];
        var rightRadius = heights[n - 1];

        // The geometry takes ownership of the builder: it must not be disposed here.
        var builder = new CanvasPathBuilder(creator);
        builder.BeginFigure(top[0]);
        AddCatmullRom(builder, top);
        builder.AddArc(bottom[0], Math.Min(rightRadius, maxCapWidth), rightRadius, 0f, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        AddCatmullRom(builder, bottom);
        builder.AddArc(top[0], Math.Min(leftRadius, maxCapWidth), leftRadius, 0f, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }

    private static void AddCatmullRom(CanvasPathBuilder builder, Vector2[] points)
    {
        for (var i = 0; i < points.Length - 1; i++)
        {
            var p0 = points[Math.Max(0, i - 1)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(points.Length - 1, i + 2)];
            var c1 = p1 + ((p2 - p0) / 6f);
            var c2 = p2 - ((p3 - p1) / 6f);

            // Horizontal tangents at both ends so the crest flows into the round caps seamlessly.
            if (i == 0)
            {
                c1 = new Vector2(p1.X + ((p2.X - p1.X) / 3f), p1.Y);
            }

            if (i == points.Length - 2)
            {
                c2 = new Vector2(p2.X - ((p2.X - p1.X) / 3f), p2.Y);
            }

            builder.AddCubicBezier(c1, c2, p2);
        }
    }

    private CanvasLinearGradientBrush BuildGradient(ICanvasResourceCreator creator, float inset, float usable, int count)
    {
        const int stops = 9;
        var gradient = new CanvasGradientStop[stops];
        for (var i = 0; i < stops; i++)
        {
            var u = i / (float)(stops - 1);
            var ratio = ratios[Math.Clamp((int)(u * (count - 1)), 0, count - 1)];
            gradient[i] = new CanvasGradientStop { Position = u, Color = AccentFor(ratio) };
        }

        return new CanvasLinearGradientBrush(creator, gradient)
        {
            StartPoint = new Vector2(inset, 0),
            EndPoint = new Vector2(inset + usable, 0)
        };
    }

    private Color AccentFor(float ratio)
    {
        ratio = Math.Clamp(ratio, 0f, 1f);
        return ratio < 0.5f
            ? Lerp(low, mid, ratio * 2f)
            : Lerp(mid, high, (ratio - 0.5f) * 2f);
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        static byte L(byte x, byte y, float k) => (byte)Math.Clamp(x + ((y - x) * k), 0, 255);
        return Color.FromArgb(255, L(a.R, b.R, t), L(a.G, b.G, t), L(a.B, b.B, t));
    }
}
