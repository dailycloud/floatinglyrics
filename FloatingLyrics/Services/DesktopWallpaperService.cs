using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace FloatingLyrics.Services;

/// <summary>
/// Rebuilds a pixel-accurate copy of the desktop wallpaper (all monitors, honouring the
/// fill/fit/stretch/tile/center/span position mode and the background colour) as a GPU bitmap.
/// The liquid-glass surfaces sample this bitmap, so they can blur and refract "what is behind"
/// even though the widget itself is a transparent child window of the desktop.
/// </summary>
public sealed class DesktopWallpaperService
{
    private static readonly Guid DesktopWallpaperClsid = new("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");
    private static readonly Guid DesktopWallpaperIid = new("B92B56A9-8B55-4E14-9A89-0199BBB6F93B");
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, (DateTime WriteTime, CanvasBitmap Bitmap)> imageCache = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherQueueTimer? pollTimer;
    private Func<RectInt32?>? hostRectProvider;
    private string lastSignature = string.Empty;
    private bool isRebuilding;
    private bool isQuerying;
    private bool rebuildQueued;

    public static DesktopWallpaperService Instance { get; } = new();

    /// <summary>Composite wallpaper at 96 DPI: one bitmap unit equals one physical pixel of the host.</summary>
    public CanvasBitmap? Bitmap { get; private set; }

    /// <summary>Screen position (physical px) of the composite bitmap's top-left corner.</summary>
    public PointInt32 Origin { get; private set; }

    /// <summary>Average colour of the wallpaper; used as a fallback tint and for UI accents.</summary>
    public Color AverageColor { get; private set; } = Color.FromArgb(255, 40, 48, 64);

    public event EventHandler? Changed;

    /// <summary>A real screen capture of the wallpaper behind the widget (Wallpaper Engine).</summary>
    private sealed record LivePatch(CanvasBitmap Bitmap, RectInt32 ScreenRect, Color Average, string EngineKey, bool Complete);

    private LivePatch? livePatch;
    private string? currentEngineKey;

    /// <summary>Identifies the Wallpaper Engine wallpaper + settings shown now (null: not running).</summary>
    public string? CurrentEngineKey => currentEngineKey;

    /// <summary>
    /// True while Wallpaper Engine is showing a wallpaper and no up-to-date capture covers
    /// <paramref name="region"/>: its rendering cannot be read from files (scene properties,
    /// user colours, animation), so the widget has to snapshot the screen behind itself.
    /// </summary>
    public bool NeedsLiveCapture(RectInt32 region) =>
        currentEngineKey is not null &&
        (livePatch is null || !livePatch.Complete ||
         !string.Equals(livePatch.EngineKey, currentEngineKey, StringComparison.Ordinal) ||
         !Contains(livePatch.ScreenRect, region));

