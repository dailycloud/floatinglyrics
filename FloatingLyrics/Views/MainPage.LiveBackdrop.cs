using System.Runtime.InteropServices;
using FloatingLyrics.Services;
using Windows.Foundation;
using Windows.Graphics;

namespace FloatingLyrics.Views;

/// <summary>
/// Live backdrop for Wallpaper Engine: its DX11 window lives in the same desktop layer as the
/// widget and cannot be captured on its own, and its preview files do not match the user's
/// scene settings. So when its wallpaper changes, the widget captures the screen behind itself:
/// only while nothing covers that area and the glass is hidden, the widget steps below the
/// wallpaper window for a couple of frames, the area is copied, and the widget comes back.
/// </summary>
public sealed partial class MainPage
{
    private const int LiveCaptureMargin = 140;
    private DateTime lastLiveCaptureAttemptUtc;

    private string? lastLiveCaptureSignature;
    private byte[]? livePixels;
    private bool[]? liveValid;
    private RectInt32 liveRect;
    private string? liveKey;

    private void TryCaptureLiveBackdrop()
    {
        if (hwnd == 0 || isWidgetHovered || isPanelHovered || isSettingsPanelOpen)
        {
            return;
        }

        if (DateTime.UtcNow - lastLiveCaptureAttemptUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        var service = DesktopWallpaperService.Instance;
        var region = GetLiveCaptureRegion();
        if (region is not { } rect || !service.NeedsLiveCapture(rect) || service.CurrentEngineKey is not { } key)
        {
            return;
        }

        // Windows over the area are fine: those parts are taken from an earlier capture (or
        // filled in) and captured again once they are uncovered. Retry only when something
        // changed, so the widget does not blink every couple of seconds.
        var covers = FindObstructingWindows(rect);
        var signature = $"{key}|{rect.X},{rect.Y},{rect.Width},{rect.Height}|" +
            string.Join(";", covers.Select(c => $"{c.Left},{c.Top},{c.Right},{c.Bottom}"));
        if (string.Equals(signature, lastLiveCaptureSignature, StringComparison.Ordinal))
        {
            return;
        }

        lastLiveCaptureAttemptUtc = DateTime.UtcNow;
        lastLiveCaptureSignature = signature;

        var count = rect.Width * rect.Height;
        var valid = new bool[count];
        Array.Fill(valid, true);
        foreach (var c in covers)
        {
            var x0 = Math.Max(c.Left, rect.X) - rect.X;
            var x1 = Math.Min(c.Right, rect.X + rect.Width) - rect.X;
            var y0 = Math.Max(c.Top, rect.Y) - rect.Y;
            var y1 = Math.Min(c.Bottom, rect.Y + rect.Height) - rect.Y;
            for (var y = y0; y < y1; y++)
            {
                Array.Fill(valid, false, (y * rect.Width) + x0, Math.Max(0, x1 - x0));
            }
        }

        var reusable = livePixels is not null && liveValid is not null && liveKey == key && liveRect.Equals(rect);
        var anyNew = false;
        for (var i = 0; i < count && !anyNew; i++)
        {
            anyNew = valid[i] && !(reusable && liveValid![i]);
        }

        if (!anyNew)
        {
            if (!reusable)
            {
                DebugTrace.Write("Wallpaper: live capture postponed, area fully covered");
            }

            return;
        }

        var parent = GetParent(hwnd);
        var wallpaperWorker = parent == 0 ? 0 : FindLastChild(parent, "WorkerW");
        if (wallpaperWorker == 0)
        {
            return;
        }

        byte[]? pixels;
        try
        {
            // Step below the wallpaper window, wait for DWM to present two frames without us,
            // copy the area, come back.
            _ = SetWindowPos(hwnd, wallpaperWorker, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
            _ = DwmFlush();
            _ = DwmFlush();
            pixels = CopyScreen(rect);
        }
        finally
        {
            EnsureDesktopLayerOrder(interactive: !isWindowClickThroughEnabled);
        }

        if (pixels is null)
        {
            return;
        }

        // Merge with what an earlier capture saw where windows cover the area now.
        long r = 0, g = 0, b = 0, n = 0;
        for (var i = 0; i < count; i++)
        {
            if (!valid[i] && reusable && liveValid![i])
            {
                Buffer.BlockCopy(livePixels!, i * 4, pixels, i * 4, 4);
                valid[i] = true;
            }

            if (valid[i])
            {
                b += pixels[i * 4];
                g += pixels[(i * 4) + 1];
                r += pixels[(i * 4) + 2];
                n++;
            }
        }

        var complete = n == count;
        if (!complete && n > 0)
        {
            // Still unknown parts: a neutral fill of the average wallpaper colour.
            var ab = (byte)(b / n);
            var ag = (byte)(g / n);
            var ar = (byte)(r / n);
            for (var i = 0; i < count; i++)
            {
                if (!valid[i])
                {
                    pixels[i * 4] = ab;
                    pixels[(i * 4) + 1] = ag;
                    pixels[(i * 4) + 2] = ar;
                    pixels[(i * 4) + 3] = 255;
                }
            }
        }

        livePixels = (byte[])pixels.Clone();
        liveValid = valid;
        liveRect = rect;
        liveKey = key;
        DebugTrace.Write($"Wallpaper: live capture {(complete ? "complete" : $"partial {100 * n / count}%")}");
        service.SetLivePatch(pixels, rect, complete);
    }

    /// <summary>The widget's lyrics area on screen, with room around it for refraction.</summary>
    private RectInt32? GetLiveCaptureRegion()
    {
        if (OverlayRootHost is null || OverlayRootHost.ActualWidth < 1 || GetHostScreenRect() is not { } host)
        {
            return null;
        }

        var topLeft = OverlayRootHost.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var scale = Math.Max(0.25, rasterScale);
        var left = host.X + (int)(topLeft.X * scale) - LiveCaptureMargin;
        var top = host.Y + (int)(topLeft.Y * scale) - LiveCaptureMargin;
        var right = host.X + (int)((topLeft.X + OverlayRootHost.ActualWidth) * scale) + LiveCaptureMargin;
        var bottom = host.Y + (int)((topLeft.Y + OverlayRootHost.ActualHeight) * scale) + LiveCaptureMargin;

        left = Math.Max(left, host.X);
        top = Math.Max(top, host.Y);
        right = Math.Min(right, host.X + host.Width);
        bottom = Math.Min(bottom, host.Y + host.Height);
        return right - left < 8 || bottom - top < 8 ? null : new RectInt32(left, top, right - left, bottom - top);
    }

    /// <summary>Screen rectangles of visible app windows overlapping the region.</summary>
    private static List<CaptureRect> FindObstructingWindows(RectInt32 region)
    {
        var result = new List<CaptureRect>();
        var ownPid = (uint)Environment.ProcessId;
        _ = EnumWindows((window, lParam) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window))
            {
                return true;
            }

            if (DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            _ = GetWindowThreadProcessId(window, out var pid);
            var className = GetClassNameOf(window);
            if (pid == ownPid || className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                return true;
            }

            // Layered click-through or non-activating overlays (GeForce/game bars, recorders,
            // notification layers) cover the screen invisibly and do not hide the wallpaper.
            var exStyle = (long)GetWindowLongPtrForCapture(window, -20);
            if ((exStyle & 0x80000) != 0 && (exStyle & (0x20 | 0x8000000)) != 0)
            {
                return true;
            }

            if (DwmGetWindowAttributeRect(window, DwmwaExtendedFrameBounds, out var r, Marshal.SizeOf<CaptureRect>()) != 0)
            {
                return true;
            }

            var intersects = r.Left < region.X + region.Width && r.Right > region.X &&
                             r.Top < region.Y + region.Height && r.Bottom > region.Y &&
                             r.Right - r.Left > 1 && r.Bottom - r.Top > 1;
            if (intersects)
            {
                result.Add(r);
            }

            return true;
        }, 0);
        return result;
    }

