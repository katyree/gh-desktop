using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using Color = Windows.UI.Color;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private UISettings? titleBarUiSettings;
    private AccessibilitySettings? titleBarAccessibilitySettings;

    private void InitializeThemeSynchronization()
    {
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        titleBarUiSettings = new UISettings();
        titleBarUiSettings.ColorValuesChanged += TitleBarUiSettings_ColorValuesChanged;
        titleBarAccessibilitySettings = new AccessibilitySettings();
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args) =>
        ApplyTitleBarTheme();

    private void TitleBarUiSettings_ColorValuesChanged(UISettings sender, object args) =>
        RootGrid.DispatcherQueue.TryEnqueue(ApplyTitleBarTheme);

    private void DisposeThemeSynchronization()
    {
        RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
        if (titleBarUiSettings is not null)
        {
            titleBarUiSettings.ColorValuesChanged -= TitleBarUiSettings_ColorValuesChanged;
            titleBarUiSettings = null;
        }

        titleBarAccessibilitySettings = null;
    }

    private void ApplyTitleBarTheme()
    {
        if (appWindow?.TitleBar is not { } titleBar)
        {
            return;
        }

        try
        {
            var highContrast = titleBarAccessibilitySettings?.HighContrast == true;
            var background = highContrast
                ? GetSystemColor(UIColorType.Background, GetFallbackBackground())
                : GetElementColor(AppTitleBar.Background, GetFallbackBackground());
            var foreground = highContrast
                ? GetSystemColor(UIColorType.Foreground, GetFallbackForeground())
                : GetElementColor(RepositoryPathText.Foreground, GetFallbackForeground());
            var inactiveForeground = highContrast
                ? foreground
                : GetElementColor(BranchText.Foreground, foreground);
            var hoverBackground = highContrast
                ? background
                : GetResourceColor("SubtleFillColorSecondaryBrush", GetElementColor(GitOperationPanel.Background, background));
            var pressedBackground = highContrast
                ? background
                : GetResourceColor("ControlFillColorSecondaryBrush", hoverBackground);

            titleBar.BackgroundColor = background;
            titleBar.ForegroundColor = foreground;
            titleBar.InactiveBackgroundColor = background;
            titleBar.InactiveForegroundColor = inactiveForeground;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverBackgroundColor = hoverBackground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = pressedBackground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonInactiveBackgroundColor = background;
            titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            if (repositoryChooserFlyout is not null)
            {
                ApplyRepositoryChooserTheme();
            }

            if (selectedChangesReviewDialog is not null)
            {
                selectedChangesReviewDialog.RequestedTheme = RootGrid.ActualTheme;
            }
        }
        catch (Exception)
        {
            // AppWindow title-bar customization is unavailable on some hosts.
        }
    }

    private Color GetFallbackBackground() => RootGrid.ActualTheme == ElementTheme.Dark
        ? Color.FromArgb(255, 32, 32, 32)
        : Color.FromArgb(255, 255, 255, 255);

    private Color GetFallbackForeground() => RootGrid.ActualTheme == ElementTheme.Dark
        ? Color.FromArgb(255, 255, 255, 255)
        : Color.FromArgb(255, 26, 26, 26);

    private static Color GetElementColor(Brush? brush, Color fallback) =>
        brush is SolidColorBrush solidBrush ? solidBrush.Color : fallback;

    private static Color GetResourceColor(string key, Color fallback)
    {
        try
        {
            return Application.Current.Resources[key] switch
            {
                SolidColorBrush brush => brush.Color,
                Color color => color,
                _ => fallback,
            };
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private Color GetSystemColor(UIColorType colorType, Color fallback)
    {
        try
        {
            return titleBarUiSettings?.GetColorValue(colorType) ?? fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
