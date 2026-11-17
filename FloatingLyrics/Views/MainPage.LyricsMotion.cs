using System.Diagnostics;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FloatingLyrics.Views;

/// <summary>
/// Spring-physics motion for the synced lyrics track (the "physical" track).
///
/// Practices applied (as in Apple Music-style lyric views):
/// - every line owns critically/under-damped springs for its position and its "activeness",
///   so a new line arriving mid-animation simply retargets the springs and keeps the current
///   velocity (fully interruptible, no queued storyboards, no jumps);
/// - lines start moving with a small cascade delay depending on their distance from the
///   active line, which gives the characteristic wave instead of a rigid block scroll;
/// - only render transforms, opacity and brush colours change per frame (no layout passes);
/// - scale, opacity and colour are all derived from the same activeness value, so they stay
///   perfectly in sync, and the "Spring" style overshoots slightly for a lively settle.
/// </summary>
public sealed partial class MainPage
{
    private readonly Stopwatch lyricsClock = Stopwatch.StartNew();
    private bool isLyricsLoopActive;
    private double lastLyricsFrameSeconds;

    private (double Stiffness, double Damping, double Stagger) ResolveLineSpring()
    {
        var duration = Math.Clamp(ViewModel.LineAnimationDurationSeconds, 0.12, 1.2);
        var (omegaScale, zeta, stagger) = ViewModel.LineAnimationStyle switch
        {
            LineAnimationStyle.Spring => (1.0, 0.6, 0.05),
            LineAnimationStyle.Snappy => (1.45, 0.88, 0.028),
            _ => (1.0, 1.0, 0.04)
        };

        // ω chosen so a critically damped spring settles within ≈ the configured duration.
        var omega = 5.4 / duration * omegaScale;
        return (omega * omega, 2 * zeta * omega, stagger);
    }

    /// <summary>Retargets every line's springs toward <paramref name="targetIndex"/> with a cascade.</summary>
    private void BeginPhysicalSpringTransition(int targetIndex, int previousIndex)
    {
        if (physicalLyricItems.Count == 0)
        {
            return;
        }

        StopActivePhysicalTrackTransition();
        LyricsTrackTranslate.Y = 0;

        var (stiffness, damping, stagger) = ResolveLineSpring();
        var now = lyricsClock.Elapsed.TotalSeconds;
        var targetOffset = -PhysicalItemTop(targetIndex);
        var forward = previousIndex < 0 || targetIndex >= previousIndex;
        var playback = ViewModel.GetInterpolatedPlaybackPosition();

        for (var i = 0; i < physicalLyricItems.Count; i++)
        {
            var item = physicalLyricItems[i];
            item.OffsetY.Configure(stiffness, damping);
            item.Active.Configure(stiffness * 1.15, damping * 1.07);
            item.ActiveScale = GetPhysicalItemActiveScale(item);

            var isVisible = item.Active.Value > 0.02 || item.ContentHost.Opacity > 0.02;
            if (i < targetIndex && (isVisible || item.IsExiting))
            {
                // Outgoing line: it does not slide away. It stays where it is, shrinks toward the
                // centre of its text and fades out quickly while the next line rises into place.
                var exitStiffness = stiffness * 2.4;
                item.Active.Configure(exitStiffness, 2 * Math.Sqrt(exitStiffness));
                item.OffsetY.Target = item.OffsetY.Value;
                item.OffsetY.Velocity = 0;
                item.IsExiting = true;
                item.PendingOffset = item.OffsetY.Value;
                item.PendingActive = 0;
                item.StartAt = now;
                item.HasPending = true;
                continue;
            }

            item.IsExiting = false;

            // The new line leads, then the following lines rise in a wave.
            var rel = forward ? i - targetIndex : targetIndex - i + 1;
            var delay = Math.Clamp(rel, 0, 6) * stagger;
            item.PendingOffset = targetOffset;
            item.OverlapActive = i > targetIndex && item.Line is { } overlapping && overlapping.IsSoundingAt(playback);
            item.PendingActive = i == targetIndex || item.OverlapActive ? 1 : 0;
            item.StartAt = now + delay;
            item.HasPending = true;
        }

        physicalRenderedIndex = targetIndex;
        EnsureLyricsLoop();
    }

