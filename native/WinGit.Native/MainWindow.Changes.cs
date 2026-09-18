using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private CancellationTokenSource? amendMessageLoadCancellation;
    private long amendMessageLoadGeneration;
    private bool amendMessageLoadInProgress;
    private AmendMessageDraft? amendMessageDraft;

    private sealed record AmendMessageDraft(
        string Root,
        string HeadId,
        string OriginalSummary,
        string OriginalDescription,
        string LoadedSummary,
        string LoadedDescription);

    private async void StageButton_Click(object sender, RoutedEventArgs e)
    {
        await RunFileMutationAsync(stage: true);
    }

    private async void StageAllButton_Click(object sender, RoutedEventArgs e)
    {
        await RunFileMutationAsync(stage: true, allVisible: true);
    }

    private async void UnstageButton_Click(object sender, RoutedEventArgs e)
    {
        await RunFileMutationAsync(stage: false);
    }

    private async void UnstageAllButton_Click(object sender, RoutedEventArgs e)
    {
        await RunFileMutationAsync(stage: false, allVisible: true);
    }

    private async void CommitButton_Click(object sender, RoutedEventArgs e)
    {
        var summary = CommitSummaryBox.Text.Trim();
        var description = string.IsNullOrWhiteSpace(CommitDescriptionBox.Text)
            ? null
            : CommitDescriptionBox.Text.Trim();
        var coAuthors = (CoAuthorsBox.Text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        await RunCommitMutationAsync(
            summary,
            description,
            coAuthors,
            SignOffCheckBox.IsChecked == true,
            AmendCheckBox.IsChecked == true,
            AmendCheckBox.IsChecked == true ? currentStatus?.HeadId : null);
    }

    private void CommitSummaryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateMutationButtons();
    }

    private async void AmendCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (AmendCheckBox.IsChecked == true)
        {
            if (currentStatus is null || currentStatus.IsUnborn || string.IsNullOrWhiteSpace(currentStatus.HeadId))
            {
                AmendCheckBox.IsChecked = false;
                ResetAmendMessageState();
                ShowError(
                    "Amend is unavailable",
                    new InvalidOperationException("There is no current HEAD commit to amend."));
                UpdateMutationButtons();
                return;
            }

            CancelAmendMessageLoad();
            amendMessageDraft = null;
            CommitIdentityText.Text = "This replaces the current HEAD commit.";
            UpdateMutationButtons();

            var root = repositoryRoot;
            var headId = currentStatus.HeadId;
            var originalSummary = CommitSummaryBox.Text;
            var originalDescription = CommitDescriptionBox.Text;
            if (root is not null
                && string.IsNullOrEmpty(originalSummary)
                && string.IsNullOrEmpty(originalDescription))
            {
                await LoadAmendMessageAsync(
                    root,
                    headId,
                    originalSummary,
                    originalDescription);
            }
        }
        else
        {
            ResetAmendMessageState(restoreOriginalDraft: true);
            CommitIdentityText.Text = string.Empty;
        }

        UpdateMutationButtons();
    }

    private void UpdateMutationButtons()
    {
        UpdateAmendCommitPresentation();
        if (StageButton is null)
        {
            return;
        }

        var canInteract = repositoryRoot is not null
            && !mutationInProgress
            && !BusyRing.IsActive
            && ChangesWorkspace.Visibility == Visibility.Visible;
        var stagedSelectionCount = StagedChangesList?.SelectedItems.Count ?? 0;
        var unstagedSelectionCount = UnstagedChangesList?.SelectedItems.Count ?? 0;
        StageButton.IsEnabled = canInteract && unstagedSelectionCount > 0;
        UnstageButton.IsEnabled = canInteract && stagedSelectionCount > 0;
        var hasFilter = !string.IsNullOrWhiteSpace(ChangesFilterBox?.Text);
        StageAllButton.Content = hasFilter ? "Stage filtered" : "Stage all";
        UnstageAllButton.Content = hasFilter ? "Unstage filtered" : "Unstage all";
        AutomationProperties.SetName(
            StageAllButton,
            hasFilter ? "Stage all filtered files" : "Stage all files");
        AutomationProperties.SetName(
            UnstageAllButton,
            hasFilter ? "Unstage all filtered files" : "Unstage all files");
        StageAllButton.IsEnabled = canInteract && unstagedChangeRows.Count > 0;
        UnstageAllButton.IsEnabled = canInteract && stagedChangeRows.Count > 0;

        var hasStagedChanges = currentStatus?.Changes.Any(change =>
            HasIndexChanges(change)) == true;
        var canAmendCurrentHead = AmendCheckBox.IsChecked == true
            && currentStatus is not null
            && !currentStatus.IsUnborn
            && !string.IsNullOrWhiteSpace(currentStatus.HeadId);
        CommitButton.IsEnabled = canInteract
            && (hasStagedChanges || canAmendCurrentHead)
            && activeGitOperationKind == GitOperationKind.None
            && !string.IsNullOrWhiteSpace(CommitSummaryBox.Text)
            && !amendMessageLoadInProgress;
        UpdatePartialSelectionControls();
        UpdateConflictEditorControls();
        UpdateCommitMessageGenerationControls();
    }

    private IReadOnlyList<ChangeRow> GetSelectedChangeRows(bool staged)
    {
        var selectedItems = staged
            ? StagedChangesList.SelectedItems
            : UnstagedChangesList.SelectedItems;
        return selectedItems.OfType<ChangeRow>().ToArray();
    }

    private IReadOnlyList<ChangeRow> GetVisibleChangeRows(bool staged) =>
        (staged ? stagedChangeRows : unstagedChangeRows).ToArray();

    private static IReadOnlyList<string> GetMutationPaths(
        IEnumerable<ChangeRow> rows,
        bool stage)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (seen.Add(row.Change.Path))
            {
                paths.Add(row.Change.Path);
            }

            if (!stage
                && !string.IsNullOrWhiteSpace(row.Change.OldPath)
                && seen.Add(row.Change.OldPath))
            {
                paths.Add(row.Change.OldPath);
            }
        }

        return paths;
    }

    private async Task RunFileMutationAsync(bool stage, bool allVisible = false)
    {
        if (mutationInProgress || repositoryRoot is null)
        {
            return;
        }

        var rows = allVisible
            ? GetVisibleChangeRows(staged: !stage)
            : GetSelectedChangeRows(staged: !stage);
        var paths = GetMutationPaths(rows, stage);
        if (paths.Count == 0)
        {
            return;
        }

        var root = repositoryRoot;
        mutationInProgress = true;
        var operation = BeginOperation(stage
            ? $"Staging {paths.Count} file{(paths.Count == 1 ? string.Empty : "s")}…"
            : $"Unstaging {paths.Count} file{(paths.Count == 1 ? string.Empty : "s")}…");
        try
        {
            if (stage)
            {
                await repositoryService.StageFilesAsync(root, paths, operation.Token);
            }
            else
            {
                await repositoryService.UnstageFilesAsync(root, paths, operation.Token);
            }

            ErrorBar.IsOpen = false;
            StatusText.Text = stage ? "Files staged" : "Files unstaged";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            StatusText.Text = stage
                ? "Stage operation cancelled; refreshing repository…"
                : "Unstage operation cancelled; refreshing repository…";
        }
        catch (Exception exception)
        {
            ShowError(stage ? "Unable to stage files" : "Unable to unstage files", exception);
        }
        finally
        {
            try
            {
                await RefreshAfterMutationAsync(root);
            }
            catch (Exception refreshException)
            {
                ShowError("Unable to refresh repository after mutation", refreshException);
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
        }
    }

    private async Task RunCommitMutationAsync(
        string summary,
        string? description,
        IReadOnlyList<string> coAuthors,
        bool signOff,
        bool amend,
        string? expectedHeadId)
    {
        if (mutationInProgress || repositoryRoot is null || activeGitOperationKind != GitOperationKind.None)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            ShowError(
                "Commit summary is required",
                new ArgumentException("Enter a one-line summary before committing.", nameof(summary)));
            return;
        }

        var hasStagedChanges = currentStatus?.Changes.Any(change =>
            HasIndexChanges(change)) == true;
        if (!hasStagedChanges && !amend)
        {
            ShowError(
                "No staged changes",
                new InvalidOperationException("Stage at least one file before creating a commit."));
            return;
        }

        if (amend && (currentStatus is null
            || currentStatus.IsUnborn
            || string.IsNullOrWhiteSpace(currentStatus.HeadId)))
        {
            ShowError(
                "Amend is unavailable",
                new InvalidOperationException("There is no current HEAD commit to amend."));
            return;
        }

        List<CommitTrailer> trailers;
        try
        {
            trailers = new List<CommitTrailer>(coAuthors.Count);
            foreach (var coAuthor in coAuthors)
            {
                trailers.Add(GitRepositoryService.ParseCoAuthorTrailer(coAuthor));
            }
        }
        catch (ArgumentException exception)
        {
            ShowError("Co-author format is invalid", exception);
            return;
        }

        var root = repositoryRoot;
        var selectedStagedPaths = StagedChangesList.SelectedItems
            .OfType<ChangeRow>()
            .Select(row => row.Path)
            .ToArray();
        var selectedUnstagedPaths = UnstagedChangesList.SelectedItems
            .OfType<ChangeRow>()
            .Select(row => row.Path)
            .ToArray();
        mutationInProgress = true;
        var operation = BeginOperation(amend ? "Checking Git identity for amend…" : "Checking Git identity…");
        var commitSucceeded = false;
        try
        {
            var identity = await repositoryService.GetCommitIdentityAsync(root, operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(identity.Name) || string.IsNullOrWhiteSpace(identity.Email))
            {
                var identityError = new InvalidOperationException(
                    "Git identity is missing. Configure user.name and user.email, then try again. "
                    + "For example: git config --global user.name \"Your Name\" and "
                    + "git config --global user.email \"you@example.com\".");
                ShowError("Git identity is missing", identityError);
                return;
            }

            CommitIdentityText.Text = $"Committing as {identity.Name} <{identity.Email}>";
            StatusText.Text = amend ? "Amending commit…" : "Creating commit…";
            var commitId = await repositoryService.CommitAsync(
                root,
                summary,
                description,
                amend,
                operation.Token,
                expectedHeadId,
                trailers,
                signOff);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var shortId = commitId.Length > 7 ? commitId[..7] : commitId;
            ErrorBar.IsOpen = false;
            StatusText.Text = $"Created commit {shortId}";
            ResetAmendMessageState();
            CommitSummaryBox.Text = string.Empty;
            CommitDescriptionBox.Text = string.Empty;
            CoAuthorsBox.Text = string.Empty;
            SignOffCheckBox.IsChecked = false;
            AmendCheckBox.IsChecked = false;
            CommitIdentityText.Text = $"Committed {shortId}";
            commitSucceeded = true;
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            if (IsCurrent(operation.Generation, operation.Token)
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "Commit operation cancelled; refreshing repository…";
            }
        }
        catch (CommitMessageSnapshotStaleException exception)
        {
            if (IsCurrent(operation.Generation, operation.Token)
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Repository changed", exception);
            }
        }
        catch (CommitHookFailureException exception)
        {
            // A hook rejection is a clear failure state, never a success: the
            // draft, selection, and amend choice are preserved (only a success
            // clears them), the refresh in finally picks up any files the hook
            // itself changed without overwriting them, and the next attempt
            // reruns hooks through the same guarded path. Duplicate
            // submissions stay blocked by mutationInProgress until that
            // refresh completes.
            if (IsCurrent(operation.Generation, operation.Token)
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                ShowError(amend ? "Amend hook failed" : "Commit hook failed", exception);
                StatusText.Text = amend
                    ? "The amend hook failed; fix the problem, then try again. The draft was kept."
                    : "The commit hook failed; fix the problem, then try again. The draft was kept.";
            }
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token)
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Unable to create commit", exception);
            }
        }
        finally
        {
            try
            {
                await RefreshAfterMutationAsync(root);
            }
            catch (Exception refreshException)
            {
                if (string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    ShowError("Unable to refresh repository after commit", refreshException);
                }
            }

            if (!commitSucceeded
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                RestoreCommitSelection(selectedStagedPaths, selectedUnstagedPaths);
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
        }
    }

    private async Task RefreshAfterMutationAsync(string expectedRoot)
    {
        if (repositoryRoot is null
            || !string.Equals(repositoryRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // RefreshRepositoryAsync begins a new operation and therefore always
        // uses a fresh token after the mutation's success, failure, or cancel.
        await RefreshRepositoryAsync();
        await WaitForLatestOperationAsync();
    }

    private void RestoreCommitSelection(
        IReadOnlyList<string> stagedPaths,
        IReadOnlyList<string> unstagedPaths)
    {
        if ((stagedPaths.Count == 0 && unstagedPaths.Count == 0)
            || StagedChangesList is null
            || UnstagedChangesList is null)
        {
            return;
        }

        var staged = new HashSet<string>(stagedPaths, StringComparer.OrdinalIgnoreCase);
        var unstaged = new HashSet<string>(unstagedPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var row in stagedChangeRows)
        {
            if (staged.Contains(row.Path))
            {
                StagedChangesList.SelectedItems.Add(row);
            }
        }

        foreach (var row in unstagedChangeRows)
        {
            if (unstaged.Contains(row.Path))
            {
                UnstagedChangesList.SelectedItems.Add(row);
            }
        }
    }

    private static bool HasIndexChanges(FileChange change) =>
        !string.IsNullOrWhiteSpace(change.IndexStatus)
        && !string.Equals(change.IndexStatus, "?", StringComparison.Ordinal)
        && !string.Equals(change.IndexStatus, "!", StringComparison.Ordinal);

    private void UpdateAmendCommitPresentation()
    {
        if (CommitButton is null)
        {
            return;
        }

        var text = AmendCheckBox.IsChecked == true
            ? "Amend previous commit"
            : "Commit staged changes";
        CommitButton.Content = text;
        AutomationProperties.SetName(CommitButton, text);
    }

    private async Task LoadAmendMessageAsync(
        string root,
        string headId,
        string originalSummary,
        string originalDescription)
    {
        CancelAmendMessageLoad();
        var generation = amendMessageLoadGeneration;
        using var cancellation = new CancellationTokenSource();
        amendMessageLoadCancellation = cancellation;
        amendMessageLoadInProgress = true;
        UpdateMutationButtons();
        StatusText.Text = "Loading previous commit message…";

        try
        {
            var message = await repositoryService.GetCommitMessageAsync(
                root,
                headId,
                cancellation.Token);
            if (!IsAmendMessageCurrent(root, headId, generation, cancellation.Token)
                || !string.Equals(CommitSummaryBox.Text, originalSummary, StringComparison.Ordinal)
                || !string.Equals(CommitDescriptionBox.Text, originalDescription, StringComparison.Ordinal))
            {
                return;
            }

            CommitSummaryBox.Text = message.Summary;
            CommitDescriptionBox.Text = message.Description;
            amendMessageDraft = new AmendMessageDraft(
                root,
                headId,
                originalSummary,
                originalDescription,
                CommitSummaryBox.Text,
                CommitDescriptionBox.Text);
            ErrorBar.IsOpen = false;
            StatusText.Text = "Previous commit message loaded";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A repository switch, HEAD change, or explicit toggle owns the UI now.
        }
        catch (Exception exception)
        {
            if (IsAmendMessageCurrent(root, headId, generation, cancellation.Token))
            {
                ShowError("Unable to load previous commit message", exception);
            }
        }
        finally
        {
            if (ReferenceEquals(amendMessageLoadCancellation, cancellation))
            {
                amendMessageLoadCancellation = null;
                amendMessageLoadInProgress = false;
                UpdateMutationButtons();
            }
        }
    }

    private bool IsAmendMessageCurrent(
        string root,
        string headId,
        long generation,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && generation == amendMessageLoadGeneration
        && AmendCheckBox.IsChecked == true
        && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
        && string.Equals(currentStatus?.HeadId, headId, StringComparison.OrdinalIgnoreCase);

    private void CancelAmendMessageLoad()
    {
        amendMessageLoadGeneration++;
        var cancellation = amendMessageLoadCancellation;
        amendMessageLoadCancellation = null;
        amendMessageLoadInProgress = false;
        cancellation?.Cancel();
    }

    private void ResetAmendMessageState(bool restoreOriginalDraft = false)
    {
        if (restoreOriginalDraft
            && amendMessageDraft is { } draft
            && string.Equals(CommitSummaryBox.Text, draft.LoadedSummary, StringComparison.Ordinal)
            && string.Equals(CommitDescriptionBox.Text, draft.LoadedDescription, StringComparison.Ordinal))
        {
            CommitSummaryBox.Text = draft.OriginalSummary;
            CommitDescriptionBox.Text = draft.OriginalDescription;
        }

        CancelAmendMessageLoad();
        amendMessageDraft = null;
    }
}
