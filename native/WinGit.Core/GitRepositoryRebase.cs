using System.Globalization;
using System.Text;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private static readonly IReadOnlyDictionary<string, string?> NoOpEditorEnvironment =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_EDITOR"] = ":",
        };

    /// <summary>Reads the current rebase marker, metadata, progress, and paths needing attention.</summary>
    public async Task<RebaseOperationState> GetRebaseStateAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ReadRebaseStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebases the current branch or detached HEAD onto a validated local branch
    /// or commit. Git's normal dirty-worktree and hook policy remains in effect.
    /// </summary>
    public async Task<RebaseOperationResult> RebaseBranchAsync(
        string root,
        string branchOrCommit,
        string expectedHeadId,
        CancellationToken cancellationToken)
    {
        ValidateMergeTargetInput(branchOrCommit);
        ValidateExpectedHeadId(expectedHeadId);
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);

        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var before = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureRebaseCanStart(before, expectedHeadId);
                var target = await ResolveMergeTargetAsync(path, branchOrCommit, cancellationToken).ConfigureAwait(false);

                try
                {
                    // Rebase has no interactive editor in this API. The per-process
                    // no-op editor also prevents Git from hanging on a continue.
                    // Omitting --no-verify retains normal hooks and signing policy.
                    await processRunner.RunAsync(
                        path,
                        ["rebase", "--no-autostash", target],
                        cancellationToken,
                        environmentOverrides: NoOpEditorEnvironment).ConfigureAwait(false);
                }
                catch (GitCommandException)
                {
                    var failed = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                    if (failed.IsInProgress && failed.HasUnresolvedConflicts)
                    {
                        return new RebaseOperationResult(
                            RebaseOutcome.Conflicts,
                            failed.CurrentHeadId,
                            failed);
                    }

                    throw;
                }

                var after = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                return new RebaseOperationResult(
                    GetRebaseCompletionOutcome(before, after),
                    after.CurrentHeadId,
                    after);
            }).ConfigureAwait(false);
    }

    /// <summary>Continues a rebase after every tracked resolution has been staged.</summary>
    public async Task<RebaseOperationResult> ContinueRebaseAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var state = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureRebaseCanContinue(state);

                try
                {
                    await processRunner.RunAsync(
                        path,
                        ["rebase", "--continue"],
                        cancellationToken,
                        environmentOverrides: NoOpEditorEnvironment).ConfigureAwait(false);
                }
                catch (GitCommandException)
                {
                    var failed = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                    if (failed.IsInProgress && failed.HasUnresolvedConflicts)
                    {
                        return new RebaseOperationResult(
                            RebaseOutcome.Conflicts,
                            failed.CurrentHeadId,
                            failed);
                    }

                    throw;
                }

                var after = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                var outcome = after.IsInProgress
                    ? after.HasUnresolvedConflicts
                        ? RebaseOutcome.Conflicts
                        : RebaseOutcome.InProgress
                    : RebaseOutcome.Completed;
                return new RebaseOperationResult(outcome, after.CurrentHeadId, after);
            }).ConfigureAwait(false);
    }

    /// <summary>Explicitly skips the current rebase commit at the caller's request.</summary>
    public async Task<RebaseOperationResult> SkipRebaseAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var state = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureRebaseCanSkip(state);

                try
                {
                    await processRunner.RunAsync(
                        path,
                        ["rebase", "--skip"],
                        cancellationToken,
                        environmentOverrides: NoOpEditorEnvironment).ConfigureAwait(false);
                }
                catch (GitCommandException)
                {
                    var failed = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                    if (failed.IsInProgress && failed.HasUnresolvedConflicts)
                    {
                        return new RebaseOperationResult(
                            RebaseOutcome.Conflicts,
                            failed.CurrentHeadId,
                            failed);
                    }

                    throw;
                }

                var after = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                var outcome = after.IsInProgress
                    ? after.HasUnresolvedConflicts
                        ? RebaseOutcome.Conflicts
                        : RebaseOutcome.InProgress
                    : RebaseOutcome.Skipped;
                return new RebaseOperationResult(outcome, after.CurrentHeadId, after);
            }).ConfigureAwait(false);
    }

    /// <summary>Aborts an in-progress rebase using Git's normal worktree safeguards.</summary>
    public async Task<RebaseOperationResult> AbortRebaseAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var state = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureRebaseCanAbort(state);
                await processRunner.RunAsync(
                    path,
                    ["rebase", "--abort"],
                    cancellationToken).ConfigureAwait(false);
                var after = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                return new RebaseOperationResult(RebaseOutcome.Aborted, after.CurrentHeadId, after);
            }).ConfigureAwait(false);
    }

    private async Task<RebaseOperationState> ReadRebaseStateAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var operationState = await ReadMergeStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var rebaseDirectory = await ResolveRebaseDirectoryAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);

        if (operationState.OperationKind != GitOperationKind.Rebase)
        {
            return new RebaseOperationState(
                operationState.OperationKind,
                operationState.CurrentHeadId,
                operationState.Branch,
                originalBranchTip: null,
                baseBranchTip: null,
                currentStep: 0,
                totalSteps: 0,
                currentCommitId: null,
                operationState.UnmergedPaths,
                status.Changes.Where(IsTrackedUnstagedChange).ToArray());
        }

        var originalBranchTip = await ReadRebaseObjectIdAsync(
            rebaseDirectory,
            "orig-head",
            cancellationToken).ConfigureAwait(false);
        var baseBranchTip = await ReadRebaseObjectIdAsync(
            rebaseDirectory,
            "onto",
            cancellationToken).ConfigureAwait(false);
        var branch = await ReadRebaseBranchNameAsync(
            rebaseDirectory,
            cancellationToken).ConfigureAwait(false)
            ?? operationState.Branch;
        var currentStep = await ReadRebaseIntegerAsync(
            rebaseDirectory,
            ["msgnum", "next"],
            cancellationToken).ConfigureAwait(false);
        var totalSteps = await ReadRebaseIntegerAsync(
            rebaseDirectory,
            ["end", "last"],
            cancellationToken).ConfigureAwait(false);
        var currentCommitId = await ReadRefIdAsync(
            repositoryRoot,
            "REBASE_HEAD",
            cancellationToken).ConfigureAwait(false);

        return new RebaseOperationState(
            operationState.OperationKind,
            operationState.CurrentHeadId,
            branch,
            originalBranchTip,
            baseBranchTip,
            currentStep,
            totalSteps,
            currentCommitId,
            operationState.UnmergedPaths,
            status.Changes.Where(IsTrackedUnstagedChange).ToArray());
    }

    private async Task<string?> ResolveRebaseDirectoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var mergeDirectory = await ReadGitPathAsync(
            repositoryRoot,
            "rebase-merge",
            cancellationToken).ConfigureAwait(false);
        if (IsExistingPath(mergeDirectory))
        {
            return mergeDirectory;
        }

        var applyDirectory = await ReadGitPathAsync(
            repositoryRoot,
            "rebase-apply",
            cancellationToken).ConfigureAwait(false);
        return IsExistingPath(applyDirectory) ? applyDirectory : null;
    }

    private async Task<string?> ReadRebaseObjectIdAsync(
        string? rebaseDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        var value = await ReadRebaseMetadataAsync(
            rebaseDirectory,
            fileName,
            cancellationToken).ConfigureAwait(false);
        return value is not null && GitObjectIdPattern.IsMatch(value)
            ? value
            : null;
    }

    private async Task<string?> ReadRebaseBranchNameAsync(
        string? rebaseDirectory,
        CancellationToken cancellationToken)
    {
        var value = await ReadRebaseMetadataAsync(
            rebaseDirectory,
            "head-name",
            cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            return null;
        }

        return value.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? value[11..]
            : value;
    }

    private async Task<int> ReadRebaseIntegerAsync(
        string? rebaseDirectory,
        IReadOnlyList<string> fileNames,
        CancellationToken cancellationToken)
    {
        foreach (var fileName in fileNames)
        {
            var value = await ReadRebaseMetadataAsync(
                rebaseDirectory,
                fileName,
                cancellationToken).ConfigureAwait(false);
            if (value is not null
                && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number > 0)
            {
                return number;
            }
        }

        return 0;
    }

    private static async Task<string?> ReadRebaseMetadataAsync(
        string? rebaseDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (rebaseDirectory is null)
        {
            return null;
        }

        var path = Path.Combine(rebaseDirectory, fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bounded = await ReadBoundedFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (bounded.Truncated)
            {
                return null;
            }

            var value = StrictUtf8.GetString(bounded.Bytes).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsTrackedUnstagedChange(FileChange change) =>
        !IsUntracked(change) && change.WorkTreeStatus.Length > 0;

    private static RebaseOutcome GetRebaseCompletionOutcome(
        RebaseOperationState before,
        RebaseOperationState after)
    {
        if (after.IsInProgress)
        {
            return after.HasUnresolvedConflicts
                ? RebaseOutcome.Conflicts
                : RebaseOutcome.InProgress;
        }

        return string.Equals(
            before.CurrentHeadId,
            after.CurrentHeadId,
            StringComparison.OrdinalIgnoreCase)
            ? RebaseOutcome.AlreadyUpToDate
            : RebaseOutcome.Completed;
    }

    private static void EnsureRebaseCanStart(
        RebaseOperationState state,
        string expectedHeadId)
    {
        if (state.CurrentHeadId.Length == 0)
        {
            throw new RebaseOperationBlockedException(
                RebaseOperationFailureReason.UnbornRepository,
                state,
                "Rebase is unavailable because the repository has no current commit.");
        }

        if (!string.Equals(state.CurrentHeadId, expectedHeadId, StringComparison.OrdinalIgnoreCase))
        {
            throw new RebaseOperationBlockedException(
                RebaseOperationFailureReason.StaleHead,
                state,
                "Rebase was not applied because the current commit changed; refresh the repository first.");
        }

        if (state.OperationKind != GitOperationKind.None)
        {
            throw new RebaseOperationBlockedException(
                RebaseOperationFailureReason.DifferentOperationInProgress,
                state,
                "Rebase is unavailable while another Git operation is in progress.");
        }

        if (state.HasUnresolvedConflicts)
        {
            throw new RebaseOperationBlockedException(
                RebaseOperationFailureReason.UnresolvedConflicts,
                state,
                "Rebase is unavailable while the index contains unresolved conflicts.");
        }
    }

    private static void EnsureRebaseCanContinue(RebaseOperationState state)
    {
        EnsureRebaseOperationIsActive(state);
        if (state.HasUnresolvedConflicts)
        {
            throw new RebaseOperationBlockedException(
                RebaseOperationFailureReason.UnresolvedConflicts,
                state,
                "Resolve and stage every unmerged path before continuing the rebase.");
        }

        if (state.HasUnstagedChanges)
        {
            throw new RebaseOperationBlockedException(
                RebaseOperationFailureReason.UnstagedChanges,
                state,
                "Stage every tracked conflict resolution before continuing the rebase.");
        }
    }

    private static void EnsureRebaseCanSkip(RebaseOperationState state) =>
        EnsureRebaseOperationIsActive(state);

    private static void EnsureRebaseCanAbort(RebaseOperationState state) =>
        EnsureRebaseOperationIsActive(state);

    private static void EnsureRebaseOperationIsActive(RebaseOperationState state)
    {
        if (!state.IsInProgress)
        {
            var reason = state.OperationKind == GitOperationKind.None
                ? RebaseOperationFailureReason.NotInProgress
                : RebaseOperationFailureReason.DifferentOperationInProgress;
            throw new RebaseOperationBlockedException(
                reason,
                state,
                state.OperationKind == GitOperationKind.None
                    ? "There is no in-progress Git rebase."
                    : "Only an in-progress rebase can be handled by this operation.");
        }
    }
}
