namespace FloatingLyrics.Services;

public interface IOverlaySettingsStore
{
    string SettingsFilePath { get; }
    string SettingsDirectoryPath { get; }
    OverlayUserSettings Load();
    void Save(OverlayUserSettings settings);
}
