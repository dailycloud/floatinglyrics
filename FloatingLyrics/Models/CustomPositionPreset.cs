namespace FloatingLyrics.Models;

public sealed class CustomPositionPreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public double RelativeX { get; init; }
    public double RelativeY { get; init; }
}