    /// <summary>Stores a BGRA screen capture of <paramref name="screenRect"/> and rebuilds.</summary>
    public void SetLivePatch(byte[] bgra, RectInt32 screenRect, bool complete)
    {
        if (currentEngineKey is null)
        {
            return;
        }

        try
        {
            var device = CanvasDevice.GetSharedDevice();
            var bitmap = CanvasBitmap.CreateFromBytes(device, bgra, screenRect.Width, screenRect.Height, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
            var average = ComputeAverageColor(bitmap, Color.FromArgb(255, 0, 0, 0));
            livePatch?.Bitmap.Dispose();
            livePatch = new LivePatch(bitmap, screenRect, average, currentEngineKey, complete);
            DebugTrace.Write($"Wallpaper: live capture {screenRect.Width}x{screenRect.Height} at {screenRect.X},{screenRect.Y}");
            RequestRefresh(force: true);
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Wallpaper: live capture rejected {ex.Message}");
        }
    }

    private static bool Contains(RectInt32 outer, RectInt32 inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y &&
        inner.X + inner.Width <= outer.X + outer.Width &&
        inner.Y + inner.Height <= outer.Y + outer.Height;

    public void Start(DispatcherQueue dispatcherQueue, Func<RectInt32?> getHostScreenRect)
    {
        hostRectProvider = getHostScreenRect;
        if (pollTimer is null)
        {
            WallpaperEngineSource.FrameReady += () => dispatcherQueue.TryEnqueue(() => RequestRefresh(force: true));
            pollTimer = dispatcherQueue.CreateTimer();
            pollTimer.Interval = PollInterval;
            pollTimer.IsRepeating = true;
            pollTimer.Tick += (_, _) => RequestRefresh(force: false);
            pollTimer.Start();
        }

        RequestRefresh(force: true);
    }

    public void Stop()
    {
        pollTimer?.Stop();
        pollTimer = null;
    }

    public void RequestRefresh(bool force)
    {
        if (hostRectProvider?.Invoke() is not { Width: > 0, Height: > 0 } host)
        {
            return;
        }

        if (isQuerying || isRebuilding)
        {
            rebuildQueued |= force;
            return;
        }

        _ = RefreshAsync(host, force);
    }

    private async Task RefreshAsync(RectInt32 host, bool force)
    {
        isQuerying = true;
        WallpaperLayout layout;
        string signature;
        try
        {
            // IDesktopWallpaper is a cross-process shell call that pumps messages while it waits;
            // doing it on the XAML thread causes re-entrancy fail-fasts, so query on the pool (MTA).
            (layout, signature) = await Task.Run(() =>
            {
                var queried = QueryLayout(host);
                return (queried, queried.BuildSignature());
            });
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Wallpaper: layout query failed {ex.Message}");
            return;
        }
        finally
        {
            isQuerying = false;
        }

        if (!force && string.Equals(signature, lastSignature, StringComparison.Ordinal) && Bitmap is not null)
        {
            return;
        }

        lastSignature = signature;
        currentEngineKey = layout.EngineKey;
        await RebuildAsync(layout);
    }

    /// <summary>
    /// Luminance statistics of the wallpaper inside a screen rectangle, from a 12x4 probe grid:
    /// mean, and the fractions of clearly light (above 0.62) and clearly dark (below 0.38) cells.
    /// </summary>
    public (float Mean, float LightFraction, float DarkFraction)? SampleLuminanceStats(Rect screenRect)
    {
        var bitmap = Bitmap;
        if (bitmap is null || screenRect.Width < 1 || screenRect.Height < 1)
        {
            return null;
        }

        try
        {
            var size = bitmap.SizeInPixels;
            var x = Math.Clamp(screenRect.X - Origin.X, 0, size.Width - 1);
            var y = Math.Clamp(screenRect.Y - Origin.Y, 0, size.Height - 1);
            var w = Math.Clamp(screenRect.Width, 1, size.Width - x);
            var h = Math.Clamp(screenRect.Height, 1, size.Height - y);
            using var probe = new CanvasRenderTarget(bitmap.Device, 12, 4, 96);
            using (var ds = probe.CreateDrawingSession())
            {
                ds.DrawImage(bitmap, new Rect(0, 0, 12, 4), new Rect(x, y, w, h), 1f, CanvasImageInterpolation.HighQualityCubic);
            }

            var colors = probe.GetPixelColors();
            double total = 0;
            var light = 0;
            var dark = 0;
            foreach (var c in colors)
            {
                var l = ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255.0;
                total += l;
                if (l > 0.62)
                {
                    light++;
                }
                else if (l < 0.38)
                {
                    dark++;
                }
            }

            var n = Math.Max(1, colors.Length);
            return ((float)(total / n), light / (float)n, dark / (float)n);
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Wallpaper: luminance probe failed {ex.Message}");
            return null;
        }
    }

    /// <summary>Average perceived luminance (0..1) of the wallpaper inside a screen rectangle.</summary>
    public float? SampleLuminance(Rect screenRect)
    {
        var bitmap = Bitmap;
        if (bitmap is null || screenRect.Width < 1 || screenRect.Height < 1)
        {
            return null;
        }

        try
        {
            var size = bitmap.SizeInPixels;
            var x = Math.Clamp(screenRect.X - Origin.X, 0, size.Width - 1);
            var y = Math.Clamp(screenRect.Y - Origin.Y, 0, size.Height - 1);
            var w = Math.Clamp(screenRect.Width, 1, size.Width - x);
            var h = Math.Clamp(screenRect.Height, 1, size.Height - y);
            using var probe = new CanvasRenderTarget(bitmap.Device, 6, 3, 96);
            using (var ds = probe.CreateDrawingSession())
            {
                ds.DrawImage(bitmap, new Rect(0, 0, 6, 3), new Rect(x, y, w, h), 1f, CanvasImageInterpolation.HighQualityCubic);
            }

            var colors = probe.GetPixelColors();
            double total = 0;
            foreach (var c in colors)
            {
                total += ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255.0;
            }

            return (float)(total / Math.Max(1, colors.Length));
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Wallpaper: luminance probe failed {ex.Message}");
            return null;
        }
    }

    /// <summary>Called when the Win2D device was lost; drops GPU resources and rebuilds.</summary>
    public void OnDeviceLost()
    {
        foreach (var entry in imageCache.Values)
        {
            entry.Bitmap.Dispose();
        }

        imageCache.Clear();
        Bitmap = null;
        RequestRefresh(force: true);
    }

    private async Task RebuildAsync(WallpaperLayout layout)
    {
        isRebuilding = true;
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            var maxSize = (int)device.MaximumBitmapSizeInPixels;
            var width = Math.Clamp(layout.Host.Width, 1, maxSize);
            var height = Math.Clamp(layout.Host.Height, 1, maxSize);

            var images = new Dictionary<string, CanvasBitmap>(StringComparer.OrdinalIgnoreCase);
            foreach (var monitor in layout.Monitors)
            {
                if (string.IsNullOrWhiteSpace(monitor.ImagePath) || images.ContainsKey(monitor.ImagePath))
                {
                    continue;
                }

                var image = await LoadImageAsync(device, monitor.ImagePath);
                if (image is not null)
                {
                    images[monitor.ImagePath] = image;
                }
            }

            var target = new CanvasRenderTarget(device, width, height, 96);
            var patch = livePatch;
            var usePatch = layout.EngineKey is not null && patch is not null &&
                string.Equals(patch.EngineKey, layout.EngineKey, StringComparison.Ordinal);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(layout.BackgroundColor);
                if (usePatch)
                {
                    // Wallpaper Engine: the captured pixels where we have them, their average
                    // colour elsewhere (its preview images do not match the user's settings).
                    ds.Clear(patch!.Average);
                    ds.DrawImage(patch.Bitmap, (float)(patch.ScreenRect.X - layout.Host.X), (float)(patch.ScreenRect.Y - layout.Host.Y));
                }
                else if (layout.Position == WallpaperPosition.Span)
                {
                    var spanImage = layout.Monitors
                        .Select(m => m.ImagePath)
                        .Where(p => !string.IsNullOrWhiteSpace(p) && images.ContainsKey(p))
                        .Select(p => images[p])
                        .FirstOrDefault();
                    if (spanImage is not null)
                    {
                        var union = UnionOf(layout.Monitors.Select(m => m.Bounds));
                        DrawImageInRect(ds, spanImage, Offset(union, layout.Host), WallpaperPosition.Fill);
                    }
                }
                else
                {
                    foreach (var monitor in layout.Monitors)
                    {
                        if (string.IsNullOrWhiteSpace(monitor.ImagePath) ||
                            !images.TryGetValue(monitor.ImagePath, out var image))
                        {
                            continue;
                        }

                        DrawImageInRect(ds, image, Offset(monitor.Bounds, layout.Host), layout.Position);
                    }
                }
            }

            var previous = Bitmap;
            Bitmap = target;
            Origin = new PointInt32(layout.Host.X, layout.Host.Y);
            AverageColor = ComputeAverageColor(target, layout.BackgroundColor);
            if (previous is not null && !imageCache.Values.Any(v => ReferenceEquals(v.Bitmap, previous)))
            {
                previous.Dispose();
            }

            DebugTrace.Write($"Wallpaper: composite rebuilt {width}x{height}; monitors={layout.Monitors.Count}; mode={layout.Position}");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Wallpaper: rebuild failed {ex.Message}");
        }
        finally
        {
            isRebuilding = false;
            if (rebuildQueued)
            {
                rebuildQueued = false;
                RequestRefresh(force: true);
            }
        }
    }

