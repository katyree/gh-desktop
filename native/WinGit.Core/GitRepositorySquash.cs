using System.Text;
using System.Text.Json;

namespace WinGit.Core;

/// <summary>A commit entry in an immutable squash preview.</summary>
public sealed record SquashPlanCommit(string Id, string Summary);

/// <summary>The action Git will perform for one commit in a squash todo.</summary>
public enum SquashPlanAction
{
    Pick,
    Squash,
}

/// <summary>A single, chronological entry in a captured squash todo.</summary>
public sealed record SquashPlanStep(SquashPlanAction Action, SquashPlanCommit Commit);

/// <summary>The captured squash plan and replacement message needed to resume a stopped replay.</summary>
public sealed record SquashRecovery(SquashPlan Plan, string ReplacementMessage);

/// <summary>The repository-scoped squash recovery marker, if one is valid.</summary>
public sealed record SquashRecoveryReadResult(
    SquashRecovery? Recovery,
    bool IsUnavailable);

/// <summary>
/// The complete immutable squash plan captured from one branch tip. Commit
/// lists and replay steps are chronological, oldest to newest, even though the
/// History view normally displays commits newest first.
/// </summary>
public sealed class SquashPlan
{
    internal SquashPlan(
        string rootPath,
        string branch,
        string expectedHeadId,
        string? lastRetainedCommitId,
        SquashPlanCommit targetCommit,
        SquashPlanCommit resultingCommit,
        CommitIdentity resultingCommitAuthor,
        IReadOnlyList<SquashPlanCommit> originalCommits,
        IReadOnlyList<SquashPlanCommit> selectedCommits,
        IReadOnlyList<SquashPlanStep> replaySteps)
    {
        RootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        Branch = branch ?? throw new ArgumentNullException(nameof(branch));
        ExpectedHeadId = expectedHeadId ?? throw new ArgumentNullException(nameof(expectedHeadId));
        LastRetainedCommitId = lastRetainedCommitId;
        TargetCommit = targetCommit ?? throw new ArgumentNullException(nameof(targetCommit));
        ResultingCommit = resultingCommit ?? throw new ArgumentNullException(nameof(resultingCommit));
        ResultingCommitAuthor = resultingCommitAuthor ?? throw new ArgumentNullException(nameof(resultingCommitAuthor));
        OriginalCommits = Copy(originalCommits);
        SelectedCommits = Copy(selectedCommits);
        ReplaySteps = Copy(replaySteps);
        SelectedCommitIds = SelectedCommits.Select(commit => commit.Id).ToList().AsReadOnly();
        ReplayCommitIds = ReplaySteps.Select(step => step.Commit.Id).ToList().AsReadOnly();
    }

    public string RootPath { get; }

    public string Branch { get; }

    /// <summary>The branch tip that must still be checked out before replay.</summary>
    public string ExpectedHeadId { get; }

    /// <summary>The commit before the replay range, or null for a root replay.</summary>
    public string? LastRetainedCommitId { get; }

    /// <summary>The commit selected as the squash target in the History view.</summary>
    public SquashPlanCommit TargetCommit { get; }

    /// <summary>
    /// The first commit in the generated squash group. Git keeps this commit's
    /// author when no explicit author override is requested.
    /// </summary>
    public SquashPlanCommit ResultingCommit { get; }

    public CommitIdentity ResultingCommitAuthor { get; }

    /// <summary>Every commit in the captured replay range, oldest to newest.</summary>
    public IReadOnlyList<SquashPlanCommit> OriginalCommits { get; }

    /// <summary>The selected commits in captured chronological order.</summary>
    public IReadOnlyList<SquashPlanCommit> SelectedCommits { get; }

    /// <summary>The exact pick/squash todo order that will be replayed by Git.</summary>
    public IReadOnlyList<SquashPlanStep> ReplaySteps { get; }

    public IReadOnlyList<string> SelectedCommitIds { get; }

    public IReadOnlyList<string> ReplayCommitIds { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new List<T>(values).AsReadOnly();
    }
}

public enum SquashOperationFailureReason
{
    EmptySelection,
    DuplicateSelection,
    MissingCommit,
    ForeignCommit,
    InvalidTarget,
    TargetInSelection,
    MergeCommitInRange,
    DirtyWorktree,
    DetachedHead,
    UnbornRepository,
    OperationInProgress,
    StalePlan,
    InvalidMessage,
}

/// <summary>Raised when a captured squash cannot safely be previewed or applied.</summary>
public sealed class SquashOperationBlockedException : InvalidOperationException
{
    public SquashOperationBlockedException(
        SquashOperationFailureReason reason,
        string message,
        string? commitId = null)
        : base(message)
    {
        Reason = reason;
        CommitId = commitId;
    }

    public SquashOperationFailureReason Reason { get; }

    public string? CommitId { get; }
}

public sealed partial class GitRepositoryService
{
    private const int MaximumSquashPlanCommits = 100_000;
    private const int MaximumSquashRecoveryMessageLength = 1_000_000;
    private const int SquashRecoveryVersion = 1;
    private const string SquashRecoveryFileName = "wingit-squash-recovery.json";
    private static readonly JsonSerializerOptions SquashRecoveryJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class SquashRecoveryFile
    {
        public SquashRecoveryFile()
        {
        }

        public int Version { get; set; }

        public string RootPath { get; set; } = string.Empty;

        public string Branch { get; set; } = string.Empty;

        public string ExpectedHeadId { get; set; } = string.Empty;

        public string? LastRetainedCommitId { get; set; }

        public SquashRecoveryCommitFile TargetCommit { get; set; } = new();

        public SquashRecoveryCommitFile ResultingCommit { get; set; } = new();

        public string ResultingAuthorName { get; set; } = string.Empty;

        public string ResultingAuthorEmail { get; set; } = string.Empty;

        public SquashRecoveryStepFile[] ReplaySteps { get; set; } = [];

        public string ReplacementMessage { get; set; } = string.Empty;
    }

