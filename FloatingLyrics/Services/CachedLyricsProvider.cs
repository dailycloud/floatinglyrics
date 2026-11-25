using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloatingLyrics.Models;

namespace FloatingLyrics.Services;

public sealed class CachedLyricsProvider(ILyricsProvider inner) : ILyricsProvider
{
    private const int MaxMemoryEntries = 64;
    private const int MaxDiskFiles = 300;
    private const long MaxDiskBytes = 16L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FloatingLyrics",
        "lyrics-cache");

    private readonly ILyricsProvider inner = inner;
    private readonly object memoryLock = new();
    private readonly Dictionary<string, LinkedListNode<MemoryCacheItem>> memoryMap = new(StringComparer.Ordinal);
    private readonly LinkedList<MemoryCacheItem> memoryOrder = new();
    private int diskWriteCount;

    private sealed record MemoryCacheItem(string Key, LyricsResult Result);

    public async Task<LyricsResult?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Title))
        {
            return await inner.GetLyricsAsync(track, cancellationToken);
        }

        var key = BuildCacheKey(track);
        if (TryGetFromMemory(key, track.Duration, out var memoryResult))
        {
            return memoryResult;
        }

        var diskResult = await TryReadFromDiskAsync(key, track.Duration, cancellationToken);
        if (diskResult is not null)
        {
            StoreInMemory(key, diskResult);
            return diskResult;
        }

        var fetched = await inner.GetLyricsAsync(track, cancellationToken);
        if (fetched is { Lines.Count: > 0 })
        {
            StoreInMemory(key, fetched);
            _ = TryWriteToDiskAsync(key, fetched, cancellationToken);
        }

        return fetched;
    }

    private bool TryGetFromMemory(string key, TimeSpan? expectedDuration, out LyricsResult result)
    {
        lock (memoryLock)
        {
            if (memoryMap.TryGetValue(key, out var node))
            {
                if (IsDurationCompatible(expectedDuration, node.Value.Result.SourceDuration))
                {
                    memoryOrder.Remove(node);
                    memoryOrder.AddFirst(node);
                    result = node.Value.Result;
                    return true;
                }

                memoryOrder.Remove(node);
                memoryMap.Remove(key);
            }
        }

        result = null!;
        return false;
    }

    private void StoreInMemory(string key, LyricsResult result)
    {
        lock (memoryLock)
        {
            if (memoryMap.TryGetValue(key, out var existing))
            {
                existing.Value = new MemoryCacheItem(key, result);
                memoryOrder.Remove(existing);
                memoryOrder.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<MemoryCacheItem>(new MemoryCacheItem(key, result));
            memoryOrder.AddFirst(node);
            memoryMap[key] = node;

            while (memoryOrder.Count > MaxMemoryEntries && memoryOrder.Last is { } oldest)
            {
                memoryOrder.RemoveLast();
                memoryMap.Remove(oldest.Value.Key);
            }
        }
    }

    private static async Task<LyricsResult?> TryReadFromDiskAsync(
        string key,
        TimeSpan? expectedDuration,
        CancellationToken cancellationToken)
    {
        var path = GetCacheFilePath(key);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var cached = await JsonSerializer.DeserializeAsync<LyricsResult>(stream, JsonOptions, cancellationToken);
            if (cached is null || cached.Lines.Count == 0 || !IsDurationCompatible(expectedDuration, cached.SourceDuration))
            {
                return null;
            }

            return cached;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task TryWriteToDiskAsync(string key, LyricsResult result, CancellationToken cancellationToken)
    {
        var path = GetCacheFilePath(key);
        var tempPath = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
            if (Interlocked.Increment(ref diskWriteCount) % 32 == 0)
            {
                PruneDiskCacheBestEffort();
            }
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    private static void PruneDiskCacheBestEffort()
    {
        try
        {
            var directory = new DirectoryInfo(CacheDirectory);
            if (!directory.Exists)
            {
                return;
            }

            var files = directory
                .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(static file => file.LastWriteTimeUtc)
                .ToList();

            long totalBytes = 0;
            foreach (var file in files)
            {
                totalBytes += file.Length;
            }

            for (var i = files.Count - 1; i >= 0; i--)
            {
                if (i < MaxDiskFiles && totalBytes <= MaxDiskBytes)
                {
                    continue;
                }

                var file = files[i];
                try
                {
                    totalBytes -= file.Length;
                    file.Delete();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static bool IsDurationCompatible(TimeSpan? expected, TimeSpan? cached)
    {
        if (!expected.HasValue || !cached.HasValue)
        {
            return true;
        }

        return Math.Abs((expected.Value - cached.Value).TotalSeconds) <= 6.0;
    }

    private static string BuildCacheKey(TrackInfo track)
    {
        var artist = (track.Artist ?? string.Empty).Trim().ToLowerInvariant();
        var title = track.Title.Trim().ToLowerInvariant();
        return $"{artist}\u001f{title}";
    }

    private static string GetCacheFilePath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(CacheDirectory, $"{hash}.json");
    }
}
