# Floating Lyrics (WinUI 3)

Minimal WinUI 3 prototype for a Windows 11 lyrics widget with:

- animated lyric line transitions,
- `Floating` window mode,
- `Wallpaper` mode (window reparented to desktop worker window),
- provider chain with automatic failover.

## Run

```powershell
dotnet build FloatingLyrics.csproj
dotnet run --project FloatingLyrics.csproj
```

If your profile disk is low on space, use local caches:

```powershell
$env:DOTNET_CLI_HOME='D:\CODEx\floating_lyrics\.dotnet_cli_home'
$env:NUGET_PACKAGES='D:\CODEx\floating_lyrics\.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH='D:\CODEx\floating_lyrics\.nuget\v3-cache'
$env:TEMP='D:\CODEx\floating_lyrics\.nuget\temp'
$env:TMP='D:\CODEx\floating_lyrics\.nuget\temp'
```

## Lyrics Provider Chain

The app now tries sources in this order:

1. `LRCLIB` (no API key required)
2. `Musixmatch` (only when `MUSIXMATCH_API_KEY` is set)
3. Local `.lrc` files in `Lyrics` folder near the executable
4. Built-in fallback lines

If the loaded lyrics contain timestamps (`[mm:ss.xx]`), the widget syncs lines to the actual playback timeline from SMTC.
If timestamps are missing, it falls back to simple timed progression.

## Musixmatch (Optional)

Set API key before running:

```powershell
$env:MUSIXMATCH_API_KEY='your_api_key_here'
```

Without the key, the app still works through LRCLIB/local/fallback.

## Local LRC files

Create folder:

`D:\CODEx\floating_lyrics\FloatingLyrics\bin\Debug\net9.0-windows10.0.19041.0\Lyrics`

Supported file names:

- `Artist - Title.lrc`
- `Title - Artist.lrc`
- `Title.lrc`

## Notes

- Current track detection uses Windows SMTC (`GlobalSystemMediaTransportControlsSessionManager`).
- If no active media session is found, app falls back to demo tracks.
- Wallpaper mode behavior depends on Explorer shell state and may vary after Explorer restarts.