    private sealed class SquashRecoveryCommitFile
    {
        public SquashRecoveryCommitFile()
        {
        }

        public string Id { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;
    }

    private sealed class SquashRecoveryStepFile
    {
        public SquashRecoveryStepFile()
        {
        }

        public int Action { get; set; }

        public SquashRecoveryCommitFile Commit { get; set; } = new();
    }

    /// <summary>
    /// Captures the exact Git history range and squash todo. The caller may
    /// provide IDs in any order; selected IDs are normalized to Git's current
    /// chronological order before the todo is built.
    /// </summary>
    public async Task<SquashPlan> CaptureSquashPlanAsync(
        string root,
        IReadOnlyList<string> selectedCommitIds,
        string squashOntoCommitId,
        string? lastRetainedCommitId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedCommitIds);
        if (selectedCommitIds.Count == 0)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.EmptySelection,
                "Select at least one other commit to squash.");
        }

        var normalizedSelectedIds = ValidateSelectedSquashIds(selectedCommitIds);
        var normalizedTargetId = ValidateSquashId(squashOntoCommitId, nameof(squashOntoCommitId));
        var normalizedLastRetainedId = ValidateOptionalSquashId(
            lastRetainedCommitId,
            nameof(lastRetainedCommitId));
        if (normalizedSelectedIds.Contains(normalizedTargetId, StringComparer.OrdinalIgnoreCase))
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.TargetInSelection,
                "The commit being squashed onto must not also be selected.",
                normalizedTargetId);
        }

        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        EnsureSquashRepositoryReady(status);

        var operation = await ReadMergeStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (operation.OperationKind != GitOperationKind.None || operation.HasUnresolvedConflicts)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.OperationInProgress,
                "Squashing is unavailable while another Git operation or unresolved conflict is active.");
        }

        IReadOnlyList<ReorderHistoryEntry> history;
        try
        {
            history = await ReadReorderHistoryAsync(
                repositoryRoot,
                normalizedLastRetainedId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ReorderOperationBlockedException exception)
        {
            throw new SquashOperationBlockedException(
                exception.Reason switch
                {
                    ReorderOperationFailureReason.ForeignCommit => SquashOperationFailureReason.ForeignCommit,
                    ReorderOperationFailureReason.InvalidTarget => SquashOperationFailureReason.InvalidTarget,
                    _ => SquashOperationFailureReason.InvalidTarget,
                },
                exception.Message,
                exception.CommitId);
        }

        if (history.Count == 0)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.InvalidTarget,
                "The selected replay range does not contain any commits.");
        }

        if (history.Count > MaximumSquashPlanCommits)
        {
            throw new GitOutputLimitException("squash history");
        }

        var historyById = history.ToDictionary(
            entry => entry.Id,
            StringComparer.OrdinalIgnoreCase);
        foreach (var selectedId in normalizedSelectedIds)
        {
            if (historyById.ContainsKey(selectedId))
            {
                continue;
            }

            await ThrowForSquashCommitOutsideRangeAsync(
                repositoryRoot,
                selectedId,
                cancellationToken).ConfigureAwait(false);
        }

        if (!historyById.ContainsKey(normalizedTargetId))
        {
            await ThrowForSquashCommitOutsideRangeAsync(
                repositoryRoot,
                normalizedTargetId,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in history)
        {
            if (entry.ParentCount > 1)
            {
                throw new SquashOperationBlockedException(
                    SquashOperationFailureReason.MergeCommitInRange,
                    "Squashing cannot replay a range that contains a merge commit.",
                    entry.Id);
            }
        }

        var selectedSet = normalizedSelectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = history
            .Where(entry => selectedSet.Contains(entry.Id))
            .Select(ToSquashPlanCommit)
            .ToArray();
        if (selected.Length != selectedSet.Count)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.ForeignCommit,
                "One or more selected commits are outside the current branch replay range.");
        }

        var targetEntry = historyById[normalizedTargetId];
        var target = ToSquashPlanCommit(targetEntry);
        var replaySteps = BuildSquashReplaySteps(history, selectedSet, normalizedTargetId, out var resultingCommit);
        var resultingAuthor = await ReadSquashCommitAuthorAsync(
            repositoryRoot,
            resultingCommit.Id,
            cancellationToken).ConfigureAwait(false);

        return new SquashPlan(
            repositoryRoot,
            status.Branch,
            status.HeadId,
            normalizedLastRetainedId,
            target,
            resultingCommit,
            resultingAuthor,
            history.Select(ToSquashPlanCommit).ToArray(),
            selected,
            replaySteps);
    }

    /// <summary>
    /// Captures a squash range using the smallest first-parent base containing
    /// the selected commits and target. This avoids treating a visible history
    /// window or an older merge as the replay root.
    /// </summary>
    public async Task<SquashPlan> CaptureSquashPlanFromCurrentHistoryAsync(
        string root,
        IReadOnlyList<string> selectedCommitIds,
        string squashOntoCommitId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedCommitIds);
        if (selectedCommitIds.Count == 0)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.EmptySelection,
                "Select at least one other commit to squash.");
        }

        var normalizedSelectedIds = ValidateSelectedSquashIds(selectedCommitIds);
        var normalizedTargetId = ValidateSquashId(squashOntoCommitId, nameof(squashOntoCommitId));
        if (normalizedSelectedIds.Contains(normalizedTargetId, StringComparer.OrdinalIgnoreCase))
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.TargetInSelection,
                "The commit being squashed onto must not also be selected.",
                normalizedTargetId);
        }

        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        EnsureSquashRepositoryReady(status);

        var operation = await ReadMergeStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (operation.OperationKind != GitOperationKind.None || operation.HasUnresolvedConflicts)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.OperationInProgress,
                "Squashing is unavailable while another Git operation or unresolved conflict is active.");
        }

        IReadOnlyList<ReorderHistoryEntry> firstParentHistory;
        try
        {
            firstParentHistory = await ReadReorderHistoryAsync(
                repositoryRoot,
                lastRetainedCommitId: null,
                cancellationToken,
                firstParentOnly: true).ConfigureAwait(false);
        }
        catch (ReorderOperationBlockedException exception)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.InvalidTarget,
                exception.Message,
                exception.CommitId);
        }

        if (firstParentHistory.Count == 0)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.InvalidTarget,
                "The current branch does not contain any commits.");
        }

        if (firstParentHistory.Count > MaximumSquashPlanCommits)
        {
            throw new GitOutputLimitException("squash first-parent history");
        }

        var relevantIds = normalizedSelectedIds.ToList();
        relevantIds.Add(normalizedTargetId);
        var earliestIndex = int.MaxValue;
        foreach (var relevantId in relevantIds)
        {
            for (var index = 0; index < firstParentHistory.Count; index++)
            {
                if (string.Equals(firstParentHistory[index].Id, relevantId, StringComparison.OrdinalIgnoreCase))
                {
                    earliestIndex = Math.Min(earliestIndex, index);
                    break;
                }
            }
        }

        var lastRetainedCommitId = earliestIndex == int.MaxValue
            ? null
            : firstParentHistory[earliestIndex].FirstParentId;
        return await CaptureSquashPlanAsync(
            repositoryRoot,
            normalizedSelectedIds,
            normalizedTargetId,
            lastRetainedCommitId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the recovery marker for the currently active rebase, if the
    /// marker belongs to a captured squash on the same branch and original
    /// tip. A malformed or mismatched marker is reported as unavailable so a
    /// caller never falls back to a generic message editor silently.
    /// </summary>
    public async Task<SquashRecoveryReadResult> GetSquashRecoveryAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        var state = await ReadRebaseStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (!state.IsInProgress)
        {
            return new SquashRecoveryReadResult(null, IsUnavailable: false);
        }

        var recoveryPath = await ResolveSquashRecoveryPathAsync(
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        if (recoveryPath is null || !File.Exists(recoveryPath))
        {
            return new SquashRecoveryReadResult(null, IsUnavailable: false);
        }

        if (ContainsReparsePoint(repositoryRoot, recoveryPath))
        {
            return new SquashRecoveryReadResult(null, IsUnavailable: true);
        }

        try
        {
            var bounded = await ReadBoundedFileAsync(recoveryPath, cancellationToken).ConfigureAwait(false);
            if (bounded.Truncated)
            {
                return new SquashRecoveryReadResult(null, IsUnavailable: true);
            }

            var json = StrictUtf8.GetString(bounded.Bytes);
            var file = JsonSerializer.Deserialize<SquashRecoveryFile>(json, SquashRecoveryJsonOptions);
            return TryCreateSquashRecovery(
                repositoryRoot,
                state,
                file,
                out var recovery)
                ? new SquashRecoveryReadResult(recovery, IsUnavailable: false)
                : new SquashRecoveryReadResult(null, IsUnavailable: true);
        }
        catch (DecoderFallbackException)
        {
            return new SquashRecoveryReadResult(null, IsUnavailable: true);
        }
        catch (JsonException)
        {
            return new SquashRecoveryReadResult(null, IsUnavailable: true);
        }
        catch (NotSupportedException)
        {
            return new SquashRecoveryReadResult(null, IsUnavailable: true);
        }
        catch (IOException)
        {
            return new SquashRecoveryReadResult(null, IsUnavailable: true);
        }
        catch (UnauthorizedAccessException)
        {
            return new SquashRecoveryReadResult(null, IsUnavailable: true);
        }
    }

    /// <summary>
    /// Applies a captured plan through Git's interactive rebase machinery. A
    /// nonblank replacement message is supplied through an owned editor file;
    /// user text is never placed in Git arguments or shell commands.
    /// </summary>
    public async Task<RebaseOperationResult> SquashAsync(
        string root,
        SquashPlan plan,
        string? replacementMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        replacementMessage = NormalizeSquashReplacementMessage(replacementMessage);
        ValidateSquashReplacementMessage(replacementMessage);
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        EnsureSquashPlanRoot(repositoryRoot, plan);

        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var currentPlan = await CaptureSquashPlanAsync(
                    path,
                    plan.SelectedCommitIds,
                    plan.TargetCommit.Id,
                    plan.LastRetainedCommitId,
                    cancellationToken).ConfigureAwait(false);
                EnsureSquashPlansMatch(plan, currentPlan);

                var before = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureRebaseCanStart(before, plan.ExpectedHeadId);
                return await RunSquashRebaseAsync(
                    path,
                    plan,
                    replacementMessage,
                    before,
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Continues a known squash after the caller has staged resolutions.</summary>
    public async Task<RebaseOperationResult> ContinueSquashAsync(
        string root,
        SquashPlan plan,
        string? replacementMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        replacementMessage = NormalizeSquashReplacementMessage(replacementMessage);
        ValidateSquashReplacementMessage(replacementMessage);
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        EnsureSquashPlanRoot(repositoryRoot, plan);

        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var state = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureSquashRebaseCanContinue(state, plan);
                var before = state;
                return await RunSquashContinueAsync(
                    path,
                    plan,
                    replacementMessage,
                    before,
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Skips the current squash replay step while preserving recovery for later steps.</summary>
    public async Task<RebaseOperationResult> SkipSquashAsync(
        string root,
        SquashPlan plan,
        string? replacementMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        replacementMessage = NormalizeSquashReplacementMessage(replacementMessage);
        ValidateSquashReplacementMessage(replacementMessage);
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        EnsureSquashPlanRoot(repositoryRoot, plan);

        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var state = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureSquashRebaseCanSkip(state, plan);
                return await RunSquashSkipAsync(
                    path,
                    plan,
                    replacementMessage,
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async Task<RebaseOperationResult> RunSquashRebaseAsync(
        string repositoryRoot,
        SquashPlan plan,
        string? replacementMessage,
        RebaseOperationState before,
        CancellationToken cancellationToken)
    {
        var todoDirectory = CreateOwnedSquashTodoDirectory();
        try
        {
            var todoPath = Path.Combine(todoDirectory, "squash.todo");
            var todo = string.Join(
                Environment.NewLine,
                plan.ReplaySteps.Select(step =>
                    $"{(step.Action == SquashPlanAction.Pick ? "pick" : "squash")} {step.Commit.Id}"))
                + Environment.NewLine;
            await File.WriteAllTextAsync(
                todoPath,
                todo,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            var editor = await CreateSquashEditorAsync(
                todoDirectory,
                replacementMessage,
                plan,
                cancellationToken).ConfigureAwait(false);
            var recoverySourcePath = await WriteSquashRecoverySourceAsync(
                todoDirectory,
                plan,
                replacementMessage,
                cancellationToken).ConfigureAwait(false);
            var sequenceEditorPath = await CreateSquashSequenceEditorAsync(
                todoDirectory,
                todoPath,
                recoverySourcePath,
                cancellationToken).ConfigureAwait(false);
            try
            {
                await processRunner.RunAsync(
                    repositoryRoot,
                    BuildSquashInteractiveArguments(sequenceEditorPath, plan.LastRetainedCommitId),
                    cancellationToken,
                    environmentOverrides: BuildSquashEditorEnvironment(editor)).ConfigureAwait(false);
            }
            catch (GitCommandException)
            {
                var failed = await ReadRebaseStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
                if (failed.IsInProgress)
                {
                    var result = new RebaseOperationResult(
                        failed.HasUnresolvedConflicts ? RebaseOutcome.Conflicts : RebaseOutcome.InProgress,
                        failed.CurrentHeadId,
                        failed);
                    await PersistSquashRecoveryAsync(
                        repositoryRoot,
                        plan,
                        replacementMessage,
                        failed,
                        cancellationToken).ConfigureAwait(false);
                    return result;
                }

                throw;
            }

            var after = await ReadRebaseStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            var completedResult = new RebaseOperationResult(
                GetRebaseCompletionOutcome(before, after),
                after.CurrentHeadId,
                after);
            await PersistSquashRecoveryAsync(
                repositoryRoot,
                plan,
                replacementMessage,
                after,
                cancellationToken).ConfigureAwait(false);
            return completedResult;
        }
        finally
        {
            DeleteOwnedSquashTodoDirectory(todoDirectory);
        }
    }

    private async Task<RebaseOperationResult> RunSquashContinueAsync(
        string repositoryRoot,
        SquashPlan plan,
        string? replacementMessage,
        RebaseOperationState before,
        CancellationToken cancellationToken)
    {
        var todoDirectory = CreateOwnedSquashTodoDirectory();
        try
        {
            var editor = await CreateSquashEditorAsync(
                todoDirectory,
                replacementMessage,
                plan,
                cancellationToken).ConfigureAwait(false);
            try
            {
                await processRunner.RunAsync(
                    repositoryRoot,
                    ["rebase", "--continue"],
                    cancellationToken,
                    environmentOverrides: BuildSquashEditorEnvironment(editor)).ConfigureAwait(false);
            }
            catch (GitCommandException)
            {
                var failed = await ReadRebaseStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
                if (failed.IsInProgress)
                {
                    var result = new RebaseOperationResult(
                        failed.HasUnresolvedConflicts ? RebaseOutcome.Conflicts : RebaseOutcome.InProgress,
                        failed.CurrentHeadId,
                        failed);
                    await PersistSquashRecoveryAsync(
                        repositoryRoot,
                        plan,
                        replacementMessage,
                        failed,
                        cancellationToken).ConfigureAwait(false);
                    return result;
                }

                throw;
            }

            var after = await ReadRebaseStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            var completedResult = new RebaseOperationResult(
                GetRebaseCompletionOutcome(before, after),
                after.CurrentHeadId,
                after);
            await PersistSquashRecoveryAsync(
                repositoryRoot,
                plan,
                replacementMessage,
                after,
                cancellationToken).ConfigureAwait(false);
            return completedResult;
        }
        finally
        {
            DeleteOwnedSquashTodoDirectory(todoDirectory);
        }
    }

    private async Task<RebaseOperationResult> RunSquashSkipAsync(
        string repositoryRoot,
        SquashPlan plan,
        string? replacementMessage,
        CancellationToken cancellationToken)
    {
        var todoDirectory = CreateOwnedSquashTodoDirectory();
        try
        {
            var editor = await CreateSquashEditorAsync(
                todoDirectory,
                replacementMessage,
                plan,
                cancellationToken).ConfigureAwait(false);
            try
            {
                await processRunner.RunAsync(
                    repositoryRoot,
                    ["rebase", "--skip"],
                    cancellationToken,
                    environmentOverrides: BuildSquashEditorEnvironment(editor)).ConfigureAwait(false);
            }
            catch (GitCommandException)
            {
                var failed = await ReadRebaseStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
                if (failed.IsInProgress)
                {
                    var result = new RebaseOperationResult(
                        failed.HasUnresolvedConflicts ? RebaseOutcome.Conflicts : RebaseOutcome.InProgress,
                        failed.CurrentHeadId,
                        failed);
                    await PersistSquashRecoveryAsync(
                        repositoryRoot,
                        plan,
                        replacementMessage,
                        failed,
                        cancellationToken).ConfigureAwait(false);
                    return result;
                }

                throw;
            }

            var after = await ReadRebaseStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            var completedResult = new RebaseOperationResult(
                after.IsInProgress
                    ? after.HasUnresolvedConflicts
                        ? RebaseOutcome.Conflicts
                        : RebaseOutcome.InProgress
                    : RebaseOutcome.Skipped,
                after.CurrentHeadId,
                after);
            await PersistSquashRecoveryAsync(
                repositoryRoot,
                plan,
                replacementMessage,
                after,
                cancellationToken).ConfigureAwait(false);
            return completedResult;
        }
        finally
        {
            DeleteOwnedSquashTodoDirectory(todoDirectory);
        }
    }

    private async Task PersistSquashRecoveryAsync(
        string repositoryRoot,
        SquashPlan plan,
        string? replacementMessage,
        RebaseOperationState state,
        CancellationToken cancellationToken)
    {
        if (!state.IsInProgress)
        {
            await DeleteSquashRecoveryAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            return;
        }

        var recoveryPath = await ResolveSquashRecoveryPathAsync(
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        if (recoveryPath is null)
        {
            throw new IOException("The active rebase metadata directory is unavailable for squash recovery.");
        }

        EnsureSquashRecoveryPathSafe(repositoryRoot, recoveryPath);
        var file = CreateSquashRecoveryFile(plan, replacementMessage);
        var temporaryPath = recoveryPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(file, SquashRecoveryJsonOptions);
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                StrictUtf8,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, recoveryPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // Preserve the original write or Git operation failure.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the original write or Git operation failure.
            }
        }
    }

    private static SquashRecoveryFile CreateSquashRecoveryFile(
        SquashPlan plan,
        string? replacementMessage) =>
        new()
        {
            Version = SquashRecoveryVersion,
            RootPath = plan.RootPath,
            Branch = plan.Branch,
            ExpectedHeadId = plan.ExpectedHeadId,
            LastRetainedCommitId = plan.LastRetainedCommitId,
            TargetCommit = ToSquashRecoveryCommit(plan.TargetCommit),
            ResultingCommit = ToSquashRecoveryCommit(plan.ResultingCommit),
            ResultingAuthorName = plan.ResultingCommitAuthor.Name,
            ResultingAuthorEmail = plan.ResultingCommitAuthor.Email,
            ReplaySteps = plan.ReplaySteps
                .Select(step => new SquashRecoveryStepFile
                {
                    Action = (int)step.Action,
                    Commit = ToSquashRecoveryCommit(step.Commit),
                })
                .ToArray(),
            ReplacementMessage = replacementMessage ?? string.Empty,
        };

    private static async Task<string> WriteSquashRecoverySourceAsync(
        string todoDirectory,
        SquashPlan plan,
        string? replacementMessage,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.Combine(todoDirectory, SquashRecoveryFileName);
        var json = JsonSerializer.Serialize(
            CreateSquashRecoveryFile(plan, replacementMessage),
            SquashRecoveryJsonOptions);
        await File.WriteAllTextAsync(
            sourcePath,
            json,
            StrictUtf8,
            cancellationToken).ConfigureAwait(false);
        return sourcePath;
    }

    private static async Task<string> CreateSquashSequenceEditorAsync(
        string todoDirectory,
        string todoPath,
        string recoverySourcePath,
        CancellationToken cancellationToken)
    {
        var editorPath = Path.Combine(todoDirectory, "squash-sequence-editor.sh");
        var recoveryPath = $"$rebase_dir/{SquashRecoveryFileName}";
        var recoveryTemporaryPath = $"$rebase_dir/{SquashRecoveryFileName}.tmp";
        var editorScript = string.Join(
            Environment.NewLine,
            [
                "#!/bin/sh",
                "set -e",
                "rebase_dir=$(git rev-parse --git-path rebase-merge 2>/dev/null || true)",
                "if [ ! -d \"$rebase_dir\" ]; then",
                "    rebase_dir=$(git rev-parse --git-path rebase-apply 2>/dev/null || true)",
                "fi",
                "if [ ! -d \"$rebase_dir\" ]; then",
                "    exit 1",
                "fi",
                $"cat \"{EscapeShellDoubleQuotedForSquash(recoverySourcePath)}\" > \"{recoveryTemporaryPath}\"",
                $"mv -f \"{recoveryTemporaryPath}\" \"{recoveryPath}\"",
                $"cat \"{EscapeShellDoubleQuotedForSquash(todoPath)}\" > \"$1\"",
                string.Empty,
            ]);
        await File.WriteAllTextAsync(
            editorPath,
            editorScript,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
        return editorPath;
    }

    private async Task DeleteSquashRecoveryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var recoveryPath = await ResolveSquashRecoveryPathAsync(
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        if (recoveryPath is null || !File.Exists(recoveryPath))
        {
            return;
        }

        EnsureSquashRecoveryPathSafe(repositoryRoot, recoveryPath);
        File.Delete(recoveryPath);
    }

    private async Task<string?> ResolveSquashRecoveryPathAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var rebaseDirectory = await ResolveRebaseDirectoryAsync(
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        return rebaseDirectory is null
            ? null
            : Path.Combine(rebaseDirectory, SquashRecoveryFileName);
    }

    private static void EnsureSquashRecoveryPathSafe(
        string repositoryRoot,
        string recoveryPath)
    {
        var parent = Path.GetDirectoryName(recoveryPath);
        if (parent is null || ContainsReparsePoint(repositoryRoot, recoveryPath))
        {
            throw new IOException("The Git rebase metadata path is no longer safe for squash recovery.");
        }

        if (File.Exists(recoveryPath)
            && File.GetAttributes(recoveryPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The squash recovery marker became a reparse point.");
        }
    }

    private static SquashRecoveryCommitFile ToSquashRecoveryCommit(SquashPlanCommit commit) =>
        new()
        {
            Id = commit.Id,
            Summary = commit.Summary,
        };

    private static bool TryCreateSquashRecovery(
        string repositoryRoot,
        RebaseOperationState state,
        SquashRecoveryFile? file,
        out SquashRecovery? recovery)
    {
        recovery = null;
        if (file is null
            || file.Version != SquashRecoveryVersion
            || string.IsNullOrWhiteSpace(file.RootPath)
            || string.IsNullOrWhiteSpace(file.Branch)
            || !string.Equals(file.Branch, state.Branch, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(file.ExpectedHeadId)
            || !string.Equals(file.ExpectedHeadId, state.OriginalBranchTip, StringComparison.OrdinalIgnoreCase)
            || file.ReplacementMessage is null
            || file.ReplacementMessage.Length > MaximumSquashRecoveryMessageLength
            || file.ReplacementMessage.IndexOf('\0') >= 0
            || file.TargetCommit is null
            || file.ResultingCommit is null
            || file.ReplaySteps is null
            || file.ReplaySteps.Length is < 1 or > MaximumSquashPlanCommits)
        {
            return false;
        }

        try
        {
            if (!PathsEqual(file.RootPath, repositoryRoot))
            {
                return false;
            }

            var expectedHeadId = ValidateSquashId(file.ExpectedHeadId, nameof(file.ExpectedHeadId));
            var lastRetainedCommitId = ValidateOptionalSquashId(
                file.LastRetainedCommitId,
                nameof(file.LastRetainedCommitId));
            var targetCommit = CreateValidatedSquashRecoveryCommit(file.TargetCommit);
            var resultingCommit = CreateValidatedSquashRecoveryCommit(file.ResultingCommit);
            var replaySteps = new List<SquashPlanStep>(file.ReplaySteps.Length);
            var replayIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasSquash = false;
            foreach (var stepFile in file.ReplaySteps)
            {
                if (stepFile is null
                    || stepFile.Commit is null
                    || stepFile.Action is not (int)SquashPlanAction.Pick and not (int)SquashPlanAction.Squash)
                {
                    return false;
                }

                var commit = CreateValidatedSquashRecoveryCommit(stepFile.Commit);
                if (!replayIds.Add(commit.Id))
                {
                    return false;
                }

                var action = (SquashPlanAction)stepFile.Action;
                hasSquash |= action == SquashPlanAction.Squash;
                replaySteps.Add(new SquashPlanStep(action, commit));
            }

            if (replaySteps[0].Action != SquashPlanAction.Pick
                || !hasSquash
                || !replayIds.Contains(targetCommit.Id)
                || !replayIds.Contains(resultingCommit.Id))
            {
                return false;
            }

            var selectedCommits = replaySteps
                .Where(step => step.Action == SquashPlanAction.Squash)
                .Select(step => step.Commit)
                .ToArray();
            var plan = new SquashPlan(
                repositoryRoot,
                file.Branch,
                expectedHeadId,
                lastRetainedCommitId,
                targetCommit,
                resultingCommit,
                new CommitIdentity(
                    file.ResultingAuthorName ?? string.Empty,
                    file.ResultingAuthorEmail ?? string.Empty),
                replaySteps.Select(step => step.Commit).ToArray(),
                selectedCommits,
                replaySteps);
            recovery = new SquashRecovery(plan, file.ReplacementMessage);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static SquashPlanCommit CreateValidatedSquashRecoveryCommit(
        SquashRecoveryCommitFile commit)
    {
        if (commit is null || commit.Summary is null)
        {
            throw new ArgumentException("The recovery commit is incomplete.", nameof(commit));
        }

        var id = ValidateSquashId(commit.Id, nameof(commit.Id));
        if (commit.Summary.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("The recovery commit summary cannot contain NUL.", nameof(commit));
        }

        return new SquashPlanCommit(id, commit.Summary);
    }

    private static IReadOnlyList<SquashPlanStep> BuildSquashReplaySteps(
        IReadOnlyList<ReorderHistoryEntry> history,
        IReadOnlySet<string> selectedSet,
        string targetId,
        out SquashPlanCommit resultingCommit)
    {
        var steps = new List<SquashPlanStep>(history.Count);
        var heldAtTarget = new List<SquashPlanCommit>();
        var heldAfterTarget = new List<SquashPlanCommit>();
        var foundTarget = false;

        foreach (var entry in history)
        {
            var commit = ToSquashPlanCommit(entry);
            if (selectedSet.Contains(entry.Id))
            {
                if (foundTarget)
                {
                    steps.Add(new SquashPlanStep(SquashPlanAction.Squash, commit));
                }
                else
                {
                    heldAtTarget.Add(commit);
                }

                continue;
            }

            if (string.Equals(entry.Id, targetId, StringComparison.OrdinalIgnoreCase))
            {
                foundTarget = true;
                heldAtTarget.Add(commit);
                resultingCommit = heldAtTarget[0];
                for (var index = 0; index < heldAtTarget.Count; index++)
                {
                    steps.Add(new SquashPlanStep(
                        index == 0 ? SquashPlanAction.Pick : SquashPlanAction.Squash,
                        heldAtTarget[index]));
                }

                continue;
            }

            if (foundTarget)
            {
                heldAfterTarget.Add(commit);
            }
            else
            {
                steps.Add(new SquashPlanStep(SquashPlanAction.Pick, commit));
            }
        }

        if (!foundTarget)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.InvalidTarget,
                "The commit being squashed onto is not in the captured replay range.",
                targetId);
        }

        foreach (var commit in heldAfterTarget)
        {
            steps.Add(new SquashPlanStep(SquashPlanAction.Pick, commit));
        }

        if (heldAtTarget.Count == 0)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.InvalidTarget,
                "The squash group is empty.",
                targetId);
        }

        resultingCommit = heldAtTarget[0];
        return steps.AsReadOnly();
    }

    private async Task<CommitIdentity> ReadSquashCommitAuthorAsync(
        string repositoryRoot,
        string commitId,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            [
                "show",
                "--no-patch",
                "--no-color",
                "--no-show-signature",
                "--format=%an%x00%ae",
                commitId,
                "--",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "squash commit author");
        var fields = DecodeUtf8(result.StandardOutput, "squash commit author")
            .TrimEnd('\r', '\n')
            .Split('\0', StringSplitOptions.None);
        if (fields.Length != 2 || string.IsNullOrWhiteSpace(fields[0]) || string.IsNullOrWhiteSpace(fields[1]))
        {
            throw new InvalidOperationException("Git returned an invalid squash commit author.");
        }

        return new CommitIdentity(fields[0], fields[1]);
    }

    private async Task ThrowForSquashCommitOutsideRangeAsync(
        string repositoryRoot,
        string commitId,
        CancellationToken cancellationToken)
    {
        var resolved = await TryResolveCommitIdAsync(
            repositoryRoot,
            commitId,
            cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.MissingCommit,
                "The selected commit no longer exists in this repository.",
                commitId);
        }

        throw new SquashOperationBlockedException(
            SquashOperationFailureReason.ForeignCommit,
            "The selected commit is not in the current branch replay range.",
            resolved);
    }

    private static IReadOnlyList<string> ValidateSelectedSquashIds(
        IReadOnlyList<string> selectedCommitIds)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(selectedCommitIds.Count);
        foreach (var commitId in selectedCommitIds)
        {
            var value = ValidateSquashId(commitId, nameof(selectedCommitIds));
            if (!seen.Add(value))
            {
                throw new SquashOperationBlockedException(
                    SquashOperationFailureReason.DuplicateSelection,
                    "A commit cannot be selected more than once.",
                    value);
            }

            normalized.Add(value);
        }

        return normalized;
    }

    private static string ValidateSquashId(string commitId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitId, parameterName);
        if (commitId.Length is < 40 or > 64 || commitId.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Git object IDs must contain 40 to 64 hexadecimal characters.",
                parameterName);
        }

        return commitId;
    }

    private static string? ValidateOptionalSquashId(string? commitId, string parameterName) =>
        string.IsNullOrWhiteSpace(commitId)
            ? null
            : ValidateSquashId(commitId, parameterName);

    private static void EnsureSquashRepositoryReady(RepositoryStatus status)
    {
        if (status.IsUnborn || status.HeadId.Length == 0)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.UnbornRepository,
                "Squashing is unavailable because the repository has no current commit.");
        }

        if (status.IsDetached || status.Branch.Length == 0)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.DetachedHead,
                "Squashing is available only on a local branch.");
        }

        if (status.Changes.Count > 0)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.DirtyWorktree,
                "Commit or set aside every local change before squashing commits.");
        }
    }

    private static void EnsureSquashPlanRoot(string repositoryRoot, SquashPlan plan)
    {
        if (!string.Equals(repositoryRoot, plan.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.StalePlan,
                "The selected repository changed; refresh the squash preview before applying it.");
        }
    }

    private static void EnsureSquashPlansMatch(SquashPlan expected, SquashPlan actual)
    {
        if (!string.Equals(expected.RootPath, actual.RootPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.Branch, actual.Branch, StringComparison.Ordinal)
            || !string.Equals(expected.ExpectedHeadId, actual.ExpectedHeadId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.LastRetainedCommitId, actual.LastRetainedCommitId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.TargetCommit.Id, actual.TargetCommit.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.ResultingCommit.Id, actual.ResultingCommit.Id, StringComparison.OrdinalIgnoreCase)
            || !expected.SelectedCommitIds.SequenceEqual(actual.SelectedCommitIds, StringComparer.OrdinalIgnoreCase)
            || !expected.ReplayCommitIds.SequenceEqual(actual.ReplayCommitIds, StringComparer.OrdinalIgnoreCase)
            || !expected.ReplaySteps.SequenceEqual(actual.ReplaySteps))
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.StalePlan,
                "The branch, HEAD, or selected commits changed; refresh the squash preview before applying it.");
        }
    }

    private static void EnsureSquashRebaseCanContinue(RebaseOperationState state, SquashPlan plan)
    {
        if (!state.IsInProgress)
        {
            var reason = state.OperationKind == GitOperationKind.None
                ? SquashOperationFailureReason.InvalidTarget
                : SquashOperationFailureReason.OperationInProgress;
            throw new SquashOperationBlockedException(
                reason,
                state.OperationKind == GitOperationKind.None
                    ? "There is no in-progress squash to continue."
                    : "Only the known in-progress squash can be continued.");
        }

        if (!string.Equals(state.OriginalBranchTip, plan.ExpectedHeadId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(state.Branch, plan.Branch, StringComparison.Ordinal))
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.StalePlan,
                "The in-progress squash no longer matches the captured branch and HEAD; refresh the operation.");
        }

        if (state.HasUnresolvedConflicts)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.OperationInProgress,
                "Resolve and stage every unmerged path before continuing the squash.");
        }

        if (state.HasUnstagedChanges)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.DirtyWorktree,
                "Stage every tracked conflict resolution before continuing the squash.");
        }
    }

    private static void EnsureSquashRebaseCanSkip(RebaseOperationState state, SquashPlan plan)
    {
        if (!state.IsInProgress)
        {
            var reason = state.OperationKind == GitOperationKind.None
                ? SquashOperationFailureReason.InvalidTarget
                : SquashOperationFailureReason.OperationInProgress;
            throw new SquashOperationBlockedException(
                reason,
                state.OperationKind == GitOperationKind.None
                    ? "There is no in-progress squash to skip."
                    : "Only the known in-progress squash can be skipped.");
        }

        if (!string.Equals(state.OriginalBranchTip, plan.ExpectedHeadId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(state.Branch, plan.Branch, StringComparison.Ordinal))
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.StalePlan,
                "The in-progress squash no longer matches the captured branch and HEAD; refresh the operation.");
        }
    }

    private static string? NormalizeSquashReplacementMessage(string? replacementMessage) =>
        replacementMessage?
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static void ValidateSquashReplacementMessage(string? replacementMessage)
    {
        if (replacementMessage?.IndexOf('\0') >= 0)
        {
            throw new SquashOperationBlockedException(
                SquashOperationFailureReason.InvalidMessage,
                "The replacement commit message cannot contain a NUL character.");
        }
    }

    private sealed record SquashEditor(string Command, string? MessagePath);

    private static async Task<SquashEditor> CreateSquashEditorAsync(
        string todoDirectory,
        string? replacementMessage,
        SquashPlan plan,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(replacementMessage))
        {
            return new SquashEditor(":", null);
        }

        var messagePath = Path.Combine(todoDirectory, "squash-message.txt");
        await File.WriteAllTextAsync(
            messagePath,
            replacementMessage,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);

        var squashCommitIds = plan.ReplaySteps
            .Where(step => step.Action == SquashPlanAction.Squash)
            .Select(step => step.Commit.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (squashCommitIds.Length == 0)
        {
            return new SquashEditor(":", messagePath);
        }

        var editorPath = Path.Combine(todoDirectory, "squash-editor.sh");
        var cases = string.Join(
            Environment.NewLine,
            squashCommitIds.Select(commitId => $"    {commitId}) cat \"$WINGIT_SQUASH_MESSAGE_PATH\" > \"$1\" ;;"));
        var editorScript = string.Join(
            Environment.NewLine,
            [
                "#!/bin/sh",
                "commit=$(git rev-parse REBASE_HEAD 2>/dev/null || true)",
                "case \"$commit\" in",
                cases,
                "esac",
                "exit 0",
                string.Empty,
            ]);
        await File.WriteAllTextAsync(
            editorPath,
            editorScript,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);

        return new SquashEditor(
            $"sh \"{EscapeShellDoubleQuotedForSquash(editorPath)}\"",
            messagePath);
    }

    private static IReadOnlyDictionary<string, string?> BuildSquashEditorEnvironment(SquashEditor editor)
    {
        var environment = new Dictionary<string, string?>(ReorderEditorEnvironment, StringComparer.Ordinal)
        {
            ["GIT_EDITOR"] = editor.Command,
            ["GIT_SEQUENCE_EDITOR"] = null,
            ["WINGIT_SQUASH_MESSAGE_PATH"] = editor.MessagePath,
        };
        return environment;
    }

    private static List<string> BuildSquashInteractiveArguments(
        string sequenceEditorPath,
        string? lastRetainedCommitId)
    {
        var sequenceEditor = $"sh \"{EscapeShellDoubleQuotedForSquash(sequenceEditorPath)}\"";
        return
        [
            "-c",
            $"sequence.editor={sequenceEditor}",
            "rebase",
            "--no-autostash",
            "--no-autosquash",
            "-i",
            lastRetainedCommitId ?? "--root",
        ];
    }

    private static string EscapeShellDoubleQuotedForSquash(string path) =>
        path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private static string CreateOwnedSquashTodoDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinGit.Native");
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, "squash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteOwnedSquashTodoDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static SquashPlanCommit ToSquashPlanCommit(ReorderHistoryEntry entry) =>
        new(entry.Id, entry.Summary);
}
