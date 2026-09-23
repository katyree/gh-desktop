using System.Reflection;
using System.Diagnostics;
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
        InstallUpdateButton.Visibility = state.Status == NativeUpdateStatus.Downloaded
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.Visibility = state.Status is NativeUpdateStatus.Checking or NativeUpdateStatus.Downloading
            or NativeUpdateStatus.Installing
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

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (nativeUpdateClient?.State.Status != NativeUpdateStatus.Downloaded)
        {
            return;
        }

        var confirmation = new ContentDialog
        {
            Title = "Install downloaded update?",
            Content = "WinGit will verify the package, close, replace its app files, and restart. Keep WinGit open while preparation runs.",
            PrimaryButtonText = "Quit and install",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var plan = await nativeUpdateClient.PrepareInstallationAsync(AppContext.BaseDirectory, nativeUpdateCancellation.Token);
        if (plan is null)
        {
            return;
        }

        try
        {
            var helperSource = Path.Combine(AppContext.BaseDirectory, "apply-native-update.ps1");
            var helperCopy = Path.Combine(NativeSettingsStore.ProfileDirectory, "updates",
                $"apply-native-update-{Guid.NewGuid():N}.ps1");
            File.Copy(helperSource, helperCopy);
            var resultPath = Path.Combine(NativeSettingsStore.ProfileDirectory, "update-install-result.txt");
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "-NoProfile", "-NonInteractive", "-File", helperCopy,
                "-ArchivePath", plan.ArchivePath,
                "-ArchiveSha256", plan.ArchiveSha256,
                "-StagedDirectory", plan.StagedDirectory,
                "-InstallationDirectory", plan.InstallationDirectory,
                "-ExpectedSignerSubject", plan.ExpectedSignerSubject,
                "-ParentProcessId", Environment.ProcessId.ToString(),
                "-ResultPath", resultPath
            })
            {
                start.ArgumentList.Add(argument);
            }
            if (Process.Start(start) is null)
            {
                throw new InvalidOperationException("The update installer did not start.");
            }

            nativeUpdateTimer?.Stop();

        }
        catch (Exception)
        {
            nativeUpdateClient.ReportInstallHandoffFailure(plan);
            return;
        }

        UpdateStatusText.Text = "Update prepared. Closing WinGit to install and restart...";
        Close();
    }

    private async Task ShowPreviousInstallResultAsync()
    {
        if (diagnosticCaptureMode)
        {
            return;
        }
        var resultPath = Path.Combine(NativeSettingsStore.ProfileDirectory, "update-install-result.txt");
        if (!File.Exists(resultPath))
        {
            return;
        }

        var message = File.ReadAllText(resultPath).Trim();
        UpdateStatusText.Text = message;
        File.Delete(resultPath);
        var dialog = new ContentDialog
        {
            Title = message.StartsWith("Update files installed", StringComparison.Ordinal)
                ? "WinGit update installed" : "WinGit update failed",
            Content = message,
            CloseButtonText = "Continue",
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme
        };
        await dialog.ShowAsync();
    }

    private void StopNativeUpdates()
    {
        nativeUpdateTimer?.Stop();
        nativeUpdateCancellation.Cancel();
    }
}
