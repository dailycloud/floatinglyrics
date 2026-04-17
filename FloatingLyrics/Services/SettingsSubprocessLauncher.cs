using System.Diagnostics;

namespace FloatingLyrics.Services;

public static class SettingsSubprocessLauncher
{
    private const string SettingsModeArgument = "--settings";
    private const string SettingsModeEnvironmentVariable = "FLOATING_LYRICS_MODE";
    private const string SettingsModeEnvironmentValue = "settings";

    public static bool TryLaunch()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = SettingsModeArgument,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false
            };
            startInfo.Environment[SettingsModeEnvironmentVariable] = SettingsModeEnvironmentValue;
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
