using System.Collections.ObjectModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core.GitHub;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private const string GitHubClientIdEnvironmentVariable = "WINGIT_GITHUB_CLIENT_ID";
    private const string GitHubHostEnvironmentVariable = "WINGIT_GITHUB_HOST";

    private readonly ObservableCollection<GitHubAccountRow> githubAccountRows = [];
    private GitHubAccountStore? githubAccountStore;
    private GitHubDeviceAuthorizationClient? githubDeviceAuthorizationClient;
    private GitHubAuthenticatedProfileClient? githubProfileClient;
    private GitHubDeviceAuthorizationClientOptions? githubDeviceAuthorizationOptions;
    private Uri? githubProfileApiOrigin;
    private CancellationTokenSource? githubOperationCancellation;
    private Task? githubOperationTask;
    private Task? githubAccountsLoadTask;
    private Task? githubSelectionTask;
    private readonly List<Task> githubSelectionTasks = [];
    private CancellationTokenSource? githubSelectionCancellation;
    private long githubOperationGeneration;
    private long githubSelectionGeneration;
    private bool githubAccountsLoaded;
    private bool githubAccountBusy;
    private bool githubSelectionLoading;
    private bool githubDisposed;
    private bool suppressGitHubSelectionLoad;
    private GitHubOperationKind githubOperationKind;
    private GitHubAccountRow? selectedGitHubAccountRow;
    private GitHubStoredAccount? selectedGitHubAccount;
    private GitHubDeviceAuthorization? activeGitHubAuthorization;
    private Uri? githubDeviceVerificationUri;
    private string? githubDeviceUserCode;
    private string? githubConfigurationMessage;

    private Task EnsureGitHubAccountsLoadedAsync()
    {
        if (githubAccountsLoaded || githubDisposed)
        {
            return Task.CompletedTask;
        }

        if (diagnosticCaptureMode)
        {
            githubAccountsLoaded = true;
            ApplyGitHubConfiguration();
            ApplyGitHubAccounts([]);
            return Task.CompletedTask;
        }

        return githubAccountsLoadTask ??= LoadGitHubAccountsAsync();
    }

    private async Task LoadGitHubAccountsAsync()
    {
        try
        {
            ApplyGitHubConfiguration();
            await RefreshGitHubAccountsAsync();
        }
        finally
        {
            githubAccountsLoaded = true;
            UpdateGitHubAccountControls();
        }
    }

    private async void RefreshGitHubAccountsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var task = RefreshGitHubAccountsAsync();
        githubOperationTask = task;
        try
        {
            await task;
        }
        finally
        {
            if (ReferenceEquals(githubOperationTask, task))
            {
                githubOperationTask = null;
            }
        }
    }

    private async Task RefreshGitHubAccountsAsync()
    {
        if (githubAccountBusy || githubDisposed || diagnosticCaptureMode)
        {
            return;
        }

        var store = GetGitHubAccountStore();
        if (store is null)
        {
            return;
        }

        var operation = BeginGitHubOperation(
            GitHubOperationKind.Refresh,
            "Loading GitHub accounts…");
        try
        {
            var summaries = await store.ListAsync(operation.Cancellation.Token);
            if (!IsGitHubOperationCurrent(operation))
            {
                return;
            }

            ApplyGitHubAccounts(summaries);
            GitHubAccountLoginStatusText.Text = summaries.Count == 0
                ? "No GitHub account is connected."
                : "GitHub accounts loaded.";
        }
        catch (OperationCanceledException)
            when (IsGitHubOperationOwnerCurrent(operation))
        {
            GitHubAccountLoginStatusText.Text = "GitHub account refresh cancelled.";
        }
        catch (Exception exception)
        {
            if (IsGitHubOperationOwnerCurrent(operation))
            {
                ShowGitHubError("Unable to load GitHub accounts", exception);
            }
        }
        finally
        {
            EndGitHubOperation(operation);
        }
    }

    private void ApplyGitHubAccounts(
        IReadOnlyList<GitHubAccountSummary> summaries,
        bool retainSelectedAccount = false)
    {
        var selectedKey = selectedGitHubAccountRow?.Key;
        var previousAccount = selectedGitHubAccount;
        GitHubAccountRow? selected;
        var reloadSelectedAccount = false;
        suppressGitHubSelectionLoad = true;
        try
        {
            CancelGitHubSelectionLoad();
            githubAccountRows.Clear();
            foreach (var summary in summaries)
            {
                githubAccountRows.Add(new GitHubAccountRow(summary));
            }

            selected = selectedKey is null
                ? null
                : githubAccountRows.FirstOrDefault(row => row.Key == selectedKey);
            selectedGitHubAccountRow = selected;
            if (selected is null)
            {
                selectedGitHubAccount = null;
                GitHubSelectedAccountText.Text = string.Empty;
            }
            else if (retainSelectedAccount &&
                     previousAccount is not null &&
                     IsSameGitHubAccount(selected.Summary, previousAccount.Summary))
            {
                selectedGitHubAccount = previousAccount;
                GitHubSelectedAccountText.Text =
                    $"Selected @{selected.Summary.Login}.";
            }
            else
            {
                selectedGitHubAccount = null;
                GitHubSelectedAccountText.Text = string.Empty;
                reloadSelectedAccount = true;
            }

            GitHubAccountsList.SelectedItem = selected;
        }
        finally
        {
            suppressGitHubSelectionLoad = false;
        }

        if (reloadSelectedAccount &&
            selectedGitHubAccountRow is not null &&
            githubAccountStore is not null &&
            !githubDisposed &&
            !diagnosticCaptureMode)
        {
            StartGitHubAccountSelectionLoad(selectedGitHubAccountRow);
        }

        GitHubAccountsEmptyText.Visibility = githubAccountRows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        GitHubAccountsStatusText.Text = githubAccountRows.Count switch
        {
            0 => "No accounts connected",
            1 => "1 account connected",
            _ => $"{githubAccountRows.Count} accounts connected",
        };
        UpdateGitHubAccountControls();
    }

    private async void GitHubAccountsList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (suppressGitHubSelectionLoad)
        {
            return;
        }

        CancelGitHubSelectionLoad();
        selectedGitHubAccountRow = e.AddedItems.OfType<GitHubAccountRow>().FirstOrDefault();
        selectedGitHubAccount = null;
        GitHubSelectedAccountText.Text = string.Empty;
        UpdateGitHubAccountControls();

        if (selectedGitHubAccountRow is null ||
            githubAccountStore is null ||
            githubDisposed ||
            diagnosticCaptureMode)
        {
            return;
        }

        var task = StartGitHubAccountSelectionLoad(selectedGitHubAccountRow);
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // A superseded selection is already represented by the newer row.
        }
    }

    private Task? StartGitHubAccountSelectionLoad(GitHubAccountRow row)
    {
        if (githubAccountStore is null || githubDisposed || diagnosticCaptureMode)
        {
            return null;
        }

        var cancellation = new CancellationTokenSource();
        var selectionGeneration = ++githubSelectionGeneration;
        githubSelectionCancellation = cancellation;
        githubSelectionLoading = true;
        GitHubSelectedAccountText.Text = "Loading account…";
        UpdateGitHubAccountControls();

        var task = LoadGitHubAccountAsync(
            row,
            selectionGeneration,
            cancellation);
        githubSelectionTask = task;
        githubSelectionTasks.Add(task);
        _ = ObserveGitHubSelectionTaskAsync(task);
        return task;
    }

    private async Task ObserveGitHubSelectionTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            githubSelectionTasks.Remove(task);
            if (ReferenceEquals(githubSelectionTask, task))
            {
                githubSelectionTask = null;
            }
        }
    }

    private async Task LoadGitHubAccountAsync(
        GitHubAccountRow row,
        long selectionGeneration,
        CancellationTokenSource cancellation)
    {
        try
        {
            var account = await githubAccountStore!.LoadAsync(
                row.Summary.ApiOrigin,
                row.Summary.Id,
                cancellation.Token);
            if (!IsGitHubSelectionCurrent(row, selectionGeneration, cancellation))
            {
                return;
            }

            selectedGitHubAccount = account;
            GitHubSelectedAccountText.Text = account is null
                ? "This account was removed. Refresh the list."
                : $"Selected @{account.Profile.Login}.";
        }
        catch (OperationCanceledException)
        {
            // A superseded account selection must not relabel the newer row.
        }
        catch (Exception exception)
        {
            if (IsGitHubSelectionCurrent(row, selectionGeneration, cancellation))
            {
                selectedGitHubAccount = null;
                ShowGitHubError("Unable to load the GitHub account", exception);
            }
        }
        finally
        {
            if (ReferenceEquals(githubSelectionCancellation, cancellation))
            {
                githubSelectionCancellation = null;
                githubSelectionLoading = false;
                UpdateGitHubAccountControls();
            }

            cancellation.Dispose();
        }
    }

    private async void SignInGitHubButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var task = SignInToGitHubAsync();
        githubOperationTask = task;
        try
        {
            await task;
        }
        finally
        {
            if (ReferenceEquals(githubOperationTask, task))
            {
                githubOperationTask = null;
            }
        }
    }

    private async Task SignInToGitHubAsync()
    {
        if (githubAccountBusy || githubDisposed || diagnosticCaptureMode)
        {
            return;
        }

        ApplyGitHubConfiguration();
        var options = githubDeviceAuthorizationOptions;
        if (options is null)
        {
            GitHubAccountLoginStatusText.Text = githubConfigurationMessage ??
                "GitHub sign-in isn’t configured in this preview.";
            return;
        }

        var store = GetGitHubAccountStore();
        if (store is null)
        {
            return;
        }

        var operation = BeginGitHubOperation(
            GitHubOperationKind.SignIn,
            "Starting GitHub sign-in…");
        try
        {
            ClearGitHubDeviceAuthorizationPresentation();
            var client = GetGitHubDeviceAuthorizationClient(options);
            GitHubAccountLoginStatusText.Text = "Requesting a GitHub sign-in code…";
            var authorization = await client.StartDeviceAuthorizationAsync(
                operation.Cancellation.Token);
            if (!IsGitHubOperationCurrent(operation))
            {
                return;
            }

            if (!IsAllowedGitHubVerificationUri(
                    authorization.VerificationUri,
                    options.Host))
            {
                throw new GitHubDeviceAuthorizationException(
                    GitHubDeviceFlowErrorKind.InvalidResponse);
            }

            activeGitHubAuthorization = authorization;
            githubDeviceUserCode = authorization.UserCode;
            githubDeviceVerificationUri = authorization.VerificationUri;
            GitHubDeviceCodeText.Text = authorization.UserCode;
            GitHubDeviceVerificationUrlText.Text = authorization.VerificationUri.AbsoluteUri;
            GitHubDeviceAuthorizationPanel.Visibility = Visibility.Visible;
            GitHubAccountLoginStatusText.Text =
                "Enter the code on GitHub. This window will finish sign-in when GitHub approves it.";
            UpdateGitHubAccountControls();

            var accessToken = await client.PollAsync(
                authorization,
                operation.Cancellation.Token);
            if (!IsGitHubOperationCurrent(operation))
            {
                return;
            }

            GitHubAccountLoginStatusText.Text = "Verifying your GitHub account…";
            var session = GitHubAccountSession.FromDeviceToken(accessToken);
            var profileClient = GetGitHubProfileClient(options, session.ApiOrigin);
            var profile = await profileClient.ReadUserAsync(
                session,
                operation.Cancellation.Token);
            if (!IsGitHubOperationCurrent(operation) ||
                !AreSameGitHubOrigin(profile.ApiOrigin, session.ApiOrigin))
            {
                return;
            }

            GitHubAccountLoginStatusText.Text = "Saving the verified GitHub account…";
            var stored = await store.SaveAsync(
                profile,
                session,
                operation.Cancellation.Token);
            if (!IsGitHubOperationCurrent(operation))
            {
                return;
            }

            selectedGitHubAccount = stored;
            selectedGitHubAccountRow = new GitHubAccountRow(stored.Summary);
            var summaries = await store.ListAsync(operation.Cancellation.Token);
            if (!IsGitHubOperationCurrent(operation))
            {
                return;
            }

            ApplyGitHubAccounts(summaries, retainSelectedAccount: true);
            var savedRow = githubAccountRows.FirstOrDefault(row =>
                row.Summary.Id == stored.Profile.Id &&
                AreSameGitHubOrigin(row.Summary.ApiOrigin, stored.Profile.ApiOrigin));
            selectedGitHubAccountRow = savedRow;
            suppressGitHubSelectionLoad = true;
            try
            {
                GitHubAccountsList.SelectedItem = savedRow;
            }
            finally
            {
                suppressGitHubSelectionLoad = false;
            }

            GitHubAccountLoginStatusText.Text =
                $"Signed in as @{profile.Login}. The account is ready for explicit GitHub actions.";
        }
        catch (GitHubDeviceAuthorizationCancelledException)
        {
            if (IsGitHubOperationOwnerCurrent(operation))
            {
                GitHubAccountLoginStatusText.Text = "GitHub sign-in cancelled.";
            }
        }
        catch (GitHubAuthenticatedProfileCancelledException)
        {
            if (IsGitHubOperationOwnerCurrent(operation))
            {
                GitHubAccountLoginStatusText.Text = "GitHub sign-in cancelled.";
            }
        }
        catch (OperationCanceledException)
            when (IsGitHubOperationOwnerCurrent(operation))
        {
            GitHubAccountLoginStatusText.Text = "GitHub sign-in cancelled.";
        }
        catch (Exception exception)
        {
            if (IsGitHubOperationOwnerCurrent(operation))
            {
                ShowGitHubError("Unable to sign in to GitHub", exception);
            }
        }
        finally
        {
            if (IsGitHubOperationOwnerCurrent(operation))
            {
                ClearGitHubDeviceAuthorizationPresentation();
            }

            EndGitHubOperation(operation);
        }
    }

    private async void OpenGitHubDeviceUrlButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var uri = githubDeviceVerificationUri;
        var options = githubDeviceAuthorizationOptions;
        var authorization = activeGitHubAuthorization;
        var cancellation = githubOperationCancellation;
        var generation = githubOperationGeneration;
        if (uri is null || options is null || authorization is null ||
            cancellation is null ||
            !IsGitHubSignInPresentationCurrent(
                generation,
                cancellation,
                authorization) ||
            !IsAllowedGitHubVerificationUri(uri, options.Host))
        {
            return;
        }

        try
        {
            var launched = await Launcher.LaunchUriAsync(uri);
            if (!IsGitHubSignInPresentationCurrent(
                    generation,
                    cancellation,
                    authorization))
            {
                return;
            }

            if (launched)
            {
                GitHubAccountLoginStatusText.Text =
                    "The GitHub verification page is open in your browser.";
            }
            else
            {
                GitHubAccountLoginStatusText.Text =
                    "Windows could not open the GitHub verification page.";
            }
        }
        catch (Exception)
        {
            if (IsGitHubSignInPresentationCurrent(
                    generation,
                    cancellation,
                    authorization))
            {
                GitHubAccountLoginStatusText.Text =
                    "Windows could not open the GitHub verification page.";
            }
        }
    }

    private void CopyGitHubDeviceCodeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var authorization = activeGitHubAuthorization;
        var cancellation = githubOperationCancellation;
        var generation = githubOperationGeneration;
        if (string.IsNullOrWhiteSpace(githubDeviceUserCode) ||
            authorization is null ||
            cancellation is null ||
            !IsGitHubSignInPresentationCurrent(
                generation,
                cancellation,
                authorization))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(githubDeviceUserCode);
            Clipboard.SetContent(package);
            if (IsGitHubSignInPresentationCurrent(
                    generation,
                    cancellation,
                    authorization))
            {
                GitHubAccountLoginStatusText.Text = "GitHub device code copied.";
            }
        }
        catch (Exception)
        {
            if (IsGitHubSignInPresentationCurrent(
                    generation,
                    cancellation,
                    authorization))
            {
                GitHubAccountLoginStatusText.Text = "The GitHub device code could not be copied.";
            }
        }
    }

    private void CancelGitHubLoginButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (githubOperationKind != GitHubOperationKind.SignIn ||
            !githubAccountBusy ||
            githubOperationCancellation is not { } cancellation)
        {
            return;
        }

        GitHubAccountLoginStatusText.Text = "Cancelling GitHub sign-in…";
        cancellation.Cancel();
    }

    private async void RemoveGitHubAccountButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var row = selectedGitHubAccountRow;
        if (row is null ||
            githubAccountBusy ||
            githubSelectionLoading ||
            githubAccountStore is null ||
            githubDisposed ||
            diagnosticCaptureMode)
        {
            return;
        }

        var dialog = CreateDialog(
            $"Remove @{row.Summary.Login}?",
            "Remove",
            new TextBlock
            {
                Text = $"This signs @{row.Summary.Login} out of WinGit on this PC. It does not change anything on GitHub.",
                TextWrapping = TextWrapping.Wrap,
            });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            githubDisposed)
        {
            return;
        }

        var task = RemoveGitHubAccountAsync(row);
        githubOperationTask = task;
        try
        {
            await task;
        }
        finally
        {
            if (ReferenceEquals(githubOperationTask, task))
            {
                githubOperationTask = null;
            }
        }
    }

    private async Task RemoveGitHubAccountAsync(GitHubAccountRow row)
    {
        var store = githubAccountStore;
        if (store is null)
        {
            return;
        }

        var operation = BeginGitHubOperation(
            GitHubOperationKind.Remove,
            "Signing out of GitHub…");
        try
        {
            var removed = await store.RemoveAsync(
                row.Summary.ApiOrigin,
                row.Summary.Id,
                operation.Cancellation.Token);
            if (!IsGitHubOperationCurrent(operation))
            {
                return;
            }

            if (removed)
            {
                selectedGitHubAccountRow = null;
                selectedGitHubAccount = null;
                GitHubSelectedAccountText.Text = string.Empty;
                var summaries = await store.ListAsync(operation.Cancellation.Token);
                if (IsGitHubOperationCurrent(operation))
                {
                    ApplyGitHubAccounts(summaries);
                    GitHubAccountLoginStatusText.Text = "GitHub account removed.";
                }
            }
            else
            {
                GitHubAccountLoginStatusText.Text =
                    "That GitHub account is no longer in the native account list.";
            }
        }
        catch (OperationCanceledException)
            when (IsGitHubOperationOwnerCurrent(operation))
        {
            GitHubAccountLoginStatusText.Text = "GitHub account removal cancelled.";
        }
        catch (Exception exception)
        {
            if (IsGitHubOperationOwnerCurrent(operation))
            {
                ShowGitHubError("Unable to remove the GitHub account", exception);
            }
        }
        finally
        {
            EndGitHubOperation(operation);
        }
    }

    private GitHubAccountStore? GetGitHubAccountStore()
    {
        if (githubAccountStore is not null)
        {
            return githubAccountStore;
        }

        try
        {
            githubAccountStore = new GitHubAccountStore(
                Path.Combine(NativeSettingsStore.ProfileDirectory, "github-accounts.json"));
            return githubAccountStore;
        }
        catch (Exception exception)
        {
            ShowGitHubError("GitHub accounts unavailable", exception);
            return null;
        }
    }

    private void ApplyGitHubConfiguration()
    {
        if (TryCreateGitHubOptions(
                out var options,
                out var message))
        {
            githubDeviceAuthorizationOptions = options;
            githubConfigurationMessage = null;
            GitHubAccountsConfigurationText.Text =
                $"Sign-in host: {FormatGitHubHost(options!.Host)}.";
        }
        else
        {
            githubDeviceAuthorizationOptions = null;
            githubConfigurationMessage = message;
            GitHubAccountsConfigurationText.Text = message;
        }

        UpdateGitHubAccountControls();
    }

    private static bool TryCreateGitHubOptions(
        out GitHubDeviceAuthorizationClientOptions? options,
        out string message)
    {
        options = null;
        var clientId = Environment.GetEnvironmentVariable(
            GitHubClientIdEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            message = "GitHub sign-in isn’t configured in this preview.";
            return false;
        }

        var hostText = Environment.GetEnvironmentVariable(
                           GitHubHostEnvironmentVariable)
                       ?? "https://github.com";
        if (!Uri.TryCreate(hostText, UriKind.Absolute, out var host))
        {
            message = "GitHub sign-in isn’t available because its host setting is invalid.";
            return false;
        }

        try
        {
            options = new GitHubDeviceAuthorizationClientOptions(
                clientId,
                host);
            message = string.Empty;
            return true;
        }
        catch (ArgumentException)
        {
            message = "GitHub sign-in isn’t available because its public configuration is invalid.";
            return false;
        }
    }

    private GitHubDeviceAuthorizationClient GetGitHubDeviceAuthorizationClient(
        GitHubDeviceAuthorizationClientOptions options)
    {
        if (githubDeviceAuthorizationClient is not null &&
            githubDeviceAuthorizationOptions is not null &&
            string.Equals(
                githubDeviceAuthorizationOptions.ClientId,
                options.ClientId,
                StringComparison.Ordinal) &&
            AreSameGitHubOrigin(
                githubDeviceAuthorizationOptions.Host,
                options.Host))
        {
            return githubDeviceAuthorizationClient;
        }

        githubDeviceAuthorizationClient?.Dispose();
        githubDeviceAuthorizationClient = new GitHubDeviceAuthorizationClient(options);
        githubDeviceAuthorizationOptions = options;
        return githubDeviceAuthorizationClient;
    }

    private GitHubAuthenticatedProfileClient GetGitHubProfileClient(
        GitHubDeviceAuthorizationClientOptions configuration,
        Uri apiOrigin)
    {
        if (githubProfileClient is not null &&
            githubProfileApiOrigin is not null &&
            AreSameGitHubOrigin(githubProfileApiOrigin, apiOrigin))
        {
            return githubProfileClient;
        }

        githubProfileClient?.Dispose();
        var profileOptions = apiOrigin.Host.Equals(
                "api.github.com",
                StringComparison.OrdinalIgnoreCase)
            ? GitHubAuthenticatedProfileClientOptions.ForGitHubCom()
            : GitHubAuthenticatedProfileClientOptions.ForEnterprise(configuration.Host);
        githubProfileClient = new GitHubAuthenticatedProfileClient(profileOptions);
        githubProfileApiOrigin = apiOrigin;
        return githubProfileClient;
    }

    private GitHubOperation BeginGitHubOperation(
        GitHubOperationKind kind,
        string status)
    {
        githubOperationCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        var operation = new GitHubOperation(
            ++githubOperationGeneration,
            kind,
            cancellation);
        githubOperationCancellation = cancellation;
        githubOperationKind = kind;
        githubAccountBusy = true;
        GitHubAccountLoginStatusText.Text = status;
        UpdateGitHubAccountControls();
        return operation;
    }

    private bool IsGitHubOperationCurrent(GitHubOperation operation) =>
        IsGitHubOperationOwnerCurrent(operation) &&
        !operation.Cancellation.IsCancellationRequested;

    private bool IsGitHubOperationOwnerCurrent(GitHubOperation operation) =>
        !githubDisposed &&
        githubAccountBusy &&
        operation.Generation == githubOperationGeneration &&
        operation.Kind == githubOperationKind &&
        ReferenceEquals(githubOperationCancellation, operation.Cancellation);

    private bool IsGitHubSignInPresentationCurrent(
        long generation,
        CancellationTokenSource cancellation,
        GitHubDeviceAuthorization authorization) =>
        !githubDisposed &&
        githubAccountBusy &&
        githubOperationKind == GitHubOperationKind.SignIn &&
        generation == githubOperationGeneration &&
        ReferenceEquals(githubOperationCancellation, cancellation) &&
        !cancellation.IsCancellationRequested &&
        ReferenceEquals(activeGitHubAuthorization, authorization);

    private void EndGitHubOperation(GitHubOperation operation)
    {
        if (ReferenceEquals(githubOperationCancellation, operation.Cancellation) &&
            operation.Generation == githubOperationGeneration)
        {
            githubOperationCancellation = null;
            githubAccountBusy = false;
            githubOperationKind = GitHubOperationKind.None;
            UpdateGitHubAccountControls();
        }

        operation.Cancellation.Dispose();
    }

    private void CancelGitHubSelectionLoad()
    {
        githubSelectionCancellation?.Cancel();
        githubSelectionGeneration++;
    }

    private bool IsGitHubSelectionCurrent(
        GitHubAccountRow row,
        long selectionGeneration,
        CancellationTokenSource cancellation) =>
        !githubDisposed &&
        !cancellation.IsCancellationRequested &&
        selectionGeneration == githubSelectionGeneration &&
        ReferenceEquals(githubSelectionCancellation, cancellation) &&
        ReferenceEquals(selectedGitHubAccountRow, row);

    private void ClearGitHubDeviceAuthorizationPresentation()
    {
        activeGitHubAuthorization = null;
        githubDeviceVerificationUri = null;
        githubDeviceUserCode = null;
        GitHubDeviceCodeText.Text = string.Empty;
        GitHubDeviceVerificationUrlText.Text = string.Empty;
        GitHubDeviceAuthorizationPanel.Visibility = Visibility.Collapsed;
        UpdateGitHubAccountControls();
    }

    private void UpdateGitHubAccountControls()
    {
        if (GitHubAccountsList is null)
        {
            return;
        }

        var writeBlocked = githubDisposed ||
            diagnosticCaptureMode ||
            mutationInProgress ||
            BusyRing.IsActive;
        var operationActive = githubAccountBusy;
        var canUseAccounts = !writeBlocked && !operationActive;
        var configured = githubDeviceAuthorizationOptions is not null;
        RefreshGitHubAccountsButton.IsEnabled = canUseAccounts;
        SignInGitHubButton.IsEnabled = canUseAccounts && configured;
        RemoveGitHubAccountButton.IsEnabled = canUseAccounts &&
            !githubSelectionLoading &&
            selectedGitHubAccountRow is not null;
        GitHubAccountsList.IsEnabled = !writeBlocked && !operationActive;
        GitHubAccountsLoadingRing.IsActive = operationActive || githubSelectionLoading;
        GitHubAccountsLoadingRing.Visibility =
            GitHubAccountsLoadingRing.IsActive
                ? Visibility.Visible
                : Visibility.Collapsed;
        var signInActive = operationActive &&
            githubOperationKind == GitHubOperationKind.SignIn;
        CancelGitHubLoginButton.Visibility = signInActive
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelGitHubLoginButton.IsEnabled = signInActive;
        OpenGitHubDeviceUrlButton.IsEnabled = signInActive &&
            activeGitHubAuthorization is not null;
        CopyGitHubDeviceCodeButton.IsEnabled = signInActive &&
            !string.IsNullOrWhiteSpace(githubDeviceUserCode);
    }

    private void ShowGitHubError(string title, Exception exception)
    {
        var detail = exception switch
        {
            GitHubDeviceAuthorizationException deviceException => deviceException.Message,
            GitHubDeviceAuthorizationCancelledException => "GitHub sign-in was cancelled.",
            GitHubAuthenticatedProfileException profileException => profileException.Message,
            GitHubAuthenticatedProfileCancelledException => "GitHub sign-in was cancelled.",
            GitHubAccountStoreException storeException => storeException.Message,
            _ => "The GitHub account operation could not be completed.",
        };
        ErrorBar.Title = title;
        ErrorBar.Message = detail;
        ErrorBar.IsOpen = true;
        StatusText.Text = title;
    }

    private static bool IsAllowedGitHubVerificationUri(Uri uri, Uri host) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, host.Host, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            uri.AbsolutePath.TrimEnd('/'),
            "/login/device",
            StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool AreSameGitHubOrigin(Uri left, Uri right) =>
        string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    private static bool IsSameGitHubAccount(
        GitHubAccountSummary left,
        GitHubAccountSummary right) =>
        left.Id == right.Id && AreSameGitHubOrigin(left.ApiOrigin, right.ApiOrigin);

    private static string FormatGitHubHost(Uri host) =>
        host.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? "github.com"
            : host.Host;

    private async Task DisposeGitHubAccountsAsync()
    {
        if (githubDisposed)
        {
            return;
        }

        githubDisposed = true;
        githubOperationCancellation?.Cancel();
        githubSelectionCancellation?.Cancel();
        var cloneLoads = CancelAndSnapshotGitHubCloneLoads();
        var pullRequestLoads = CancelAndSnapshotGitHubPullRequestLoads();

        var tasks = new[]
        {
            githubOperationTask,
            githubAccountsLoadTask,
            githubSelectionTask,
        }
            .Where(task => task is not null)
            .Cast<Task>()
            .Concat(githubSelectionTasks)
            .Concat(cloneLoads)
            .Concat(pullRequestLoads)
            .Distinct()
            .ToArray();
        foreach (var task in tasks)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
            }
        }

        githubProfileClient?.Dispose();
        githubProfileClient = null;
        githubDeviceAuthorizationClient?.Dispose();
        githubDeviceAuthorizationClient = null;
        githubAccountStore?.Dispose();
        githubAccountStore = null;
        githubProfileApiOrigin = null;
        githubOperationCancellation = null;
        githubSelectionCancellation = null;
        githubOperationTask = null;
        githubSelectionTask = null;
        githubSelectionTasks.Clear();
        activeGitHubAuthorization = null;
        githubDeviceVerificationUri = null;
        githubDeviceUserCode = null;
    }

    private sealed class GitHubAccountRow
    {
        public GitHubAccountRow(GitHubAccountSummary summary)
        {
            Summary = summary;
        }

        public GitHubAccountSummary Summary { get; }

        public string Key =>
            $"{Summary.ApiOrigin.AbsoluteUri}|{Summary.Id}";

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Summary.DisplayName)
                ? $"@{Summary.Login}"
                : $"{Summary.DisplayName} (@{Summary.Login})";

        public string Details => string.IsNullOrWhiteSpace(Summary.Email)
            ? FormatGitHubHost(Summary.ApiOrigin)
            : $"{Summary.Email} · {FormatGitHubHost(Summary.ApiOrigin)}";
    }

    private enum GitHubOperationKind
    {
        None,
        Refresh,
        SignIn,
        Remove,
    }

    private sealed record GitHubOperation(
        long Generation,
        GitHubOperationKind Kind,
        CancellationTokenSource Cancellation);
}
