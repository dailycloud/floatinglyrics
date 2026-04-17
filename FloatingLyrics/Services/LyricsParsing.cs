using System.Text.RegularExpressions;

namespace FloatingLyrics.Services;

internal static partial class LyricsParsing
{
    public static List<LyricLine> ParseLrc(string text)
    {
        var lines = new List<LyricLine>();
        foreach (var rawLine in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = TimestampRegex().Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            var minutes = int.Parse(match.Groups[1].Value);
            var seconds = int.Parse(match.Groups[2].Value);
            var fractionGroup = match.Groups[3].Value;
            var content = match.Groups[4].Value.Trim();

            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var milliseconds = fractionGroup.Length switch
            {
                0 => 0,
                2 => int.Parse(fractionGroup) * 10,
                _ => int.Parse(fractionGroup[..Math.Min(3, fractionGroup.Length)])
            };

            var timestamp =
                TimeSpan.FromMinutes(minutes) +
                TimeSpan.FromSeconds(seconds) +
                TimeSpan.FromMilliseconds(milliseconds);
            lines.Add(new LyricLine(content, timestamp));
        }

        return lines;
    }

    public static List<LyricLine> ParsePlainLyrics(string text)
    {
        var lines = new List<LyricLine>();
        foreach (var rawLine in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawLine.StartsWith('*') || rawLine.StartsWith("*******"))
            {
                continue;
            }

            lines.Add(new LyricLine(rawLine, null));
        }

        return lines;
    }

    [GeneratedRegex(@"^\[(\d{2}):(\d{2})(?:\.(\d{2,3}))?\](.+)$")]
    private static partial Regex TimestampRegex();
}
