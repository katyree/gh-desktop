using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private bool historyReorderDialogOpen;

    private sealed record HistoryReorderRequest(
        string Root,
        string Branch,
        string ExpectedHeadId,
        IReadOnlyList<CommitSummary> Commits);

    private void UpdateHistoryReorderControls()
    {
        if (HistoryReorderButton is null)
        {
            return;
        }

        var selectedRows = GetSelectedHistoryCommitRows();
        var canStart = repositoryRoot is not null
            && currentWorkspace == "history"
            && !diagnosticCaptureMode
            && !historyComparisonActive
            && !historyReorderDialogOpen
            && !mutationInProgress
            && !BusyRing.IsActive
            && activeGitOperationKind == GitOperationKind.None
            && currentStatus is { IsDetached: false, IsUnborn: false } status
            && !string.IsNullOrWhiteSpace(status.Branch)
            && !string.IsNullOrWhiteSpace(status.HeadId)
            && status.Changes.Count == 0;

        HistoryReorderButton.Label = selectedRows.Length > 1
            ? $"Reorder {selectedRows.Length} commits"
            : "Reorder selected";
        HistoryReorderButton.IsEnabled = canStart && selectedRows.Length > 0;
    }

    private async void HistoryReorderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await StartHistoryReorderAsync();
    }

    private HistoryReorderRequest? CaptureHistoryReorderRequest()
    {
        if (repositoryRoot is null
            || currentWorkspace != "history"
            || diagnosticCaptureMode
            || historyComparisonActive
            || historyReorderDialogOpen
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
        if (selectedRows.Length == 0)
        {
            return null;
        }

        return new HistoryReorderRequest(
            repositoryRoot,
            status.Branch,
            status.HeadId,
            selectedRows.Select(row => row.Commit).ToArray());
    }

    private async Task StartHistoryReorderAsync()
    {
        var request = CaptureHistoryReorderRequest();
        if (request is null)
        {
            return;
        }

        historyReorderDialogOpen = true;
        UpdateHistoryReorderControls();
        try
        {
            var targetBox = CreateHistoryReorderTargetBox(request);
            var targetDialog = CreateDialog(
                "Choose reorder target",
                "Preview reorder",
                new StackPanel
                {
                    Spacing = 10,
                    MaxWidth = 680,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Move {request.Commits.Count} selected commit{(request.Commits.Count == 1 ? string.Empty : "s")} on \"{request.Branch}\". The selected commits stay in their current chronological order.",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = "Choose a commit to insert before, or move the selection to the newest end of the branch.",
                            TextWrapping = TextWrapping.Wrap,
                            Style = (Style)RootGrid.Resources["SecondaryTextStyle"],
                        },
                        targetBox,
                    },
                });
            targetDialog.DefaultButton = ContentDialogButton.Primary;
            AutomationProperties.SetName(targetDialog, "Choose a target for reordering history");
            if (await targetDialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!IsHistoryReorderRequestCurrent(request))
            {
                ShowHistoryReorderChangedError();
                return;
            }

            var beforeCommitId = targetBox.SelectedItem is ComboBoxItem { Tag: string target }
                && !string.IsNullOrWhiteSpace(target)
                ? target
                : null;

            var planOperation = BeginOperation("Preparing reorder preview…");
            ReorderPlan? plan = null;
            try
            {
                plan = await repositoryService.CaptureReorderPlanFromCurrentHistoryAsync(
                    request.Root,
                    request.Commits.Select(commit => commit.Id).ToArray(),
                    beforeCommitId,
                    planOperation.Token);
            }
            catch (OperationCanceledException) when (planOperation.Token.IsCancellationRequested)
            {
                if (planOperation.Generation == operationGeneration
                    && string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase))
                {
                    StatusText.Text = "Reorder preview canceled. Refresh History before trying again.";
                }

                return;
            }
            catch (ReorderOperationBlockedException exception)
            {
                if (IsCurrent(planOperation.Generation, planOperation.Token))
                {
                    ShowError("Reorder unavailable", exception);
                }

                return;
            }
            catch (Exception exception)
            {
                if (IsCurrent(planOperation.Generation, planOperation.Token))
                {
                    ShowError("Unable to prepare reorder", exception);
                }

                return;
            }
            finally
            {
                EndOperation(planOperation.Generation);
            }

            if (plan is null || !IsHistoryReorderRequestCurrent(request))
            {
                if (plan is not null)
                {
                    ShowHistoryReorderChangedError();
                }

                return;
            }

            var confirmation = CreateHistoryReorderConfirmation(plan);
            confirmation.DefaultButton = ContentDialogButton.Primary;
            AutomationProperties.SetName(confirmation, "Confirm history reorder");
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!IsHistoryReorderRequestCurrent(request))
            {
                ShowHistoryReorderChangedError();
                return;
            }

            await RunHistoryReorderAsync(request, plan);
        }
        finally
        {
            historyReorderDialogOpen = false;
            UpdateHistoryReorderControls();
        }
    }

    private ComboBox CreateHistoryReorderTargetBox(HistoryReorderRequest request)
    {
        var targetBox = new ComboBox
        {
            Header = "Insert selected commits before",
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(targetBox, "Reorder insertion target");

        targetBox.Items.Add(new ComboBoxItem
        {
            Content = "Move to newest (end)",
            Tag = string.Empty,
        });

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
                Content = $"Before {row.ShortId}  ·  {row.Summary}",
                Tag = row.Commit.Id,
            });
        }

        targetBox.SelectedIndex = 0;
        return targetBox;
    }

    private ContentDialog CreateHistoryReorderConfirmation(ReorderPlan plan)
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
                    Text = plan.BeforeCommitId is null
                        ? "The selected commits will move to the newest end of the branch."
                        : $"The selected commits will be inserted before {plan.BeforeCommitId}.",
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
                    Text = "Commit authors and messages stay with their commits, but replaying them changes commit IDs. Git may stop for conflicts; use the existing operation panel to inspect, resolve, continue, skip, or abort.",
                    TextWrapping = TextWrapping.Wrap,
                },
                CreateHistoryReorderPlanDetails("Selected commits (oldest to newest)", plan.SelectedCommits),
                CreateHistoryReorderPlanDetails("Replay order (oldest to newest)", plan.ReplayCommits),
            },
        };

        return CreateDialog("Reorder commits?", "Reorder commits", new ScrollViewer
        {
            Content = content,
            MaxHeight = 600,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
    }

    private static TextBlock CreateHistoryReorderPlanDetails(
        string title,
        IReadOnlyList<ReorderPlanCommit> commits)
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

    private bool IsHistoryReorderRequestCurrent(HistoryReorderRequest request)
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

    private void ShowHistoryReorderChangedError()
    {
        ShowError(
            "Reorder stopped",
            new InvalidOperationException(
                "The branch, HEAD, selected commits, or repository state changed. Refresh History and review the reorder again."));
    }

    private async Task RunHistoryReorderAsync(
        HistoryReorderRequest request,
        ReorderPlan plan)
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

        mutationInProgress = true;
        var operation = BeginOperation("Reordering commits…");
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
                && mergeState.HasUnresolvedConflicts == false
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
                    ShowHistoryReorderChangedError();
                }

                return;
            }

            var result = await repositoryService.ReorderAsync(
                request.Root,
                plan,
                operation.Token);
            ErrorBar.IsOpen = false;
            StatusText.Text = FormatHistoryReorderResult(result);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            StatusText.Text = "Reorder canceled; repository was refreshed. Check the operation panel for its current state.";
        }
        catch (ReorderOperationBlockedException exception)
        {
            ShowError("Reorder unavailable", exception);
        }
        catch (Exception exception)
        {
            ShowError("Unable to reorder commits", exception);
        }
        finally
        {
            try
            {
                await RefreshAfterGitOperationAsync(request.Root);
            }
            catch (Exception refreshException)
            {
                ShowError("Unable to refresh repository after reorder", refreshException);
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
        }
    }

    private static string FormatHistoryReorderResult(RebaseOperationResult result) => result.Outcome switch
    {
        RebaseOutcome.Completed => $"Reorder completed at {ShortObjectId(result.HeadId)}",
        RebaseOutcome.AlreadyUpToDate => "Reorder is already up to date",
        RebaseOutcome.Conflicts => "Reorder stopped with conflicts",
        RebaseOutcome.InProgress => "Reorder is still in progress",
        RebaseOutcome.Skipped => "Reorder commit skipped",
        RebaseOutcome.Aborted => "Reorder aborted",
        _ => "Reorder finished",
    };
}
