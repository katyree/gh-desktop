using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<HistoryComparisonBranchRow> historyComparisonBranchRows = [];
    private readonly ObservableCollection<HistoryComparisonBranchRow> filteredHistoryComparisonBranchRows = [];
    private BranchComparisonSnapshot? historyComparisonSnapshot;
    private CommitSelectionSnapshot? historyCommitSelectionSnapshot;
    private HistoryComparisonBranchRow? selectedHistoryComparisonBranch;
    private string? historyComparisonReference;
    private ComparisonMode historyComparisonMode = ComparisonMode.Behind;
    private bool historyComparisonBranchesLoaded;
    private bool historyComparisonActive;
    private bool suppressHistoryComparisonSelection;
    private bool suppressHistorySelection;

    private void InitializeHistoryComparison()
    {
        HistoryComparisonBranchList.ItemsSource = filteredHistoryComparisonBranchRows;
        UpdateHistoryComparisonControls();
    }

    private async Task LoadHistoryComparisonBranchesAsync()
    {
        if (repositoryRoot is null)
        {
            return;
        }

        var root = repositoryRoot;
        var operation = BeginOperation("Loading comparison branches…");
        try
        {
            var branches = await repositoryService.GetBranchesAsync(root, operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                || currentWorkspace != "history")
            {
                return;
            }

            var selectedReference = selectedHistoryComparisonBranch?.Reference;
            historyComparisonBranchRows.Clear();
            foreach (var branch in branches)
            {
                historyComparisonBranchRows.Add(new HistoryComparisonBranchRow(branch));
            }

            selectedHistoryComparisonBranch = historyComparisonBranchRows.FirstOrDefault(row =>
                string.Equals(row.Reference, selectedReference, StringComparison.Ordinal));

            historyComparisonBranchesLoaded = true;
            ApplyHistoryComparisonBranchFilter();
            HistoryComparisonBranchStatusText.Text = historyComparisonBranchRows.Count == 0
                ? "No local branches are available to compare."
                : $"{historyComparisonBranchRows.Count} local branch{(historyComparisonBranchRows.Count == 1 ? string.Empty : "es")} available.";
            StatusText.Text = $"Loaded {historyComparisonBranchRows.Count} comparison branches";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // A newer history operation owns the panel.
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("Unable to load comparison branches", exception);
                historyComparisonBranchesLoaded = false;
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }

    private void HistoryBranchFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyHistoryComparisonBranchFilter();
    }

    private void ApplyHistoryComparisonBranchFilter()
    {
        if (HistoryComparisonBranchList is null)
        {
            return;
        }

        var query = HistoryBranchFilterBox.Text.Trim();
        var rows = string.IsNullOrWhiteSpace(query)
            ? historyComparisonBranchRows.ToArray()
            : historyComparisonBranchRows
                .Where(row => row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        suppressHistoryComparisonSelection = true;
        try
        {
            filteredHistoryComparisonBranchRows.Clear();
            foreach (var row in rows)
            {
                filteredHistoryComparisonBranchRows.Add(row);
            }

            if (selectedHistoryComparisonBranch is not null
                && !rows.Contains(selectedHistoryComparisonBranch))
            {
                selectedHistoryComparisonBranch = null;
                HistoryComparisonBranchList.SelectedIndex = -1;
            }
            else if (selectedHistoryComparisonBranch is not null)
            {
                HistoryComparisonBranchList.SelectedItem = selectedHistoryComparisonBranch;
            }
        }
        finally
        {
            suppressHistoryComparisonSelection = false;
        }

        UpdateHistoryComparisonControls();
    }

    private async void HistoryComparisonBranchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressHistoryComparisonSelection)
        {
            return;
        }

        selectedHistoryComparisonBranch = HistoryComparisonBranchList.SelectedItem as HistoryComparisonBranchRow;
        UpdateHistoryComparisonControls();
        if (selectedHistoryComparisonBranch is not null)
        {
            latestOperationTask = LoadHistoryBranchComparisonAsync(
                selectedHistoryComparisonBranch.Reference,
                ComparisonMode.Behind);
            await latestOperationTask;
        }
    }

    private async void HistoryComparisonBehindButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(historyComparisonReference))
        {
            latestOperationTask = LoadHistoryBranchComparisonAsync(
                historyComparisonReference,
                ComparisonMode.Behind);
            await latestOperationTask;
        }
    }

    private async void HistoryComparisonAheadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(historyComparisonReference))
        {
            latestOperationTask = LoadHistoryBranchComparisonAsync(
                historyComparisonReference,
                ComparisonMode.Ahead);
            await latestOperationTask;
        }
    }

    private async void ClearHistoryComparisonButton_Click(object sender, RoutedEventArgs e)
    {
        if (BusyRing.IsActive || mutationInProgress || repositoryRoot is null)
        {
            return;
        }

        ClearHistoryComparisonState(clearHistoryRows: true, clearBranchSelection: true);
        latestOperationTask = LoadHistoryAsync();
        await latestOperationTask;
    }

    private async Task LoadHistoryBranchComparisonAsync(
        string comparisonReference,
        ComparisonMode comparisonMode)
    {
        if (repositoryRoot is null || currentWorkspace != "history")
        {
            return;
        }

        var root = repositoryRoot;
        historyComparisonActive = true;
        historyComparisonReference = comparisonReference;
        historyComparisonMode = comparisonMode;
        historyComparisonSnapshot = null;
        historyCommitSelectionSnapshot = null;
        historyCacheRoot = null;
        historyCacheHeadId = null;
        selectedCommit = null;
        selectedCommitFile = null;
        ClearHistoryCommitView("Loading branch comparison…", "Reading both branch tips and their commits from Git.");

        var operation = BeginOperation($"Comparing {comparisonReference}…");
        try
        {
            var snapshot = await repositoryService.CaptureBranchComparisonAsync(
                root,
                comparisonReference,
                comparisonMode,
                100,
                operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                || currentWorkspace != "history"
                || !historyComparisonActive
                || !string.Equals(historyComparisonReference, comparisonReference, StringComparison.Ordinal)
                || historyComparisonMode != comparisonMode)
            {
                return;
            }

            if (snapshot is null)
            {
                SetHistoryComparisonSummary(null);
                HistoryCommitText.Text = "Comparison unavailable";
                HistoryCommitSummaryText.Text = "The current branch has no commit to compare.";
                ShowHistoryDiffMessage("No comparison", "Choose a branch with a reachable commit.");
                StatusText.Text = "Comparison unavailable";
                return;
            }

            historyComparisonSnapshot = snapshot;
            commitRows.Clear();
            foreach (var commit in snapshot.Commits)
            {
                commitRows.Add(new CommitRow(commit));
            }

            ApplyHistoryComparisonBranchFilter();
            SetHistoryComparisonSummary(snapshot);
            ClearHistoryCommitSelection();
            HistoryCommitText.Text = commitRows.Count == 0
                ? "No commits on this side"
                : $"{ComparisonModeLabel(snapshot.Mode)} commits";
            HistoryCommitSummaryText.Text = commitRows.Count == 0
                ? "The selected branch has no commits unique to this side."
                : "Select one commit or several consecutive commits to inspect their files.";
            ShowHistoryDiffMessage(
                commitRows.Count == 0 ? "No comparison commits" : "Select a commit",
                commitRows.Count == 0
                    ? "Try the other comparison side or choose another branch."
                    : "Choose a commit and file to inspect its diff.");
            StatusText.Text = snapshot.CommitsTruncated
                ? $"Showing the first {commitRows.Count} {ComparisonModeLabel(snapshot.Mode).ToLowerInvariant()} commits"
                : $"Loaded {commitRows.Count} {ComparisonModeLabel(snapshot.Mode).ToLowerInvariant()} commits";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // A newer branch comparison owns the history panel.
        }
        catch (BranchComparisonSnapshotStaleException exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("Comparison changed", exception);
                ShowHistoryDiffMessage("Refresh required", exception.Message);
            }
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("Unable to compare branches", exception);
                ShowHistoryDiffMessage("Comparison unavailable", exception.Message);
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }

    private void SetHistoryComparisonSummary(BranchComparisonSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            HistoryComparisonSummaryText.Text = string.Empty;
            HistoryComparisonEndpointsText.Text = string.Empty;
            HistoryComparisonBehindButton.Content = "Behind";
            HistoryComparisonAheadButton.Content = "Ahead";
            HistoryComparisonBehindButton.IsChecked = false;
            HistoryComparisonAheadButton.IsChecked = false;
            HistoryComparisonTruncatedText.Text = string.Empty;
            return;
        }

        var baseBranchLabel = DisplayBranchName(snapshot.BaseBranch);
        var comparisonBranchLabel = DisplayHistoryReference(snapshot.ComparisonReference);
        HistoryComparisonSummaryText.Text =
            $"Comparing {baseBranchLabel} with {comparisonBranchLabel}";
        HistoryComparisonEndpointsText.Text =
            $"{baseBranchLabel} {ShortObjectId(snapshot.BaseHeadId)}  ·  "
            + $"{comparisonBranchLabel} {ShortObjectId(snapshot.ComparisonHeadId)}";
        HistoryComparisonBehindButton.Content = $"Behind  {snapshot.Behind}";
        HistoryComparisonAheadButton.Content = $"Ahead  {snapshot.Ahead}";
        HistoryComparisonBehindButton.IsChecked = snapshot.Mode == ComparisonMode.Behind;
        HistoryComparisonAheadButton.IsChecked = snapshot.Mode == ComparisonMode.Ahead;
        HistoryComparisonTruncatedText.Text = snapshot.CommitsTruncated
            ? "The commit list is limited to the first 100 results."
            : string.Empty;
    }

    private static string ComparisonModeLabel(ComparisonMode mode) => mode == ComparisonMode.Ahead
        ? "Ahead"
        : "Behind";

    private static string DisplayBranchName(string branch) => string.IsNullOrWhiteSpace(branch)
        ? "Detached HEAD"
        : branch;

    private static string DisplayHistoryReference(string reference)
    {
        const string localBranchPrefix = "refs/heads/";
        return reference.StartsWith(localBranchPrefix, StringComparison.Ordinal)
            ? reference[localBranchPrefix.Length..]
            : reference;
    }

    private void ClearHistoryCommitView(string title, string message)
    {
        suppressHistorySelection = true;
        try
        {
            commitRows.Clear();
            commitFileRows.Clear();
            historyDiffRows.Clear();
            HistoryList.SelectedItems.Clear();
            HistoryList.SelectedIndex = -1;
            HistoryFilesList.SelectedIndex = -1;
        }
        finally
        {
            suppressHistorySelection = false;
        }

        HistoryList.ItemsSource = commitRows;
        HistoryFilesList.ItemsSource = commitFileRows;
        HistoryDiffList.ItemsSource = historyDiffRows;
        ShowHistoryDiffMessage(title, message);
    }

    private void ClearHistoryCommitSelection()
    {
        suppressHistorySelection = true;
        try
        {
            HistoryList.SelectedItems.Clear();
            HistoryList.SelectedIndex = -1;
            HistoryFilesList.SelectedIndex = -1;
        }
        finally
        {
            suppressHistorySelection = false;
        }

        selectedCommit = null;
        selectedCommitFile = null;
        commitFileRows.Clear();
        historyDiffRows.Clear();
        HistoryFilesList.ItemsSource = commitFileRows;
        HistoryDiffList.ItemsSource = historyDiffRows;
        historyCommitSelectionSnapshot = null;
        HistorySelectionInfoText.Text = string.Empty;
        ShowHistoryOmittedCommitsButton.Visibility = Visibility.Collapsed;
        HistoryDiffImageView.Clear();
        HistoryDiffImageView.Visibility = Visibility.Collapsed;
        UpdateRepositoryCommandStates();
    }

    private void ClearHistoryComparisonState(bool clearHistoryRows, bool clearBranchSelection)
    {
        historyComparisonActive = false;
        historyComparisonReference = null;
        historyComparisonSnapshot = null;
        historyCommitSelectionSnapshot = null;
        selectedHistoryComparisonBranch = null;

        if (clearBranchSelection)
        {
            suppressHistoryComparisonSelection = true;
            try
            {
                HistoryComparisonBranchList.SelectedIndex = -1;
            }
            finally
            {
                suppressHistoryComparisonSelection = false;
            }
        }

        if (clearHistoryRows)
        {
            ClearHistoryCommitView("Select a commit", "Choose a commit and file to inspect its diff.");
            SetHistoryComparisonSummary(null);
            HistoryCommitText.Text = "Select a commit";
            HistoryCommitSummaryText.Text = "Choose a commit to inspect its files.";
        }

        HistorySelectionInfoText.Text = string.Empty;
        ShowHistoryOmittedCommitsButton.Visibility = Visibility.Collapsed;
        UpdateHistoryComparisonControls();
    }

    private void ResetHistoryComparisonState()
    {
        ClearHistoryComparisonState(clearHistoryRows: false, clearBranchSelection: true);
        historyComparisonBranchesLoaded = false;
        historyComparisonBranchRows.Clear();
        filteredHistoryComparisonBranchRows.Clear();
    }

    private void UpdateHistoryComparisonControls()
    {
        UpdateHistoryDetailVisibility();
        if (HistoryComparisonBranchList is null)
        {
            return;
        }

        var canBrowse = repositoryRoot is not null
            && currentWorkspace == "history"
            && !mutationInProgress
            && !BusyRing.IsActive;
        HistoryBranchFilterBox.IsEnabled = canBrowse;
        HistoryComparisonBranchList.IsEnabled = canBrowse;
        HistoryComparisonBehindButton.IsEnabled = canBrowse && historyComparisonActive;
        HistoryComparisonAheadButton.IsEnabled = canBrowse && historyComparisonActive;
        ClearHistoryComparisonButton.IsEnabled = canBrowse && historyComparisonActive;
        ShowHistoryOmittedCommitsButton.IsEnabled = canBrowse
            && historyCommitSelectionSnapshot?.OmittedSelectedCommitIds.Count > 0;
    }

    private void UpdateHistoryDetailVisibility()
    {
        if (HistoryComparisonDetailsPanel is not null)
        {
            HistoryComparisonDetailsPanel.Visibility = historyComparisonActive
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (HistorySelectionDetailsPanel is not null)
        {
            var hasSelectionDetails = !string.IsNullOrWhiteSpace(HistorySelectionInfoText.Text)
                || ShowHistoryOmittedCommitsButton.Visibility == Visibility.Visible;
            HistorySelectionDetailsPanel.Visibility = hasSelectionDetails
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private async void ShowHistoryOmittedCommitsButton_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = historyCommitSelectionSnapshot;
        if (snapshot is null || snapshot.OmittedSelectedCommitIds.Count == 0)
        {
            return;
        }

        var omitted = snapshot.OmittedSelectedCommitIds.Select(id =>
        {
            var row = commitRows.FirstOrDefault(candidate =>
                string.Equals(candidate.Commit.Id, id, StringComparison.OrdinalIgnoreCase));
            return row is null ? ShortObjectId(id) : $"{ShortObjectId(id)}  {row.Summary}";
        });
        var dialog = CreateDialog(
            "Some selected commits are omitted",
            "Close",
            new TextBlock
            {
                Text = "These selected commits are not part of the selected range's final commit history, so their changes are omitted from this combined diff:\n\n"
                    + string.Join("\n", omitted),
                TextWrapping = TextWrapping.Wrap,
            });
        dialog.SecondaryButtonText = string.Empty;
        dialog.DefaultButton = ContentDialogButton.Primary;
        await dialog.ShowAsync();
    }

    private bool IsHistorySelectionCurrent(IReadOnlyList<CommitRow> rows)
    {
        if (HistoryList.SelectedItems.Count != rows.Count)
        {
            return false;
        }

        var selectedIds = HistoryList.SelectedItems
            .OfType<CommitRow>()
            .Select(row => row.Commit.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.All(row => selectedIds.Contains(row.Commit.Id));
    }

    private async Task<bool> HandleHistoryCommitSelectionChangedAsync()
    {
        if (suppressHistorySelection)
        {
            return true;
        }

        var rows = HistoryList.SelectedItems.OfType<CommitRow>().ToArray();
        if (rows.Length <= 1)
        {
            historyCommitSelectionSnapshot = null;
            HistorySelectionInfoText.Text = string.Empty;
            ShowHistoryOmittedCommitsButton.Visibility = Visibility.Collapsed;
            UpdateHistoryDetailVisibility();
            return false;
        }

        selectedCommit = null;
        selectedCommitFile = null;
        commitFileRows.Clear();
        historyDiffRows.Clear();
        HistoryFilesList.SelectedIndex = -1;
        HistoryFilesList.ItemsSource = commitFileRows;
        HistoryDiffList.ItemsSource = historyDiffRows;
        ShowHistoryDiffMessage("Loading selected commits…", "Reading the combined range from Git.");
        UpdateRepositoryCommandStates();

        var root = repositoryRoot;
        if (root is null)
        {
            return true;
        }

        var visibleIndexes = rows.Select(row => commitRows.IndexOf(row)).Where(index => index >= 0).ToArray();
        var isContiguous = visibleIndexes.Length == rows.Length
            && visibleIndexes.Max() - visibleIndexes.Min() + 1 == rows.Length;
        var orderedIds = rows
            .OrderByDescending(row => commitRows.IndexOf(row))
            .Select(row => row.Commit.Id)
            .ToArray();
        var operation = BeginOperation("Loading selected commits…");
        try
        {
            if (historyComparisonSnapshot is not null)
            {
                await repositoryService.RevalidateBranchComparisonSnapshotAsync(
                    root,
                    historyComparisonSnapshot,
                    operation.Token);
            }

            var snapshot = await repositoryService.CaptureCommitSelectionAsync(
                root,
                orderedIds,
                isContiguous,
                1000,
                operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                || !IsHistorySelectionCurrent(rows))
            {
                return true;
            }

            historyCommitSelectionSnapshot = snapshot;
            foreach (var file in snapshot.ChangedFiles)
            {
                commitFileRows.Add(new CommitFileRow(file));
            }

            HistorySelectionInfoText.Text = snapshot.HasCombinedDiff
                ? $"{rows.Length} commits selected · oldest to newest · combined diff"
                : $"{rows.Length} commits selected · nonconsecutive selections keep their metadata but do not have a combined diff";
            ShowHistoryOmittedCommitsButton.Visibility = snapshot.OmittedSelectedCommitIds.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateHistoryDetailVisibility();
            HistoryCommitText.Text = $"{rows.Length} commits selected";
            HistoryCommitSummaryText.Text = snapshot.HasCombinedDiff
                ? $"{ShortObjectId(snapshot.FirstSelectedCommitId)} → {ShortObjectId(snapshot.LastSelectedCommitId)}"
                : "Select consecutive commits to inspect a combined file diff.";

            if (!snapshot.HasCombinedDiff)
            {
                ShowHistoryDiffMessage(
                    "Combined diff unavailable",
                    "The selected commits are not consecutive. Select a consecutive range to inspect one combined diff.");
            }
            else if (commitFileRows.Count > 0)
            {
                HistoryFilesList.SelectedIndex = 0;
            }
            else
            {
                ShowHistoryDiffMessage("No files changed", "Git did not report changed paths for this selection.");
            }

            StatusText.Text = snapshot.ChangedFilesTruncated
                ? $"Showing the first {commitFileRows.Count} changed files"
                : $"Loaded {commitFileRows.Count} changed files for the selection";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // A newer history selection owns the panel.
        }
        catch (CommitSelectionSnapshotStaleException exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("History selection changed", exception);
                ShowHistoryDiffMessage("Refresh required", exception.Message);
            }
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("Unable to load selected commits", exception);
                ShowHistoryDiffMessage("Selection unavailable", exception.Message);
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }

        return true;
    }

    private async Task LoadCommitSelectionDiffAsync(
        CommitSelectionSnapshot snapshot,
        CommitFileRow file)
    {
        if (repositoryRoot is null)
        {
            return;
        }

        var root = repositoryRoot;
        historyDiffRows.Clear();
        HistoryDiffList.ItemsSource = historyDiffRows;
        ShowHistoryDiffMessage("Loading combined diff…", "Reading the captured history range from Git.");
        var operation = BeginOperation($"Loading {file.Path}…");
        try
        {
            if (historyComparisonSnapshot is not null)
            {
                await repositoryService.RevalidateBranchComparisonSnapshotAsync(
                    root,
                    historyComparisonSnapshot,
                    operation.Token);
            }

            await repositoryService.RevalidateCommitSelectionAsync(root, snapshot, operation.Token);
            var diff = await repositoryService.GetCommitSelectionFileDiffAsync(
                root,
                snapshot,
                file.File,
                operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                || !ReferenceEquals(historyCommitSelectionSnapshot, snapshot)
                || !ReferenceEquals(selectedCommitFile, file))
            {
                return;
            }

            await ApplyDiffAsync(
                historyDiffRows,
                diff,
                HistoryDiffList,
                HistoryDiffImageView,
                HistoryDiffMessagePanel,
                HistoryDiffMessageTitle,
                HistoryDiffMessageText,
                operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                || !ReferenceEquals(historyCommitSelectionSnapshot, snapshot)
                || !ReferenceEquals(selectedCommitFile, file))
            {
                return;
            }

            StatusText.Text = $"Showing combined selection / {file.Path}";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // A newer history file selection owns the panel.
        }
        catch (CommitSelectionSnapshotStaleException exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("History selection changed", exception);
                ShowHistoryDiffMessage("Refresh required", exception.Message);
            }
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("Unable to load combined diff", exception);
                ShowHistoryDiffMessage("Combined diff unavailable", exception.Message);
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }

    private void UpdateHistorySelectionCommands()
    {
        selectedCommit = HistoryList.SelectedItems.Count == 1
            ? HistoryList.SelectedItems[0] as CommitRow
            : null;
        UpdateRepositoryCommandStates();
    }
}
