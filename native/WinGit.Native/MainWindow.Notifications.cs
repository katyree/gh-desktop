using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private readonly NativeDesktopNotifications desktopNotifications = new();
    private bool applyingNotificationSettings;
    private bool notificationDeliverySuppressed = true;
    private bool notificationMonitorEventsAttached;
    private string? notificationMonitorStatus;

    internal event EventHandler? NotificationsEnabledChanged;

    internal bool AreNotificationsEnabled => settings.NotificationsEnabled;

    internal string NotificationAvailabilityStatus => desktopNotifications.AvailabilityStatus;

    internal void InitializeNotificationControls()
    {
        if (!notificationMonitorEventsAttached)
        {
            NotificationsEnabledChanged += NotificationsEnabledChangedForMonitor;
            notificationMonitorEventsAttached = true;
        }

        notificationDeliverySuppressed = true;
        desktopNotifications.SetEnabled(false);

        applyingNotificationSettings = true;
        try
        {
            NotificationsEnabledCheckBox.IsChecked = settings.NotificationsEnabled;
        }
        finally
        {
            applyingNotificationSettings = false;
        }

        RefreshNotificationStatus();
    }

    internal void SetNotificationDeliverySuppressed(bool suppressed)
    {
        notificationDeliverySuppressed = suppressed;
        desktopNotifications.SetEnabled(!suppressed && settings.NotificationsEnabled);
        RefreshNotificationStatus();
        if (suppressed)
        {
            SetNotificationMonitorStatus(GetGitHubNotificationWaitingStatus());
            _ = StopGitHubNotificationMonitorAsync();
        }
    }

    internal bool TryShowNotification(string title, string body, Uri target)
    {
        if (notificationDeliverySuppressed || !settings.NotificationsEnabled)
        {
            return false;
        }

        return desktopNotifications.TryShow(title, body, target);
    }

    internal void DisposeNotificationDelivery() => desktopNotifications.Dispose();

    internal void RefreshNotificationStatus()
    {
        var statuses = new[]
        {
            desktopNotifications.AvailabilityStatus,
            notificationMonitorStatus,
        }
            .Where(status => !string.IsNullOrWhiteSpace(status))
            .ToArray();
        NotificationStatusText.Text = string.Join(" ", statuses);
    }

    internal void SetNotificationMonitorStatus(string? status)
    {
        notificationMonitorStatus = status;
        if (RootGrid.DispatcherQueue.HasThreadAccess)
        {
            RefreshNotificationStatus();
            return;
        }

        RootGrid.DispatcherQueue.TryEnqueue(
            () =>
            {
                notificationMonitorStatus = status;
                RefreshNotificationStatus();
            });
    }

    private void NotificationsEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingNotificationSettings || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.NotificationsEnabled = checkBox.IsChecked == true;
        desktopNotifications.SetEnabled(!notificationDeliverySuppressed && settings.NotificationsEnabled);
        RefreshNotificationStatus();
        _ = SaveSettingsAsync();
        NotificationsEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OpenNotificationSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri("ms-settings:notifications")))
            {
                NotificationStatusText.Text =
                    "Windows notification settings could not be opened.";
            }
        }
        catch (Exception)
        {
            NotificationStatusText.Text = "Windows notification settings could not be opened.";
        }
    }
}
