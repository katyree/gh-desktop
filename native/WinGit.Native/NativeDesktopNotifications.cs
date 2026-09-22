using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.System;

namespace WinGit.Native;

internal sealed class NativeDesktopNotifications : IDisposable
{
    private const string TargetArgumentName = "target";

    private AppNotificationManager? manager;
    private bool enabled;
    private bool registered;
    private bool disposed;

    public bool IsAvailable { get; private set; }

    public string AvailabilityStatus { get; private set; } =
        "Notifications are disabled in WinGit settings.";

    public void SetEnabled(bool value)
    {
        if (disposed)
        {
            return;
        }

        enabled = value;
        if (!value)
        {
            Unregister();
            AvailabilityStatus = "Notifications are disabled in WinGit settings.";
            return;
        }

        EnsureRegistered();
    }

    public bool TryShow(string title, string body, Uri target)
    {
        if (disposed || !enabled || !IsAllowedGitHubTarget(target) || !EnsureRegistered())
        {
            return false;
        }

        RefreshAvailabilityStatus();
        if (!IsAvailable)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title.Trim())
                .AddText(body.Trim())
                .AddArgument(TargetArgumentName, target.AbsoluteUri)
                .BuildNotification();
            manager!.Show(notification);
            return true;
        }
        catch (Exception)
        {
            IsAvailable = false;
            AvailabilityStatus = "Windows notifications are unavailable on this runtime.";
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Unregister();
        GC.SuppressFinalize(this);
    }

    private bool EnsureRegistered()
    {
        if (registered)
        {
            return true;
        }

        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                IsAvailable = false;
                AvailabilityStatus = "Windows notifications are unavailable on this runtime.";
                return false;
            }

            var notificationManager = AppNotificationManager.Default;
            notificationManager.NotificationInvoked += NotificationManager_NotificationInvoked;
            try
            {
                notificationManager.Register();
            }
            catch
            {
                notificationManager.NotificationInvoked -= NotificationManager_NotificationInvoked;
                throw;
            }

            manager = notificationManager;
            registered = true;
            RefreshAvailabilityStatus();
            return true;
        }
        catch (Exception)
        {
            manager = null;
            registered = false;
            IsAvailable = false;
            AvailabilityStatus = "Windows notifications are unavailable on this runtime.";
            return false;
        }
    }

    private void Unregister()
    {
        var notificationManager = manager;
        manager = null;
        var wasRegistered = registered;
        registered = false;
        IsAvailable = false;
        if (notificationManager is null)
        {
            return;
        }

        try
        {
            notificationManager.NotificationInvoked -= NotificationManager_NotificationInvoked;
        }
        catch (Exception)
        {
        }

        if (wasRegistered)
        {
            try
            {
                notificationManager.Unregister();
            }
            catch (Exception)
            {
            }
        }
    }

    private void RefreshAvailabilityStatus()
    {
        var notificationManager = manager;
        if (!registered || notificationManager is null)
        {
            IsAvailable = false;
            return;
        }

        try
        {
            var setting = notificationManager.Setting;
            IsAvailable = setting == AppNotificationSetting.Enabled;
            AvailabilityStatus = setting switch
            {
                AppNotificationSetting.Enabled => "Windows notifications are available.",
                AppNotificationSetting.DisabledByGroupPolicy or
                    AppNotificationSetting.DisabledByManifest or
                    AppNotificationSetting.DisabledForApplication or
                    AppNotificationSetting.DisabledForUser =>
                    "Windows notifications are blocked in Windows Settings.",
                _ => "Windows notifications are unavailable on this runtime.",
            };
        }
        catch (Exception)
        {
            IsAvailable = false;
            AvailabilityStatus = "Windows notifications are unavailable on this runtime.";
        }
    }

    private void NotificationManager_NotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        if (disposed || !enabled
            || !args.Arguments.TryGetValue(TargetArgumentName, out var targetText)
            || !Uri.TryCreate(targetText, UriKind.Absolute, out var target)
            || !IsAllowedGitHubTarget(target))
        {
            return;
        }

        _ = OpenNotificationTargetAsync(target);
    }

    private static async Task OpenNotificationTargetAsync(Uri target)
    {
        try
        {
            await Launcher.LaunchUriAsync(target);
        }
        catch (Exception)
        {
        }
    }

    private static bool IsAllowedGitHubTarget(Uri? target) =>
        target is not null
        && target.IsAbsoluteUri
        && string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(target.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && target.Port == 443
        && string.IsNullOrEmpty(target.UserInfo);
}
