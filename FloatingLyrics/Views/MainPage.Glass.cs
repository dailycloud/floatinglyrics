using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using FloatingLyrics.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace FloatingLyrics.Views;

/// <summary>
/// Liquid-glass presentation layer of the desktop widget: hover detection, the glass plate behind
/// the lyrics, the hover toolbar, the in-desktop settings panel, and the spring physics that drive
/// all of it. Everything here renders inside the widget window, which lives on the desktop layer.
/// </summary>
public sealed partial class MainPage
{
    private const double HoverLeaveDelaySeconds = 0.32;
    private const double HoverInflate = 8;
    private const double PanelGap = 18;
    private const double PanelEdgeMargin = 14;
    private const double PanelMaxHeight = 640;
    private const double ToolbarGap = 10;
    private const double EmptyPlateWidth = 400;
    private const double EmptyPlateHeight = 88;
    private const double GrabberWidth = 54;
    private const double AnchorEdgeMargin = 40;
    private const double DragStartThreshold = 4;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const uint GaRoot = 2;

    private Spring platePresence = new(0, 120, 17, 0.001);
    private Spring plateX = new(0, 320, 34, 0.05);
    private Spring plateY = new(0, 320, 34, 0.05);
    private Spring plateW = new(0, 320, 34, 0.05);
    private Spring plateH = new(0, 320, 34, 0.05);
    private Spring toolbarPresence = new(0, 170, 20, 0.001);
    private Spring panelPresence = new(0, 150, 19, 0.001);
    private Spring lightIntensity = new(0, 140, 24, 0.002);
    private Spring panelLightIntensity = new(0, 140, 24, 0.002);

    private bool glassInitialized;
    private bool isGlassRenderLoopActive;
    private TimeSpan lastGlassFrameTime;
    private bool isWidgetHovered;
    private bool isPanelHovered;
    private DateTimeOffset lastWidgetHoverUtc = DateTimeOffset.MinValue;
    private bool isSettingsPanelOpen;
    private int activeSettingsTab;
    private Vector2 plateLightPosition;
    private Vector2 plateLightTarget;
    private Vector2 panelLightPosition;
    private Vector2 panelLightTarget;
    private double rasterScale = 1;
    private PointInt32 windowScreenOrigin;
    private bool wasMouseButtonDown;
    private bool isSyncingChoices;
    private bool isAnchorAnimating;
    private bool isDragPending;
    private Point dragPressPointInRoot;
    private Rect panelRectInRoot;
    private Storyboard? settingsPageStoryboard;
    private Storyboard? anchorStoryboard;
    private DispatcherQueueTimer? desktopWatchdogTimer;
    private string centerTextWidthKey = string.Empty;
    private double centerTextWidth = double.NaN;
    private readonly List<(Panel Panel, Func<string> Get)> choiceGroups = [];
    private readonly CompositeTransform settingsContentTransform = new();
    private readonly ScaleTransform toolbarButtonsScale = new();

    // ------------------------------------------------------------------ x:Bind helpers

    public static string FormatNumber(double value) => Math.Round(value).ToString("0", CultureInfo.CurrentCulture);

    public static string FormatPercent(double value) => $"{Math.Round(value * 100):0}%";

    public static string FormatDecimal(double value) => value.ToString("0.00", CultureInfo.CurrentCulture);

