using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private NativeUpdateClient? nativeUpdateClient;
    private DispatcherTimer? nativeUpdateTimer;
    private readonly CancellationTokenSource nativeUpdateCancellation = new();
    private static readonly HttpClient NativeUpdateHttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    private void InitializeNativeUpdates()
    {
        var feed = Environment.GetEnvironmentVariable("WINGIT_NATIVE_UPDATE_FEED_URL");
        var channel = Environment.GetEnvironmentVariable("WINGIT_NATIVE_UPDATE_CHANNEL") ?? "development";
        var signer = Environment.GetEnvironmentVariable("WINGIT_NATIVE_UPDATE_SIGNER_SUBJECT");
        if (string.IsNullOrWhiteSpace(feed) || string.IsNullOrWhiteSpace(signer)
            || channel is not ("production" or "beta" or "test")
            || !Uri.TryCreate(feed, UriKind.Absolute, out var feedUrl))
        {
            UpdateStatusText.Text = "Updates are disabled until a signed WinGit update channel is configured.";
            CheckForUpdatesButton.IsEnabled = false;
            return;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        try
        {
            nativeUpdateClient = new NativeUpdateClient(
                NativeUpdateHttpClient,
                new AuthenticodeUpdateVerifier(),
                new NativeUpdateOptions(
                    feedUrl,
                    channel,
                    signer,
                    version,
                    Path.Combine(NativeSettingsStore.ProfileDirectory, "updates"),
                    Path.Combine(NativeSettingsStore.ProfileDirectory, ".update-id")));
        }
        catch (ArgumentException)
        {
            UpdateStatusText.Text = "The configured update channel is invalid.";
            CheckForUpdatesButton.IsEnabled = false;
            return;
        }

        nativeUpdateClient.StateChanged += OnNativeUpdateStateChanged;
        UpdateStatusText.Text = $"{channel} updates have not been checked.";
        if (!diagnosticCaptureMode && channel is ("production" or "beta"))
        {
            nativeUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
            nativeUpdateTimer.Tick += NativeUpdateTimer_Tick;
            nativeUpdateTimer.Start();
            _ = nativeUpdateClient.CheckAsync(false, nativeUpdateCancellation.Token);
        }
    }

    private async void NativeUpdateTimer_Tick(object? sender, object e)
    {
        if (nativeUpdateClient is not null)
        {
            await nativeUpdateClient.CheckAsync(false, nativeUpdateCancellation.Token);
        }
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (nativeUpdateClient is not null)
        {
            await nativeUpdateClient.CheckAsync(true, nativeUpdateCancellation.Token);
        }
    }

    private void OnNativeUpdateStateChanged(NativeUpdateState state)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnNativeUpdateStateChanged(state));
            return;
        }

        CheckForUpdatesButton.IsEnabled = state.Status is NativeUpdateStatus.NotChecked
            or NativeUpdateStatus.NotAvailable or NativeUpdateStatus.Failed;
        UpdateProgressBar.Visibility = state.Status is NativeUpdateStatus.Checking or NativeUpdateStatus.Downloading
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.IsIndeterminate = state.Status is NativeUpdateStatus.Checking
            || state.TotalBytes is not > 0;
        if (state.TotalBytes is > 0)
        {
            UpdateProgressBar.Value = Math.Clamp(100.0 * state.DownloadedBytes / state.TotalBytes.Value, 0, 100);
        }

        UpdateStatusText.Text = state.Status is NativeUpdateStatus.Downloading
            ? $"Downloading update {state.Version}: {state.DownloadedBytes / 1024:N0} KB"
                + (state.TotalBytes is > 0 ? $" of {state.TotalBytes.Value / 1024:N0} KB" : string.Empty)
            : state.Message;
    }

    private void StopNativeUpdates()
    {
        nativeUpdateTimer?.Stop();
        nativeUpdateCancellation.Cancel();
    }
}
