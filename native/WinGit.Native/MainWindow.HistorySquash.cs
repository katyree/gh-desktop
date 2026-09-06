using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private bool historySquashDialogOpen;
    private SquashPlan? activeSquashPlan;
    private string? activeSquashMessage;
    private string? squashRecoveryUnavailableRoot;

    private sealed record HistorySquashRequest(
        string Root,
        string Branch,
        string ExpectedHeadId,
        IReadOnlyList<CommitSummary> Commits);

    private void UpdateHistorySquashControls()
    {
        if (HistorySquashButton is null)
        {
            return;
        }

        var selectedRows = GetSelectedHistoryCommitRows();
        var selectedIds = selectedRows
            .Select(row => row.Commit.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasTarget = commitRows.Any(row => !selectedIds.Contains(row.Commit.Id));
        var canStart = repositoryRoot is not null
            && currentWorkspace == "history"
            && !diagnosticCaptureMode
            && !historyComparisonActive
            && !historySquashDialogOpen
            && !mutationInProgress
            && !BusyRing.IsActive
            && activeGitOperationKind == GitOperationKind.None
            && currentStatus is { IsDetached: false, IsUnborn: false } status
            && !string.IsNullOrWhiteSpace(status.Branch)
            && !string.IsNullOrWhiteSpace(status.HeadId)
            && status.Changes.Count == 0;

        HistorySquashButton.Label = selectedRows.Length > 1
            ? $"Squash {selectedRows.Length} commits"
            : "Squash selected";
        HistorySquashButton.IsEnabled = canStart
            && selectedRows.Length > 0
            && selectedRows.Length <= 1_000
            && hasTarget;
    }

    private async void HistorySquashButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await StartHistorySquashAsync();
    }

    private HistorySquashRequest? CaptureHistorySquashRequest()
    {
        if (repositoryRoot is null
            || currentWorkspace != "history"
            || diagnosticCaptureMode
            || historyComparisonActive
            || historySquashDialogOpen
            || mutationInProgress
            || BusyRing.IsActive
            || activeGitOperationKind != GitOperationKind.None
            || currentStatus is not { IsDetached: false, IsUnborn: false } status
            || string.IsNullOrWhiteSpace(status.Branch)
            || string.IsNullOrWhiteSpace(status.HeadId)
            || status.Changes.Count != 0)
        {
            return null;
        }

        var selectedRows = GetSelectedHistoryCommitRows();
        if (selectedRows.Length == 0 || selectedRows.Length > 1_000)
        {
            return null;
        }

        return new HistorySquashRequest(
            repositoryRoot,
            status.Branch,
            status.HeadId,
            selectedRows.Select(row => row.Commit).ToArray());
    }

    private async Task StartHistorySquashAsync()
    {
        var request = CaptureHistorySquashRequest();
        if (request is null)
        {
            return;
        }

        historySquashDialogOpen = true;
        UpdateHistorySquashControls();
        try
        {
            var targetBox = CreateHistorySquashTargetBox(request);
            var targetDialog = CreateDialog(
                "Choose squash target",
                "Preview squash",
                new StackPanel
                {
                    Spacing = 10,
                    MaxWidth = 680,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Fold {request.Commits.Count} selected commit{(request.Commits.Count == 1 ? string.Empty : "s")} into another commit on \"{request.Branch}\".",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = "Choose the commit that should remain as the base of the resulting commit. The selected commits are captured by ID and replayed in chronological order.",
                            TextWrapping = TextWrapping.Wrap,
                            Style = (Style)RootGrid.Resources["SecondaryTextStyle"],
                        },
                        targetBox,
                    },
                });
            targetDialog.DefaultButton = ContentDialogButton.Primary;
            AutomationProperties.SetName(targetDialog, "Choose a target for squashing history");
            if (await targetDialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!IsHistorySquashRequestCurrent(request))
            {
                ShowHistorySquashChangedError();
                return;
            }

            var targetCommitId = targetBox.SelectedItem is ComboBoxItem { Tag: string target }
                && !string.IsNullOrWhiteSpace(target)
                ? target
                : null;
            if (targetCommitId is null)
            {
                ShowError(
                    "Squash target is required",
                    new InvalidOperationException("Choose a commit to keep as the squash target."));
                return;
            }

            var planOperation = BeginOperation("Preparing squash preview…");
            SquashPlan? plan = null;
            try
            {
                plan = await repositoryService.CaptureSquashPlanFromCurrentHistoryAsync(
                    request.Root,
                    request.Commits.Select(commit => commit.Id).ToArray(),
                    targetCommitId,
                    planOperation.Token);
            }
            catch (OperationCanceledException) when (planOperation.Token.IsCancellationRequested)
            {
                if (planOperation.Generation == operationGeneration
                    && string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase))
                {
                    StatusText.Text = "Squash preview canceled. Refresh History before trying again.";
                }

                return;
            }
            catch (SquashOperationBlockedException exception)
            {
                if (IsCurrent(planOperation.Generation, planOperation.Token))
                {
                    ShowError("Squash unavailable", exception);
                }

                return;
            }
            catch (Exception exception)
            {
                if (IsCurrent(planOperation.Generation, planOperation.Token))
                {
                    ShowError("Unable to prepare squash", exception);
                }

                return;
            }
            finally
            {
                EndOperation(planOperation.Generation);
            }

            if (plan is null || !IsHistorySquashRequestCurrent(request))
            {
                if (plan is not null)
                {
                    ShowHistorySquashChangedError();
                }

                return;
            }

            var messageBox = new TextBox
            {
                Header = "Replacement commit message (required)",
                Text = plan.ResultingCommit.Summary,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MaxHeight = 150,
            };
            AutomationProperties.SetName(messageBox, "Replacement commit message");

            var confirmation = CreateHistorySquashConfirmation(plan, messageBox);
            confirmation.DefaultButton = ContentDialogButton.Primary;
            AutomationProperties.SetName(confirmation, "Confirm history squash");
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var replacementMessage = messageBox.Text.Trim();
            if (replacementMessage.Length == 0)
            {
                ShowError(
                    "Replacement message is required",
                    new ArgumentException("Choose a nonblank replacement commit message before rewriting history."));
                return;
            }

            if (!IsHistorySquashRequestCurrent(request))
            {
                ShowHistorySquashChangedError();
                return;
            }

            await RunHistorySquashAsync(request, plan, replacementMessage);
        }
        finally
        {
            historySquashDialogOpen = false;
            UpdateHistorySquashControls();
        }
    }

    private ComboBox CreateHistorySquashTargetBox(HistorySquashRequest request)
    {
        var targetBox = new ComboBox
        {
            Header = "Squash selected commits onto",
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(targetBox, "Squash target commit");

        var selectedIds = request.Commits
            .Select(commit => commit.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in commitRows)
        {
            if (selectedIds.Contains(row.Commit.Id))
            {
                continue;
            }

            targetBox.Items.Add(new ComboBoxItem
            {
                Content = $"Keep {row.ShortId}  ·  {row.Summary}",
                Tag = row.Commit.Id,
            });
        }

        if (targetBox.Items.Count > 0)
        {
            targetBox.SelectedIndex = 0;
        }

        return targetBox;
    }

    private ContentDialog CreateHistorySquashConfirmation(
        SquashPlan plan,
        TextBox messageBox)
    {
        var content = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 760,
            Children =
            {
                new TextBlock
                {
                    Text = $"Rewrite the local history of \"{plan.Branch}\" at captured HEAD {plan.ExpectedHeadId}?",
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = $"The selected commits will be folded into {plan.TargetCommit.Id}. The resulting commit is based on {plan.ResultingCommit.Id}; commit IDs after the replay will change.",
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)RootGrid.Resources["SecondaryTextStyle"],
                },
                new TextBlock
                {
                    Text = $"Replay base: {(plan.LastRetainedCommitId is null ? "empty tree (root range)" : plan.LastRetainedCommitId)}.",
                    FontFamily = new FontFamily("Cascadia Mono"),
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)RootGrid.Resources["SecondaryTextStyle"],
                },
                new TextBlock
                {
                    Text = "The message below replaces the combined squash message. Git may stop for conflicts; use the existing operation panel to resolve, continue, skip, or abort.",
                    TextWrapping = TextWrapping.Wrap,
                },
                messageBox,
                CreateHistorySquashPlanDetails("Selected commits (oldest to newest)", plan.SelectedCommits),
                CreateHistorySquashReplayDetails(plan),
            },
        };

        return CreateDialog("Squash commits?", "Rewrite history", new ScrollViewer
        {
            Content = content,
            MaxHeight = 620,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
    }

    private static TextBlock CreateHistorySquashPlanDetails(
        string title,
        IReadOnlyList<SquashPlanCommit> commits)
    {
        const int maximumVisibleCommits = 80;
        var lines = commits
            .Take(maximumVisibleCommits)
            .Select(commit => $"{commit.Id}  {commit.Summary}")
            .ToList();
        if (commits.Count > lines.Count)
        {
            lines.Add($"… and {commits.Count - maximumVisibleCommits} more");
        }

        return new TextBlock
        {
            Text = $"{title} ({commits.Count})\n{string.Join(Environment.NewLine, lines)}",
            FontFamily = new FontFamily("Cascadia Mono"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
    }

    private static TextBlock CreateHistorySquashReplayDetails(SquashPlan plan)
    {
        const int maximumVisibleCommits = 80;
        var lines = plan.ReplaySteps
            .Take(maximumVisibleCommits)
            .Select(step => $"{step.Action.ToString().ToLowerInvariant()} {step.Commit.Id}  {step.Commit.Summary}")
            .ToList();
        if (plan.ReplaySteps.Count > lines.Count)
        {
            lines.Add($"… and {plan.ReplaySteps.Count - maximumVisibleCommits} more");
        }

        return new TextBlock
        {
            Text = $"Replay order (oldest to newest) ({plan.ReplaySteps.Count})\n{string.Join(Environment.NewLine, lines)}",
            FontFamily = new FontFamily("Cascadia Mono"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
    }

    private bool IsHistorySquashRequestCurrent(HistorySquashRequest request)
    {
        if (repositoryRoot is null
            || currentWorkspace != "history"
            || diagnosticCaptureMode
            || historyComparisonActive
            || mutationInProgress
            || BusyRing.IsActive
            || activeGitOperationKind != GitOperationKind.None
            || currentStatus is not { IsDetached: false, IsUnborn: false } status
            || string.IsNullOrWhiteSpace(status.Branch)
            || string.IsNullOrWhiteSpace(status.HeadId)
            || status.Changes.Count != 0
            || !string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(status.RootPath, request.Root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(status.Branch, request.Branch, StringComparison.Ordinal)
            || !string.Equals(status.HeadId, request.ExpectedHeadId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return GetSelectedHistoryCommitRows()
            .Select(row => row.Commit.Id)
            .SequenceEqual(
                request.Commits.Select(commit => commit.Id),
                StringComparer.OrdinalIgnoreCase);
    }

    private void ShowHistorySquashChangedError()
    {
        ShowError(
            "Squash stopped",
            new InvalidOperationException(
                "The branch, HEAD, selected commits, or repository state changed. Refresh History and review the squash again."));
    }

    private async Task RunHistorySquashAsync(
        HistorySquashRequest request,
        SquashPlan plan,
        string replacementMessage)
    {
        if (repositoryRoot is null
            || currentWorkspace != "history"
            || historyComparisonActive
            || mutationInProgress
            || BusyRing.IsActive
            || activeGitOperationKind != GitOperationKind.None
            || !string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        activeSquashPlan = plan;
        activeSquashMessage = replacementMessage;
        squashRecoveryUnavailableRoot = null;
        mutationInProgress = true;
        var operation = BeginOperation("Squashing commits…");
        try
        {
            var status = await repositoryService.GetStatusAsync(
                request.Root,
                operation.Token);
            var mergeState = await repositoryService.GetMergeStateAsync(
                request.Root,
                operation.Token);
            var repositoryStillMatches = IsCurrent(operation.Generation, operation.Token)
                && string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase)
                && currentWorkspace == "history"
                && !historyComparisonActive
                && status.Changes.Count == 0
                && !status.IsDetached
                && !status.IsUnborn
                && string.Equals(status.RootPath, request.Root, StringComparison.OrdinalIgnoreCase)
                && string.Equals(status.Branch, request.Branch, StringComparison.Ordinal)
                && string.Equals(status.HeadId, request.ExpectedHeadId, StringComparison.OrdinalIgnoreCase)
                && mergeState.OperationKind == GitOperationKind.None
                && !mergeState.HasUnresolvedConflicts
                && GetSelectedHistoryCommitRows()
                    .Select(row => row.Commit.Id)
                    .SequenceEqual(
                        request.Commits.Select(commit => commit.Id),
                        StringComparer.OrdinalIgnoreCase);
            if (!repositoryStillMatches)
            {
                if (!operation.Token.IsCancellationRequested
                    && string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase))
                {
                    ShowHistorySquashChangedError();
                }

                return;
            }

            var result = await repositoryService.SquashAsync(
                request.Root,
                plan,
                replacementMessage,
                operation.Token);
            ErrorBar.IsOpen = false;
            StatusText.Text = FormatHistorySquashResult(result);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            StatusText.Text = "Squash canceled; repository was refreshed. Check the operation panel for its current state.";
        }
        catch (SquashOperationBlockedException exception)
        {
            ShowError("Squash unavailable", exception);
        }
        catch (Exception exception)
        {
            ShowError("Unable to squash commits", exception);
        }
        finally
        {
            try
            {
                await RefreshAfterGitOperationAsync(request.Root);
            }
            catch (Exception refreshException)
            {
                ShowError("Unable to refresh repository after squash", refreshException);
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
        }
    }

    private static string FormatHistorySquashResult(RebaseOperationResult result) => result.Outcome switch
    {
        RebaseOutcome.Completed => $"Squash completed at {ShortObjectId(result.HeadId)}",
        RebaseOutcome.AlreadyUpToDate => "Squash is already up to date",
        RebaseOutcome.Conflicts => "Squash stopped with conflicts",
        RebaseOutcome.InProgress => "Squash is still in progress",
        RebaseOutcome.Skipped => "Squash commit skipped",
        RebaseOutcome.Aborted => "Squash aborted",
        _ => "Squash finished",
    };

    private bool TryGetActiveSquashRecovery(
        GitOperationSnapshot snapshot,
        out SquashPlan plan,
        out string replacementMessage)
    {
        plan = null!;
        replacementMessage = string.Empty;
        if (activeSquashPlan is not { } activePlan
            || string.IsNullOrWhiteSpace(activeSquashMessage)
            || snapshot.Kind != GitOperationKind.Rebase
            || !string.Equals(snapshot.Root, activePlan.RootPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.Branch, activePlan.Branch, StringComparison.Ordinal)
            || !string.Equals(snapshot.OriginalBranchTip, activePlan.ExpectedHeadId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        plan = activePlan;
        replacementMessage = activeSquashMessage;
        return true;
    }

    private bool IsSquashRecoveryUnavailable(GitOperationSnapshot snapshot) =>
        snapshot.Kind == GitOperationKind.Rebase
        && squashRecoveryUnavailableRoot is { } root
        && string.Equals(root, snapshot.Root, StringComparison.OrdinalIgnoreCase);

    private void ReconcileActiveSquashRecovery(
        string root,
        GitOperationLoadState state)
    {
        if (state.Kind == GitOperationKind.Rebase)
        {
            if (state.SquashRecovery?.IsUnavailable == true)
            {
                ClearActiveSquashRecovery();
                squashRecoveryUnavailableRoot = root;
                return;
            }

            if (state.SquashRecovery?.Recovery is { } recovery)
            {
                var persistedPlan = recovery.Plan;
                if (state.Rebase is { } persistedRebase
                    && string.Equals(root, persistedPlan.RootPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(persistedRebase.Branch, persistedPlan.Branch, StringComparison.Ordinal)
                    && string.Equals(persistedRebase.OriginalBranchTip, persistedPlan.ExpectedHeadId, StringComparison.OrdinalIgnoreCase))
                {
                    activeSquashPlan = persistedPlan;
                    activeSquashMessage = recovery.ReplacementMessage;
                    squashRecoveryUnavailableRoot = null;
                    return;
                }

                ClearActiveSquashRecovery();
                squashRecoveryUnavailableRoot = root;
                return;
            }

            if (state.Rebase is { } rebase
                && activeSquashPlan is { } plan
                && string.Equals(root, plan.RootPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(rebase.Branch, plan.Branch, StringComparison.Ordinal)
                && string.Equals(rebase.OriginalBranchTip, plan.ExpectedHeadId, StringComparison.OrdinalIgnoreCase))
            {
                squashRecoveryUnavailableRoot = null;
                return;
            }

            ClearActiveSquashRecovery();
            return;
        }

        if (state.Kind == GitOperationKind.None)
        {
            ClearActiveSquashRecovery();
            return;
        }

        ClearActiveSquashRecovery();
    }

    private void ClearActiveSquashRecovery()
    {
        activeSquashPlan = null;
        activeSquashMessage = null;
        squashRecoveryUnavailableRoot = null;
    }
}
