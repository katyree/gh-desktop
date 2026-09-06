using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private CancellationTokenSource? gitConfigCancellation;
    private GitConfigValues? globalGitConfigValues;
    private GitConfigValues? localGitConfigValues;
    private string? gitConfigLoadedRoot;
    private long gitConfigGeneration;
    private bool gitConfigControlsInitialized;
    private bool loadingGitConfigScope;
    private bool gitConfigLoading;

    private void InitializeGitConfigControls()
    {
        if (gitConfigControlsInitialized)
        {
            return;
        }

        gitConfigControlsInitialized = true;
        loadingGitConfigScope = true;
        GitConfigScopeComboBox.SelectedIndex = 1;
        loadingGitConfigScope = false;
        UpdateGitConfigControls();
    }

    private async Task EnsureGitConfigLoadedAsync()
    {
        if (!gitConfigControlsInitialized)
        {
            return;
        }

        gitConfigCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        gitConfigCancellation = cancellation;
        var generation = ++gitConfigGeneration;
        var root = repositoryRoot;
        gitConfigLoading = true;
        GitConfigScopeStatusText.Text = "Reading Git configuration…";
        UpdateGitConfigControls();

        try
        {
            var globalTask = repositoryService.GetGitConfigValuesAsync(
                root: null,
                scope: GitConfigScope.Global,
                cancellationToken: cancellation.Token);
            Task<GitConfigValues?> localTask = ReadLocalGitConfigAsync(root, cancellation.Token);
            await Task.WhenAll(new Task[] { globalTask, localTask });

            if (!IsGitConfigLoadCurrent(generation, cancellation, root))
            {
                return;
            }

            globalGitConfigValues = await globalTask;
            localGitConfigValues = await localTask;
            gitConfigLoadedRoot = root;
            ApplySelectedGitConfigValues();
            GitConfigScopeStatusText.Text = BuildGitConfigScopeStatus();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer scope or repository load owns the controls now.
        }
        catch (Exception exception)
        {
            if (IsGitConfigLoadCurrent(generation, cancellation, root))
            {
                globalGitConfigValues = null;
                localGitConfigValues = null;
                gitConfigLoadedRoot = null;
                ApplySelectedGitConfigValues();
                GitConfigScopeStatusText.Text = "Git configuration could not be read. Refresh to try again.";
                ShowError("Unable to read Git configuration", exception);
            }
        }
        finally
        {
            if (IsGitConfigTaskCurrent(generation, cancellation))
            {
                gitConfigLoading = false;
                UpdateGitConfigControls();
                gitConfigCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void GitConfigScopeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (loadingGitConfigScope)
        {
            return;
        }

        ApplySelectedGitConfigValues();
        UpdateGitConfigControls();
        _ = EnsureGitConfigLoadedAsync();
    }

    private void RefreshGitConfigButton_Click(object sender, RoutedEventArgs args)
    {
        if (mutationInProgress || BusyRing.IsActive || diagnosticCaptureMode)
        {
            return;
        }

        _ = EnsureGitConfigLoadedAsync();
    }

    private async void SaveGitConfigButton_Click(object sender, RoutedEventArgs args)
    {
        if (mutationInProgress || BusyRing.IsActive || diagnosticCaptureMode || gitConfigLoading)
        {
            return;
        }

        var scope = SelectedGitConfigScope();
        var root = repositoryRoot;
        if (scope == GitConfigScope.Local && root is null)
        {
            ShowError(
                "Local Git configuration unavailable",
                new InvalidOperationException("Open a repository before editing repository-local Git configuration."));
            return;
        }

        if (!string.Equals(gitConfigLoadedRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            GitConfigScopeStatusText.Text = "Git configuration changed context. Refreshing before save…";
            _ = EnsureGitConfigLoadedAsync();
            return;
        }

        var userName = GitConfigUserNameBox.Text.Trim();
        var userEmail = GitConfigUserEmailBox.Text.Trim();
        var defaultBranch = GitConfigDefaultBranchBox.Text.Trim();
        var original = scope == GitConfigScope.Local
            ? localGitConfigValues
            : globalGitConfigValues;
        var userNameChanged = !string.Equals(userName, original?.UserName ?? string.Empty, StringComparison.Ordinal);
        var userEmailChanged = !string.Equals(userEmail, original?.UserEmail ?? string.Empty, StringComparison.Ordinal);
        var defaultBranchChanged = !string.Equals(defaultBranch, original?.DefaultBranch ?? string.Empty, StringComparison.Ordinal);
        try
        {
            if (userNameChanged)
            {
                ValidateGitConfigText(userName, "user name");
            }

            if (userEmailChanged)
            {
                ValidateGitConfigText(userEmail, "user email");
            }

            if (defaultBranchChanged
                && (defaultBranch.Length == 0
                    || defaultBranch.StartsWith("-", StringComparison.Ordinal)
                    || defaultBranch.Contains('\0')
                    || defaultBranch.Contains('\r')
                    || defaultBranch.Contains('\n')))
            {
                throw new ArgumentException(
                    "Enter a default branch name without a leading dash, line breaks, or NUL characters.",
                    nameof(defaultBranch));
            }
        }
        catch (ArgumentException exception)
        {
            ShowError("Git configuration is invalid", exception);
            return;
        }

        if (!userNameChanged && !userEmailChanged && !defaultBranchChanged)
        {
            StatusText.Text = "No Git configuration changes to save.";
            return;
        }

        mutationInProgress = true;
        ErrorBar.IsOpen = false;
        var operation = BeginOperation("Saving Git configuration…");
        try
        {
            if (defaultBranchChanged)
            {
                await repositoryService.SetGitConfigValueAsync(
                    root,
                    scope,
                    GitConfigSetting.DefaultBranch,
                    defaultBranch,
                    operation.Token);
            }

            if (userNameChanged)
            {
                await repositoryService.SetGitConfigValueAsync(
                    root,
                    scope,
                    GitConfigSetting.UserName,
                    userName,
                    operation.Token);
            }

            if (userEmailChanged)
            {
                await repositoryService.SetGitConfigValueAsync(
                    root,
                    scope,
                    GitConfigSetting.UserEmail,
                    userEmail,
                    operation.Token);
            }

            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StatusText.Text = $"Saved {FormatGitConfigScope(scope)} Git configuration.";
            await EnsureGitConfigLoadedAsync();
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            StatusText.Text = "Git configuration save cancelled; refresh to confirm the current values.";
        }
        catch (Exception exception)
        {
            ShowError("Unable to save Git configuration", exception);
        }
        finally
        {
            mutationInProgress = false;
            EndOperation(operation.Generation);
        }
    }

    private void ApplySelectedGitConfigValues()
    {
        var scope = SelectedGitConfigScope();
        var selected = scope == GitConfigScope.Local
            ? localGitConfigValues
            : globalGitConfigValues;
        GitConfigUserNameBox.Text = selected?.UserName ?? string.Empty;
        GitConfigUserEmailBox.Text = selected?.UserEmail ?? string.Empty;
        GitConfigDefaultBranchBox.Text = selected?.DefaultBranch ?? string.Empty;
        GitConfigUserNameScopeText.Text = DescribeGitConfigValue(
            "user.name",
            selected?.UserName,
            globalGitConfigValues?.UserName,
            scope);
        GitConfigUserEmailScopeText.Text = DescribeGitConfigValue(
            "user.email",
            selected?.UserEmail,
            globalGitConfigValues?.UserEmail,
            scope);
        GitConfigDefaultBranchScopeText.Text = DescribeGitConfigValue(
            "init.defaultBranch",
            selected?.DefaultBranch,
            globalGitConfigValues?.DefaultBranch,
            scope);
    }

    private void UpdateGitConfigControls()
    {
        if (!gitConfigControlsInitialized)
        {
            return;
        }

        var scope = SelectedGitConfigScope();
        var canEdit = scope == GitConfigScope.Global || repositoryRoot is not null;
        var canInteract = !gitConfigLoading
            && !mutationInProgress
            && !BusyRing.IsActive
            && !diagnosticCaptureMode;
        GitConfigScopeComboBox.IsEnabled = canInteract;
        GitConfigUserNameBox.IsEnabled = canEdit && canInteract;
        GitConfigUserEmailBox.IsEnabled = canEdit && canInteract;
        GitConfigDefaultBranchBox.IsEnabled = canEdit && canInteract;
        SaveGitConfigButton.IsEnabled = canEdit && canInteract;
        RefreshGitConfigButton.IsEnabled = canInteract;
        GitConfigScopeStatusText.Text = gitConfigLoading
            ? "Reading Git configuration…"
            : BuildGitConfigScopeStatus();
    }

    private string BuildGitConfigScopeStatus()
    {
        var scope = SelectedGitConfigScope();
        if (scope == GitConfigScope.Local && repositoryRoot is null)
        {
            return "Open a repository to read or edit repository-local Git configuration.";
        }

        return scope == GitConfigScope.Local
            ? "Repository-local values override the global Git configuration."
            : "Global values apply when a repository has no local override.";
    }

    private async Task<string> GetDefaultBranchForCreationAsync()
    {
        try
        {
            var values = await repositoryService.GetGitConfigValuesAsync(
                root: null,
                scope: GitConfigScope.Global,
                cancellationToken: CancellationToken.None);
            globalGitConfigValues = values;
            return string.IsNullOrWhiteSpace(values.DefaultBranch)
                ? "main"
                : values.DefaultBranch.Trim();
        }
        catch (Exception exception)
        {
            ShowError("Unable to read global default branch", exception);
            return "main";
        }
    }

    private async Task<GitConfigValues?> ReadLocalGitConfigAsync(
        string? root,
        CancellationToken cancellationToken)
    {
        if (root is null)
        {
            return null;
        }

        return await repositoryService.GetGitConfigValuesAsync(
            root,
            scope: GitConfigScope.Local,
            cancellationToken);
    }

    private bool IsGitConfigLoadCurrent(
        long generation,
        CancellationTokenSource cancellation,
        string? root) =>
        generation == gitConfigGeneration
        && ReferenceEquals(gitConfigCancellation, cancellation)
        && !cancellation.IsCancellationRequested
        && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase);

    private bool IsGitConfigTaskCurrent(
        long generation,
        CancellationTokenSource cancellation) =>
        generation == gitConfigGeneration
        && ReferenceEquals(gitConfigCancellation, cancellation)
        && !cancellation.IsCancellationRequested;

    private GitConfigScope SelectedGitConfigScope() =>
        GitConfigScopeComboBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<GitConfigScope>(tag, ignoreCase: true, out var scope)
            ? scope
            : GitConfigScope.Global;

    private static void ValidateGitConfigText(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('\0')
            || value.Contains('\r')
            || value.Contains('\n'))
        {
            throw new ArgumentException($"Enter a {label} without line breaks or NUL characters.", nameof(value));
        }
    }

    private static string DescribeGitConfigValue(
        string key,
        string? selectedValue,
        string? globalValue,
        GitConfigScope scope)
    {
        if (selectedValue is not null)
        {
            return scope == GitConfigScope.Local
                ? $"Local {key}: {selectedValue}"
                : $"Global {key}: {selectedValue}";
        }

        if (scope == GitConfigScope.Local && globalValue is not null)
        {
            return $"Inherited global {key}: {globalValue}";
        }

        return scope == GitConfigScope.Local
            ? $"No local or global {key} is configured."
            : $"No global {key} is configured.";
    }

    private static string FormatGitConfigScope(GitConfigScope scope) =>
        scope == GitConfigScope.Local ? "local" : "global";
}
