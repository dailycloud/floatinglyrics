using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FloatingLyrics.Services;

/// <summary>
/// Wallpaper Engine draws its wallpaper in a window inside the same desktop layer as the widget,
/// and Windows keeps reporting the old (or no) picture. When Wallpaper Engine is running, this
/// resolves an image that matches what is really on screen for each monitor: a frame of the
/// video for video wallpapers, otherwise the project's preview image.
/// </summary>
internal static class WallpaperEngineSource
{
    private static readonly string[] ProcessNames = ["wallpaper64", "wallpaper32"];
    private static readonly ConcurrentDictionary<string, byte> PendingFrames = new(StringComparer.OrdinalIgnoreCase);
    private static string? cachedInstallDir;

    /// <summary>Raised (on a pool thread) when a video frame finished extracting.</summary>
    public static event Action? FrameReady;

    public static bool IsRunning(out string? installDir)
    {
        installDir = null;
        foreach (var name in ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length == 0)
                {
                    continue;
                }

                installDir = cachedInstallDir;
                if (installDir is null)
                {
                    try
                    {
                        installDir = Path.GetDirectoryName(processes[0].MainModule?.FileName);
                    }
                    catch
                    {
                        // Access to the module can fail (e.g. 32/64-bit mismatch); search instead.
                    }

                    installDir ??= FindInstallDir();
                    cachedInstallDir = installDir;
                }

                return true;
            }
            finally
            {
                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
        }

        return false;
    }

    /// <summary>Changes whenever the wallpaper or its properties change in Wallpaper Engine.</summary>
    public static string GetConfigStamp(string? installDir)
    {
        try
        {
            return installDir is null ? string.Empty : File.GetLastWriteTimeUtc(Path.Combine(installDir, "config.json")).Ticks.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Image per monitor index (in Wallpaper Engine's order); empty when unknown.</summary>
    public static IReadOnlyList<string> ResolveMonitorImages(string? installDir)
    {
        var result = new List<string>();
        if (installDir is null)
        {
            return result;
        }

        var config = Path.Combine(installDir, "config.json");
        if (!File.Exists(config))
        {
            return result;
        }

        try
        {
            using var stream = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            // Sections are keyed by Windows user name (old profiles stay in the file): the
            // current user's section first, any other only as a fallback.
            var sections = doc.RootElement.EnumerateObject()
                .OrderBy(p => string.Equals(p.Name, Environment.UserName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
            foreach (var user in sections)
            {
                if (user.Value.ValueKind != JsonValueKind.Object ||
                    !user.Value.TryGetProperty("general", out var general) ||
                    !general.TryGetProperty("wallpaperconfig", out var wallpaperConfig) ||
                    !wallpaperConfig.TryGetProperty("selectedwallpapers", out var selected) ||
                    selected.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var monitor in selected.EnumerateObject())
                {
                    var file = monitor.Value.ValueKind == JsonValueKind.Object &&
                               monitor.Value.TryGetProperty("file", out var f) &&
                               f.ValueKind == JsonValueKind.String
                        ? f.GetString()
                        : null;
                    result.Add(ResolveProjectImage(file));
                }

                if (result.Count > 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"WallpaperEngine: config read failed {ex.Message}");
        }

        return result;
    }

    private static string ResolveProjectImage(string? wallpaperFile)
    {
        if (string.IsNullOrWhiteSpace(wallpaperFile))
        {
            return string.Empty;
        }

        wallpaperFile = wallpaperFile.Replace('/', Path.DirectorySeparatorChar);
        var dir = Path.GetDirectoryName(wallpaperFile);
        if (dir is null || !Directory.Exists(dir))
        {
            return string.Empty;
        }

        string? type = null;
        string? file = null;
        string? preview = null;
        try
        {
            var projectPath = Path.Combine(dir, "project.json");
            if (File.Exists(projectPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(projectPath));
                var root = doc.RootElement;
                type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                file = root.TryGetProperty("file", out var fl) && fl.ValueKind == JsonValueKind.String ? fl.GetString() : null;
                preview = root.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null;
            }
        }
        catch
        {
            // A broken project.json just means we fall back to the preview lookup below.
        }

        // Video wallpapers: a real full-frame image of the video (the preview is a square crop).
        var video = file is not null ? Path.Combine(dir, file) : wallpaperFile;
        if ((string.Equals(type, "video", StringComparison.OrdinalIgnoreCase) || IsVideo(video)) && File.Exists(video))
        {
            var frame = GetFrameCachePath(video);
            if (File.Exists(frame))
            {
                return frame;
            }

            QueueFrameExtraction(video, frame);
        }

        if (preview is not null && File.Exists(Path.Combine(dir, preview)))
        {
            return Path.Combine(dir, preview);
        }

        foreach (var name in new[] { "preview.jpg", "preview.png", "preview.gif" })
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static bool IsVideo(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".webm" or ".mkv" or ".avi" or ".mov" or ".wmv";

    private static string GetFrameCachePath(string video)
    {
        var info = new FileInfo(video);
        var key = $"{video.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FloatingLyrics", "cache", $"we-frame-{hash}.jpg");
    }

    private static void QueueFrameExtraction(string video, string target)
    {
        if (!PendingFrames.TryAdd(target, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(video);
                var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(storageFile);
                var composition = new Windows.Media.Editing.MediaComposition();
                composition.Clips.Add(clip);

                var duration = clip.OriginalDuration;
                var at = duration > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(1) : TimeSpan.Zero;
                var props = clip.GetVideoEncodingProperties();
                var width = (int)Math.Clamp(props?.Width ?? 1920, 64, 2560);
                var height = (int)Math.Clamp(props?.Height ?? 1080, 64, 1440);

                using var image = await composition.GetThumbnailAsync(at, width, height, Windows.Media.Editing.VideoFramePrecision.NearestFrame);
                using var input = image.AsStreamForRead();
                var temp = target + ".tmp";
                using (var output = File.Create(temp))
                {
                    await input.CopyToAsync(output);
                }

                File.Move(temp, target, overwrite: true);
                DebugTrace.Write($"WallpaperEngine: video frame extracted {width}x{height} from '{Path.GetFileName(video)}'");
                FrameReady?.Invoke();
            }
            catch (Exception ex)
            {
                DebugTrace.Write($"WallpaperEngine: frame extraction failed {ex.Message}");
            }
            finally
            {
                PendingFrames.TryRemove(target, out _);
            }
        });
    }

    private static string? FindInstallDir()
    {
        var candidates = new List<string>();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steam)
            {
                candidates.Add(Path.Combine(steam.Replace('/', '\\'), "steamapps", "common", "wallpaper_engine"));
                var libraries = Path.Combine(steam.Replace('/', '\\'), "steamapps", "libraryfolders.vdf");
                if (File.Exists(libraries))
                {
                    foreach (var line in File.ReadLines(libraries))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 3)
                            {
                                candidates.Add(Path.Combine(parts[^1].Replace(@"\\", @"\"), "steamapps", "common", "wallpaper_engine"));
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return candidates.FirstOrDefault(dir => File.Exists(Path.Combine(dir, "config.json")));
    }
}