    private async Task<CanvasBitmap?> LoadImageAsync(CanvasDevice device, string path)
    {
        try
        {
            var writeTime = File.GetLastWriteTimeUtc(path);
            if (imageCache.TryGetValue(path, out var cached))
            {
                if (cached.WriteTime == writeTime)
                {
                    return cached.Bitmap;
                }

                cached.Bitmap.Dispose();
                imageCache.Remove(path);
            }

            // Read through a memory stream so the wallpaper file is never locked.
            var bytes = await File.ReadAllBytesAsync(path);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = await CanvasBitmap.LoadAsync(device, stream, 96);
            imageCache[path] = (writeTime, bitmap);

            // Keep the cache small (slideshows rotate through many files).
            while (imageCache.Count > 4)
            {
                var victim = imageCache.First();
                victim.Value.Bitmap.Dispose();
                imageCache.Remove(victim.Key);
            }

            return bitmap;
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Wallpaper: failed to load '{path}': {ex.Message}");
            return null;
        }
    }

    private static void DrawImageInRect(CanvasDrawingSession ds, CanvasBitmap image, Rect dest, WallpaperPosition position)
    {
        var size = image.SizeInPixels;
        double iw = Math.Max(1, size.Width);
        double ih = Math.Max(1, size.Height);
        using var layer = ds.CreateLayer(1f, dest);

        switch (position)
        {
            case WallpaperPosition.Stretch:
                ds.DrawImage(image, dest, new Rect(0, 0, iw, ih));
                break;
            case WallpaperPosition.Center:
                ds.DrawImage(
                    image,
                    new Rect(dest.X + (dest.Width - iw) / 2, dest.Y + (dest.Height - ih) / 2, iw, ih),
                    new Rect(0, 0, iw, ih));
                break;
            case WallpaperPosition.Tile:
                for (var y = dest.Y; y < dest.Bottom; y += ih)
                {
                    for (var x = dest.X; x < dest.Right; x += iw)
                    {
                        ds.DrawImage(image, new Rect(x, y, iw, ih), new Rect(0, 0, iw, ih));
                    }
                }

                break;
            case WallpaperPosition.Fit:
            {
                var scale = Math.Min(dest.Width / iw, dest.Height / ih);
                var w = iw * scale;
                var h = ih * scale;
                ds.DrawImage(image, new Rect(dest.X + (dest.Width - w) / 2, dest.Y + (dest.Height - h) / 2, w, h), new Rect(0, 0, iw, ih));
                break;
            }
            default:
            {
                var scale = Math.Max(dest.Width / iw, dest.Height / ih);
                var w = iw * scale;
                var h = ih * scale;
                ds.DrawImage(image, new Rect(dest.X + (dest.Width - w) / 2, dest.Y + (dest.Height - h) / 2, w, h), new Rect(0, 0, iw, ih));
                break;
            }
        }
    }

