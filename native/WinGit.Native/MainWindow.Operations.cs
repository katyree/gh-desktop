using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private GitOperationKind activeGitOperationKind;
    private MergeOperationState? activeMergeOperationState;
    private RebaseOperationState? activeRebaseOperationState;
    private CherryPickOperationState? activeCherryPickOperationState;
    private RevertOperationState? activeRevertOperationState;
    private string? gitOperationRepositoryRoot;

    private sealed record BranchOperationRequest(
        string Root,
        string CurrentBranch,
        string SourceBranch,
        string ExpectedHeadId,
        string SourceHeadId);

    private sealed record GitOperationSnapshot(
        string Root,
        GitOperationKind Kind,
        string Branch,
        string CurrentHeadId,
        string? CurrentCommitId,
        IReadOnlyList<string> MergeHeadIds,
        IReadOnlyList<string> SequenceCommitIds,
        string? OriginalBranchTip,
        string? BaseBranchTip,
        int ConflictCount,
        bool HasUnstagedChanges);

    private sealed record GitOperationLoadState(
        GitOperationKind Kind,
        MergeOperationState? Merge,
        RebaseOperationState? Rebase,
        CherryPickOperationState? CherryPick,
        RevertOperationState? Revert,
        string? Root,
        SquashRecoveryReadResult? SquashRecovery);

    private async Task LoadGitOperationStateAsync(
        string root,
        long generation,
        CancellationToken cancellationToken)
    {
        var state = await ReadGitOperationStateAsync(root, cancellationToken);
        if (!IsCurrent(generation, cancellationToken)
            || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyGitOperationState(state);
    }

    private async Task<GitOperationLoadState> ReadGitOperationStateAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var mergeState = await repositoryService.GetMergeStateAsync(root, cancellationToken);
        RebaseOperationState? rebaseState = null;
        if (mergeState.OperationKind == GitOperationKind.Rebase)
        {
            rebaseState = await repositoryService.GetRebaseStateAsync(root, cancellationToken);
        }

        CherryPickOperationState? cherryPickState = null;
        RevertOperationState? revertState = null;
        if (mergeState.OperationKind is GitOperationKind.CherryPick or GitOperationKind.Sequencer)
        {
            cherryPickState = await repositoryService.GetCherryPickStateAsync(root, cancellationToken);
        }

        if (mergeState.OperationKind is GitOperationKind.Revert or GitOperationKind.Sequencer)
        {
            revertState = await repositoryService.GetRevertStateAsync(root, cancellationToken);
        }

        var effectiveOperationKind = rebaseState?.OperationKind
            ?? (cherryPickState?.OperationKind == GitOperationKind.CherryPick
                ? GitOperationKind.CherryPick
                : revertState?.OperationKind == GitOperationKind.Revert
                    ? GitOperationKind.Revert
                    : mergeState.OperationKind);

        SquashRecoveryReadResult? squashRecovery = null;
        if (effectiveOperationKind == GitOperationKind.Rebase)
        {
            squashRecovery = await repositoryService.GetSquashRecoveryAsync(
                root,
                cancellationToken);
        }

        return new GitOperationLoadState(
            effectiveOperationKind,
            effectiveOperationKind == GitOperationKind.None ? null : mergeState,
            rebaseState,
            cherryPickState?.OperationKind == GitOperationKind.CherryPick ? cherryPickState : null,
            revertState?.OperationKind == GitOperationKind.Revert ? revertState : null,
            effectiveOperationKind == GitOperationKind.None ? null : root,
            squashRecovery);
    }

    private void ApplyGitOperationState(GitOperationLoadState state)
    {
        activeGitOperationKind = state.Kind;
        activeMergeOperationState = state.Merge;
        activeRebaseOperationState = state.Rebase;
        activeCherryPickOperationState = state.CherryPick;
        activeRevertOperationState = state.Revert;
        gitOperationRepositoryRoot = state.Root;
        var currentRoot = repositoryRoot;
        if (currentRoot is not null
            && activeSquashPlan is { } existingPlan
            && !string.Equals(currentRoot, existingPlan.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            activeSquashPlan = null;
            activeSquashMessage = null;
        }

        if (state.SquashRecovery?.Recovery is { } recovery)
        {
            activeSquashPlan = recovery.Plan;
            activeSquashMessage = recovery.ReplacementMessage;
            squashRecoveryUnavailableRoot = null;
        }
        else if (state.SquashRecovery?.IsUnavailable == true
            && currentRoot is not null)
        {
            activeSquashPlan = null;
            activeSquashMessage = null;
            squashRecoveryUnavailableRoot = currentRoot;
        }
        else if (state.Kind != GitOperationKind.Rebase
            || state.SquashRecovery?.IsUnavailable != true)
        {
            squashRecoveryUnavailableRoot = null;
        }
        ReconcileActiveSquashRecovery(
            repositoryRoot ?? state.Root ?? string.Empty,
            state);
        UpdateGitOperationPresentation();
    }

    private void ClearGitOperationState()
    {
        activeGitOperationKind = GitOperationKind.None;
        activeMergeOperationState = null;
        activeRebaseOperationState = null;
        activeCherryPickOperationState = null;
        activeRevertOperationState = null;
        gitOperationRepositoryRoot = null;
        UpdateGitOperationPresentation();
    }

    private void UpdateGitOperationControls()
    {
        if (MergeBranchButton is null)
        {
            return;
        }

        var canInteract = repositoryRoot is not null
            && !mutationInProgress
            && !BusyRing.IsActive;
        var canStartBranchOperation = canInteract
            && currentWorkspace == "branches"
            && selectedBranch is not null
            && !selectedBranch.Branch.IsCurrent
            && currentStatus is not null
            && !currentStatus.IsDetached
            && !currentStatus.IsUnborn
            && !string.IsNullOrWhiteSpace(currentStatus.Branch)
            && !string.IsNullOrWhiteSpace(currentStatus.HeadId)
            && activeGitOperationKind == GitOperationKind.None;

        MergeBranchButton.IsEnabled = canStartBranchOperation;
        RebaseBranchButton.IsEnabled = canStartBranchOperation;

        var hasRepositoryOperation = repositoryRoot is not null
            && string.Equals(repositoryRoot, gitOperationRepositoryRoot, StringComparison.OrdinalIgnoreCase)
            && activeGitOperationKind != GitOperationKind.None;
        var canOperate = hasRepositoryOperation
            && !mutationInProgress
            && !BusyRing.IsActive;
        RefreshGitOperationButton.IsEnabled = canOperate;

        var isMerge = hasRepositoryOperation && activeGitOperationKind == GitOperationKind.Merge;
        var isRebase = hasRepositoryOperation && activeGitOperationKind == GitOperationKind.Rebase;
        var isCherryPick = hasRepositoryOperation && activeGitOperationKind == GitOperationKind.CherryPick;
        var isRevert = hasRepositoryOperation && activeGitOperationKind == GitOperationKind.Revert;
        ContinueGitOperationButton.IsEnabled = canOperate
            && ((isMerge && activeMergeOperationState is { IsMergeInProgress: true, HasUnresolvedConflicts: false })
                || (isRebase && activeRebaseOperationState is { IsInProgress: true, HasUnresolvedConflicts: false, HasUnstagedChanges: false })
                || (isCherryPick && activeCherryPickOperationState is { IsInProgress: true, HasUnresolvedConflicts: false, HasUnstagedChanges: false })
                || (isRevert && activeRevertOperationState is { IsInProgress: true, HasUnresolvedConflicts: false, HasUnstagedChanges: false }));
        SkipGitOperationButton.IsEnabled = canOperate
            && ((isRebase && activeRebaseOperationState?.IsInProgress == true)
                || (isCherryPick && activeCherryPickOperationState?.IsInProgress == true)
                || (isRevert && activeRevertOperationState?.IsInProgress == true));
        AbortGitOperationButton.IsEnabled = canOperate && (isMerge || isRebase || isCherryPick || isRevert);
    }

    private void UpdateGitOperationPresentation()
    {
        if (GitOperationPanel is null)
        {
            return;
        }

        var hasRepositoryOperation = repositoryRoot is not null
            && string.Equals(repositoryRoot, gitOperationRepositoryRoot, StringComparison.OrdinalIgnoreCase)
            && activeGitOperationKind != GitOperationKind.None;
        GitOperationPanel.Visibility = hasRepositoryOperation
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!hasRepositoryOperation)
        {
            GitOperationTitle.Text = "Git operation in progress";
            GitOperationProgressText.Text = string.Empty;
            GitOperationMessageText.Text = string.Empty;
            GitOperationConflictList.ItemsSource = Array.Empty<FileChange>();
            GitOperationConflictList.Visibility = Visibility.Collapsed;
            GitOperationConflictHint.Visibility = Visibility.Collapsed;
            ContinueGitOperationButton.Visibility = Visibility.Collapsed;
            SkipGitOperationButton.Visibility = Visibility.Collapsed;
            AbortGitOperationButton.Visibility = Visibility.Collapsed;
            UpdateGitOperationControls();
            return;
        }

        var isMerge = activeGitOperationKind == GitOperationKind.Merge;
        var isRebase = activeGitOperationKind == GitOperationKind.Rebase;
        var isCherryPick = activeGitOperationKind == GitOperationKind.CherryPick;
        var isRevert = activeGitOperationKind == GitOperationKind.Revert;
        GitOperationTitle.Text = activeGitOperationKind switch
        {
            GitOperationKind.Merge => "Merge in progress",
            GitOperationKind.Rebase => "Rebase in progress",
            GitOperationKind.CherryPick => "Cherry-pick in progress",
            GitOperationKind.Revert => "Revert in progress",
            _ => "Git operation in progress",
        };

        var conflicts = isRebase
            ? activeRebaseOperationState?.UnmergedPaths ?? Array.Empty<FileChange>()
            : isCherryPick
                ? activeCherryPickOperationState?.UnmergedPaths ?? Array.Empty<FileChange>()
                : isRevert
                    ? activeRevertOperationState?.UnmergedPaths ?? Array.Empty<FileChange>()
                    : activeMergeOperationState?.UnmergedPaths ?? Array.Empty<FileChange>();
        GitOperationConflictList.ItemsSource = conflicts;
        GitOperationConflictList.Visibility = conflicts.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        GitOperationConflictHint.Visibility = conflicts.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (isRebase && activeRebaseOperationState is { } rebase)
        {
            var step = rebase.TotalSteps > 0
                ? $"Step {Math.Min(rebase.CurrentStep, rebase.TotalSteps)} of {rebase.TotalSteps}"
                : "Progress not reported by Git";
            var commit = string.IsNullOrWhiteSpace(rebase.CurrentCommitId)
                ? string.Empty
                : $"  ·  commit {ShortObjectId(rebase.CurrentCommitId)}";
            GitOperationProgressText.Text = $"{rebase.Branch}  ·  {step}{commit}  ·  HEAD {ShortObjectId(rebase.CurrentHeadId)}";
            GitOperationMessageText.Text = rebase.HasUnresolvedConflicts
                ? "Resolve and stage every conflicted path before continuing the rebase."
                : rebase.HasUnstagedChanges
                    ? "Stage tracked changes before continuing the rebase."
                    : "The rebase is ready to continue. Review the current changes first.";
        }
        else if (isMerge && activeMergeOperationState is { } merge)
        {
            var source = merge.MergeHeadIds.Count == 0
                ? "target commit not reported"
                : string.Join(", ", merge.MergeHeadIds.Select(ShortObjectId));
            GitOperationProgressText.Text = $"{merge.Branch}  ·  HEAD {ShortObjectId(merge.CurrentHeadId)}  ·  target {source}";
            GitOperationMessageText.Text = merge.HasUnresolvedConflicts
                ? "Resolve and stage every conflicted path before continuing the merge."
                : "The merge is ready to continue. Review the current changes first.";
        }
        else if (isCherryPick && activeCherryPickOperationState is { } cherryPick)
        {
            var step = cherryPick.TotalSteps > 0
                ? $"Step {Math.Min(cherryPick.CurrentStep, cherryPick.TotalSteps)} of {cherryPick.TotalSteps}"
                : "Progress not reported by Git";
            var commit = string.IsNullOrWhiteSpace(cherryPick.CurrentCommitId)
                ? string.Empty
                : $"  ·  commit {ShortObjectId(cherryPick.CurrentCommitId)}";
            GitOperationProgressText.Text = $"{cherryPick.Branch}  ·  {step}{commit}  ·  HEAD {ShortObjectId(cherryPick.CurrentHeadId)}";
            GitOperationMessageText.Text = cherryPick.HasUnresolvedConflicts
                ? "Resolve and stage every conflicted path before continuing the cherry-pick."
                : cherryPick.HasUnstagedChanges
                    ? "Stage tracked changes before continuing the cherry-pick."
                    : "The cherry-pick is ready to continue. Review the current changes first.";
        }
        else if (isRevert && activeRevertOperationState is { } revert)
        {
            var step = revert.TotalSteps > 0
                ? $"Step {Math.Min(revert.CurrentStep, revert.TotalSteps)} of {revert.TotalSteps}"
                : "Progress not reported by Git";
            var commit = string.IsNullOrWhiteSpace(revert.CurrentCommitId)
                ? string.Empty
                : $"  ·  commit {ShortObjectId(revert.CurrentCommitId)}";
            GitOperationProgressText.Text = $"{revert.Branch}  ·  {step}{commit}  ·  HEAD {ShortObjectId(revert.CurrentHeadId)}";
            GitOperationMessageText.Text = revert.HasUnresolvedConflicts
                ? "Resolve and stage every conflicted path before continuing the revert."
                : revert.HasUnstagedChanges
                    ? "Stage tracked changes before continuing the revert."
                    : "The revert is ready to continue. Review the current changes first.";
        }
        else
        {
            GitOperationProgressText.Text = $"{activeGitOperationKind}  ·  HEAD {ShortObjectId(activeMergeOperationState?.CurrentHeadId ?? string.Empty)}";
            GitOperationMessageText.Text = conflicts.Count > 0
                ? "Select a conflicted path to inspect it in Changes."
                : "Git reports an active operation. Refresh to read its current state.";
        }

        ContinueGitOperationButton.Visibility = isMerge || isRebase || isCherryPick || isRevert
            ? Visibility.Visible
            : Visibility.Collapsed;
        SkipGitOperationButton.Content = isRebase
            ? "Skip rebase commit"
            : isCherryPick
                ? "Skip cherry-pick commit"
                : "Skip revert commit";
        SkipGitOperationButton.Visibility = isRebase || isCherryPick || isRevert
            ? Visibility.Visible
            : Visibility.Collapsed;
        AbortGitOperationButton.Visibility = isMerge || isRebase || isCherryPick || isRevert
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateGitOperationControls();
    }

    private async Task<BranchOperationRequest?> CaptureBranchOperationAsync()
    {
        if (!CanStartRepositoryWrite()
            || repositoryRoot is null
            || currentStatus is not { IsDetached: false, IsUnborn: false } status
            || string.IsNullOrWhiteSpace(status.Branch)
            || string.IsNullOrWhiteSpace(status.HeadId)
            || selectedBranch is not { Branch.IsCurrent: false } branch)
        {
            return null;
        }

        var root = repositoryRoot;
        var operation = BeginOperation($"Reading {branch.Name} tip…");
        try
        {
            var sourceHeadId = await repositoryService.GetLocalBranchTipAsync(
                root,
                branch.Name,
                operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentStatus?.HeadId, status.HeadId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new BranchOperationRequest(
                root,
                status.Branch,
                branch.Name,
                status.HeadId,
                sourceHeadId);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("Unable to read selected branch", exception);
            }

            return null;
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }

    private async void MergeBranchButton_Click(object sender, RoutedEventArgs e)
    {
        var request = await CaptureBranchOperationAsync();
        if (request is null)
        {
            return;
        }

        var dialog = CreateDialog(
            "Merge branch?",
            "Merge",
            new TextBlock
            {
                Text = $"Merge \"{request.SourceBranch}\" ({ShortObjectId(request.SourceHeadId)}) into \"{request.CurrentBranch}\" ({ShortObjectId(request.ExpectedHeadId)})?\n\nGit will use this captured source commit and may stop with conflicts that you resolve and stage manually.",
                TextWrapping = TextWrapping.Wrap,
            });
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunBranchOperationAsync(request, rebase: false);
        }
    }

    private async void RebaseBranchButton_Click(object sender, RoutedEventArgs e)
    {
        var request = await CaptureBranchOperationAsync();
        if (request is null)
        {
            return;
        }

        var dialog = CreateDialog(
            "Rebase branch?",
            "Rebase",
            new TextBlock
            {
                Text = $"Rebase \"{request.CurrentBranch}\" ({ShortObjectId(request.ExpectedHeadId)}) onto \"{request.SourceBranch}\" ({ShortObjectId(request.SourceHeadId)})?\n\nGit will replay the current branch commits onto this captured source commit, changing commit IDs, and may stop with conflicts that you resolve and stage manually.",
                TextWrapping = TextWrapping.Wrap,
            });
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunBranchOperationAsync(request, rebase: true);
        }
    }

    private async Task RunBranchOperationAsync(
        BranchOperationRequest request,
        bool rebase)
    {
        if (!CanStartRepositoryWrite()
            || !string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(currentStatus?.HeadId, request.ExpectedHeadId, StringComparison.OrdinalIgnoreCase))
        {
            ShowError(
                "Repository changed",
                new InvalidOperationException("The repository changed while the confirmation was open. Refresh and review the branch again."));
            return;
        }

        mutationInProgress = true;
        var operation = BeginOperation(rebase
            ? $"Rebasing {request.CurrentBranch} onto {request.SourceBranch}…"
            : $"Merging {request.SourceBranch} into {request.CurrentBranch}…");
        try
        {
            var status = await repositoryService.GetStatusAsync(request.Root, operation.Token);
            var repositoryStillMatches = IsCurrent(operation.Generation, operation.Token)
                && string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase)
                && string.Equals(status.RootPath, request.Root, StringComparison.OrdinalIgnoreCase)
                && !status.IsDetached
                && !status.IsUnborn
                && string.Equals(status.Branch, request.CurrentBranch, StringComparison.Ordinal)
                && string.Equals(status.HeadId, request.ExpectedHeadId, StringComparison.OrdinalIgnoreCase);
            if (!repositoryStillMatches)
            {
                if (!operation.Token.IsCancellationRequested
                    && string.Equals(repositoryRoot, request.Root, StringComparison.OrdinalIgnoreCase))
                {
                    ShowError(
                        "Repository changed",
                        new InvalidOperationException("The current branch or HEAD changed while the confirmation was open. Refresh and review the branch again."));
                }

                return;
            }

            if (rebase)
            {
                var result = await repositoryService.RebaseBranchAsync(
                    request.Root,
                    request.SourceHeadId,
                    request.ExpectedHeadId,
                    operation.Token);
                ErrorBar.IsOpen = false;
                StatusText.Text = FormatRebaseResult(result);
            }
            else
            {
                var result = await repositoryService.MergeBranchAsync(
                    request.Root,
                    request.SourceHeadId,
                    request.ExpectedHeadId,
                    operation.Token);
                ErrorBar.IsOpen = false;
                StatusText.Text = FormatMergeResult(result);
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            StatusText.Text = $"{(rebase ? "Rebase" : "Merge")} canceled; repository was refreshed. Check the operation panel for its current state.";
        }
        catch (Exception exception)
        {
            ShowError(rebase ? "Unable to rebase branch" : "Unable to merge branch", exception);
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

    private async void RefreshGitOperationButton_Click(object sender, RoutedEventArgs e)
    {
        if (repositoryRoot is not null && !mutationInProgress && !BusyRing.IsActive)
        {
            await RefreshRepositoryAsync();
        }
    }

    private bool TryCaptureActiveOperation(out GitOperationSnapshot snapshot)
    {
        snapshot = null!;
        if (repositoryRoot is null
            || !string.Equals(repositoryRoot, gitOperationRepositoryRoot, StringComparison.OrdinalIgnoreCase)
            || activeGitOperationKind is not (GitOperationKind.Merge
                or GitOperationKind.Rebase
                or GitOperationKind.CherryPick
                or GitOperationKind.Revert))
        {
            return false;
        }

        if (activeGitOperationKind == GitOperationKind.Rebase
            && activeRebaseOperationState is { } rebase)
        {
            snapshot = new GitOperationSnapshot(
                repositoryRoot,
                GitOperationKind.Rebase,
                rebase.Branch,
                rebase.CurrentHeadId,
                rebase.CurrentCommitId,
                Array.Empty<string>(),
                Array.Empty<string>(),
                rebase.OriginalBranchTip,
                rebase.BaseBranchTip,
                rebase.UnmergedPaths.Count,
                rebase.HasUnstagedChanges);
            return true;
        }

        if (activeGitOperationKind == GitOperationKind.Merge
            && activeMergeOperationState is { } merge)
        {
            snapshot = new GitOperationSnapshot(
                repositoryRoot,
                GitOperationKind.Merge,
                merge.Branch,
                merge.CurrentHeadId,
                merge.MergeHeadIds.FirstOrDefault(),
                merge.MergeHeadIds.ToArray(),
                Array.Empty<string>(),
                null,
                null,
                merge.UnmergedPaths.Count,
                false);
            return true;
        }

        if (activeGitOperationKind == GitOperationKind.CherryPick
            && activeCherryPickOperationState is { } cherryPick)
        {
            snapshot = new GitOperationSnapshot(
                repositoryRoot,
                GitOperationKind.CherryPick,
                cherryPick.Branch,
                cherryPick.CurrentHeadId,
                cherryPick.CurrentCommitId,
                Array.Empty<string>(),
                cherryPick.CommitIds.ToArray(),
                null,
                null,
                cherryPick.UnmergedPaths.Count,
                cherryPick.HasUnstagedChanges);
            return true;
        }

        if (activeGitOperationKind == GitOperationKind.Revert
            && activeRevertOperationState is { } revert)
        {
            snapshot = new GitOperationSnapshot(
                repositoryRoot,
                GitOperationKind.Revert,
                revert.Branch,
                revert.CurrentHeadId,
                revert.CurrentCommitId,
                Array.Empty<string>(),
                revert.CommitIds.ToArray(),
                null,
                null,
                revert.UnmergedPaths.Count,
                revert.HasUnstagedChanges);
            return true;
        }

        return false;
    }

    private async void ContinueGitOperationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCaptureActiveOperation(out var snapshot)
            || snapshot.Kind is not (GitOperationKind.Merge
                or GitOperationKind.Rebase
                or GitOperationKind.CherryPick
                or GitOperationKind.Revert))
        {
            return;
        }

        if (!string.Equals(currentStatus?.HeadId, snapshot.CurrentHeadId, StringComparison.OrdinalIgnoreCase))
        {
            ShowError(
                "Git operation changed",
                new InvalidOperationException("The repository HEAD changed before the operation could continue. Refresh and review the operation again."));
            await RefreshAfterGitOperationAsync(snapshot.Root);
            return;
        }

        if (snapshot.Kind == GitOperationKind.Merge)
        {
            await RunGitOperationMutationAsync(
                snapshot,
                "Continuing merge…",
                "Merge continued",
                async (root, token) =>
                    FormatMergeResult(await repositoryService.ContinueMergeAsync(root, token)));
        }
        else if (snapshot.Kind == GitOperationKind.Rebase)
        {
            if (IsSquashRecoveryUnavailable(snapshot))
            {
                ShowError(
                    "Squash continuation unavailable",
                    new InvalidOperationException(
                        "The active squash no longer has a valid captured plan and replacement message. Refresh the repository or abort it and start a fresh squash from History."));
                await RefreshAfterGitOperationAsync(snapshot.Root);
            }
            else if (TryGetActiveSquashRecovery(
                snapshot,
                out var squashPlan,
                out var replacementMessage))
            {
                await RunGitOperationMutationAsync(
                    snapshot,
                    "Continuing squash…",
                    "Squash continued",
                    async (root, token) =>
                        FormatHistorySquashResult(
                            await repositoryService.ContinueSquashAsync(
                                root,
                                squashPlan,
                                replacementMessage,
                                token)));
            }
            else
            {
                await RunGitOperationMutationAsync(
                    snapshot,
                    "Continuing rebase…",
                    "Rebase continued",
                    async (root, token) =>
                        FormatRebaseResult(await repositoryService.ContinueRebaseAsync(root, token)));
            }
        }
        else if (snapshot.Kind == GitOperationKind.CherryPick)
        {
            await RunGitOperationMutationAsync(
                snapshot,
                "Continuing cherry-pick…",
                "Cherry-pick continued",
                async (root, token) =>
                    FormatCherryPickResult(await repositoryService.ContinueCherryPickAsync(root, token)));
        }
        else
        {
            await RunGitOperationMutationAsync(
                snapshot,
                "Continuing revert…",
                "Revert continued",
                async (root, token) =>
                    FormatRevertResult(await repositoryService.ContinueRevertAsync(root, token)));
        }
    }

    private async void SkipGitOperationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCaptureActiveOperation(out var snapshot)
            || snapshot.Kind is not (GitOperationKind.Rebase
                or GitOperationKind.CherryPick
                or GitOperationKind.Revert))
        {
            return;
        }

        if (snapshot.Kind == GitOperationKind.Rebase
            && IsSquashRecoveryUnavailable(snapshot))
        {
            ShowError(
                "Squash skip unavailable",
                new InvalidOperationException(
                    "The active squash no longer has a valid captured plan and replacement message. Refresh the repository or abort it and start a fresh squash from History."));
            await RefreshAfterGitOperationAsync(snapshot.Root);
            return;
        }

        SquashPlan squashPlan = null!;
        var replacementMessage = string.Empty;
        var hasSquashRecovery = snapshot.Kind == GitOperationKind.Rebase
            && TryGetActiveSquashRecovery(
                snapshot,
                out squashPlan,
                out replacementMessage);
        var operationName = snapshot.Kind switch
        {
            GitOperationKind.Rebase when hasSquashRecovery => "squash",
            GitOperationKind.CherryPick => "cherry-pick",
            GitOperationKind.Revert => "revert",
            _ => "rebase",
        };
        var operationVerb = snapshot.Kind switch
        {
            GitOperationKind.Rebase when hasSquashRecovery => "squashing",
            GitOperationKind.CherryPick => "cherry-picking",
            GitOperationKind.Revert => "reverting",
            _ => "rebasing",
        };
        var commit = string.IsNullOrWhiteSpace(snapshot.CurrentCommitId)
            ? $"the current {operationName} commit"
            : $"commit {ShortObjectId(snapshot.CurrentCommitId)}";
        var dialog = CreateDialog(
            $"Skip {operationName} commit?",
            "Skip",
            new TextBlock
            {
                Text = $"Skip {commit} while {operationVerb} \"{snapshot.Branch}\"? Git will discard this commit and continue the {operationName}. Resolve conflicts manually when Git stops again.",
                TextWrapping = TextWrapping.Wrap,
            });
        dialog.DefaultButton = ContentDialogButton.Secondary;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (snapshot.Kind == GitOperationKind.Rebase)
            {
                if (hasSquashRecovery)
                {
                    await RunGitOperationMutationAsync(
                        snapshot,
                        "Skipping current squash commit…",
                        "Squash commit skipped",
                        async (root, token) =>
                            FormatHistorySquashResult(
                                await repositoryService.SkipSquashAsync(
                                    root,
                                    squashPlan,
                                    replacementMessage,
                                    token)));
                }
                else
                {
                    await RunGitOperationMutationAsync(
                        snapshot,
                        "Skipping current rebase commit…",
                        "Rebase commit skipped",
                        async (root, token) =>
                            FormatRebaseResult(await repositoryService.SkipRebaseAsync(root, token)));
                }
            }
            else if (snapshot.Kind == GitOperationKind.CherryPick)
            {
                await RunGitOperationMutationAsync(
                    snapshot,
                    "Skipping current cherry-pick commit…",
                    "Cherry-pick commit skipped",
                    async (root, token) =>
                        FormatCherryPickResult(await repositoryService.SkipCherryPickAsync(root, token)));
            }
            else
            {
                await RunGitOperationMutationAsync(
                    snapshot,
                    "Skipping current revert commit…",
                    "Revert commit skipped",
                    async (root, token) =>
                        FormatRevertResult(await repositoryService.SkipRevertAsync(root, token)));
            }
        }
    }

    private async void AbortGitOperationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCaptureActiveOperation(out var snapshot))
        {
            return;
        }

        var operationName = snapshot.Kind switch
        {
            GitOperationKind.Rebase => "rebase",
            GitOperationKind.CherryPick => "cherry-pick",
            GitOperationKind.Revert => "revert",
            _ => "merge",
        };
        var effect = snapshot.Kind switch
        {
            GitOperationKind.Rebase => "Git will restore the branch tip saved before the rebase and discard the in-progress replay.",
            GitOperationKind.CherryPick => "Git will restore the branch tip saved before the cherry-pick and discard its in-progress replay.",
            GitOperationKind.Revert => "Git will restore the branch tip saved before the revert and discard its in-progress replay.",
            _ => "Git will restore the pre-merge index and working-tree state where possible.",
        };
        var dialog = CreateDialog(
            $"Abort {operationName}?",
            "Abort",
            new TextBlock
            {
                Text = $"Abort the {operationName} on \"{snapshot.Branch}\" at HEAD {ShortObjectId(snapshot.CurrentHeadId)}?\n\n{effect} Any conflict work from this operation is discarded. This cannot be undone by WinGit.",
                TextWrapping = TextWrapping.Wrap,
            });
        dialog.DefaultButton = ContentDialogButton.Secondary;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (snapshot.Kind == GitOperationKind.Merge)
        {
            await RunGitOperationMutationAsync(
                snapshot,
                "Aborting merge…",
                "Merge aborted",
                async (root, token) =>
                    FormatMergeResult(await repositoryService.AbortMergeAsync(root, token)));
        }
        else if (snapshot.Kind == GitOperationKind.Rebase)
        {
            await RunGitOperationMutationAsync(
                snapshot,
                "Aborting rebase…",
                "Rebase aborted",
                async (root, token) =>
                    FormatRebaseResult(await repositoryService.AbortRebaseAsync(root, token)));
        }
        else if (snapshot.Kind == GitOperationKind.CherryPick)
        {
            await RunGitOperationMutationAsync(
                snapshot,
                "Aborting cherry-pick…",
                "Cherry-pick aborted",
                async (root, token) =>
                    FormatCherryPickResult(await repositoryService.AbortCherryPickAsync(root, token)));
        }
        else
        {
            await RunGitOperationMutationAsync(
                snapshot,
                "Aborting revert…",
                "Revert aborted",
                async (root, token) =>
                    FormatRevertResult(await repositoryService.AbortRevertAsync(root, token)));
        }
    }

    private async Task RunGitOperationMutationAsync(
        GitOperationSnapshot snapshot,
        string progress,
        string success,
        Func<string, CancellationToken, Task<string>> mutation)
    {
        if (!CanStartRepositoryWrite()
            || !string.Equals(repositoryRoot, snapshot.Root, StringComparison.OrdinalIgnoreCase)
            || activeGitOperationKind != snapshot.Kind)
        {
            return;
        }

        mutationInProgress = true;
        var operation = BeginOperation(progress);
        try
        {
            var isCurrent = await IsConfirmedGitOperationCurrentAsync(snapshot, operation.Token);
            if (!isCurrent
                || !IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, snapshot.Root, StringComparison.OrdinalIgnoreCase)
                || activeGitOperationKind != snapshot.Kind
                || !string.Equals(currentStatus?.HeadId, snapshot.CurrentHeadId, StringComparison.OrdinalIgnoreCase))
            {
                if (!operation.Token.IsCancellationRequested
                    && string.Equals(repositoryRoot, snapshot.Root, StringComparison.OrdinalIgnoreCase))
                {
                    ShowError(
                        "Git operation changed",
                        new InvalidOperationException("The active Git operation changed while the confirmation was open. The repository was refreshed without running the requested action."));
                }

                return;
            }

            var resultMessage = await mutation(snapshot.Root, operation.Token);
            ErrorBar.IsOpen = false;
            StatusText.Text = string.IsNullOrWhiteSpace(resultMessage) ? success : resultMessage;
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            StatusText.Text = $"{snapshot.Kind} action canceled; repository was refreshed. Check the operation panel for its current state.";
        }
        catch (Exception exception)
        {
            ShowError($"Unable to update {snapshot.Kind.ToString().ToLowerInvariant()}", exception);
        }
        finally
        {
            try
            {
                await RefreshAfterGitOperationAsync(snapshot.Root);
            }
            catch (Exception refreshException)
            {
                ShowError("Unable to refresh repository after Git operation", refreshException);
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
        }
    }

    private async Task<bool> IsConfirmedGitOperationCurrentAsync(
        GitOperationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(repositoryRoot, snapshot.Root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(currentStatus?.HeadId, snapshot.CurrentHeadId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var mergeState = await repositoryService.GetMergeStateAsync(
                snapshot.Root,
                cancellationToken);
            if (snapshot.Kind == GitOperationKind.Merge)
            {
                return mergeState.OperationKind == GitOperationKind.Merge
                    && string.Equals(mergeState.CurrentHeadId, snapshot.CurrentHeadId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(mergeState.Branch, snapshot.Branch, StringComparison.Ordinal)
                    && mergeState.MergeHeadIds.SequenceEqual(
                    snapshot.MergeHeadIds,
                    StringComparer.OrdinalIgnoreCase);
            }

            if (snapshot.Kind == GitOperationKind.Rebase)
            {
                var rebaseState = await repositoryService.GetRebaseStateAsync(
                    snapshot.Root,
                    cancellationToken);
                return mergeState.OperationKind == GitOperationKind.Rebase
                    && rebaseState.OperationKind == GitOperationKind.Rebase
                    && string.Equals(rebaseState.CurrentHeadId, snapshot.CurrentHeadId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(rebaseState.Branch, snapshot.Branch, StringComparison.Ordinal)
                    && string.Equals(rebaseState.CurrentCommitId, snapshot.CurrentCommitId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(rebaseState.OriginalBranchTip, snapshot.OriginalBranchTip, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(rebaseState.BaseBranchTip, snapshot.BaseBranchTip, StringComparison.OrdinalIgnoreCase);
            }

            if (snapshot.Kind == GitOperationKind.CherryPick)
            {
                var cherryPickState = await repositoryService.GetCherryPickStateAsync(
                    snapshot.Root,
                    cancellationToken);
                return cherryPickState.OperationKind == GitOperationKind.CherryPick
                    && string.Equals(cherryPickState.CurrentHeadId, snapshot.CurrentHeadId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(cherryPickState.Branch, snapshot.Branch, StringComparison.Ordinal)
                    && string.Equals(cherryPickState.CurrentCommitId, snapshot.CurrentCommitId, StringComparison.OrdinalIgnoreCase)
                    && cherryPickState.CommitIds.SequenceEqual(
                        snapshot.SequenceCommitIds,
                        StringComparer.OrdinalIgnoreCase);
            }

            if (snapshot.Kind == GitOperationKind.Revert)
            {
                var revertState = await repositoryService.GetRevertStateAsync(
                    snapshot.Root,
                    cancellationToken);
                return revertState.OperationKind == GitOperationKind.Revert
                    && string.Equals(revertState.CurrentHeadId, snapshot.CurrentHeadId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(revertState.Branch, snapshot.Branch, StringComparison.Ordinal)
                    && string.Equals(revertState.CurrentCommitId, snapshot.CurrentCommitId, StringComparison.OrdinalIgnoreCase)
                    && revertState.CommitIds.SequenceEqual(
                        snapshot.SequenceCommitIds,
                        StringComparer.OrdinalIgnoreCase);
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task RefreshAfterGitOperationAsync(string expectedRoot)
    {
        if (repositoryRoot is null
            || !string.Equals(repositoryRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await RefreshRepositoryAsync();
        if (repositoryRoot is null
            || !string.Equals(repositoryRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        branchesLoaded = false;
        commitRows.Clear();
        commitFileRows.Clear();
        historyDiffRows.Clear();
        HistoryList.ItemsSource = commitRows;
        HistoryFilesList.ItemsSource = commitFileRows;
        HistoryDiffList.ItemsSource = historyDiffRows;
        selectedCommit = null;
        selectedCommitFile = null;

        if (currentWorkspace == "branches")
        {
            await LoadBranchesAsync();
        }
        else if (currentWorkspace == "history")
        {
            await LoadHistoryAsync();
        }

        UpdateGitOperationPresentation();
        UpdateRepositoryCommandStates();
    }

    private void GitOperationConflictList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GitOperationConflictList.SelectedItem is not FileChange conflict
            || repositoryRoot is null
            || mutationInProgress
            || BusyRing.IsActive)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(ChangesFilterBox.Text))
        {
            ChangesFilterBox.Text = string.Empty;
        }

        MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
        ShowWorkspace("changes");
        var unstaged = unstagedChangeRows.FirstOrDefault(row =>
            string.Equals(row.Path, conflict.Path, StringComparison.OrdinalIgnoreCase));
        var staged = stagedChangeRows.FirstOrDefault(row =>
            string.Equals(row.Path, conflict.Path, StringComparison.OrdinalIgnoreCase));
        if (unstaged is not null)
        {
            UnstagedChangesList.SelectedItem = unstaged;
        }
        else if (staged is not null)
        {
            StagedChangesList.SelectedItem = staged;
        }
    }

    private static string FormatMergeResult(MergeOperationResult result) => result.Outcome switch
    {
        MergeOutcome.Completed => $"Merge completed at {ShortObjectId(result.HeadId)}",
        MergeOutcome.AlreadyUpToDate => "Merge already up to date",
        MergeOutcome.Conflicts => "Merge stopped with conflicts",
        MergeOutcome.InProgress => "Merge is still in progress",
        MergeOutcome.Aborted => "Merge aborted",
        _ => "Merge finished",
    };

    private static string FormatRebaseResult(RebaseOperationResult result) => result.Outcome switch
    {
        RebaseOutcome.Completed => $"Rebase completed at {ShortObjectId(result.HeadId)}",
        RebaseOutcome.AlreadyUpToDate => "Rebase already up to date",
        RebaseOutcome.Conflicts => "Rebase stopped with conflicts",
        RebaseOutcome.InProgress => "Rebase is still in progress",
        RebaseOutcome.Skipped => "Rebase commit skipped",
        RebaseOutcome.Aborted => "Rebase aborted",
        _ => "Rebase finished",
    };
}
