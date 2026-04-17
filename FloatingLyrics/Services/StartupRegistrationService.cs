using Microsoft.Win32;

namespace FloatingLyrics.Services;

public sealed class StartupRegistrationService : IStartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "FloatingLyrics";
    private const string DesktopWidgetStartupArgument = "--desktop-widget-startup";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(EntryName) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var expectedPath = BuildExecutablePath();
            return value.Contains(expectedPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetEnabled(bool enabled, bool startInDesktopWidgetMode = false)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true) ??
                Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(EntryName, throwOnMissingValue: false);
                return true;
            }

            var executablePath = BuildExecutablePath();
            var command = startInDesktopWidgetMode
                ? $"\"{executablePath}\" {DesktopWidgetStartupArgument}"
                : $"\"{executablePath}\"";
            key.SetValue(EntryName, command);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            processPath = Path.Combine(AppContext.BaseDirectory, "FloatingLyrics.exe");
        }

        return processPath;
    }
}
