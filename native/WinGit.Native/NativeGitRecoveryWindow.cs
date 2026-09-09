using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Windows.Graphics;

namespace WinGit.Native;

internal sealed class NativeGitRecoveryWindow : Window
{
    private bool configured;

    public NativeGitRecoveryWindow(string gitExecutablePath, string? failureReason)
    {
        var closeButton = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(20, 8, 20, 8),
        };
        closeButton.Click += (_, _) => Close();

        var recoveryContent = new StackPanel
        {
            Spacing = 16,
            Padding = new Thickness(32),
            Children =
            {
                new TextBlock
                {
                    Text = "WinGit could not start because its bundled Git runtime is missing or cannot run.",
                    FontSize = 22,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = $"Bundled Git path:\n{gitExecutablePath}",
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
                new TextBlock
                {
                    Text = $"{failureReason}\n\nRestore or reinstall the complete app bundle, then restart WinGit.",
                    TextWrapping = TextWrapping.Wrap,
                },
                closeButton,
            },
        };
        Content = new ScrollViewer
        {
            Content = recoveryContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Activated += RecoveryWindow_Activated;
    }

    private void RecoveryWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (configured)
        {
            return;
        }

        configured = true;
        Activated -= RecoveryWindow_Activated;
        try
        {
            var windowHandle = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Title = "WinGit startup recovery";
            appWindow.Resize(new SizeInt32(720, 420));
        }
        catch (Exception)
        {
            // The recovery content remains usable when AppWindow metadata is unavailable.
        }
    }
}
