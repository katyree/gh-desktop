using System.Runtime.ExceptionServices;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private const int MaximumCommitSequenceLength = 1_000;

    /// <summary>Reads the current cherry-pick marker, sequence progress, and conflict paths.</summary>
    public async Task<CherryPickOperationState> GetCherryPickStateAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadCommitOperationSnapshotAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        return CreateCherryPickState(snapshot);
    }

    /// <summary>
    /// Cherry-picks one or more immutable commit IDs in the supplied order. Merge
    /// commits use their first parent, matching the Electron workflow.
    /// </summary>
    public async Task<CherryPickOperationResult> CherryPickAsync(
        string root,
        IReadOnlyList<string> commitIds,
        string expectedHeadId,
        CancellationToken cancellationToken)
    {
        ValidateSelectedCommitIds(commitIds, nameof(commitIds));
        ValidateExpectedHeadId(expectedHeadId);
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);

        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var before = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureCherryPickCanStart(before, expectedHeadId);
                var resolvedCommitIds = await ResolveSelectedCommitIdsAsync(
                    path,
                    commitIds,
                    cancellationToken).ConfigureAwait(false);

                try
                {
                    var arguments = new List<string>
                    {
                        "cherry-pick",
                        "--no-edit",
                        "--empty=keep",
                        "-m",
                        "1",
                        "--stdin",
                    };
                    await processRunner.RunAsync(
                        path,
                        arguments,
                        cancellationToken,
                        standardInput: string.Join(
                            Environment.NewLine,
                            resolvedCommitIds) + Environment.NewLine,
                        environmentOverrides: NoOpEditorEnvironment).ConfigureAwait(false);
                }
                catch (GitCommandException commandException)
                {
                    var failed = await ReadCommitOperationSnapshotPreservingFailureAsync(
                        path,
                        commandException,
                        cancellationToken).ConfigureAwait(false);
                    if (failed.OperationKind == GitOperationKind.CherryPick
                        && failed.HasUnresolvedConflicts)
                    {
                        var state = CreateCherryPickState(failed);
                        return new CherryPickOperationResult(
                            CherryPickOutcome.Conflicts,
                            state.CurrentHeadId,
                            state);
                    }

                    ExceptionDispatchInfo.Capture(commandException).Throw();
                    throw;
                }

                var after = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                var stateAfter = CreateCherryPickState(after);
                var outcome = stateAfter.IsInProgress
                    ? stateAfter.HasUnresolvedConflicts
                        ? CherryPickOutcome.Conflicts
                        : CherryPickOutcome.InProgress
                    : CherryPickOutcome.Completed;
                return new CherryPickOperationResult(outcome, stateAfter.CurrentHeadId, stateAfter);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Continues a cherry-pick after every tracked resolution is staged. When
    /// the staged resolutions leave nothing to commit for the current pick,
    /// an empty commit is recorded so the picked commit still shows up in
    /// history, matching the Electron continue behavior, before advancing.
    /// </summary>
    public async Task<CherryPickOperationResult> ContinueCherryPickAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var stateBefore = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureCherryPickCanContinue(stateBefore);

                if (!await HasStagedCommitResolutionAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    return await ContinueEmptiedCherryPickAsync(path, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await processRunner.RunAsync(
                        path,
                        ["cherry-pick", "--continue"],
                        cancellationToken,
                        environmentOverrides: NoOpEditorEnvironment).ConfigureAwait(false);
                }
                catch (GitCommandException commandException)
                {
                    var failed = await ReadCommitOperationSnapshotPreservingFailureAsync(
                        path,
                        commandException,
                        cancellationToken).ConfigureAwait(false);
                    if (failed.OperationKind == GitOperationKind.CherryPick
                        && failed.HasUnresolvedConflicts)
                    {
                        var state = CreateCherryPickState(failed);
                        return new CherryPickOperationResult(
                            CherryPickOutcome.Conflicts,
                            state.CurrentHeadId,
                            state);
                    }

                    ExceptionDispatchInfo.Capture(commandException).Throw();
                    throw;
                }

                var after = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                var stateAfter = CreateCherryPickState(after);
                var outcome = stateAfter.IsInProgress
                    ? stateAfter.HasUnresolvedConflicts
                        ? CherryPickOutcome.Conflicts
                        : CherryPickOutcome.InProgress
                    : CherryPickOutcome.Completed;
                return new CherryPickOperationResult(outcome, stateAfter.CurrentHeadId, stateAfter);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Records an emptied cherry-picked commit with <c>commit
    /// --allow-empty</c> and advances the sequencer when picks remain, so the
    /// picked commit remains visible in history. Reports the resulting
    /// sequence outcome.
    /// </summary>
    private async Task<CherryPickOperationResult> ContinueEmptiedCherryPickAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            await processRunner.RunAsync(
                repositoryRoot,
                ["commit", "--allow-empty", "--no-edit"],
                cancellationToken,
                environmentOverrides: NoOpEditorEnvironment).ConfigureAwait(false);
        }
        catch (GitCommandException commandException)
        {
            var commitFailed = await ReadCommitOperationSnapshotPreservingFailureAsync(
                repositoryRoot,
                commandException,
                cancellationToken).ConfigureAwait(false);
            if (commitFailed.OperationKind == GitOperationKind.CherryPick
                && commitFailed.HasUnresolvedConflicts)
            {
                var commitConflicted = CreateCherryPickState(commitFailed);
                return new CherryPickOperationResult(
                    CherryPickOutcome.Conflicts,
                    commitConflicted.CurrentHeadId,
                    commitConflicted);
            }

            ExceptionDispatchInfo.Capture(commandException).Throw();
            throw;
        }

        // Recording the empty commit finishes a final pick on its own; only
        // advance the sequencer explicitly when picks remain.
        var committed = await ReadCommitOperationSnapshotAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (committed.OperationKind != GitOperationKind.CherryPick)
        {
            var finished = CreateCherryPickState(committed);
            return new CherryPickOperationResult(CherryPickOutcome.Completed, finished.CurrentHeadId, finished);
        }

        try
        {
            await processRunner.RunAsync(
                repositoryRoot,
                ["cherry-pick", "--continue"],
                cancellationToken,
                environmentOverrides: NoOpEditorEnvironment).ConfigureAwait(false);
        }
        catch (GitCommandException commandException)
        {
            var continueFailed = await ReadCommitOperationSnapshotPreservingFailureAsync(
                repositoryRoot,
                commandException,
                cancellationToken).ConfigureAwait(false);
            if (continueFailed.OperationKind == GitOperationKind.CherryPick
                && continueFailed.HasUnresolvedConflicts)
            {
                var continueConflicted = CreateCherryPickState(continueFailed);
                return new CherryPickOperationResult(
                    CherryPickOutcome.Conflicts,
                    continueConflicted.CurrentHeadId,
                    continueConflicted);
            }

            ExceptionDispatchInfo.Capture(commandException).Throw();
            throw;
        }

        var after = await ReadCommitOperationSnapshotAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var stateAfter = CreateCherryPickState(after);
        var outcome = stateAfter.IsInProgress
            ? stateAfter.HasUnresolvedConflicts
                ? CherryPickOutcome.Conflicts
                : CherryPickOutcome.InProgress
            : CherryPickOutcome.Completed;
        return new CherryPickOperationResult(outcome, stateAfter.CurrentHeadId, stateAfter);
    }

    /// <summary>
    /// Reports whether the index holds staged changes. Called after the
    /// continue guards have refused missing sequences, other operations, and
    /// unresolved or unstaged resolutions, so an empty answer means the
    /// current pick has nothing to commit.
    /// </summary>
    private async Task<bool> HasStagedCommitResolutionAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        return status.Changes.Any(change =>
            !IsUntracked(change) && change.IndexStatus.Length > 0);
    }

    /// <summary>Explicitly skips the current cherry-picked commit.</summary>
    public async Task<CherryPickOperationResult> SkipCherryPickAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var stateBefore = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureCherryPickIsActive(stateBefore);
                try
                {
                    await processRunner.RunAsync(
                        path,
                        ["cherry-pick", "--skip"],
                        cancellationToken,
                        environmentOverrides: NoOpEditorEnvironment).ConfigureAwait(false);
                }
                catch (GitCommandException commandException)
                {
                    var failed = await ReadCommitOperationSnapshotPreservingFailureAsync(
                        path,
                        commandException,
                        cancellationToken).ConfigureAwait(false);
                    if (failed.OperationKind == GitOperationKind.CherryPick
                        && failed.HasUnresolvedConflicts)
                    {
                        var state = CreateCherryPickState(failed);
                        return new CherryPickOperationResult(
                            CherryPickOutcome.Conflicts,
                            state.CurrentHeadId,
                            state);
                    }

                    ExceptionDispatchInfo.Capture(commandException).Throw();
                    throw;
                }
                var after = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                var stateAfter = CreateCherryPickState(after);
                var outcome = stateAfter.IsInProgress
                    ? stateAfter.HasUnresolvedConflicts
                        ? CherryPickOutcome.Conflicts
                        : CherryPickOutcome.InProgress
                    : CherryPickOutcome.Skipped;
                return new CherryPickOperationResult(outcome, stateAfter.CurrentHeadId, stateAfter);
            }).ConfigureAwait(false);
    }

    /// <summary>Aborts an in-progress cherry-pick sequence using Git's safeguards.</summary>
    public async Task<CherryPickOperationResult> AbortCherryPickAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var stateBefore = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureCherryPickIsActive(stateBefore);
                await processRunner.RunAsync(
                    path,
                    ["cherry-pick", "--abort"],
                    cancellationToken).ConfigureAwait(false);
                var after = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                var stateAfter = CreateCherryPickState(after);
                return new CherryPickOperationResult(CherryPickOutcome.Aborted, stateAfter.CurrentHeadId, stateAfter);
            }).ConfigureAwait(false);
    }

    /// <summary>Reads the current revert marker, sequence progress, and conflict paths.</summary>
    public async Task<RevertOperationState> GetRevertStateAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadCommitOperationSnapshotAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        return CreateRevertState(snapshot);
    }

    /// <summary>
    /// Reverts one immutable commit ID. Merge commits use their first parent,
    /// matching the existing Electron action.
    /// </summary>
    public async Task<RevertOperationResult> RevertCommitAsync(
        string root,
        string commitId,
        string expectedHeadId,
        CancellationToken cancellationToken)
    {
        ValidateCommitId(commitId);
        ValidateExpectedHeadId(expectedHeadId);
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);

        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var before = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureRevertCanStart(before, expectedHeadId);
                var resolvedCommitId = await ResolveCommitIdAsync(
                    path,
                    commitId,
                    cancellationToken).ConfigureAwait(false);
                var parentIds = await ReadCommitParentIdsAsync(
                    path,
                    resolvedCommitId,
                    cancellationToken).ConfigureAwait(false);

                var arguments = new List<string>
                {
                    "revert",
                    "--no-edit",
                };
                if (parentIds.Count > 1)
                {
                    arguments.Add("-m");
                    arguments.Add("1");
                }

                arguments.Add(resolvedCommitId);
                try
                {
                    await processRunner.RunAsync(
                        path,
                        arguments,
                        cancellationToken,
                        environmentOverrides: NoOpEditorEnvironment).ConfigureAwait(false);
                }
                catch (GitCommandException commandException)
                {
                    var failed = await ReadCommitOperationSnapshotPreservingFailureAsync(
                        path,
                        commandException,
                        cancellationToken).ConfigureAwait(false);
                    if (failed.OperationKind == GitOperationKind.Revert
                        && failed.HasUnresolvedConflicts)
                    {
                        var state = CreateRevertState(failed);
                        return new RevertOperationResult(
                            RevertOutcome.Conflicts,
                            state.CurrentHeadId,
                            state);
                    }

                    ExceptionDispatchInfo.Capture(commandException).Throw();
                    throw;
                }

                var after = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                var stateAfter = CreateRevertState(after);
                var outcome = stateAfter.IsInProgress
                    ? stateAfter.HasUnresolvedConflicts
                        ? RevertOutcome.Conflicts
                        : RevertOutcome.InProgress
                    : RevertOutcome.Completed;
                return new RevertOperationResult(outcome, stateAfter.CurrentHeadId, stateAfter);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Continues a revert after every tracked resolution is staged. When the
    /// staged resolutions leave nothing to commit, an empty revert commit is
    /// recorded so the revert stays visible in history, matching the
    /// cherry-pick continue behavior.
    /// </summary>
    public async Task<RevertOperationResult> ContinueRevertAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var stateBefore = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureRevertCanContinue(stateBefore);

                if (!await HasStagedCommitResolutionAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    return await ContinueEmptiedRevertAsync(path, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await processRunner.RunAsync(
                        path,
                        ["revert", "--continue"],
                        cancellationToken,
                        environmentOverrides: NoOpEditorEnvironment).ConfigureAwait(false);
                }
                catch (GitCommandException commandException)
                {
                    var failed = await ReadCommitOperationSnapshotPreservingFailureAsync(
                        path,
                        commandException,
                        cancellationToken).ConfigureAwait(false);
                    if (failed.OperationKind == GitOperationKind.Revert
                        && failed.HasUnresolvedConflicts)
                    {
                        var state = CreateRevertState(failed);
                        return new RevertOperationResult(
                            RevertOutcome.Conflicts,
                            state.CurrentHeadId,
                            state);
                    }

                    ExceptionDispatchInfo.Capture(commandException).Throw();
                    throw;
                }

                var after = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                var stateAfter = CreateRevertState(after);
                var outcome = stateAfter.IsInProgress
                    ? stateAfter.HasUnresolvedConflicts
                        ? RevertOutcome.Conflicts
                        : RevertOutcome.InProgress
                    : RevertOutcome.Completed;
                return new RevertOperationResult(outcome, stateAfter.CurrentHeadId, stateAfter);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Records an emptied revert with <c>commit --allow-empty</c> and advances
    /// the sequencer when work remains, so the revert remains visible in
    /// history. Reports the resulting sequence outcome.
    /// </summary>
    private async Task<RevertOperationResult> ContinueEmptiedRevertAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            await processRunner.RunAsync(
                repositoryRoot,
                ["commit", "--allow-empty", "--no-edit"],
                cancellationToken,
                environmentOverrides: NoOpEditorEnvironment).ConfigureAwait(false);
        }
        catch (GitCommandException commandException)
        {
            var commitFailed = await ReadCommitOperationSnapshotPreservingFailureAsync(
                repositoryRoot,
                commandException,
                cancellationToken).ConfigureAwait(false);
            if (commitFailed.OperationKind == GitOperationKind.Revert
                && commitFailed.HasUnresolvedConflicts)
            {
                var commitConflicted = CreateRevertState(commitFailed);
                return new RevertOperationResult(
                    RevertOutcome.Conflicts,
                    commitConflicted.CurrentHeadId,
                    commitConflicted);
            }

            ExceptionDispatchInfo.Capture(commandException).Throw();
            throw;
        }

        // Recording the empty commit finishes a final revert on its own; only
        // advance the sequencer explicitly when work remains.
        var committed = await ReadCommitOperationSnapshotAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (committed.OperationKind != GitOperationKind.Revert)
        {
            var finished = CreateRevertState(committed);
            return new RevertOperationResult(RevertOutcome.Completed, finished.CurrentHeadId, finished);
        }

        try
        {
            await processRunner.RunAsync(
                repositoryRoot,
                ["revert", "--continue"],
                cancellationToken,
                environmentOverrides: NoOpEditorEnvironment).ConfigureAwait(false);
        }
        catch (GitCommandException commandException)
        {
            var continueFailed = await ReadCommitOperationSnapshotPreservingFailureAsync(
                repositoryRoot,
                commandException,
                cancellationToken).ConfigureAwait(false);
            if (continueFailed.OperationKind == GitOperationKind.Revert
                && continueFailed.HasUnresolvedConflicts)
            {
                var continueConflicted = CreateRevertState(continueFailed);
                return new RevertOperationResult(
                    RevertOutcome.Conflicts,
                    continueConflicted.CurrentHeadId,
                    continueConflicted);
            }

            ExceptionDispatchInfo.Capture(commandException).Throw();
            throw;
        }

        var after = await ReadCommitOperationSnapshotAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var stateAfter = CreateRevertState(after);
        var outcome = stateAfter.IsInProgress
            ? stateAfter.HasUnresolvedConflicts
                ? RevertOutcome.Conflicts
                : RevertOutcome.InProgress
            : RevertOutcome.Completed;
        return new RevertOperationResult(outcome, stateAfter.CurrentHeadId, stateAfter);
    }

    /// <summary>Explicitly skips the current revert commit.</summary>
    public async Task<RevertOperationResult> SkipRevertAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var stateBefore = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureRevertIsActive(stateBefore);
                try
                {
                    await processRunner.RunAsync(
                        path,
                        ["revert", "--skip"],
                        cancellationToken,
                        environmentOverrides: NoOpEditorEnvironment).ConfigureAwait(false);
                }
                catch (GitCommandException commandException)
                {
                    var failed = await ReadCommitOperationSnapshotPreservingFailureAsync(
                        path,
                        commandException,
                        cancellationToken).ConfigureAwait(false);
                    if (failed.OperationKind == GitOperationKind.Revert
                        && failed.HasUnresolvedConflicts)
                    {
                        var state = CreateRevertState(failed);
                        return new RevertOperationResult(
                            RevertOutcome.Conflicts,
                            state.CurrentHeadId,
                            state);
                    }

                    ExceptionDispatchInfo.Capture(commandException).Throw();
                    throw;
                }
                var after = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                var stateAfter = CreateRevertState(after);
                var outcome = stateAfter.IsInProgress
                    ? stateAfter.HasUnresolvedConflicts
                        ? RevertOutcome.Conflicts
                        : RevertOutcome.InProgress
                    : RevertOutcome.Skipped;
                return new RevertOperationResult(outcome, stateAfter.CurrentHeadId, stateAfter);
            }).ConfigureAwait(false);
    }

    /// <summary>Aborts an in-progress revert sequence using Git's safeguards.</summary>
    public async Task<RevertOperationResult> AbortRevertAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var stateBefore = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureRevertIsActive(stateBefore);
                await processRunner.RunAsync(
                    path,
                    ["revert", "--abort"],
                    cancellationToken).ConfigureAwait(false);
                var after = await ReadCommitOperationSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                var stateAfter = CreateRevertState(after);
                return new RevertOperationResult(RevertOutcome.Aborted, stateAfter.CurrentHeadId, stateAfter);
            }).ConfigureAwait(false);
    }

    private async Task<CommitOperationSnapshot> ReadCommitOperationSnapshotAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var mergeState = await ReadMergeStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var operationKind = mergeState.OperationKind;
        var sequenceDirectory = await ReadGitPathAsync(
            repositoryRoot,
            "sequencer",
            cancellationToken).ConfigureAwait(false);
        var sequenceDirectoryExists = sequenceDirectory is not null && Directory.Exists(sequenceDirectory);

        IReadOnlyList<SequenceCommand> done = [];
        IReadOnlyList<SequenceCommand> todo = [];
        if (sequenceDirectoryExists)
        {
            done = await ReadSequenceCommandsAsync(
                sequenceDirectory,
                "done",
                cancellationToken).ConfigureAwait(false);
            todo = await ReadSequenceCommandsAsync(
                sequenceDirectory,
                "todo",
                cancellationToken).ConfigureAwait(false);
        }

        if (operationKind == GitOperationKind.Sequencer)
        {
            operationKind = InferSequencerOperationKind(done, todo);
        }

        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (operationKind is not (GitOperationKind.CherryPick or GitOperationKind.Revert))
        {
            return new CommitOperationSnapshot(
                operationKind,
                status.HeadId,
                status.Branch,
                OriginalHeadId: null,
                CurrentStep: 0,
                TotalSteps: 0,
                CurrentCommitId: null,
                CommitIds: [],
                mergeState.UnmergedPaths,
                status.Changes.Where(IsTrackedUnstagedChange).ToArray());
        }

        var markerName = operationKind == GitOperationKind.CherryPick
            ? "CHERRY_PICK_HEAD"
            : "REVERT_HEAD";
        var currentCommitId = await ReadRefIdAsync(
            repositoryRoot,
            markerName,
            cancellationToken).ConfigureAwait(false);
        var originalHeadId = await ReadSequenceObjectIdAsync(
            sequenceDirectory,
            "head",
            cancellationToken).ConfigureAwait(false);
        if (originalHeadId is null && status.HeadId.Length > 0)
        {
            // Single-commit conflicts have no sequencer/head file; HEAD is still
            // the target tip because Git has not created the operation commit.
            originalHeadId = status.HeadId;
        }

        // Cherry-pick's sequencer does not create a done file on the current
        // Git version. The Electron implementation derives completed commits
        // from head..abort-safety, which also remains correct after --skip.
        var abortSafetyId = await ReadSequenceObjectIdAsync(
            sequenceDirectory,
            "abort-safety",
            cancellationToken).ConfigureAwait(false);
        var completedCommitIds = await ReadCompletedSequenceCommitIdsAsync(
            repositoryRoot,
            originalHeadId,
            abortSafetyId,
            cancellationToken).ConfigureAwait(false);
        var commitIds = completedCommitIds
            .Concat(todo.Select(command => command.CommitId))
            .ToList();
        if (currentCommitId is not null && commitIds.Count == 0)
        {
            commitIds.Add(currentCommitId);
        }

        var totalSteps = commitIds.Count;
        if (totalSteps == 0 && currentCommitId is not null)
        {
            totalSteps = 1;
        }

        var currentStep = totalSteps == 0
            ? 0
            : Math.Min(completedCommitIds.Count + 1, totalSteps);
        if (currentCommitId is null && todo.Count > 0)
        {
            currentCommitId = todo[0].CommitId;
        }

        return new CommitOperationSnapshot(
            operationKind,
            status.HeadId,
            status.Branch,
            originalHeadId,
            currentStep,
            totalSteps,
            currentCommitId,
            commitIds,
            mergeState.UnmergedPaths,
            status.Changes.Where(IsTrackedUnstagedChange).ToArray());
    }

    private async Task<IReadOnlyList<string>> ReadCompletedSequenceCommitIdsAsync(
        string repositoryRoot,
        string? originalHeadId,
        string? abortSafetyId,
        CancellationToken cancellationToken)
    {
        if (originalHeadId is null
            || abortSafetyId is null
            || string.Equals(originalHeadId, abortSafetyId, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var result = await processRunner.RunAsync(
            repositoryRoot,
            [
                "rev-list",
                "--reverse",
                $"--max-count={MaximumCommitSequenceLength}",
                "--no-abbrev-commit",
                $"{originalHeadId}..{abortSafetyId}",
                "--",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "commit operation progress");

        var completed = new List<string>();
        foreach (var rawLine in DecodeUtf8(result.StandardOutput, "commit operation progress")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var commitId = rawLine.Trim();
            ValidateObjectId(commitId, "completed commit ID");
            completed.Add(commitId);
        }

        return completed;
    }

    private async Task<CommitOperationSnapshot> ReadCommitOperationSnapshotPreservingFailureAsync(
        string repositoryRoot,
        GitCommandException commandException,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCommitOperationSnapshotAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ExceptionDispatchInfo.Capture(commandException).Throw();
            throw;
        }
    }

    private async Task<IReadOnlyList<SequenceCommand>> ReadSequenceCommandsAsync(
        string? sequenceDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (sequenceDirectory is null)
        {
            return [];
        }

        var path = Path.Combine(sequenceDirectory, fileName);
        if (!File.Exists(path))
        {
            return [];
        }

        var bounded = await ReadBoundedFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (bounded.Truncated)
        {
            throw new GitOutputLimitException("commit operation state");
        }

        var text = DecodeUtf8(bounded.Bytes, "commit operation state");
        var commands = new List<SequenceCommand>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var firstSpace = line.IndexOfAny([' ', '\t']);
            if (firstSpace <= 0 || firstSpace == line.Length - 1)
            {
                continue;
            }

            var action = line[..firstSpace];
            if (action is not ("pick" or "revert"))
            {
                continue;
            }

            var remainder = line[(firstSpace + 1)..].TrimStart(' ', '\t');
            var separator = remainder.IndexOfAny([' ', '\t']);
            var commitId = separator < 0 ? remainder : remainder[..separator];
            ValidateCommitId(commitId);
            commands.Add(new SequenceCommand(action, commitId));
            if (commands.Count > MaximumCommitSequenceLength)
            {
                throw new GitOutputLimitException("commit operation state");
            }
        }

        return commands;
    }

    private async Task<string?> ReadSequenceObjectIdAsync(
        string? sequenceDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (sequenceDirectory is null)
        {
            return null;
        }

        var path = Path.Combine(sequenceDirectory, fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var bounded = await ReadBoundedFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (bounded.Truncated)
        {
            throw new GitOutputLimitException("commit operation state");
        }

        var objectId = DecodeUtf8(bounded.Bytes, "commit operation state").Trim();
        if (objectId.Length == 0)
        {
            return null;
        }

        ValidateObjectId(objectId, "commit operation head");
        return objectId;
    }

    private async Task<IReadOnlyList<string>> ResolveSelectedCommitIdsAsync(
        string repositoryRoot,
        IReadOnlyList<string> commitIds,
        CancellationToken cancellationToken)
    {
        var resolved = new List<string>(commitIds.Count);
        foreach (var commitId in commitIds)
        {
            resolved.Add(await ResolveCommitIdAsync(
                repositoryRoot,
                commitId,
                cancellationToken).ConfigureAwait(false));
        }

        return resolved;
    }

    private async Task<IReadOnlyList<string>> ReadCommitParentIdsAsync(
        string repositoryRoot,
        string commitId,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["rev-list", "--parents", "--max-count=1", "--no-walk", "--end-of-options", commitId],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "commit parent lookup");
        var fields = DecodeUtf8(result.StandardOutput, "commit parent lookup")
            .Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0)
        {
            throw new InvalidOperationException("Git returned no commit parent metadata.");
        }

        ValidateObjectId(fields[0], "commit ID");
        var parents = new List<string>(Math.Max(fields.Length - 1, 0));
        foreach (var parent in fields.Skip(1))
        {
            ValidateObjectId(parent, "commit parent ID");
            parents.Add(parent);
        }

        return parents;
    }

    private static GitOperationKind InferSequencerOperationKind(
        IReadOnlyList<SequenceCommand> done,
        IReadOnlyList<SequenceCommand> todo)
    {
        var command = done.Concat(todo).FirstOrDefault();
        return command?.Action switch
        {
            "pick" => GitOperationKind.CherryPick,
            "revert" => GitOperationKind.Revert,
            _ => GitOperationKind.Sequencer,
        };
    }

    private static CherryPickOperationState CreateCherryPickState(CommitOperationSnapshot snapshot) =>
        new(
            snapshot.OperationKind,
            snapshot.CurrentHeadId,
            snapshot.Branch,
            snapshot.OriginalHeadId,
            snapshot.CurrentStep,
            snapshot.TotalSteps,
            snapshot.CurrentCommitId,
            snapshot.CommitIds,
            snapshot.UnmergedPaths,
            snapshot.UnstagedPaths);

    private static RevertOperationState CreateRevertState(CommitOperationSnapshot snapshot) =>
        new(
            snapshot.OperationKind,
            snapshot.CurrentHeadId,
            snapshot.Branch,
            snapshot.OriginalHeadId,
            snapshot.CurrentStep,
            snapshot.TotalSteps,
            snapshot.CurrentCommitId,
            snapshot.CommitIds,
            snapshot.UnmergedPaths,
            snapshot.UnstagedPaths);

    private static void EnsureCherryPickCanStart(
        CommitOperationSnapshot state,
        string expectedHeadId)
    {
        if (state.CurrentHeadId.Length == 0)
        {
            throw new CherryPickOperationBlockedException(
                CherryPickOperationFailureReason.UnbornRepository,
                CreateCherryPickState(state),
                "Cherry-pick is unavailable because the repository has no current commit.");
        }

        if (!string.Equals(state.CurrentHeadId, expectedHeadId, StringComparison.OrdinalIgnoreCase))
        {
            throw new CherryPickOperationBlockedException(
                CherryPickOperationFailureReason.StaleHead,
                CreateCherryPickState(state),
                "Cherry-pick was not applied because the current commit changed; refresh the repository first.");
        }

        if (state.OperationKind != GitOperationKind.None)
        {
            throw new CherryPickOperationBlockedException(
                CherryPickOperationFailureReason.DifferentOperationInProgress,
                CreateCherryPickState(state),
                "Cherry-pick is unavailable while another Git operation is in progress.");
        }

        if (state.HasUnresolvedConflicts)
        {
            throw new CherryPickOperationBlockedException(
                CherryPickOperationFailureReason.UnresolvedConflicts,
                CreateCherryPickState(state),
                "Cherry-pick is unavailable while the index contains unresolved conflicts.");
        }
    }

    private static void EnsureCherryPickIsActive(CommitOperationSnapshot state)
    {
        if (state.OperationKind != GitOperationKind.CherryPick)
        {
            throw new CherryPickOperationBlockedException(
                state.OperationKind == GitOperationKind.None
                    ? CherryPickOperationFailureReason.NotInProgress
                    : CherryPickOperationFailureReason.DifferentOperationInProgress,
                CreateCherryPickState(state),
                state.OperationKind == GitOperationKind.None
                    ? "There is no in-progress Git cherry-pick."
                    : "Only an in-progress cherry-pick can be handled by this operation.");
        }

    }

    private static void EnsureCherryPickCanContinue(CommitOperationSnapshot state)
    {
        EnsureCherryPickIsActive(state);
        if (state.HasUnresolvedConflicts)
        {
            throw new CherryPickOperationBlockedException(
                CherryPickOperationFailureReason.UnresolvedConflicts,
                CreateCherryPickState(state),
                "Resolve and stage every unmerged path before continuing the cherry-pick.");
        }

        if (state.HasUnstagedChanges)
        {
            throw new CherryPickOperationBlockedException(
                CherryPickOperationFailureReason.UnstagedChanges,
                CreateCherryPickState(state),
                "Stage every tracked conflict resolution before continuing the cherry-pick.");
        }
    }

    private static void EnsureRevertCanStart(
        CommitOperationSnapshot state,
        string expectedHeadId)
    {
        if (state.CurrentHeadId.Length == 0)
        {
            throw new RevertOperationBlockedException(
                RevertOperationFailureReason.UnbornRepository,
                CreateRevertState(state),
                "Revert is unavailable because the repository has no current commit.");
        }

        if (!string.Equals(state.CurrentHeadId, expectedHeadId, StringComparison.OrdinalIgnoreCase))
        {
            throw new RevertOperationBlockedException(
                RevertOperationFailureReason.StaleHead,
                CreateRevertState(state),
                "Revert was not applied because the current commit changed; refresh the repository first.");
        }

        if (state.OperationKind != GitOperationKind.None)
        {
            throw new RevertOperationBlockedException(
                RevertOperationFailureReason.DifferentOperationInProgress,
                CreateRevertState(state),
                "Revert is unavailable while another Git operation is in progress.");
        }

        if (state.HasUnresolvedConflicts)
        {
            throw new RevertOperationBlockedException(
                RevertOperationFailureReason.UnresolvedConflicts,
                CreateRevertState(state),
                "Revert is unavailable while the index contains unresolved conflicts.");
        }
    }

    private static void EnsureRevertIsActive(CommitOperationSnapshot state)
    {
        if (state.OperationKind != GitOperationKind.Revert)
        {
            throw new RevertOperationBlockedException(
                state.OperationKind == GitOperationKind.None
                    ? RevertOperationFailureReason.NotInProgress
                    : RevertOperationFailureReason.DifferentOperationInProgress,
                CreateRevertState(state),
                state.OperationKind == GitOperationKind.None
                    ? "There is no in-progress Git revert."
                    : "Only an in-progress revert can be handled by this operation.");
        }
    }

    private static void EnsureRevertCanContinue(CommitOperationSnapshot state)
    {
        EnsureRevertIsActive(state);
        if (state.HasUnresolvedConflicts)
        {
            throw new RevertOperationBlockedException(
                RevertOperationFailureReason.UnresolvedConflicts,
                CreateRevertState(state),
                "Resolve and stage every unmerged path before continuing the revert.");
        }

        if (state.HasUnstagedChanges)
        {
            throw new RevertOperationBlockedException(
                RevertOperationFailureReason.UnstagedChanges,
                CreateRevertState(state),
                "Stage every tracked conflict resolution before continuing the revert.");
        }
    }

    private static void ValidateSelectedCommitIds(
        IReadOnlyList<string> commitIds,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(commitIds);
        if (commitIds.Count == 0)
        {
            throw new ArgumentException("At least one commit must be selected.", parameterName);
        }

        if (commitIds.Count > MaximumCommitSequenceLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"A commit sequence cannot contain more than {MaximumCommitSequenceLength} commits.");
        }

        foreach (var commitId in commitIds)
        {
            ValidateCommitId(commitId);
        }
    }

    private sealed record CommitOperationSnapshot(
        GitOperationKind OperationKind,
        string CurrentHeadId,
        string Branch,
        string? OriginalHeadId,
        int CurrentStep,
        int TotalSteps,
        string? CurrentCommitId,
        IReadOnlyList<string> CommitIds,
        IReadOnlyList<FileChange> UnmergedPaths,
        IReadOnlyList<FileChange> UnstagedPaths)
    {
        public bool HasUnresolvedConflicts => UnmergedPaths.Count > 0;

        public bool HasUnstagedChanges => UnstagedPaths.Count > 0;
    }

    private sealed record SequenceCommand(string Action, string CommitId);
}
