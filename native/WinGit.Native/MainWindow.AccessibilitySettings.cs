using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Text;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private bool applyingAccessibilitySettings;
    private bool accessibilityControlsInitialized;

    internal void InitializeAccessibilityControls()
    {
        applyingAccessibilitySettings = true;
        try
        {
            UnderlineLinksCheckBox.IsChecked = settings.UnderlineLinks;
            ShowDiffCheckMarksCheckBox.IsChecked = settings.ShowDiffCheckMarks;
            UpdateAccessibilityPreview();
            accessibilityControlsInitialized = true;
        }
        finally
        {
            applyingAccessibilitySettings = false;
        }
    }

    private void UnderlineLinksCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingAccessibilitySettings
            || !accessibilityControlsInitialized
            || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.UnderlineLinks = checkBox.IsChecked == true;
        UpdateAccessibilityPreview();
        _ = SaveSettingsAsync();
    }

    private void ShowDiffCheckMarksCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingAccessibilitySettings
            || !accessibilityControlsInitialized
            || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.ShowDiffCheckMarks = checkBox.IsChecked == true;
        RefreshPartialDiffRowCheckBoxVisibility();
        _ = SaveSettingsAsync();
    }

    private void UpdateAccessibilityPreview()
    {
        AccessibilityExampleLinkText.TextDecorations = settings.UnderlineLinks
            ? TextDecorations.Underline
            : TextDecorations.None;
    }
}
