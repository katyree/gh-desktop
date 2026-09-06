using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<ReflogRow> reflogRows = [];
    private bool historyResetDialogOpen;

    private sealed record HistoryResetRequest(
        string Root,
        string Branch,
        bool IsDetached,
        string ExpectedHeadId,
        string TargetCommitId,
        string TargetSummary,
        string TargetSource,
        GitResetMode Mode,
        bool HasWorkingChanges,
        bool HasIndexChanges,
        bool FromReflog);

    private void UpdateHistoryResetControls()
    {
        if (HistoryResetButton is null || HistoryReflogButton is null)
        {
            return;
        }

        var canBrowse = repositoryRoot is not null
            && currentWorkspace == "history"
            && !diagnosticCaptureMode
            && !historyResetDialogOpen
            && !mutationInProgress
            && !BusyRing.IsActive
            && activeGitOperationKind == GitOperationKind.None
            && currentStatus is { IsUnborn: false } status
            && !string.IsNullOrWhiteSpace(status.HeadId);

        var selectedRows = GetSelectedHistoryCommitRows();
        HistoryResetButton.IsEnabled = canBrowse && selectedRows.Length == 1;
        HistoryReflogButton.IsEnabled = canBrowse;
    }

    private async void HistoryResetButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedRows = GetSelectedHistoryCommitRows();
        if (selectedRows.Length != 1)
        {
            return;
        }

        var request = CaptureHistoryResetRequest(
            selectedRows[0].Commit,
            targetSource: "Selected History commit",
            fromReflog: false);
        if (request is not null)
        {
            await ShowHistoryResetConfirmationAsync(request);
        }
    }

    private async void HistoryReflogButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowHeadReflogDialogAsync();
    }

    private HistoryResetRequest? CaptureHistoryResetRequest(
        CommitSummary target,
        string targetSource,
        bool fromReflog)
    {
        if (repositoryRoot is null
            || currentWorkspace != "history"
            || diagnosticCaptureMode
            || mutationInProgress
            || BusyRing.IsActive
            || activeGitOperationKind != GitOperationKind.None
            || currentStatus is not { IsUnborn: false } status
            || string.IsNullOrWhiteSpace(status.HeadId))
        {
            return null;
        }

        var hasWorkingChanges = status.Changes.Any(change =>
            !string.IsNullOrWhiteSpace(change.WorkTreeStatus));
        var hasIndexChanges = status.Changes.Any(HasIndexChanges);
        return new HistoryResetRequest(
            repositoryRoot,
            status.Branch,
            status.IsDetached,
            status.HeadId,
            target.Id,
            target.Summary,
            targetSource,
            GitResetMode.Mixed,
            hasWorkingChanges,
            hasIndexChanges,
            fromReflog);
    }

    private async Task ShowHistoryResetConfirmationAsync(HistoryResetRequest request)
    {
        if (!CanStartRepositoryWrite() || !IsHistoryResetRequestCurrent(request))
        {
            return;
        }

        historyResetDialogOpen = true;
        UpdateHistoryResetControls();
        try
        {
            var modeDescription = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)RootGrid.Resources["SecondaryTextStyle"],
            };
            var modeBox = new ComboBox
            {
                Header = "Reset mode",
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(modeBox, "Reset mode");
            modeBox.Items.Add(new ComboBoxItem
            {
                Content = "Soft — move HEAD only",
                Tag = GitResetMode.Soft,
            });
            modeBox.Items.Add(new ComboBoxItem
            {
                Content = "Mixed — keep files, reset index",
                Tag = GitResetMode.Mixed,
            });
            modeBox.Items.Add(new ComboBoxItem
            {
                Content = "Hard — discard tracked file changes",
                Tag = GitResetMode.Hard,
            });
            modeBox.SelectedIndex = 1;
            modeDescription.Text = DescribeResetMode(
                GitResetMode.Mixed,
                request.HasWorkingChanges,
                request.HasIndexChanges);
            modeBox.SelectionChanged += (_, _) =>
            {
                if (modeBox.SelectedItem is ComboBoxItem { Tag: GitResetMode mode })
                {
                    modeDescription.Text = DescribeResetMode(
                        mode,
                        request.HasWorkingChanges,
                        request.HasIndexChanges);
                }
            };

            var branchLabel = FormatBranchLabel(request.Branch, request.IsDetached);
            var content = new StackPanel
            {
                Spacing = 10,
                MaxWidth = 680,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Reset {branchLabel} in the repository below to the captured commit.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    CreateResetDetailTextBlock("Repository", request.Root),
                    CreateResetDetailTextBlock("Current HEAD", request.ExpectedHeadId),
                    CreateResetDetailTextBlock("Target", request.TargetSource),
                    CreateResetDetailTextBlock("Target commit", request.TargetCommitId),
                    CreateResetDetailTextBlock(
                        "Target summary",
                        string.IsNullOrWhiteSpace(request.TargetSummary)
                            ? "(no commit message)"
                            : request.TargetSummary),
                    modeBox,
                    modeDescription,
                    new TextBlock
                    {
                        Text = "A reflog can recover commits after a reset, but it cannot recover uncommitted file contents.",
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)RootGrid.Resources["SecondaryTextStyle"],
                    },
                },
            };

            var dialog = CreateDialog("Reset repository", "Reset", content);
            dialog.DefaultButton = ContentDialogButton.Primary;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!IsHistoryResetRequestCurrent(request))
            {
                ShowError(
                    "Reset stopped",
                    new InvalidOperationException(
                        "The branch, HEAD, or selected target changed while the confirmation was open. Refresh History and review the reset again."));
                return;
            }

            var mode = modeBox.SelectedItem is ComboBoxItem { Tag: GitResetMode selectedMode }
                ? selectedMode
                : GitResetMode.Mixed;
            historyResetDialogOpen = false;
            UpdateHistoryResetControls();
            await RunHistoryResetAsync(request with { Mode = mode });
        }
        finally
        {
            historyResetDialogOpen = false;
            UpdateHistoryResetControls();
        }
    }

    private async Task ShowHeadReflogDialogAsync()
    {
        if (!CanStartRepositoryWrite()
            || repositoryRoot is null
            || currentWorkspace != "history"
            || diagnosticCaptureMode
            || activeGitOperationKind != GitOperationKind.None
            || currentStatus is not { IsUnborn: false } status
            || string.IsNullOrWhiteSpace(status.HeadId))
        {
            return;
        }

        var root = repositoryRoot;
        var expectedHeadId = status.HeadId;
        var branch = status.Branch;
        var isDetached = status.IsDetached;
        var hasWorkingChanges = status.Changes.Any(change =>
            !string.IsNullOrWhiteSpace(change.WorkTreeStatus));
        var hasIndexChanges = status.Changes.Any(HasIndexChanges);

        historyResetDialogOpen = true;
        UpdateHistoryResetControls();
        try
        {
            var operation = BeginOperation("Loading HEAD reflog…");
            IReadOnlyList<ReflogEntry> entries;
            try
            {
                entries = await repositoryService.GetHeadReflogAsync(
                    root,
                    250,
                    operation.Token);
                if (!IsCurrent(operation.Generation, operation.Token)
                    || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                    || currentWorkspace != "history"
                    || !string.Equals(currentStatus?.HeadId, expectedHeadId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (IsCurrent(operation.Generation, operation.Token))
                {
                    ShowError("Unable to load HEAD reflog", exception);
                }

                return;
            }
            finally
            {
                EndOperation(operation.Generation);
            }

            reflogRows.Clear();
            foreach (var entry in entries)
            {
                reflogRows.Add(new ReflogRow(entry));
            }

            if (reflogRows.Count == 0)
            {
                StatusText.Text = "No HEAD reflog entries are available to recover.";
                return;
            }

            var list = new ListView
            {
                ItemTemplate = RootGrid.Resources["ReflogRowTemplate"] as DataTemplate,
                ItemContainerStyle = RootGrid.Resources["ListItemStyle"] as Style,
                ItemsSource = reflogRows,
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 390,
            };
            AutomationProperties.SetName(list, "HEAD reflog entries");

            var selectedDetails = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)RootGrid.Resources["SecondaryTextStyle"],
            };
            AutomationProperties.SetName(selectedDetails, "Selected reflog entry");
            list.SelectedIndex = 0;
            selectedDetails.Text = FormatReflogSelection((ReflogRow)list.SelectedItem!);

            var dialog = CreateDialog(
                "HEAD reflog",
                "Recover selected commit",
                new StackPanel
                {
                    Spacing = 10,
                    MaxWidth = 700,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Choose a recorded HEAD position to inspect or recover. Recovery uses the default Mixed reset, which keeps working files and resets the index.",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = "The reflog can recover commits after a reset, but it cannot recover uncommitted file contents.",
                            TextWrapping = TextWrapping.Wrap,
                            Style = (Style)RootGrid.Resources["SecondaryTextStyle"],
                        },
                        list,
                        selectedDetails,
                    },
                });
            dialog.DefaultButton = ContentDialogButton.Primary;
            dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is ReflogRow row)
                {
                    selectedDetails.Text = FormatReflogSelection(row);
                }

                dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || list.SelectedItem is not ReflogRow selectedRow)
            {
                return;
            }

            var request = new HistoryResetRequest(
                root,
                branch,
                isDetached,
                expectedHeadId,
                selectedRow.CommitId,
                selectedRow.Subject,
                $"HEAD reflog {selectedRow.Selector}",
                GitResetMode.Mixed,
                hasWorkingChanges,
                hasIndexChanges,
                FromReflog: true);
            await ShowHistoryResetConfirmationAsync(request);
        }
        finally
        {
            historyResetDialogOpen = false;
            UpdateHistoryResetControls();
        }
    }

    private async Task RunHistoryResetAsync(HistoryResetRequest request)
    {
        if (!CanStartRepositoryWrite()
            || !IsHistoryResetRequestCurrent(request))
        {
            return;
        }

        mutationInProgress = true;
        var operation = BeginOperation($"Resetting to {ShortObjectId(request.TargetCommitId)}…");
        ResetOperationResult? result = null;
        string? completionMessage = null;
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
                && !status.IsUnborn
                && string.Equals(status.HeadId, request.ExpectedHeadId, StringComparison.OrdinalIgnoreCase)
                && status.IsDetached == request.IsDetached
                && string.Equals(status.Branch, request.Branch, StringComparison.Ordinal)
                && mergeState.OperationKind == GitOperationKind.None
                && !mergeState.HasUnresolvedConflicts
                && (request.FromReflog || IsSelectedHistoryResetTargetCurrent(request.TargetCommitId));
            if (!repositoryStillMatches)
            {
                if (!operation.Token.IsCancellationRequested
                    && string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase))
                {
                    ShowError(
                        "Reset stopped",
                        new InvalidOperationException(
                            "The repository, branch, HEAD, or selected target changed before reset could run. Refresh History and review the reset again."));
                }

                return;
            }

            result = await repositoryService.ResetAsync(
                request.Root,
                request.Mode,
                request.TargetCommitId,
                request.ExpectedHeadId,
                operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ErrorBar.IsOpen = false;
            completionMessage = FormatResetResult(result);
            StatusText.Text = completionMessage;
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            completionMessage = "Reset cancelled; the repository was refreshed because it may have changed.";
            StatusText.Text = completionMessage;
        }
        catch (ResetOperationBlockedException exception)
        {
            ShowError("Reset unavailable", exception);
        }
        catch (Exception exception)
        {
            ShowError("Unable to reset repository", exception);
        }
        finally
        {
            try
            {
                await RefreshAfterMutationAsync(request.Root);
                if (string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase)
                    && currentWorkspace == "history")
                {
                    await ReloadHistoryAfterResetAsync(request.Root);
                }
            }
            catch (Exception refreshException)
            {
                ShowError("Unable to refresh repository after reset", refreshException);
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
            if (completionMessage is not null
                && !ErrorBar.IsOpen
                && string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = completionMessage;
            }
        }
    }

    private async Task ReloadHistoryAfterResetAsync(string expectedRoot)
    {
        if (repositoryRoot is null
            || !string.Equals(repositoryRoot, expectedRoot, StringComparison.OrdinalIgnoreCase)
            || currentWorkspace != "history")
        {
            return;
        }

        ResetHistoryComparisonState();
        ClearHistoryComparisonState(clearHistoryRows: true, clearBranchSelection: true);
        latestOperationTask = LoadHistoryComparisonBranchesAsync();
        await latestOperationTask;
        if (repositoryRoot is null
            || !string.Equals(repositoryRoot, expectedRoot, StringComparison.OrdinalIgnoreCase)
            || currentWorkspace != "history")
        {
            return;
        }

        latestOperationTask = LoadHistoryAsync();
        await latestOperationTask;
        await WaitForLatestOperationAsync();
    }

    private bool IsHistoryResetRequestCurrent(HistoryResetRequest request)
    {
        if (repositoryRoot is null
            || currentWorkspace != "history"
            || diagnosticCaptureMode
            || activeGitOperationKind != GitOperationKind.None
            || currentStatus is not { IsUnborn: false } status
            || !string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(status.RootPath, request.Root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(status.HeadId, request.ExpectedHeadId, StringComparison.OrdinalIgnoreCase)
            || status.IsDetached != request.IsDetached
            || !string.Equals(status.Branch, request.Branch, StringComparison.Ordinal))
        {
            return false;
        }

        return request.FromReflog || IsSelectedHistoryResetTargetCurrent(request.TargetCommitId);
    }

    private bool IsSelectedHistoryResetTargetCurrent(string targetCommitId) =>
        GetSelectedHistoryCommitRows() is [var selected]
        && string.Equals(selected.Commit.Id, targetCommitId, StringComparison.OrdinalIgnoreCase);

    private static TextBlock CreateResetDetailTextBlock(string label, string value) => new()
    {
        Text = $"{label}: {value}",
        TextWrapping = TextWrapping.Wrap,
        FontFamily = label is "Current HEAD" or "Target commit"
            ? new FontFamily("Cascadia Mono")
            : null,
    };

    private static string FormatBranchLabel(string branch, bool isDetached) =>
        isDetached
            ? "Detached HEAD"
            : string.IsNullOrWhiteSpace(branch) ? "the current branch" : $"branch \"{branch}\"";

    private static string DescribeResetMode(
        GitResetMode mode,
        bool hasWorkingChanges,
        bool hasIndexChanges) => mode switch
        {
            GitResetMode.Soft => "Move HEAD to the target and leave the index and working files unchanged.",
            GitResetMode.Mixed => hasIndexChanges
                ? "Move HEAD and reset the index to the target; keep working files as local changes. This is the default."
                : "Move HEAD and reset the index to the target; keep working files in place. This is the default.",
            GitResetMode.Hard => "Move HEAD, reset the index, and permanently discard tracked working-file changes. Git may also delete untracked paths that block tracked files; WinGit does not make a Recycle Bin backup.",
            _ => "Choose a reset mode.",
        };

    private static string FormatReflogSelection(ReflogRow row) =>
        $"Selected {row.Selector}: {row.CommitId} · {row.Subject}";

    private static string FormatResetResult(ResetOperationResult result) =>
        $"{result.Mode} reset completed at {ShortObjectId(result.CurrentHeadId)}";
}
