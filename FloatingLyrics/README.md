# Floating Lyrics (WinUI 3)

WinUI 3 lyrics widget that lives **on the desktop only**:

- the widget is hosted inside the shell desktop window (on top of the wallpaper, below every
  application window) and never floats above other apps;
- hovering it materialises a **liquid-glass plate** behind the lyrics: the wallpaper under it is
  blurred, saturated and refracted by a lens-shaped rim (with chromatic dispersion, bevel
  highlights, a double rim and a cursor-following light), all rendered with Win2D;
- right-click (or the gear in the hover toolbar, or the tray icon) opens the **glass settings
  menu** right next to the widget, also on the desktop layer;
- animated lyric line transitions (smooth / spring / snappy), font, weight, colours, glass
  material, tint and shape are customisable from that menu;
- provider chain with automatic failover.

## Установка (для друзей)

1. Откройте [Releases](https://github.com/dailycloud/floatinglyrics/releases/latest) и скачайте `FloatingLyrics-win-x64.zip`.
2. Распакуйте в любую папку, например `%LOCALAPPDATA%\FloatingLyrics\app`, и запустите `FloatingLyrics.exe`.
   Ничего доустанавливать не нужно: .NET и Windows App SDK уже внутри.
3. Наведите на виджет → шестерёнка → «Виджет», чтобы включить автозапуск с Windows.

Обновления ставятся сами: виджет раз в несколько часов проверяет последний релиз, скачивает его,
перезапускается и продолжает работать. Настройки лежат отдельно (`%LOCALAPPDATA%\FloatingLyrics`)
и при обновлении не теряются. Пословные тексты Spicy Lyrics работают только со своим ключом
(`SpicyLyricsApiKey` в `settings.json`); без него используются обычные источники.

## Выпуск новой версии

Поднимите тег — GitHub Actions соберёт пакет и опубликует релиз:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

Локально пакет собирается `.\build-release.ps1 -Version 1.1.0` (→ `artifacts\FloatingLyrics-win-x64.zip`).

## Desktop layer notes

- The widget window is a child of the window that owns `SHELLDLL_DefView` (Progman on
  Windows 11 24H2+, a WorkerW on older shells). The app sends the usual `0x052C` message so the
  wallpaper is drawn by its own WorkerW and the icon layer becomes transparent.
- Idle: the widget sits below the icon layer, so icons and the desktop stay fully clickable.
  Hover / menu open: it moves above the icon layer and receives mouse input.
- The glass samples a copy of the real wallpaper (all monitors, fill/fit/stretch/tile/center/span)
  built from `IDesktopWallpaper`; it refreshes automatically when the wallpaper changes.
- A watchdog re-attaches the widget after an Explorer restart.

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

0. `Spicy Lyrics` — word-by-word (syllable) lyrics when a personal API key is configured:
   put it into `%LocalAppData%\FloatingLyrics\settings.json` as `"SpicyLyricsApiKey"` or set
   `FLOATING_LYRICS_SPICY_KEY`. The key is never stored in the repository.
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
  Spotify publishes new track metadata to SMTC 10–30 s late, so for Spotify the window title
  ("Artist - Title") is used while SMTC still reports the previous song.
- If no active media session is found, app falls back to demo tracks.
- Desktop hosting depends on Explorer shell state; the widget re-attaches itself after Explorer restarts.
