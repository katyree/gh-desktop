using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core.Codex;
using Windows.ApplicationModel.DataTransfer;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<CodexModelRow> codexModelRows = [];
    private readonly ObservableCollection<CodexReasoningRow> codexReasoningRows = [];
    private readonly SemaphoreSlim codexLifecycleGate = new(1, 1);
    private CodexAppServerClient? codexClient;
    private CancellationTokenSource? codexCancellation;
    private CodexAccountState codexAccount = CodexAccountState.UnavailableState;
    private CodexRateLimitState codexRateLimits = CodexRateLimitState.UnavailableState;
    private string? codexUnavailableReason;
    private string? codexCatalogUnavailableReason;
    private Task? codexSettingsLoadTask;
    private bool codexSettingsLoaded;
    private bool codexBusy;
    private bool codexDisposed;
    private bool loadingCodexSelection;
    private bool refreshCodexAfterLogin;
    private string? activeCodexLoginId;
    private CodexLoginMethod? activeCodexLoginMethod;
    private string? codexDeviceAuthorizationUrl;
    private string? codexDeviceCode;

    private Task EnsureCodexSettingsLoadedAsync()
    {
        if (codexSettingsLoaded)
        {
            return Task.CompletedTask;
        }

        return codexSettingsLoadTask ??= LoadCodexSettingsAsync();
    }

    private async Task LoadCodexSettingsAsync()
    {
        try
        {
            await RefreshCodexStateAsync();
        }
        finally
        {
            codexSettingsLoaded = true;
        }
    }

    private async Task RefreshCodexStateAsync()
    {
        if (codexBusy || codexDisposed)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        codexCancellation = cancellation;
        codexBusy = true;
        codexUnavailableReason = null;
        codexCatalogUnavailableReason = null;
        UpdateCodexControls();
        try
        {
            var client = await EnsureCodexClientAsync(cancellation.Token);
            if (client is null)
            {
                ApplyCodexUnavailable("The Codex app server is unavailable.");
                return;
            }

            var account = await client.ReadAccountAsync(
                refreshToken: false,
                cancellation.Token);
            var rateLimits = await client.ReadRateLimitsAsync(cancellation.Token);
            IReadOnlyList<CodexModel> models;
            try
            {
                models = await client.ReadModelsAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                models = [];
                codexCatalogUnavailableReason = exception.Message;
            }

            if (codexDisposed)
            {
                return;
            }

            ApplyCodexAccount(account);
            ApplyCodexRateLimits(account.Status == CodexAccountStatus.SignedIn
                ? rateLimits
                : CodexRateLimitState.UnavailableState);
            // An empty result can mean that the app server could not provide
            // a catalog. Keep the user's saved selector until a real catalog
            // can validate or replace it.
            ApplyCodexModels(models, preserveSelectionWhenEmpty: models.Count == 0);
            CodexLoginStatusText.Text = account.Status switch
            {
                CodexAccountStatus.SignedIn => "Codex is ready for explicit AI actions.",
                CodexAccountStatus.SignedOut => "Sign-in starts only when you choose a sign-in button.",
                _ => "Codex account information is unavailable.",
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            CodexLoginStatusText.Text = "Codex refresh cancelled.";
        }
        catch (Exception exception)
        {
            ApplyCodexUnavailable(GetCodexFailureMessage(exception));
        }
        finally
        {
            codexBusy = false;
            if (ReferenceEquals(codexCancellation, cancellation))
            {
                codexCancellation = null;
            }

            cancellation.Dispose();
            UpdateCodexControls();
            StartCodexRefreshAfterLoginIfNeeded();
        }
    }

    private void StartCodexRefreshAfterLoginIfNeeded()
    {
        if (!refreshCodexAfterLogin || codexBusy || codexDisposed)
        {
            return;
        }

        refreshCodexAfterLogin = false;
        _ = RefreshCodexStateAsync();
    }

    private async Task<CodexAppServerClient?> EnsureCodexClientAsync(
        CancellationToken cancellationToken)
    {
        await codexLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (codexDisposed)
            {
                return null;
            }

            if (codexClient is null)
            {
                var codexHome = Path.Combine(NativeSettingsStore.ProfileDirectory, "codex");
                codexClient = new CodexAppServerClient(
                    new CodexAppServerClientOptions
                    {
                        ApplicationRoot = AppContext.BaseDirectory,
                        CodexHomePath = codexHome,
                        WorkingDirectory = codexHome,
                        ClientName = "wingit-native",
                        ClientTitle = "WinGit",
                        ClientVersion = "0.1.0",
                    });
                codexClient.NotificationReceived += CodexClient_NotificationReceived;
                codexClient.GenerationProgressReceived += CodexClient_GenerationProgressReceived;
            }

            var client = codexClient;
            if (!client.IsRunning)
            {
                await client.StartAsync(cancellationToken);
            }

            return client;
        }
        catch
        {
            if (codexClient is { IsRunning: false } failedClient)
            {
                failedClient.NotificationReceived -= CodexClient_NotificationReceived;
                failedClient.GenerationProgressReceived -= CodexClient_GenerationProgressReceived;
                codexClient = null;
                await failedClient.DisposeAsync();
            }

            throw;
        }
        finally
        {
            codexLifecycleGate.Release();
        }
    }

    private async void RefreshCodexButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCodexStateAsync();
    }

    private async void BrowserLoginCodexButton_Click(object sender, RoutedEventArgs e)
    {
        await StartCodexLoginAsync(CodexLoginMethod.Browser);
    }

    private async void DeviceLoginCodexButton_Click(object sender, RoutedEventArgs e)
    {
        await StartCodexLoginAsync(CodexLoginMethod.DeviceCode);
    }

    private async Task StartCodexLoginAsync(CodexLoginMethod method)
    {
        if (codexBusy || codexDisposed || activeCodexLoginId is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        codexCancellation = cancellation;
        codexBusy = true;
        ErrorBar.IsOpen = false;
        UpdateCodexControls();
        try
        {
            var client = await EnsureCodexClientAsync(cancellation.Token);
            if (client is null)
            {
                throw new InvalidOperationException("The Codex app server is unavailable.");
            }

            CodexLoginStart login;
            try
            {
                login = await client.StartAccountLoginAsync(method, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            var authorizationUrl = CodexAppServerProtocol.ValidateAuthorizationUrl(
                login.AuthorizationUrl);
            activeCodexLoginId = login.LoginId;
            activeCodexLoginMethod = method;
            codexDeviceAuthorizationUrl = method == CodexLoginMethod.DeviceCode
                ? authorizationUrl
                : null;
            codexDeviceCode = method == CodexLoginMethod.DeviceCode
                ? login.UserCode
                : null;
            CodexDeviceCodeText.Text = codexDeviceCode ?? string.Empty;
            CodexDeviceVerificationUrlText.Text = method == CodexLoginMethod.DeviceCode
                ? authorizationUrl
                : string.Empty;
            CodexDeviceCodePanel.Visibility = method == CodexLoginMethod.DeviceCode
                ? Visibility.Visible
                : Visibility.Collapsed;
            CodexLoginStatusText.Text = method == CodexLoginMethod.DeviceCode
                ? "Device sign-in is waiting for you to finish."
                : "Browser sign-in is waiting for you to finish.";

            if (!TryLaunchCodexAuthorizationUrl(authorizationUrl))
            {
                CodexLoginStatusText.Text = method == CodexLoginMethod.DeviceCode
                    ? "The verification page could not be opened. Use the button below."
                    : "The sign-in page could not be opened. Try the device-code option.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            CodexLoginStatusText.Text = "Codex sign-in cancelled.";
        }
        catch (Exception exception)
        {
            ShowError("Unable to start Codex sign-in", exception);
        }
        finally
        {
            codexBusy = false;
            if (ReferenceEquals(codexCancellation, cancellation))
            {
                codexCancellation = null;
            }

            cancellation.Dispose();
            UpdateCodexControls();
            StartCodexRefreshAfterLoginIfNeeded();
        }
    }

    private void OpenCodexDeviceUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (codexDeviceAuthorizationUrl is null)
        {
            return;
        }

        if (!TryLaunchCodexAuthorizationUrl(codexDeviceAuthorizationUrl))
        {
            ShowError(
                "Unable to open verification page",
                new InvalidOperationException(
                    "Windows could not open the Codex verification page."));
        }
    }

    private void CopyCodexDeviceCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(codexDeviceCode))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(codexDeviceCode);
        Clipboard.SetContent(package);
        CodexLoginStatusText.Text = "Device code copied to the clipboard.";
    }

    private async void CancelCodexLoginButton_Click(object sender, RoutedEventArgs e)
    {
        var loginId = activeCodexLoginId;
        if (string.IsNullOrWhiteSpace(loginId) || codexClient is not { } client)
        {
            return;
        }

        // Clear the active correlation before the request so a late completion
        // from the cancelled attempt cannot update the account state.
        activeCodexLoginId = null;
        activeCodexLoginMethod = null;
        ClearCodexLoginPresentation();
        codexBusy = true;
        UpdateCodexControls();
        try
        {
            await client.CancelAccountLoginAsync(loginId);
            CodexLoginStatusText.Text = "Codex sign-in cancelled.";
        }
        catch (Exception exception)
        {
            ShowError("Unable to cancel Codex sign-in", exception);
        }
        finally
        {
            codexBusy = false;
            UpdateCodexControls();
        }
    }

    private async void SignOutCodexButton_Click(object sender, RoutedEventArgs e)
    {
        if (codexAccount.Status != CodexAccountStatus.SignedIn || codexClient is not { } client)
        {
            return;
        }

        var dialog = CreateDialog(
            "Sign out of Codex?",
            "Sign out",
            new TextBlock
            {
                Text = "This removes the Codex account from the native Codex profile. You can sign in again later.",
                TextWrapping = TextWrapping.Wrap,
            });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        codexCancellation = cancellation;
        codexBusy = true;
        UpdateCodexControls();
        try
        {
            var account = await client.LogoutAccountAsync(cancellation.Token);
            activeCodexLoginId = null;
            activeCodexLoginMethod = null;
            ClearCodexLoginPresentation();
            ApplyCodexAccount(account);
            ApplyCodexRateLimits(CodexRateLimitState.UnavailableState);
            CodexLoginStatusText.Text = "Codex signed out.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            CodexLoginStatusText.Text = "Codex sign-out cancelled.";
        }
        catch (Exception exception)
        {
            ShowError("Unable to sign out of Codex", exception);
        }
        finally
        {
            codexBusy = false;
            if (ReferenceEquals(codexCancellation, cancellation))
            {
                codexCancellation = null;
            }

            cancellation.Dispose();
            UpdateCodexControls();
        }
    }

    private void CodexModelComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (loadingCodexSelection || CodexModelComboBox.SelectedItem is not CodexModelRow row)
        {
            return;
        }

        settings.CodexModelId = row.Model.Id;
        ApplyReasoningOptions(row.Model, settings.CodexReasoningEffort, persistFallback: true);
        if (!diagnosticCaptureMode)
        {
            _ = SaveSettingsAsync();
        }
    }

    private void CodexReasoningComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (loadingCodexSelection || CodexReasoningComboBox.SelectedItem is not CodexReasoningRow row)
        {
            return;
        }

        settings.CodexReasoningEffort = row.ReasoningEffort;
        if (!diagnosticCaptureMode)
        {
            _ = SaveSettingsAsync();
        }
    }

    private void ApplyCodexUnavailable(string reason)
    {
        codexUnavailableReason = reason;
        codexAccount = CodexAccountState.UnavailableState;
        codexRateLimits = CodexRateLimitState.UnavailableState;
        ApplyCodexAccount(codexAccount);
        ApplyCodexRateLimits(codexRateLimits);
        // A missing catalog is a connection failure, not evidence that the
        // user's saved choice was removed. Keep it for the next successful
        // catalog read.
        ApplyCodexModels([], preserveSelectionWhenEmpty: true);
        CodexLoginStatusText.Text = "Codex could not be reached. Git features remain available.";
    }

    private void ApplyCodexAccount(CodexAccountState account)
    {
        codexAccount = account;
        CodexAccountStatusText.Text = account.Status switch
        {
            CodexAccountStatus.SignedIn => "Signed in",
            CodexAccountStatus.SignedOut => "Signed out",
            _ => "Unavailable",
        };
        CodexAccountDetailsText.Text = account.Status switch
        {
            CodexAccountStatus.SignedIn => FormatCodexAccountDetails(account),
            CodexAccountStatus.SignedOut => "Sign in to use Codex account features. No account is connected to this native app.",
            _ => codexUnavailableReason ?? "The native Codex app server is not available.",
        };
    }

    private void ApplyCodexRateLimits(CodexRateLimitState rateLimits)
    {
        codexRateLimits = rateLimits;
        if (rateLimits.Status == CodexRateLimitStatus.Unavailable)
        {
            CodexUsageStatusText.Text = "Usage information is unavailable.";
            CodexPrimaryUsageBar.Value = 0;
            CodexSecondaryUsageBar.Value = 0;
            CodexPrimaryUsageText.Text = "—";
            CodexSecondaryUsageText.Text = "—";
            CodexUsageResetText.Text = string.Empty;
            return;
        }

        var status = rateLimits.Status switch
        {
            CodexRateLimitStatus.NearLimit => "Near limit",
            CodexRateLimitStatus.Exhausted => "Exhausted",
            _ => "Available",
        };
        CodexUsageStatusText.Text = $"{status}. Higher usage can pause AI actions until the limit resets.";
        ApplyCodexUsageWindow(rateLimits.Primary, CodexPrimaryUsageBar, CodexPrimaryUsageText);
        ApplyCodexUsageWindow(rateLimits.Secondary, CodexSecondaryUsageBar, CodexSecondaryUsageText);
        CodexUsageResetText.Text = rateLimits.ResetsAt is { } reset
            ? $"Earliest reset: {FormatCodexDate(reset)}"
            : "No reset time was provided.";
    }

    private void ApplyCodexModels(
        IReadOnlyList<CodexModel> models,
        bool preserveSelectionWhenEmpty = false)
    {
        var previousModelId = settings.CodexModelId;
        var previousReasoningEffort = settings.CodexReasoningEffort;
        var rows = models.Select(model => new CodexModelRow(model)).ToArray();
        loadingCodexSelection = true;
        try
        {
            codexModelRows.Clear();
            foreach (var row in rows)
            {
                codexModelRows.Add(row);
            }

            var selected = rows.FirstOrDefault(row =>
                    string.Equals(row.Model.Id, previousModelId, StringComparison.Ordinal))
                ?? rows.FirstOrDefault(row => row.Model.IsDefault)
                ?? rows.FirstOrDefault();
            if (selected is null)
            {
                if (!preserveSelectionWhenEmpty)
                {
                    settings.CodexModelId = null;
                    settings.CodexReasoningEffort = null;
                }

                CodexModelComboBox.SelectedItem = null;
                CodexReasoningComboBox.SelectedItem = null;
                CodexSelectedModelDescriptionText.Text = string.Empty;
            }
            else
            {
                settings.CodexModelId = string.Equals(
                        selected.Model.Id,
                        previousModelId,
                        StringComparison.Ordinal)
                    ? previousModelId
                    : selected.Model.IsDefault ? null : selected.Model.Id;
                CodexModelComboBox.SelectedItem = selected;
                ApplyReasoningOptions(
                    selected.Model,
                    previousReasoningEffort,
                    persistFallback: true);
            }
        }
        finally
        {
            loadingCodexSelection = false;
        }

        CodexCatalogStatusText.Text = rows.Length == 0
            ? codexCatalogUnavailableReason ?? "No visible Codex models were returned."
            : $"{rows.Length} model{(rows.Length == 1 ? string.Empty : "s")} available.";
        CodexSelectedModelDescriptionText.Text = CodexModelComboBox.SelectedItem is CodexModelRow selectedRow
            ? selectedRow.Description
            : string.Empty;

        if (!diagnosticCaptureMode &&
            (!string.Equals(previousModelId, settings.CodexModelId, StringComparison.Ordinal) ||
             !string.Equals(previousReasoningEffort, settings.CodexReasoningEffort, StringComparison.Ordinal)))
        {
            _ = SaveSettingsAsync();
        }
    }

    private void ApplyReasoningOptions(
        CodexModel model,
        string? preferredEffort,
        bool persistFallback)
    {
        var efforts = model.SupportedReasoningEfforts
            .Select(effort => new CodexReasoningRow(effort))
            .ToArray();
        codexReasoningRows.Clear();
        foreach (var effort in efforts)
        {
            codexReasoningRows.Add(effort);
        }

        var selected = efforts.FirstOrDefault(effort =>
                string.Equals(effort.ReasoningEffort, preferredEffort, StringComparison.Ordinal))
            ?? efforts.FirstOrDefault(effort =>
                string.Equals(effort.ReasoningEffort, model.DefaultReasoningEffort, StringComparison.Ordinal))
            ?? efforts.FirstOrDefault();
        CodexReasoningComboBox.SelectedItem = selected;
        if (persistFallback && selected is not null &&
            !string.Equals(selected.ReasoningEffort, preferredEffort, StringComparison.Ordinal))
        {
            settings.CodexReasoningEffort =
                string.Equals(selected.ReasoningEffort, model.DefaultReasoningEffort, StringComparison.Ordinal)
                    ? null
                    : selected.ReasoningEffort;
        }
        else if (selected is null)
        {
            settings.CodexReasoningEffort = null;
        }
    }

    private void ApplyCodexUsageWindow(
        CodexRateLimitWindow? window,
        ProgressBar bar,
        TextBlock valueText)
    {
        if (window is null)
        {
            bar.Value = 0;
            valueText.Text = "—";
            return;
        }

        var usedPercent = Math.Clamp(window.UsedPercent, 0, 100);
        bar.Value = usedPercent;
        valueText.Text = $"{usedPercent:0.#}%";
    }

    private void UpdateCodexControls()
    {
        if (codexDisposed)
        {
            return;
        }

        var loginActive = activeCodexLoginId is not null;
        var signedIn = codexAccount.Status == CodexAccountStatus.SignedIn;
        CodexLoadingRing.IsActive = codexBusy;
        CodexLoadingRing.Visibility = codexBusy ? Visibility.Visible : Visibility.Collapsed;
        RefreshCodexButton.IsEnabled = !codexBusy;
        BrowserLoginCodexButton.IsEnabled = !codexBusy && !loginActive && !signedIn;
        DeviceLoginCodexButton.IsEnabled = !codexBusy && !loginActive && !signedIn;
        SignOutCodexButton.IsEnabled = !codexBusy && signedIn;
        CancelCodexLoginButton.IsEnabled = !codexBusy && loginActive;
        OpenCodexDeviceUrlButton.IsEnabled = !codexBusy && loginActive && codexDeviceAuthorizationUrl is not null;
        CopyCodexDeviceCodeButton.IsEnabled = !codexBusy && loginActive && codexDeviceCode is not null;
        CodexModelComboBox.IsEnabled = !codexBusy && codexModelRows.Count > 0;
        CodexReasoningComboBox.IsEnabled = !codexBusy && codexReasoningRows.Count > 0;
    }

    private void CodexClient_NotificationReceived(
        object? sender,
        CodexServerNotification notification)
    {
        if (codexDisposed || !ReferenceEquals(sender, codexClient))
        {
            return;
        }

        RootGrid.DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Normal,
            () => HandleCodexNotification(sender, notification));
    }

    private void HandleCodexNotification(
        object? sender,
        CodexServerNotification notification)
    {
        if (codexDisposed || !ReferenceEquals(sender, codexClient))
        {
            return;
        }

        if (notification.RateLimits is not null)
        {
            ApplyCodexRateLimits(notification.RateLimits);
        }

        if (notification.LoginCompletion is not { } completion ||
            string.IsNullOrWhiteSpace(completion.LoginId) ||
            !string.Equals(completion.LoginId, activeCodexLoginId, StringComparison.Ordinal))
        {
            return;
        }

        activeCodexLoginId = null;
        activeCodexLoginMethod = null;
        ClearCodexLoginPresentation();
        CodexLoginStatusText.Text = completion.Success
            ? "Codex sign-in completed. Reading account details…"
            : "Codex sign-in did not complete.";
        if (completion.Success)
        {
            if (codexBusy)
            {
                refreshCodexAfterLogin = true;
            }
            else
            {
                _ = RefreshCodexStateAsync();
            }
        }

        UpdateCodexControls();
    }

    private void ClearCodexLoginPresentation()
    {
        codexDeviceAuthorizationUrl = null;
        codexDeviceCode = null;
        CodexDeviceCodeText.Text = string.Empty;
        CodexDeviceVerificationUrlText.Text = string.Empty;
        CodexDeviceCodePanel.Visibility = Visibility.Collapsed;
    }

    private static bool TryLaunchCodexAuthorizationUrl(string authorizationUrl)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUrl,
                UseShellExecute = true,
            });
            return process is not null;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            FileNotFoundException)
        {
            return false;
        }
    }

    private async Task DisposeCodexClientAsync()
    {
        CodexAppServerClient? client;
        await codexLifecycleGate.WaitAsync();
        try
        {
            if (codexDisposed && codexClient is null)
            {
                return;
            }

            codexDisposed = true;
            activeCodexLoginId = null;
            activeCodexLoginMethod = null;
            ClearCodexLoginPresentation();
            codexCancellation?.Cancel();
            codexCancellation?.Dispose();
            codexCancellation = null;
            client = codexClient;
            codexClient = null;
            if (client is not null)
            {
                client.NotificationReceived -= CodexClient_NotificationReceived;
                client.GenerationProgressReceived -= CodexClient_GenerationProgressReceived;
            }
        }
        finally
        {
            codexLifecycleGate.Release();
        }

        if (client is not null)
        {
            await client.DisposeAsync();
        }
    }

    private static string FormatCodexAccountDetails(CodexAccountState account)
    {
        var type = account.Type switch
        {
            CodexAccountType.ChatGpt => "ChatGPT account",
            CodexAccountType.ApiKey => "API key account",
            _ => "Codex account",
        };
        var details = new List<string> { type };
        if (!string.IsNullOrWhiteSpace(account.Email))
        {
            details.Add(account.Email);
        }

        if (!string.IsNullOrWhiteSpace(account.PlanType))
        {
            details.Add(account.PlanType);
        }

        return string.Join(" · ", details);
    }

    private static string FormatCodexDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("g");

    private static string GetCodexFailureMessage(Exception exception) => exception switch
    {
        FileNotFoundException => "The Codex executable was not found.",
        System.ComponentModel.Win32Exception => "Windows could not start the Codex app server.",
        _ => "The Codex app server is unavailable.",
    };
}