    private static Color ComputeAverageColor(CanvasBitmap target, Color fallback)
    {
        try
        {
            var device = target.Device;
            using var tiny = new CanvasRenderTarget(device, 8, 8, 96);
            using (var ds = tiny.CreateDrawingSession())
            {
                ds.DrawImage(target, new Rect(0, 0, 8, 8), target.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic);
            }

            var colors = tiny.GetPixelColors();
            long r = 0, g = 0, b = 0;
            foreach (var c in colors)
            {
                r += c.R;
                g += c.G;
                b += c.B;
            }

            var n = Math.Max(1, colors.Length);
            return Color.FromArgb(255, (byte)(r / n), (byte)(g / n), (byte)(b / n));
        }
        catch
        {
            return fallback;
        }
    }

    private static Rect Offset(RectInt32 r, RectInt32 host) =>
        new(r.X - host.X, r.Y - host.Y, r.Width, r.Height);

    private static RectInt32 UnionOf(IEnumerable<RectInt32> rects)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var r in rects)
        {
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.X + r.Width);
            bottom = Math.Max(bottom, r.Y + r.Height);
        }

        return left == int.MaxValue
            ? default
            : new RectInt32(left, top, right - left, bottom - top);
    }

    // ------------------------------------------------------------------ layout query

    private enum WallpaperPosition
    {
        Center = 0,
        Tile = 1,
        Stretch = 2,
        Fit = 3,
        Fill = 4,
        Span = 5
    }

    private sealed record MonitorWallpaper(RectInt32 Bounds, string ImagePath);

    private sealed record WallpaperLayout(
        RectInt32 Host,
        IReadOnlyList<MonitorWallpaper> Monitors,
        WallpaperPosition Position,
        Color BackgroundColor,
        string? EngineKey = null)
    {
        public string BuildSignature()
        {
            var sb = new StringBuilder();
            sb.Append(Host.X).Append(',').Append(Host.Y).Append(',').Append(Host.Width).Append(',').Append(Host.Height);
            sb.Append('|').Append((int)Position).Append('|').Append(BackgroundColor.ToString()).Append('|').Append(EngineKey);
            foreach (var m in Monitors)
            {
                sb.Append('|').Append(m.Bounds.X).Append(',').Append(m.Bounds.Y).Append(',')
                    .Append(m.Bounds.Width).Append(',').Append(m.Bounds.Height).Append(':').Append(m.ImagePath);
                try
                {
                    if (!string.IsNullOrWhiteSpace(m.ImagePath))
                    {
                        sb.Append('@').Append(File.GetLastWriteTimeUtc(m.ImagePath).Ticks);
                    }
                }
                catch
                {
                    // Ignore races with the shell rewriting the file.
                }
            }

            return sb.ToString();
        }
    }

    private static WallpaperLayout QueryLayout(RectInt32 host)
    {
        var layout = TryQueryViaDesktopWallpaper(host, out var queried) ? queried : QueryViaFallback(host);
        return ApplyWallpaperEngine(layout);
    }

    /// <summary>
    /// Wallpaper Engine covers the Windows wallpaper with its own; while it runs, use an image of
    /// what it shows (fill mode, like Wallpaper Engine's default) instead of the hidden picture.
    /// </summary>
    private static string? lastEngineSummary;

    private static WallpaperLayout ApplyWallpaperEngine(WallpaperLayout layout)
    {
        try
        {
            if (!WallpaperEngineSource.IsRunning(out var installDir))
            {
                return layout;
            }

            var images = WallpaperEngineSource.ResolveMonitorImages(installDir);
            if (images.Count == 0)
            {
                return layout;
            }

            var monitors = layout.Monitors
                .Select((m, i) => m with { ImagePath = images[Math.Min(i, images.Count - 1)] })
                .ToList();
            var summary = string.Join(" | ", images);
            if (!string.Equals(summary, lastEngineSummary, StringComparison.Ordinal))
            {
                lastEngineSummary = summary;
                DebugTrace.Write($"Wallpaper: Wallpaper Engine image(s) {summary}");
            }
            return layout with
            {
                Monitors = monitors,
                Position = WallpaperPosition.Fill,
                BackgroundColor = Color.FromArgb(255, 0, 0, 0),
                EngineKey = summary + "#" + WallpaperEngineSource.GetConfigStamp(installDir)
            };
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Wallpaper: Wallpaper Engine lookup failed {ex.Message}");
            return layout;
        }
    }

    private static unsafe bool TryQueryViaDesktopWallpaper(RectInt32 host, out WallpaperLayout layout)
    {
        layout = null!;
        nint instance = 0;
        try
        {
            var clsid = DesktopWallpaperClsid;
            var iid = DesktopWallpaperIid;
            var hr = CoCreateInstance(ref clsid, 0, ClsctxLocalServer | ClsctxInprocServer, ref iid, out instance);
            if (hr < 0 || instance == 0)
            {
                return false;
            }

            var vtable = *(nint**)instance;
            // IDesktopWallpaper vtable (after IUnknown): 3 SetWallpaper, 4 GetWallpaper,
            // 5 GetMonitorDevicePathAt, 6 GetMonitorDevicePathCount, 7 GetMonitorRECT,
            // 8 SetBackgroundColor, 9 GetBackgroundColor, 10 SetPosition, 11 GetPosition.
            var getWallpaper = (delegate* unmanaged[Stdcall]<nint, char*, char**, int>)vtable[4];
            var getMonitorPathAt = (delegate* unmanaged[Stdcall]<nint, uint, char**, int>)vtable[5];
            var getMonitorCount = (delegate* unmanaged[Stdcall]<nint, uint*, int>)vtable[6];
            var getMonitorRect = (delegate* unmanaged[Stdcall]<nint, char*, NativeRect*, int>)vtable[7];
            var getBackground = (delegate* unmanaged[Stdcall]<nint, uint*, int>)vtable[9];
            var getPosition = (delegate* unmanaged[Stdcall]<nint, int*, int>)vtable[11];

            uint count = 0;
            if (getMonitorCount(instance, &count) < 0)
            {
                return false;
            }

            var monitors = new List<MonitorWallpaper>();
            for (uint i = 0; i < count; i++)
            {
                char* monitorId = null;
                if (getMonitorPathAt(instance, i, &monitorId) < 0 || monitorId == null)
                {
                    continue;
                }

                try
                {
                    NativeRect rect;
                    if (getMonitorRect(instance, monitorId, &rect) < 0)
                    {
                        // Detached monitors report an error here; skip them.
                        continue;
                    }

                    char* wallpaperPath = null;
                    var path = string.Empty;
                    if (getWallpaper(instance, monitorId, &wallpaperPath) >= 0 && wallpaperPath != null)
                    {
                        path = new string(wallpaperPath);
                        Marshal.FreeCoTaskMem((nint)wallpaperPath);
                    }

                    monitors.Add(new MonitorWallpaper(
                        new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
                        ResolveImagePath(path, (int)i)));
                }
                finally
                {
                    Marshal.FreeCoTaskMem((nint)monitorId);
                }
            }

            uint background = 0;
            _ = getBackground(instance, &background);
            int position = (int)WallpaperPosition.Fill;
            _ = getPosition(instance, &position);

            if (monitors.Count == 0)
            {
                return false;
            }

            layout = new WallpaperLayout(
                host,
                monitors,
                Enum.IsDefined(typeof(WallpaperPosition), position) ? (WallpaperPosition)position : WallpaperPosition.Fill,
                FromColorRef(background));
            return true;
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Wallpaper: IDesktopWallpaper query failed {ex.Message}");
            return false;
        }
        finally
        {
            if (instance != 0)
            {
                Marshal.Release(instance);
            }
        }
    }

    private static WallpaperLayout QueryViaFallback(RectInt32 host)
    {
        var path = ResolveImagePath(GetSystemWallpaperPath(), 0);
        var monitors = new List<MonitorWallpaper>();
        foreach (var bounds in EnumerateMonitors())
        {
            monitors.Add(new MonitorWallpaper(bounds, path));
        }

        if (monitors.Count == 0)
        {
            monitors.Add(new MonitorWallpaper(host, path));
        }

        return new WallpaperLayout(host, monitors, WallpaperPosition.Fill, Color.FromArgb(255, 0, 0, 0));
    }

    /// <summary>
    /// The shell keeps a transcoded copy of what is actually on screen (also for Spotlight and
    /// slideshows). Prefer the original file when it exists, otherwise use the transcoded one.
    /// </summary>
    private static string ResolveImagePath(string path, int monitorIndex)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return path;
        }

        // No picture at all (solid colour, or cleared by a live-wallpaper app): the transcoded
        // file is then a stale copy of some earlier wallpaper and must not be shown.
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(GetRegistryWallpaper()))
        {
            return string.Empty;
        }

        var themes = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes");
        var perMonitor = Path.Combine(themes, $"Transcoded_{monitorIndex:000}");
        if (File.Exists(perMonitor))
        {
            return perMonitor;
        }

        var transcoded = Path.Combine(themes, "TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : string.Empty;
    }

    private static string GetRegistryWallpaper()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("WallPaper") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetSystemWallpaperPath()
    {
        var buffer = new char[520];
        return SystemParametersInfo(SpiGetDeskWallpaper, (uint)buffer.Length, buffer, 0)
            ? new string(buffer).TrimEnd('\0')
            : string.Empty;
    }

    private static List<RectInt32> EnumerateMonitors()
    {
        var result = new List<RectInt32>();
        _ = EnumDisplayMonitors(0, 0, (nint _, nint _, ref NativeRect rect, nint _) =>
        {
            result.Add(new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            return true;
        }, 0);
        return result;
    }

    private static Color FromColorRef(uint colorRef) =>
        Color.FromArgb(255, (byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));

    private const uint ClsctxInprocServer = 0x1;
    private const uint ClsctxLocalServer = 0x4;
    private const uint SpiGetDeskWallpaper = 0x0073;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref NativeRect rect, nint data);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, char[] buffer, uint winIni);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
}
