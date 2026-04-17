using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using FloatingLyrics.Models;
using FloatingLyrics.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace FloatingLyrics.Views;

public sealed partial class MainPage : Page
{
    private const int SettingsFlyoutWidth = 276;
    private const int SettingsFlyoutHeight = 460;
    private const int SettingsFlyoutGap = 18;
    private const int GwlExStyle = -20;
    private const nint WsExLayered = 0x00080000;
    private const nint WsExTransparent = 0x00000020;
    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const double VisualizerCanvasHeight = 118;
    private const int VisualizerPointCountMin = 96;
    private const int VisualizerPointCountMax = 320;
    private const int VisualizerPointCountDefault = 240;
    private const double VisualizerMinAmplitude = 0.9;
    private const double VisualizerMaxAmplitude = 44;
    private const float VisualizerSmoothingFallback = 0.28f;
    private const double VisualizerBarGap = 1.6;
    private const double VisualizerBarMinWidth = 2.0;
    private const double VisualizerBarCornerRatio = 0.44;
    private const double ModeTransitionYOffset = 12;
    private const double PreRollTransitionYOffset = 8;
    private const double OverlayPresenceOffsetY = 14;
    private static readonly TimeSpan OverlayHideGracePeriod = TimeSpan.FromSeconds(2.2);
    private const double LineExitPadding = 0;
    private const double PhysicalTrackClipHorizontalPadding = 384;
    private const double PhysicalTrackClipVerticalPadding = 0;
    private const double PhysicalTrackLayerHorizontalOverflow = 2048;
    private const double PhysicalTrackViewportFadeZoneDefault = 58;
    private const double CenterAlignedLaneWidthRatio = 0.74;
    private const double CenterAlignedLaneMinWidth = 360;
    private const double OverlayTextEdgePadding = 24;
    private const double OverlayContentWidthHeadroom = 140;
    private const double OverlayContentMinWidth = 360;
    private const double DragBarWidthRatio = 0.40;
    private const double DragBarMinWidth = 110;
    private const double DragBarMaxWidthRatio = 0.84;
    private const double CenterModeVisualizerMinWidth = 360;
    private const double CenterModeVisualizerMaxWidthRatio = 0.78;
    private const double CenterModeVisualizerDragScale = 1.9;
    private const double DragSnapGridStep = 32;
    private const double DragSnapAxisThreshold = 10;
    private const int VkShift = 0x10;
    private static readonly Duration OverlayGeometryAnimationDuration = new(TimeSpan.FromMilliseconds(260));
    private const double WaitingDotsTopMargin = 10;
    private const double CountdownTopMargin = 1;
    private const double TextLineHeightMultiplier = 1.22;
    private const string WaitingDotsText = "\u2022 \u2022 \u2022";
    private static readonly Duration CoverShowDuration = new(TimeSpan.FromMilliseconds(280));
    private static readonly Duration CoverHideDuration = new(TimeSpan.FromMilliseconds(210));

    private bool isDraggingWindow;
    private bool isDraggingSettingsFlyout;
    private bool isSettingsFlyoutOpen;
    private bool isWindowClickThroughEnabled;
    private bool hasSettingsFlyoutOffset;
    private uint draggingPointerId;
    private uint settingsFlyoutDraggingPointerId;
    private Point dragStartOverlayOffset;
    private Point settingsFlyoutOffset;
    private Point dragStartPointerPositionInRoot;
    private DragAxisLockMode dragAxisLockMode;
    private NativePoint dragStartSettingsCursorPosition;
    private Point dragStartSettingsOffset;
    private nint hwnd;
    private DispatcherQueueTimer? clickThroughTimer;
    private DispatcherQueueTimer? debugOverlayTimer;
    private DispatcherQueueTimer? hostTransparencyRetryTimer;
    private AppWindow? appWindow;
    private Storyboard? activeLineTransitionStoryboard;
    private Storyboard? overlayModeStoryboard;
    private Storyboard? overlayContentVisibilityStoryboard;
    private Storyboard? overlayGeometryStoryboard;
    private Storyboard? preRollInnerStoryboard;
    private Storyboard? coverPresentationStoryboard;
    private bool? overlayContentVisibleState;
    private DateTimeOffset lastOverlayPositiveSignalAtUtc = DateTimeOffset.MinValue;
    private string lastOverlayPresenceSignalReason = "init";
    private string lastOverlayVisibilityTraceState = string.Empty;
    private bool startupWindowStateApplied;
    private bool isLineTransitionRunning;
    private bool hasPendingLineTransition;
    private bool suppressManualAlignmentSelectionChanged;
    private OverlayContentMode currentOverlayMode;
    private OverlayContentMode overlayModeTransitionTarget;
    private bool isWaitingDotsStoryboardRunning;
    private bool isUpdatingOverlayGeometry;
    private string overlayGeometryTrackIdentity = string.Empty;
    private double cachedTrackLongestContentWidth = double.NaN;
    private bool visualizerLinesDirty = true;
    private string displayedCurrentLine = string.Empty;
    private string displayedNextLine = string.Empty;
    private string pendingTargetCurrentLine = string.Empty;
    private string pendingTargetNextLine = string.Empty;
    private readonly SolidColorBrush previousLineMainBrush = new();
    private readonly SolidColorBrush currentLineMainBrush = new();
    private readonly SolidColorBrush nextLineMainBrush = new();
    private readonly SolidColorBrush lineShadowBrush = new();
    private readonly SolidColorBrush currentLineShadowBrush = new();
    private readonly FontFamily currentLineFontFamily = new("Segoe UI Variable Text");
    private readonly FontFamily nextLineFontFamily = new("Segoe UI Variable Text");
    private readonly SystemAudioSpectrumAnalyzer audioSpectrumAnalyzer = new();
    private DispatcherQueueTimer? physicalTrackFadeTimer;
    private int visualizerPointCount = VisualizerPointCountDefault;
    private float[] visualizerLevels = new float[VisualizerPointCountDefault / 2];
    private float[] visualizerPeakLevels = new float[VisualizerPointCountDefault / 2];
    private readonly List<VisualizerBarVisual> visualizerBarVisuals = [];
    private string currentVisualizerSourceAppId = string.Empty;
    private Color palettePreviousColor = Color.FromArgb(255, 186, 186, 186);
    private Color paletteCurrentColor = Color.FromArgb(255, 255, 255, 255);
    private Color paletteNextColor = Color.FromArgb(255, 213, 213, 213);
    private Color paletteShadowColor = Color.FromArgb(160, 0, 0, 0);
    private DispatcherQueueTimer? visualizerTimer;
    private readonly List<PhysicalLyricItem> physicalLyricItems = [];
    private Storyboard? activePhysicalTrackStoryboard;
    private int physicalRenderedIndex = -1;
    private double physicalLineStep;
    private bool physicalTrackInitialized;
    private bool physicalTrackModeActive;
    private bool includeVisualizerLeadForCurrentTrack;
    private bool includePreRollLeadForCurrentTrack;
    private int hostTransparencyRetryCount;
    private string preRollLeadText = "• • •";
    private string physicalTrackIdentity = string.Empty;
    private Canvas? physicalTrackVisualizerCanvas;
    private Canvas? activeVisualizerCanvas;
    private PhysicalTrackPlan activePhysicalTrackPlan = new(Array.Empty<PhysicalTrackEntry>(), 0, 0, false);

    private sealed class PhysicalLyricItem
    {
        public required Grid Layer { get; init; }
        public required Grid ContentHost { get; init; }
        public required TextBlock MainText { get; init; }
        public required TextBlock ShadowText { get; init; }
        public required SolidColorBrush MainBrush { get; init; }
        public required SolidColorBrush ShadowBrush { get; init; }
        public required ScaleTransform Scale { get; init; }
        public bool IsVisualizer { get; init; }
    }

    private sealed class VisualizerBarVisual
    {
        public required Rectangle Bar { get; init; }
        public required SolidColorBrush Brush { get; init; }
        public required double FrequencyRatio { get; init; }
    }

    private readonly record struct PhysicalTrackEntry(string Text, bool IsVisualizer);

    private readonly record struct PhysicalTrackPlan(
        IReadOnlyList<PhysicalTrackEntry> Entries,
        int ActiveIndex,
        double LineBandHeight,
        bool ContainsVisualizer);

    private readonly record struct LyricsHorizontalLayout(
        LyricsTextAlignmentMode Mode,
        double ViewportWidth,
        double CenterLaneWidth,
        double TextLaneLeft,
        double TextLaneRight);

    private readonly record struct OverlayPresenceSignal(
        bool ShouldShow,
        bool HasPositiveSignal,
        string Reason);

    private enum OverlayContentMode
    {
        Lyrics,
        PreRoll,
        Visualizer
    }

    private enum DragAxisLockMode
    {
        None,
        Horizontal,
        Vertical
    }

    // Canvas.Top positions for the three lyric layers
    private double canvasCurrentTop;
    private double canvasNextTop;

    public MainViewModel ViewModel { get; }

    public MainPage(MainViewModel viewModel)
    {
        DebugTrace.Write("MainPage: ctor start");
        InitializeComponent();
        ViewModel = viewModel;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DebugTrace.Write("MainPage: ctor done");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DebugTrace.Write("MainPage: loaded start");
        RunLoadedStep("bind-viewmodel", () => ViewModel.PropertyChanged += OnViewModelPropertyChanged);
        RunLoadedStep("resolve-window", () =>
        {
            appWindow = ResolveAppWindow();
            hwnd = ResolveWindowHandle();
        });
        RunLoadedStep("attach-settings-events", AttachSettingsFlyoutEvents);
        RunLoadedStep("apply-line-layout", () =>
        {
            ApplyNextLineMargin();
            ApplyLineBrushPalette();
            InitializeDisplayedLyricsState();
            LyricsCanvas.SizeChanged += LyricsCanvas_OnSizeChanged;
            LyricVisualizerHost.SizeChanged += LyricVisualizerHost_OnSizeChanged;
            RootGrid.SizeChanged += RootGrid_OnSizeChanged;
        });
        RunLoadedStep("initialize-mode-state", () =>
        {
            UpdatePhysicalTrackMode();
            InitializeOverlayModeState();
        });
        RunLoadedStep("presence-signal", () => ProcessOverlayPresenceSignal(animated: false, trigger: "loaded"));
        RunLoadedStep("cover", ApplyCoverPresentationImmediate);
        RunLoadedStep("text-alignment", () => UpdateLyricsHorizontalAlignment());
        RunLoadedStep("host-transparency", EnsureHostTransparency);
        RunLoadedStep("click-through", () =>
        {
            StartClickThroughTimer();
            UpdateWindowClickThroughMode();
        });
        RunLoadedStep("debug-overlay", UpdateDebugOverlayState);
        RunLoadedStep("startup-window-state", () =>
        {
            if (startupWindowStateApplied)
            {
                return;
            }

            ViewModel.SetOverlayResizeDeferred(false);
            ViewModel.ApplyStartupWindowState();
            startupWindowStateApplied = true;
        });
        RunLoadedStep("apply-overlay-offset", ApplyOverlayRootOffsetFromViewModel);
        DebugTrace.Write("MainPage: loaded done");
    }

    private static void RunLoadedStepCore(string name, Action action)
    {
        action();
        DebugTrace.Write($"MainPage: loaded step ok {name}");
    }

