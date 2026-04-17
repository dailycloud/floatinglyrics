namespace FloatingLyrics.Services;

public interface IWindowModeService
{
    WindowPresentationMode CurrentMode { get; }
    bool IsAlwaysOnTop { get; }

    bool TrySetMode(WindowPresentationMode mode);
    bool TrySetAlwaysOnTop(bool alwaysOnTop);
    bool TryApplyPositionPreset(WindowPositionPreset preset);
    bool TryApplyRelativePosition(double relativeX, double relativeY);
    bool TryGetCurrentRelativePosition(out double relativeX, out double relativeY);
    bool TryGetWorkAreaSize(out int width, out int height);
    bool TryResize(int width, int height);
    bool TryEnsureVisibleOnWorkArea();
}
