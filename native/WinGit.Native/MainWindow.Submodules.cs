using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private async Task LoadSubmodulesAsync()
    {
        if (repositoryRoot is null)
        {
            return;
        }

        var root = repositoryRoot;
        var operation = BeginOperation("Loading submodules…");
        try
        {
            var submodules = await repositoryService.GetSubmodulesAsync(root, operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                || currentWorkspace != "submodules")
            {
                return;
            }

            submoduleRows.Clear();
            foreach (var submodule in submodules)
            {
                submoduleRows.Add(new SubmoduleRow(submodule));
            }

            submodulesLoaded = true;
            selectedSubmodule = null;
            SubmodulesList.SelectedIndex = -1;
            SubmodulesEmptyText.Text = submoduleRows.Count == 0
                ? "No submodules configured"
                : string.Empty;
            SubmodulesEmptyText.Visibility = submoduleRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateSubmoduleDetails();
            if (submoduleRows.Count > 0)
            {
                SubmodulesList.SelectedIndex = 0;
            }

            SubmodulesStatusText.Text = submoduleRows.Count == 0
                ? "Add a Git submodule to see its recorded index commit and local worktree state here."
                : "Select a submodule to inspect or update it.";
            StatusText.Text = submoduleRows.Count == 0
                ? "No submodules configured"
                : $"Loaded {submoduleRows.Count} submodules";
            UpdateSubmoduleControls();
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // A newer repository operation owns the submodule panel.
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token)
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                && currentWorkspace == "submodules")
            {
                submodulesLoaded = false;
                submoduleRows.Clear();
                selectedSubmodule = null;
                SubmodulesList.SelectedIndex = -1;
                SubmodulesEmptyText.Text = "Submodules unavailable. Refresh to try again.";
                SubmodulesEmptyText.Visibility = Visibility.Visible;
                UpdateSubmoduleDetails();
                SubmodulesStatusText.Text = "Refresh the submodule view after resolving the repository error.";
                ShowError("Unable to load submodules", exception);
                UpdateSubmoduleControls();
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }

    private async void RefreshSubmodulesButton_Click(object sender, RoutedEventArgs e)
    {
        if (repositoryRoot is null || currentWorkspace != "submodules")
        {
            return;
        }

        submodulesLoaded = false;
        await LoadSubmodulesAsync();
    }

    private void SubmodulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedSubmodule = SubmodulesList.SelectedItem as SubmoduleRow;
        UpdateSubmoduleDetails();
        UpdateSubmoduleControls();
    }

    private void UpdateSubmoduleDetails()
    {
        if (selectedSubmodule is null)
        {
            SelectedSubmodulePathText.Text = "Select a submodule";
            SelectedSubmoduleStatusText.Text = "Choose a submodule to inspect its recorded commit and local worktree state.";
            SelectedSubmoduleExpectedText.Text = string.Empty;
            SelectedSubmoduleHeadText.Text = string.Empty;
            SelectedSubmoduleChangesText.Text = string.Empty;
            return;
        }

        var snapshot = selectedSubmodule.Snapshot;
        SelectedSubmodulePathText.Text = snapshot.Path;
        SelectedSubmoduleStatusText.Text = selectedSubmodule.StatusLabel;
        SelectedSubmoduleExpectedText.Text = $"Expected index commit: {snapshot.ExpectedIndexCommitId}";
        SelectedSubmoduleHeadText.Text = snapshot.CheckedOutHeadCommitId is null
            ? "Current HEAD: not initialized"
            : $"Current HEAD: {snapshot.CheckedOutHeadCommitId}";
        SelectedSubmoduleChangesText.Text = snapshot.IsDirty
            ? "Nested worktree: local changes detected. Review or stash local changes before updating."
            : snapshot.IsInitialized
                ? "Nested worktree: clean"
                : "Nested worktree: not initialized";
    }

    private void UpdateSubmoduleControls()
    {
        if (SubmodulesList is null)
        {
            return;
        }

        var canRead = repositoryRoot is not null
            && currentWorkspace == "submodules"
            && !mutationInProgress
            && !BusyRing.IsActive;
        var canUpdate = canRead && activeGitOperationKind == GitOperationKind.None;
        RefreshSubmodulesButton.IsEnabled = canRead;
        OpenSubmoduleButton.IsEnabled = canRead
            && selectedSubmodule?.Snapshot.IsInitialized == true;
        UpdateSubmoduleButton.IsEnabled = canUpdate
            && selectedSubmodule is not null
            && !selectedSubmodule.Snapshot.IsDirty;
    }

    private async void OpenSubmoduleButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = selectedSubmodule;
        var root = repositoryRoot;
        if (selected is null
            || root is null
            || !selected.Snapshot.IsInitialized
            || mutationInProgress
            || BusyRing.IsActive)
        {
            return;
        }

        var path = Path.GetFullPath(
            Path.Combine(root, selected.Snapshot.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!Directory.Exists(path))
        {
            ShowError(
                "Unable to open submodule",
                new DirectoryNotFoundException($"The initialized submodule path '{selected.Snapshot.Path}' is not present."));
            return;
        }

        await OpenRepositoryAsync(path);
    }

    private async void UpdateSubmoduleButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = selectedSubmodule;
        var root = repositoryRoot;
        if (selected is null
            || root is null
            || currentWorkspace != "submodules"
            || !CanStartRepositoryWrite()
            || activeGitOperationKind != GitOperationKind.None
            || selected.Snapshot.IsDirty
            || !IsSamePath(root, selected.Snapshot.RootPath))
        {
            return;
        }

        var snapshot = selected.Snapshot;
        var currentHead = snapshot.CheckedOutHeadCommitId ?? "not initialized";
        var dialog = CreateDialog(
            "Update submodule",
            "Update",
            new TextBlock
            {
                Text = $"Update '{snapshot.Path}' to the commit recorded in the containing repository index?\n\n"
                    + $"Target index commit: {snapshot.ExpectedIndexCommitId}\n"
                    + $"Current HEAD: {currentHead}\n\n"
                    + "Git may download missing commits. WinGit preserves local changes and refuses the update if they would be overwritten.",
                TextWrapping = TextWrapping.Wrap,
            });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunRepositoryWriteAsync(
            $"Updating {snapshot.Path}…",
            $"Submodule '{snapshot.Path}' updated",
            "Submodule update cancelled; refreshing repository…",
            $"Unable to update submodule '{snapshot.Path}'",
            (mutationRoot, token) => repositoryService.UpdateSubmoduleAsync(
                mutationRoot,
                snapshot,
                token),
            expectedRoot: root);
    }
}
