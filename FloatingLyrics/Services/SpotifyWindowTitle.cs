using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FloatingLyrics.Services;

/// <summary>
/// Reads "Artist - Title" from the Spotify desktop window title.
///
/// Why: Spotify publishes new media properties to SMTC 10–30 s after a track starts (measured:
/// the SMTC timeline resets to 0:00 immediately, but the title keeps the previous song), while
/// its window title switches instantly. The window title is therefore used as the fast source of
/// the current track identity. Paused/idle titles ("Spotify Premium", "Spotify Free",
/// "Advertisement") are ignored.
/// </summary>
internal static class SpotifyWindowTitle
{
    private static readonly TimeSpan ResolveInterval = TimeSpan.FromSeconds(3);
    private static nint cachedWindow;
    private static long lastResolveTimestamp;

    public static bool TryRead(out string artist, out string title)
    {
        artist = string.Empty;
        title = string.Empty;
        var text = ReadTitle();
        if (string.IsNullOrWhiteSpace(text) ||
            text.StartsWith("Spotify", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Advertisement", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Artist names rarely contain " - ", titles often do ("Song - Remix"): split at the first.
        var separator = text.IndexOf(" - ", StringComparison.Ordinal);
        if (separator <= 0 || separator >= text.Length - 3)
        {
            return false;
        }

        artist = text[..separator].Trim();
        title = text[(separator + 3)..].Trim();
        return artist.Length > 0 && title.Length > 0;
    }

    private static string? ReadTitle()
    {
        if (cachedWindow == 0 || !IsWindow(cachedWindow) || NeedsResolve())
        {
            cachedWindow = FindSpotifyWindow();
            lastResolveTimestamp = Stopwatch.GetTimestamp();
        }

        if (cachedWindow == 0)
        {
            return null;
        }

        var length = GetWindowTextLength(cachedWindow);
        if (length <= 0)
        {
            cachedWindow = 0;
            return null;
        }

        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(cachedWindow, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static bool NeedsResolve() =>
        Stopwatch.GetElapsedTime(lastResolveTimestamp) > ResolveInterval &&
        GetWindowTextLength(cachedWindow) <= 0;

    private static nint FindSpotifyWindow()
    {
        HashSet<uint> spotifyProcessIds;
        try
        {
            spotifyProcessIds = Process.GetProcessesByName("Spotify").Select(p =>
            {
                using (p)
                {
                    return (uint)p.Id;
                }
            }).ToHashSet();
        }
        catch
        {
            return 0;
        }

        if (spotifyProcessIds.Count == 0)
        {
            return 0;
        }

        nint found = 0;
        _ = EnumWindows((window, lParam) =>
        {
            _ = GetWindowThreadProcessId(window, out var processId);
            if (!spotifyProcessIds.Contains(processId) || GetWindowTextLength(window) <= 0)
            {
                return true;
            }

            var className = new StringBuilder(64);
            _ = GetClassName(window, className, className.Capacity);
            if (!className.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal))
            {
                return true;
            }

            found = window;
            return false;
        }, 0);
        return found;
    }

    private delegate bool EnumWindowsProc(nint window, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);
}
