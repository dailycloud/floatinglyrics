using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using FloatingLyrics.Models;
using FloatingLyrics.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FloatingLyrics.Views;

public sealed class SettingsPage : Page
{
    private const int WmNclButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    private Color pageBackgroundColor;
    private Color surfaceColor;
    private Color surfaceMutedColor;
    private Color borderColor;
    private Color accentColor;
    private Color primaryTextColor;
    private Color secondaryTextColor;

    private FrameworkElement? titleBarDragRegion;
    private bool isLoaded;

    public MainViewModel ViewModel { get; }

    public SettingsPage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyPalette();
        Content = BuildUi();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static bool UseRussianUi =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase);

    private static string T(string en, string ru) => UseRussianUi ? ru : en;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        isLoaded = true;
        ApplyWindowChrome();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        isLoaded = false;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (Application.Current is not App app)
        {
            return;
        }

        try
        {
            app.MainWindow.ExtendsContentIntoTitleBar = false;
            app.MainWindow.SetTitleBar(null);
        }
        catch (COMException)
        {
            // Window can already be closed during teardown.
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SettingsUseDarkTheme))
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            ApplyPalette();
            Content = BuildUi();
            if (isLoaded)
            {
                ApplyWindowChrome();
            }
        });
    }

    private void ApplyPalette()
    {
        if (ViewModel.SettingsUseDarkTheme)
        {
            pageBackgroundColor = Color.FromArgb(255, 18, 20, 25);
            surfaceColor = Color.FromArgb(255, 29, 33, 40);
            surfaceMutedColor = Color.FromArgb(255, 35, 39, 48);
            borderColor = Color.FromArgb(255, 71, 80, 97);
            accentColor = Color.FromArgb(255, 150, 187, 246);
            primaryTextColor = Color.FromArgb(255, 232, 238, 248);
            secondaryTextColor = Color.FromArgb(255, 150, 161, 181);
        }
        else
        {
            pageBackgroundColor = Color.FromArgb(255, 236, 239, 245);
            surfaceColor = Color.FromArgb(255, 246, 248, 252);
            surfaceMutedColor = Color.FromArgb(255, 238, 242, 249);
            borderColor = Color.FromArgb(255, 176, 186, 204);
            accentColor = Color.FromArgb(255, 63, 111, 189);
            primaryTextColor = Color.FromArgb(255, 38, 46, 61);
            secondaryTextColor = Color.FromArgb(255, 93, 104, 124);
        }

        Background = new SolidColorBrush(pageBackgroundColor);
    }

    private UIElement BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var topBar = BuildTopBar();
        root.Children.Add(topBar);

        var scroll = new ScrollViewer
        {
            Margin = new Thickness(6, 0, 6, 6),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var stack = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        scroll.Content = stack;

        stack.Children.Add(BuildPlaybackWindowCard());
        stack.Children.Add(BuildUpdatesCard());
        stack.Children.Add(BuildLyricsCard());
        stack.Children.Add(BuildOscillatorCard());
        stack.Children.Add(BuildToolsDebugCard());

        return root;
    }

    private UIElement BuildTopBar()
    {
        var shell = new Border
        {
            Margin = new Thickness(6, 6, 6, 6),
            Padding = new Thickness(6, 4, 6, 4),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(borderColor),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(surfaceMutedColor)
        };

        var layout = new Grid
        {
            Height = 24,
            ColumnSpacing = 6
        };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dragZone = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        dragZone.PointerPressed += DragZone_OnPointerPressed;

        var dragLine = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(999),
            Background = new SolidColorBrush(Color.FromArgb(180, borderColor.R, borderColor.G, borderColor.B)),
            Margin = new Thickness(2, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        dragZone.Children.Add(dragLine);

        titleBarDragRegion = dragZone;
        layout.Children.Add(dragZone);

        var refreshButton = CreateHeaderIconButton("\uE72C", T("Refresh", "Обновить"), RefreshButton_OnClick);
        Grid.SetColumn(refreshButton, 1);
        layout.Children.Add(refreshButton);

        var closeButton = CreateHeaderIconButton("\uE8BB", T("Close", "Закрыть"), CloseButton_OnClick);
        Grid.SetColumn(closeButton, 2);
        layout.Children.Add(closeButton);

        shell.Child = layout;
        return shell;
    }

    private UIElement BuildPlaybackWindowCard()
    {
        var body = CreateSectionBody();
        body.Children.Add(CreateToggleRow(T("Dark theme (settings)", "Тёмная тема (настройки)"), nameof(MainViewModel.SettingsUseDarkTheme)));
        body.Children.Add(CreateToggleRow(T("Hide overlay on pause", "Скрывать оверлей на паузе"), nameof(MainViewModel.HideOnPause)));
        body.Children.Add(CreateSliderRow(
            T("Pause hide delay", "Задержка скрытия"),
            nameof(MainViewModel.PauseHideDelaySeconds),
            1,
            20,
            0.5,
            nameof(MainViewModel.PauseHideDelayText)));
        body.Children.Add(CreateToggleRow(T("Desktop widget mode", "Режим desktop-widget"), nameof(MainViewModel.DesktopWidgetMode)));

        var alwaysOnTop = CreateToggleRow(T("Always on top", "Поверх всех окон"), nameof(MainViewModel.AlwaysOnTop));
        ((ToggleSwitch)alwaysOnTop.Children[1]).SetBinding(IsEnabledProperty, new Binding
        {
            Path = new PropertyPath(nameof(MainViewModel.AlwaysOnTopToggleEnabled)),
            Mode = BindingMode.OneWay
        });
        body.Children.Add(alwaysOnTop);

        body.Children.Add(CreateToggleRow(T("Show drag bar", "Показывать drag-полосу"), nameof(MainViewModel.ShowDragHandle)));
        body.Children.Add(CreateSliderRow(T("Overlay width", "Ширина оверлея"), nameof(MainViewModel.OverlayWidth), 980, 2200, 10));
        body.Children.Add(CreateToggleRow(T("Run at startup", "Запускать вместе с Windows"), nameof(MainViewModel.RunAtStartup)));
        body.Children.Add(CreateToggleRow(T("Start in desktop-widget mode", "Стартовать в desktop-widget"), nameof(MainViewModel.StartInDesktopWidgetMode)));

        return CreateSection(
            T("Playback & Window", "Плеер и окно"),
            T("Main overlay behavior.", "Основное поведение оверлея."),
            body,
            true);
    }

    private UIElement BuildLyricsCard()
    {
        var body = CreateSectionBody();
        body.Children.Add(CreateToggleRow(T("Show line timings", "Показывать тайминги"), nameof(MainViewModel.ShowLineTimings)));
        body.Children.Add(CreateToggleRow(T("Show cover art", "Показывать обложку"), nameof(MainViewModel.ShowCoverArt)));
        body.Children.Add(CreateToggleRow(T("Auto text alignment", "Автовыравнивание текста"), nameof(MainViewModel.AutoLyricsTextAlignment)));
        body.Children.Add(CreateComboRow(
            T("Manual text alignment", "Ручное выравнивание"),
            nameof(MainViewModel.LyricsTextAlignmentOptions),
            nameof(MainViewModel.LyricsTextAlignment),
            "Mode",
            "Label",
            ManualLyricsAlignmentComboBox_OnSelectionChanged));
        body.Children.Add(CreateSliderRow(
            T("Line animation duration", "Длительность анимации строк"),
            nameof(MainViewModel.LineAnimationDurationSeconds),
            0.12,
            1.2,
            0.01));
        body.Children.Add(CreateSliderRow(
            T("Current line size", "Размер текущей строки"),
            nameof(MainViewModel.CurrentLineFontSize),
            20,
            84,
            1));
        body.Children.Add(CreateSliderRow(
            T("Next line size", "Размер следующей строки"),
            nameof(MainViewModel.NextLineFontSize),
            16,
            54,
            1));
        body.Children.Add(CreateSliderRow(
            T("Gap between lines", "Отступ между строками"),
            nameof(MainViewModel.NextLineTopMargin),
            0,
            48,
            1));
        body.Children.Add(CreateSliderRow(
            T("Text shadow opacity", "Непрозрачность тени текста"),
            nameof(MainViewModel.TextShadowOpacity),
            0.05,
            1,
            0.01));

        return CreateSection(
            T("Lyrics Motion", "Движение строк"),
            T("Typography and line movement.", "Типографика и движение строк."),
            body,
            false);
    }

    private UIElement BuildUpdatesCard()
    {
        var body = CreateSectionBody();
        body.Children.Add(CreateInfoRow(T("Current version", "Текущая версия"), nameof(MainViewModel.AppVersionText)));
        body.Children.Add(CreateTextBoxRow(T("GitHub owner", "Владелец GitHub"), nameof(MainViewModel.GitHubOwner), T("Example: your-name", "Пример: your-name")));
        body.Children.Add(CreateTextBoxRow(T("Repository", "Репозиторий"), nameof(MainViewModel.GitHubRepository), T("Example: FloatingLyrics", "Пример: FloatingLyrics")));
        body.Children.Add(CreateInfoRow(T("Repository target", "Целевой репозиторий"), nameof(MainViewModel.UpdateRepositoryText)));
        body.Children.Add(CreateToggleRow(T("Check for updates automatically", "Проверять обновления автоматически"), nameof(MainViewModel.EnableAutomaticUpdateChecks)));
        body.Children.Add(CreateSliderRow(
            T("Auto-check interval", "Интервал автопроверки"),
            nameof(MainViewModel.UpdateCheckIntervalHours),
            1,
            168,
            1,
            nameof(MainViewModel.UpdateCheckIntervalText)));
        body.Children.Add(CreateInfoRow(T("Last check", "Последняя проверка"), nameof(MainViewModel.UpdateLastCheckedText)));
        body.Children.Add(CreateInfoRow(T("Updater status", "Статус updater-а"), nameof(MainViewModel.UpdateStatusText)));

        var actionsGrid = new Grid
        {
            ColumnSpacing = 6
        };
        actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var checkButton = CreateActionButton(T("Check now", "Проверить"), CheckForUpdatesButton_OnClick);
        actionsGrid.Children.Add(checkButton);

        var releasesButton = CreateActionButton(T("Open releases", "Открыть releases"), OpenReleasesButton_OnClick);
        Grid.SetColumn(releasesButton, 1);
        actionsGrid.Children.Add(releasesButton);

        body.Children.Add(actionsGrid);

        return CreateSection(
            T("Updates", "Обновления"),
            T("GitHub release checks for the unpackaged build.", "Проверка GitHub Releases для unpackaged-сборки."),
            body,
            false);
    }

    private UIElement BuildOscillatorCard()
    {
        var body = CreateSectionBody();
        body.Children.Add(CreateToggleRow(T("Capture only active app (beta)", "Захват только активного приложения (beta)"), nameof(MainViewModel.AppScopedAudioCapture)));
        body.Children.Add(CreateToggleRow(T("Mirror bars", "Зеркалить полосы"), nameof(MainViewModel.OscilloscopeMirrorBars)));
        body.Children.Add(CreateSliderRow(T("Detail (bars)", "Детализация (полосы)"), nameof(MainViewModel.OscilloscopeDetail), 96, 320, 2));
        body.Children.Add(CreateSliderRow(T("Responsiveness", "Отзывчивость"), nameof(MainViewModel.OscilloscopeResponsiveness), 0.08, 0.95, 0.01));
        body.Children.Add(CreateSliderRow(T("Sensitivity", "Чувствительность"), nameof(MainViewModel.OscilloscopeSensitivity), 0.5, 2.6, 0.01));
        body.Children.Add(CreateSliderRow(T("Peak decay", "Затухание пиков"), nameof(MainViewModel.OscilloscopePeakDecay), 0.65, 0.99, 0.01));
        body.Children.Add(CreateSliderRow(T("Color intensity", "Интенсивность цвета"), nameof(MainViewModel.OscilloscopeColorIntensity), 0, 1, 0.01));
        body.Children.Add(CreateSliderRow(T("Low-frequency focus", "Фокус низких частот"), nameof(MainViewModel.OscilloscopeLowFrequencyFocus), 0, 1, 0.01));

        return CreateSection(
            T("Oscilloscope", "Осциллоскоп"),
            T("FFT and visual responsiveness.", "FFT и визуальная отзывчивость."),
            body,
            false);
    }

    private UIElement BuildToolsDebugCard()
    {
        var body = CreateSectionBody();
        body.Children.Add(CreateSliderRow(T("Viewport fade zone", "Зона fade по краям"), nameof(MainViewModel.ViewportFadeZone), 0, 180, 1));
        body.Children.Add(CreateSliderRow(T("Viewport fade softness", "Мягкость fade"), nameof(MainViewModel.ViewportFadeSoftness), 0.4, 2.2, 0.01));
        body.Children.Add(CreateToggleRow(T("Debug hitboxes", "Debug-хитбоксы"), nameof(MainViewModel.ShowDebugHitboxes)));
        body.Children.Add(CreateToggleRow(T("Runtime debug panel", "Панель runtime-debug"), nameof(MainViewModel.ShowRuntimeDebugPanel)));

        var actionsGrid = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 6
        };
        actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        actionsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        actionsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var configFolder = CreateActionButton(T("Config folder", "Папка конфига"), OpenConfigFolderButton_OnClick);
        actionsGrid.Children.Add(configFolder);

        var configFile = CreateActionButton(T("Open settings.json", "Открыть settings.json"), OpenConfigFileButton_OnClick);
        Grid.SetColumn(configFile, 1);
        actionsGrid.Children.Add(configFile);

        var logs = CreateActionButton(T("Logs folder", "Папка логов"), OpenLogsFolderButton_OnClick);
        Grid.SetRow(logs, 1);
        actionsGrid.Children.Add(logs);

        var export = CreateActionButton(T("Export snapshot", "Экспорт snapshot"), ExportDebugSnapshotButton_OnClick);
        Grid.SetRow(export, 1);
        Grid.SetColumn(export, 1);
        actionsGrid.Children.Add(export);

        var reset = CreateActionButton(T("Reset all settings", "Сбросить все настройки"), ResetAllButton_OnClick);
        Grid.SetRow(reset, 2);
        Grid.SetColumnSpan(reset, 2);
        actionsGrid.Children.Add(reset);

        body.Children.Add(actionsGrid);

        return CreateSection(
            T("Tools & Debug", "Инструменты и отладка"),
            T("System parameters and utilities.", "Системные параметры и утилиты."),
            body,
            false);
    }

    private void ApplyWindowChrome()
    {
        if (Application.Current is not App app)
        {
            return;
        }

        app.MainWindow.ExtendsContentIntoTitleBar = false;
        app.MainWindow.SetTitleBar(null);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app.MainWindow);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Title = string.Empty;

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = appWindow.TitleBar;
        titleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.ForegroundColor = Colors.Transparent;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.InactiveForegroundColor = Colors.Transparent;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = Colors.Transparent;
        titleBar.ButtonPressedBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Colors.Transparent;
        titleBar.ButtonInactiveForegroundColor = Colors.Transparent;
        titleBar.ButtonHoverForegroundColor = Colors.Transparent;
        titleBar.ButtonPressedForegroundColor = Colors.Transparent;
    }

    private Button CreateHeaderIconButton(string glyph, string toolTip, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 12,
                Foreground = new SolidColorBrush(primaryTextColor)
            },
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(borderColor),
            Background = new SolidColorBrush(surfaceColor),
            Foreground = new SolidColorBrush(primaryTextColor)
        };

        ToolTipService.SetToolTip(button, toolTip);
        button.Click += handler;
        return button;
    }

    private Button CreateActionButton(string caption, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = caption,
            Padding = new Thickness(6, 4, 6, 4),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(borderColor),
            Background = new SolidColorBrush(surfaceColor),
            Foreground = new SolidColorBrush(primaryTextColor),
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 11
        };
        button.Click += onClick;
        return button;
    }

    private StackPanel CreateSectionBody()
    {
        return new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private UIElement CreateSection(string title, string caption, UIElement content, bool isExpanded)
    {
        var shell = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(borderColor),
            Background = new SolidColorBrush(surfaceColor),
            Padding = new Thickness(8, 6, 8, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var expander = new Expander
        {
            IsExpanded = isExpanded,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var header = new StackPanel
        {
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Segoe UI Variable Text Semibold"),
            FontSize = 12,
            Foreground = new SolidColorBrush(accentColor)
        });
        header.Children.Add(new TextBlock
        {
            Text = caption,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 10,
            Foreground = new SolidColorBrush(secondaryTextColor),
            TextWrapping = TextWrapping.WrapWholeWords
        });
        expander.Header = header;

        var bodyBorder = new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content
        };
        expander.Content = bodyBorder;

        shell.Child = expander;
        return shell;
    }

    private Grid CreateToggleRow(string title, string propertyName)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(primaryTextColor),
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12
        });

        var toggle = new ToggleSwitch
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            OnContent = string.Empty,
            OffContent = string.Empty
        };
        toggle.SetBinding(ToggleSwitch.IsOnProperty, new Binding
        {
            Path = new PropertyPath(propertyName),
            Mode = BindingMode.TwoWay
        });
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private UIElement CreateSliderRow(
        string label,
        string propertyName,
        double min,
        double max,
        double step,
        string? captionPropertyName = null)
    {
        var row = new StackPanel { Spacing = 3 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(primaryTextColor),
            FontFamily = new FontFamily("Segoe UI Variable Text Semibold"),
            FontSize = 11
        });

        if (!string.IsNullOrWhiteSpace(captionPropertyName))
        {
            var caption = new TextBlock
            {
                Foreground = new SolidColorBrush(secondaryTextColor),
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = 10
            };
            caption.SetBinding(TextBlock.TextProperty, new Binding
            {
                Path = new PropertyPath(captionPropertyName),
                Mode = BindingMode.OneWay
            });
            row.Children.Add(caption);
        }

        row.Children.Add(CreateSlider(propertyName, min, max, step));
        return row;
    }

    private UIElement CreateTextBoxRow(string label, string propertyName, string placeholderText)
    {
        var row = new StackPanel { Spacing = 3 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(primaryTextColor),
            FontFamily = new FontFamily("Segoe UI Variable Text Semibold"),
            FontSize = 11
        });

        var textBox = new TextBox
        {
            PlaceholderText = placeholderText,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        textBox.SetBinding(TextBox.TextProperty, new Binding
        {
            Path = new PropertyPath(propertyName),
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        row.Children.Add(textBox);
        return row;
    }

    private UIElement CreateInfoRow(string label, string propertyName)
    {
        var row = new StackPanel { Spacing = 2 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(primaryTextColor),
            FontFamily = new FontFamily("Segoe UI Variable Text Semibold"),
            FontSize = 11
        });

        var valueText = new TextBlock
        {
            Foreground = new SolidColorBrush(secondaryTextColor),
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 10,
            TextWrapping = TextWrapping.WrapWholeWords
        };
        valueText.SetBinding(TextBlock.TextProperty, new Binding
        {
            Path = new PropertyPath(propertyName),
            Mode = BindingMode.OneWay
        });
        row.Children.Add(valueText);
        return row;
    }

    private UIElement CreateComboRow(
        string label,
        string itemsSourceProperty,
        string selectedValueProperty,
        string selectedValuePath,
        string displayMemberPath,
        SelectionChangedEventHandler? selectionChanged = null)
    {
        var row = new StackPanel { Spacing = 3 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(primaryTextColor),
            FontFamily = new FontFamily("Segoe UI Variable Text Semibold"),
            FontSize = 11
        });

        var combo = CreateComboBox(itemsSourceProperty, selectedValueProperty, selectedValuePath, displayMemberPath);
        if (selectionChanged is not null)
        {
            combo.SelectionChanged += selectionChanged;
        }

        row.Children.Add(combo);
        return row;
    }

    private Slider CreateSlider(string propertyName, double min, double max, double step)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = step,
            Foreground = new SolidColorBrush(accentColor),
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        slider.SetBinding(Slider.ValueProperty, new Binding
        {
            Path = new PropertyPath(propertyName),
            Mode = BindingMode.TwoWay
        });
        return slider;
    }

    private ComboBox CreateComboBox(string itemsSourceProperty, string selectedValueProperty, string selectedValuePath, string displayMemberPath)
    {
        var combo = new ComboBox
        {
            DisplayMemberPath = displayMemberPath,
            SelectedValuePath = selectedValuePath,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 180
        };
        combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding
        {
            Path = new PropertyPath(itemsSourceProperty),
            Mode = BindingMode.OneWay
        });
        combo.SetBinding(ComboBox.SelectedValueProperty, new Binding
        {
            Path = new PropertyPath(selectedValueProperty),
            Mode = BindingMode.TwoWay
        });
        return combo;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.MainWindow.Close();
        }
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.RefreshCommand.CanExecute(null))
        {
            await ViewModel.RefreshCommand.ExecuteAsync(null);
        }
    }

    private void OpenConfigFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenConfigFolderCommand.Execute(null);
    }

    private void OpenConfigFileButton_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenConfigFileCommand.Execute(null);
    }

    private void OpenLogsFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenLogsFolderCommand.Execute(null);
    }

    private async void CheckForUpdatesButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CheckForUpdatesCommand.CanExecute(null))
        {
            await ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        }
    }

    private void OpenReleasesButton_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenUpdateReleasePageCommand.Execute(null);
    }

    private void ExportDebugSnapshotButton_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ExportDebugSnapshotCommand.Execute(null);
    }

    private void ResetAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetAllSettingsCommand.Execute(null);
    }

    private void ManualLyricsAlignmentComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedValue is null)
        {
            return;
        }

        if (combo.SelectedValue is LyricsTextAlignmentMode mode)
        {
            ViewModel.LyricsTextAlignment = mode;
        }
    }

    private void DragZone_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Application.Current is not App app)
        {
            return;
        }

        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app.MainWindow);
        _ = ReleaseCapture();
        _ = SendMessage(hwnd, WmNclButtonDown, (nint)HtCaption, 0);
        e.Handled = true;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);
}
