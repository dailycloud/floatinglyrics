namespace FloatingLyrics.Services;

public sealed class NoOpWindowModeService : IWindowModeService
{
    private double relativeX = 0.5;
    private double relativeY = 0.5;

    public WindowPresentationMode CurrentMode { get; private set; } = WindowPresentationMode.Floating;

    public bool IsAlwaysOnTop { get; private set; } = true;

    public bool TrySetMode(WindowPresentationMode mode)
    {
        CurrentMode = mode;
        return true;
    }

    public bool TrySetAlwaysOnTop(bool alwaysOnTop)
    {
        IsAlwaysOnTop = alwaysOnTop;
        return true;
    }

    public bool TryApplyPositionPreset(WindowPositionPreset preset)
    {
        return true;
    }

    public bool TryApplyRelativePosition(double relativeX, double relativeY)
    {
        this.relativeX = Math.Clamp(relativeX, 0, 1);
        this.relativeY = Math.Clamp(relativeY, 0, 1);
        return true;
    }

    public bool TryGetCurrentRelativePosition(out double relativeX, out double relativeY)
    {
        relativeX = this.relativeX;
        relativeY = this.relativeY;
        return true;
    }

    public bool TryGetWorkAreaSize(out int width, out int height)
    {
        width = 1920;
        height = 1080;
        return true;
    }

    public bool TryResize(int width, int height)
    {
        return true;
    }

    public bool TryEnsureVisibleOnWorkArea()
    {
        return true;
    }
}
