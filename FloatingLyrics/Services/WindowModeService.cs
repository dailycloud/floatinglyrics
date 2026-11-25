using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace FloatingLyrics.Services;

public sealed class WindowModeService : IWindowModeService
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const nint WsPopup = unchecked((int)0x80000000);
    private const nint WsChild = 0x40000000;
    private const nint WsVisible = 0x10000000;
    private const nint WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpShowwindow = 0x0040;
    private const uint ProgmanMessage = 0x052C;
    private const uint SmtoNormal = 0x0000;

    private static readonly nint HwndTopMost = -1;
    private static readonly nint HwndNotTopMost = -2;
    private static readonly nint HwndBottom = 1;
    private static readonly nint HwndTop = 0;

    private readonly nint hwnd;
    private bool isAlwaysOnTop = true;
    private nint attachedHost;

    public WindowModeService(Window window)
    {
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
    }

    public WindowPresentationMode CurrentMode { get; private set; } = WindowPresentationMode.Floating;
    public bool IsAlwaysOnTop => isAlwaysOnTop;

    public bool TrySetMode(WindowPresentationMode mode)
    {
        try
        {
            return mode switch
            {
                WindowPresentationMode.Floating => TrySetFloatingMode(),
                WindowPresentationMode.Wallpaper => TrySetWallpaperMode(),
                _ => false
            };
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"WindowModeService: TrySetMode({mode}) fatal: {ex}");
            try
            {
                _ = TrySetFloatingMode();
            }
            catch (Exception fallbackEx)
            {
                DebugTrace.Write($"WindowModeService: TrySetMode fallback floating failed: {fallbackEx}");
            }

            return false;
        }
    }

    public bool TrySetAlwaysOnTop(bool alwaysOnTop)
    {
        isAlwaysOnTop = alwaysOnTop;
        if (hwnd == 0)
        {
            return false;
        }

        if (CurrentMode != WindowPresentationMode.Floating)
        {
            return true;
        }

        return SetWindowPos(
            hwnd,
            alwaysOnTop ? HwndTopMost : HwndNotTopMost,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);
    }

    public bool TryApplyPositionPreset(WindowPositionPreset preset)
    {
        if (hwnd == 0 || preset == WindowPositionPreset.Manual)
        {
            return hwnd != 0;
        }

        if (CurrentMode == WindowPresentationMode.Wallpaper)
        {
            // In desktop-widget mode the overlay is a desktop child and must stay anchored.
            return true;
        }

        if (!TryGetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var windowWidth = Math.Max(1, rect.Right - rect.Left);
        var windowHeight = Math.Max(1, rect.Bottom - rect.Top);
        var workArea = GetWorkArea();
        var targetPoint = CalculatePresetPoint(preset, workArea, windowWidth, windowHeight);
        return ApplyAbsolutePosition(targetPoint.X, targetPoint.Y);
    }

    public bool TryApplyRelativePosition(double relativeX, double relativeY)
    {
        if (hwnd == 0)
        {
            return false;
        }

        if (CurrentMode == WindowPresentationMode.Wallpaper)
        {
            // Do not move child-hosted desktop widget through window coordinates.
            return true;
        }

        if (!TryGetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var windowWidth = Math.Max(1, rect.Right - rect.Left);
        var windowHeight = Math.Max(1, rect.Bottom - rect.Top);
        var workArea = GetWorkArea();
        var maxX = Math.Max(1, workArea.Width - windowWidth);
        var maxY = Math.Max(1, workArea.Height - windowHeight);
        var normalizedX = Math.Clamp(relativeX, 0.0, 1.0);
        var normalizedY = Math.Clamp(relativeY, 0.0, 1.0);
        var targetX = workArea.X + (int)Math.Round(maxX * normalizedX);
        var targetY = workArea.Y + (int)Math.Round(maxY * normalizedY);
        return ApplyAbsolutePosition(targetX, targetY);
    }

    public bool TryGetCurrentRelativePosition(out double relativeX, out double relativeY)
    {
        relativeX = 0;
        relativeY = 0;
        if (hwnd == 0)
        {
            return false;
        }

        if (CurrentMode == WindowPresentationMode.Wallpaper)
        {
            relativeX = 0.5;
            relativeY = 0.5;
            return true;
        }

        if (!TryGetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var windowWidth = Math.Max(1, rect.Right - rect.Left);
        var windowHeight = Math.Max(1, rect.Bottom - rect.Top);
        var workArea = GetWorkArea();
        var maxX = Math.Max(1, workArea.Width - windowWidth);
        var maxY = Math.Max(1, workArea.Height - windowHeight);
        var currentX = Math.Clamp(rect.Left - workArea.X, 0, maxX);
        var currentY = Math.Clamp(rect.Top - workArea.Y, 0, maxY);
        relativeX = currentX / (double)maxX;
        relativeY = currentY / (double)maxY;
        return true;
    }

    public bool TryResize(int width, int height)
    {
        if (hwnd == 0)
        {
            return false;
        }

        if (CurrentMode == WindowPresentationMode.Wallpaper && attachedHost != 0 && IsWindow(attachedHost))
        {
            // Desktop widget always covers the whole desktop host surface.
            ApplyHostBoundsAndZOrder(attachedHost);
            return true;
        }

        return SetWindowPos(
            hwnd,
            0,
            0,
            0,
            Math.Max(640, width),
            Math.Max(240, height),
            SwpNomove | SwpNozorder | SwpNoactivate | SwpShowwindow);
    }

    public bool TryGetWorkAreaSize(out int width, out int height)
    {
        if (hwnd == 0)
        {
            width = 0;
            height = 0;
            return false;
        }

        var workArea = GetWorkArea();
        width = Math.Max(1, workArea.Width);
        height = Math.Max(1, workArea.Height);
        return true;
    }

    public bool TryEnsureVisibleOnWorkArea()
    {
        if (CurrentMode == WindowPresentationMode.Wallpaper)
        {
            return true;
        }

        if (hwnd == 0 || !TryGetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var windowWidth = Math.Max(1, rect.Right - rect.Left);
        var windowHeight = Math.Max(1, rect.Bottom - rect.Top);
        var workArea = GetWorkArea();

        var maxX = workArea.X + Math.Max(0, workArea.Width - windowWidth);
        var maxY = workArea.Y + Math.Max(0, workArea.Height - windowHeight);
        var targetX = Math.Clamp(rect.Left, workArea.X, maxX);
        var targetY = Math.Clamp(rect.Top, workArea.Y, maxY);

        if (windowWidth > workArea.Width)
        {
            targetX = workArea.X;
        }

        if (windowHeight > workArea.Height)
        {
            targetY = workArea.Y;
        }

        if (targetX == rect.Left && targetY == rect.Top)
        {
            return true;
        }

        return ApplyAbsolutePosition(targetX, targetY);
    }

    private bool TrySetFloatingMode()
    {
        if (hwnd == 0)
        {
            return false;
        }

        DebugTrace.Write("WindowModeService: switching to Floating");

        _ = SetWindowPos(
            hwnd,
            HwndNotTopMost,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);

        _ = SetParent(hwnd, 0);
        var style = GetWindowLongPtr(hwnd, GwlStyle);
        style |= WsPopup | WsVisible;
        style &= ~WsChild;
        _ = SetWindowLongPtr(hwnd, GwlStyle, style);
        EnsureLayeredWindowStyle();
        _ = SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
        _ = SetWindowPos(
            hwnd,
            isAlwaysOnTop ? HwndTopMost : HwndNotTopMost,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);

        CurrentMode = WindowPresentationMode.Floating;
        DebugTrace.Write($"WindowModeService: floating applied; topMost={isAlwaysOnTop}");
        return true;
    }

    private bool TrySetWallpaperMode()
    {
        if (hwnd == 0)
        {
            return false;
        }

        if (CurrentMode == WindowPresentationMode.Wallpaper)
        {
            var existingParent = GetParent(hwnd);
            if (attachedHost != 0 && IsWindow(attachedHost) && existingParent == attachedHost)
            {
                ApplyHostBoundsAndZOrder(attachedHost);
                return true;
            }

            if (attachedHost == 0 && existingParent == 0 && (GetWindowLongPtr(hwnd, GwlStyle) & WsChild) == 0)
            {
                return true;
            }
        }

        DebugTrace.Write("WindowModeService: switching to Wallpaper");

        _ = SetWindowPos(
            hwnd,
            HwndNotTopMost,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);

        // Preferred host: the window that owns the desktop icon view (SHELLDLL_DefView).
        // On Windows 11 24H2+ this is Progman itself, on older shells a WorkerW. Hosting the
        // widget there, above the icon list, keeps it on the desktop layer (below every app
        // window) while still letting it receive mouse input for hover and the glass menu.
        // Ask Explorer to split the wallpaper into its own WorkerW behind the icon view (the
        // same message wallpaper engines use). Afterwards the icon layer is transparent, so the
        // widget stays visible while it sits below the icons in click-through mode.
        SplitWallpaperLayer();
        var shellViewHost = FindShellViewHost();
        if (shellViewHost != 0 && TryAttachToDesktopHost(shellViewHost))
        {
            EnsureLayeredWindowStyle();
            _ = SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
            ApplyHostBoundsAndZOrder(shellViewHost);
            attachedHost = shellViewHost;
            CurrentMode = WindowPresentationMode.Wallpaper;
            DebugTrace.Write($"WindowModeService: wallpaper applied (shell-view host) {DescribeWindow(shellViewHost)}");
            return true;
        }

        if (!TryGetWindowRect(hwnd, out var rect))
        {
            var workArea = GetWorkArea();
            rect = new NativeRect
            {
                Left = workArea.X,
                Top = workArea.Y,
                Right = workArea.X + Math.Max(1, workArea.Width),
                Bottom = workArea.Y + Math.Max(1, workArea.Height)
            };
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);

        var hostCandidates = CollectWallpaperHostCandidates();
        foreach (var host in hostCandidates)
        {
            if (!TryAttachToDesktopHost(host))
            {
                continue;
            }

            if (TryGetWindowRect(host, out var hostRect))
            {
                var hostWidth = Math.Max(1, hostRect.Right - hostRect.Left);
                var hostHeight = Math.Max(1, hostRect.Bottom - hostRect.Top);
                if (hostWidth > 1 && hostHeight > 1)
                {
                    width = hostWidth;
                    height = hostHeight;
                }
            }

            EnsureLayeredWindowStyle();
            _ = SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
            _ = SetWindowPos(
                hwnd,
                HwndTop,
                0,
                0,
                width,
                height,
                SwpNoactivate | SwpShowwindow);

            CurrentMode = WindowPresentationMode.Wallpaper;
            attachedHost = host;
            DebugTrace.Write(
                $"WindowModeService: wallpaper applied (desktop-host) host=0x{host.ToInt64():X}; size={width}x{height}");
            return true;
        }

        attachedHost = 0;
        DebugTrace.Write("WindowModeService: desktop-host attach unavailable, applying safe-bottom fallback");
        _ = SetParent(hwnd, 0);
        var wallpaperStyle = GetWindowLongPtr(hwnd, GwlStyle);
        wallpaperStyle |= WsPopup | WsVisible;
        wallpaperStyle &= ~WsChild;
        _ = SetWindowLongPtr(hwnd, GwlStyle, wallpaperStyle);
        EnsureLayeredWindowStyle();
        _ = SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
        _ = SetWindowPos(
            hwnd,
            HwndBottom,
            rect.Left,
            rect.Top,
            width,
            height,
            SwpNoactivate | SwpShowwindow);

        CurrentMode = WindowPresentationMode.Wallpaper;
        DebugTrace.Write(
            $"WindowModeService: wallpaper applied (safe-bottom fallback); pos={rect.Left},{rect.Top}; size={width}x{height}");
        return true;
    }

    public bool EnsureWallpaperAttachment()
    {
        if (hwnd == 0 || CurrentMode != WindowPresentationMode.Wallpaper)
        {
            return false;
        }

        var expectedHost = FindShellViewHost();
        var currentParent = GetParent(hwnd);
        var hostAlive = attachedHost != 0 && IsWindow(attachedHost);
        if (expectedHost != 0 && (!hostAlive || currentParent != expectedHost))
        {
            DebugTrace.Write("WindowModeService: desktop host changed (Explorer restart?) - re-attaching");
            return TrySetWallpaperMode();
        }

        if (attachedHost == 0 || currentParent != attachedHost)
        {
            return false;
        }

        // Z-order relative to the icon view is managed by the page (hover ⇄ click-through).
        var changed = false;

        if (TryGetClientSize(attachedHost, out var hostWidth, out var hostHeight) &&
            TryGetWindowRect(hwnd, out var ownRect) &&
            (ownRect.Right - ownRect.Left != hostWidth || ownRect.Bottom - ownRect.Top != hostHeight))
        {
            ApplyHostBoundsAndZOrder(attachedHost);
            changed = true;
        }

        return changed;
    }

    private void ApplyHostBoundsAndZOrder(nint host)
    {
        if (!TryGetClientSize(host, out var width, out var height))
        {
            if (!TryGetWindowRect(host, out var hostRect))
            {
                return;
            }

            width = Math.Max(1, hostRect.Right - hostRect.Left);
            height = Math.Max(1, hostRect.Bottom - hostRect.Top);
        }

        _ = SetWindowPos(hwnd, HwndTop, 0, 0, width, height, SwpNoactivate | SwpShowwindow);
    }

    private static bool TryGetClientSize(nint window, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (window == 0 || !GetClientRect(window, out var client))
        {
            return false;
        }

        width = Math.Max(1, client.Right - client.Left);
        height = Math.Max(1, client.Bottom - client.Top);
        return width > 1 && height > 1;
    }

    private static void SplitWallpaperLayer()
    {
        var progman = FindWindow("Progman", null);
        if (progman == 0)
        {
            return;
        }

        _ = SendMessageTimeout(progman, ProgmanMessage, 0, 0, SmtoNormal, 1000, out _);
        _ = SendMessageTimeout(progman, ProgmanMessage, 0xD, 0, SmtoNormal, 1000, out _);
        _ = SendMessageTimeout(progman, ProgmanMessage, 0xD, 1, SmtoNormal, 1000, out _);
    }

    /// <summary>Returns the window that directly hosts the desktop icon view (SHELLDLL_DefView).</summary>
    private static nint FindShellViewHost()
    {
        var progman = FindWindow("Progman", null);
        if (progman != 0 && FindWindowEx(progman, 0, "SHELLDLL_DefView", null) != 0)
        {
            return progman;
        }

        nint found = 0;
        _ = EnumWindows((topWindow, _) =>
        {
            if (FindWindowEx(topWindow, 0, "SHELLDLL_DefView", null) == 0)
            {
                return true;
            }

            found = topWindow;
            return false;
        }, 0);
        return found;
    }

    private bool TryAttachToDesktopHost(nint desktopHostWindow)
    {
        if (desktopHostWindow == 0 || !IsWindow(desktopHostWindow))
        {
            DebugTrace.Write("WindowModeService: TryAttachToDesktopHost skipped invalid host");
            return false;
        }

        var previousParent = SetParent(hwnd, desktopHostWindow);
        var setParentError = Marshal.GetLastWin32Error();
        var style = GetWindowLongPtr(hwnd, GwlStyle);
        style |= WsChild | WsVisible;
        style &= ~WsPopup;
        _ = SetWindowLongPtr(hwnd, GwlStyle, style);
        _ = SetWindowPos(
            hwnd,
            0,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpShowwindow);

        var actualParent = GetParent(hwnd);
        if (actualParent == desktopHostWindow)
        {
            DebugTrace.Write(
                $"WindowModeService: host attach success host=0x{desktopHostWindow.ToInt64():X}; prevParent=0x{previousParent.ToInt64():X}");
            return true;
        }

        DebugTrace.Write(
            $"WindowModeService: TryAttachToDesktopHost failed host=0x{desktopHostWindow.ToInt64():X}; actualParent=0x{actualParent.ToInt64():X}; prevParent=0x{previousParent.ToInt64():X}; setParentErr={setParentError}");
        _ = SetParent(hwnd, 0);
        var restoreStyle = GetWindowLongPtr(hwnd, GwlStyle);
        restoreStyle |= WsPopup | WsVisible;
        restoreStyle &= ~WsChild;
        _ = SetWindowLongPtr(hwnd, GwlStyle, restoreStyle);
        return false;
    }

    private void EnsureLayeredWindowStyle()
    {
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        if ((exStyle & WsExLayered) == 0)
        {
            _ = SetWindowLongPtr(hwnd, GwlExStyle, exStyle | WsExLayered);
        }
    }

    private static bool TryGetWindowRect(nint targetHwnd, out NativeRect rect)
    {
        if (GetWindowRect(targetHwnd, out rect))
        {
            return true;
        }

        rect = default;
        return false;
    }

    private static PointInt32 CalculatePresetPoint(
        WindowPositionPreset preset,
        RectInt32 workArea,
        int windowWidth,
        int windowHeight)
    {
        const int margin = 28;
        var availableWidth = Math.Max(0, workArea.Width - windowWidth);
        var availableHeight = Math.Max(0, workArea.Height - windowHeight);

        var left = workArea.X + margin;
        var centerX = workArea.X + (workArea.Width - windowWidth) / 2;
        var right = workArea.X + workArea.Width - windowWidth - margin;

        var top = workArea.Y + margin;
        var centerY = workArea.Y + (workArea.Height - windowHeight) / 2;
        var bottom = workArea.Y + workArea.Height - windowHeight - margin;

        var x = preset switch
        {
            WindowPositionPreset.TopLeft or WindowPositionPreset.MiddleLeft or WindowPositionPreset.BottomLeft => left,
            WindowPositionPreset.TopCenter or WindowPositionPreset.MiddleCenter or WindowPositionPreset.BottomCenter => centerX,
            WindowPositionPreset.TopRight or WindowPositionPreset.MiddleRight or WindowPositionPreset.BottomRight => right,
            _ => centerX
        };

        var y = preset switch
        {
            WindowPositionPreset.TopLeft or WindowPositionPreset.TopCenter or WindowPositionPreset.TopRight => top,
            WindowPositionPreset.MiddleLeft or WindowPositionPreset.MiddleCenter or WindowPositionPreset.MiddleRight => centerY,
            WindowPositionPreset.BottomLeft or WindowPositionPreset.BottomCenter or WindowPositionPreset.BottomRight => bottom,
            _ => centerY
        };

        x = Math.Clamp(x, workArea.X, workArea.X + availableWidth);
        y = Math.Clamp(y, workArea.Y, workArea.Y + availableHeight);
        return new PointInt32(x, y);
    }

    private RectInt32 GetWorkArea()
    {
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        return displayArea?.WorkArea ?? new RectInt32(0, 0, 1920, 1080);
    }

    private bool ApplyAbsolutePosition(int x, int y)
    {
        var targetX = x;
        var targetY = y;
        if (CurrentMode == WindowPresentationMode.Wallpaper)
        {
            var parent = GetParent(hwnd);
            if (parent != 0 && TryGetWindowRect(parent, out var parentRect))
            {
                targetX -= parentRect.Left;
                targetY -= parentRect.Top;
            }
        }

        return SetWindowPos(
            hwnd,
            0,
            targetX,
            targetY,
            0,
            0,
            SwpNosize | SwpNozorder | SwpNoactivate | SwpShowwindow);
    }

    private static IReadOnlyList<nint> CollectWallpaperHostCandidates()
    {
        var hosts = new List<nint>(8);
        static void AddUnique(List<nint> list, nint candidate)
        {
            if (candidate == 0 || !IsWindow(candidate) || list.Contains(candidate))
            {
                return;
            }

            list.Add(candidate);
        }

        AddUnique(hosts, FindDesktopHostWindow());
        AddUnique(hosts, FindWindow("Progman", null));

        nint workerAfter = 0;
        while (true)
        {
            workerAfter = FindWindowEx(0, workerAfter, "WorkerW", null);
            if (workerAfter == 0)
            {
                break;
            }

            AddUnique(hosts, workerAfter);
        }

        hosts.Sort((left, right) =>
        {
            var leftVisible = IsWindowVisible(left);
            var rightVisible = IsWindowVisible(right);
            if (leftVisible != rightVisible)
            {
                return leftVisible ? -1 : 1;
            }

            var leftProgman = IsProgmanWindow(left);
            var rightProgman = IsProgmanWindow(right);
            if (leftProgman != rightProgman)
            {
                return leftProgman ? 1 : -1;
            }

            return 0;
        });

        return hosts;
    }

    private static nint FindDesktopHostWindow()
    {
        var progman = FindWindow("Progman", null);
        if (progman == 0)
        {
            return 0;
        }

        _ = SendMessageTimeout(
            progman,
            ProgmanMessage,
            0,
            0,
            SmtoNormal,
            1000,
            out _);

        _ = SendMessageTimeout(
            progman,
            ProgmanMessage,
            0xD,
            0,
            SmtoNormal,
            1000,
            out _);

        _ = SendMessageTimeout(
            progman,
            ProgmanMessage,
            0xD,
            1,
            SmtoNormal,
            1000,
            out _);

        nint visibleWorkerWindow = 0;
        nint workerWindowFallback = 0;
        nint shellOwnerWindow = 0;
        _ = EnumWindows((topWindow, _) =>
        {
            var shellView = FindWindowEx(topWindow, 0, "SHELLDLL_DefView", null);
            if (shellView == 0)
            {
                return true;
            }

            if (shellOwnerWindow == 0)
            {
                shellOwnerWindow = topWindow;
            }

            // Prefer a visible WorkerW host first.
            var workerWindow = FindWindowEx(0, topWindow, "WorkerW", null);
            if (workerWindow != 0 && IsWindowVisible(workerWindow))
            {
                visibleWorkerWindow = workerWindow;
                return false;
            }

            if (workerWindow != 0 && workerWindowFallback == 0)
            {
                workerWindowFallback = workerWindow;
            }

            return true;
        }, 0);

        if (visibleWorkerWindow != 0)
        {
            return visibleWorkerWindow;
        }

        if (workerWindowFallback != 0)
        {
            return workerWindowFallback;
        }

        if (shellOwnerWindow != 0)
        {
            return shellOwnerWindow;
        }

        return progman;
    }

    private static bool IsProgmanWindow(nint window)
    {
        if (!TryGetClassName(window, out var className))
        {
            return false;
        }

        return string.Equals(className, "Progman", StringComparison.Ordinal);
    }

    private static string DescribeWindow(nint window)
    {
        _ = TryGetClassName(window, out var className);
        var visible = IsWindowVisible(window);
        return $"hwnd=0x{window.ToInt64():X}; class='{className}'; visible={visible}";
    }

    private static nint SetWindowLongPtr(nint hWnd, int index, nint value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, index, value)
            : SetWindowLong32(hWnd, index, unchecked((int)value));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(nint topWindow, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowEx(nint parentHandle, nint childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint child, nint newParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetParent(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(
        nint hWnd,
        uint crKey,
        byte bAlpha,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(nint hWnd, out NativeRect rect);


    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint hWnd, char[] className, int maxCount);

    private static bool TryGetClassName(nint hWnd, out string className)
    {
        var buffer = new char[256];
        var length = GetClassName(hWnd, buffer, buffer.Length);
        if (length <= 0)
        {
            className = string.Empty;
            return false;
        }

        className = new string(buffer, 0, length);
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint hWnd,
        uint msg,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nint result);

    private static nint GetWindowLongPtr(nint hWnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, index)
            : GetWindowLong32(hWnd, index);
    }
}