    /// <summary>First appearance of a track: lines rise from below in a cascade.</summary>
    private void BeginPhysicalSpringIntro(int targetIndex)
    {
        if (physicalLyricItems.Count == 0)
        {
            return;
        }

        var introOffset = Math.Max(physicalLineStep, GetLineBandHeight()) * 1.15;
        var start = -PhysicalItemTop(targetIndex) + introOffset;
        foreach (var item in physicalLyricItems)
        {
            item.OffsetY.Snap(start);
            item.Active.Snap(0);
        }

        BeginPhysicalSpringTransition(targetIndex, previousIndex: -1);
        foreach (var item in physicalLyricItems)
        {
            ApplyPhysicalItemVisual(item);
        }
    }

    /// <summary>Puts every line at rest for <paramref name="targetIndex"/> (no animation).</summary>
    private void SnapPhysicalSprings(int targetIndex)
    {
        var targetOffset = -PhysicalItemTop(targetIndex);
        var playback = ViewModel.GetInterpolatedPlaybackPosition();
        for (var i = 0; i < physicalLyricItems.Count; i++)
        {
            var item = physicalLyricItems[i];
            item.ActiveScale = GetPhysicalItemActiveScale(item);
            item.OffsetY.Snap(targetOffset);
            item.OverlapActive = i > targetIndex && item.Line is { } overlapping && overlapping.IsSoundingAt(playback);
            item.Active.Snap(i == targetIndex || item.OverlapActive ? 1 : 0);
            item.HasPending = false;
            item.IsExiting = false;
            item.ExitScale = 1;
            ApplyPhysicalItemVisual(item);
        }

        LyricsTrackTranslate.Y = 0;
        StopLyricsLoop();
    }

    private void EnsureLyricsLoop()
    {
        if (isLyricsLoopActive)
        {
            return;
        }

        isLyricsLoopActive = true;
        lastLyricsFrameSeconds = lyricsClock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnLyricsFrame;
    }

    private void StopLyricsLoop()
    {
        if (!isLyricsLoopActive)
        {
            return;
        }

        isLyricsLoopActive = false;
        CompositionTarget.Rendering -= OnLyricsFrame;
    }

    private void OnLyricsFrame(object? sender, object e)
    {
        try
        {
            var now = lyricsClock.Elapsed.TotalSeconds;
            var dt = Math.Clamp(now - lastLyricsFrameSeconds, 1.0 / 240.0, 1.0 / 20.0);
            lastLyricsFrameSeconds = now;

            if (!physicalTrackModeActive || physicalLyricItems.Count == 0)
            {
                StopLyricsLoop();
                return;
            }

            var moving = false;
            foreach (var item in physicalLyricItems)
            {
                if (item.HasPending && now >= item.StartAt)
                {
                    item.OffsetY.Target = item.PendingOffset;
                    item.Active.Target = item.PendingActive;
                    item.HasPending = false;
                }

                moving |= item.HasPending;
                moving |= item.OffsetY.Step(dt);
                moving |= item.Active.Step(dt);

                if (item.IsExiting && !item.HasPending && item.Active.Target <= 0 && item.Active.Value < 0.01)
                {
                    // Fully gone: park it in its normal place in the track (invisible).
                    item.IsExiting = false;
                    item.Active.Snap(0);
                    item.OffsetY.Snap(-PhysicalItemTop(physicalRenderedIndex));
                }
                ApplyPhysicalItemVisual(item);
            }

            UpdatePhysicalTrackViewportFade();
            if (!moving)
            {
                StopLyricsLoop();
                if (activePhysicalTrackPlan.ContainsVisualizer || ViewModel.VisualizerVisibility == Visibility.Visible)
                {
                    UpdateVisualizerState();
                }

                UpdateDebugOverlayInfo();
            }
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Lyrics: frame failed {ex.Message}");
            StopLyricsLoop();
        }
    }

