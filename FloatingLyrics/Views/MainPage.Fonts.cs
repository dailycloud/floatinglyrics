using System.Globalization;
using Microsoft.Graphics.Canvas.Text;
using FloatingLyrics.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingLyrics.Views;

/// <summary>An installed font family shown in the font picker (rendered in its own face).</summary>
public sealed class FontChoice(string name)
{
    public string Name { get; } = name;

    public FontFamily Family { get; } = new(name);
}

/// <summary>
/// Custom fonts: any font family installed on the system (including per-user installs) can be
/// used for the lyrics, or a .ttf/.otf/.ttc file can be loaded: it is installed for the current
/// user and applied immediately.
/// </summary>
public sealed partial class MainPage
{
    private bool fontListLoaded;

    public static string FormatOffset(double milliseconds) => Math.Abs(milliseconds) < 5
        ? "0 мс"
        : $"{(milliseconds > 0 ? "+" : "−")}{Math.Abs(Math.Round(milliseconds)):0} мс";

    public static string FormatSpicyStatus(bool hasKey) => hasKey
        ? "Spicy Lyrics подключён: пословные тексты, где они есть"
        : "Нужен ключ Spicy Lyrics (settings.json → SpicyLyricsApiKey)";

    private void ToggleFontListButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (FontList.Visibility == Visibility.Visible)
        {
            FontList.Visibility = Visibility.Collapsed;
            return;
        }

        if (!fontListLoaded)
        {
            LoadFontList();
        }

        FontList.Visibility = Visibility.Visible;
        var current = ViewModel.CustomFontFamily;
        if (!string.IsNullOrWhiteSpace(current) && FontList.ItemsSource is IList<FontChoice> items)
        {
            var match = items.FirstOrDefault(f => string.Equals(f.Name, current, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                FontList.SelectedItem = match;
                FontList.ScrollIntoView(match);
            }
        }
    }

    private void LoadFontList()
    {
        try
        {
            var languages = new[] { CultureInfo.CurrentUICulture.Name, "en-us" };
            var names = CanvasTextFormat.GetSystemFontFamilies(languages)
                .Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith('@'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .Select(name => new FontChoice(name))
                .ToList();
            FontList.ItemsSource = names;
            fontListLoaded = true;
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Fonts: enumeration failed {ex.Message}");
        }
    }

    /// <summary>Shows who provided the current lyrics in the hover toolbar (API attribution).</summary>
    private void UpdateLyricsAttribution()
    {
        var text = ViewModel.LyricsAttribution;
        ToolbarAttributionText.Text = text;
        ToolbarAttributionButton.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(ToolbarAttributionButton, string.IsNullOrWhiteSpace(ViewModel.LyricsAttributionUrl)
            ? text
            : $"{text}\n{ViewModel.LyricsAttributionUrl}");
    }

    private void ToolbarAttributionButton_OnClick(object sender, RoutedEventArgs e)
    {
        var url = ViewModel.LyricsAttributionUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Attribution: cannot open link {ex.Message}");
        }
    }

    private void FontList_OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FontChoice choice)
        {
            ViewModel.CustomFontFamily = choice.Name;
        }
    }

    /// <summary>
    /// "From file…": pick a .ttf/.otf/.ttc, install it for the current user (no admin rights)
    /// and apply it straight away.
    /// </summary>
    private async void LoadFontFileButton_OnClick(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        try
        {
            if (button is not null)
            {
                button.IsEnabled = false;
            }

            var path = await FontFileInstaller.PickFontFileAsync();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            ShowFontHint("Устанавливаю шрифт…");
            var result = await Task.Run(() => FontFileInstaller.Install(path));
            DebugTrace.Write($"Fonts: installed '{result.FamilyName}' -> {result.InstalledPath}");

            fontListLoaded = false;
            if (FontList.Visibility == Visibility.Visible)
            {
                LoadFontList();
            }

            ViewModel.CustomFontFamily = result.FamilyName;
            var visible = IsFontFamilyAvailable(result.FamilyName);
            ShowFontHint(visible
                ? $"Шрифт «{result.FamilyName}» установлен и применён"
                : $"Шрифт «{result.FamilyName}» установлен. Если он не применился, перезапустите виджет");
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"Fonts: install failed {ex}");
            ShowFontHint($"Не удалось добавить шрифт: {ex.Message}");
        }
        finally
        {
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }

    private static bool IsFontFamilyAvailable(string family)
    {
        try
        {
            return CanvasTextFormat.GetSystemFontFamilies()
                .Any(name => string.Equals(name, family, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private void ShowFontHint(string text)
    {
        FontHint.Text = text;
        FontHint.Visibility = Visibility.Visible;
    }
}
