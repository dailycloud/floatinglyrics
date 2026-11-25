using System.Runtime.InteropServices;
using System.Globalization;
using System.IO;
using FloatingLyrics.Services;
using FloatingLyrics.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;

namespace FloatingLyrics;

public partial class App : Application
{
    private const int GwlStyle = -16;
    private const int GwlExstyle = -20;
    private const nint WsCaption = 0x00C00000;
    private const nint WsThickFrame = 0x00040000;
    private const nint WsMinimizeBox = 0x00020000;
    private const nint WsMaximizeBox = 0x00010000;
    private const nint WsBorder = 0x00800000;
    private const nint WsDlgFrame = 0x00400000;
    private const nint WsExWindowEdge = 0x00000100;
    private const nint WsExClientEdge = 0x00000200;
    private const nint WsExDlgModalFrame = 0x00000001;
    private const nint WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private const int DwmaNcRenderingPolicy = 2;
    private const int DwmaWindowCornerPreference = 33;
    private const int DwmaBorderColor = 34;
    private const int DwmaRedirectionBitmapAlpha = 39;
    private const uint DwmncrpDisabled = 1;
    private const uint DwmwcpDoNotRound = 1;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint TrayIconIdBase = 7300;
    private const string SettingsModeArgument = "--settings";
    private const string DesktopWidgetStartupArgument = "--desktop-widget-startup";
    private const string SettingsModeEnvironmentVariable = "FLOATING_LYRICS_MODE";
    private const string SettingsModeEnvironmentValue = "settings";
    // Change these constants to rename overlay/settings window titles.
    private const string OverlayWindowTitleEn = "Floating Lyrics";
    private const string OverlayWindowTitleRu = "Плавающие тексты";
    private const string SettingsWindowTitleEn = "Floating Lyrics Settings";
    private const string SettingsWindowTitleRu = "Плавающие тексты — Настройки";

    private bool isDisposed;
    private bool isSettingsSubprocess;
    private bool forceDesktopWidgetOnLaunch;
    private MainPage? mainPage;
    private DispatcherQueue? uiDispatcherQueue;
    private WindowManager? windowManager;
    private FileSystemWatcher? settingsWatcher;
    private MenuFlyout? trayMenuFlyout;
    private ToggleMenuFlyoutItem? trayShowTimingsItem;
    private readonly uint trayIconId = TrayIconIdBase + (uint)(Environment.ProcessId % 100000);
    private TypedEventHandler<WindowManager, TrayIconEventArgs>? trayIconContextMenuHandler;
    private TypedEventHandler<WindowManager, TrayIconEventArgs>? trayIconSelectedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? mainViewModelChangedHandler;

    public Window MainWindow { get; private set; } = null!;