    /// <summary>Derives transform, opacity and colours of one line from its springs.</summary>
    private void ApplyPhysicalItemVisual(PhysicalLyricItem item)
    {
        var offset = item.OffsetY.Value;
        item.Offset.Y = offset;

        var active = item.Active.Value;
        var activeClamped = Math.Clamp(active, 0, 1);
        var scale = 1 + ((item.ActiveScale - 1) * active);
        item.Scale.ScaleX = scale;
        item.Scale.ScaleY = scale;

        // Row in viewport units: 0 = current slot, 1 = next slot, negative = already sung.
        var step = Math.Max(1, physicalLineStep);
        var row = (Canvas.GetTop(item.Layer) + offset) / step;
        if (item.IsExiting)
        {
            item.ExitScale = 0.84 + (0.16 * activeClamped);
            item.ContentHost.Opacity = Math.Pow(activeClamped, 1.6);
            item.MainBrush.Color = LerpLineColor(palettePreviousColor, paletteCurrentColor, activeClamped);
            item.ShadowBrush.Color = LerpLineColor(paletteShadowColor, EnhanceCurrentLineShadowColor(paletteShadowColor), activeClamped);
            InvalidateLyricsText();
            return;
        }

        item.ExitScale = 1;
        var inactiveOpacity = row switch
        {
            < 0 => 0.72 * Math.Clamp(1 + (row * 1.35), 0, 1),
            <= 1 => 0.72,
            _ => 0.72 * Math.Clamp(2 - row, 0, 1)
        };
        item.ContentHost.Opacity = inactiveOpacity + ((1 - inactiveOpacity) * activeClamped);

        var baseColor = row < -0.05 ? palettePreviousColor : paletteNextColor;
        item.MainBrush.Color = LerpLineColor(baseColor, paletteCurrentColor, activeClamped);
        item.ShadowBrush.Color = LerpLineColor(paletteShadowColor, EnhanceCurrentLineShadowColor(paletteShadowColor), activeClamped);
        InvalidateLyricsText();
    }

    private double[] physicalItemTops = [];

    /// <summary>Height of the smaller background-vocal line drawn under a lead line.</summary>
    private double GetBackgroundLineHeight() => ComputeLineHeight(ViewModel.NextLineFontSize * BackgroundLineScale) + 2;

    /// <summary>Recomputes item positions: lines with background vocals take extra height.</summary>
    private void RecomputePhysicalItemTops()
    {
        var tops = new double[physicalLyricItems.Count];
        var y = 0.0;
        for (var i = 0; i < physicalLyricItems.Count; i++)
        {
            tops[i] = y;
            Canvas.SetTop(physicalLyricItems[i].Layer, y);
            y += physicalLineStep;
            if (physicalLyricItems[i].Line is { HasBackgroundVocals: true })
            {
                y += GetBackgroundLineHeight();
            }
        }

        physicalItemTops = tops;
    }

    private double PhysicalItemTop(int index) =>
        index >= 0 && index < physicalItemTops.Length ? physicalItemTops[index] : index * physicalLineStep;

    /// <summary>
    /// Lines after the current one that have already started (overlapping vocals) are highlighted
    /// for exactly as long as their own timing says, without moving the scroll position.
    /// </summary>
    private void UpdateOverlapActivity()
    {
        if (!physicalTrackModeActive || physicalRenderedIndex < 0)
        {
            return;
        }

        var now = ViewModel.GetInterpolatedPlaybackPosition();
        var changed = false;
        var last = Math.Min(physicalLyricItems.Count, physicalRenderedIndex + 5);
        for (var i = physicalRenderedIndex + 1; i < last; i++)
        {
            var item = physicalLyricItems[i];
            if (item.HasPending || item.IsExiting || item.Line is null)
            {
                continue;
            }

            var sounding = item.Line.IsSoundingAt(now);
            if (sounding == item.OverlapActive)
            {
                continue;
            }

            item.OverlapActive = sounding;
            item.Active.Target = sounding ? 1 : 0;
            changed = true;
        }

        if (changed)
        {
            EnsureLyricsLoop();
        }
    }

    private static Color LerpLineColor(Color from, Color to, double t)
    {
        static byte L(byte a, byte b, double k) => (byte)Math.Clamp(Math.Round(a + ((b - a) * k)), 0, 255);
        return Color.FromArgb(L(from.A, to.A, t), L(from.R, to.R, t), L(from.G, to.G, t), L(from.B, to.B, t));
    }
}
