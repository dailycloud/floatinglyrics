using System.Text.Json;
using System.Threading;

namespace FloatingLyrics.Services;

public sealed class OverlaySettingsStore : IOverlaySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string filePath;
    private readonly string directoryPath;
    private readonly object syncRoot = new();
    private OverlayUserSettings? lastKnownGoodSettings;

    public OverlaySettingsStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FloatingLyrics");
        directoryPath = root;
        filePath = Path.Combine(root, "settings.json");
    }

    public string SettingsFilePath => filePath;
    public string SettingsDirectoryPath => directoryPath;

    public OverlayUserSettings Load()
    {
        lock (syncRoot)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return lastKnownGoodSettings ??= new OverlayUserSettings();
                }

                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        using var stream = new FileStream(
                            filePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        var json = reader.ReadToEnd();
                        var settings = JsonSerializer.Deserialize<OverlayUserSettings>(json, JsonOptions);
                        if (settings is not null)
                        {
                            lastKnownGoodSettings = settings;
                            return settings;
                        }

                        break;
                    }
                    catch (IOException) when (attempt < 2)
                    {
                        Thread.Sleep(20);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 2)
                    {
                        Thread.Sleep(20);
                    }
                    catch (JsonException)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // Fall through to return the last known good value.
            }

            return lastKnownGoodSettings ??= new OverlayUserSettings();
        }
    }

    public void Save(OverlayUserSettings settings)
    {
        lock (syncRoot)
        {
            string? tempPath = null;
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(settings, JsonOptions);
                tempPath = $"{filePath}.{Environment.ProcessId}.tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(filePath))
                {
                    File.Replace(tempPath, filePath, null, true);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }

                lastKnownGoodSettings = settings;
            }
            catch
            {
                // Best-effort save; runtime behavior should continue even when disk write fails.
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Ignore temp cleanup failures.
                }
            }
        }
    }
}
