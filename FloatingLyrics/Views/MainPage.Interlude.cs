using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace FloatingLyrics.Views;

/// <summary>
/// Interlude indicator for long instrumental gaps between lyric lines: the last sung line fades
/// down and three breathing dots take its place, filling up over the gap, followed by a
/// countdown to the next line. Timing uses line end times (Spicy Lyrics) or a length-based
/// estimate for plain LRC lines.
/// </summary>
public sealed partial class MainPage
{
    private static readonly TimeSpan MinimumInterlude = TimeSpan.FromSeconds(3.5);
    private readonly Stopwatch interludeClock = Stopwatch.StartNew();
    private double interludeAlpha;
    private double interludeProgress;
    private TimeSpan interludeRemaining;
    private bool interludeActive;
    private double lastInterludeFrameSeconds;

    /// <summary>Recomputes whether we are inside a long gap; returns true while drawing is needed.</summary>
    private bool UpdateInterludeState()
    {
        interludeActive = TryComputeInterlude(out interludeProgress, out interludeRemaining);

        var now = interludeClock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - lastInterludeFrameSeconds, 0, 0.1);
        lastInterludeFrameSeconds = now;
        var target = interludeActive ? 1.0 : 0.0;
        interludeAlpha += (target - interludeAlpha) * Math.Min(1, dt * 7);
        if (Math.Abs(interludeAlpha - target) < 0.004)
        {
            interludeAlpha = target;
        }

        return interludeActive || interludeAlpha > 0.004;
    }

    private bool TryComputeInterlude(out double progress, out TimeSpan remaining)
    {
        progress = 0;
        remaining = TimeSpan.Zero;
        if (!physicalTrackModeActive || !ViewModel.HasSyncedTimeline || ViewModel.IsPreRollActive)
        {
            return false;
        }

        var lines = ViewModel.TimelineLines;
        var index = ViewModel.ActiveLineIndex;
        if (index < 0 || index >= lines.Count - 1)
        {
            return false;
        }

        var current = lines[index];
        var next = lines[index + 1];
        if (current.Timestamp is not { } currentStart || next.Timestamp is not { } nextStart)
        {
            return false;
        }

        var currentEnd = current.EndTime
            ?? (current.HasWordTiming ? current.Syllables!.Max(s => s.End) : (TimeSpan?)null)
            ?? currentStart + TimeSpan.FromSeconds(Math.Clamp(current.Text.Length * 0.075, 1.6, 5.0));

        var gapStart = currentEnd + TimeSpan.FromSeconds(0.4);
        var gapEnd = nextStart - TimeSpan.FromSeconds(0.5);
        if (gapEnd - gapStart < MinimumInterlude)
        {
            return false;
        }

        var now = ViewModel.GetInterpolatedPlaybackPosition();
        if (now < gapStart || now > gapEnd)
        {
            return false;
        }

        progress = Math.Clamp((now - gapStart).TotalSeconds / (gapEnd - gapStart).TotalSeconds, 0, 1);
        remaining = nextStart - now;
        return true;
    }

    private PhysicalLyricItem? GetInterludeItem()
    {
        if (interludeAlpha <= 0.004 || physicalLyricItems.Count == 0)
        {
            return null;
        }

        var item = physicalLyricItems[Math.Clamp(GetActivePhysicalPlanIndex(), 0, physicalLyricItems.Count - 1)];
        return item.IsVisualizer ? null : item;
    }

    /// <summary>The last sung line leaves completely while the interlude timer takes its place.</summary>
    private float GetInterludeLineFade(PhysicalLyricItem item) =>
        ReferenceEquals(item, GetInterludeItem()) ? (float)Math.Pow(1 - interludeAlpha, 1.6) : 1f;

    /// <summary>…and shrinks toward its centre, like any outgoing line.</summary>
    private double GetInterludeLineShrink(PhysicalLyricItem item) =>
        ReferenceEquals(item, GetInterludeItem()) ? 1 - (0.16 * interludeAlpha) : 1;

    private void DrawInterlude(CanvasControl sender, CanvasDrawingSession ds)
    {
        var item = GetInterludeItem();
        if (item is null || lyricsTextOverlay is null || item.TextLayout is not { } layout)
        {
            return;
        }

        var hostOrigin = item.ContentHost.TransformToVisual(lyricsTextOverlay).TransformPoint(new Point(0, 0));
        var margin = item.MainText.Margin;
        var basePosition = new Vector2((float)(hostOrigin.X + margin.Left), (float)(hostOrigin.Y + margin.Top));
        var bounds = layout.LayoutBounds;
        var fontSize = (float)Math.Max(12, item.MainText.FontSize * Math.Max(1, item.Scale.ScaleX));

        var radius = fontSize * 0.15f;
        var spacing = radius * 3.3f;
        var dotsWidth = (spacing * 2) + (radius * 2);
        var color = item.MainBrush.Color;
        var alpha = (float)interludeAlpha;
        var t = (float)interludeClock.Elapsed.TotalSeconds;

        var seconds = (int)Math.Ceiling(Math.Max(0, interludeRemaining.TotalSeconds));
        var countdown = seconds >= 60 ? $"{seconds / 60}:{seconds % 60:00}" : $"0:{seconds:00}";
        using var format = new CanvasTextFormat
        {
            FontFamily = currentLineFontFamily.Source,
            FontSize = fontSize * 0.5f,
            FontWeight = lyricsFontWeight,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
        using var timer = new CanvasTextLayout(sender, countdown, format, 1000, fontSize);
        var timerWidth = (float)timer.LayoutBounds.Width;
        var gap = radius * 3f;
        var totalWidth = dotsWidth + gap + timerWidth;

        var left = item.MainText.TextAlignment switch
        {
            TextAlignment.Center => (float)(bounds.X + (bounds.Width / 2)) - (totalWidth / 2),
            TextAlignment.Right => (float)(bounds.X + bounds.Width) - totalWidth,
            _ => (float)bounds.X
        };
        var centerY = (float)(bounds.Y + (bounds.Height / 2));
        var origin = basePosition + new Vector2(left, centerY);

        for (var i = 0; i < 3; i++)
        {
            var fill = (float)Math.Clamp((interludeProgress * 3) - i, 0, 1);
            var breath = 1f + (0.14f * MathF.Sin((t * 3.2f) - (i * 0.9f)));
            var r = radius * (0.82f + (0.18f * fill)) * breath;
            var dotColor = color;
            dotColor.A = (byte)Math.Clamp((0.28f + (0.72f * fill)) * 255 * alpha, 0, 255);
            ds.FillCircle(origin + new Vector2(radius + (i * spacing), 0), r, dotColor);
        }

        var timerColor = color;
        timerColor.A = (byte)Math.Clamp(0.7f * 255 * alpha, 0, 255);
        ds.DrawTextLayout(timer, origin + new Vector2(dotsWidth + gap, -(float)(timer.LayoutBounds.Y + (timer.LayoutBounds.Height / 2))), timerColor);
    }
}