    private void RunLoadedStep(string name, Action action)
    {
        try
        {
            RunLoadedStepCore(name, action);
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"MainPage: loaded step fail {name}: {ex}");
        }
    }

    private void EnsureHostTransparency()
    {
        ApplyTransparentBackgroundToVisualHost();
        StartHostTransparencyRetryTimer();
    }

    private void StartHostTransparencyRetryTimer()
    {
        if (DispatcherQueue is null)
        {
            return;
        }

        hostTransparencyRetryCount = 0;
        hostTransparencyRetryTimer ??= DispatcherQueue.CreateTimer();
        hostTransparencyRetryTimer.Interval = TimeSpan.FromMilliseconds(180);
        hostTransparencyRetryTimer.IsRepeating = true;
        hostTransparencyRetryTimer.Tick -= OnHostTransparencyRetryTick;
        hostTransparencyRetryTimer.Tick += OnHostTransparencyRetryTick;
        if (!hostTransparencyRetryTimer.IsRunning)
        {
            hostTransparencyRetryTimer.Start();
        }
    }

    private void StopHostTransparencyRetryTimer()
    {
        if (hostTransparencyRetryTimer is null)
        {
            return;
        }

        if (hostTransparencyRetryTimer.IsRunning)
        {
            hostTransparencyRetryTimer.Stop();
        }

        hostTransparencyRetryTimer.Tick -= OnHostTransparencyRetryTick;
        hostTransparencyRetryTimer = null;
    }

    private void OnHostTransparencyRetryTick(DispatcherQueueTimer sender, object args)
    {
        ApplyTransparentBackgroundToVisualHost();
        hostTransparencyRetryCount++;
        if (hostTransparencyRetryCount >= 16)
        {
            sender.Stop();
        }
    }

    private void ApplyTransparentBackgroundToVisualHost()
    {
        try
        {
            var touched = 0;
            DependencyObject? current = this;
            while (current is not null)
            {
                if (TryClearVisualBackground(current))
                {
                    touched++;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            DebugTrace.Write($"HostTransparency: touched={touched}");
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"HostTransparency: failed {ex.Message}");
        }
    }

    private static bool TryClearVisualBackground(DependencyObject node)
    {
        var touched = false;

        if (node is Panel panel && !IsTransparentBrush(panel.Background))
        {
            panel.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            touched = true;
        }

        if (node is Border border && !IsTransparentBrush(border.Background))
        {
            border.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            touched = true;
        }

        if (node is Control control && !IsTransparentBrush(control.Background))
        {
            control.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            touched = true;
        }

        return touched;
    }

    private static bool IsTransparentBrush(Brush? brush)
    {
        return brush switch
        {
            null => true,
            SolidColorBrush solid => solid.Color.A == 0,
            _ => false
        };
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        startupWindowStateApplied = false;
        DetachSettingsFlyoutEvents();
        EndSettingsFlyoutDrag();
        ViewModel.SetOverlayResizeDeferred(false);
        StopActiveLineTransition();
        isLineTransitionRunning = false;
        hasPendingLineTransition = false;
        StopOverlayModeTransition();
        StopOverlayContentVisibilityTransition();
        StopOverlayGeometryAnimation();
        StopPreRollInnerTransition();
        StopCoverPresentationAnimation();
        StopWaitingDotsAnimation();
        StopActivePhysicalTrackTransition();
        StopPhysicalTrackFadeTimer();
        physicalLyricItems.Clear();
        LyricsTrackSurface.Children.Clear();
        activePhysicalTrackPlan = new PhysicalTrackPlan(Array.Empty<PhysicalTrackEntry>(), 0, 0, false);
        physicalTrackVisualizerCanvas = null;
        activeVisualizerCanvas = null;
        LyricsCanvas.SizeChanged -= LyricsCanvas_OnSizeChanged;
        RootGrid.SizeChanged -= RootGrid_OnSizeChanged;
        StopDebugOverlayTimer();
        StopHostTransparencyRetryTimer();
        StopClickThroughTimer();
        SetWindowClickThrough(false);
        LyricVisualizerHost.SizeChanged -= LyricVisualizerHost_OnSizeChanged;
        StopVisualizerAnimation();
        if (physicalTrackFadeTimer is not null)
        {
            physicalTrackFadeTimer.Tick -= OnPhysicalTrackFadeTimerTick;
            physicalTrackFadeTimer = null;
        }

        if (visualizerTimer is not null)
        {
            visualizerTimer.Tick -= OnVisualizerTimerTick;
            visualizerTimer = null;
        }

        audioSpectrumAnalyzer.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LineAnimationVersion))
        {
            _ = DispatcherQueue.TryEnqueue(QueueLineTransition);
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.HasActiveSession) or
            nameof(MainViewModel.IsBusy) or
            nameof(MainViewModel.IsPlaybackVisible) or
            nameof(MainViewModel.CurrentLine) or
            nameof(MainViewModel.NextLine) or
            nameof(MainViewModel.PreRollCountdownText) or
            nameof(MainViewModel.ShowLyricVisualizer) or
            nameof(MainViewModel.IsPreRollActive) or
            nameof(MainViewModel.IsWaitingDotsActive) or
            nameof(MainViewModel.IsCountdownActive))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                ProcessOverlayPresenceSignal(animated: true, e.PropertyName ?? "<null>");
                UpdateLyricsHorizontalAlignment();
            });
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.TrackTitle) or
            nameof(MainViewModel.ArtistName))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                ProcessOverlayPresenceSignal(animated: true, e.PropertyName ?? "<null>");
                InvalidateOverlayGeometryCache();
                UpdateLyricsHorizontalAlignment(animateGeometry: true, forceGeometryRecompute: true);
            });
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.NextLineTopMargin))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (ShouldUsePhysicalTrack())
                {
                    RebuildPhysicalTrack(forceImmediate: true);
                }
                else
                {
                    ApplyNextLineMargin();
                }

                UpdateDebugOverlayInfo();
            });
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.CurrentLineFontSize) or
            nameof(MainViewModel.NextLineFontSize))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                InvalidateOverlayGeometryCache();
                if (ShouldUsePhysicalTrack())
                {
                    RebuildPhysicalTrack(forceImmediate: true);
                }
                else if (!isLineTransitionRunning)
                {
                    ApplyRestingLineLayerVisuals();
                }
                else
                {
                    SetLineLayerHeights(GetCurrentLineHeight(), GetNextLineHeight());
                    ApplyTransitionLineLayerStyles();
                }

                UpdateLyricsHorizontalAlignment(forceGeometryRecompute: true);
                UpdateDebugOverlayInfo();
            });
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.TitleFontSize) or
            nameof(MainViewModel.ArtistFontSize) or
            nameof(MainViewModel.CoverVisibility) or
            nameof(MainViewModel.UseCenterTopCoverLayout) or
            nameof(MainViewModel.CenterAlignmentCoverSize))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                InvalidateOverlayGeometryCache();
                UpdateLyricsHorizontalAlignment(forceGeometryRecompute: true);
            });
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.PreviousLineBrush) or
            nameof(MainViewModel.CurrentLineBrush) or
            nameof(MainViewModel.NextLineBrush) or
            nameof(MainViewModel.TextShadowBrush))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                ApplyLineBrushPalette();
                if (ShouldUsePhysicalTrack())
                {
                    ApplyPhysicalTrackFinalState(GetActivePhysicalPlanIndex());
                }
            });
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.ShowLyricVisualizer) or
            nameof(MainViewModel.VisualizerVisibility) or
            nameof(MainViewModel.IsPreRollActive) or
            nameof(MainViewModel.PreRollVisibility) or
            nameof(MainViewModel.LyricsVisibility))
        {
            _ = DispatcherQueue.TryEnqueue(() => ProcessOverlayPresenceSignal(animated: true, e.PropertyName ?? "<null>"));
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.TimelineLines) or
            nameof(MainViewModel.HasSyncedTimeline))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                InvalidateOverlayGeometryCache();
                if (ShouldUsePhysicalTrack())
                {
                    UpdatePhysicalTrackMode();
                    StartPhysicalTrackTransition(forceImmediate: false);
                }
                else
                {
                    UpdatePhysicalTrackMode();
                }

                UpdateLyricsHorizontalAlignment(animateGeometry: true, forceGeometryRecompute: true);
            });
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.IsWaitingDotsActive) or
            nameof(MainViewModel.IsCountdownActive) or
            nameof(MainViewModel.WaitingDotsVisibility) or
            nameof(MainViewModel.CountdownVisibility))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (physicalTrackModeActive)
                {
                    StartPhysicalTrackTransition(forceImmediate: false);
                }
                else
                {
                    UpdatePreRollInnerState(animated: true);
                }
            });
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.ShowDebugHitboxes) or
            nameof(MainViewModel.ShowRuntimeDebugPanel))
        {
            _ = DispatcherQueue.TryEnqueue(UpdateDebugOverlayState);
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.OscilloscopeDetail) or
            nameof(MainViewModel.OscilloscopeMirrorBars))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                EnsureVisualizerBuffers();
                visualizerLinesDirty = true;
                EnsureVisualizerLines();
            });
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.AudioSourceAppId) or nameof(MainViewModel.AppScopedAudioCapture))
        {
            _ = DispatcherQueue.TryEnqueue(UpdateVisualizerCaptureSource);
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.ViewportFadeZone) or
            nameof(MainViewModel.ViewportFadeSoftness))
        {
            _ = DispatcherQueue.TryEnqueue(UpdatePhysicalTrackViewportFade);
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.LyricsTextAlignment) or
            nameof(MainViewModel.AutoLyricsTextAlignment) or
            nameof(MainViewModel.SelectedPositionPreset) or
            nameof(MainViewModel.SelectedCustomPositionPreset))
        {
            _ = DispatcherQueue.TryEnqueue(() => UpdateLyricsHorizontalAlignment(forceGeometryRecompute: true));
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.OverlayContentOffsetX) or
            nameof(MainViewModel.OverlayContentOffsetY))
        {
            _ = DispatcherQueue.TryEnqueue(ApplyOverlayRootOffsetFromViewModel);
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.CoverOpacity) or
            nameof(MainViewModel.CoverSize) or
            nameof(MainViewModel.CoverColumnSpacing))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                InvalidateOverlayGeometryCache();
                AnimateCoverPresentation();
                UpdateLyricsHorizontalAlignment(animateGeometry: true, forceGeometryRecompute: true);
            });
            return;
        }

        if (e.PropertyName != nameof(MainViewModel.ShowDragHandle) &&
            e.PropertyName != nameof(MainViewModel.ClickThroughMode))
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(UpdateWindowClickThroughMode);
    }

    private void RootGrid_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateOverlayGeometry();
        var snapped = SnapOverlayRootOffsetToGrid(new Point(OverlayRootHostTranslate.X, OverlayRootHostTranslate.Y));
        var clamped = ClampOverlayRootOffset(snapped);
        ApplyOverlayRootOffset(clamped, persist: false);
        if (DragSnapOverlay.Visibility == Visibility.Visible)
        {
            RebuildDragSnapGridOverlay();
        }
    }

    private void OverlayRootHost_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Avoid cumulative X drift when width changes repeatedly (for example while
        // toggling text alignment Left/Center/Right). We do not compensate by delta
        // width here; we only keep the current offset inside safe bounds.
        var snapped = SnapOverlayRootOffsetToGrid(new Point(OverlayRootHostTranslate.X, OverlayRootHostTranslate.Y));
        var clamped = ClampOverlayRootOffset(snapped);
        ApplyOverlayRootOffset(clamped, persist: false);
    }

    private void ApplyOverlayRootOffsetFromViewModel()
    {
        var snapped = SnapOverlayRootOffsetToGrid(new Point(
            ViewModel.OverlayContentOffsetX,
            ViewModel.OverlayContentOffsetY));
        var clamped = ClampOverlayRootOffset(snapped);
        ApplyOverlayRootOffset(clamped, persist: false);
    }

    private void ApplyOverlayRootOffset(Point offset, bool persist)
    {
        OverlayRootHostTranslate.X = offset.X;
        OverlayRootHostTranslate.Y = offset.Y;
        if (persist)
        {
            ViewModel.SetOverlayContentOffset(offset.X, offset.Y, persist: true);
        }
    }

    private Point ClampOverlayRootOffset(Point raw)
    {
        var minVisibleDragHandle = Math.Clamp(Math.Max(24, DragHandle.ActualWidth * 0.35), 24, 180);
        var viewportWidth = Math.Max(1, RootGrid.ActualWidth);
        var viewportHeight = Math.Max(1, RootGrid.ActualHeight);
        var hostWidth = Math.Max(minVisibleDragHandle, OverlayRootHost.ActualWidth);
        var hostHeight = Math.Max(minVisibleDragHandle, OverlayRootHost.ActualHeight);
        var margin = OverlayRootHost.Margin;

        // At least drag-handle area remains reachable.
        var minX = minVisibleDragHandle - (margin.Left + hostWidth);
        var maxX = viewportWidth - minVisibleDragHandle - margin.Left;
        var minY = minVisibleDragHandle - (margin.Top + hostHeight);
        var maxY = viewportHeight - minVisibleDragHandle - margin.Top;

        if (minX > maxX)
        {
            var centerX = (minX + maxX) * 0.5;
            minX = centerX;
            maxX = centerX;
        }

        if (minY > maxY)
        {
            var centerY = (minY + maxY) * 0.5;
            minY = centerY;
            maxY = centerY;
        }

        return new Point(
            Math.Clamp(raw.X, minX, maxX),
            Math.Clamp(raw.Y, minY, maxY));
    }

    private Point SnapOverlayRootOffsetToGrid(Point raw)
    {
        var step = Math.Max(1, DragSnapGridStep);
        var margin = OverlayRootHost.Margin;
        var snappedX = (Math.Round((raw.X + margin.Left) / step) * step) - margin.Left;
        var snappedY = (Math.Round((raw.Y + margin.Top) / step) * step) - margin.Top;
        return new Point(snappedX, snappedY);
    }

    private void UpdateLyricsHorizontalAlignment(bool animateGeometry = false, bool forceGeometryRecompute = false)
    {
        try
        {
            UpdateOverlayGeometry(forceTrackRecompute: forceGeometryRecompute, animateWidthChange: animateGeometry);
            ApplyCurrentHorizontalLayoutSnapshot();
        }
        catch (Exception ex)
        {
            // Prevent UI-thread termination on rare transient size states.
            DebugTrace.Write($"UpdateLyricsHorizontalAlignment failed: {ex.Message}");
        }
    }

    private void ApplyCurrentHorizontalLayoutSnapshot()
    {
        var layout = GetLyricsHorizontalLayout();
        ApplyTrackMetaAlignment(layout);
        ApplyLegacyLyricsAlignment(layout);
        ApplyPhysicalTrackAlignment(layout);
        ApplyPreRollAlignment(layout);
    }

    private LyricsHorizontalLayout GetLyricsHorizontalLayout()
    {
        var overlayMargin = OverlayRootHost.Margin;
        var rootUsableWidth = Math.Max(1, RootGrid.ActualWidth - overlayMargin.Left - overlayMargin.Right);
        var textBlockHostWidth = OverlayTextBlockHost.ActualWidth > 1
            ? OverlayTextBlockHost.ActualWidth
            : OverlayContentHost.ActualWidth;
        var viewportWidth = Math.Max(
            1,
            LyricsCanvas.ActualWidth > 1
                ? LyricsCanvas.ActualWidth
                : Math.Max(textBlockHostWidth, rootUsableWidth));
        if (!double.IsFinite(viewportWidth))
        {
            viewportWidth = 1;
        }

        var mode = ResolveEffectiveLyricsTextAlignmentMode();
        var laneTargetWidth = viewportWidth * CenterAlignedLaneWidthRatio;
        var centerLaneMinWidth = Math.Min(CenterAlignedLaneMinWidth, viewportWidth);
        var centerLaneMaxWidth = Math.Max(centerLaneMinWidth, viewportWidth);
        var centerLaneWidth = Math.Max(centerLaneMinWidth, Math.Min(laneTargetWidth, centerLaneMaxWidth));

        var textLaneLeft = mode == LyricsTextAlignmentMode.Center ? OverlayTextEdgePadding : 0;
        var textLaneRight = mode == LyricsTextAlignmentMode.Center
            ? Math.Max(textLaneLeft + 1, viewportWidth - OverlayTextEdgePadding)
            : Math.Max(1, viewportWidth);
        if (mode is LyricsTextAlignmentMode.Left or LyricsTextAlignmentMode.Right)
        {
            var showCover = ViewModel.CoverVisibility == Visibility.Visible;
            if (showCover)
            {
                var coverWidth = Math.Max(CoverArtShell.ActualWidth, ViewModel.CoverSize);
                if (!double.IsFinite(coverWidth) || coverWidth < 0)
                {
                    coverWidth = 0;
                }

                var coverInset = Math.Max(0, coverWidth) + Math.Max(0, TrackMetaGrid.ColumnSpacing);
                textLaneLeft = Math.Min(textLaneRight - 1, textLaneLeft + coverInset);
            }
        }

        return new LyricsHorizontalLayout(mode, viewportWidth, centerLaneWidth, textLaneLeft, textLaneRight);
    }

    private LyricsTextAlignmentMode ResolveEffectiveLyricsTextAlignmentMode()
    {
        if (!ViewModel.AutoLyricsTextAlignment)
        {
            return ViewModel.LyricsTextAlignment;
        }

        try
        {
            appWindow ??= ResolveAppWindow();
            if (appWindow is null)
            {
                return ViewModel.LyricsTextAlignment;
            }

            var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;
            if (workArea.Width <= 1)
            {
                return ViewModel.LyricsTextAlignment;
            }

            var windowCenterX = appWindow.Position.X + (appWindow.Size.Width / 2.0);
            var relativeX = (windowCenterX - workArea.X) / workArea.Width;
            if (relativeX <= 0.34)
            {
                return LyricsTextAlignmentMode.Left;
            }

            if (relativeX >= 0.66)
            {
                return LyricsTextAlignmentMode.Right;
            }

            return LyricsTextAlignmentMode.Center;
        }
        catch
        {
            return ViewModel.LyricsTextAlignment;
        }
    }

    private void ApplyTrackMetaAlignment(LyricsHorizontalLayout layout)
    {
        var horizontalAlignment = layout.Mode switch
        {
            LyricsTextAlignmentMode.Center => HorizontalAlignment.Center,
            LyricsTextAlignmentMode.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left
        };

        var showCover = ViewModel.CoverVisibility == Visibility.Visible;
        var placeCoverOnTop =
            showCover &&
            layout.Mode == LyricsTextAlignmentMode.Center &&
            ViewModel.UseCenterTopCoverLayout;

        if (!showCover)
        {
            TrackMetaGrid.RowSpacing = 0;

            Grid.SetColumn(TrackMetaTextHost, 0);
            Grid.SetColumnSpan(TrackMetaTextHost, 2);
            Grid.SetRow(TrackMetaTextHost, 0);
            Grid.SetRowSpan(TrackMetaTextHost, 2);
        }
        else if (placeCoverOnTop)
        {
            TrackMetaGrid.RowSpacing = 10;

            Grid.SetColumn(CoverArtShell, 0);
            Grid.SetColumnSpan(CoverArtShell, 2);
            Grid.SetRow(CoverArtShell, 0);
            Grid.SetRowSpan(CoverArtShell, 1);
            CoverArtShell.HorizontalAlignment = HorizontalAlignment.Center;
            CoverArtShell.VerticalAlignment = VerticalAlignment.Top;

            Grid.SetColumn(TrackMetaTextHost, 0);
            Grid.SetColumnSpan(TrackMetaTextHost, 2);
            Grid.SetRow(TrackMetaTextHost, 1);
            Grid.SetRowSpan(TrackMetaTextHost, 1);
        }
        else
        {
            TrackMetaGrid.RowSpacing = 0;

            Grid.SetColumn(CoverArtShell, 0);
            Grid.SetColumnSpan(CoverArtShell, 1);
            Grid.SetRow(CoverArtShell, 0);
            Grid.SetRowSpan(CoverArtShell, 2);
            CoverArtShell.HorizontalAlignment = HorizontalAlignment.Left;
            CoverArtShell.VerticalAlignment = VerticalAlignment.Top;

            Grid.SetColumn(TrackMetaTextHost, 1);
            Grid.SetColumnSpan(TrackMetaTextHost, 1);
            Grid.SetRow(TrackMetaTextHost, 0);
            Grid.SetRowSpan(TrackMetaTextHost, 2);
        }

        TrackMetaGrid.Width = Math.Max(120, layout.ViewportWidth);
        TrackMetaTextHost.HorizontalAlignment = horizontalAlignment;
        TrackMetaTextHost.MaxWidth = Math.Max(120, layout.ViewportWidth - (OverlayTextEdgePadding * 2));
        var unsyncedReserve = ViewModel.UnsyncedIndicatorVisibility == Visibility.Visible ? 34 : 0;
        var titleMaxWidth = Math.Max(120, TrackMetaTextHost.MaxWidth - unsyncedReserve);
        if (layout.Mode == LyricsTextAlignmentMode.Center)
        {
            TrackTitleRow.HorizontalAlignment = HorizontalAlignment.Center;
            TrackTitleRow.Width = double.NaN;
            TrackTitleText.MaxWidth = titleMaxWidth;
        }
        else
        {
            TrackTitleRow.HorizontalAlignment = horizontalAlignment;
            TrackTitleRow.Width = double.NaN;
            TrackTitleText.MaxWidth = titleMaxWidth;
        }
        ArtistNameText.MaxWidth = titleMaxWidth;
        TrackTitleText.TextAlignment = layout.Mode switch
        {
            LyricsTextAlignmentMode.Center => TextAlignment.Center,
            LyricsTextAlignmentMode.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };
        ArtistNameText.TextAlignment = TrackTitleText.TextAlignment;
    }

    private void ApplyLegacyLyricsAlignment(LyricsHorizontalLayout layout)
    {
        ApplyTextPlacement(CurrentLineText, CurrentLineShadowText, layout);
        ApplyTextPlacement(NextLineText, NextLineShadowText, layout);
        ApplyTextPlacement(PreviousLineText, PreviousLineShadowText, layout);
    }

    private void ApplyPhysicalTrackAlignment(LyricsHorizontalLayout layout)
    {
        foreach (var item in physicalLyricItems)
        {
            if (item.IsVisualizer)
            {
                continue;
            }

            ApplyTextPlacement(item.MainText, item.ShadowText, layout);
        }
    }

    private void ApplyPreRollAlignment(LyricsHorizontalLayout layout)
    {
        CountdownTextLayer.TextAlignment = layout.Mode switch
        {
            LyricsTextAlignmentMode.Center => TextAlignment.Center,
            LyricsTextAlignmentMode.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };

        if (layout.Mode == LyricsTextAlignmentMode.Center)
        {
            // Fixed center anchor for center mode: no width-based X recalc per text change.
            WaitingDotsLayer.HorizontalAlignment = HorizontalAlignment.Center;
            WaitingDotsLayer.Margin = new Thickness(0, WaitingDotsTopMargin, 0, 0);

            CountdownTextLayer.HorizontalAlignment = HorizontalAlignment.Center;
            CountdownTextLayer.Margin = new Thickness(0, CountdownTopMargin, 0, 0);
            return;
        }

        WaitingDotsLayer.HorizontalAlignment = HorizontalAlignment.Left;
        WaitingDotsLayer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var dotsWidth = Math.Max(WaitingDotsLayer.ActualWidth, WaitingDotsLayer.DesiredSize.Width);
        var dotsX = ComputeAlignedTextOffset(layout, dotsWidth);
        WaitingDotsLayer.Margin = new Thickness(dotsX + 1, WaitingDotsTopMargin, 0, 0);

        CountdownTextLayer.HorizontalAlignment = HorizontalAlignment.Left;
        CountdownTextLayer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var countdownWidth = Math.Max(CountdownTextLayer.ActualWidth, CountdownTextLayer.DesiredSize.Width);
        var countdownX = ComputeAlignedTextOffset(layout, countdownWidth);
        CountdownTextLayer.Margin = new Thickness(countdownX + 1, CountdownTopMargin, 0, 0);
    }

    private void UpdateOverlayGeometry(bool forceTrackRecompute = false, bool animateWidthChange = false)
    {
        if (isUpdatingOverlayGeometry)
        {
            return;
        }

        var margin = OverlayRootHost.Margin;
        var availableWidth = RootGrid.ActualWidth - margin.Left - margin.Right;
        if (!double.IsFinite(availableWidth) || availableWidth <= 24)
        {
            return;
        }

        isUpdatingOverlayGeometry = true;
        try
        {
            var effectiveMode = ResolveEffectiveLyricsTextAlignmentMode();
            var centerAnchorMode = effectiveMode == LyricsTextAlignmentMode.Center;

            double contentTargetWidth;
            double dragTargetWidth;
            double visualizerTargetWidth;

            if (centerAnchorMode)
            {
                // In center mode use a fixed center anchor width (viewport-wide),
                // so geometry does not drift when track/text changes.
                contentTargetWidth = Math.Max(OverlayContentMinWidth, availableWidth);
                dragTargetWidth = Math.Clamp(
                    ResolveElementWidth(DragHandle, Math.Max(DragBarMinWidth, DragHandle.Width)),
                    DragBarMinWidth,
                    Math.Max(DragBarMinWidth, contentTargetWidth * DragBarMaxWidthRatio));
                var visualizerMaxWidth = Math.Max(
                    CenterModeVisualizerMinWidth,
                    availableWidth * CenterModeVisualizerMaxWidthRatio);
                visualizerTargetWidth = Math.Clamp(
                    dragTargetWidth * CenterModeVisualizerDragScale,
                    CenterModeVisualizerMinWidth,
                    visualizerMaxWidth);
            }
            else
            {
            var trackIdentity = BuildOverlayGeometryTrackIdentity();
            var shouldRecomputeTrackWidth =
                forceTrackRecompute ||
                !double.IsFinite(cachedTrackLongestContentWidth) ||
                !string.Equals(overlayGeometryTrackIdentity, trackIdentity, StringComparison.Ordinal);

            if (shouldRecomputeTrackWidth)
            {
                cachedTrackLongestContentWidth = MeasureOverlayLongestContentWidth();
                overlayGeometryTrackIdentity = trackIdentity;
            }

            var longestContentWidth = Math.Max(OverlayContentMinWidth, cachedTrackLongestContentWidth);
            contentTargetWidth = Math.Clamp(
                longestContentWidth + OverlayContentWidthHeadroom,
                OverlayContentMinWidth,
                availableWidth);

            dragTargetWidth = Math.Clamp(
                longestContentWidth * DragBarWidthRatio,
                DragBarMinWidth,
                Math.Max(DragBarMinWidth, contentTargetWidth * DragBarMaxWidthRatio));

            visualizerTargetWidth = contentTargetWidth;
            }

            if (animateWidthChange)
            {
                AnimateOverlayGeometry(contentTargetWidth, dragTargetWidth, visualizerTargetWidth);
            }
            else
            {
                ApplyOverlayGeometry(contentTargetWidth, dragTargetWidth, visualizerTargetWidth);
            }
        }
        finally
        {
            isUpdatingOverlayGeometry = false;
        }
    }

    private void InvalidateOverlayGeometryCache()
    {
        overlayGeometryTrackIdentity = string.Empty;
        cachedTrackLongestContentWidth = double.NaN;
    }

    private string BuildOverlayGeometryTrackIdentity()
    {
        var timelineCount = ViewModel.HasSyncedTimeline ? ViewModel.TimelineLines.Count : 0;
        return string.Concat(
            ViewModel.AudioSourceAppId ?? string.Empty,
            "\u001F",
            ViewModel.TrackTitle ?? string.Empty,
            "\u001F",
            ViewModel.ArtistName ?? string.Empty,
            "\u001F",
            ViewModel.HasSyncedTimeline ? "1" : "0",
            "\u001F",
            timelineCount.ToString(CultureInfo.InvariantCulture),
            "\u001F",
            ViewModel.CoverVisibility == Visibility.Visible ? "1" : "0",
            "\u001F",
            ViewModel.UseCenterTopCoverLayout ? "1" : "0");
    }

    private void ApplyOverlayGeometry(double contentWidth, double dragWidth, double visualizerWidth)
    {
        StopOverlayGeometryAnimation();
        SetElementWidthIfNeeded(OverlayTextBlockHost, contentWidth);
        SetElementWidthIfNeeded(OverlayContentHost, contentWidth);
        SetElementWidthIfNeeded(TrackMetaGrid, contentWidth);
        SetElementWidthIfNeeded(LyricVisualizerHost, visualizerWidth);
        SetElementWidthIfNeeded(LyricsCanvas, contentWidth);
        SetElementWidthIfNeeded(PreRollHost, contentWidth);
        SetElementWidthIfNeeded(DragHandle, dragWidth);
        ApplyCurrentHorizontalLayoutSnapshot();
    }

    private void AnimateOverlayGeometry(double contentWidth, double dragWidth, double visualizerWidth)
    {
        var contentDelta = Math.Abs(ResolveElementWidth(OverlayContentHost, contentWidth) - contentWidth);
        var dragDelta = Math.Abs(ResolveElementWidth(DragHandle, dragWidth) - dragWidth);
        var visualizerDelta = Math.Abs(ResolveElementWidth(LyricVisualizerHost, visualizerWidth) - visualizerWidth);
        if (contentDelta < 0.75 && dragDelta < 0.75 && visualizerDelta < 0.75)
        {
            ApplyOverlayGeometry(contentWidth, dragWidth, visualizerWidth);
            return;
        }

        StopOverlayGeometryAnimation();
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();
        AddWidthAnim(storyboard, OverlayTextBlockHost, contentWidth, OverlayGeometryAnimationDuration, easing);
        AddWidthAnim(storyboard, OverlayContentHost, contentWidth, OverlayGeometryAnimationDuration, easing);
        AddWidthAnim(storyboard, TrackMetaGrid, contentWidth, OverlayGeometryAnimationDuration, easing);
        AddWidthAnim(storyboard, LyricVisualizerHost, visualizerWidth, OverlayGeometryAnimationDuration, easing);
        AddWidthAnim(storyboard, LyricsCanvas, contentWidth, OverlayGeometryAnimationDuration, easing);
        AddWidthAnim(storyboard, PreRollHost, contentWidth, OverlayGeometryAnimationDuration, easing);
        AddWidthAnim(storyboard, DragHandle, dragWidth, OverlayGeometryAnimationDuration, easing);

        if (storyboard.Children.Count == 0)
        {
            ApplyOverlayGeometry(contentWidth, dragWidth, visualizerWidth);
            return;
        }

        overlayGeometryStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            overlayGeometryStoryboard = null;
            ApplyOverlayGeometry(contentWidth, dragWidth, visualizerWidth);
        };
        storyboard.Begin();
    }

    private void StopOverlayGeometryAnimation()
    {
        if (overlayGeometryStoryboard is null)
        {
            return;
        }

        overlayGeometryStoryboard.Stop();
        overlayGeometryStoryboard = null;
    }

    private static void AddWidthAnim(
        Storyboard storyboard,
        FrameworkElement target,
        double toWidth,
        Duration duration,
        EasingFunctionBase easing)
    {
        var fromWidth = ResolveElementWidth(target, toWidth);
        if (Math.Abs(fromWidth - toWidth) <= 0.5)
        {
            target.Width = toWidth;
            return;
        }

        target.Width = fromWidth;
        var anim = new DoubleAnimation
        {
            From = fromWidth,
            To = toWidth,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, nameof(FrameworkElement.Width));
        storyboard.Children.Add(anim);
    }

    private static double ResolveElementWidth(FrameworkElement element, double fallback)
    {
        if (double.IsFinite(element.Width) && element.Width > 0)
        {
            return element.Width;
        }

        if (double.IsFinite(element.ActualWidth) && element.ActualWidth > 0)
        {
            return element.ActualWidth;
        }

        return fallback;
    }

    private double MeasureOverlayLongestContentWidth()
    {
        var longest = 0d;
        var titleWidth = MeasureTextBlockNaturalWidth(TrackTitleText);
        var artistWidth = MeasureTextBlockNaturalWidth(ArtistNameText);
        var unsyncedWidth = ViewModel.UnsyncedIndicatorVisibility == Visibility.Visible ? 27 : 0;
        var metaTextWidth = Math.Max(titleWidth + unsyncedWidth + 8, artistWidth);
        if (ViewModel.CoverVisibility == Visibility.Visible && !ViewModel.UseCenterTopCoverLayout)
        {
            var coverWidth = Math.Max(CoverArtShell.ActualWidth, ViewModel.CoverSize);
            metaTextWidth += coverWidth + Math.Max(TrackMetaGrid.ColumnSpacing, 0);
        }

        UpdateLongest(ref longest, metaTextWidth);
        UpdateLongest(ref longest, MeasureTimelineLongestWidth());
        UpdateLongest(ref longest, MeasureTextBlockNaturalWidth(CurrentLineText));
        UpdateLongest(ref longest, MeasureTextBlockNaturalWidth(NextLineText));
        UpdateLongest(ref longest, MeasureTextBlockNaturalWidth(PreviousLineText));
        UpdateLongest(ref longest, MeasureElementWidth(CountdownTextLayer));
        UpdateLongest(ref longest, MeasureElementWidth(WaitingDotsLayer));

        if (physicalTrackModeActive && physicalLyricItems.Count > 0)
        {
            var activeIndex = GetActivePhysicalPlanIndex();
            if (activeIndex >= 0 && activeIndex < physicalLyricItems.Count)
            {
                UpdateLongest(ref longest, MeasureTextBlockNaturalWidth(physicalLyricItems[activeIndex].MainText));
            }

            var nextIndex = activeIndex + 1;
            if (nextIndex >= 0 && nextIndex < physicalLyricItems.Count)
            {
                UpdateLongest(ref longest, MeasureTextBlockNaturalWidth(physicalLyricItems[nextIndex].MainText));
            }
        }

        if (longest < 1)
        {
            var fallbackHostWidth = Math.Max(OverlayTextBlockHost.ActualWidth, OverlayContentHost.ActualWidth);
            longest = Math.Max(OverlayContentMinWidth, fallbackHostWidth - OverlayContentWidthHeadroom);
        }

        return Math.Max(OverlayContentMinWidth, longest);
    }

    private double MeasureTimelineLongestWidth()
    {
        if (!ViewModel.HasSyncedTimeline || ViewModel.TimelineLines.Count == 0)
        {
            return 0;
        }

        var probe = new TextBlock
        {
            FontFamily = currentLineFontFamily,
            FontWeight = FontWeights.SemiBold,
            FontSize = ViewModel.CurrentLineFontSize,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None
        };

        var longest = 0d;
        foreach (var line in ViewModel.TimelineLines)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            probe.Text = line.Text;
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var measured = Math.Max(probe.DesiredSize.Width, probe.ActualWidth);
            if (double.IsFinite(measured) && measured > longest)
            {
                longest = measured;
            }
        }

        return longest;
    }

    private static void SetElementWidthIfNeeded(FrameworkElement element, double width)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            return;
        }

        if (Math.Abs(element.Width - width) > 0.5)
        {
            element.Width = width;
        }
    }

    private static double MeasureElementWidth(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max(element.ActualWidth, element.DesiredSize.Width);
        return double.IsFinite(width) ? width : 0;
    }

    private static double MeasureTextBlockNaturalWidth(TextBlock text)
    {
        var originalMaxWidth = text.MaxWidth;
        var originalWidth = text.Width;
        try
        {
            text.MaxWidth = double.PositiveInfinity;
            text.Width = double.NaN;
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = Math.Max(text.DesiredSize.Width, text.ActualWidth);
            return double.IsFinite(width) ? width : 0;
        }
        finally
        {
            text.MaxWidth = originalMaxWidth;
            text.Width = originalWidth;
        }
    }

    private static void UpdateLongest(ref double currentMax, double candidate)
    {
        if (candidate > currentMax)
        {
            currentMax = candidate;
        }
    }


    private void ApplyTextPlacement(TextBlock mainText, TextBlock shadowText, LyricsHorizontalLayout layout)
    {
        var textAlignment = layout.Mode switch
        {
            LyricsTextAlignmentMode.Center => TextAlignment.Center,
            LyricsTextAlignmentMode.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };

        mainText.TextAlignment = textAlignment;
        shadowText.TextAlignment = textAlignment;
        mainText.HorizontalAlignment = HorizontalAlignment.Left;
        shadowText.HorizontalAlignment = HorizontalAlignment.Left;

        if (layout.Mode == LyricsTextAlignmentMode.Center)
        {
            // Fixed center anchor for center mode.
            var usableWidth = Math.Max(1, layout.ViewportWidth - (OverlayTextEdgePadding * 2));
            var anchorX = OverlayTextEdgePadding;

            mainText.Width = usableWidth;
            shadowText.Width = usableWidth;
            mainText.Margin = new Thickness(anchorX, 0, 0, 0);
            shadowText.Margin = new Thickness(anchorX + 1, 1, 0, 0);
            return;
        }

        mainText.Width = double.NaN;
        shadowText.Width = double.NaN;
        mainText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = Math.Max(mainText.ActualWidth, mainText.DesiredSize.Width);
        var x = ComputeAlignedTextOffset(layout, textWidth);
        mainText.Margin = new Thickness(x, 0, 0, 0);
        shadowText.Margin = new Thickness(x + 1, 1, 0, 0);
    }

    private double ComputeAlignedTextOffset(LyricsHorizontalLayout layout, double textWidth)
    {
        textWidth = Math.Max(0, textWidth);
        var laneLeft = Math.Clamp(layout.TextLaneLeft, 0, Math.Max(0, layout.ViewportWidth - 1));
        var laneRight = Math.Max(laneLeft + 1, Math.Min(layout.ViewportWidth, layout.TextLaneRight));
        var usableWidth = Math.Max(1, laneRight - laneLeft);
        var usableCenterLaneWidth = Math.Max(1, Math.Min(layout.CenterLaneWidth, usableWidth));
        var offset = layout.Mode switch
        {
            LyricsTextAlignmentMode.Center when textWidth <= usableCenterLaneWidth =>
                ((usableWidth - usableCenterLaneWidth) * 0.5) + ((usableCenterLaneWidth - textWidth) * 0.5),
            LyricsTextAlignmentMode.Center => (usableWidth - textWidth) * 0.5,
            LyricsTextAlignmentMode.Right => usableWidth - textWidth,
            _ => 0
        };

        // For centered/right modes we intentionally allow negative offsets,
        // so long lines remain truly centered/right-aligned instead of snapping to left.
        if (layout.Mode is LyricsTextAlignmentMode.Center or LyricsTextAlignmentMode.Right)
        {
            return laneLeft + offset;
        }

        return laneLeft + Math.Max(0, offset);
    }

    private void ApplyCoverPresentationImmediate()
    {
        var targetSize = Math.Max(0, ViewModel.CoverSize);
        var targetOpacity = Math.Clamp(ViewModel.CoverOpacity, 0, 1);
        var targetSpacing = Math.Max(0, ViewModel.CoverColumnSpacing);
        var targetScale = targetOpacity > 0.001 ? 1.0 : 0.9;

        CoverArtShell.Width = targetSize;
        CoverArtShell.Height = targetSize;
        CoverArtShell.Opacity = targetOpacity;
        CoverArtScale.ScaleX = targetScale;
        CoverArtScale.ScaleY = targetScale;
        TrackMetaGrid.ColumnSpacing = targetSpacing;
        UpdateLyricsHorizontalAlignment();
    }

    private void AnimateCoverPresentation()
    {
        var targetSize = Math.Max(0, ViewModel.CoverSize);
        var targetOpacity = Math.Clamp(ViewModel.CoverOpacity, 0, 1);
        var targetSpacing = Math.Max(0, ViewModel.CoverColumnSpacing);
        var targetScale = targetOpacity > 0.001 ? 1.0 : 0.9;
        var duration = targetOpacity > CoverArtShell.Opacity ? CoverShowDuration : CoverHideDuration;
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        StopCoverPresentationAnimation();
        if (Math.Abs(CoverArtShell.Width - targetSize) < 0.01 &&
            Math.Abs(CoverArtShell.Height - targetSize) < 0.01 &&
            Math.Abs(CoverArtShell.Opacity - targetOpacity) < 0.01 &&
            Math.Abs(TrackMetaGrid.ColumnSpacing - targetSpacing) < 0.01 &&
            Math.Abs(CoverArtScale.ScaleX - targetScale) < 0.01)
        {
            return;
        }

        var storyboard = new Storyboard();
        storyboard.Children.Add(MakeDoubleAnim(CoverArtShell, nameof(FrameworkElement.Width), CoverArtShell.Width, targetSize, duration, easing));
        storyboard.Children.Add(MakeDoubleAnim(CoverArtShell, nameof(FrameworkElement.Height), CoverArtShell.Height, targetSize, duration, easing));
        storyboard.Children.Add(MakeOpacityAnim(CoverArtShell, CoverArtShell.Opacity, targetOpacity, duration, easing));
        storyboard.Children.Add(MakeDoubleAnim(TrackMetaGrid, nameof(Grid.ColumnSpacing), TrackMetaGrid.ColumnSpacing, targetSpacing, duration, easing));
        storyboard.Children.Add(MakeDoubleAnim(CoverArtScale, nameof(ScaleTransform.ScaleX), CoverArtScale.ScaleX, targetScale, duration, easing));
        storyboard.Children.Add(MakeDoubleAnim(CoverArtScale, nameof(ScaleTransform.ScaleY), CoverArtScale.ScaleY, targetScale, duration, easing));

        coverPresentationStoryboard = storyboard;
        coverPresentationStoryboard.Completed += OnCoverPresentationCompleted;
        coverPresentationStoryboard.Begin();
    }

    private void OnCoverPresentationCompleted(object? sender, object e)
    {
        ApplyCoverPresentationImmediate();
        StopCoverPresentationAnimation();
    }

    private void StopCoverPresentationAnimation()
    {
        if (coverPresentationStoryboard is null)
        {
            return;
        }

        coverPresentationStoryboard.Completed -= OnCoverPresentationCompleted;
        coverPresentationStoryboard.Stop();
        coverPresentationStoryboard = null;
    }

    private void DragHandle_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(DragHandle);
        if (point.Properties.IsRightButtonPressed)
        {
            if (!SettingsSubprocessLauncher.TryLaunch())
            {
                ShowSettingsFlyout();
            }
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (point.PointerDeviceType != PointerDeviceType.Mouse &&
            point.PointerDeviceType != PointerDeviceType.Pen &&
            point.PointerDeviceType != PointerDeviceType.Touch)
        {
            e.Handled = true;
            return;
        }

        dragStartPointerPositionInRoot = e.GetCurrentPoint(RootGrid).Position;
        dragStartOverlayOffset = new Point(OverlayRootHostTranslate.X, OverlayRootHostTranslate.Y);
        dragAxisLockMode = DragAxisLockMode.None;
        draggingPointerId = e.Pointer.PointerId;
        isDraggingWindow = DragHandle.CapturePointer(e.Pointer);
        if (isDraggingWindow)
        {
            ShowDragSnapOverlay();
            UpdateDragSnapHint(DragAxisLockMode.None, isBypassed: false);
        }
        e.Handled = true;
    }

    private void ShowSettingsFlyout()
    {
        appWindow ??= ResolveAppWindow();
        if (appWindow is null || DragHandle.ContextFlyout is not Flyout settingsFlyout)
        {
            return;
        }

        if (settingsFlyout.IsOpen)
        {
            settingsFlyout.Hide();
        }

        if (!hasSettingsFlyoutOffset)
        {
            var rootWidth = RootGrid.ActualWidth > 1
                ? RootGrid.ActualWidth
                : appWindow.Size.Width;
            var openOnRight = ShouldOpenSettingsOnRight(appWindow);
            var x = openOnRight
                ? rootWidth + SettingsFlyoutGap
                : -SettingsFlyoutWidth - SettingsFlyoutGap;
            var y = 8.0;
            if (ViewModel.ShowDragHandle && DragHandle.Visibility == Visibility.Visible)
            {
                var dragHandlePosition = DragHandle.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0));
                y = Math.Max(2, dragHandlePosition.Y - 8);
            }

            settingsFlyoutOffset = new Point(x, y);
            hasSettingsFlyoutOffset = true;
        }

        settingsFlyoutOffset = ClampSettingsFlyoutOffset(settingsFlyoutOffset);

        var options = new FlyoutShowOptions
        {
            Position = settingsFlyoutOffset,
            ShowMode = FlyoutShowMode.Standard
        };

        settingsFlyout.ShowAt(RootGrid, options);
    }

    private void CloseSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DragHandle.ContextFlyout is Flyout settingsFlyout)
        {
            settingsFlyout.Hide();
        }
    }

    private static void ExecuteCommand(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    private void SettingsRefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteCommand(ViewModel.RefreshCommand);
    }

    private void SaveCustomPresetButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteCommand(ViewModel.SaveCurrentAsCustomPresetCommand);
    }

    private void RemoveCustomPresetButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteCommand(ViewModel.RemoveSelectedCustomPresetCommand);
    }

    private void OpenConfigFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteCommand(ViewModel.OpenConfigFolderCommand);
    }

    private void OpenConfigFileButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteCommand(ViewModel.OpenConfigFileCommand);
    }

    private void OpenLogsFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteCommand(ViewModel.OpenLogsFolderCommand);
    }

    private void CheckForUpdatesButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteCommand(ViewModel.CheckForUpdatesCommand);
    }

    private void OpenReleasesButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteCommand(ViewModel.OpenUpdateReleasePageCommand);
    }

    private void ExportDebugSnapshotButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteCommand(ViewModel.ExportDebugSnapshotCommand);
    }

    private void ResetAllSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteCommand(ViewModel.ResetAllSettingsCommand);
    }

    private void SettingsFlyoutHeader_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!isSettingsFlyoutOpen || !e.GetCurrentPoint(SettingsFlyoutHeader).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!GetCursorPos(out dragStartSettingsCursorPosition))
        {
            return;
        }

        settingsFlyoutDraggingPointerId = e.Pointer.PointerId;
        dragStartSettingsOffset = settingsFlyoutOffset;
        isDraggingSettingsFlyout = SettingsFlyoutHeader.CapturePointer(e.Pointer);
        if (isDraggingSettingsFlyout)
        {
            SetWindowClickThrough(false);
        }

        e.Handled = isDraggingSettingsFlyout;
    }

    private void SettingsFlyoutHeader_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!isDraggingSettingsFlyout || e.Pointer.PointerId != settingsFlyoutDraggingPointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(SettingsFlyoutHeader);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndSettingsFlyoutDrag();
            e.Handled = true;
            return;
        }

        if (!GetCursorPos(out var currentCursor))
        {
            return;
        }

        var deltaX = currentCursor.X - dragStartSettingsCursorPosition.X;
        var deltaY = currentCursor.Y - dragStartSettingsCursorPosition.Y;
        settingsFlyoutOffset = ClampSettingsFlyoutOffset(new Point(
            dragStartSettingsOffset.X + deltaX,
            dragStartSettingsOffset.Y + deltaY));
        if (SettingsFlyoutDragTransform is not null)
        {
            SettingsFlyoutDragTransform.X = deltaX;
            SettingsFlyoutDragTransform.Y = deltaY;
        }
        hasSettingsFlyoutOffset = true;
        e.Handled = true;
    }

    private void SettingsFlyoutHeader_OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!isDraggingSettingsFlyout || e.Pointer.PointerId != settingsFlyoutDraggingPointerId)
        {
            return;
        }

        if (SettingsFlyoutDragTransform is not null)
        {
            SettingsFlyoutDragTransform.X = 0;
            SettingsFlyoutDragTransform.Y = 0;
        }
        EndSettingsFlyoutDrag();
        ReopenSettingsFlyoutAtCurrentOffset();
        e.Handled = true;
    }

    public void OpenSettingsFlyoutFromTray()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (SettingsSubprocessLauncher.TryLaunch())
            {
                return;
            }

            SetWindowClickThrough(false);
            ShowSettingsFlyout();
            UpdateWindowClickThroughMode();
        });
    }

    private static bool ShouldOpenSettingsOnRight(AppWindow window)
    {
        var displayArea = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var leftSpace = window.Position.X - workArea.X;
        var rightSpace = workArea.X + workArea.Width - (window.Position.X + window.Size.Width);
        return rightSpace >= SettingsFlyoutWidth + SettingsFlyoutGap || rightSpace >= leftSpace;
    }

    private void DragHandle_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!isDraggingWindow || e.Pointer.PointerId != draggingPointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(DragHandle);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndWindowDrag();
            e.Handled = true;
            return;
        }

        var currentPointInRoot = e.GetCurrentPoint(RootGrid).Position;
        var deltaX = currentPointInRoot.X - dragStartPointerPositionInRoot.X;
        var deltaY = currentPointInRoot.Y - dragStartPointerPositionInRoot.Y;
        var bypassSnap = IsDragSnapBypassModifierPressed();

        if (!bypassSnap && dragAxisLockMode == DragAxisLockMode.None)
        {
            var absX = Math.Abs(deltaX);
            var absY = Math.Abs(deltaY);
            if (absX >= DragSnapAxisThreshold || absY >= DragSnapAxisThreshold)
            {
                dragAxisLockMode = absX >= absY
                    ? DragAxisLockMode.Horizontal
                    : DragAxisLockMode.Vertical;
            }
        }

        var effectiveAxis = bypassSnap ? DragAxisLockMode.None : dragAxisLockMode;
        if (effectiveAxis == DragAxisLockMode.Horizontal)
        {
            deltaY = 0;
        }
        else if (effectiveAxis == DragAxisLockMode.Vertical)
        {
            deltaX = 0;
        }

        if (!bypassSnap)
        {
            deltaX = Math.Round(deltaX / DragSnapGridStep) * DragSnapGridStep;
            deltaY = Math.Round(deltaY / DragSnapGridStep) * DragSnapGridStep;
        }

        var target = new Point(
            dragStartOverlayOffset.X + deltaX,
            dragStartOverlayOffset.Y + deltaY);
        if (!bypassSnap)
        {
            target = SnapOverlayRootOffsetToGrid(target);
        }

        target = ClampOverlayRootOffset(target);
        ApplyOverlayRootOffset(target, persist: false);
        UpdateDragSnapHint(effectiveAxis, bypassSnap);
        e.Handled = true;
    }

    private void DragHandle_OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag();
    }

    private void DragHandle_OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag();
    }

    private void DragHandle_OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag();
    }

    private void EndWindowDrag()
    {
        if (isDraggingWindow)
        {
            var target = SnapOverlayRootOffsetToGrid(new Point(
                OverlayRootHostTranslate.X,
                OverlayRootHostTranslate.Y));
            ApplyOverlayRootOffset(ClampOverlayRootOffset(target), persist: true);
        }

        isDraggingWindow = false;
        dragAxisLockMode = DragAxisLockMode.None;
        draggingPointerId = 0;
        DragHandle.ReleasePointerCaptures();
        HideDragSnapOverlay();
        UpdateWindowClickThroughMode();
    }

    private void ShowDragSnapOverlay()
    {
        DragSnapOverlay.Visibility = Visibility.Visible;
        RebuildDragSnapGridOverlay();
    }

    private void HideDragSnapOverlay()
    {
        DragSnapOverlay.Visibility = Visibility.Collapsed;
    }

    private void RebuildDragSnapGridOverlay()
    {
        var width = RootGrid.ActualWidth;
        var height = RootGrid.ActualHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 1 || height <= 1)
        {
            return;
        }

        DragSnapGridCanvas.Width = width;
        DragSnapGridCanvas.Height = height;
        DragSnapGridCanvas.Children.Clear();

        var step = DragSnapGridStep;
        var majorInterval = 4;
        var minorBrush = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
        var majorBrush = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));
        var centerBrush = new SolidColorBrush(Color.FromArgb(74, 196, 226, 255));

        var index = 0;
        for (var x = 0d; x <= width; x += step, index++)
        {
            var isMajor = index % majorInterval == 0;
            var line = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = height,
                StrokeThickness = isMajor ? 1.1 : 0.8,
                Stroke = isMajor ? majorBrush : minorBrush,
                IsHitTestVisible = false
            };
            DragSnapGridCanvas.Children.Add(line);
        }

        index = 0;
        for (var y = 0d; y <= height; y += step, index++)
        {
            var isMajor = index % majorInterval == 0;
            var line = new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                StrokeThickness = isMajor ? 1.1 : 0.8,
                Stroke = isMajor ? majorBrush : minorBrush,
                IsHitTestVisible = false
            };
            DragSnapGridCanvas.Children.Add(line);
        }

        var centerX = width * 0.5;
        var centerY = height * 0.5;
        DragSnapGridCanvas.Children.Add(new Line
        {
            X1 = centerX,
            X2 = centerX,
            Y1 = 0,
            Y2 = height,
            Stroke = centerBrush,
            StrokeThickness = 1.4,
            IsHitTestVisible = false
        });
        DragSnapGridCanvas.Children.Add(new Line
        {
            X1 = 0,
            X2 = width,
            Y1 = centerY,
            Y2 = centerY,
            Stroke = centerBrush,
            StrokeThickness = 1.4,
            IsHitTestVisible = false
        });
    }

    private void UpdateDragSnapHint(DragAxisLockMode axisMode, bool isBypassed)
    {
        var axisText = axisMode switch
        {
            DragAxisLockMode.Horizontal => "ось X",
            DragAxisLockMode.Vertical => "ось Y",
            _ => "ось авто"
        };

        DragSnapHintText.Text = isBypassed
            ? "Привязка: ВЫКЛ (Shift) • свободное движение"
            : $"Привязка: ВКЛ ({DragSnapGridStep:0}px) • {axisText} • Shift — игнорировать";
    }

    private static bool IsDragSnapBypassModifierPressed() =>
        (GetAsyncKeyState(VkShift) & 0x8000) != 0;

    private void ManualLyricsAlignmentComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressManualAlignmentSelectionChanged || sender is not ComboBox combo)
        {
            return;
        }

        LyricsTextAlignmentMode? selectedMode = combo.SelectedValue switch
        {
            LyricsTextAlignmentMode mode => mode,
            _ => null
        };

        if (selectedMode is null && combo.SelectedItem is LyricsTextAlignmentOption option)
        {
            selectedMode = option.Mode;
        }

        if (selectedMode is null || ViewModel.LyricsTextAlignment == selectedMode.Value)
        {
            return;
        }

        try
        {
            suppressManualAlignmentSelectionChanged = true;
            if (ViewModel.AutoLyricsTextAlignment)
            {
                ViewModel.AutoLyricsTextAlignment = false;
            }

            ViewModel.LyricsTextAlignment = selectedMode.Value;
            UpdateLyricsHorizontalAlignment();
        }
        finally
        {
            suppressManualAlignmentSelectionChanged = false;
        }
    }

    private void EndSettingsFlyoutDrag()
    {
        isDraggingSettingsFlyout = false;
        settingsFlyoutDraggingPointerId = 0;
        SettingsFlyoutHeader?.ReleasePointerCaptures();
        UpdateWindowClickThroughMode();
    }

    private Point ClampSettingsFlyoutOffset(Point offset)
    {
        appWindow ??= ResolveAppWindow();
        var rootWidth = RootGrid.ActualWidth > 1
            ? RootGrid.ActualWidth
            : appWindow?.Size.Width ?? SettingsFlyoutWidth;
        var rootHeight = RootGrid.ActualHeight > 1
            ? RootGrid.ActualHeight
            : appWindow?.Size.Height ?? SettingsFlyoutHeight;

        var minX = -GetSettingsFlyoutWidth() - 48;
        var maxX = rootWidth + 48;
        var minY = 0.0;
        var maxY = Math.Max(0.0, rootHeight - Math.Min(rootHeight, GetSettingsFlyoutHeight()) + 24);
        return new Point(
            Math.Clamp(offset.X, minX, maxX),
            Math.Clamp(offset.Y, minY, maxY));
    }

    private void ReopenSettingsFlyoutAtCurrentOffset()
    {
        if (!isSettingsFlyoutOpen || DragHandle.ContextFlyout is not Flyout settingsFlyout)
        {
            return;
        }

        settingsFlyout.Hide();
        var options = new FlyoutShowOptions
        {
            Position = settingsFlyoutOffset,
            ShowMode = FlyoutShowMode.Standard
        };
        settingsFlyout.ShowAt(RootGrid, options);
    }

    private static nint ResolveWindowHandle()
    {
        if (Application.Current is not App app)
        {
            return 0;
        }

        return WinRT.Interop.WindowNative.GetWindowHandle(app.MainWindow);
    }

    private static AppWindow? ResolveAppWindow()
    {
        var hwnd = ResolveWindowHandle();
        if (hwnd == 0)
        {
            return null;
        }

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    private void AttachSettingsFlyoutEvents()
    {
        if (DragHandle.ContextFlyout is not Flyout settingsFlyout)
        {
            return;
        }

        settingsFlyout.Opened += SettingsFlyout_OnOpened;
        settingsFlyout.Closed += SettingsFlyout_OnClosed;
    }

    private void DetachSettingsFlyoutEvents()
    {
        if (DragHandle.ContextFlyout is not Flyout settingsFlyout)
        {
            return;
        }

        settingsFlyout.Opened -= SettingsFlyout_OnOpened;
        settingsFlyout.Closed -= SettingsFlyout_OnClosed;
    }

    private void SettingsFlyout_OnOpened(object? sender, object e)
    {
        isSettingsFlyoutOpen = true;
        ViewModel.SetOverlayResizeDeferred(true);
        UpdateWindowClickThroughMode();
    }

    private void SettingsFlyout_OnClosed(object? sender, object e)
    {
        if (SettingsFlyoutDragTransform is not null)
        {
            SettingsFlyoutDragTransform.X = 0;
            SettingsFlyoutDragTransform.Y = 0;
        }
        EndSettingsFlyoutDrag();
        isSettingsFlyoutOpen = false;
        ViewModel.SetOverlayResizeDeferred(false);
        UpdateWindowClickThroughMode();
    }

    private ScreenRect GetDragHandleScreenBounds()
    {
        if (!ViewModel.ShowDragHandle || DragHandle.Visibility != Visibility.Visible)
        {
            return new ScreenRect(0, 0, 0, 0);
        }

        var position = DragHandle.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0));
        var x = appWindow!.Position.X + (int)Math.Round(position.X);
        var y = appWindow.Position.Y + (int)Math.Round(position.Y);
        var width = (int)Math.Ceiling(Math.Max(DragHandle.ActualWidth, 1));
        var height = (int)Math.Ceiling(Math.Max(DragHandle.ActualHeight, 1));
        return new ScreenRect(x, y, width, height);
    }

    private ScreenRect GetSettingsFlyoutScreenBounds()
    {
        var x = appWindow!.Position.X + (int)Math.Round(settingsFlyoutOffset.X);
        var y = appWindow.Position.Y + (int)Math.Round(settingsFlyoutOffset.Y);
        return new ScreenRect(x, y, GetSettingsFlyoutWidth(), GetSettingsFlyoutHeight());
    }

    private int GetSettingsFlyoutWidth()
    {
        var measured = SettingsFlyoutRoot?.ActualWidth ?? 0;
        return (int)Math.Ceiling(Math.Max(SettingsFlyoutWidth, measured));
    }

    private int GetSettingsFlyoutHeight()
    {
        var measured = SettingsFlyoutRoot?.ActualHeight ?? 0;
        return (int)Math.Ceiling(Math.Max(320, Math.Min(SettingsFlyoutHeight, measured > 0 ? measured : SettingsFlyoutHeight)));
    }

    private void StartClickThroughTimer()
    {
        if (clickThroughTimer is not null)
        {
            return;
        }

        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            return;
        }

        clickThroughTimer = queue.CreateTimer();
        clickThroughTimer.Interval = TimeSpan.FromMilliseconds(33);
        clickThroughTimer.IsRepeating = true;
        clickThroughTimer.Tick += OnClickThroughTimerTick;
        clickThroughTimer.Start();
    }

    private void StopClickThroughTimer()
    {
        if (clickThroughTimer is null)
        {
            return;
        }

        clickThroughTimer.Stop();
        clickThroughTimer.Tick -= OnClickThroughTimerTick;
        clickThroughTimer = null;
    }

    private void OnClickThroughTimerTick(DispatcherQueueTimer sender, object args)
    {
        UpdateWindowClickThroughMode();
    }

    private void UpdateDebugOverlayState()
    {
        var shouldRunDebugTimer = ViewModel.ShowDebugHitboxes || ViewModel.ShowRuntimeDebugPanel;
        if (!shouldRunDebugTimer)
        {
            StopDebugOverlayTimer();
            if (DebugMetricsText is not null)
            {
                DebugMetricsText.Text = string.Empty;
            }

            if (RuntimeDebugPanelText is not null)
            {
                RuntimeDebugPanelText.Text = string.Empty;
            }

            return;
        }

        if (debugOverlayTimer is null)
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            if (queue is null)
            {
                return;
            }

            debugOverlayTimer = queue.CreateTimer();
            debugOverlayTimer.Interval = TimeSpan.FromMilliseconds(33);
            debugOverlayTimer.IsRepeating = true;
            debugOverlayTimer.Tick += OnDebugOverlayTimerTick;
        }

        debugOverlayTimer.Start();
        UpdateDebugOverlayInfo();
    }

    private void StopDebugOverlayTimer()
    {
        if (debugOverlayTimer is null)
        {
            return;
        }

        debugOverlayTimer.Stop();
        debugOverlayTimer.Tick -= OnDebugOverlayTimerTick;
        debugOverlayTimer = null;
    }

    private void OnDebugOverlayTimerTick(DispatcherQueueTimer sender, object args)
    {
        UpdateDebugOverlayInfo();
    }

    private void UpdateDebugOverlayInfo()
    {
        UpdateRuntimeDebugPanel();

        if (!ViewModel.ShowDebugHitboxes || DebugMetricsText is null)
        {
            return;
        }

        if (physicalTrackModeActive)
        {
            UpdatePhysicalTrackDebugInfo();
            return;
        }

        var prevDpTop = NormalizeCanvasTop(Canvas.GetTop(PreviousLineLayer));
        var currentDpTop = NormalizeCanvasTop(Canvas.GetTop(CurrentLineLayer));
        var nextDpTop = NormalizeCanvasTop(Canvas.GetTop(NextLineLayer));
        var prevVisTop = GetTopRelativeTo(PreviousLineLayer, LyricsCanvas);
        var currentVisTop = GetTopRelativeTo(CurrentLineLayer, LyricsCanvas);
        var nextVisTop = GetTopRelativeTo(NextLineLayer, LyricsCanvas);

        var prevHeight = GetElementHeight(PreviousLineLayer);
        var currentHeight = GetElementHeight(CurrentLineLayer);
        var nextHeight = GetElementHeight(NextLineLayer);
        var delta = canvasNextTop - canvasCurrentTop;
        var lineBandHeight = Math.Max(currentHeight, nextHeight);
        var expectedPrevTop = canvasCurrentTop - lineBandHeight - LineExitPadding;

        var prevTextTopInLayer = GetTopRelativeTo(PreviousLineText, PreviousLineLayer);
        var currentTextTopInLayer = GetTopRelativeTo(CurrentLineText, CurrentLineLayer);
        var nextTextTopInLayer = GetTopRelativeTo(NextLineText, NextLineLayer);

        var currentTextTopInCanvas = GetTopRelativeTo(CurrentLineText, LyricsCanvas);
        var nextTextTopInCanvas = GetTopRelativeTo(NextLineText, LyricsCanvas);
        var currentTextHeight = GetElementHeight(CurrentLineText);
        var visibleCurrentTop = NextLineLayer.Opacity >= CurrentLineLayer.Opacity ? nextVisTop : currentVisTop;
        var visibleNextTop = PreviousLineLayer.Opacity > 0.01 ? prevVisTop : nextVisTop;
        var layerGap = visibleNextTop - (visibleCurrentTop + currentHeight);
        var textGap = nextTextTopInCanvas - (currentTextTopInCanvas + currentTextHeight);

        PreviousHitboxDebugLabel.Text = $"PREV visY={prevVisTop:0.0} dpY={prevDpTop:0.0} h={prevHeight:0.0}";
        CurrentHitboxDebugLabel.Text = $"CUR visY={currentVisTop:0.0} dpY={currentDpTop:0.0} h={currentHeight:0.0}";
        NextHitboxDebugLabel.Text = $"NEXT visY={nextVisTop:0.0} dpY={nextDpTop:0.0} h={nextHeight:0.0}";

        var sb = new StringBuilder(640);
        sb.Append("state: transition=").Append(isLineTransitionRunning)
            .Append(" pending=").Append(hasPendingLineTransition)
            .Append(" mode=").Append(currentOverlayMode)
            .Append(" animV=").Append(ViewModel.LineAnimationVersion)
            .AppendLine();
        sb.Append("session: active=").Append(ViewModel.HasActiveSession)
            .Append(" playbackVisible=").Append(ViewModel.IsPlaybackVisible)
            .Append(" busy=").Append(ViewModel.IsBusy)
            .Append(" preRoll=").Append(ViewModel.IsPreRollActive)
            .Append(" waiting=").Append(ViewModel.IsWaitingDotsActive)
            .Append(" countdown=").Append(ViewModel.IsCountdownActive)
            .Append(" visualizer=").Append(ViewModel.ShowLyricVisualizer)
            .AppendLine();
        sb.Append("smtc: ").Append(ViewModel.PlaybackVisibilityDebugState).AppendLine();
        sb.Append("overlayHost: op=").Append(FormatDebug(OverlayContentHost.Opacity))
            .Append(" trY=").Append(FormatDebug(OverlayContentHostTranslate.Y))
            .Append(" visibleState=").Append(overlayContentVisibleState?.ToString() ?? "<null>")
            .AppendLine();
        sb.Append("tracked: currentTop=").Append(FormatDebug(canvasCurrentTop))
            .Append(" nextTop=").Append(FormatDebug(canvasNextTop))
            .Append(" delta=").Append(FormatDebug(delta))
            .Append(" expectedPrev=").Append(FormatDebug(expectedPrevTop))
            .AppendLine();
        sb.Append("note: visTop=rendered position, dpTop=Canvas.Top DP (can diverge during dependent animation)")
            .AppendLine();
        sb.Append("gaps: layerGap=").Append(FormatDebug(layerGap))
            .Append(" textGap=").Append(FormatDebug(textGap))
            .AppendLine();
        sb.AppendLine(BuildLayerDebugLine("PREV", PreviousLineLayer, PreviousLineText, PreviousLineScale, expectedPrevTop));
        sb.AppendLine(BuildLayerDebugLine("CUR ", CurrentLineLayer, CurrentLineText, CurrentLineScale, canvasCurrentTop));
        sb.Append(BuildLayerDebugLine("NEXT", NextLineLayer, NextLineText, NextLineScale, canvasNextTop));

        DebugMetricsText.Text = sb.ToString();
    }

    private void UpdateRuntimeDebugPanel()
    {
        if (RuntimeDebugPanelText is null)
        {
            return;
        }

        if (!ViewModel.ShowRuntimeDebugPanel)
        {
            RuntimeDebugPanelText.Text = string.Empty;
            return;
        }

        var signal = ComputeOverlayPresenceSignal(commitSignal: false);
        var effectiveGracePeriod = GetEffectiveOverlayGracePeriod();
        var graceLeft = lastOverlayPositiveSignalAtUtc == DateTimeOffset.MinValue
            ? 0
            : Math.Max(0, (effectiveGracePeriod - (DateTimeOffset.UtcNow - lastOverlayPositiveSignalAtUtc)).TotalSeconds);

        var sb = new StringBuilder(520);
        sb.AppendLine("FloatingLyrics runtime debug");
        sb.Append("signal: shouldShow=").Append(signal.ShouldShow)
            .Append(" positive=").Append(signal.HasPositiveSignal)
            .Append(" reason=").Append(signal.Reason)
            .AppendLine();
        sb.Append("signal(last applied): ").Append(lastOverlayPresenceSignalReason).AppendLine();
        sb.Append("grace: ").Append(graceLeft.ToString("0.00")).Append("s / ").Append(effectiveGracePeriod.TotalSeconds.ToString("0.00")).AppendLine("s");
        sb.Append("overlay: op=").Append(FormatDebug(OverlayContentHost.Opacity))
            .Append(" y=").Append(FormatDebug(OverlayContentHostTranslate.Y))
            .Append(" state=").Append(overlayContentVisibleState?.ToString() ?? "<null>")
            .AppendLine();
        sb.Append("mode: current=").Append(currentOverlayMode)
            .Append(" target=").Append(overlayModeTransitionTarget)
            .Append(" transition=").Append(overlayModeStoryboard is not null)
            .AppendLine();
        sb.Append("session: active=").Append(ViewModel.HasActiveSession)
            .Append(" playbackVisible=").Append(ViewModel.IsPlaybackVisible)
            .Append(" busy=").Append(ViewModel.IsBusy)
            .Append(" preRoll=").Append(ViewModel.IsPreRollActive)
            .Append(" waiting=").Append(ViewModel.IsWaitingDotsActive)
            .Append(" countdown=").Append(ViewModel.IsCountdownActive)
            .Append(" viz=").Append(ViewModel.ShowLyricVisualizer)
            .AppendLine();
        sb.Append("meta: title='").Append(ViewModel.TrackTitle)
            .Append("' artist='").Append(ViewModel.ArtistName)
            .Append("' current='").Append(ViewModel.CurrentLine)
            .Append("' next='").Append(ViewModel.NextLine)
            .AppendLine("'");
        sb.Append("smtc: ").Append(ViewModel.PlaybackVisibilityDebugState);
        sb.AppendLine();
        sb.Append("audio: route=").Append(audioSpectrumAnalyzer.CaptureRouteDescription)
            .Append(" sourceApp='").Append(ViewModel.AudioSourceAppId).Append("'");
        RuntimeDebugPanelText.Text = sb.ToString();
    }

    private void UpdatePhysicalTrackDebugInfo()
    {
        if (DebugMetricsText is null)
        {
            return;
        }

        if (physicalLyricItems.Count == 0)
        {
            DebugMetricsText.Text = "state: physical-track active, no items";
            return;
        }

        var activeIndex = Math.Clamp(GetActivePhysicalPlanIndex(), 0, physicalLyricItems.Count - 1);
        var sb = new StringBuilder(420);
        sb.Append("state: physical-track=True ")
            .Append("transition=").Append(activePhysicalTrackStoryboard is not null)
            .Append(" pending=").Append(hasPendingLineTransition)
            .Append(" preRoll=").Append(ViewModel.IsPreRollActive)
            .Append(" viz=").Append(activePhysicalTrackPlan.ContainsVisualizer)
            .Append(' ')
            .Append("active=").Append(activeIndex)
            .Append(" rendered=").Append(physicalRenderedIndex)
            .Append(" items=").Append(physicalLyricItems.Count)
            .AppendLine();
        sb.Append("trackTranslateY=").Append(FormatDebug(LyricsTrackTranslate.Y))
            .Append(" step=").Append(FormatDebug(physicalLineStep))
            .Append(" viewportH=").Append(FormatDebug(LyricsCanvas.Height))
            .AppendLine();

        AppendPhysicalTrackDebugLine(sb, "PREV", activeIndex - 1);
        AppendPhysicalTrackDebugLine(sb, "CUR ", activeIndex);
        AppendPhysicalTrackDebugLine(sb, "NEXT", activeIndex + 1);
        DebugMetricsText.Text = sb.ToString();
    }

    private void AppendPhysicalTrackDebugLine(StringBuilder sb, string tag, int index)
    {
        if (index < 0 || index >= physicalLyricItems.Count)
        {
            sb.Append(tag).Append(" <none>").AppendLine();
            return;
        }

        var item = physicalLyricItems[index];
        var dpTop = NormalizeCanvasTop(Canvas.GetTop(item.Layer));
        var visTop = dpTop + LyricsTrackTranslate.Y;
        sb.Append(tag)
            .Append(" idx=").Append(index)
            .Append(" visY=").Append(FormatDebug(visTop))
            .Append(" dpY=").Append(FormatDebug(dpTop))
            .Append(" h=").Append(FormatDebug(item.Layer.Height))
            .Append(" sc=").Append(FormatDebug(item.Scale.ScaleY))
            .Append(" op=").Append(FormatDebug(item.Layer.Opacity))
            .AppendLine();
    }

    private string BuildLayerDebugLine(
        string name,
        FrameworkElement layer,
        TextBlock text,
        ScaleTransform scale,
        double trackedTop)
    {
        var dpTop = NormalizeCanvasTop(Canvas.GetTop(layer));
        var layerTopInCanvas = GetTopRelativeTo(layer, LyricsCanvas);
        var textTopInLayer = GetTopRelativeTo(text, layer);
        var textTopInCanvas = GetTopRelativeTo(text, LyricsCanvas);
        var layerHeight = GetElementHeight(layer);
        var textHeight = GetElementHeight(text);
        var layerOffset = layer.ActualOffset;
        var textOffset = text.ActualOffset;
        var translateY = GetLayerTranslateY(layer);
        var textScaleY = GetTextScaleY(text);

        return string.Concat(
            name, " ",
            "dpTop=", FormatDebug(dpTop),
            " visTop=", FormatDebug(layerTopInCanvas),
            " tracked=", FormatDebug(trackedTop),
            " txtIn=", FormatDebug(textTopInLayer),
            " txtY=", FormatDebug(textTopInCanvas),
            " h=", FormatDebug(layerHeight),
            " txtH=", FormatDebug(textHeight),
            " fs=", FormatDebug(text.FontSize),
            " lh=", FormatDebug(text.LineHeight),
            " fw=", text.FontWeight.Weight,
            " sc=", FormatDebug(scale.ScaleY),
            " ts=", FormatDebug(textScaleY),
            " trY=", FormatDebug(translateY),
            " op=", FormatDebug(layer.Opacity),
            " offL=", FormatDebug(layerOffset.Y),
            " offT=", FormatDebug(textOffset.Y));
    }

    private static double GetLayerTranslateY(FrameworkElement layer)
    {
        return layer.RenderTransform switch
        {
            TranslateTransform translate => translate.Y,
            TransformGroup group => group.Children
                .OfType<TranslateTransform>()
                .Select(t => t.Y)
                .DefaultIfEmpty(0)
                .First(),
            _ => 0
        };
    }

    private static double GetTextScaleY(TextBlock text)
    {
        return text.RenderTransform switch
        {
            ScaleTransform scale => scale.ScaleY,
            TransformGroup group => group.Children
                .OfType<ScaleTransform>()
                .Select(s => s.ScaleY)
                .DefaultIfEmpty(1)
                .First(),
            _ => 1
        };
    }

    private static double GetElementHeight(FrameworkElement element)
    {
        var actual = element.ActualHeight;
        if (actual > 0)
        {
            return actual;
        }

        return element.Height;
    }

    private static double GetTopRelativeTo(FrameworkElement element, UIElement relativeTo)
    {
        try
        {
            var transform = element.TransformToVisual(relativeTo);
            return transform.TransformPoint(new Point(0, 0)).Y;
        }
        catch
        {
            return double.NaN;
        }
    }

    private static double NormalizeCanvasTop(double value)
    {
        return double.IsNaN(value) ? 0 : value;
    }

    private static string FormatDebug(double value)
    {
        return double.IsNaN(value) ? "NaN" : value.ToString("0.00");
    }

    private void InitializeOverlayModeState()
    {
        currentOverlayMode = DetermineOverlayMode();
        overlayModeTransitionTarget = currentOverlayMode;
        ApplyOverlayModeState(currentOverlayMode);
    }

    private void UpdateOverlayModeState(bool animated)
    {
        var targetMode = DetermineOverlayMode();
        if (!animated)
        {
            StopOverlayModeTransition();
            currentOverlayMode = targetMode;
            overlayModeTransitionTarget = targetMode;
            ApplyOverlayModeState(targetMode);
            return;
        }

        if (overlayModeStoryboard is not null)
        {
            // Ignore duplicate notifications for the same target while the transition runs.
            if (targetMode == overlayModeTransitionTarget)
            {
                return;
            }

            StopOverlayModeTransition();
        }

        if (targetMode == currentOverlayMode)
        {
            // Reconcile visual state even when mode value didn't change.
            ApplyOverlayModeState(targetMode);
            return;
        }

        var duration = new Duration(TimeSpan.FromSeconds(Math.Clamp(ViewModel.LineAnimationDurationSeconds * 0.8, 0.12, 0.45)));
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var storyboard = new Storyboard();
        AddModeLayerAnimations(storyboard, LyricVisualizerHost, VisualizerModeTranslate, targetMode == OverlayContentMode.Visualizer, duration, easing);
        AddModeLayerAnimations(storyboard, LyricsCanvas, LyricsModeTranslate, targetMode == OverlayContentMode.Lyrics, duration, easing);
        AddModeLayerAnimations(storyboard, PreRollHost, PreRollModeTranslate, targetMode == OverlayContentMode.PreRoll, duration, easing);

        overlayModeStoryboard = storyboard;
        overlayModeTransitionTarget = targetMode;
        overlayModeStoryboard.Completed += OnOverlayModeTransitionCompleted;
        overlayModeStoryboard.Begin();
        currentOverlayMode = targetMode;
    }

    private void AddModeLayerAnimations(
        Storyboard storyboard,
        UIElement layer,
        TranslateTransform translate,
        bool shouldShow,
        Duration duration,
        EasingFunctionBase easing)
    {
        var targetOpacity = shouldShow ? 1 : 0;
        var targetY = shouldShow ? 0 : ModeTransitionYOffset;
        storyboard.Children.Add(MakeOpacityAnim(layer, layer.Opacity, targetOpacity, duration, easing));
        storyboard.Children.Add(MakeDoubleAnim(translate, nameof(TranslateTransform.Y), translate.Y, targetY, duration, easing));
    }

    private void OnOverlayModeTransitionCompleted(object? sender, object e)
    {
        StopOverlayModeTransition();
        currentOverlayMode = overlayModeTransitionTarget;
        ApplyOverlayModeState(currentOverlayMode);
    }

    private void ApplyOverlayModeState(OverlayContentMode mode)
    {
        ApplyModeLayerState(LyricVisualizerHost, VisualizerModeTranslate, mode == OverlayContentMode.Visualizer);
        ApplyModeLayerState(LyricsCanvas, LyricsModeTranslate, mode == OverlayContentMode.Lyrics);
        ApplyModeLayerState(PreRollHost, PreRollModeTranslate, mode == OverlayContentMode.PreRoll);
    }

    private static void ApplyModeLayerState(UIElement layer, TranslateTransform transform, bool show)
    {
        layer.Opacity = show ? 1 : 0;
        transform.Y = show ? 0 : ModeTransitionYOffset;
    }

    private void ProcessOverlayPresenceSignal(bool animated, string trigger)
    {
        var signal = ComputeOverlayPresenceSignal(commitSignal: true);
        lastOverlayPresenceSignalReason = signal.Reason;
        ApplyOverlayContentVisibility(signal, animated, trigger);
        UpdateOverlayMetaElements(animated);
        UpdateRuntimeDebugPanel();
    }

    private void UpdateOverlayMetaElements(bool animated)
    {
        UpdatePhysicalTrackMode();
        UpdateOverlayModeState(animated);
        UpdateVisualizerState();
        if (!physicalTrackModeActive)
        {
            UpdatePreRollInnerState(animated);
        }
    }

    private void ApplyOverlayContentVisibility(OverlayPresenceSignal signal, bool animated, string trigger)
    {
        var shouldShow = signal.ShouldShow;
        TraceOverlayVisibilityDecision(signal, animated, trigger);
        var targetOpacity = shouldShow ? 1 : 0;
        var targetY = shouldShow ? 0 : OverlayPresenceOffsetY;

        if (overlayContentVisibleState.HasValue &&
            overlayContentVisibleState.Value == shouldShow &&
            overlayContentVisibilityStoryboard is null)
        {
            // Guard against stale visual state: if state says "shown" but opacity/offset drifted,
            // hard-apply final values instead of returning with invisible content.
            if (!IsClose(OverlayContentHost.Opacity, targetOpacity, 0.01) ||
                !IsClose(OverlayContentHostTranslate.Y, targetY, 0.15))
            {
                OverlayContentHost.Opacity = targetOpacity;
                OverlayContentHostTranslate.Y = targetY;
            }

            OverlayContentHost.IsHitTestVisible = shouldShow;
            return;
        }

        if (!animated)
        {
            StopOverlayContentVisibilityTransition();
            OverlayContentHost.Opacity = targetOpacity;
            OverlayContentHostTranslate.Y = targetY;
            OverlayContentHost.IsHitTestVisible = shouldShow;
            overlayContentVisibleState = shouldShow;
            return;
        }

        StopOverlayContentVisibilityTransition();
        OverlayContentHost.IsHitTestVisible = true;
        var duration = new Duration(TimeSpan.FromSeconds(shouldShow ? 0.34 : 0.26));
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var storyboard = new Storyboard();
        storyboard.Children.Add(MakeOpacityAnim(OverlayContentHost, OverlayContentHost.Opacity, targetOpacity, duration, easing));
        storyboard.Children.Add(MakeDoubleAnim(
            OverlayContentHostTranslate,
            nameof(TranslateTransform.Y),
            OverlayContentHostTranslate.Y,
            targetY,
            duration,
            easing));

        overlayContentVisibilityStoryboard = storyboard;
        overlayContentVisibilityStoryboard.Completed += OnOverlayContentVisibilityTransitionCompleted;
        overlayContentVisibilityStoryboard.Begin();
        overlayContentVisibleState = shouldShow;
    }

    private void OnOverlayContentVisibilityTransitionCompleted(object? sender, object e)
    {
        StopOverlayContentVisibilityTransition();
        var shouldShow = overlayContentVisibleState ?? false;
        OverlayContentHost.Opacity = shouldShow ? 1 : 0;
        OverlayContentHostTranslate.Y = shouldShow ? 0 : OverlayPresenceOffsetY;
        OverlayContentHost.IsHitTestVisible = shouldShow;
        overlayContentVisibleState = shouldShow;
    }

    private void StopOverlayContentVisibilityTransition()
    {
        if (overlayContentVisibilityStoryboard is null)
        {
            return;
        }

        overlayContentVisibilityStoryboard.Completed -= OnOverlayContentVisibilityTransitionCompleted;
        overlayContentVisibilityStoryboard.Stop();
        overlayContentVisibilityStoryboard = null;
    }

    private static bool IsClose(double left, double right, double epsilon)
    {
        return Math.Abs(left - right) <= epsilon;
    }

    private OverlayPresenceSignal ComputeOverlayPresenceSignal(bool commitSignal)
    {
        var hasSignal =
            ViewModel.IsPlaybackVisible &&
            (ViewModel.HasActiveSession ||
             !string.IsNullOrWhiteSpace(ViewModel.TrackTitle) ||
             !string.IsNullOrWhiteSpace(ViewModel.ArtistName) ||
             !string.IsNullOrWhiteSpace(ViewModel.CurrentLine) ||
             !string.IsNullOrWhiteSpace(ViewModel.NextLine) ||
             ViewModel.ShowLyricVisualizer ||
             ViewModel.IsPreRollActive);

        if (hasSignal)
        {
            if (commitSignal)
            {
                lastOverlayPositiveSignalAtUtc = DateTimeOffset.UtcNow;
            }

            return new OverlayPresenceSignal(
                ShouldShow: true,
                HasPositiveSignal: true,
                Reason: "live-signal");
        }

        if (lastOverlayPositiveSignalAtUtc == DateTimeOffset.MinValue)
        {
            return new OverlayPresenceSignal(
                ShouldShow: false,
                HasPositiveSignal: false,
                Reason: "no-signal-no-grace");
        }

        var gracePeriod = GetEffectiveOverlayGracePeriod();
        if (gracePeriod <= TimeSpan.Zero)
        {
            return new OverlayPresenceSignal(
                ShouldShow: false,
                HasPositiveSignal: false,
                Reason: "no-extra-grace");
        }

        var inGrace = DateTimeOffset.UtcNow - lastOverlayPositiveSignalAtUtc < gracePeriod;
        return new OverlayPresenceSignal(
            ShouldShow: inGrace,
            HasPositiveSignal: false,
            Reason: inGrace ? "grace-window" : "grace-expired");
    }

    private TimeSpan GetEffectiveOverlayGracePeriod()
    {
        // When hide-on-pause is enabled, user-controlled pause delay should be exact
        // and must not be extended by additional UI grace.
        if (ViewModel.HideOnPause)
        {
            return TimeSpan.Zero;
        }

        return OverlayHideGracePeriod;
    }

    private void TraceOverlayVisibilityDecision(OverlayPresenceSignal signal, bool animated, string trigger)
    {
        var shouldShow = signal.ShouldShow;
        var state = string.Join(
            "|",
            shouldShow,
            signal.HasPositiveSignal,
            signal.Reason,
            animated,
            trigger,
            ViewModel.HasActiveSession,
            ViewModel.IsBusy,
            ViewModel.IsPlaybackVisible,
            ViewModel.TrackTitle,
            ViewModel.ArtistName,
            ViewModel.CurrentLine,
            ViewModel.NextLine,
            ViewModel.ShowLyricVisualizer,
            ViewModel.IsPreRollActive,
            ViewModel.IsWaitingDotsActive,
            ViewModel.IsCountdownActive);

        if (string.Equals(lastOverlayVisibilityTraceState, state, StringComparison.Ordinal))
        {
            return;
        }

        lastOverlayVisibilityTraceState = state;
        DebugTrace.Write(
            $"OverlaySignal: show={shouldShow}; positive={signal.HasPositiveSignal}; reason={signal.Reason}; trigger={trigger}; animated={animated}; " +
            $"active={ViewModel.HasActiveSession}; busy={ViewModel.IsBusy}; playbackVisible={ViewModel.IsPlaybackVisible}; " +
            $"title={ViewModel.TrackTitle}; artist={ViewModel.ArtistName}; current={ViewModel.CurrentLine}; next={ViewModel.NextLine}; " +
            $"viz={ViewModel.ShowLyricVisualizer}; preRoll={ViewModel.IsPreRollActive}; waiting={ViewModel.IsWaitingDotsActive}; countdown={ViewModel.IsCountdownActive}");
    }

    private OverlayContentMode DetermineOverlayMode()
    {
        if (physicalTrackModeActive)
        {
            return OverlayContentMode.Lyrics;
        }

        if (ViewModel.VisualizerVisibility == Visibility.Visible)
        {
            return OverlayContentMode.Visualizer;
        }

        return ViewModel.PreRollVisibility == Visibility.Visible
            ? OverlayContentMode.PreRoll
            : OverlayContentMode.Lyrics;
    }

    private void StopOverlayModeTransition()
    {
        if (overlayModeStoryboard is null)
        {
            return;
        }

        overlayModeStoryboard.Completed -= OnOverlayModeTransitionCompleted;
        overlayModeStoryboard.Stop();
        overlayModeStoryboard = null;
    }

    private void StartWaitingDotsAnimation()
    {
        if (isWaitingDotsStoryboardRunning)
        {
            return;
        }

        ResetWaitingDotsState();
        if (Resources["WaitingDotsStoryboard"] is Storyboard dotsStoryboard)
        {
            dotsStoryboard.Begin();
            isWaitingDotsStoryboardRunning = true;
        }
    }

    private void UpdatePreRollInnerState(bool animated)
    {
        var showWaiting = ViewModel.WaitingDotsVisibility == Visibility.Visible;
        var showCountdown = ViewModel.CountdownVisibility == Visibility.Visible;

        if (!animated)
        {
            StopPreRollInnerTransition();
            WaitingDotsLayer.Opacity = showWaiting ? 1 : 0;
            WaitingDotsTranslate.Y = showWaiting ? 0 : PreRollTransitionYOffset;
            CountdownTextLayer.Opacity = showCountdown ? 1 : 0;
            CountdownTranslate.Y = showCountdown ? 0 : PreRollTransitionYOffset;

            if (showWaiting)
            {
                StartWaitingDotsAnimation();
            }
            else
            {
                StopWaitingDotsAnimation();
            }

            return;
        }

        StopPreRollInnerTransition();
        var duration = new Duration(TimeSpan.FromSeconds(Math.Clamp(ViewModel.LineAnimationDurationSeconds * 0.6, 0.1, 0.3)));
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var storyboard = new Storyboard();
        storyboard.Children.Add(MakeOpacityAnim(WaitingDotsLayer, WaitingDotsLayer.Opacity, showWaiting ? 1 : 0, duration, easing));
        storyboard.Children.Add(MakeDoubleAnim(WaitingDotsTranslate, nameof(TranslateTransform.Y), WaitingDotsTranslate.Y, showWaiting ? 0 : PreRollTransitionYOffset, duration, easing));
        storyboard.Children.Add(MakeOpacityAnim(CountdownTextLayer, CountdownTextLayer.Opacity, showCountdown ? 1 : 0, duration, easing));
        storyboard.Children.Add(MakeDoubleAnim(CountdownTranslate, nameof(TranslateTransform.Y), CountdownTranslate.Y, showCountdown ? 0 : PreRollTransitionYOffset, duration, easing));

        preRollInnerStoryboard = storyboard;
        preRollInnerStoryboard.Completed += OnPreRollInnerTransitionCompleted;
        preRollInnerStoryboard.Begin();

        if (showWaiting)
        {
            StartWaitingDotsAnimation();
        }
        else
        {
            StopWaitingDotsAnimation();
        }
    }

    private void OnPreRollInnerTransitionCompleted(object? sender, object e)
    {
        StopPreRollInnerTransition();
    }

    private void StopPreRollInnerTransition()
    {
        if (preRollInnerStoryboard is null)
        {
            return;
        }

        preRollInnerStoryboard.Completed -= OnPreRollInnerTransitionCompleted;
        preRollInnerStoryboard.Stop();
        preRollInnerStoryboard = null;
    }

    private void ApplyLineBrushPalette()
    {
        palettePreviousColor = GetBrushColor(ViewModel.PreviousLineBrush, palettePreviousColor);
        paletteCurrentColor = GetBrushColor(ViewModel.CurrentLineBrush, paletteCurrentColor);
        paletteNextColor = GetBrushColor(ViewModel.NextLineBrush, paletteNextColor);
        paletteShadowColor = GetBrushColor(ViewModel.TextShadowBrush, paletteShadowColor);

        if (!isLineTransitionRunning)
        {
            previousLineMainBrush.Color = palettePreviousColor;
            currentLineMainBrush.Color = paletteCurrentColor;
            nextLineMainBrush.Color = paletteNextColor;
        }

        lineShadowBrush.Color = paletteShadowColor;
        currentLineShadowBrush.Color = EnhanceCurrentLineShadowColor(paletteShadowColor);

        PreviousLineText.Foreground = previousLineMainBrush;
        CurrentLineText.Foreground = currentLineMainBrush;
        NextLineText.Foreground = nextLineMainBrush;

        PreviousLineShadowText.Foreground = lineShadowBrush;
        CurrentLineShadowText.Foreground = currentLineShadowBrush;
        NextLineShadowText.Foreground = lineShadowBrush;
    }

    private static Color GetBrushColor(Brush brush, Color fallback)
    {
        return brush is SolidColorBrush solidBrush
            ? solidBrush.Color
            : fallback;
    }

    private static Color EnhanceCurrentLineShadowColor(Color baseShadow)
    {
        var boostedAlpha = Math.Clamp((int)Math.Round(baseShadow.A * 1.55), 96, 238);
        return Color.FromArgb((byte)boostedAlpha, baseShadow.R, baseShadow.G, baseShadow.B);
    }

    private void InitializeDisplayedLyricsState()
    {
        displayedCurrentLine = ViewModel.CurrentLine ?? string.Empty;
        displayedNextLine = ViewModel.NextLine ?? string.Empty;
        SetLineLayerTexts(string.Empty, displayedCurrentLine, displayedNextLine);
        ApplyRestingLineLayerVisuals();
    }

    private bool ShouldUsePhysicalTrack()
    {
        return ViewModel.HasSyncedTimeline && ViewModel.TimelineLines.Count > 0;
    }

    private bool TryStartPhysicalTrackTransition()
    {
        if (!ShouldUsePhysicalTrack())
        {
            return false;
        }

        StartPhysicalTrackTransition(forceImmediate: false);
        return true;
    }

    private void UpdatePhysicalTrackMode()
    {
        var shouldUsePhysicalTrack = ShouldUsePhysicalTrack();
        var modeWasActive = physicalTrackModeActive;
        SetPhysicalTrackModeActive(shouldUsePhysicalTrack);

        if (shouldUsePhysicalTrack)
        {
            StopActiveLineTransition();
            isLineTransitionRunning = false;
            var plan = BuildPhysicalTrackPlan();
            if (!modeWasActive || NeedsPhysicalTrackRebuild(plan))
            {
                var forceImmediate = !modeWasActive || physicalRenderedIndex < 0;
                RebuildPhysicalTrack(forceImmediate, plan);
            }
            else
            {
                activePhysicalTrackPlan = plan;
                SyncPhysicalTrackEntries(plan);
            }

            return;
        }

        StopActivePhysicalTrackTransition();
        activePhysicalTrackPlan = new PhysicalTrackPlan(Array.Empty<PhysicalTrackEntry>(), 0, 0, false);
        physicalTrackInitialized = false;
        physicalRenderedIndex = -1;
        LyricsTrackSurface.Children.Clear();
        physicalLyricItems.Clear();
        physicalTrackVisualizerCanvas = null;
        activeVisualizerCanvas = null;
        visualizerLinesDirty = true;
        ApplyRestingLineLayerVisuals();
    }

    private void SetPhysicalTrackModeActive(bool active)
    {
        if (physicalTrackModeActive == active)
        {
            return;
        }

        physicalTrackModeActive = active;
        LyricsTrackSurface.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        var legacyVisibility = active ? Visibility.Collapsed : Visibility.Visible;
        PreviousLineLayer.Visibility = legacyVisibility;
        CurrentLineLayer.Visibility = legacyVisibility;
        NextLineLayer.Visibility = legacyVisibility;

        if (!active)
        {
            StopPhysicalTrackFadeTimer();
        }
    }

    private void EnsurePhysicalTrackFadeTimer()
    {
        if (physicalTrackFadeTimer is not null)
        {
            return;
        }

        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        if (dispatcherQueue is null)
        {
            return;
        }

        physicalTrackFadeTimer = dispatcherQueue.CreateTimer();
        physicalTrackFadeTimer.Interval = TimeSpan.FromMilliseconds(16);
        physicalTrackFadeTimer.IsRepeating = true;
        physicalTrackFadeTimer.Tick += OnPhysicalTrackFadeTimerTick;
    }

    private void StartPhysicalTrackFadeTimer()
    {
        EnsurePhysicalTrackFadeTimer();
        if (physicalTrackFadeTimer is not null && !physicalTrackFadeTimer.IsRunning)
        {
            physicalTrackFadeTimer.Start();
        }
    }

    private void StopPhysicalTrackFadeTimer()
    {
        if (physicalTrackFadeTimer is not null && physicalTrackFadeTimer.IsRunning)
        {
            physicalTrackFadeTimer.Stop();
        }
    }

    private void OnPhysicalTrackFadeTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!physicalTrackModeActive || physicalLyricItems.Count == 0)
        {
            StopPhysicalTrackFadeTimer();
            return;
        }

        UpdatePhysicalTrackViewportFade();
        if (activePhysicalTrackStoryboard is null)
        {
            StopPhysicalTrackFadeTimer();
        }
    }

    private void LyricsCanvas_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLyricsHorizontalAlignment();

        if (!physicalTrackModeActive)
        {
            return;
        }

        RebuildPhysicalTrack(forceImmediate: true, activePhysicalTrackPlan.Entries.Count > 0 ? activePhysicalTrackPlan : null);
    }

    private PhysicalTrackPlan BuildPhysicalTrackPlan()
    {
        var identity = $"{ViewModel.TrackTitle}\u001F{ViewModel.ArtistName}";
        if (!string.Equals(identity, physicalTrackIdentity, StringComparison.Ordinal))
        {
            physicalTrackIdentity = identity;
            includeVisualizerLeadForCurrentTrack = false;
            includePreRollLeadForCurrentTrack = false;
            preRollLeadText = WaitingDotsText;
            preRollLeadText = "• • •";
        }

        var lineBandHeight = GetLineBandHeight();
        var entries = new List<PhysicalTrackEntry>();

        if (ViewModel.VisualizerVisibility == Visibility.Visible)
        {
            includeVisualizerLeadForCurrentTrack = true;
            entries.Add(new PhysicalTrackEntry(string.Empty, IsVisualizer: true));
            return new PhysicalTrackPlan(entries, ActiveIndex: 0, lineBandHeight, ContainsVisualizer: true);
        }

        if (!ViewModel.HasSyncedTimeline || ViewModel.TimelineLines.Count == 0)
        {
            return new PhysicalTrackPlan(entries, ActiveIndex: 0, lineBandHeight, ContainsVisualizer: false);
        }

        var offset = 0;
        var containsVisualizer = false;
        if (includeVisualizerLeadForCurrentTrack)
        {
            entries.Add(new PhysicalTrackEntry(string.Empty, IsVisualizer: true));
            offset++;
            containsVisualizer = true;
        }

        if (ViewModel.IsPreRollActive)
        {
            includePreRollLeadForCurrentTrack = true;
            preRollLeadText = string.IsNullOrWhiteSpace(ViewModel.CurrentLine) ? "• • •" : ViewModel.CurrentLine;
            if (string.IsNullOrWhiteSpace(preRollLeadText))
            {
                preRollLeadText = WaitingDotsText;
            }

            entries.Add(new PhysicalTrackEntry(preRollLeadText, IsVisualizer: false));
            offset++;
        }
        else if (includePreRollLeadForCurrentTrack)
        {
            entries.Add(new PhysicalTrackEntry(preRollLeadText, IsVisualizer: false));
            offset++;
        }

        foreach (var line in ViewModel.TimelineLines)
        {
            entries.Add(new PhysicalTrackEntry(line.Text, IsVisualizer: false));
        }

        if (entries.Count == 0)
        {
            return new PhysicalTrackPlan(entries, ActiveIndex: 0, lineBandHeight, containsVisualizer);
        }

        var activeIndex = ViewModel.IsPreRollActive
            ? Math.Clamp(offset - 1, 0, entries.Count - 1)
            : Math.Clamp(ViewModel.ActiveLineIndex, 0, ViewModel.TimelineLines.Count - 1) + offset;

        activeIndex = Math.Clamp(activeIndex, 0, entries.Count - 1);
        return new PhysicalTrackPlan(entries, activeIndex, lineBandHeight, containsVisualizer);
    }

    private int GetActivePhysicalPlanIndex()
    {
        if (activePhysicalTrackPlan.Entries.Count == 0)
        {
            return 0;
        }

        return Math.Clamp(activePhysicalTrackPlan.ActiveIndex, 0, activePhysicalTrackPlan.Entries.Count - 1);
    }

    private double GetPhysicalItemActiveScale(PhysicalLyricItem item)
    {
        if (item.IsVisualizer)
        {
            return 1;
        }

        var desired = Math.Clamp(GetCurrentLineHeight() / Math.Max(1, GetNextLineHeight()), 1, 2.2);
        if (desired <= 1)
        {
            return desired;
        }

        item.MainText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = Math.Max(item.MainText.ActualWidth, item.MainText.DesiredSize.Width);
        if (textWidth <= 1)
        {
            return desired;
        }

        var availableWidth = Math.Max(120, LyricsCanvas.ActualWidth - 6);
        var maxScaleToFit = availableWidth / textWidth;
        return Math.Clamp(Math.Min(desired, maxScaleToFit), 1, desired);
    }

    private void SyncPhysicalTrackEntries(PhysicalTrackPlan plan)
    {
        if (physicalLyricItems.Count != plan.Entries.Count)
        {
            return;
        }

        for (var i = 0; i < plan.Entries.Count; i++)
        {
            var item = physicalLyricItems[i];
            var entry = plan.Entries[i];
            if (!item.IsVisualizer)
            {
                if (!string.Equals(item.MainText.Text, entry.Text, StringComparison.Ordinal))
                {
                    item.MainText.Text = entry.Text;
                }

                if (!string.Equals(item.ShadowText.Text, entry.Text, StringComparison.Ordinal))
                {
                    item.ShadowText.Text = entry.Text;
                }
            }

            Canvas.SetTop(item.Layer, i * physicalLineStep);
        }
    }

    private bool NeedsPhysicalTrackRebuild(PhysicalTrackPlan plan)
    {
        if (!physicalTrackInitialized || physicalLyricItems.Count != plan.Entries.Count)
        {
            return true;
        }

        if (Math.Abs(plan.LineBandHeight - activePhysicalTrackPlan.LineBandHeight) > 0.5)
        {
            return true;
        }

        for (var i = 0; i < plan.Entries.Count; i++)
        {
            if (physicalLyricItems[i].IsVisualizer != plan.Entries[i].IsVisualizer)
            {
                return true;
            }
        }

        return false;
    }

    private void RebuildPhysicalTrack(bool forceImmediate, PhysicalTrackPlan? precomputedPlan = null)
    {
        if (!ShouldUsePhysicalTrack())
        {
            return;
        }

        var previousPlan = activePhysicalTrackPlan;
        var previousRenderedIndex = physicalRenderedIndex;
        StopActivePhysicalTrackTransition();
        physicalLyricItems.Clear();
        LyricsTrackSurface.Children.Clear();
        physicalTrackVisualizerCanvas = null;
        activeVisualizerCanvas = null;

        var plan = precomputedPlan ?? BuildPhysicalTrackPlan();
        activePhysicalTrackPlan = plan;
        if (plan.Entries.Count == 0)
        {
            physicalTrackInitialized = false;
            physicalRenderedIndex = -1;
            visualizerLinesDirty = true;
            return;
        }

        var lineBandHeight = plan.LineBandHeight;
        var margin = Math.Clamp(ViewModel.NextLineTopMargin, 0, 48);
        physicalLineStep = lineBandHeight + margin;
        var viewportHeight = lineBandHeight + physicalLineStep;
        var canvasWidth = Math.Max(1, LyricsCanvas.ActualWidth);
        var trackSurfaceWidth = canvasWidth + PhysicalTrackLayerHorizontalOverflow;
        var clipPadX = PhysicalTrackClipHorizontalPadding;
        var clipPadY = PhysicalTrackClipVerticalPadding;

        LyricsCanvas.Height = viewportHeight;
        LyricsTrackSurface.Width = trackSurfaceWidth;
        LyricsTrackSurface.Height = (plan.Entries.Count * physicalLineStep) + lineBandHeight;
        LyricsCanvas.Clip = new RectangleGeometry
        {
            Rect = new Rect(
                -clipPadX,
                -clipPadY,
                trackSurfaceWidth + (clipPadX * 2),
                viewportHeight + (clipPadY * 2))
        };

        for (var i = 0; i < plan.Entries.Count; i++)
        {
            var item = CreatePhysicalLyricItem(plan.Entries[i], lineBandHeight, trackSurfaceWidth);
            physicalLyricItems.Add(item);
            Canvas.SetTop(item.Layer, i * physicalLineStep);
            LyricsTrackSurface.Children.Add(item.Layer);
        }

        ApplyCurrentHorizontalLayoutSnapshot();

        visualizerLinesDirty = true;
        physicalTrackInitialized = true;
        physicalRenderedIndex = -1;
        if (!forceImmediate &&
            previousRenderedIndex >= 0 &&
            previousRenderedIndex < plan.Entries.Count &&
            previousRenderedIndex < previousPlan.Entries.Count &&
            previousPlan.Entries[previousRenderedIndex].IsVisualizer == plan.Entries[previousRenderedIndex].IsVisualizer)
        {
            physicalRenderedIndex = previousRenderedIndex;
            ApplyPhysicalTrackFinalState(previousRenderedIndex);
        }

        StartPhysicalTrackTransition(forceImmediate);
    }

    private PhysicalLyricItem CreatePhysicalLyricItem(PhysicalTrackEntry entry, double lineBandHeight, double width)
    {
        var mainBrush = new SolidColorBrush(paletteNextColor);
        var shadowBrush = new SolidColorBrush(paletteShadowColor);

        var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        var layer = new Grid
        {
            Width = width,
            Height = lineBandHeight,
            IsHitTestVisible = false,
            Opacity = 1
        };

        var contentHost = new Grid
        {
            Width = width,
            Height = lineBandHeight,
            RenderTransform = scale,
            RenderTransformOrigin = new Point(0, 0),
            IsHitTestVisible = false,
            Opacity = 0.72
        };

        var shadowText = new TextBlock
        {
            Text = entry.Text,
            Margin = new Thickness(1, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            MaxLines = 1,
            TextTrimming = TextTrimming.None,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = shadowBrush,
            RenderTransformOrigin = new Point(0, 0)
        };
        var mainText = new TextBlock
        {
            Text = entry.Text,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            MaxLines = 1,
            TextTrimming = TextTrimming.None,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = mainBrush,
            RenderTransformOrigin = new Point(0, 0)
        };

        if (entry.IsVisualizer)
        {
            mainText.Opacity = 0;
            shadowText.Opacity = 0;
            var vizCanvas = new Canvas
            {
                Height = lineBandHeight,
                Width = width,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false
            };
            contentHost.Children.Add(vizCanvas);
            contentHost.Children.Add(shadowText);
            contentHost.Children.Add(mainText);
            physicalTrackVisualizerCanvas = vizCanvas;
        }
        else
        {
            ApplyNextLineTypography(mainText, shadowText);
            SetTextLayerFontSize(mainText, shadowText, ViewModel.NextLineFontSize);
            contentHost.Children.Add(shadowText);
            contentHost.Children.Add(mainText);
        }

        layer.Children.Add(contentHost);

        return new PhysicalLyricItem
        {
            Layer = layer,
            ContentHost = contentHost,
            MainText = mainText,
            ShadowText = shadowText,
            MainBrush = mainBrush,
            ShadowBrush = shadowBrush,
            Scale = scale,
            IsVisualizer = entry.IsVisualizer
        };
    }

    private void StartPhysicalTrackTransition(bool forceImmediate)
    {
        if (!ShouldUsePhysicalTrack())
        {
            return;
        }

        var plan = BuildPhysicalTrackPlan();
        if (NeedsPhysicalTrackRebuild(plan))
        {
            RebuildPhysicalTrack(forceImmediate: true, plan);
            return;
        }

        activePhysicalTrackPlan = plan;
        SyncPhysicalTrackEntries(plan);

        if (physicalLyricItems.Count == 0)
        {
            return;
        }

        var targetIndex = Math.Clamp(plan.ActiveIndex, 0, physicalLyricItems.Count - 1);
        if (physicalRenderedIndex < 0)
        {
            StartPhysicalTrackIntroFromBottom(targetIndex);
            return;
        }

        if (forceImmediate)
        {
            ApplyPhysicalTrackFinalState(targetIndex);
            return;
        }

        if (targetIndex == physicalRenderedIndex)
        {
            ApplyPhysicalTrackFinalState(targetIndex);
            return;
        }

        StopActivePhysicalTrackTransition();

        var oldIndex = physicalRenderedIndex;
        var oldItem = physicalLyricItems[oldIndex];
        var newItem = physicalLyricItems[targetIndex];
        var newActiveScale = GetPhysicalItemActiveScale(newItem);
        var distanceSteps = Math.Abs(targetIndex - oldIndex);
        var durationSeconds = Math.Clamp(
            ViewModel.LineAnimationDurationSeconds * (0.75 + Math.Max(0, distanceSteps - 1) * 0.22),
            0.12,
            1.9);
        var duration = TimeSpan.FromSeconds(durationSeconds);
        var timelineDuration = new Duration(duration);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var storyboard = new Storyboard();
        storyboard.Children.Add(MakeTranslateYAnim(
            LyricsTrackTranslate,
            LyricsTrackTranslate.Y,
            -(targetIndex * physicalLineStep),
            timelineDuration,
            easing));

        storyboard.Children.Add(MakeScaleAnim(oldItem.Scale, nameof(ScaleTransform.ScaleX), oldItem.Scale.ScaleX, 1, timelineDuration, easing));
        storyboard.Children.Add(MakeScaleAnim(oldItem.Scale, nameof(ScaleTransform.ScaleY), oldItem.Scale.ScaleY, 1, timelineDuration, easing));
        storyboard.Children.Add(MakeScaleAnim(newItem.Scale, nameof(ScaleTransform.ScaleX), newItem.Scale.ScaleX, newActiveScale, timelineDuration, easing));
        storyboard.Children.Add(MakeScaleAnim(newItem.Scale, nameof(ScaleTransform.ScaleY), newItem.Scale.ScaleY, newActiveScale, timelineDuration, easing));

        storyboard.Children.Add(MakeOpacityAnim(oldItem.ContentHost, oldItem.ContentHost.Opacity, 0, timelineDuration, easing));
        storyboard.Children.Add(MakeOpacityAnim(newItem.ContentHost, newItem.ContentHost.Opacity, 1, timelineDuration, easing));
        storyboard.Children.Add(MakeColorAnim(oldItem.MainBrush, oldItem.MainBrush.Color, palettePreviousColor, timelineDuration, easing));
        storyboard.Children.Add(MakeColorAnim(newItem.MainBrush, newItem.MainBrush.Color, paletteCurrentColor, timelineDuration, easing));
        storyboard.Children.Add(MakeColorAnim(oldItem.ShadowBrush, oldItem.ShadowBrush.Color, paletteShadowColor, timelineDuration, easing));
        storyboard.Children.Add(MakeColorAnim(newItem.ShadowBrush, newItem.ShadowBrush.Color, EnhanceCurrentLineShadowColor(paletteShadowColor), timelineDuration, easing));

        activePhysicalTrackStoryboard = storyboard;
        activePhysicalTrackStoryboard.Completed += OnPhysicalTrackTransitionCompleted;
        StartPhysicalTrackFadeTimer();
        activePhysicalTrackStoryboard.Begin();
    }

    private void StartPhysicalTrackIntroFromBottom(int targetIndex)
    {
        if (physicalLyricItems.Count == 0)
        {
            return;
        }

        StopActivePhysicalTrackTransition();
        ApplyPhysicalTrackFinalState(targetIndex);

        var clampedTarget = Math.Clamp(targetIndex, 0, physicalLyricItems.Count - 1);
        var currentItem = physicalLyricItems[clampedTarget];
        var nextItem = clampedTarget + 1 < physicalLyricItems.Count
            ? physicalLyricItems[clampedTarget + 1]
            : null;

        var finalY = LyricsTrackTranslate.Y;
        var introOffset = Math.Max(physicalLineStep, GetLineBandHeight());
        LyricsTrackTranslate.Y = finalY + introOffset;

        currentItem.ContentHost.Opacity = 0;
        if (nextItem is not null)
        {
            nextItem.ContentHost.Opacity = 0;
        }

        var duration = new Duration(TimeSpan.FromSeconds(Math.Clamp(ViewModel.LineAnimationDurationSeconds * 0.9, 0.18, 0.5)));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();
        storyboard.Children.Add(MakeTranslateYAnim(
            LyricsTrackTranslate,
            LyricsTrackTranslate.Y,
            finalY,
            duration,
            easing));
        storyboard.Children.Add(MakeOpacityAnim(currentItem.ContentHost, 0, 1, duration, easing));
        if (nextItem is not null)
        {
            storyboard.Children.Add(MakeOpacityAnim(nextItem.ContentHost, 0, 0.72, duration, easing));
        }

        activePhysicalTrackStoryboard = storyboard;
        activePhysicalTrackStoryboard.Completed += OnPhysicalTrackTransitionCompleted;
        StartPhysicalTrackFadeTimer();
        activePhysicalTrackStoryboard.Begin();
    }

    private void ApplyPhysicalTrackFinalState(int targetIndex)
    {
        if (physicalLyricItems.Count == 0)
        {
            return;
        }

        var clamped = Math.Clamp(targetIndex, 0, physicalLyricItems.Count - 1);
        for (var i = 0; i < physicalLyricItems.Count; i++)
        {
            var item = physicalLyricItems[i];
            var isCurrent = i == clamped;
            ApplyNextLineTypography(item.MainText, item.ShadowText);
            SetTextLayerFontSize(item.MainText, item.ShadowText, ViewModel.NextLineFontSize);
            var activeScale = GetPhysicalItemActiveScale(item);
            item.Scale.ScaleX = isCurrent ? activeScale : 1;
            item.Scale.ScaleY = isCurrent ? activeScale : 1;
            item.ContentHost.Opacity = isCurrent ? 1 : (i > clamped ? 0.72 : 0);
            item.MainBrush.Color = isCurrent ? paletteCurrentColor : (i < clamped ? palettePreviousColor : paletteNextColor);
            item.ShadowBrush.Color = isCurrent
                ? EnhanceCurrentLineShadowColor(paletteShadowColor)
                : paletteShadowColor;
        }

        LyricsTrackTranslate.Y = -(clamped * physicalLineStep);
        physicalRenderedIndex = clamped;
        ApplyPhysicalTrackAlignment(GetLyricsHorizontalLayout());
        UpdatePhysicalTrackViewportFade();
        if (activePhysicalTrackPlan.ContainsVisualizer || ViewModel.VisualizerVisibility == Visibility.Visible)
        {
            UpdateVisualizerState();
        }

        UpdateDebugOverlayInfo();
    }

    private void OnPhysicalTrackTransitionCompleted(object? sender, object e)
    {
        StopActivePhysicalTrackTransition();
        if (physicalLyricItems.Count == 0)
        {
            return;
        }

        var targetIndex = Math.Clamp(GetActivePhysicalPlanIndex(), 0, physicalLyricItems.Count - 1);
        ApplyPhysicalTrackFinalState(targetIndex);
        if (!hasPendingLineTransition)
        {
            return;
        }

        hasPendingLineTransition = false;
        StartPhysicalTrackTransition(forceImmediate: false);
    }

    private void StopActivePhysicalTrackTransition()
    {
        if (activePhysicalTrackStoryboard is null)
        {
            return;
        }

        activePhysicalTrackStoryboard.Completed -= OnPhysicalTrackTransitionCompleted;
        activePhysicalTrackStoryboard.Stop();
        activePhysicalTrackStoryboard = null;
    }

    private void UpdatePhysicalTrackViewportFade()
    {
        if (!physicalTrackModeActive || physicalLyricItems.Count == 0)
        {
            return;
        }

        var viewportHeight = Math.Max(1, LyricsCanvas.Height > 0 ? LyricsCanvas.Height : LyricsCanvas.ActualHeight);
        var configuredFadeZone = Math.Clamp(
            ViewModel?.ViewportFadeZone ?? PhysicalTrackViewportFadeZoneDefault,
            0,
            180);
        if (configuredFadeZone < 0.5)
        {
            foreach (var item in physicalLyricItems)
            {
                item.Layer.Opacity = 1;
            }

            return;
        }

        var fadeZone = Math.Max(1, Math.Min(configuredFadeZone, viewportHeight * 0.45));
        var fadeSoftness = Math.Clamp(ViewModel?.ViewportFadeSoftness ?? 1.0, 0.4, 2.2);
        var topExpansion = Math.Min(fadeZone * 0.72, Math.Max(10, physicalLineStep * 0.72));
        var viewportTop = -topExpansion;
        var viewportBottom = viewportHeight;
        var translateY = LyricsTrackTranslate.Y;

        foreach (var item in physicalLyricItems)
        {
            var top = Canvas.GetTop(item.Layer) + translateY;
            var bottom = top + item.Layer.Height;

            // Fade outside/near edges while keeping center lines untouched.
            var topRamp = (top - (viewportTop - fadeZone)) / fadeZone;
            var bottomRamp = ((viewportBottom + fadeZone) - bottom) / fadeZone;
            var edgeOpacity = ApplyFadeCurve(SmoothStep01(Math.Min(topRamp, bottomRamp)), fadeSoftness);

            var visibleTop = Math.Max(top, viewportTop);
            var visibleBottom = Math.Min(bottom, viewportBottom);
            var visibleHeight = Math.Max(0, visibleBottom - visibleTop);
            var visibleRatio = visibleHeight / Math.Max(1, item.Layer.Height);
            var clipOpacity = ApplyFadeCurve(SmoothStep01(visibleRatio), fadeSoftness);

            item.Layer.Opacity = Math.Clamp(edgeOpacity * clipOpacity, 0, 1);
        }
    }

    private static double SmoothStep01(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - (2 * t));
    }

    private static double ApplyFadeCurve(double value, double softness)
    {
        var v = Math.Clamp(value, 0, 1);
        var s = Math.Clamp(softness, 0.4, 2.2);
        return Math.Pow(v, s);
    }

    private void QueueLineTransition()
    {
        if (ShouldUsePhysicalTrack())
        {
            if (activePhysicalTrackStoryboard is not null)
            {
                hasPendingLineTransition = true;
                return;
            }

            StartPhysicalTrackTransition(forceImmediate: false);
            return;
        }

        if (isLineTransitionRunning)
        {
            hasPendingLineTransition = true;
            return;
        }

        StartLineTransition();
    }

    private void StartLineTransition()
    {
        if (TryStartPhysicalTrackTransition())
        {
            return;
        }

        if (TryStartLineTransitionModern())
        {
            return;
        }

        var targetCurrent = ViewModel.CurrentLine ?? string.Empty;
        var targetNext = ViewModel.NextLine ?? string.Empty;

        if (string.Equals(targetCurrent, displayedCurrentLine, StringComparison.Ordinal) &&
            string.Equals(targetNext, displayedNextLine, StringComparison.Ordinal))
        {
            return;
        }

        var canSlide = !string.IsNullOrEmpty(displayedNextLine) &&
            string.Equals(targetCurrent, displayedNextLine, StringComparison.Ordinal);
        if (!canSlide)
        {
            ApplyImmediateLineState(targetCurrent, targetNext);
            return;
        }

        StopActiveLineTransition();
        pendingTargetCurrentLine = targetCurrent;
        pendingTargetNextLine = targetNext;

        // Текущая строка становится "предыдущей" (уходит вверх)
        // Следующая строка поднимается на место текущей
        // Новая "следующая" появляется снизу

        SetLineLayerTexts(targetNext, displayedCurrentLine, displayedNextLine);

        // Measure heights for canvas positioning.
        var currentHeight = GetCurrentLineHeight();
        var nextHeight = GetNextLineHeight();
        var lineBandHeight = Math.Max(currentHeight, nextHeight);
        var margin = Math.Clamp(ViewModel.NextLineTopMargin, 0, 48);

        // Стартовые позиции
        var fromCurrentTop = canvasCurrentTop;
        var fromNextTop = canvasNextTop;

        // Финальные позиции: следующая строка едет на место текущей
        var toCurrentTop = canvasCurrentTop - lineBandHeight - LineExitPadding;         // текущая уходит вверх
        var toNextTop = canvasCurrentTop;                                  // следующая встаёт на место текущей
        var toPrevTop = canvasCurrentTop + lineBandHeight + margin;        // бывшая "следующая" становится новой "следующей"

        // Canvas.Top фиксируем сразу в финале, движение выполняем через TranslateTransform.Y.
        Canvas.SetTop(CurrentLineLayer, toCurrentTop);
        Canvas.SetTop(NextLineLayer, toNextTop);
        Canvas.SetTop(PreviousLineLayer, toPrevTop);

        var fromPrevTop = fromNextTop + lineBandHeight + margin;
        CurrentLineTranslate.Y = fromCurrentTop - toCurrentTop;
        NextLineTranslate.Y = fromNextTop - toNextTop;
        PreviousLineTranslate.Y = fromPrevTop - toPrevTop;

        // Избегаем клиппинга растущего текста во время перехода.
        SetLineLayerHeights(currentHeight, nextHeight);

        // Цвета
        currentLineMainBrush.Color = paletteCurrentColor;
        nextLineMainBrush.Color = paletteNextColor;
        previousLineMainBrush.Color = paletteNextColor;

        PreviousLineLayer.Opacity = 0;
        CurrentLineLayer.Opacity = 1;
        NextLineLayer.Opacity = 0.72;

        var duration = TimeSpan.FromSeconds(Math.Clamp(ViewModel.LineAnimationDurationSeconds, 0.25, 1.2));
        var timelineDuration = new Duration(duration);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var storyboard = new Storyboard();

        // --- CurrentLineLayer: едет вверх и исчезает ---
        storyboard.Children.Add(MakeTranslateYAnim(CurrentLineTranslate, CurrentLineTranslate.Y, 0, timelineDuration, easing));
        storyboard.Children.Add(MakeOpacityAnim(CurrentLineLayer, 1, 0, timelineDuration, easing));
        storyboard.Children.Add(MakeColorAnim(currentLineMainBrush, paletteCurrentColor, palettePreviousColor, timelineDuration, easing));

        // --- NextLineLayer: едет вверх на место текущей и становится яркой ---
        storyboard.Children.Add(MakeTranslateYAnim(NextLineTranslate, NextLineTranslate.Y, 0, timelineDuration, easing));
        storyboard.Children.Add(MakeOpacityAnim(NextLineLayer, 0.72, 1, timelineDuration, easing));
        storyboard.Children.Add(MakeColorAnim(nextLineMainBrush, paletteNextColor, paletteCurrentColor, timelineDuration, easing));

        // --- PreviousLineLayer: появляется снизу как новая следующая строка ---
        storyboard.Children.Add(MakeTranslateYAnim(PreviousLineTranslate, PreviousLineTranslate.Y, 0, timelineDuration, easing));
        storyboard.Children.Add(MakeOpacityAnim(PreviousLineLayer, 0, 0.72, timelineDuration, easing));
        LyricsCanvas.Height = Math.Max(LyricsCanvas.Height, fromPrevTop + lineBandHeight + 4);

        activeLineTransitionStoryboard = storyboard;
        activeLineTransitionStoryboard.Completed += OnLineTransitionCompleted;
        isLineTransitionRunning = true;
        activeLineTransitionStoryboard.Begin();
    }

    private static DoubleAnimation MakeTranslateYAnim(
        TranslateTransform target,
        double from,
        double to,
        Duration duration,
        EasingFunctionBase easing)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, nameof(TranslateTransform.Y));
        return anim;
    }

    private bool TryStartLineTransitionModern()
    {
        var targetCurrent = ViewModel.CurrentLine ?? string.Empty;
        var targetNext = ViewModel.NextLine ?? string.Empty;

        if (string.Equals(targetCurrent, displayedCurrentLine, StringComparison.Ordinal) &&
            string.Equals(targetNext, displayedNextLine, StringComparison.Ordinal))
        {
            return true;
        }

        var hasRenderableState =
            !string.IsNullOrWhiteSpace(displayedCurrentLine) ||
            !string.IsNullOrWhiteSpace(displayedNextLine) ||
            !string.IsNullOrWhiteSpace(targetCurrent) ||
            !string.IsNullOrWhiteSpace(targetNext);
        if (!hasRenderableState)
        {
            ApplyImmediateLineState(targetCurrent, targetNext);
            return true;
        }

        StopActiveLineTransition();
        pendingTargetCurrentLine = targetCurrent;
        pendingTargetNextLine = targetNext;
        SetLineLayerTexts(targetNext, displayedCurrentLine, targetCurrent);

        var currentHeight = GetCurrentLineHeight();
        var nextHeight = GetNextLineHeight();
        var lineBandHeight = Math.Max(currentHeight, nextHeight);
        var margin = Math.Clamp(ViewModel.NextLineTopMargin, 0, 48);

        var fromCurrentTop = canvasCurrentTop;
        var fromNextTop = canvasNextTop;
        var fromPrevTop = fromNextTop + lineBandHeight + margin;
        var toCurrentTop = fromCurrentTop - lineBandHeight - LineExitPadding;
        var toNextTop = fromCurrentTop;
        var toPrevTop = fromCurrentTop + lineBandHeight + margin;

        // Canvas.Top фиксируем в финале перехода; визуальное движение делаем через TranslateTransform.
        Canvas.SetTop(CurrentLineLayer, toCurrentTop);
        Canvas.SetTop(NextLineLayer, toNextTop);
        Canvas.SetTop(PreviousLineLayer, toPrevTop);
        CurrentLineTranslate.Y = fromCurrentTop - toCurrentTop;
        NextLineTranslate.Y = fromNextTop - toNextTop;
        PreviousLineTranslate.Y = fromPrevTop - toPrevTop;

        // Keep hitboxes in a stable line band so text growth never gets clipped.
        SetLineLayerHeights(currentHeight, nextHeight);
        currentLineMainBrush.Color = paletteCurrentColor;
        nextLineMainBrush.Color = paletteNextColor;
        previousLineMainBrush.Color = paletteNextColor;
        ApplyTransitionLineLayerStyles();

        var outgoingOpacity = string.IsNullOrWhiteSpace(displayedCurrentLine) ? 0 : 1;
        var incomingOpacityStart = string.IsNullOrWhiteSpace(targetCurrent) ? 0 : 0.72;
        var incomingOpacityEnd = string.IsNullOrWhiteSpace(targetCurrent) ? 0 : 1;
        var upcomingOpacityEnd = string.IsNullOrWhiteSpace(targetNext) ? 0 : 0.72;
        var shrinkToNextScale = Math.Clamp(nextHeight / currentHeight, 0.55, 1.0);
        var growToCurrentScale = Math.Clamp(currentHeight / nextHeight, 1.0, 1.9);

        PreviousLineLayer.Opacity = 0;
        CurrentLineLayer.Opacity = outgoingOpacity;
        NextLineLayer.Opacity = incomingOpacityStart;
        SetScale(CurrentLineScale, 1);
        SetScale(NextLineScale, 1);
        SetScale(PreviousLineScale, 1);
        SetTextScale(CurrentLineTextScale, CurrentLineShadowTextScale, 1);
        SetTextScale(NextLineTextScale, NextLineShadowTextScale, 1);
        SetTextScale(PreviousLineTextScale, PreviousLineShadowTextScale, 1);

        var duration = TimeSpan.FromSeconds(Math.Clamp(ViewModel.LineAnimationDurationSeconds, 0.25, 1.2));
        var timelineDuration = new Duration(duration);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var storyboard = new Storyboard();
        storyboard.Children.Add(MakeTranslateYAnim(CurrentLineTranslate, CurrentLineTranslate.Y, 0, timelineDuration, easing));
        AddLayerScaleAnim(
            storyboard,
            CurrentLineScale,
            1,
            shrinkToNextScale,
            timelineDuration,
            easing);
        storyboard.Children.Add(MakeOpacityAnim(CurrentLineLayer, outgoingOpacity, 0, timelineDuration, easing));
        storyboard.Children.Add(MakeColorAnim(currentLineMainBrush, paletteCurrentColor, palettePreviousColor, timelineDuration, easing));

        storyboard.Children.Add(MakeTranslateYAnim(NextLineTranslate, NextLineTranslate.Y, 0, timelineDuration, easing));
        AddLayerScaleAnim(
            storyboard,
            NextLineScale,
            1,
            growToCurrentScale,
            timelineDuration,
            easing);
        storyboard.Children.Add(MakeOpacityAnim(NextLineLayer, incomingOpacityStart, incomingOpacityEnd, timelineDuration, easing));
        storyboard.Children.Add(MakeColorAnim(nextLineMainBrush, paletteNextColor, paletteCurrentColor, timelineDuration, easing));

        storyboard.Children.Add(MakeTranslateYAnim(PreviousLineTranslate, PreviousLineTranslate.Y, 0, timelineDuration, easing));
        storyboard.Children.Add(MakeOpacityAnim(PreviousLineLayer, 0, upcomingOpacityEnd, timelineDuration, easing));

        LyricsCanvas.Height = Math.Max(LyricsCanvas.Height, fromPrevTop + lineBandHeight + 4);

        activeLineTransitionStoryboard = storyboard;
        activeLineTransitionStoryboard.Completed += OnLineTransitionCompleted;
        isLineTransitionRunning = true;
        activeLineTransitionStoryboard.Begin();
        return true;
    }

    private static DoubleAnimation MakeDoubleAnim(DependencyObject target, string property, double from, double to,
        Duration duration, EasingFunctionBase easing)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        return anim;
    }

    private static void AddLayerScaleAnim(
        Storyboard storyboard,
        ScaleTransform layerScale,
        double from,
        double to,
        Duration duration,
        EasingFunctionBase easing)
    {
        storyboard.Children.Add(MakeScaleAnim(layerScale, nameof(ScaleTransform.ScaleX), from, to, duration, easing));
        storyboard.Children.Add(MakeScaleAnim(layerScale, nameof(ScaleTransform.ScaleY), from, to, duration, easing));
    }

    private static DoubleAnimation MakeScaleAnim(
        ScaleTransform target,
        string property,
        double from,
        double to,
        Duration duration,
        EasingFunctionBase easing)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        return anim;
    }

    private static void SetTextScale(ScaleTransform mainScale, ScaleTransform shadowScale, double value)
    {
        mainScale.ScaleX = value;
        mainScale.ScaleY = value;
        shadowScale.ScaleX = value;
        shadowScale.ScaleY = value;
    }

    private static void SetScale(ScaleTransform transform, double value)
    {
        transform.ScaleX = value;
        transform.ScaleY = value;
    }

    private static DoubleAnimation MakeOpacityAnim(UIElement target, double from, double to,
        Duration duration, EasingFunctionBase easing)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, nameof(Opacity));
        return anim;
    }

    private static ColorAnimation MakeColorAnim(SolidColorBrush target, Color from, Color to,
        Duration duration, EasingFunctionBase easing)
    {
        var anim = new ColorAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, nameof(SolidColorBrush.Color));
        return anim;
    }

    private void OnLineTransitionCompleted(object? sender, object e)
    {
        displayedCurrentLine = pendingTargetCurrentLine;
        displayedNextLine = pendingTargetNextLine;
        var currentHeight = GetCurrentLineHeight();
        var nextHeight = GetNextLineHeight();
        var lineBandHeight = Math.Max(currentHeight, nextHeight);
        var margin = Math.Clamp(ViewModel.NextLineTopMargin, 0, 48);
        canvasCurrentTop = 0;
        canvasNextTop = lineBandHeight + margin;

        // Сначала фиксируем базовые значения, чтобы Stop() не дал однокадровый откат.
        Canvas.SetTop(PreviousLineLayer, canvasCurrentTop - lineBandHeight - LineExitPadding);
        Canvas.SetTop(CurrentLineLayer, canvasCurrentTop);
        Canvas.SetTop(NextLineLayer, canvasNextTop);
        PreviousLineTranslate.Y = 0;
        CurrentLineTranslate.Y = 0;
        NextLineTranslate.Y = 0;

        SetLineLayerHeights(currentHeight, nextHeight);
        SetLineLayerTexts(string.Empty, displayedCurrentLine, displayedNextLine);
        previousLineMainBrush.Color = palettePreviousColor;
        currentLineMainBrush.Color = paletteCurrentColor;
        nextLineMainBrush.Color = paletteNextColor;
        ApplyRestingLineLayerStyles();
        SetScale(PreviousLineScale, 1);
        SetScale(CurrentLineScale, 1);
        SetScale(NextLineScale, 1);
        PreviousLineLayer.Opacity = 0;
        CurrentLineLayer.Opacity = 1;
        NextLineLayer.Opacity = string.IsNullOrWhiteSpace(displayedNextLine) ? 0 : 0.72;
        LyricsCanvas.Height = canvasNextTop + lineBandHeight;

        StopActiveLineTransition();
        UpdateDebugOverlayInfo();

        isLineTransitionRunning = false;
        if (!hasPendingLineTransition)
        {
            return;
        }

        hasPendingLineTransition = false;
        StartLineTransition();
    }

    private void SetLineLayerTexts(string previousLine, string currentLine, string nextLine)
    {
        PreviousLineShadowText.Text = previousLine;
        PreviousLineText.Text = previousLine;

        CurrentLineShadowText.Text = currentLine;
        CurrentLineText.Text = currentLine;

        NextLineShadowText.Text = nextLine;
        NextLineText.Text = nextLine;
        UpdateLyricsHorizontalAlignment();
    }

    private void ApplyTransitionLineLayerStyles()
    {
        ApplyCurrentLineTypography(CurrentLineText, CurrentLineShadowText);
        ApplyNextLineTypography(NextLineText, NextLineShadowText);
        ApplyNextLineTypography(PreviousLineText, PreviousLineShadowText);

        SetTextLayerFontSize(CurrentLineText, CurrentLineShadowText, ViewModel.CurrentLineFontSize);
        SetTextLayerFontSize(NextLineText, NextLineShadowText, ViewModel.NextLineFontSize);
        SetTextLayerFontSize(PreviousLineText, PreviousLineShadowText, ViewModel.NextLineFontSize);
        SetTextScale(CurrentLineTextScale, CurrentLineShadowTextScale, 1);
        SetTextScale(NextLineTextScale, NextLineShadowTextScale, 1);
        SetTextScale(PreviousLineTextScale, PreviousLineShadowTextScale, 1);
    }

    private void ApplyRestingLineLayerStyles()
    {
        ApplyNextLineTypography(PreviousLineText, PreviousLineShadowText);
        ApplyCurrentLineTypography(CurrentLineText, CurrentLineShadowText);
        ApplyNextLineTypography(NextLineText, NextLineShadowText);

        SetTextLayerFontSize(PreviousLineText, PreviousLineShadowText, ViewModel.NextLineFontSize);
        SetTextLayerFontSize(CurrentLineText, CurrentLineShadowText, ViewModel.CurrentLineFontSize);
        SetTextLayerFontSize(NextLineText, NextLineShadowText, ViewModel.NextLineFontSize);
        SetTextScale(CurrentLineTextScale, CurrentLineShadowTextScale, 1);
        SetTextScale(NextLineTextScale, NextLineShadowTextScale, 1);
        SetTextScale(PreviousLineTextScale, PreviousLineShadowTextScale, 1);
    }

    private void ApplyCurrentLineTypography(TextBlock text, TextBlock shadow)
    {
        text.FontFamily = currentLineFontFamily;
        shadow.FontFamily = currentLineFontFamily;
        text.FontWeight = FontWeights.Normal;
        shadow.FontWeight = FontWeights.Normal;
    }

    private void ApplyNextLineTypography(TextBlock text, TextBlock shadow)
    {
        text.FontFamily = nextLineFontFamily;
        shadow.FontFamily = nextLineFontFamily;
        text.FontWeight = FontWeights.Normal;
        shadow.FontWeight = FontWeights.Normal;
    }

    private static void SetTextLayerFontSize(TextBlock text, TextBlock shadow, double size)
    {
        text.FontSize = size;
        shadow.FontSize = size;

        var lineHeight = ComputeLineHeight(size);
        text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        shadow.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        text.LineHeight = lineHeight;
        shadow.LineHeight = lineHeight;
    }

    private void ApplyRestingLineLayerVisuals(double? currentHeightOverride = null, double? nextHeightOverride = null)
    {
        var currentHeight = currentHeightOverride ?? GetCurrentLineHeight();
        var nextHeight = nextHeightOverride ?? GetNextLineHeight();
        var lineBandHeight = Math.Max(currentHeight, nextHeight);
        var nextMargin = Math.Clamp(ViewModel.NextLineTopMargin, 0, 48);

        canvasCurrentTop = 0;
        canvasNextTop = lineBandHeight + nextMargin;

        SetLineLayerHeights(currentHeight, nextHeight);

        // Previous line lives above current — hidden by default
        Canvas.SetTop(PreviousLineLayer, canvasCurrentTop - lineBandHeight - LineExitPadding);
        Canvas.SetTop(CurrentLineLayer, canvasCurrentTop);
        Canvas.SetTop(NextLineLayer, canvasNextTop);

        LyricsCanvas.Height = canvasNextTop + lineBandHeight;
        ApplyRestingLineLayerStyles();

        SetScale(PreviousLineScale, 1);
        SetScale(CurrentLineScale, 1);
        SetScale(NextLineScale, 1);
        PreviousLineTranslate.Y = 0;
        CurrentLineTranslate.Y = 0;
        NextLineTranslate.Y = 0;
        PreviousLineLayer.Opacity = 0;
        CurrentLineLayer.Opacity = 1;
        NextLineLayer.Opacity = 0.72;
    }

    private void ApplyImmediateLineState(string targetCurrent, string targetNext)
    {
        StopActiveLineTransition();
        pendingTargetCurrentLine = targetCurrent;
        pendingTargetNextLine = targetNext;
        displayedCurrentLine = targetCurrent;
        displayedNextLine = targetNext;
        SetLineLayerTexts(string.Empty, displayedCurrentLine, displayedNextLine);
        previousLineMainBrush.Color = palettePreviousColor;
        currentLineMainBrush.Color = paletteCurrentColor;
        nextLineMainBrush.Color = paletteNextColor;
        ApplyRestingLineLayerVisuals();
        UpdateDebugOverlayInfo();
    }

    private void StopActiveLineTransition()
    {
        if (activeLineTransitionStoryboard is null)
        {
            return;
        }

        activeLineTransitionStoryboard.Completed -= OnLineTransitionCompleted;
        activeLineTransitionStoryboard.Stop();
        activeLineTransitionStoryboard = null;
    }

    private void ApplyNextLineMargin()
    {
        var currentHeight = GetCurrentLineHeight();
        var nextHeight = GetNextLineHeight();
        var lineBandHeight = Math.Max(currentHeight, nextHeight);
        SetLineLayerHeights(currentHeight, nextHeight);
        canvasCurrentTop = 0;
        canvasNextTop = canvasCurrentTop + lineBandHeight + Math.Clamp(ViewModel.NextLineTopMargin, 0, 48);
        Canvas.SetTop(PreviousLineLayer, canvasCurrentTop - lineBandHeight - LineExitPadding);
        Canvas.SetTop(CurrentLineLayer, canvasCurrentTop);
        Canvas.SetTop(NextLineLayer, canvasNextTop);
        PreviousLineTranslate.Y = 0;
        CurrentLineTranslate.Y = 0;
        NextLineTranslate.Y = 0;
        LyricsCanvas.Height = canvasNextTop + lineBandHeight;
        UpdateDebugOverlayInfo();
    }

    private double GetCurrentLineHeight()
    {
        return ComputeLineHeight(ViewModel.CurrentLineFontSize);
    }

    private double GetNextLineHeight()
    {
        return ComputeLineHeight(ViewModel.NextLineFontSize);
    }

    private double GetLineBandHeight()
    {
        return Math.Max(GetCurrentLineHeight(), GetNextLineHeight());
    }

    private static double ComputeLineHeight(double fontSize)
    {
        return Math.Max(1, Math.Ceiling(fontSize * TextLineHeightMultiplier));
    }

    private void SetLineLayerHeights(double currentHeight, double nextHeight)
    {
        var lineBandHeight = Math.Max(currentHeight, nextHeight);
        var normalized = Math.Max(1, Math.Ceiling(lineBandHeight));
        var expandedWidth = Math.Max(1, Math.Ceiling(Math.Max(1, LyricsCanvas.ActualWidth) + PhysicalTrackLayerHorizontalOverflow));
        PreviousLineLayer.Height = normalized;
        CurrentLineLayer.Height = normalized;
        NextLineLayer.Height = normalized;
        PreviousLineLayer.Width = expandedWidth;
        CurrentLineLayer.Width = expandedWidth;
        NextLineLayer.Width = expandedWidth;
    }

    private void StopWaitingDotsAnimation()
    {
        if (Resources["WaitingDotsStoryboard"] is Storyboard dotsStoryboard)
        {
            dotsStoryboard.Stop();
            isWaitingDotsStoryboardRunning = false;
        }

        ResetWaitingDotsState();
    }

    private void ResetWaitingDotsState()
    {
        WaitDot1Translate.Y = 0;
        WaitDot2Translate.Y = 0;
        WaitDot3Translate.Y = 0;
        WaitDot1Scale.ScaleX = 1;
        WaitDot1Scale.ScaleY = 1;
        WaitDot2Scale.ScaleX = 1;
        WaitDot2Scale.ScaleY = 1;
        WaitDot3Scale.ScaleX = 1;
        WaitDot3Scale.ScaleY = 1;
        WaitDot1.Opacity = 0.34;
        WaitDot2.Opacity = 0.34;
        WaitDot3.Opacity = 0.34;
    }

    private void LyricVisualizerHost_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            visualizerLinesDirty = true;
            EnsureVisualizerLines();
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Visualizer host size-change failed: {ex}");
        }
    }

    private void UpdateVisualizerCaptureSource()
    {
        var sourceId = ViewModel.AppScopedAudioCapture
            ? ViewModel.AudioSourceAppId
            : string.Empty;
        sourceId ??= string.Empty;

        if (string.Equals(currentVisualizerSourceAppId, sourceId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            currentVisualizerSourceAppId = sourceId;
            audioSpectrumAnalyzer.SetPreferredSourceApp(sourceId);
        }
        catch (Exception ex)
        {
            currentVisualizerSourceAppId = string.Empty;
            DebugTrace.Write($"UpdateVisualizerCaptureSource failed: {ex}");
        }
    }

    private void UpdateVisualizerState()
    {
        var shouldShowVisualizer =
            ViewModel.VisualizerVisibility == Visibility.Visible ||
            ShouldRenderPhysicalLeadVisualizer();
        if (!shouldShowVisualizer)
        {
            StopVisualizerAnimation();
            return;
        }

        try
        {
            EnsureVisualizerLines();
            if (visualizerTimer is null)
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                if (queue is null)
                {
                    return;
                }

                visualizerTimer = queue.CreateTimer();
                visualizerTimer.Interval = TimeSpan.FromMilliseconds(33);
                visualizerTimer.IsRepeating = true;
                visualizerTimer.Tick += OnVisualizerTimerTick;
            }
            UpdateVisualizerCaptureSource();
            audioSpectrumAnalyzer.Start();
            visualizerTimer.Start();
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"UpdateVisualizerState failed: {ex}");
            StopVisualizerAnimation();
        }
    }

    private void StopVisualizerAnimation()
    {
        audioSpectrumAnalyzer.Stop();
        if (visualizerTimer is null)
        {
            return;
        }

        visualizerTimer.Stop();
    }

    private bool ShouldRenderPhysicalLeadVisualizer()
    {
        if (!physicalTrackModeActive || !activePhysicalTrackPlan.ContainsVisualizer || physicalLyricItems.Count == 0)
        {
            return false;
        }

        if (activePhysicalTrackStoryboard is not null)
        {
            return true;
        }

        var referenceIndex = physicalRenderedIndex >= 0
            ? physicalRenderedIndex
            : GetActivePhysicalPlanIndex();
        return referenceIndex <= 1;
    }

    private Canvas ResolveVisualizerCanvas()
    {
        if (physicalTrackModeActive && physicalTrackVisualizerCanvas is not null)
        {
            return physicalTrackVisualizerCanvas;
        }

        return LyricVisualizerCanvas;
    }

    private void EnsureVisualizerBuffers()
    {
        var targetPointCount = (int)Math.Round(ViewModel.OscilloscopeDetail);
        targetPointCount = Math.Clamp(targetPointCount, VisualizerPointCountMin, VisualizerPointCountMax);
        if ((targetPointCount & 1) == 1)
        {
            targetPointCount++;
            targetPointCount = Math.Clamp(targetPointCount, VisualizerPointCountMin, VisualizerPointCountMax);
        }

        if (targetPointCount == visualizerPointCount && visualizerLevels.Length == targetPointCount / 2)
        {
            return;
        }

        visualizerPointCount = targetPointCount;
        visualizerLevels = new float[visualizerPointCount / 2];
        visualizerPeakLevels = new float[visualizerPointCount / 2];
        visualizerLinesDirty = true;
    }

    private void OnVisualizerTimerTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            var shouldShowVisualizer =
                ViewModel.VisualizerVisibility == Visibility.Visible ||
                ShouldRenderPhysicalLeadVisualizer();
            if (!shouldShowVisualizer)
            {
                StopVisualizerAnimation();
                return;
            }

            EnsureVisualizerBuffers();
            EnsureVisualizerLines();
            if (visualizerBarVisuals.Count == 0)
            {
                return;
            }

            var canvas = activeVisualizerCanvas ?? LyricVisualizerCanvas;
            var width = canvas.Width;
            var height = canvas.Height;
            if (width < 16 || height < 16)
            {
                return;
            }

            var halfPointCount = visualizerLevels.Length;
            if (halfPointCount < 2)
            {
                return;
            }

            var lowFrequencyFocus = Math.Clamp(ViewModel.OscilloscopeLowFrequencyFocus, 0, 1);
            var spectrum = audioSpectrumAnalyzer.GetSpectrum(Math.Max(halfPointCount * 2, 48), lowFrequencyFocus);
            if (spectrum.Length < 8)
            {
                return;
            }

            var responsiveness = (float)Math.Clamp(ViewModel.OscilloscopeResponsiveness, 0.08, 0.95);
            if (!float.IsFinite(responsiveness))
            {
                responsiveness = VisualizerSmoothingFallback;
            }

            var sensitivity = (float)Math.Clamp(ViewModel.OscilloscopeSensitivity, 0.5, 2.6);
            var peakDecay = (float)Math.Clamp(ViewModel.OscilloscopePeakDecay, 0.65, 0.99);
            var colorIntensity = Math.Clamp(ViewModel.OscilloscopeColorIntensity, 0, 1);
            var neutralBase = Color.FromArgb(255, 214, 214, 214);
            var whiteFlash = Color.FromArgb(255, 246, 246, 246);
            var lowAccent = ViewModel.OscilloscopeLowAccentColor;
            var midAccent = ViewModel.OscilloscopeMidAccentColor;
            var highAccent = ViewModel.OscilloscopeHighAccentColor;
            var centerY = height * 0.5;
            var maxHalfHeight = Math.Max(1.2, Math.Min(VisualizerMaxAmplitude, centerY - 1.6));
            var minHalfHeight = Math.Min(maxHalfHeight, Math.Max(0.8, VisualizerMinAmplitude));

            for (var i = 0; i < visualizerBarVisuals.Count; i++)
            {
                var visual = visualizerBarVisuals[i];
                var sampled = SampleSpectrumForBar(spectrum, visual.FrequencyRatio);
                var level = Math.Clamp(sampled * sensitivity, 0f, 1f);
                var smoothed = visualizerLevels[i] + ((level - visualizerLevels[i]) * responsiveness);
                visualizerLevels[i] = smoothed;
                visualizerPeakLevels[i] = Math.Max(smoothed, visualizerPeakLevels[i] * peakDecay);

                var curved = Math.Clamp(MathF.Pow(smoothed, 0.84f), 0f, 1f);
                var halfHeight = minHalfHeight + (curved * (maxHalfHeight - minHalfHeight));
                var rectHeight = Math.Max(1.0, halfHeight * 2.0);
                visual.Bar.Height = rectHeight;
                Canvas.SetTop(visual.Bar, centerY - (rectHeight * 0.5));

                var accentColor = EvaluateBarAccentColor(lowAccent, midAccent, highAccent, visual.FrequencyRatio);
                var peakBoost = Math.Clamp((visualizerPeakLevels[i] - (smoothed * 0.58f)) * 2.1f, 0f, 1f);

                // Minimeters-like behavior: each frequency bar is always colorized.
                var baseColorStrength = Math.Clamp(0.68 + (colorIntensity * 0.30), 0, 1);
                var baseTone = LerpColor(neutralBase, accentColor, baseColorStrength);

                // Signal and peak add extra saturation/brightness, not the base color itself.
                var levelBoost = Math.Clamp(Math.Pow(smoothed, 0.86) * 0.42, 0, 1);
                var peakMix = Math.Clamp(Math.Pow(peakBoost, 0.72) * 0.72, 0, 1);
                var energizedTone = LerpColor(baseTone, accentColor, Math.Clamp(0.24 + levelBoost + (peakMix * 0.35), 0, 1));
                var flashMix = Math.Clamp(peakMix * 0.18, 0, 0.24);
                visual.Brush.Color = LerpColor(energizedTone, whiteFlash, flashMix);
            }
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Visualizer tick failed: {ex}");
            StopVisualizerAnimation();
        }
    }

    private void EnsureVisualizerLines()
    {
        try
        {
            EnsureVisualizerBuffers();
            var canvas = ResolveVisualizerCanvas();
            if (!ReferenceEquals(canvas, activeVisualizerCanvas))
            {
                activeVisualizerCanvas = canvas;
                visualizerBarVisuals.Clear();
                visualizerLinesDirty = true;
            }

            var width = ReferenceEquals(canvas, LyricVisualizerCanvas)
                ? LyricVisualizerHost.ActualWidth
                : Math.Max(LyricsTrackSurface.ActualWidth, LyricsCanvas.ActualWidth);
            if (width <= 20 || double.IsNaN(width) || double.IsInfinity(width))
            {
                return;
            }

            var targetHeight = ReferenceEquals(canvas, LyricVisualizerCanvas)
                ? VisualizerCanvasHeight
                : GetLineBandHeight();
            if (targetHeight <= 0 || double.IsNaN(targetHeight) || double.IsInfinity(targetHeight))
            {
                return;
            }

            if (double.IsNaN(canvas.Width) || Math.Abs(canvas.Width - width) > 0.5)
            {
                canvas.Width = width;
                visualizerLinesDirty = true;
            }

            if (Math.Abs(canvas.Height - targetHeight) > 0.5)
            {
                canvas.Height = targetHeight;
                visualizerLinesDirty = true;
            }

            if (!visualizerLinesDirty && visualizerBarVisuals.Count == visualizerLevels.Length)
            {
                return;
            }

            visualizerLinesDirty = false;
            canvas.Children.Clear();
            Array.Fill(visualizerLevels, 0f);
            Array.Fill(visualizerPeakLevels, 0f);
            visualizerBarVisuals.Clear();

            var barCount = Math.Max(2, visualizerLevels.Length);
            var gap = VisualizerBarGap;
            var drawableWidth = Math.Max(8, canvas.Width - 2);
            var barWidth = (drawableWidth - (Math.Max(0, barCount - 1) * gap)) / barCount;
            if (barWidth < VisualizerBarMinWidth)
            {
                barWidth = VisualizerBarMinWidth;
                gap = Math.Max(0.6, (drawableWidth - (barWidth * barCount)) / Math.Max(1, barCount - 1));
            }

            var usedWidth = (barWidth * barCount) + (Math.Max(0, barCount - 1) * gap);
            var startX = (canvas.Width - usedWidth) * 0.5;
            var centerY = canvas.Height * 0.5;
            var corner = Math.Clamp(barWidth * VisualizerBarCornerRatio, 1.2, 4.2);
            var lowAccent = ViewModel.OscilloscopeLowAccentColor;
            var midAccent = ViewModel.OscilloscopeMidAccentColor;
            var highAccent = ViewModel.OscilloscopeHighAccentColor;
            var mirrorBars = ViewModel.OscilloscopeMirrorBars;
            var baseTone = Color.FromArgb(255, 208, 208, 208);
            for (var i = 0; i < barCount; i++)
            {
                var ratio = ComputeBarFrequencyRatio(i, barCount, mirrorBars);
                var accent = EvaluateBarAccentColor(lowAccent, midAccent, highAccent, ratio);
                var brush = new SolidColorBrush(LerpColor(baseTone, accent, 0.80));
                var rect = new Rectangle
                {
                    Width = barWidth,
                    Height = 2,
                    RadiusX = corner,
                    RadiusY = corner,
                    Fill = brush,
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(rect, startX + (i * (barWidth + gap)));
                Canvas.SetTop(rect, centerY - 1);
                canvas.Children.Add(rect);

                visualizerBarVisuals.Add(new VisualizerBarVisual
                {
                    Bar = rect,
                    Brush = brush,
                    FrequencyRatio = ratio
                });
            }
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"EnsureVisualizerLines failed: {ex}");
            visualizerLinesDirty = true;
        }
    }

    private static double ComputeBarFrequencyRatio(int index, int barCount, bool mirrored)
    {
        if (barCount <= 1)
        {
            return 0;
        }

        var position = Math.Clamp(index / (double)(barCount - 1), 0, 1);
        if (!mirrored)
        {
            // Full sweep left->right: 0 lows -> 1 highs.
            return position;
        }

        // Mirrored sweep from center to edges:
        // center = highs, edges = lows (symmetrical L/R).
        var centerDistance = Math.Abs((position * 2) - 1); // 0 at center, 1 at edges
        return Math.Clamp(1 - centerDistance, 0, 1);
    }

    private static float SampleSpectrumForBar(float[] spectrum, double frequencyRatio)
    {
        if (spectrum.Length == 0)
        {
            return 0;
        }

        var mapped = Math.Clamp(frequencyRatio, 0, 1);
        var center = (int)Math.Round(mapped * (spectrum.Length - 1));
        var radius = 2 + (int)Math.Round((1 - mapped) * 2.2);
        var from = Math.Max(0, center - radius);
        var to = Math.Min(spectrum.Length - 1, center + radius);

        var weighted = 0f;
        var weightSum = 0f;
        for (var i = from; i <= to; i++)
        {
            var distance = Math.Abs(i - center);
            var weight = 1f / (1f + distance);
            weighted += spectrum[i] * weight;
            weightSum += weight;
        }

        return weightSum <= 0
            ? 0
            : weighted / weightSum;
    }

    private static Color EvaluateBarAccentColor(Color low, Color mid, Color high, double ratio)
    {
        var clamped = Math.Clamp(ratio, 0, 1);
        if (clamped <= 0.5)
        {
            return LerpColor(low, mid, clamped * 2);
        }

        return LerpColor(mid, high, (clamped - 0.5) * 2);
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

    private void UpdateWindowClickThroughMode()
    {
        if (hwnd == 0)
        {
            return;
        }

        appWindow ??= ResolveAppWindow();
        if (appWindow is null)
        {
            return;
        }

        if (isDraggingWindow || isDraggingSettingsFlyout)
        {
            SetWindowClickThrough(false);
            return;
        }

        if (isSettingsFlyoutOpen)
        {
            SetWindowClickThrough(false);
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var isOverHandle = ViewModel.ShowDragHandle &&
            GetDragHandleScreenBounds().Contains(cursor.X, cursor.Y);
        var isOverSettings = isSettingsFlyoutOpen &&
            GetSettingsFlyoutScreenBounds().Contains(cursor.X, cursor.Y);
        var shouldBeInteractive = ViewModel.ClickThroughMode switch
        {
            ClickThroughMode.Disabled => true,
            ClickThroughMode.Always => isOverSettings,
            _ => isOverHandle || isOverSettings
        };
        SetWindowClickThrough(!shouldBeInteractive);
    }

    private void SetWindowClickThrough(bool enabled)
    {
        if (isWindowClickThroughEnabled == enabled || hwnd == 0)
        {
            return;
        }

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        exStyle |= WsExLayered;
        if (enabled)
        {
            exStyle |= WsExTransparent;
        }
        else
        {
            exStyle &= ~WsExTransparent;
        }

        _ = SetWindowLongPtr(hwnd, GwlExStyle, exStyle);
        _ = SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
        _ = SetWindowPos(
            hwnd,
            0,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFrameChanged);
        isWindowClickThroughEnabled = enabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ScreenRect(int x, int y, int width, int height)
    {
        private readonly int left = x;
        private readonly int top = y;
        private readonly int right = x + width;
        private readonly int bottom = y + height;

        public bool Contains(int px, int py) =>
            px >= left && px < right &&
            py >= top && py < bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(
        nint hWnd,
        uint crKey,
        byte bAlpha,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}
