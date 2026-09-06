using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private bool historyOperationDialogOpen;

    private sealed record HistoryCommitOperationRequest(
        string Root,
        string Branch,
        string ExpectedHeadId,
        IReadOnlyList<CommitSummary> Commits);

    private void UpdateHistoryOperationControls()
    {
        if (HistoryCherryPickButton is null)
        {
            return;
        }

        var selectedRows = GetSelectedHistoryCommitRows();
        var canStart = repositoryRoot is not null
            && currentWorkspace == "history"
            && !historyOperationDialogOpen
            && !mutationInProgress
            && !BusyRing.IsActive
            && activeGitOperationKind == GitOperationKind.None
            && currentStatus is { IsDetached: false, IsUnborn: false }
            && !string.IsNullOrWhiteSpace(currentStatus.Branch)
            && !string.IsNullOrWhiteSpace(currentStatus.HeadId);

        HistoryCherryPickButton.Label = selectedRows.Length > 1
            ? $"Cherry-pick {selectedRows.Length} commits"
            : "Cherry-pick selected";
        HistoryCherryPickButton.IsEnabled = canStart
            && selectedRows.Length > 0
            && selectedRows.Length <= 1_000;

        // Reverting a comparison commit is meaningful only for commits ahead of
        // the current branch. In the normal History view every listed commit is
        // on the current branch, matching the Electron action's availability.
        var canRevert = canStart
            && selectedRows.Length == 1
            && (!historyComparisonActive || historyComparisonMode == ComparisonMode.Ahead);
        HistoryRevertButton.IsEnabled = canRevert;
        UpdateHistoryReorderControls();
        UpdateHistorySquashControls();
    }

    private CommitRow[] GetSelectedHistoryCommitRows()
    {
        if (HistoryList is null)
        {
            return [];
        }

        return HistoryList.SelectedItems
            .OfType<CommitRow>()
            .Where(row => commitRows.IndexOf(row) >= 0)
            .OrderByDescending(row => commitRows.IndexOf(row))
            .ToArray();
    }

    private HistoryCommitOperationRequest? CaptureHistoryCommitOperation(
        bool cherryPick)
    {
        if (repositoryRoot is null
            || currentWorkspace != "history"
            || mutationInProgress
            || BusyRing.IsActive
            || activeGitOperationKind != GitOperationKind.None
            || currentStatus is not { IsDetached: false, IsUnborn: false } status
            || string.IsNullOrWhiteSpace(status.Branch)
            || string.IsNullOrWhiteSpace(status.HeadId))
        {
            return null;
        }

        var selectedRows = GetSelectedHistoryCommitRows();
        if (selectedRows.Length == 0
            || (!cherryPick && selectedRows.Length != 1)
            || selectedRows.Length > 1_000)
        {
            return null;
        }

        return new HistoryCommitOperationRequest(
            repositoryRoot,
            status.Branch,
            status.HeadId,
            selectedRows.Select(row => row.Commit).ToArray());
    }

    private async void HistoryCherryPickButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await StartHistoryCommitOperationAsync(cherryPick: true);
    }

    private async void HistoryRevertButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await StartHistoryCommitOperationAsync(cherryPick: false);
    }

    private async Task StartHistoryCommitOperationAsync(bool cherryPick)
    {
        var request = CaptureHistoryCommitOperation(cherryPick);
        if (request is null)
        {
            return;
        }

        historyOperationDialogOpen = true;
        UpdateHistoryOperationControls();
        try
        {
            var operationName = cherryPick ? "Cherry-pick" : "Revert";
            var dialog = CreateDialog(
                cherryPick ? "Cherry-pick commits?" : "Revert commit?",
                operationName,
                new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = cherryPick
                                ? $"Apply {request.Commits.Count} captured commit{(request.Commits.Count == 1 ? string.Empty : "s")} to the current branch \"{request.Branch}\" at HEAD {ShortObjectId(request.ExpectedHeadId)}? Git will apply them from oldest to newest and may stop for conflicts."
                                : $"Create a new commit on the current branch \"{request.Branch}\" at HEAD {ShortObjectId(request.ExpectedHeadId)} that reverses the selected commit.",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = FormatHistoryOperationCommits(request.Commits),
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                });
            dialog.DefaultButton = ContentDialogButton.Primary;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!IsHistoryCommitOperationCurrent(request))
            {
                ShowError(
                    "History changed",
                    new InvalidOperationException("The current branch, HEAD, or selected commits changed while the confirmation was open. Refresh History and review the operation again."));
                return;
            }

            historyOperationDialogOpen = false;
            UpdateHistoryOperationControls();
            await RunHistoryCommitOperationAsync(request, cherryPick);
        }
        finally
        {
            historyOperationDialogOpen = false;
            UpdateHistoryOperationControls();
        }
    }

    private static string FormatHistoryOperationCommits(
        IReadOnlyList<CommitSummary> commits)
    {
        var visible = commits
            .Take(12)
            .Select(commit => $"{ShortObjectId(commit.Id)}  {commit.Summary}")
            .ToList();
        if (commits.Count > visible.Count)
        {
            visible.Add($"… and {commits.Count - visible.Count} more");
        }

        return string.Join(Environment.NewLine, visible);
    }

    private bool IsHistoryCommitOperationCurrent(
        HistoryCommitOperationRequest request)
    {
        if (repositoryRoot is null
            || currentWorkspace != "history"
            || activeGitOperationKind != GitOperationKind.None
            || currentStatus is not { IsDetached: false, IsUnborn: false } status
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

    private async Task RunHistoryCommitOperationAsync(
        HistoryCommitOperationRequest request,
        bool cherryPick)
    {
        if (!CanStartRepositoryWrite()
            || !IsHistoryCommitOperationCurrent(request))
        {
            return;
        }

        mutationInProgress = true;
        var operation = BeginOperation(cherryPick
            ? $"Cherry-picking {request.Commits.Count} commit{(request.Commits.Count == 1 ? string.Empty : "s")}…"
            : $"Reverting {ShortObjectId(request.Commits[0].Id)}…");
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
                && string.Equals(status.RootPath, request.Root, StringComparison.OrdinalIgnoreCase)
                && !status.IsDetached
                && !status.IsUnborn
                && string.Equals(status.Branch, request.Branch, StringComparison.Ordinal)
                && string.Equals(status.HeadId, request.ExpectedHeadId, StringComparison.OrdinalIgnoreCase)
                && mergeState.OperationKind == GitOperationKind.None;
            if (!repositoryStillMatches)
            {
                if (!operation.Token.IsCancellationRequested
                    && string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase))
                {
                    ShowError(
                        "Repository changed",
                        new InvalidOperationException("The current branch, HEAD, or an active Git operation changed before the requested action could run. Refresh History and review the commits again."));
                }

                return;
            }

            if (cherryPick)
            {
                var result = await repositoryService.CherryPickAsync(
                    request.Root,
                    request.Commits.Select(commit => commit.Id).ToArray(),
                    request.ExpectedHeadId,
                    operation.Token);
                ErrorBar.IsOpen = false;
                StatusText.Text = FormatCherryPickResult(result);
            }
            else
            {
                var result = await repositoryService.RevertCommitAsync(
                    request.Root,
                    request.Commits[0].Id,
                    request.ExpectedHeadId,
                    operation.Token);
                ErrorBar.IsOpen = false;
                StatusText.Text = FormatRevertResult(result);
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            StatusText.Text = $"{(cherryPick ? "Cherry-pick" : "Revert")} canceled; repository was refreshed. Check the operation panel for its current state.";
        }
        catch (Exception exception)
        {
            ShowError(cherryPick ? "Unable to cherry-pick commits" : "Unable to revert commit", exception);
        }
        finally
        {
            try
            {
                await RefreshAfterGitOperationAsync(request.Root);
            }
            catch (Exception refreshException)
            {
                ShowError("Unable to refresh repository after Git operation", refreshException);
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
        }
    }

    private static string FormatCherryPickResult(CherryPickOperationResult result) => result.Outcome switch
    {
        CherryPickOutcome.Completed => $"Cherry-pick completed at {ShortObjectId(result.HeadId)}",
        CherryPickOutcome.Conflicts => "Cherry-pick stopped with conflicts",
        CherryPickOutcome.InProgress => "Cherry-pick is still in progress",
        CherryPickOutcome.Skipped => "Cherry-pick commit skipped",
        CherryPickOutcome.Aborted => "Cherry-pick aborted",
        _ => "Cherry-pick finished",
    };

    private static string FormatRevertResult(RevertOperationResult result) => result.Outcome switch
    {
        RevertOutcome.Completed => $"Revert completed at {ShortObjectId(result.HeadId)}",
        RevertOutcome.Conflicts => "Revert stopped with conflicts",
        RevertOutcome.InProgress => "Revert is still in progress",
        RevertOutcome.Skipped => "Revert commit skipped",
        RevertOutcome.Aborted => "Revert aborted",
        _ => "Revert finished",
    };
}
