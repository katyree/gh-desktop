using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private async Task LoadBranchesAsync(string? selectBranchName = null)
    {
        if (repositoryRoot is null)
        {
            return;
        }

        var root = repositoryRoot;
        var operation = BeginOperation("Loading branches…");
        try
        {
            var branches = await repositoryService.GetBranchesAsync(root, operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            branchRows.Clear();
            foreach (var branch in branches)
            {
                branchRows.Add(new BranchRow(branch));
            }

            branchesLoaded = true;
            selectedBranch = null;
            BranchesList.SelectedIndex = -1;
            var preferredIndex = -1;
            if (!string.IsNullOrEmpty(selectBranchName))
            {
                preferredIndex = branchRows.ToList().FindIndex(row => string.Equals(row.Branch.Name, selectBranchName, StringComparison.Ordinal));
            }

            var currentIndex = preferredIndex >= 0
                ? preferredIndex
                : branchRows.ToList().FindIndex(row => row.Branch.IsCurrent);
            if (currentIndex < 0 && branchRows.Count > 0)
            {
                currentIndex = 0;
            }

            if (currentIndex >= 0)
            {
                BranchesList.SelectedIndex = currentIndex;
            }
            else
            {
                UpdateBranchDetails();
            }

            StatusText.Text = $"Loaded {branchRows.Count} local branches";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // A newer repository operation owns the branch panel.
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("Unable to load branches", exception);
                UpdateBranchDetails();
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }

    private async Task LoadWorktreesAndStashesAsync()
    {
        if (repositoryRoot is null)
        {
            return;
        }

        var root = repositoryRoot;
        var operation = BeginOperation("Loading worktrees and stashes…");
        try
        {
            var worktreesTask = repositoryService.GetWorktreesAsync(root, operation.Token);
            var stashesTask = repositoryService.GetStashesAsync(root, operation.Token);
            await Task.WhenAll(worktreesTask, stashesTask);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            worktreeRows.Clear();
            foreach (var worktree in worktreesTask.Result)
            {
                worktreeRows.Add(new WorktreeRow(worktree, IsSamePath(worktree.Path, root)));
            }

            stashRows.Clear();
            foreach (var stash in stashesTask.Result)
            {
                stashRows.Add(new StashRow(stash));
            }
            StashesEmptyText.Visibility = stashRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            worktreesLoaded = true;
            stashesLoaded = true;
            selectedWorktree = null;
            selectedStash = null;
            WorktreesList.SelectedIndex = -1;
            StashesList.SelectedIndex = -1;
            if (worktreeRows.Count > 0)
            {
                WorktreesList.SelectedIndex = 0;
            }

            if (stashRows.Count > 0)
            {
                StashesList.SelectedIndex = 0;
            }

            UpdateRepositoryCommandStates();
            StatusText.Text = $"Loaded {worktreeRows.Count} worktrees and {stashRows.Count} stashes";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // A newer repository operation owns the worktree and stash panels.
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("Unable to load worktrees and stashes", exception);
                selectedWorktree = null;
                selectedStash = null;
                StashesEmptyText.Visibility = Visibility.Collapsed;
                UpdateRepositoryCommandStates();
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }

    private void BranchesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedBranch = BranchesList.SelectedItem as BranchRow;
        UpdateBranchDetails();
        UpdateRepositoryCommandStates();
    }

    private void WorktreesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedWorktree = WorktreesList.SelectedItem as WorktreeRow;
        UpdateRepositoryCommandStates();
    }

    private void StashesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedStash = StashesList.SelectedItem as StashRow;
        UpdateRepositoryCommandStates();
    }

    private void UpdateBranchDetails()
    {
        if (selectedBranch is null)
        {
            SelectedBranchNameText.Text = "Select a branch";
            SelectedBranchStatusText.Text = "Choose a local branch to inspect or switch.";
            SelectedBranchTrackingText.Text = string.Empty;
            SelectedBranchWorktreeText.Text = string.Empty;
            return;
        }

        SelectedBranchNameText.Text = selectedBranch.Name;
        SelectedBranchStatusText.Text = selectedBranch.StatusLabel;
        SelectedBranchTrackingText.Text = string.IsNullOrWhiteSpace(selectedBranch.TrackingLabel)
            ? "No upstream tracking branch configured."
            : $"Tracking: {selectedBranch.TrackingLabel}";
        SelectedBranchWorktreeText.Text = string.IsNullOrWhiteSpace(selectedBranch.WorktreeLabel)
            ? string.Empty
            : $"Worktree: {selectedBranch.WorktreeLabel}";
    }

    private void UpdateRepositoryCommandStates()
    {
        if (CreateBranchButton is null)
        {
            return;
        }

        var canInteract = repositoryRoot is not null
            && !mutationInProgress
            && !BusyRing.IsActive
            && activeGitOperationKind == GitOperationKind.None;
        var branchesView = currentWorkspace == "branches";
        var worktreesView = currentWorkspace == "worktrees";
        var remotesView = currentWorkspace == "remotes";
        var tagsView = currentWorkspace == "tags";
        var historyView = currentWorkspace == "history";
        var transportReady = canInteract
            && remotesView
            && selectedRemote is not null;
        var branchTransportReady = transportReady
            && currentStatus is not null
            && !currentStatus.IsDetached
            && !currentStatus.IsUnborn
            && !string.IsNullOrWhiteSpace(currentStatus.Branch);

        CreateBranchButton.IsEnabled = canInteract && branchesView;
        SwitchBranchButton.IsEnabled = canInteract
            && branchesView
            && selectedBranch is not null
            && !selectedBranch.Branch.IsCurrent;
        RenameBranchButton.IsEnabled = canInteract && branchesView && selectedBranch is not null;
        DeleteBranchButton.IsEnabled = canInteract
            && branchesView
            && selectedBranch is not null
            && !selectedBranch.Branch.IsCurrent;

        AddWorktreeButton.IsEnabled = canInteract && worktreesView;
        OpenWorktreeButton.IsEnabled = canInteract && worktreesView && selectedWorktree is not null;
        RemoveWorktreeButton.IsEnabled = canInteract
            && worktreesView
            && selectedWorktree is not null
            && !selectedWorktree.IsCurrent;
        CreateStashButton.IsEnabled = canInteract && worktreesView;
        ApplyStashButton.IsEnabled = canInteract && worktreesView && selectedStash is not null;
        DropStashButton.IsEnabled = canInteract && worktreesView && selectedStash is not null;

        AddRemoteButton.IsEnabled = canInteract && remotesView;
        EditRemoteButton.IsEnabled = transportReady;
        RemoveRemoteButton.IsEnabled = transportReady;
        FetchRemoteButton.IsEnabled = transportReady;
        PullRemoteButton.IsEnabled = branchTransportReady;
        PushRemoteButton.IsEnabled = branchTransportReady;

        CreateTagButton.IsEnabled = canInteract
            && tagsView
            && currentStatus is not null
            && !currentStatus.IsUnborn
            && !string.IsNullOrWhiteSpace(currentStatus.HeadId);
        DeleteTagButton.IsEnabled = canInteract && tagsView && selectedTag is not null;
        UndoHeadButton.IsEnabled = canInteract
            && historyView
            && currentStatus is not null
            && !currentStatus.IsUnborn
            && !currentStatus.IsDetached
            && !string.IsNullOrWhiteSpace(currentStatus.Branch)
            && !string.IsNullOrWhiteSpace(currentStatus.HeadId);
        CreateTagFromHistoryButton.IsEnabled = canInteract
            && historyView
            && selectedCommit is not null;
        UpdateHistoryComparisonControls();
        UpdateHistoryOperationControls();
        UpdateHistoryResetControls();
        UpdateGitOperationControls();
    }

    private async void CreateBranchButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartRepositoryWrite())
        {
            return;
        }

        var nameBox = new TextBox
        {
            Header = "Branch name",
            PlaceholderText = "feature/my-branch",
        };
        AutomationProperties.SetName(nameBox, "New branch name");
        var startPointBox = new TextBox
        {
            Header = "Start point (optional)",
            Text = currentStatus?.Branch ?? string.Empty,
            PlaceholderText = "Current HEAD or another ref",
        };
        AutomationProperties.SetName(startPointBox, "Branch start point");
        var dialog = CreateDialog("Create branch", "Create", new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Create a local branch without changing the current checkout.", TextWrapping = TextWrapping.Wrap },
                nameBox,
                startPointBox,
            },
        });

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = nameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("Branch name is required", new ArgumentException("Enter a local branch name."));
            return;
        }

        var startPoint = string.IsNullOrWhiteSpace(startPointBox.Text)
            ? null
            : startPointBox.Text.Trim();
        await RunRepositoryWriteAsync(
            "Creating branch…",
            "Branch created",
            "Branch creation cancelled; refreshing repository…",
            "Unable to create branch",
            (root, token) => repositoryService.CreateBranchAsync(root, name, startPoint, token),
            refreshBranches: true,
            refreshWorktrees: true);
    }

    private async void SwitchBranchButton_Click(object sender, RoutedEventArgs e)
    {
        var branch = selectedBranch;
        if (!CanStartRepositoryWrite() || branch is null || branch.Branch.IsCurrent)
        {
            return;
        }

        var sourceRoot = repositoryRoot;
        var sourceStatus = currentStatus;
        if (sourceRoot is null || sourceStatus is null)
        {
            ShowError(
                "Unable to prepare branch switch",
                new InvalidOperationException("Refresh the repository before switching branches."));
            return;
        }

        var targetBranchName = branch.Name;
        string targetTip;
        try
        {
            targetTip = await repositoryService.GetLocalBranchTipAsync(
                sourceRoot,
                targetBranchName,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            ShowError("Unable to prepare branch switch", exception);
            return;
        }

        var checkoutContext = new BranchCheckoutContext(
            sourceRoot,
            sourceStatus.Branch,
            sourceStatus.HeadId,
            targetBranchName,
            targetTip);
        var hasChanges = sourceStatus.Changes.Count > 0;
        BranchCheckoutStrategy? strategy = null;
        if (hasChanges)
        {
            var selectedStrategy = await ChooseDirtyBranchCheckoutStrategyAsync(
                targetBranchName,
                sourceStatus.Changes.Count);
            if (selectedStrategy is null)
            {
                return;
            }

            strategy = selectedStrategy.Value;
        }

        if (!IsCurrentBranchCheckoutContext(sourceRoot, sourceStatus, targetBranchName))
        {
            ShowError(
                "Branch switch context changed",
                new InvalidOperationException("The repository or selected branch changed while waiting for confirmation; refresh and try again."));
            return;
        }

        if (!hasChanges)
        {
            await RunRepositoryWriteAsync(
                $"Switching to {targetBranchName}…",
                $"Switched to {targetBranchName}",
                "Branch switch cancelled; refreshing repository…",
                "Unable to switch branch",
                (root, token) => repositoryService.CheckoutBranchAsync(root, targetBranchName, checkoutContext, token),
                refreshBranches: true,
                refreshWorktrees: true,
                expectedRoot: sourceRoot);
            return;
        }

        var bringChanges = strategy == BranchCheckoutStrategy.BringChanges;
        await RunRepositoryWriteAsync(
            bringChanges
                ? $"Switching to {targetBranchName} with local changes…"
                : $"Saving changes and switching to {targetBranchName}…",
            bringChanges
                ? $"Switched to {targetBranchName} with local changes"
                : $"Switched to {targetBranchName}; local changes saved in a stash",
            "Branch switch cancelled; refreshing repository…",
            "Unable to switch branch",
            bringChanges
                ? (root, token) => repositoryService.CheckoutBranchBringingChangesAsync(root, targetBranchName, checkoutContext, token)
                : (root, token) => repositoryService.CheckoutBranchWithStashAsync(root, targetBranchName, checkoutContext, token),
            refreshBranches: true,
            refreshWorktrees: true,
            expectedRoot: sourceRoot);
    }

    private bool IsCurrentBranchCheckoutContext(
        string sourceRoot,
        RepositoryStatus sourceStatus,
        string targetBranchName)
    {
        return repositoryRoot is not null
            && IsSamePath(repositoryRoot, sourceRoot)
            && currentStatus is not null
            && string.Equals(currentStatus.RootPath, sourceStatus.RootPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(currentStatus.Branch, sourceStatus.Branch, StringComparison.Ordinal)
            && string.Equals(currentStatus.HeadId, sourceStatus.HeadId, StringComparison.OrdinalIgnoreCase)
            && selectedBranch is not null
            && string.Equals(selectedBranch.Name, targetBranchName, StringComparison.Ordinal);
    }

    private async Task<BranchCheckoutStrategy?> ChooseDirtyBranchCheckoutStrategyAsync(
        string branchName,
        int changeCount)
    {
        var changeLabel = changeCount == 1 ? "1 local change" : $"{changeCount} local changes";
        var dialog = new ContentDialog
        {
            Title = "Switch branch with local changes?",
            PrimaryButtonText = "Bring changes",
            SecondaryButtonText = "Save stash and switch",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
            Content = new TextBlock
            {
                Text = $"This repository has {changeLabel}. Bring them to '{branchName}' if Git permits the switch. Git will otherwise save them in a temporary stash and try to apply them. Or save them in a named stash on the current branch and switch.",
                TextWrapping = TextWrapping.Wrap,
            },
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => BranchCheckoutStrategy.BringChanges,
            ContentDialogResult.Secondary => BranchCheckoutStrategy.StashChanges,
            _ => null,
        };
    }

    private async void RenameBranchButton_Click(object sender, RoutedEventArgs e)
    {
        var branch = selectedBranch;
        if (!CanStartRepositoryWrite() || branch is null || repositoryRoot is null)
        {
            return;
        }

        var sourceRoot = repositoryRoot;
        var sourceName = branch.Name;
        var wasCurrent = branch.Branch.IsCurrent;
        var upstream = branch.Branch.Upstream;
        string expectedTip;
        try
        {
            expectedTip = await repositoryService.GetLocalBranchTipAsync(
                sourceRoot,
                sourceName,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            ShowError("Unable to prepare branch rename", exception);
            await LoadBranchesAsync();
            return;
        }

        string? defaultBranchName = null;
        try
        {
            defaultBranchName = await repositoryService.TryGetDefaultBranchNameAsync(
                sourceRoot,
                CancellationToken.None);
        }
        catch
        {
            // Protection information is unavailable; never assume a default branch.
            defaultBranchName = null;
        }

        if (!wasCurrent && !string.IsNullOrWhiteSpace(branch.Branch.WorktreePath))
        {
            ShowError(
                "Unable to rename branch",
                new InvalidOperationException($"The branch '{sourceName}' is checked out in the worktree at '{branch.Branch.WorktreePath}'; switch or remove that worktree before renaming it."));
            await LoadBranchesAsync();
            return;
        }

        var notes = new List<string>(3);
        if (wasCurrent)
        {
            notes.Add("This is the current branch. It stays checked out under the new name.");
        }

        if (!string.IsNullOrWhiteSpace(upstream))
        {
            notes.Add($"This branch is tracking {upstream} and renaming it will not change the branch name on the remote.");
        }

        if (string.Equals(defaultBranchName, sourceName, StringComparison.Ordinal))
        {
            notes.Add($"'{sourceName}' appears to be the default branch; renaming it locally does not change the remote.");
        }

        notes.Add("Only the local branch is renamed; no remote branches are touched.");
        var nameBox = new TextBox
        {
            Header = "New branch name",
            Text = sourceName,
        };
        AutomationProperties.SetName(nameBox, "Renamed branch name");
        var dialogContent = new StackPanel
        {
            Spacing = 12,
        };
        dialogContent.Children.Add(new TextBlock { Text = $"Rename the local branch \"{sourceName}\".", TextWrapping = TextWrapping.Wrap });
        foreach (var note in notes)
        {
            dialogContent.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap });
        }

        dialogContent.Children.Add(nameBox);
        var dialog = CreateDialog("Rename branch", "Rename", dialogContent);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var newName = nameBox.Text.Trim();
        if (newName.Length == 0)
        {
            ShowError("Branch name is required", new ArgumentException("Enter a local branch name."));
            return;
        }

        if (string.Equals(newName, sourceName, StringComparison.Ordinal))
        {
            ShowError("Branch name is unchanged", new ArgumentException("The new branch name must be different from the current branch name."));
            return;
        }

        if (branchRows.Any(row => string.Equals(row.Branch.Name, newName, StringComparison.Ordinal)))
        {
            ShowError("Unable to rename branch", new InvalidOperationException($"A branch named '{newName}' already exists; choose a different name."));
            await LoadBranchesAsync();
            return;
        }

        if (!IsCurrentBranchMutationContext(sourceRoot, sourceName))
        {
            ShowError(
                "Branch changed",
                new InvalidOperationException("The repository or selected branch changed while the rename dialog was open; refresh and try again."));
            await LoadBranchesAsync();
            return;
        }

        string freshTip;
        try
        {
            freshTip = await repositoryService.GetLocalBranchTipAsync(
                sourceRoot,
                sourceName,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            ShowError("Unable to rename branch", exception);
            await LoadBranchesAsync();
            return;
        }

        if (!string.Equals(freshTip, expectedTip, StringComparison.OrdinalIgnoreCase))
        {
            ShowError(
                "Branch changed",
                new InvalidOperationException("The branch changed while the rename dialog was open; refresh and try again."));
            await LoadBranchesAsync();
            return;
        }

        var mutationContext = new BranchMutationContext(sourceRoot, sourceName, expectedTip);
        await RunRepositoryWriteAsync(
            "Renaming branch…",
            "Branch renamed",
            "Branch rename cancelled; refreshing repository…",
            "Unable to rename branch",
            (root, token) => repositoryService.RenameBranchAsync(root, sourceName, newName, mutationContext, token),
            refreshBranches: true,
            refreshWorktrees: true,
            expectedRoot: sourceRoot,
            selectBranchName: newName);
    }

    private async void DeleteBranchButton_Click(object sender, RoutedEventArgs e)
    {
        var branch = selectedBranch;
        if (!CanStartRepositoryWrite() || branch is null || branch.Branch.IsCurrent || repositoryRoot is null)
        {
            return;
        }

        var sourceRoot = repositoryRoot;
        var sourceName = branch.Name;
        var upstream = branch.Branch.Upstream;
        string expectedTip;
        try
        {
            expectedTip = await repositoryService.GetLocalBranchTipAsync(
                sourceRoot,
                sourceName,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            ShowError("Unable to prepare branch deletion", exception);
            await LoadBranchesAsync();
            return;
        }

        string? defaultBranchName = null;
        try
        {
            defaultBranchName = await repositoryService.TryGetDefaultBranchNameAsync(
                sourceRoot,
                CancellationToken.None);
        }
        catch
        {
            // Protection information is unavailable; never assume a default branch.
            defaultBranchName = null;
        }

        if (!string.IsNullOrWhiteSpace(branch.Branch.WorktreePath))
        {
            ShowError(
                "Unable to delete branch",
                new InvalidOperationException($"The branch '{sourceName}' is checked out in the worktree at '{branch.Branch.WorktreePath}'; remove or switch that worktree before deleting it."));
            await LoadBranchesAsync();
            return;
        }

        if (string.Equals(defaultBranchName, sourceName, StringComparison.Ordinal))
        {
            ShowError(
                "Unable to delete branch",
                new InvalidOperationException($"The branch '{sourceName}' appears to be the default branch; deleting it locally is not allowed from this view."));
            await LoadBranchesAsync();
            return;
        }

        var confirmation = new StackPanel
        {
            Spacing = 12,
        };
        confirmation.Children.Add(new TextBlock { Text = $"Delete the local branch \"{sourceName}\"?", TextWrapping = TextWrapping.Wrap });
        confirmation.Children.Add(new TextBlock { Text = "This action cannot be undone.", TextWrapping = TextWrapping.Wrap });
        confirmation.Children.Add(new TextBlock { Text = $"Safe delete is used: Git refuses when \"{sourceName}\" contains commits that are not merged, and the branch is kept. There is no force-delete choice here.", TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(upstream))
        {
            confirmation.Children.Add(new TextBlock { Text = $"The upstream {upstream} is not touched.", TextWrapping = TextWrapping.Wrap });
        }

        confirmation.Children.Add(new TextBlock { Text = "No remote branches are deleted by this action.", TextWrapping = TextWrapping.Wrap });
        var dialog = CreateDialog("Delete branch?", "Delete", confirmation);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (!IsCurrentBranchMutationContext(sourceRoot, sourceName))
        {
            ShowError(
                "Branch changed",
                new InvalidOperationException("The repository or selected branch changed while the delete dialog was open; refresh and try again."));
            await LoadBranchesAsync();
            return;
        }

        string freshTip;
        try
        {
            freshTip = await repositoryService.GetLocalBranchTipAsync(
                sourceRoot,
                sourceName,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            ShowError("Unable to delete branch", exception);
            await LoadBranchesAsync();
            return;
        }

        if (!string.Equals(freshTip, expectedTip, StringComparison.OrdinalIgnoreCase))
        {
            ShowError(
                "Branch changed",
                new InvalidOperationException("The branch changed while the delete dialog was open; refresh and try again."));
            await LoadBranchesAsync();
            return;
        }

        var mutationContext = new BranchMutationContext(sourceRoot, sourceName, expectedTip);
        await RunRepositoryWriteAsync(
            $"Deleting {sourceName}…",
            "Branch deleted",
            "Branch deletion cancelled; refreshing repository…",
            "Unable to delete branch",
            (root, token) => repositoryService.DeleteBranchAsync(root, sourceName, force: false, mutationContext, token),
            refreshBranches: true,
            refreshWorktrees: true,
            expectedRoot: sourceRoot);
    }

    private bool IsCurrentBranchMutationContext(
        string sourceRoot,
        string sourceBranchName)
    {
        if (repositoryRoot is null
            || !IsSamePath(repositoryRoot, sourceRoot)
            || currentStatus is null
            || !string.Equals(currentStatus.RootPath, sourceRoot, StringComparison.OrdinalIgnoreCase)
            || selectedBranch is null
            || !string.Equals(selectedBranch.Name, sourceBranchName, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private async void AddWorktreeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartRepositoryWrite())
        {
            return;
        }

        var pathBox = new TextBox
        {
            Header = "Destination folder",
            PlaceholderText = "C:\\work\\my-worktree",
        };
        AutomationProperties.SetName(pathBox, "Worktree destination folder");
        var revisionBox = new TextBox
        {
            Header = "Start from branch or commit",
            Text = currentStatus?.Branch ?? string.Empty,
            PlaceholderText = "main or a commit SHA",
        };
        AutomationProperties.SetName(revisionBox, "Worktree starting reference");
        var branchBox = new TextBox
        {
            Header = "New branch name (optional)",
            PlaceholderText = "Create a branch at the start point",
        };
        AutomationProperties.SetName(branchBox, "New worktree branch name");
        var dialog = CreateDialog("Add worktree", "Add", new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Add a clean registered worktree. Its destination must not already exist.", TextWrapping = TextWrapping.Wrap },
                pathBox,
                revisionBox,
                branchBox,
            },
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var path = pathBox.Text.Trim();
        var revision = revisionBox.Text.Trim();
        if (path.Length == 0 || revision.Length == 0)
        {
            ShowError("Worktree details are required", new ArgumentException("Enter a destination folder and starting branch or commit."));
            return;
        }

        var newBranch = string.IsNullOrWhiteSpace(branchBox.Text) ? null : branchBox.Text.Trim();
        await RunRepositoryWriteAsync(
            "Adding worktree…",
            "Worktree added",
            "Worktree add cancelled; refreshing repository…",
            "Unable to add worktree",
            (root, token) => repositoryService.AddWorktreeAsync(root, path, revision, newBranch, token),
            refreshBranches: true,
            refreshWorktrees: true);
    }

    private async void OpenWorktreeButton_Click(object sender, RoutedEventArgs e)
    {
        var worktree = selectedWorktree;
        if (mutationInProgress || BusyRing.IsActive || worktree is null)
        {
            return;
        }

        await OpenRepositoryAsync(worktree.Path);
    }

    private async void RemoveWorktreeButton_Click(object sender, RoutedEventArgs e)
    {
        var worktree = selectedWorktree;
        if (!CanStartRepositoryWrite() || worktree is null || worktree.IsCurrent)
        {
            return;
        }

        var dialog = CreateDialog(
            "Remove worktree?",
            "Remove",
            new TextBlock
            {
                Text = $"Remove the worktree folder \"{worktree.Path}\" and delete its files? Git will refuse while the worktree has uncommitted changes; this action never forces removal.",
                TextWrapping = TextWrapping.Wrap,
            });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunRepositoryWriteAsync(
            "Removing worktree…",
            "Worktree removed",
            "Worktree removal cancelled; refreshing repository…",
            "Unable to remove worktree",
            (root, token) => repositoryService.RemoveWorktreeAsync(root, worktree.Path, token),
            refreshBranches: true,
            refreshWorktrees: true);
    }

    private async void CreateStashButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartRepositoryWrite())
        {
            return;
        }

        var messageBox = new TextBox
        {
            Header = "Stash message",
            PlaceholderText = "Work in progress",
        };
        AutomationProperties.SetName(messageBox, "Stash message");
        var includeUntracked = new CheckBox
        {
            Content = "Include untracked files",
        };
        AutomationProperties.SetName(includeUntracked, "Include untracked files in stash");
        var dialog = CreateDialog("Create stash", "Create", new StackPanel
        {
            Spacing = 12,
            Children =
            {
                messageBox,
                includeUntracked,
            },
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var message = messageBox.Text.Trim();
        if (message.Length == 0)
        {
            ShowError("Stash message is required", new ArgumentException("Enter a short stash message."));
            return;
        }

        await RunRepositoryWriteAsync(
            "Creating stash…",
            "Stash created",
            "Stash creation cancelled; refreshing repository…",
            "Unable to create stash",
            async (root, token) =>
            {
                var stashId = await repositoryService.CreateStashAsync(root, message, includeUntracked.IsChecked == true, token);
                StatusText.Text = $"Created stash {ShortObjectId(stashId)}";
            },
            refreshStashes: true);
    }

    private async void ApplyStashButton_Click(object sender, RoutedEventArgs e)
    {
        var stash = selectedStash;
        if (!CanStartRepositoryWrite() || stash is null)
        {
            return;
        }

        var restoreIndex = new CheckBox
        {
            Content = "Restore staged changes",
        };
        AutomationProperties.SetName(restoreIndex, "Restore staged stash state");
        var dialog = CreateDialog("Apply stash", "Apply", new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"Apply {stash.Reference} ({ShortObjectId(stash.CommitId)}) to the current worktree? The stash is retained and remains available after applying.",
                    TextWrapping = TextWrapping.Wrap,
                },
                restoreIndex,
            },
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var stashSHA = stash.Stash.CommitId;
        await RunRepositoryWriteAsync(
            $"Applying {stash.Reference}…",
            "Stash applied and retained",
            "Stash apply cancelled; refreshing repository…",
            "Unable to apply stash",
            (root, token) => repositoryService.ApplyStashAsync(root, stashSHA, restoreIndex.IsChecked == true, token),
            refreshStashes: true);
    }

    private async void DropStashButton_Click(object sender, RoutedEventArgs e)
    {
        var stash = selectedStash;
        if (!CanStartRepositoryWrite() || stash is null)
        {
            return;
        }

        var dialog = CreateDialog(
            "Drop stash?",
            "Drop",
            new TextBlock
            {
                Text = $"Drop the stash \"{stash.Reference}\" ({ShortObjectId(stash.CommitId)})? This removes that stash entry from the reflog.",
                TextWrapping = TextWrapping.Wrap,
            });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var expectedReference = stash.Stash.Reference;
        var expectedSHA = stash.Stash.CommitId;
        await RunRepositoryWriteAsync(
            $"Dropping {expectedReference}…",
            "Stash dropped",
            "Stash drop cancelled; refreshing repository…",
            "Unable to drop stash",
            (root, token) => repositoryService.DropStashAsync(root, expectedReference, expectedSHA, token),
            refreshStashes: true);
    }

    private bool CanStartRepositoryWrite() => repositoryRoot is not null
        && !mutationInProgress
        && !BusyRing.IsActive;

    private async Task<bool> RunRepositoryWriteAsync(
        string progress,
        string success,
        string cancelled,
        string failureTitle,
        Func<string, CancellationToken, Task> mutation,
        bool refreshBranches = false,
        bool refreshWorktrees = false,
        bool refreshStashes = false,
        bool refreshRemotes = false,
        bool refreshTags = false,
        string? expectedRoot = null,
        string? selectBranchName = null)
    {
        if (!CanStartRepositoryWrite() || repositoryRoot is null)
        {
            return false;
        }

        if (expectedRoot is not null && !IsSamePath(repositoryRoot, expectedRoot))
        {
            ShowError(
                "Repository changed",
                new InvalidOperationException("The repository changed while waiting for branch confirmation; refresh and try again."));
            return false;
        }

        var root = expectedRoot ?? repositoryRoot;
        mutationInProgress = true;
        var operation = BeginOperation(progress);
        var mutationSucceeded = false;
        try
        {
            await mutation(root, operation.Token);
            ErrorBar.IsOpen = false;
            StatusText.Text = success;
            mutationSucceeded = true;
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            StatusText.Text = cancelled;
        }
        catch (Exception exception)
        {
            ShowError(failureTitle, exception);
        }
        finally
        {
            try
            {
                await RefreshAfterRepositoryWriteAsync(root, refreshBranches, refreshWorktrees, refreshStashes, refreshRemotes, refreshTags, selectBranchName);
            }
            catch (Exception refreshException)
            {
                ShowError("Unable to refresh repository after operation", refreshException);
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
        }

        return mutationSucceeded;
    }

    private async Task RefreshAfterRepositoryWriteAsync(
        string expectedRoot,
        bool refreshBranches,
        bool refreshWorktrees,
        bool refreshStashes,
        bool refreshRemotes,
        bool refreshTags,
        string? selectBranchName = null)
    {
        if (repositoryRoot is null
            || !string.Equals(repositoryRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await RefreshRepositoryAsync();
        await WaitForLatestOperationAsync();
        if (repositoryRoot is null
            || !string.Equals(repositoryRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (refreshBranches)
        {
            branchesLoaded = false;
        }

        if (refreshWorktrees || refreshStashes)
        {
            worktreesLoaded = false;
            stashesLoaded = false;
        }

        if (refreshRemotes)
        {
            remotesLoaded = false;
        }

        if (refreshTags)
        {
            tagsLoaded = false;
        }

        if (refreshBranches)
        {
            await LoadBranchesAsync(selectBranchName);
        }

        if (refreshWorktrees || refreshStashes)
        {
            await LoadWorktreesAndStashesAsync();
        }

        if (refreshRemotes)
        {
            await LoadRemotesAsync();
        }

        if (refreshTags)
        {
            await LoadTagsAsync();
        }
    }

    private ContentDialog CreateDialog(string title, string primaryButtonText, object content)
    {
        return new ContentDialog
        {
            Title = title,
            PrimaryButtonText = primaryButtonText,
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
            Content = content,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
        };
    }

    private static string ShortObjectId(string value) => value.Length > 7 ? value[..7] : value;

    private static bool IsSamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }
}