    private static byte[]? CopyScreen(RectInt32 rect)
    {
        var screen = GetDC(0);
        if (screen == 0)
        {
            return null;
        }

        var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo
        {
            Size = Marshal.SizeOf<BitmapInfo>(),
            Width = rect.Width,
            Height = -rect.Height, // top-down
            Planes = 1,
            BitCount = 32
        };
        var dib = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
        try
        {
            if (dib == 0 || bits == 0)
            {
                return null;
            }

            var old = SelectObject(memory, dib);
            var ok = BitBlt(memory, 0, 0, rect.Width, rect.Height, screen, rect.X, rect.Y, 0x00CC0020 /* SRCCOPY */);
            _ = SelectObject(memory, old);
            if (!ok)
            {
                return null;
            }

            var pixels = new byte[rect.Width * rect.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255; // GDI leaves alpha undefined
            }

            return pixels;
        }
        finally
        {
            if (dib != 0)
            {
                _ = DeleteObject(dib);
            }

            _ = DeleteDC(memory);
            _ = ReleaseDC(0, screen);
        }
    }

    private static nint FindLastChild(nint parent, string className)
    {
        nint last = 0;
        nint child = 0;
        while ((child = FindWindowEx(parent, child, className, null)) != 0)
        {
            last = child;
        }

        return last;
    }

    private static string GetClassNameOf(nint window)
    {
        var buffer = new char[256];
        var length = GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct CaptureRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
        public int Colors;
    }

    private delegate bool CaptureEnumProc(nint window, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(CaptureEnumProc callback, nint data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, char[] buffer, int length);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrForCapture(nint window, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(nint window, int attribute, out CaptureRect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);
}
