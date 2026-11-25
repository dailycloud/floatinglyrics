using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace FloatingLyrics.Services;

/// <summary>
/// Installs a release downloaded from GitHub: the zip is extracted next to the app, then a small
/// hidden script waits for the widget to exit, copies the new files over the app folder and
/// starts the widget again. Settings live in %LOCALAPPDATA%\FloatingLyrics and are untouched.
/// </summary>
public static class UpdateInstaller
{
    public const string AssetName = "FloatingLyrics-win-x64.zip";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Downloads and stages the update; returns false when it cannot be installed.</summary>
    public static async Task<bool> DownloadAndScheduleAsync(string assetUrl, Version version, CancellationToken cancellationToken)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var exePath = Environment.ProcessPath ?? Path.Combine(appDir, "FloatingLyrics.exe");
        var workDir = Path.Combine(Path.GetTempPath(), "FloatingLyrics-update", version.ToString());
        if (Directory.Exists(workDir))
        {
            Directory.Delete(workDir, recursive: true);
        }

        Directory.CreateDirectory(workDir);
        var zipPath = Path.Combine(workDir, AssetName);
        using (var request = new HttpRequestMessage(HttpMethod.Get, assetUrl))
        {
            request.Headers.UserAgent.ParseAdd("FloatingLyrics-Updater");
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(zipPath);
            await input.CopyToAsync(output, cancellationToken);
        }

        var staging = Path.Combine(workDir, "files");
        ZipFile.ExtractToDirectory(zipPath, staging);

        // The zip may contain the files directly or inside one top-level folder.
        if (!File.Exists(Path.Combine(staging, "FloatingLyrics.exe")))
        {
            var nested = Directory.GetDirectories(staging)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "FloatingLyrics.exe")));
            if (nested is null)
            {
                DebugTrace.Write("Update: package has no FloatingLyrics.exe");
                return false;
            }

            staging = nested;
        }

        // Hidden script: wait for this process, copy (no purge: never delete the user's other
        // files if the app lives in a shared folder), relaunch, clean up.
        var script = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine("chcp 65001 >nul")
            .AppendLine($"set PID={Environment.ProcessId}")
            .AppendLine(":wait")
            .AppendLine("tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul && (timeout /t 1 /nobreak >nul & goto wait)")
            .AppendLine($"robocopy \"{staging}\" \"{appDir}\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul")
            .AppendLine($"start \"\" \"{exePath}\" --desktop-widget-startup")
            .AppendLine($"rd /s /q \"{workDir}\" >nul 2>&1")
            .ToString();
        var scriptPath = Path.Combine(Path.GetTempPath(), "FloatingLyrics-update.cmd");
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false), cancellationToken);

        using var _ = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false
        });
        DebugTrace.Write($"Update: v{version} staged, restarting to install");
        return true;
    }
}
