using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private async Task LoadHistoryAsync()
    {
        if (repositoryRoot is null)
        {
            return;
        }

        if (historyComparisonActive)
        {
            ClearHistoryComparisonState(clearHistoryRows: false, clearBranchSelection: true);
        }

        SetHistoryComparisonSummary(null);

        var root = repositoryRoot;
        var operation = BeginOperation("Loading history…", allowHistorySelection: true);
        try
        {
            var commits = await repositoryService.GetHistoryAsync(root, 100, operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || currentWorkspace != "history"
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            suppressHistorySelection = true;
            HistoryList.SelectedItems.Clear();
            HistoryList.SelectedIndex = -1;
            suppressHistorySelection = false;
            historyCommitSelectionSnapshot = null;
            HistorySelectionInfoText.Text = string.Empty;
            ShowHistoryOmittedCommitsButton.Visibility = Visibility.Collapsed;
            UpdateHistoryDetailVisibility();
            commitRows.Clear();
            commitFileRows.Clear();
            historyDiffRows.Clear();
            foreach (var commit in commits)
            {
                commitRows.Add(new CommitRow(commit));
            }

            historyCacheRoot = root;
            historyCacheHeadId = currentStatus?.HeadId;

            HistoryCommitText.Text = commitRows.Count == 0 ? "No commits yet" : "Select a commit";
            HistoryCommitSummaryText.Text = commitRows.Count == 0
                ? "This repository has no reachable commits."
                : "Choose a commit to inspect its files.";
            UpdateUndoHeadPresentation();
            StatusText.Text = $"Loaded {commitRows.Count} commits";
            UpdateHistoryDetailVisibility();
            if (commitRows.Count > 0)
            {
                HistoryList.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // The user navigated away or requested a newer operation.
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token)
                && currentWorkspace == "history"
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Unable to load history", exception);
                HistoryCommitText.Text = "History unavailable";
                HistoryCommitSummaryText.Text = exception.Message;
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }

    private async void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressHistorySelection)
        {
            return;
        }

        if (HistoryList.SelectedItems.Count > 1)
        {
            await HandleHistoryCommitSelectionChangedAsync();
            return;
        }

        historyCommitSelectionSnapshot = null;
        HistorySelectionInfoText.Text = string.Empty;
        ShowHistoryOmittedCommitsButton.Visibility = Visibility.Collapsed;
        UpdateHistoryDetailVisibility();
        selectedCommit = HistoryList.SelectedItem as CommitRow;
        selectedCommitFile = null;
        commitFileRows.Clear();
        historyDiffRows.Clear();
        HistoryFilesList.SelectedIndex = -1;
        HistoryDiffList.ItemsSource = historyDiffRows;

        if (selectedCommit is null)
        {
            HistoryCommitText.Text = "Select a commit";
            HistoryCommitSummaryText.Text = "Choose a commit to inspect its files.";
            ShowHistoryDiffMessage("Select a commit", "Choose a commit and file to inspect its diff.");
            StatusText.Text = "Select a commit to inspect its files.";
            UpdateRepositoryCommandStates();
            return;
        }

        HistoryCommitText.Text = selectedCommit.Summary;
        HistoryCommitSummaryText.Text = $"{selectedCommit.ShortId}  ·  {selectedCommit.Author}  ·  {selectedCommit.Date}";
        ShowHistoryDiffMessage("Loading commit files…", "Reading changed paths from Git.");
        latestOperationTask = LoadCommitFilesAsync(selectedCommit);
        await latestOperationTask;

        UpdateRepositoryCommandStates();
    }

    private async Task LoadCommitFilesAsync(CommitRow commit)
    {
        if (repositoryRoot is null)
        {
            return;
        }

        var root = repositoryRoot;
        var operation = BeginOperation("Loading commit files…", allowHistorySelection: true);
        try
        {
            var files = await repositoryService.GetCommitFilesAsync(root, commit.Commit.Id, operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || currentWorkspace != "history"
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                || !ReferenceEquals(selectedCommit, commit))
            {
                return;
            }

            commitFileRows.Clear();
            foreach (var file in files)
            {
                commitFileRows.Add(new CommitFileRow(file));
            }

            if (commitFileRows.Count > 0)
            {
                HistoryFilesList.SelectedIndex = 0;
            }
            else
            {
                StatusText.Text = "No files changed in this commit";
                ShowHistoryDiffMessage("No files in this commit", "Git did not report any changed paths.");
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // A newer commit selection owns the panel.
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token)
                && currentWorkspace == "history"
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                && ReferenceEquals(selectedCommit, commit))
            {
                ShowError("Unable to load commit files", exception);
                ShowHistoryDiffMessage("Commit files unavailable", exception.Message);
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }

    private async void HistoryFilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedCommitFile = HistoryFilesList.SelectedItem as CommitFileRow;
        if (selectedCommitFile is not null && historyCommitSelectionSnapshot is { } selection)
        {
            latestOperationTask = LoadCommitSelectionDiffAsync(selection, selectedCommitFile);
            await latestOperationTask;
        }
        else if (selectedCommit is not null && selectedCommitFile is not null)
        {
            latestOperationTask = LoadCommitDiffAsync(selectedCommit, selectedCommitFile);
            await latestOperationTask;
        }
        else if (selectedCommitFile is null && historyCommitSelectionSnapshot is null)
        {
            historyDiffRows.Clear();
            HistoryDiffList.ItemsSource = historyDiffRows;
            ShowHistoryDiffMessage("Select a file", "Choose a commit and file to inspect its diff.");
        }
    }

    private async Task LoadCommitDiffAsync(CommitRow commit, CommitFileRow file)
    {
        if (repositoryRoot is null)
        {
            return;
        }

        historyDiffRows.Clear();
        HistoryDiffList.ItemsSource = historyDiffRows;
        InvalidateTextDiffCache(history: true);
        ShowHistoryDiffMessage("Loading diff…", "Reading the selected path from Git.");
        var root = repositoryRoot;
        var operation = BeginOperation($"Loading {file.Path}…", allowHistorySelection: true);
        try
        {
            var diff = await repositoryService.GetCommitDiffAsync(
                root,
                commit.Commit.Id,
                file.File,
                operation.Token,
                hideWhitespaceChanges);
            if (!IsCurrent(operation.Generation, operation.Token)
                || currentWorkspace != "history"
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                || !ReferenceEquals(selectedCommit, commit)
                || !ReferenceEquals(selectedCommitFile, file))
            {
                return;
            }

            await ApplyDiffAsync(
                historyDiffRows,
                diff,
                HistoryDiffList,
                HistoryDiffSideBySideList,
                HistoryDiffImageView,
                HistoryDiffMessagePanel,
                HistoryDiffMessageTitle,
                HistoryDiffMessageText,
                operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || currentWorkspace != "history"
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                || !ReferenceEquals(selectedCommit, commit)
                || !ReferenceEquals(selectedCommitFile, file))
            {
                return;
            }

            StatusText.Text = $"Showing {commit.ShortId} / {file.Path}";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // This diff was superseded by a newer file selection.
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token)
                && currentWorkspace == "history"
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                && ReferenceEquals(selectedCommit, commit)
                && ReferenceEquals(selectedCommitFile, file))
            {
                ShowError("Unable to load commit diff", exception);
                ShowHistoryDiffMessage("Diff unavailable", exception.Message);
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }
}
