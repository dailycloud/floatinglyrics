namespace FloatingLyrics.Services;

public interface IStartupRegistrationService
{
    bool IsEnabled();
    bool TrySetEnabled(bool enabled, bool startInDesktopWidgetMode = false);
}
