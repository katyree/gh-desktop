namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private static readonly string[] MergeOperationMarkerNames =
    [
        "MERGE_HEAD",
        "CHERRY_PICK_HEAD",
        "REVERT_HEAD",
        "sequencer",
        "rebase-merge",
        "rebase-apply",
    ];

    /// <summary>Reads the current Git operation marker and typed unmerged paths.</summary>
    public async Task<MergeOperationState> GetMergeStateAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ReadMergeStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges a validated local branch or commit into the expected current HEAD.
    /// Git's normal dirty-worktree and hook/signing policy remains in effect.
    /// </summary>
    public async Task<MergeOperationResult> MergeBranchAsync(
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
                var before = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureMergeCanStart(before, expectedHeadId);
                var target = await ResolveMergeTargetAsync(path, branchOrCommit, cancellationToken).ConfigureAwait(false);

                try
                {
                    // --no-edit prevents Git from opening an editor.  Omitting
                    // --no-verify keeps normal merge hooks and signing policy.
                    // --no-autostash makes dirty-worktree behavior explicit.
                    await processRunner.RunAsync(
                        path,
                        ["merge", "--no-edit", "--no-autostash", target],
                        cancellationToken).ConfigureAwait(false);
                }
                catch (GitCommandException)
                {
                    var failed = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                    if (failed.IsMergeInProgress && failed.HasUnresolvedConflicts)
                    {
                        return new MergeOperationResult(
                            MergeOutcome.Conflicts,
                            failed.CurrentHeadId,
                            failed);
                    }

                    throw;
                }

                var after = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                var outcome = after.IsMergeInProgress
                    ? MergeOutcome.InProgress
                    : string.Equals(
                        before.CurrentHeadId,
                        after.CurrentHeadId,
                        StringComparison.OrdinalIgnoreCase)
                        ? MergeOutcome.AlreadyUpToDate
                        : MergeOutcome.Completed;
                return new MergeOperationResult(outcome, after.CurrentHeadId, after);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Squash-merges a validated local branch or commit into the index without
    /// moving HEAD. A successful squash stages one combined change plus
    /// SQUASH_MSG; the caller commits it to finish. Git's normal
    /// dirty-worktree and hook/signing policy remains in effect.
    /// </summary>
    public async Task<MergeOperationResult> SquashMergeBranchAsync(
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
                var before = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureMergeCanStart(before, expectedHeadId);
                EnsureNoStagedSquash(before);
                var target = await ResolveMergeTargetAsync(path, branchOrCommit, cancellationToken).ConfigureAwait(false);

                try
                {
                    // --no-edit is harmless for squash (no commit is created).
                    // Omitting --no-verify keeps normal hooks and signing policy.
                    // --no-autostash makes dirty-worktree behavior explicit.
                    await processRunner.RunAsync(
                        path,
                        ["merge", "--squash", "--no-autostash", target],
                        cancellationToken).ConfigureAwait(false);
                }
                catch (GitCommandException)
                {
                    var failed = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                    if (failed.IsSquashMergePending && failed.HasUnresolvedConflicts)
                    {
                        return new MergeOperationResult(
                            MergeOutcome.Conflicts,
                            failed.CurrentHeadId,
                            failed);
                    }

                    throw;
                }

                var after = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                // A squash merge never advances HEAD: either the combined change
                // is staged (SQUASH_MSG set) or the target was already merged.
                var outcome = after.IsSquashMergePending
                    ? MergeOutcome.SquashStaged
                    : MergeOutcome.AlreadyUpToDate;
                return new MergeOperationResult(outcome, after.CurrentHeadId, after);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits a conflict-free staged squash merge with the existing
    /// SQUASH_MSG. The explicit no-edit commit keeps the native UI from ever
    /// waiting on an external editor.
    /// </summary>
    public async Task<MergeOperationResult> ContinueSquashMergeAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var state = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureSquashMergeCanContinue(state);
                await processRunner.RunAsync(
                    path,
                    ["commit", "--no-edit"],
                    cancellationToken).ConfigureAwait(false);
                var after = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                return new MergeOperationResult(MergeOutcome.Completed, after.CurrentHeadId, after);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Aborts a staged squash merge by restoring the captured pre-merge tip.
    /// A squash merge never moves HEAD, so the tip also guards against
    /// external changes. Verified to clear SQUASH_MSG and staged changes.
    /// </summary>
    public async Task<MergeOperationResult> AbortSquashMergeAsync(
        string root,
        string expectedPreMergeTip,
        CancellationToken cancellationToken)
    {
        ValidateExpectedHeadId(expectedPreMergeTip);
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var state = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureSquashMergeCanAbort(state, expectedPreMergeTip);
                await processRunner.RunAsync(
                    path,
                    ["reset", "--hard", expectedPreMergeTip],
                    cancellationToken).ConfigureAwait(false);
                var after = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                if (after.IsSquash
                    || !string.Equals(after.CurrentHeadId, expectedPreMergeTip, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Aborting the squash merge left unexpected repository state; refresh and review the repository before retrying.");
                }

                return new MergeOperationResult(MergeOutcome.Aborted, after.CurrentHeadId, after);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes a conflict-free merge with the existing MERGE_MSG.  The explicit
    /// no-edit commit keeps the native UI from ever waiting on an external editor.
    /// </summary>
    public async Task<MergeOperationResult> ContinueMergeAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var state = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureMergeCanContinue(state);
                await processRunner.RunAsync(
                    path,
                    ["commit", "--no-edit"],
                    cancellationToken).ConfigureAwait(false);
                var after = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                return new MergeOperationResult(MergeOutcome.Completed, after.CurrentHeadId, after);
            }).ConfigureAwait(false);
    }

    /// <summary>Aborts an in-progress merge using Git's normal worktree safeguards.</summary>
    public async Task<MergeOperationResult> AbortMergeAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var state = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureMergeCanAbort(state);
                await processRunner.RunAsync(
                    path,
                    ["merge", "--abort"],
                    cancellationToken).ConfigureAwait(false);
                var after = await ReadMergeStateAsync(path, cancellationToken).ConfigureAwait(false);
                return new MergeOperationResult(MergeOutcome.Aborted, after.CurrentHeadId, after);
            }).ConfigureAwait(false);
    }

    private async Task<MergeOperationState> ReadMergeStateAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var markerPaths = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var markerName in MergeOperationMarkerNames)
        {
            markerPaths[markerName] = await ReadGitPathAsync(
                repositoryRoot,
                markerName,
                cancellationToken).ConfigureAwait(false);
        }

        var mergeHeadPath = markerPaths["MERGE_HEAD"];
        var mergeHeadExists = IsExistingPath(mergeHeadPath);
        var squashMessagePath = await ReadGitPathAsync(
            repositoryRoot,
            "SQUASH_MSG",
            cancellationToken).ConfigureAwait(false);
        var isSquash = IsExistingPath(squashMessagePath);

        var operationKind = DetermineOperationKind(markerPaths, mergeHeadExists);
        var mergeHeadIds = mergeHeadExists
            ? await ReadMergeHeadIdsAsync(repositoryRoot, cancellationToken).ConfigureAwait(false)
            : [];
        var unmergedPaths = status.Changes
            .Where(IsUnmergedChange)
            .ToArray();

        return new MergeOperationState(
            operationKind,
            status.HeadId,
            status.Branch,
            mergeHeadIds,
            isSquash,
            unmergedPaths);
    }

    private async Task<IReadOnlyList<string>> ReadMergeHeadIdsAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var mergeHeadPath = await ReadGitPathAsync(
            repositoryRoot,
            "MERGE_HEAD",
            cancellationToken).ConfigureAwait(false);
        if (mergeHeadPath is null)
        {
            return [];
        }

        if (ContainsReparsePoint(repositoryRoot, mergeHeadPath))
        {
            throw new InvalidOperationException(
                "Git merge metadata cannot be read through a reparse point.");
        }

        if (!File.Exists(mergeHeadPath))
        {
            return [];
        }

        if (Directory.Exists(mergeHeadPath))
        {
            throw new InvalidOperationException("Git returned an invalid MERGE_HEAD path.");
        }

        BoundedFile bounded;
        try
        {
            bounded = await ReadBoundedFileAsync(
                mergeHeadPath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // An external Git operation may finish between the metadata lookup
            // and the bounded read.  Treat the marker as gone in that case.
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        if (bounded.Truncated)
        {
            throw new GitOutputLimitException("merge head metadata");
        }

        var ids = DecodeUtf8(bounded.Bytes, "merge head metadata")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(id => id.Trim());
        var validated = new List<string>();
        foreach (var id in ids)
        {
            ValidateObjectId(id, "merge head ID");
            validated.Add(id);
        }

        return validated;
    }

    private async Task<string> ResolveMergeTargetAsync(
        string repositoryRoot,
        string branchOrCommit,
        CancellationToken cancellationToken)
    {
        if (CommitIdPattern.IsMatch(branchOrCommit))
        {
            return await ResolveCommitIdAsync(
                repositoryRoot,
                branchOrCommit,
                cancellationToken).ConfigureAwait(false);
        }

        var branchName = await ValidateBranchNameAsync(
            repositoryRoot,
            branchOrCommit,
            cancellationToken).ConfigureAwait(false);
        var branchRef = $"refs/heads/{branchName}";
        var branchId = await ReadRefIdAsync(
            repositoryRoot,
            branchRef,
            cancellationToken).ConfigureAwait(false);
        if (branchId is null)
        {
            throw new InvalidOperationException("The selected local branch no longer exists; refresh branches and try again.");
        }

        return branchRef;
    }

    private static GitOperationKind DetermineOperationKind(
        IReadOnlyDictionary<string, string?> markerPaths,
        bool mergeHeadExists)
    {
        // A rebase can also leave merge/cherry-pick markers while replaying a
        // commit. Its rebase directory is the operation boundary and must win.
        if (IsExistingPath(markerPaths["rebase-merge"])
            || IsExistingPath(markerPaths["rebase-apply"]))
        {
            return GitOperationKind.Rebase;
        }

        if (mergeHeadExists)
        {
            return GitOperationKind.Merge;
        }

        if (IsExistingPath(markerPaths["CHERRY_PICK_HEAD"]))
        {
            return GitOperationKind.CherryPick;
        }

        if (IsExistingPath(markerPaths["REVERT_HEAD"]))
        {
            return GitOperationKind.Revert;
        }

        if (IsExistingPath(markerPaths["sequencer"]))
        {
            return GitOperationKind.Sequencer;
        }

        return GitOperationKind.None;
    }

    private static bool IsExistingPath(string? path) =>
        path is not null && (File.Exists(path) || Directory.Exists(path));

    private static bool IsUnmergedChange(FileChange change) =>
        change.Kind == ChangeKind.Conflicted
        || change.IndexStatus.Contains('U', StringComparison.Ordinal)
        || change.WorkTreeStatus.Contains('U', StringComparison.Ordinal);

    private static void EnsureMergeCanStart(
        MergeOperationState state,
        string expectedHeadId)
    {
        if (state.CurrentHeadId.Length == 0)
        {
            throw new MergeOperationBlockedException(
                MergeOperationFailureReason.UnbornRepository,
                state,
                "Merge is unavailable because the repository has no current commit.");
        }

        if (!string.Equals(state.CurrentHeadId, expectedHeadId, StringComparison.OrdinalIgnoreCase))
        {
            throw new MergeOperationBlockedException(
                MergeOperationFailureReason.StaleHead,
                state,
                "Merge was not applied because the current commit changed; refresh the repository first.");
        }

        if (state.OperationKind != GitOperationKind.None)
        {
            throw new MergeOperationBlockedException(
                MergeOperationFailureReason.DifferentOperationInProgress,
                state,
                "Merge is unavailable while another Git operation is in progress.");
        }

        if (state.HasUnresolvedConflicts)
        {
            throw new MergeOperationBlockedException(
                MergeOperationFailureReason.UnresolvedConflicts,
                state,
                "Merge is unavailable while the index contains unresolved conflicts.");
        }
    }

    private static void EnsureMergeCanContinue(MergeOperationState state)
    {
        if (!state.IsInProgress)
        {
            throw new MergeOperationBlockedException(
                MergeOperationFailureReason.NotInProgress,
                state,
                "There is no in-progress Git merge to continue.");
        }

        if (!state.IsMergeInProgress)
        {
            throw new MergeOperationBlockedException(
                MergeOperationFailureReason.DifferentOperationInProgress,
                state,
                "Only an in-progress merge can be continued by this operation.");
        }

        if (state.HasUnresolvedConflicts)
        {
            throw new MergeOperationBlockedException(
                MergeOperationFailureReason.UnresolvedConflicts,
                state,
                "Resolve and stage every unmerged path before continuing the merge.");
        }
    }

    private static void EnsureNoStagedSquash(MergeOperationState state)
    {
        if (state.IsSquash)
        {
            throw new MergeOperationBlockedException(
                MergeOperationFailureReason.SquashAlreadyStaged,
                state,
                "A squashed result is already staged; commit or abort it before starting another merge.");
        }
    }

    private static void EnsureSquashMergeCanContinue(MergeOperationState state)
    {
        if (!state.IsSquashMergePending)
        {
            throw new MergeOperationBlockedException(
                state.IsMergeInProgress
                    ? MergeOperationFailureReason.DifferentOperationInProgress
                    : MergeOperationFailureReason.NotInProgress,
                state,
                state.IsMergeInProgress
                    ? "Only a staged squash merge can be continued by this operation."
                    : "There is no staged squash merge to continue.");
        }

        if (state.HasUnresolvedConflicts)
        {
            throw new MergeOperationBlockedException(
                MergeOperationFailureReason.UnresolvedConflicts,
                state,
                "Resolve and stage every unmerged path before continuing the squash merge.");
        }
    }

    private static void EnsureSquashMergeCanAbort(MergeOperationState state, string expectedPreMergeTip)
    {
        if (!state.IsSquashMergePending)
        {
            throw new MergeOperationBlockedException(
                state.IsMergeInProgress
                    ? MergeOperationFailureReason.DifferentOperationInProgress
                    : MergeOperationFailureReason.NotInProgress,
                state,
                state.IsMergeInProgress
                    ? "Only a staged squash merge can be aborted by this operation."
                    : "There is no staged squash merge to abort.");
        }

        if (!string.Equals(state.CurrentHeadId, expectedPreMergeTip, StringComparison.OrdinalIgnoreCase))
        {
            throw new MergeOperationBlockedException(
                MergeOperationFailureReason.StaleHead,
                state,
                "The current commit changed since the squash merge started; refresh the repository before aborting it.");
        }
    }

    private static void EnsureMergeCanAbort(MergeOperationState state)
    {
        if (!state.IsInProgress)
        {
            throw new MergeOperationBlockedException(
                MergeOperationFailureReason.NotInProgress,
                state,
                "There is no in-progress Git merge to abort.");
        }

        if (!state.IsMergeInProgress)
        {
            throw new MergeOperationBlockedException(
                MergeOperationFailureReason.DifferentOperationInProgress,
                state,
                "Only an in-progress merge can be aborted by this operation.");
        }
    }

    private static void ValidateMergeTargetInput(string branchOrCommit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchOrCommit);
        if (branchOrCommit[0] == '-'
            || branchOrCommit.IndexOf('\0') >= 0
            || branchOrCommit.Contains('\r')
            || branchOrCommit.Contains('\n'))
        {
            throw new ArgumentException("The merge target contains an invalid character.", nameof(branchOrCommit));
        }
    }
}