    public static string FormatSeconds(double value) => $"{value.ToString("0.00", CultureInfo.CurrentCulture)} с";

    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatNowPlaying(string title, string artist)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        var hasArtist = !string.IsNullOrWhiteSpace(artist);
        return (hasTitle, hasArtist) switch
        {
            (true, true) => $"{title} — {artist}",
            (true, false) => title,
            (false, true) => artist,
            _ => "Виджет текста песен"
        };
    }

    // ------------------------------------------------------------------ lifecycle

    private void InitializeGlass()
    {
        if (glassInitialized)
        {
            return;
        }

        rasterScale = XamlRoot?.RasterizationScale ?? 1;
        if (XamlRoot is not null)
        {
            XamlRoot.Changed += OnXamlRootChanged;
        }

        RefreshWindowOrigin();
        DesktopWallpaperService.Instance.Changed += OnWallpaperChanged;
        DesktopWallpaperService.Instance.Start(DispatcherQueue, GetHostScreenRect);

        WidgetToolbarButtons.RenderTransform = toolbarButtonsScale;
        toolbarButtonsScale.CenterX = 80;
        toolbarButtonsScale.CenterY = 46;
        SettingsPanelContent.RenderTransform = settingsContentTransform;

        FirstSettingsTab.IsChecked = true;
        RegisterChoiceGroups();
        SyncChoiceGroups();
        UpdateTypographyFromSettings();

        desktopWatchdogTimer = DispatcherQueue.CreateTimer();
        desktopWatchdogTimer.Interval = TimeSpan.FromSeconds(2);
        desktopWatchdogTimer.IsRepeating = true;
        desktopWatchdogTimer.Tick += OnDesktopWatchdogTick;
        desktopWatchdogTimer.Start();

        var initialRect = ComputePlateTargetRect();
        plateX.Snap(initialRect.X);
        plateY.Snap(initialRect.Y);
        plateW.Snap(initialRect.Width);
        plateH.Snap(initialRect.Height);
        platePresence.Snap(ViewModel.GlassVisibility == GlassVisibilityMode.Always ? 1 : 0);
        glassInitialized = true;
        ApplyGlassVisuals();
        EnsureGlassRenderLoop();
    }

    private void TeardownGlass()
    {
        if (!glassInitialized)
        {
            return;
        }

        glassInitialized = false;
        StopGlassRenderLoop();
        DesktopWallpaperService.Instance.Changed -= OnWallpaperChanged;
        if (XamlRoot is not null)
        {
            XamlRoot.Changed -= OnXamlRootChanged;
        }

        if (desktopWatchdogTimer is not null)
        {
            desktopWatchdogTimer.Stop();
            desktopWatchdogTimer.Tick -= OnDesktopWatchdogTick;
            desktopWatchdogTimer = null;
        }

        settingsPageStoryboard?.Stop();
        anchorStoryboard?.Stop();
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        var scale = sender.RasterizationScale;
        if (Math.Abs(scale - rasterScale) > 0.001)
        {
            rasterScale = scale;
            DesktopWallpaperService.Instance.RequestRefresh(force: true);
        }

        RequestGlassFrame();
    }

    private void OnWallpaperChanged(object? sender, EventArgs e)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                UpdateArtworkSwatch();
                InvalidateAllGlass();
            }
            catch (Exception ex)
            {
                DebugTrace.Write($"Glass: wallpaper refresh failed {ex.Message}");
            }
        });
    }

    private void OnDesktopWatchdogTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (ViewModel.EnsureDesktopAttachment())
            {
                RefreshWindowOrigin();
                DesktopWallpaperService.Instance.RequestRefresh(force: true);
                RequestGlassFrame();
            }

            TryCaptureLiveBackdrop();
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Glass: desktop watchdog failed {ex.Message}");
        }
    }

    private RectInt32? GetHostScreenRect()
    {
        if (hwnd == 0 || !GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        return new RectInt32(rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
    }

    private void RefreshWindowOrigin()
    {
        if (hwnd != 0 && GetWindowRect(hwnd, out var rect))
        {
            windowScreenOrigin = new PointInt32(rect.Left, rect.Top);
        }
    }

    // ------------------------------------------------------------------ hover & input

    /// <summary>
    /// Polled at ~30 Hz. Decides whether the cursor is over the widget (on a visible part of the
    /// desktop), toggles click-through accordingly, drives hover targets and the cursor light.
    /// </summary>
    private void UpdateWidgetInteraction()
    {
        if (hwnd == 0)
        {
            return;
        }

        if (!glassInitialized)
        {
            // Never block the desktop icons while the glass layer is not ready.
            SetWindowClickThrough(true);
            return;
        }

        try
        {
            UpdateWidgetInteractionCore();
        }
        catch (Exception ex)
        {
            // Never let a timer tick throw: WinUI turns that into a process fail-fast.
            DebugTrace.Write($"Glass: interaction update failed {ex.Message}");
        }
    }

    private void UpdateWidgetInteractionCore()
    {

        RefreshWindowOrigin();
        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var desktopVisible = IsDesktopVisibleAt(cursor);
        var cursorInRoot = ScreenToRoot(cursor.X, cursor.Y);

        var plateInRoot = OverlayRectToRoot(CurrentPlateRect());
        var hoverRect = Inflate(plateInRoot, HoverInflate);
        var overWidget = desktopVisible && (Contains(hoverRect, cursorInRoot) || Contains(GetToolbarRectInRoot(), cursorInRoot));
        var overPanel = desktopVisible && isSettingsPanelOpen && Contains(panelRectInRoot, cursorInRoot);

        if (overWidget || isDraggingWindow)
        {
            lastWidgetHoverUtc = now;
        }

        var hovered = overWidget || isDraggingWindow || (now - lastWidgetHoverUtc).TotalSeconds < HoverLeaveDelaySeconds;
        var hoverChanged = hovered != isWidgetHovered || overPanel != isPanelHovered;
        isWidgetHovered = hovered;
        isPanelHovered = overPanel;

        // A click anywhere outside the widget and the panel dismisses the panel.
        var mouseDown = (GetAsyncKeyState(VkLButton) & 0x8000) != 0 || (GetAsyncKeyState(VkRButton) & 0x8000) != 0;
        if (isSettingsPanelOpen && mouseDown && !wasMouseButtonDown && !overPanel && !overWidget)
        {
            CloseSettingsPanel();
        }

        wasMouseButtonDown = mouseDown;

        // While the menu is open the widget stays above the icon layer (a click elsewhere on the
        // desktop dismisses the menu, like a regular light-dismiss popup).
        var interactive = isDraggingWindow || overWidget || overPanel || isSettingsPanelOpen || panelPresence.Value > 0.02;
        SetWindowClickThrough(!interactive);

        var lightEnabled = ViewModel.GlassCursorLight;
        if (overWidget && lightEnabled)
        {
            var plateShape = CurrentPlateRect();
            var plateOrigin = OverlayRectToRoot(plateShape);
            plateLightTarget = new Vector2((float)(cursorInRoot.X - plateOrigin.X), (float)(cursorInRoot.Y - plateOrigin.Y));
            if (lightIntensity.Value < 0.01)
            {
                plateLightPosition = plateLightTarget;
            }
        }

        if (overPanel && lightEnabled)
        {
            panelLightTarget = new Vector2((float)(cursorInRoot.X - panelRectInRoot.X), (float)(cursorInRoot.Y - panelRectInRoot.Y));
            if (panelLightIntensity.Value < 0.01)
            {
                panelLightPosition = panelLightTarget;
            }
        }

        lightIntensity.Target = overWidget && lightEnabled ? 1 : 0;
        panelLightIntensity.Target = overPanel && lightEnabled ? 1 : 0;

        UpdateWordTicker();
        UpdateBackdropContrast();

        var targetsChanged = UpdateSpringTargets();
        var lightMoving = (overWidget && Vector2.DistanceSquared(plateLightPosition, plateLightTarget) > 0.25f) ||
            (overPanel && Vector2.DistanceSquared(panelLightPosition, panelLightTarget) > 0.25f);
        if (hoverChanged || targetsChanged || lightMoving || isDraggingWindow)
        {
            EnsureGlassRenderLoop();
        }
    }

    private bool IsDesktopVisibleAt(NativePoint cursor)
    {
        var hit = WindowFromPoint(cursor);
        if (hit == 0)
        {
            return false;
        }

        var root = GetAncestor(hit, GaRoot);
        if (root == 0)
        {
            return false;
        }

        if (root == hwnd || root == GetAncestor(hwnd, GaRoot))
        {
            return true;
        }

        // When click-through is active the hit test lands on the shell's icon view.
        var className = GetWindowClassName(root);
        return className is "Progman" or "WorkerW";
    }

    private static string GetWindowClassName(nint window)
    {
        Span<char> buffer = stackalloc char[64];
        unsafe
        {
            fixed (char* ptr = buffer)
            {
                var length = GetClassNameW(window, ptr, buffer.Length);
                return length > 0 ? new string(ptr, 0, length) : string.Empty;
            }
        }
    }

    private void ToolbarSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleSettingsPanel();
    }

    private void CloseSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        CloseSettingsPanel();
    }

    public void OpenSettingsPanelFromTray()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            lastWidgetHoverUtc = DateTimeOffset.UtcNow;
            OpenSettingsPanel();
        });
    }

    private void ToggleSettingsPanel()
    {
        if (isSettingsPanelOpen)
        {
            CloseSettingsPanel();
        }
        else
        {
            OpenSettingsPanel();
        }
    }

    private void OpenSettingsPanel()
    {
        if (isSettingsPanelOpen)
        {
            return;
        }

        isSettingsPanelOpen = true;
        SyncChoiceGroups();
        UpdateArtworkSwatch();
        UpdateSettingsPanelPlacement();
        SettingsPanel.Visibility = Visibility.Visible;
        panelPresence.Target = 1;
        if (panelPresence.Value < 0.05)
        {
            panelPresence.Velocity = 0.6;
        }

        AnimateSettingsPageIn(activeSettingsTab);
        SetWindowClickThrough(false);
        EnsureGlassRenderLoop();
    }

    private void CloseSettingsPanel()
    {
        if (!isSettingsPanelOpen)
        {
            return;
        }

        isSettingsPanelOpen = false;
        isPanelHovered = false;
        panelPresence.Target = 0;
        panelLightIntensity.Target = 0;
        EnsureGlassRenderLoop();
    }

    private void SettingsTab_OnChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var index) ||
            SettingsPages is null || index < 0 || index >= SettingsPages.Children.Count)
        {
            return;
        }

        if (index == activeSettingsTab && SettingsPages.Children[index].Visibility == Visibility.Visible)
        {
            return;
        }

        activeSettingsTab = index;
        AnimateSettingsPageIn(index);
    }

    private void AnimateSettingsPageIn(int index)
    {
        if (SettingsPages is null)
        {
            return;
        }

        for (var i = 0; i < SettingsPages.Children.Count; i++)
        {
            SettingsPages.Children[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        if (SettingsTabs.Children[index] is RadioButton { IsChecked: not true } tabButton)
        {
            tabButton.IsChecked = true;
        }

        SettingsScroll?.ChangeView(null, 0, null, disableAnimation: true);
        if (SettingsPages.Children[index] is not Panel page)
        {
            return;
        }

        settingsPageStoryboard?.Stop();
        var storyboard = new Storyboard();
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var delayIndex = 0;
        foreach (var child in page.Children)
        {
            if (child is not FrameworkElement element)
            {
                continue;
            }

            if (element.RenderTransform is not TranslateTransform translate)
            {
                translate = new TranslateTransform();
                element.RenderTransform = translate;
            }

            var begin = TimeSpan.FromMilliseconds(Math.Min(delayIndex, 10) * 24);
            var duration = new Duration(TimeSpan.FromMilliseconds(300));
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration, BeginTime = begin, EasingFunction = easing };
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
            var rise = new DoubleAnimation { From = 14, To = 0, Duration = duration, BeginTime = begin, EasingFunction = easing };
            Storyboard.SetTarget(rise, translate);
            Storyboard.SetTargetProperty(rise, nameof(TranslateTransform.Y));
            element.Opacity = 0;
            storyboard.Children.Add(fade);
            storyboard.Children.Add(rise);
            delayIndex++;
        }

        settingsPageStoryboard = storyboard;
        storyboard.Begin();
    }

    private void AnchorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var v))
        {
            return;
        }

        MoveWidgetToAnchor(h, v);
    }

    /// <summary>Animates the widget so its glass plate snaps to a corner/edge/center of its monitor.</summary>
    private void MoveWidgetToAnchor(int horizontal, int vertical)
    {
        var plate = ComputePlateTargetRect();
        var plateRoot = OverlayRectToRoot(plate);
        var monitor = GetMonitorWorkAreaInRoot(new Point(plateRoot.X + (plateRoot.Width / 2), plateRoot.Y + (plateRoot.Height / 2)));
        var left = horizontal switch
        {
            0 => monitor.X + AnchorEdgeMargin,
            1 => monitor.X + ((monitor.Width - plate.Width) / 2),
            _ => monitor.X + monitor.Width - AnchorEdgeMargin - plate.Width
        };
        var top = vertical switch
        {
            0 => monitor.Y + AnchorEdgeMargin,
            1 => monitor.Y + ((monitor.Height - plate.Height) / 2),
            _ => monitor.Y + monitor.Height - AnchorEdgeMargin - plate.Height
        };

        var margin = OverlayRootHost.Margin;
        var target = ClampOverlayRootOffset(new Point(left - margin.Left - plate.X, top - margin.Top - plate.Y));

        anchorStoryboard?.Stop();
        var duration = new Duration(TimeSpan.FromMilliseconds(520));
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 };
        var storyboard = new Storyboard();
        var ax = new DoubleAnimation { From = OverlayRootHostTranslate.X, To = target.X, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(ax, OverlayRootHostTranslate);
        Storyboard.SetTargetProperty(ax, nameof(TranslateTransform.X));
        var ay = new DoubleAnimation { From = OverlayRootHostTranslate.Y, To = target.Y, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(ay, OverlayRootHostTranslate);
        Storyboard.SetTargetProperty(ay, nameof(TranslateTransform.Y));
        storyboard.Children.Add(ax);
        storyboard.Children.Add(ay);
        storyboard.Completed += (_, _) =>
        {
            isAnchorAnimating = false;
            anchorStoryboard = null;
            ApplyOverlayRootOffset(target, persist: true);
            RequestGlassFrame();
        };
        anchorStoryboard = storyboard;
        isAnchorAnimating = true;
        storyboard.Begin();
        EnsureGlassRenderLoop();
    }

    // ------------------------------------------------------------------ choice groups

    private void RegisterChoiceGroups()
    {
        BindChoice(GlassVisibilityChoices,
            () => ViewModel.GlassVisibility.ToString(),
            v => ViewModel.GlassVisibility = Enum.Parse<GlassVisibilityMode>(v));
        BindChoice(GlassTintChoices,
            () => ViewModel.GlassTint.ToString(),
            v => ViewModel.GlassTint = Enum.Parse<GlassTintPreset>(v));
        BindChoice(LyricsColorChoices,
            () => ViewModel.UseArtworkTextColors ? string.Empty : ViewModel.LyricsColorPreset.ToString(),
            v =>
            {
                ViewModel.UseArtworkTextColors = false;
                ViewModel.LyricsColorPreset = Enum.Parse<LyricsColorPreset>(v);
            });
        BindChoice(LyricsFontChoices,
            () => string.IsNullOrWhiteSpace(ViewModel.CustomFontFamily) ? ViewModel.LyricsFont.ToString() : string.Empty,
            v =>
            {
                ViewModel.CustomFontFamily = string.Empty;
                ViewModel.LyricsFont = Enum.Parse<LyricsFontOption>(v);
            });
        BindChoice(TextContrastChoices,
            () => ViewModel.TextContrast.ToString(),
            v => ViewModel.TextContrast = Enum.Parse<TextContrastMode>(v));
        BindChoice(LyricsWeightChoices,
            () => ViewModel.LyricsFontWeight.ToString(),
            v => ViewModel.LyricsFontWeight = Enum.Parse<LyricsFontWeightOption>(v));
        BindChoice(LyricsAlignmentChoices,
            () => ViewModel.AutoLyricsTextAlignment ? "Auto" : ViewModel.LyricsTextAlignment.ToString(),
            v =>
            {
                if (v == "Auto")
                {
                    ViewModel.AutoLyricsTextAlignment = true;
                    return;
                }

                ViewModel.AutoLyricsTextAlignment = false;
                ViewModel.LyricsTextAlignment = Enum.Parse<LyricsTextAlignmentMode>(v);
            });
        BindChoice(LineAnimationChoices,
            () => ViewModel.LineAnimationStyle.ToString(),
            v => ViewModel.LineAnimationStyle = Enum.Parse<LineAnimationStyle>(v));
    }

    private void BindChoice(Panel panel, Func<string> get, Action<string> set)
    {
        foreach (var child in panel.Children)
        {
            if (child is not RadioButton radio)
            {
                continue;
            }

            radio.Checked += (_, _) =>
            {
                if (isSyncingChoices || radio.Tag is not string tag || tag == get())
                {
                    return;
                }

                set(tag);
                SyncChoiceGroups();
            };
        }

        choiceGroups.Add((panel, get));
    }

    private void SyncChoiceGroups()
    {
        isSyncingChoices = true;
        try
        {
            foreach (var (panel, get) in choiceGroups)
            {
                var current = get();
                foreach (var child in panel.Children)
                {
                    if (child is RadioButton { Tag: string tag } radio)
                    {
                        radio.IsChecked = tag == current;
                    }
                }
            }

            GlassTintCaption.Text = ViewModel.GlassTint switch
            {
                GlassTintPreset.Clear => "Прозрачная — чистое стекло",
                GlassTintPreset.Smoke => "Дымчатая — лучше читается текст",
                GlassTintPreset.Frost => "Иней — светлое матовое стекло",
                GlassTintPreset.Artwork => "Цвет обложки — подстраивается под трек",
                GlassTintPreset.Ocean => "Океан",
                _ => "Роза"
            };
        }
        finally
        {
            isSyncingChoices = false;
        }
    }

    private void UpdateArtworkSwatch()
    {
        if (ArtworkTintSwatch is not null)
        {
            ArtworkTintSwatch.Background = new SolidColorBrush(ViewModel.ArtworkAccentColor);
        }
    }

    /// <summary>Called from the main property-changed handler for glass/typography settings.</summary>
    private void OnGlassSettingChanged(string? propertyName)
    {
        if (!glassInitialized)
        {
            return;
        }

        try
        {
            ApplyGlassSettingChange(propertyName);
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Glass: setting change failed {ex.Message}");
        }
    }

    private void ApplyGlassSettingChange(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(MainViewModel.LyricsFont):
            case nameof(MainViewModel.LyricsFontWeight):
            case nameof(MainViewModel.CustomFontFamily):
                UpdateTypographyFromSettings();
                InvalidateOverlayGeometryCache();
                centerTextWidthKey = string.Empty;
                if (ShouldUsePhysicalTrack())
                {
                    RebuildPhysicalTrack(forceImmediate: true);
                }
                else
                {
                    ApplyRestingLineLayerVisuals();
                }

                UpdateLyricsHorizontalAlignment(forceGeometryRecompute: true);
                break;
            case nameof(MainViewModel.ArtworkAccentColor):
                UpdateArtworkSwatch();
                break;
            case nameof(MainViewModel.LyricsAttribution):
                UpdateLyricsAttribution();
                break;
        }

        SyncChoiceGroups();
        UpdateSpringTargets();
        EnsureGlassRenderLoop();
        contrastSampleClock.Reset();
        InvalidateLyricsText();
    }

    private static bool IsGlassSettingProperty(string? name) => name is
        nameof(MainViewModel.LyricsAttribution) or
        nameof(MainViewModel.GlassVisibility) or
        nameof(MainViewModel.GlassBlur) or
        nameof(MainViewModel.GlassRefraction) or
        nameof(MainViewModel.GlassDispersion) or
        nameof(MainViewModel.GlassSpecular) or
        nameof(MainViewModel.GlassTint) or
        nameof(MainViewModel.GlassTintStrength) or
        nameof(MainViewModel.GlassCornerRadius) or
        nameof(MainViewModel.GlassPadding) or
        nameof(MainViewModel.GlassCursorLight) or
        nameof(MainViewModel.UseArtworkTextColors) or
        nameof(MainViewModel.LyricsColorPreset) or
        nameof(MainViewModel.LyricsFont) or
        nameof(MainViewModel.CustomFontFamily) or
        nameof(MainViewModel.TextContrast) or
        nameof(MainViewModel.WordByWordHighlight) or
        nameof(MainViewModel.LyricsFontWeight) or
        nameof(MainViewModel.LineAnimationStyle) or
        nameof(MainViewModel.ArtworkAccentColor) or
        nameof(MainViewModel.AutoLyricsTextAlignment) or
        nameof(MainViewModel.LyricsTextAlignment);

    // ------------------------------------------------------------------ typography & motion

    private void UpdateTypographyFromSettings()
    {
        var family = ViewModel.LyricsFont switch
        {
            LyricsFontOption.SegoeDisplay => "Segoe UI Variable Display",
            LyricsFontOption.Bahnschrift => "Bahnschrift",
            LyricsFontOption.Georgia => "Georgia",
            LyricsFontOption.Consolas => "Cascadia Mono, Consolas",
            _ => "Segoe UI Variable Text"
        };

        var custom = ViewModel.CustomFontFamily?.Trim();
        if (!string.IsNullOrEmpty(custom))
        {
            family = custom;
        }

        currentLineFontFamily = new FontFamily(family);
        nextLineFontFamily = currentLineFontFamily;

        // A custom font applies to the whole widget; presets keep the designed title fonts.
        TrackTitleText.FontFamily = string.IsNullOrEmpty(custom) ? new FontFamily("Bahnschrift SemiBold") : new FontFamily(custom);
        ArtistNameText.FontFamily = string.IsNullOrEmpty(custom) ? new FontFamily("Segoe UI Variable Text") : new FontFamily(custom);
        if (CustomFontValue is not null)
        {
            CustomFontValue.Text = string.IsNullOrEmpty(custom) ? "не выбран" : custom;
        }
        lyricsFontWeight = ViewModel.LyricsFontWeight switch
        {
            LyricsFontWeightOption.Light => Microsoft.UI.Text.FontWeights.Light,
            LyricsFontWeightOption.Regular => Microsoft.UI.Text.FontWeights.Normal,
            LyricsFontWeightOption.Bold => Microsoft.UI.Text.FontWeights.Bold,
            _ => Microsoft.UI.Text.FontWeights.SemiBold
        };
    }

    /// <summary>Easing for position/scale of lyric lines (opacity and colour keep a plain cubic).</summary>
    private EasingFunctionBase CreateLineMotionEasing() => ViewModel.LineAnimationStyle switch
    {
        LineAnimationStyle.Spring => new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.32 },
        LineAnimationStyle.Snappy => new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 },
        _ => new CubicEase { EasingMode = EasingMode.EaseInOut }
    };

    private double LineMotionDurationScale => ViewModel.LineAnimationStyle switch
    {
        LineAnimationStyle.Spring => 1.25,
        LineAnimationStyle.Snappy => 0.85,
        _ => 1.0
    };

    // ------------------------------------------------------------------ render loop

    private void RequestGlassFrame() => EnsureGlassRenderLoop();

    private void EnsureGlassRenderLoop()
    {
        if (isGlassRenderLoopActive || !glassInitialized)
        {
            return;
        }

        isGlassRenderLoopActive = true;
        lastGlassFrameTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnGlassFrame;
    }

    private void StopGlassRenderLoop()
    {
        if (!isGlassRenderLoopActive)
        {
            return;
        }

        isGlassRenderLoopActive = false;
        CompositionTarget.Rendering -= OnGlassFrame;
    }

    private void OnGlassFrame(object? sender, object e)
    {
        try
        {
            OnGlassFrameCore(e);
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Glass: frame failed {ex.Message}");
            StopGlassRenderLoop();
        }
    }

    private void OnGlassFrameCore(object e)
    {
        var renderingTime = e is RenderingEventArgs args ? args.RenderingTime : TimeSpan.Zero;
        var dt = lastGlassFrameTime == TimeSpan.Zero
            ? 1.0 / 60.0
            : Math.Clamp((renderingTime - lastGlassFrameTime).TotalSeconds, 1.0 / 240.0, 1.0 / 20.0);
        lastGlassFrameTime = renderingTime;

        UpdateSpringTargets();
        var moving = false;
        moving |= platePresence.Step(dt);
        moving |= plateX.Step(dt);
        moving |= plateY.Step(dt);
        moving |= plateW.Step(dt);
        moving |= plateH.Step(dt);
        moving |= toolbarPresence.Step(dt);
        moving |= panelPresence.Step(dt);
        moving |= lightIntensity.Step(dt);
        moving |= panelLightIntensity.Step(dt);

        var follow = (float)(1 - Math.Exp(-dt * 14));
        plateLightPosition = Vector2.Lerp(plateLightPosition, plateLightTarget, follow);
        panelLightPosition = Vector2.Lerp(panelLightPosition, panelLightTarget, follow);
        moving |= lightIntensity.Value > 0.01 && Vector2.DistanceSquared(plateLightPosition, plateLightTarget) > 0.25f;
        moving |= panelLightIntensity.Value > 0.01 && Vector2.DistanceSquared(panelLightPosition, panelLightTarget) > 0.25f;
        moving |= isDraggingWindow || isAnchorAnimating;
        moving |= GlassPlate.NeedsFrames || ToolbarGlass.NeedsFrames || SettingsGlass.NeedsFrames;

        ApplyGlassVisuals();

        if (!moving)
        {
            StopGlassRenderLoop();
        }
    }

    /// <summary>Recomputes spring targets from state. Returns true if any target changed.</summary>
    private bool UpdateSpringTargets()
    {
        var changed = false;
        var mode = ViewModel.GlassVisibility;
        var wantPlate = mode switch
        {
            GlassVisibilityMode.Always => true,
            GlassVisibilityMode.Hidden => isSettingsPanelOpen || isDraggingWindow,
            _ => isWidgetHovered || isSettingsPanelOpen || isDraggingWindow
        };
        changed |= SetTarget(ref platePresence, wantPlate ? 1 : 0);
        changed |= SetTarget(ref toolbarPresence, isWidgetHovered || isSettingsPanelOpen ? 1 : 0);

        var rect = ComputePlateTargetRect();
        if (platePresence.Value < 0.01 && platePresence.Target < 0.5)
        {
            // Invisible: follow the content instantly so the next appearance starts in place.
            plateX.Snap(rect.X);
            plateY.Snap(rect.Y);
            plateW.Snap(rect.Width);
            plateH.Snap(rect.Height);
        }
        else
        {
            changed |= SetTarget(ref plateX, rect.X);
            changed |= SetTarget(ref plateY, rect.Y);
            changed |= SetTarget(ref plateW, rect.Width);
            changed |= SetTarget(ref plateH, rect.Height);
        }

        return changed;
    }

    private static bool SetTarget(ref Spring spring, double target)
    {
        if (Math.Abs(spring.Target - target) <= spring.Epsilon)
        {
            return false;
        }

        spring.Target = target;
        return true;
    }

    private void ApplyGlassVisuals()
    {
        var presence = platePresence.Value;
        var visible = presence > 0.002;
        var plate = CurrentPlateRect();
        var margin = LiquidGlassSurface.ShapeMargin;

        GlassPlate.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            GlassPlate.SetShapeSize(plate.Width, plate.Height);
            Canvas.SetLeft(GlassPlate, plate.X - margin);
            Canvas.SetTop(GlassPlate, plate.Y - margin);
            ApplyMaterial(GlassPlate, forPanel: false);
            GlassPlate.ShapeCornerRadius = (float)ViewModel.GlassCornerRadius;
            GlassPlate.Presence = (float)presence;
            GlassPlate.ShapeScreenOrigin = RootToScreen(OverlayRectToRoot(plate));
            GlassPlate.LightPosition = lightIntensity.Value > 0.01 ? plateLightPosition : null;
            GlassPlate.LightIntensity = (float)lightIntensity.Value;
            GlassPlate.Invalidate();
        }

        Canvas.SetLeft(GlassPlateHitArea, plate.X);
        Canvas.SetTop(GlassPlateHitArea, plate.Y);
        GlassPlateHitArea.Width = Math.Max(0, plate.Width);
        GlassPlateHitArea.Height = Math.Max(0, plate.Height);

        var visibility = Math.Clamp(presence, 0, 1);
        DragHandle.Opacity = visibility * 0.5;

        var contentShown = overlayContentVisibleState == true;
        EmptyStatePlaceholder.Opacity = contentShown ? 0 : visibility;
        Canvas.SetLeft(EmptyStatePlaceholder, plate.X + Math.Max(18, ViewModel.GlassPadding));
        Canvas.SetTop(EmptyStatePlaceholder, plate.Y + ((plate.Height - 40) / 2));

        ApplyToolbarVisuals(plate);
        ApplySettingsPanelVisuals();
    }

    private void ApplyToolbarVisuals(Rect plate)
    {
        var presence = toolbarPresence.Value;
        var visible = presence > 0.01;
        WidgetToolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            return;
        }

        var size = GetToolbarSize();
        var x = plate.X + plate.Width - size.Width;
        var y = plate.Y - size.Height - ToolbarGap;
        var rootY = OverlayRectToRoot(new Rect(x, y, size.Width, size.Height)).Y;
        if (rootY < 4)
        {
            y = plate.Y + plate.Height + ToolbarGap;
        }

        Canvas.SetLeft(WidgetToolbar, x);
        Canvas.SetTop(WidgetToolbar, y);
        var visibility = Math.Clamp(presence, 0, 1);
        WidgetToolbarButtons.Opacity = visibility;
        toolbarButtonsScale.CenterX = size.Width / 2;
        toolbarButtonsScale.CenterY = size.Height / 2;
        toolbarButtonsScale.ScaleX = 0.8 + (0.2 * presence);
        toolbarButtonsScale.ScaleY = 0.8 + (0.2 * presence);
        WidgetToolbar.Opacity = 1;

        ToolbarGlass.SetShapeSize(size.Width, size.Height);
        ApplyMaterial(ToolbarGlass, forPanel: false);
        ToolbarGlass.ShapeCornerRadius = (float)(size.Height / 2);
        ToolbarGlass.Presence = (float)presence;
        ToolbarGlass.ShapeScreenOrigin = RootToScreen(OverlayRectToRoot(new Rect(x, y, size.Width, size.Height)));
        ToolbarGlass.ShadowOpacity = 0.22f;
        ToolbarGlass.Invalidate();
    }

    private void ApplySettingsPanelVisuals()
    {
        var presence = panelPresence.Value;
        var visible = presence > 0.004 || isSettingsPanelOpen;
        SettingsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            return;
        }

        if (isSettingsPanelOpen)
        {
            // Follow the widget while it is dragged or resized.
            UpdateSettingsPanelPlacement();
        }

        var visibility = Math.Clamp(presence, 0, 1);
        SettingsPanel.Opacity = 1;
        SettingsPanelContent.Opacity = Math.Pow(visibility, 1.6);
        settingsContentTransform.CenterX = panelOpensToRight ? 0 : panelRectInRoot.Width;
        settingsContentTransform.CenterY = 40;
        settingsContentTransform.ScaleX = 0.94 + (0.06 * presence);
        settingsContentTransform.ScaleY = 0.94 + (0.06 * presence);
        settingsContentTransform.TranslateX = (1 - visibility) * (panelOpensToRight ? -18 : 18);

        SettingsGlass.SetShapeSize(panelRectInRoot.Width, panelRectInRoot.Height);
        ApplyMaterial(SettingsGlass, forPanel: true);
        SettingsGlass.ShapeCornerRadius = 30;
        SettingsGlass.Presence = (float)presence;
        SettingsGlass.ShapeScreenOrigin = RootToScreen(panelRectInRoot);
        SettingsGlass.LightPosition = panelLightIntensity.Value > 0.01 ? panelLightPosition : null;
        SettingsGlass.LightIntensity = (float)(panelLightIntensity.Value * 0.7);
        SettingsGlass.Invalidate();
    }

    private bool panelOpensToRight = true;

    private void UpdateSettingsPanelPlacement()
    {
        var plateRoot = OverlayRectToRoot(CurrentPlateRect());
        var monitor = GetMonitorWorkAreaInRoot(new Point(plateRoot.X + (plateRoot.Width / 2), plateRoot.Y + (plateRoot.Height / 2)));
        var width = SettingsPanel.Width;
        var height = Math.Min(PanelMaxHeight, Math.Max(320, monitor.Height - (2 * PanelEdgeMargin)));

        var x = plateRoot.X + plateRoot.Width + PanelGap;
        panelOpensToRight = true;
        if (x + width > monitor.X + monitor.Width - PanelEdgeMargin)
        {
            var leftX = plateRoot.X - PanelGap - width;
            if (leftX >= monitor.X + PanelEdgeMargin)
            {
                x = leftX;
                panelOpensToRight = false;
            }
            else
            {
                x = monitor.X + monitor.Width - PanelEdgeMargin - width;
            }
        }

        var y = Math.Clamp(
            plateRoot.Y,
            monitor.Y + PanelEdgeMargin,
            Math.Max(monitor.Y + PanelEdgeMargin, monitor.Y + monitor.Height - PanelEdgeMargin - height));

        SettingsPanel.Height = height;
        Canvas.SetLeft(SettingsPanel, x);
        Canvas.SetTop(SettingsPanel, y);
        panelRectInRoot = new Rect(x, y, width, height);
    }

    private void ApplyMaterial(LiquidGlassSurface surface, bool forPanel)
    {
        var tintPreset = ViewModel.GlassTint;
        var tint = ResolveTintColor(tintPreset);
        var strength = (float)Math.Clamp(ViewModel.GlassTintStrength, 0, 0.9);
        var opacity = tintPreset == GlassTintPreset.Clear ? strength * 0.35f : strength;

        if (forPanel)
        {
            // The menu always needs comfortable contrast for small white text.
            tint = LerpColor(tint, Color.FromArgb(255, 14, 16, 22), tintPreset == GlassTintPreset.Frost ? 0.78 : 0.5);
            opacity = Math.Max(opacity, 0.46f);
        }

        surface.Tint = tint;
        surface.TintOpacity = opacity;
        surface.Blur = (float)(forPanel ? Math.Max(ViewModel.GlassBlur, 18) + 8 : ViewModel.GlassBlur);
        surface.Refraction = (float)(forPanel ? ViewModel.GlassRefraction * 0.85 : ViewModel.GlassRefraction);
        surface.Dispersion = (float)ViewModel.GlassDispersion;
        surface.Specular = (float)ViewModel.GlassSpecular;
        surface.RasterScale = (float)rasterScale;
        surface.ShadowOpacity = forPanel ? 0.4f : 0.3f;
    }

    private Color ResolveTintColor(GlassTintPreset preset) => preset switch
    {
        GlassTintPreset.Clear => Color.FromArgb(255, 255, 255, 255),
        GlassTintPreset.Frost => Color.FromArgb(255, 238, 242, 248),
        GlassTintPreset.Artwork => LerpColor(ViewModel.ArtworkAccentColor, Color.FromArgb(255, 0, 0, 0), 0.45),
        GlassTintPreset.Ocean => Color.FromArgb(255, 18, 70, 150),
        GlassTintPreset.Rose => Color.FromArgb(255, 160, 38, 96),
        _ => Color.FromArgb(255, 12, 14, 20)
    };

    private void InvalidateAllGlass()
    {
        GlassPlate.Invalidate();
        ToolbarGlass.Invalidate();
        SettingsGlass.Invalidate();
        RequestGlassFrame();
    }

    // ------------------------------------------------------------------ geometry

    private Rect CurrentPlateRect() => new(plateX.Value, plateY.Value, Math.Max(1, plateW.Value), Math.Max(1, plateH.Value));

    /// <summary>Target rect of the glass plate in OverlayRootHost coordinates.</summary>
    private Rect ComputePlateTargetRect()
    {
        var pad = Math.Clamp(ViewModel.GlassPadding, 8, 56);
        var host = OverlayTextBlockHost;
        if (host is null || host.ActualWidth < 1)
        {
            return new Rect(-pad, -pad, EmptyPlateWidth, EmptyPlateHeight);
        }

        var hostOrigin = host.TransformToVisual(OverlayRootHost).TransformPoint(new Point(0, 0));
        var contentWidth = host.ActualWidth;
        var mode = ResolveEffectiveLyricsTextAlignmentMode();

        if (overlayContentVisibleState != true)
        {
            // Nothing is playing: a compact plate for the placeholder, aligned like the lyrics.
            var emptyX = mode switch
            {
                LyricsTextAlignmentMode.Center => hostOrigin.X + ((contentWidth - EmptyPlateWidth) / 2),
                LyricsTextAlignmentMode.Right => hostOrigin.X + contentWidth - EmptyPlateWidth,
                _ => hostOrigin.X - pad
            };
            return new Rect(emptyX, hostOrigin.Y - (pad * 0.5), EmptyPlateWidth, EmptyPlateHeight);
        }

        var textWidth = Math.Min(contentWidth, ResolveVisibleTextWidth(mode, contentWidth));
        var left = mode switch
        {
            LyricsTextAlignmentMode.Center => hostOrigin.X + ((contentWidth - textWidth) / 2),
            LyricsTextAlignmentMode.Right => hostOrigin.X + contentWidth - textWidth,
            _ => hostOrigin.X
        };

        var top = ViewModel.ShowDragHandle ? 0 : hostOrigin.Y;
        var bottom = hostOrigin.Y + host.ActualHeight;
        var topPad = ViewModel.ShowDragHandle ? pad * 0.55 : pad * 0.8;
        return new Rect(left - pad, top - topPad, textWidth + (2 * pad), Math.Max(40, bottom - top) + topPad + (pad * 0.8));
    }

    private double ResolveVisibleTextWidth(LyricsTextAlignmentMode mode, double contentWidth)
    {
        if (mode != LyricsTextAlignmentMode.Center && double.IsFinite(cachedTrackLongestContentWidth))
        {
            return Math.Max(OverlayContentMinWidth, cachedTrackLongestContentWidth + 24);
        }

        var key = string.Concat(
            BuildOverlayGeometryTrackIdentity(),
            "|", ViewModel.CurrentLineFontSize.ToString(CultureInfo.InvariantCulture),
            "|", ViewModel.NextLineFontSize.ToString(CultureInfo.InvariantCulture),
            "|", ViewModel.TitleFontSize.ToString(CultureInfo.InvariantCulture),
            "|", ((int)ViewModel.LyricsFont).ToString(CultureInfo.InvariantCulture),
            "|", ((int)ViewModel.LyricsFontWeight).ToString(CultureInfo.InvariantCulture));
        if (!string.Equals(key, centerTextWidthKey, StringComparison.Ordinal) || !double.IsFinite(centerTextWidth))
        {
            centerTextWidthKey = key;
            centerTextWidth = MeasureOverlayLongestContentWidth();
        }

        return double.IsFinite(centerTextWidth)
            ? Math.Clamp(centerTextWidth + 24, Math.Min(OverlayContentMinWidth, contentWidth), Math.Max(1, contentWidth))
            : contentWidth;
    }

    private Size GetToolbarSize()
    {
        var w = WidgetToolbarButtons.ActualWidth;
        var h = WidgetToolbarButtons.ActualHeight;
        if (w < 1 || h < 1)
        {
            WidgetToolbarButtons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            w = WidgetToolbarButtons.DesiredSize.Width;
            h = WidgetToolbarButtons.DesiredSize.Height;
        }

        return new Size(Math.Max(40, w), Math.Max(40, h));
    }

    private Rect GetToolbarRectInRoot()
    {
        if (toolbarPresence.Value < 0.05 || WidgetToolbar.Visibility != Visibility.Visible)
        {
            return Rect.Empty;
        }

        var size = GetToolbarSize();
        var local = new Rect(Canvas.GetLeft(WidgetToolbar), Canvas.GetTop(WidgetToolbar), size.Width, size.Height);
        return Inflate(OverlayRectToRoot(local), 6);
    }

    private Rect OverlayRectToRoot(Rect rect)
    {
        try
        {
            var origin = OverlayRootHost.TransformToVisual(RootGrid).TransformPoint(new Point(rect.X, rect.Y));
            return new Rect(origin.X, origin.Y, rect.Width, rect.Height);
        }
        catch
        {
            return rect;
        }
    }

    private Point ScreenToRoot(int x, int y)
    {
        var scale = Math.Max(0.25, rasterScale);
        return new Point((x - windowScreenOrigin.X) / scale, (y - windowScreenOrigin.Y) / scale);
    }

    private Vector2 RootToScreen(Rect rootRect)
    {
        var scale = Math.Max(0.25, rasterScale);
        return new Vector2(
            (float)(windowScreenOrigin.X + (rootRect.X * scale)),
            (float)(windowScreenOrigin.Y + (rootRect.Y * scale)));
    }

    private Rect GetMonitorWorkAreaInRoot(Point rootPoint)
    {
        var scale = Math.Max(0.25, rasterScale);
        try
        {
            var screenPoint = new PointInt32(
                (int)Math.Round(windowScreenOrigin.X + (rootPoint.X * scale)),
                (int)Math.Round(windowScreenOrigin.Y + (rootPoint.Y * scale)));
            var area = DisplayArea.GetFromPoint(screenPoint, DisplayAreaFallback.Nearest).WorkArea;
            return new Rect(
                (area.X - windowScreenOrigin.X) / scale,
                (area.Y - windowScreenOrigin.Y) / scale,
                area.Width / scale,
                area.Height / scale);
        }
        catch
        {
            return new Rect(0, 0, Math.Max(1, RootGrid.ActualWidth), Math.Max(1, RootGrid.ActualHeight));
        }
    }

    private static Rect Inflate(Rect rect, double amount) => rect.IsEmpty
        ? rect
        : new Rect(rect.X - amount, rect.Y - amount, rect.Width + (2 * amount), rect.Height + (2 * amount));

    private static bool Contains(Rect rect, Point point) =>
        !rect.IsEmpty &&
        point.X >= rect.X && point.X <= rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y <= rect.Y + rect.Height;

    // ------------------------------------------------------------------ spring

    /// <summary>Damped harmonic oscillator (semi-implicit Euler with fixed sub-steps).</summary>
    private struct Spring
    {
        public double Value;
        public double Velocity;
        public double Target;
        public double Stiffness;
        public double Damping;
        public readonly double Epsilon;

        public Spring(double value, double stiffness, double damping, double epsilon)
        {
            Value = value;
            Target = value;
            Velocity = 0;
            Stiffness = stiffness;
            Damping = damping;
            Epsilon = epsilon;
        }

        public void Configure(double stiffness, double damping)
        {
            Stiffness = stiffness;
            Damping = damping;
        }

        public void Snap(double value)
        {
            Value = value;
            Target = value;
            Velocity = 0;
        }

        /// <summary>Advances the spring; returns true while it is still moving.</summary>
        public bool Step(double dt)
        {
            if (Value == Target && Velocity == 0)
            {
                return false;
            }

            const double maxStep = 1.0 / 240.0;
            var steps = Math.Max(1, (int)Math.Ceiling(dt / maxStep));
            var h = dt / steps;
            for (var i = 0; i < steps; i++)
            {
                var force = (-Stiffness * (Value - Target)) - (Damping * Velocity);
                Velocity += force * h;
                Value += Velocity * h;
            }

            if (Math.Abs(Value - Target) < Epsilon && Math.Abs(Velocity) < Epsilon * 20)
            {
                Value = Target;
                Velocity = 0;
                return false;
            }

            return true;
        }
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct GlassNativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static bool GetWindowRect(nint window, out GlassNativeRect rect) => GetWindowRectNative(window, out rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    private static extern bool GetWindowRectNative(nint hWnd, out GlassNativeRect rect);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int GetClassNameW(nint hWnd, char* className, int maxCount);
}
