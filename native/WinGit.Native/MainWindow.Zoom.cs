using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using UiDispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private const double ZoomComparisonTolerance = 0.0001d;

    private UiDispatcherQueueTimer? zoomInfoTimer;
    private bool zoomCommandsInitialized;
    private bool zoomControlsInitialized;
    private double currentZoomFactor = NativeZoomLevels.Default;
    private double? pendingZoomFactor;

    private void InitializeZoomCommands()
    {
        if (zoomCommandsInitialized)
        {
            return;
        }

        zoomCommandsInitialized = true;
        ResetZoomMenuItem.KeyboardAcceleratorTextOverride = "Ctrl+0";
        ZoomInMenuItem.KeyboardAcceleratorTextOverride = "Ctrl+=";
        ZoomOutMenuItem.KeyboardAcceleratorTextOverride = "Ctrl+-";
        ResetZoomMenuItem.KeyboardAccelerators.Add(
            new KeyboardAccelerator
            {
                Key = VirtualKey.Number0,
                Modifiers = VirtualKeyModifiers.Control,
            });
        ZoomInMenuItem.KeyboardAccelerators.Add(
            new KeyboardAccelerator
            {
                Key = (VirtualKey)0xBB,
                Modifiers = VirtualKeyModifiers.Control,
            });
        ZoomInMenuItem.KeyboardAccelerators.Add(
            new KeyboardAccelerator
            {
                Key = (VirtualKey)0xBB,
                Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            });
        ZoomOutMenuItem.KeyboardAccelerators.Add(
            new KeyboardAccelerator
            {
                Key = (VirtualKey)0xBD,
                Modifiers = VirtualKeyModifiers.Control,
            });
    }

    private void InitializeZoomControls()
    {
        if (zoomControlsInitialized)
        {
            return;
        }

        zoomControlsInitialized = true;
        zoomInfoTimer = RootGrid.DispatcherQueue.CreateTimer();
        zoomInfoTimer.Interval = TimeSpan.FromMilliseconds(950);
        zoomInfoTimer.Tick += ZoomInfoTimer_Tick;
        WorkspaceZoomViewer.ViewChanged += WorkspaceZoomViewer_ViewChanged;
        currentZoomFactor = NativeZoomLevels.Normalize(settings.ZoomFactor);
        ApplyZoomFactor(currentZoomFactor, showIndicator: false, persist: false);
        ApplyWorkspaceZoomViewportSize();
    }

    private void DisposeZoomControls()
    {
        if (zoomInfoTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= ZoomInfoTimer_Tick;
            zoomInfoTimer = null;
        }

        if (zoomControlsInitialized)
        {
            WorkspaceZoomViewer.ViewChanged -= WorkspaceZoomViewer_ViewChanged;
            zoomControlsInitialized = false;
        }

        pendingZoomFactor = null;
        ZoomInfoBorder.Visibility = Visibility.Collapsed;
    }

    private void ZoomMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag })
        {
            return;
        }

        var nextFactor = tag switch
        {
            "Reset" => NativeZoomLevels.Reset(),
            "In" => NativeZoomLevels.NextIn(currentZoomFactor),
            "Out" => NativeZoomLevels.NextOut(currentZoomFactor),
            _ => currentZoomFactor,
        };
        if (AreZoomFactorsEqual(nextFactor, currentZoomFactor))
        {
            return;
        }

        ApplyZoomFactor(nextFactor, showIndicator: !diagnosticCaptureMode, persist: true);
    }

    private void ApplyZoomFactor(double factor, bool showIndicator, bool persist)
    {
        var normalized = NativeZoomLevels.Normalize(factor);
        currentZoomFactor = normalized;
        pendingZoomFactor = normalized;
        if (persist && !AreZoomFactorsEqual(settings.ZoomFactor, normalized))
        {
            settings.ZoomFactor = normalized;
            _ = SaveSettingsAsync();
        }

        if (WorkspaceZoomViewer.IsLoaded)
        {
            WorkspaceZoomViewer.ChangeView(
                horizontalOffset: null,
                verticalOffset: null,
                zoomFactor: (float)normalized,
                disableAnimation: true);
        }

        ApplyWorkspaceZoomViewportSize();
        if (showIndicator)
        {
            ShowZoomIndicator();
        }
    }

    private void WorkspaceZoomViewer_ViewChanged(
        object? sender,
        ScrollViewerViewChangedEventArgs args)
    {
        if (!zoomControlsInitialized || args.IsIntermediate)
        {
            return;
        }

        var actual = WorkspaceZoomViewer.ZoomFactor;
        var normalized = NativeZoomLevels.Normalize(actual);
        if (!AreZoomFactorsEqual(actual, normalized))
        {
            ApplyZoomFactor(normalized, showIndicator: !diagnosticCaptureMode, persist: true);
            return;
        }

        var wasPending = pendingZoomFactor is { } pending
            && AreZoomFactorsEqual(pending, normalized);
        pendingZoomFactor = null;
        currentZoomFactor = normalized;
        ApplyWorkspaceZoomViewportSize();
        if (wasPending || AreZoomFactorsEqual(settings.ZoomFactor, normalized))
        {
            return;
        }

        settings.ZoomFactor = normalized;
        _ = SaveSettingsAsync();
        if (!diagnosticCaptureMode)
        {
            ShowZoomIndicator();
        }
    }

    private void ShowZoomIndicator()
    {
        if (diagnosticCaptureMode)
        {
            return;
        }

        ZoomPercentText.Text = $"{currentZoomFactor * 100:0}%";
        ZoomInfoBorder.Visibility = Visibility.Visible;
        zoomInfoTimer?.Stop();
        zoomInfoTimer?.Start();
    }

    private void ZoomInfoTimer_Tick(UiDispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        ZoomInfoBorder.Visibility = Visibility.Collapsed;
    }

    private void WorkspaceZoomViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (zoomControlsInitialized && pendingZoomFactor is { } pending)
        {
            ApplyZoomFactor(pending, showIndicator: false, persist: false);
        }

        ApplyWorkspaceZoomViewportSize();
    }

    private void WorkspaceZoomViewer_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyWorkspaceZoomViewportSize();

    private void ApplyWorkspaceZoomViewportSize()
    {
        if (WorkspaceZoomViewer is null || MainNavigation is null)
        {
            return;
        }

        var width = WorkspaceZoomViewer.ViewportWidth > 0
            ? WorkspaceZoomViewer.ViewportWidth
            : WorkspaceZoomViewer.ActualWidth;
        var height = WorkspaceZoomViewer.ViewportHeight > 0
            ? WorkspaceZoomViewer.ViewportHeight
            : WorkspaceZoomViewer.ActualHeight;
        if (!double.IsFinite(width)
            || !double.IsFinite(height)
            || width <= 0
            || height <= 0)
        {
            return;
        }

        var zoomFactor = pendingZoomFactor
            ?? (WorkspaceZoomViewer.ZoomFactor > 0
                ? WorkspaceZoomViewer.ZoomFactor
                : currentZoomFactor);
        if (!double.IsFinite(zoomFactor) || zoomFactor <= 0)
        {
            zoomFactor = NativeZoomLevels.Default;
        }

        var logicalWidth = width / zoomFactor;
        var logicalHeight = height / zoomFactor;
        if (!double.IsFinite(logicalWidth)
            || !double.IsFinite(logicalHeight)
            || logicalWidth <= 0
            || logicalHeight <= 0)
        {
            return;
        }

        if (!double.IsFinite(MainNavigation.Width)
            || Math.Abs(MainNavigation.Width - logicalWidth) > 0.5)
        {
            MainNavigation.Width = logicalWidth;
        }

        if (!double.IsFinite(MainNavigation.Height)
            || Math.Abs(MainNavigation.Height - logicalHeight) > 0.5)
        {
            MainNavigation.Height = logicalHeight;
        }
    }

    private static bool AreZoomFactorsEqual(double left, double right) =>
        Math.Abs(left - right) <= ZoomComparisonTolerance;
}