    public AppServices Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeResources();
    }

    private static bool UseRussianUi =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase);

    private static string T(string en, string ru) => UseRussianUi ? ru : en;

    private static string? ResolveAppIconPath()
    {
        var preferredOutputPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(preferredOutputPath))
        {
            return preferredOutputPath;
        }

        var outputAssetsDirectory = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (Directory.Exists(outputAssetsDirectory))
        {
            var firstOutputIcon = Directory.GetFiles(outputAssetsDirectory, "*.ico")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstOutputIcon))
            {
                return firstOutputIcon;
            }
        }

        var sourceAssetsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets"));
        if (!Directory.Exists(sourceAssetsDirectory))
        {
            return null;
        }

        var preferredSourcePath = Path.Combine(sourceAssetsDirectory, "AppIcon.ico");
        if (File.Exists(preferredSourcePath))
        {
            return preferredSourcePath;
        }

        return Directory.GetFiles(sourceAssetsDirectory, "*.ico")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static void ApplyWindowIcon(Window window, AppWindow? appWindow = null)
    {
        var iconPath = ResolveAppIconPath();
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            DebugTrace.Write("ApplyWindowIcon: no .ico found in Assets");
            return;
        }

        try
        {
            window.SetIcon(iconPath);
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"ApplyWindowIcon: window.SetIcon failed: {ex.Message}");
        }

        var targetAppWindow = appWindow;
        if (targetAppWindow is null)
        {
            try
            {
                targetAppWindow = AppWindow.GetFromWindowId(
                    Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                        WinRT.Interop.WindowNative.GetWindowHandle(window)));
            }
            catch (Exception ex)
            {
                DebugTrace.Write($"ApplyWindowIcon: AppWindow resolve failed: {ex.Message}");
            }
        }

        if (targetAppWindow is not null)
        {
            try
            {
                targetAppWindow.SetIcon(iconPath);
            }
            catch (Exception ex)
            {
                DebugTrace.Write($"ApplyWindowIcon: appWindow.SetIcon failed: {ex.Message}");
            }

            try
            {
                // WinUIEx tray icon mirrors taskbar icon, so set both.
                targetAppWindow.SetTaskbarIcon(iconPath);
            }
            catch (Exception ex)
            {
                DebugTrace.Write($"ApplyWindowIcon: appWindow.SetTaskbarIcon failed: {ex.Message}");
            }
        }

        DebugTrace.Write($"ApplyWindowIcon: {iconPath}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            DebugTrace.Write("OnLaunched: start");
            isSettingsSubprocess = IsSettingsModeLaunch(args.Arguments);
            forceDesktopWidgetOnLaunch = IsDesktopWidgetStartupLaunch(args.Arguments);
            var envMode = Environment.GetEnvironmentVariable(SettingsModeEnvironmentVariable) ?? "(null)";
            var commandLine = string.Join(" ", Environment.GetCommandLineArgs());
            DebugTrace.Write(
                $"OnLaunched: modeProbe args='{args.Arguments ?? "(null)"}' cmd='{commandLine}' env='{envMode}' settingsMode={isSettingsSubprocess} forceDesktopWidget={forceDesktopWidgetOnLaunch}");
            if (isSettingsSubprocess)
            {
                // Settings now live in the glass menu on the desktop; a separate settings window
                // is no longer used. Exit so an old shortcut does not start a second widget.
                DebugTrace.Write("OnLaunched: --settings is obsolete, exiting");
                Current.Exit();
                return;
            }

            LaunchOverlayWindow();
        }
        catch (Exception ex)
        {
            TryWriteCrashLog(ex);
            DebugTrace.Write($"OnLaunched: fatal {ex}");
            MainWindow = new Window
            {
                Title = "Floating Lyrics - startup error",
                Content = new TextBlock
                {
                    Text = "Startup failed. Check %LocalAppData%\\FloatingLyrics\\crash.log",
                    Margin = new Thickness(24)
                }
            };
            MainWindow.Activate();
        }
    }

    private void LaunchOverlayWindow()
    {
        MainWindow = new Window
        {
            Title = T(OverlayWindowTitleEn, OverlayWindowTitleRu)
        };

        DebugTrace.Write("OnLaunched: creating services");
        Services = new AppServices(MainWindow);
        Services.MainViewModel.SetOverlayResizeDeferred(true);
        // The widget is desktop-only; the host attach itself is applied from MainPage.OnLoaded
        // after full XAML/UI init (early host-attach can be unstable on some shells).
        Services.MainViewModel.DesktopWidgetMode = true;
        DebugTrace.Write($"OnLaunched: desktop widget (startup flag={forceDesktopWidgetOnLaunch}, deferred apply)");
        DebugTrace.Write("OnLaunched: creating page");
        mainPage = new MainPage(Services.MainViewModel);
        MainWindow.Content = mainPage;
        DebugTrace.Write("OnLaunched: activating window");
        MainWindow.Activate();
        ApplyWindowIcon(MainWindow);
        MainWindow.Closed += (_, _) => DisposeResources();
        uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();

        try
        {
            DebugTrace.Write("OnLaunched: configuring overlay");
            ConfigureOverlayWindow(MainWindow, (int)Math.Round(Services.MainViewModel.OverlayWidth));
            DebugTrace.Write("OnLaunched: overlay configured");
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"OnLaunched: configure overlay failed: {ex}");
        }

        try
        {
            DebugTrace.Write("OnLaunched: init tray");
            InitializeTrayMenu();
            DebugTrace.Write("OnLaunched: tray initialized");
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"OnLaunched: tray init failed: {ex}");
        }
        InitializeSettingsWatcher();
        DebugTrace.Write("OnLaunched: startup state deferred to MainPage.OnLoaded");
        DebugTrace.Write("OnLaunched: done");
    }

    private static bool IsSettingsModeLaunch(string? launchArguments)
    {
        if (ContainsSettingsArgument(launchArguments))
        {
            return true;
        }

        var commandLineArgs = Environment.GetCommandLineArgs();
        if (commandLineArgs.Any(a => a.Equals(SettingsModeArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var settingsMode = Environment.GetEnvironmentVariable(SettingsModeEnvironmentVariable);
        return settingsMode?.Equals(SettingsModeEnvironmentValue, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool ContainsSettingsArgument(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Equals(SettingsModeArgument, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDesktopWidgetStartupLaunch(string? launchArguments)
    {
        if (ContainsDesktopWidgetStartupArgument(launchArguments))
        {
            return true;
        }

        return Environment.GetCommandLineArgs()
            .Any(token => token.Equals(DesktopWidgetStartupArgument, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsDesktopWidgetStartupArgument(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Equals(DesktopWidgetStartupArgument, StringComparison.OrdinalIgnoreCase));
    }

    private void DisposeResources()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        DisposeSettingsWatcher();
        if (mainViewModelChangedHandler is not null)
        {
            Services.MainViewModel.PropertyChanged -= mainViewModelChangedHandler;
            mainViewModelChangedHandler = null;
        }

        if (windowManager is not null)
        {
            if (trayIconSelectedHandler is not null)
            {
                windowManager.TrayIconSelected -= trayIconSelectedHandler;
                trayIconSelectedHandler = null;
            }

            if (trayIconContextMenuHandler is not null)
            {
                windowManager.TrayIconContextMenu -= trayIconContextMenuHandler;
                trayIconContextMenuHandler = null;
            }

            windowManager.IsVisibleInTray = false;
            windowManager = null;
        }

        trayMenuFlyout = null;
        Services?.Dispose();
    }

    private void InitializeSettingsWatcher()
    {
        try
        {
            var filePath = Services.MainViewModel.SettingsFilePath;
            var directoryPath = Path.GetDirectoryName(filePath);
            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            Directory.CreateDirectory(directoryPath);
            settingsWatcher = new FileSystemWatcher(directoryPath, fileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            settingsWatcher.Changed += OnSettingsFileChanged;
            settingsWatcher.Created += OnSettingsFileChanged;
            settingsWatcher.Renamed += OnSettingsFileRenamed;
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Settings watcher init failed: {ex.Message}");
        }
    }

    private void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
    {
        _ = uiDispatcherQueue?.TryEnqueue(() =>
        {
            if (!isDisposed)
            {
                Services.MainViewModel.ReloadSettingsFromDisk();
            }
        });
    }

    private void OnSettingsFileRenamed(object sender, RenamedEventArgs e)
    {
        _ = uiDispatcherQueue?.TryEnqueue(() =>
        {
            if (!isDisposed)
            {
                Services.MainViewModel.ReloadSettingsFromDisk();
            }
        });
    }

    private void DisposeSettingsWatcher()
    {
        if (settingsWatcher is null)
        {
            return;
        }

        settingsWatcher.EnableRaisingEvents = false;
        settingsWatcher.Changed -= OnSettingsFileChanged;
        settingsWatcher.Created -= OnSettingsFileChanged;
        settingsWatcher.Renamed -= OnSettingsFileRenamed;
        settingsWatcher.Dispose();
        settingsWatcher = null;
    }

    private void InitializeTrayMenu()
    {
        trayMenuFlyout = BuildTrayMenuFlyout();
        windowManager = WindowManager.Get(MainWindow);
        windowManager.TrayIconId = trayIconId;
        windowManager.IsVisibleInTray = true;

        trayIconSelectedHandler = (_, _) => OpenSettingsFromTray();
        trayIconContextMenuHandler = (_, e) =>
        {
            SyncTrayMenuState();
            e.Flyout = trayMenuFlyout;
            e.Handled = true;
        };
        windowManager.TrayIconSelected += trayIconSelectedHandler;
        windowManager.TrayIconContextMenu += trayIconContextMenuHandler;

        mainViewModelChangedHandler = (_, e) =>
        {
            if (isDisposed || e.PropertyName is not nameof(MainViewModel.ShowLineTimings))
            {
                return;
            }

            _ = uiDispatcherQueue?.TryEnqueue(() =>
            {
                if (!isDisposed)
                {
                    SyncTrayMenuState();
                }
            });
        };
        Services.MainViewModel.PropertyChanged += mainViewModelChangedHandler;
        SyncTrayMenuState();
    }

    private MenuFlyout BuildTrayMenuFlyout()
    {
        var viewModel = Services.MainViewModel;
        var flyout = new MenuFlyout();

        var openSettingsItem = new MenuFlyoutItem
        {
            Text = T("Widget Settings (on desktop)", "Настройки виджета (на рабочем столе)"),
            Icon = new FontIcon { Glyph = "" }
        };
        openSettingsItem.Click += (_, _) => OpenSettingsFromTray();
        flyout.Items.Add(openSettingsItem);

        var showOverlayItem = new MenuFlyoutItem
        {
            Text = T("Re-attach to Desktop", "Вернуть на рабочий стол"),
            Icon = new FontIcon { Glyph = "" }
        };
        showOverlayItem.Click += (_, _) => ShowOverlayFromTray();
        flyout.Items.Add(showOverlayItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        trayShowTimingsItem = new ToggleMenuFlyoutItem { Text = T("Show Timings", "Показывать тайминги") };
        trayShowTimingsItem.Click += (_, _) => viewModel.ShowLineTimings = trayShowTimingsItem.IsChecked;
        flyout.Items.Add(trayShowTimingsItem);

        var refreshItem = new MenuFlyoutItem
        {
            Text = T("Refresh Lyrics", "Обновить текст"),
            Icon = new FontIcon { Glyph = "" }
        };
        refreshItem.Click += (_, _) =>
        {
            if (viewModel.RefreshCommand.CanExecute(null))
            {
                _ = viewModel.RefreshCommand.ExecuteAsync(null);
            }
        };
        flyout.Items.Add(refreshItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem { Text = T("Exit", "Выход") };
        exitItem.Click += (_, _) => Current.Exit();
        flyout.Items.Add(exitItem);

        return flyout;
    }

    private void SyncTrayMenuState()
    {
        if (trayMenuFlyout is null)
        {
            return;
        }

        if (trayShowTimingsItem is not null)
        {
            trayShowTimingsItem.IsChecked = Services.MainViewModel.ShowLineTimings;
        }
    }

    private void OpenSettingsFromTray()
    {
        // The menu opens next to the widget, on the desktop layer (never above other apps).
        mainPage?.OpenSettingsPanelFromTray();
    }

    private void ShowOverlayFromTray()
    {
        Services.MainViewModel.RestoreOverlayVisibility();
    }

    private static void ConfigureOverlayWindow(Window window, int preferredWidth)
    {
        // WinUIEx transparent backdrop keeps the top-level XAML host from falling back to a black fill.
        window.SystemBackdrop = new TransparentTintBackdrop(Color.FromArgb(0, 0, 0, 0));
        DebugTrace.Write("ConfigureOverlayWindow: TransparentTintBackdrop applied");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
        }

        var width = Math.Clamp(preferredWidth, 980, 2200);
        appWindow.Resize(new SizeInt32(width, 460));
        ApplyPopupWindowStyles(hwnd);
        ApplyBorderlessDwmStyle(hwnd);
    }

    private static void ApplyPopupWindowStyles(nint hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle);
        style &= ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsBorder | WsDlgFrame);
        _ = SetWindowLongPtr(hwnd, GwlStyle, style);

        var exStyle = GetWindowLongPtr(hwnd, GwlExstyle);
        exStyle &= ~(WsExWindowEdge | WsExClientEdge | WsExDlgModalFrame);
        exStyle |= WsExLayered;
        _ = SetWindowLongPtr(hwnd, GwlExstyle, exStyle);

        _ = SetWindowPos(
            hwnd,
            0,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFrameChanged);

        _ = SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
        var styleAfter = GetWindowLongPtr(hwnd, GwlStyle);
        var exStyleAfter = GetWindowLongPtr(hwnd, GwlExstyle);
        DebugTrace.Write($"ApplyPopupWindowStyles: style={FormatHex(styleAfter)} exStyle={FormatHex(exStyleAfter)}");
    }

    private static void ApplyBorderlessDwmStyle(nint hwnd)
    {
        var ncRenderingPolicy = DwmncrpDisabled;
        _ = DwmSetWindowAttribute(hwnd, DwmaNcRenderingPolicy, ref ncRenderingPolicy, Marshal.SizeOf<uint>());
        var cornerPreference = DwmwcpDoNotRound;
        _ = DwmSetWindowAttribute(hwnd, DwmaWindowCornerPreference, ref cornerPreference, Marshal.SizeOf<uint>());
        var borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(hwnd, DwmaBorderColor, ref borderColor, Marshal.SizeOf<uint>());
        var redirectionAlphaEnabled = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmaRedirectionBitmapAlpha, ref redirectionAlphaEnabled, Marshal.SizeOf<int>());
    }

    private static nint SetWindowLongPtr(nint hWnd, int index, nint value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, index, value)
            : SetWindowLong32(hWnd, index, unchecked((int)value));
    }

    private static nint GetWindowLongPtr(nint hWnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, index)
            : GetWindowLong32(hWnd, index);
    }

    private static string FormatHex(nint value)
    {
        return $"0x{unchecked((ulong)value):X}";
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        TryWriteCrashLog(e.Exception);
        DebugTrace.Write($"UnhandledException: {e.Exception}");
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        TryWriteCrashLog(e.ExceptionObject as Exception);
    }

    private static void TryWriteCrashLog(Exception? exception)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FloatingLyrics");
            Directory.CreateDirectory(root);
            var file = Path.Combine(root, "crash.log");
            var text = $"[{DateTime.Now:O}] {exception}\n\n";
            File.AppendAllText(file, text);
        }
        catch
        {
            // Ignore logging failures; they should not affect app lifecycle.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int attribute,
        ref uint attributeValue,
        int attributeSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(
        nint hWnd,
        uint crKey,
        byte bAlpha,
        uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int index, nint value);
}
