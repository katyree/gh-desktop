using System.Text;

namespace WinGit.Core;

/// <summary>A commit entry in an immutable interactive-reorder preview.</summary>
public sealed record ReorderPlanCommit(string Id, string Summary);

/// <summary>
/// The complete, repository-relative replay plan captured before a reorder.
/// The commit lists are chronological, oldest to newest, even though the
/// History view normally displays them newest first.
/// </summary>
public sealed class ReorderPlan
{
    internal ReorderPlan(
        string rootPath,
        string branch,
        string expectedHeadId,
        string? lastRetainedCommitId,
        string? beforeCommitId,
        IReadOnlyList<ReorderPlanCommit> originalCommits,
        IReadOnlyList<ReorderPlanCommit> selectedCommits,
        IReadOnlyList<ReorderPlanCommit> replayCommits)
    {
        RootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        Branch = branch ?? throw new ArgumentNullException(nameof(branch));
        ExpectedHeadId = expectedHeadId ?? throw new ArgumentNullException(nameof(expectedHeadId));
        LastRetainedCommitId = lastRetainedCommitId;
        BeforeCommitId = beforeCommitId;
        OriginalCommits = Copy(originalCommits);
        SelectedCommits = Copy(selectedCommits);
        ReplayCommits = Copy(replayCommits);
        SelectedCommitIds = SelectedCommits.Select(commit => commit.Id).ToList().AsReadOnly();
        ReplayCommitIds = ReplayCommits.Select(commit => commit.Id).ToList().AsReadOnly();
    }

    public string RootPath { get; }

    public string Branch { get; }

    /// <summary>The branch tip that must still be checked out before replay.</summary>
    public string ExpectedHeadId { get; }

    /// <summary>The commit before the replay range, or null when replaying from the root.</summary>
    public string? LastRetainedCommitId { get; }

    /// <summary>The commit before which selected commits are inserted, or null to move them to the end.</summary>
    public string? BeforeCommitId { get; }

    /// <summary>Every commit in the interactive replay range, oldest to newest.</summary>
    public IReadOnlyList<ReorderPlanCommit> OriginalCommits { get; }

    /// <summary>The selected commits in their captured chronological order.</summary>
    public IReadOnlyList<ReorderPlanCommit> SelectedCommits { get; }

    /// <summary>The exact todo order that will be replayed by Git.</summary>
    public IReadOnlyList<ReorderPlanCommit> ReplayCommits { get; }

    public IReadOnlyList<string> SelectedCommitIds { get; }

    public IReadOnlyList<string> ReplayCommitIds { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new List<T>(values).AsReadOnly();
    }
}

public enum ReorderOperationFailureReason
{
    EmptySelection,
    DuplicateSelection,
    MissingCommit,
    ForeignCommit,
    InvalidTarget,
    MergeCommitInRange,
    DirtyWorktree,
    DetachedHead,
    UnbornRepository,
    OperationInProgress,
    StalePlan,
}

/// <summary>Raised when a captured reorder cannot safely be previewed or applied.</summary>
public sealed class ReorderOperationBlockedException : InvalidOperationException
{
    public ReorderOperationBlockedException(
        ReorderOperationFailureReason reason,
        string message,
        string? commitId = null)
        : base(message)
    {
        Reason = reason;
        CommitId = commitId;
    }

    public ReorderOperationFailureReason Reason { get; }

    public string? CommitId { get; }
}

public sealed partial class GitRepositoryService
{
    // Git gives GIT_SEQUENCE_EDITOR precedence over sequence.editor. Clear it
    // for this owned command so an inherited editor cannot skip the captured
    // reorder todo. GIT_EDITOR still keeps commit-message prompts noninteractive.
    private static readonly IReadOnlyDictionary<string, string?> ReorderEditorEnvironment =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_EDITOR"] = ":",
            ["GIT_SEQUENCE_EDITOR"] = null,
        };

    private const int MaximumReorderPlanCommits = 100_000;

    /// <summary>
    /// Captures a safe interactive-rebase plan. The caller may provide commit
    /// IDs in any order; the plan preserves the order Git currently reports
    /// from oldest to newest, matching Electron's reorder behavior.
    /// </summary>
    public async Task<ReorderPlan> CaptureReorderPlanAsync(
        string root,
        IReadOnlyList<string> selectedCommitIds,
        string? beforeCommitId,
        string? lastRetainedCommitId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedCommitIds);
        if (selectedCommitIds.Count == 0)
        {
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.EmptySelection,
                "Select at least one commit to reorder.");
        }

        var normalizedSelectedIds = ValidateSelectedReorderIds(selectedCommitIds);
        var normalizedBeforeId = ValidateOptionalReorderId(beforeCommitId, nameof(beforeCommitId));
        var normalizedLastRetainedId = ValidateOptionalReorderId(
            lastRetainedCommitId,
            nameof(lastRetainedCommitId));
        if (normalizedBeforeId is not null
            && string.Equals(normalizedBeforeId, normalizedLastRetainedId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.InvalidTarget,
                "The insertion target must be inside the replay range, after the retained base.",
                normalizedBeforeId);
        }

        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        EnsureReorderRepositoryReady(status);

        var operation = await ReadMergeStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (operation.OperationKind != GitOperationKind.None || operation.HasUnresolvedConflicts)
        {
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.OperationInProgress,
                "Reordering is unavailable while a Git operation or unresolved conflict is active.");
        }

        var history = await ReadReorderHistoryAsync(
            repositoryRoot,
            normalizedLastRetainedId,
            cancellationToken).ConfigureAwait(false);
        if (history.Count == 0)
        {
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.InvalidTarget,
                "The selected replay range does not contain any commits.");
        }

        if (history.Count > MaximumReorderPlanCommits)
        {
            throw new GitOutputLimitException("reorder history");
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

            await ThrowForCommitOutsideRangeAsync(
                repositoryRoot,
                selectedId,
                cancellationToken).ConfigureAwait(false);
        }

        if (normalizedBeforeId is not null && !historyById.ContainsKey(normalizedBeforeId))
        {
            await ThrowForCommitOutsideRangeAsync(
                repositoryRoot,
                normalizedBeforeId,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in history)
        {
            if (entry.ParentCount > 1)
            {
                throw new ReorderOperationBlockedException(
                    ReorderOperationFailureReason.MergeCommitInRange,
                    "Reordering replays every commit in the range, so the range cannot contain a merge commit.",
                    entry.Id);
            }
        }

        var selectedSet = normalizedSelectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = history
            .Where(entry => selectedSet.Contains(entry.Id))
            .Select(ToPlanCommit)
            .ToArray();
        if (selected.Length != selectedSet.Count)
        {
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.ForeignCommit,
                "One or more selected commits are outside the current branch replay range.");
        }

        if (normalizedBeforeId is not null && selectedSet.Contains(normalizedBeforeId))
        {
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.InvalidTarget,
                "A selected commit cannot also be the insertion target.",
                normalizedBeforeId);
        }

        var retained = history
            .Where(entry => !selectedSet.Contains(entry.Id))
            .Select(ToPlanCommit)
            .ToList();
        var insertionIndex = normalizedBeforeId is null
            ? retained.Count
            : retained.FindIndex(commit => string.Equals(
                commit.Id,
                normalizedBeforeId,
                StringComparison.OrdinalIgnoreCase));
        if (insertionIndex < 0)
        {
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.InvalidTarget,
                "The insertion target is not available in the replay range.",
                normalizedBeforeId);
        }

        retained.InsertRange(insertionIndex, selected);
        return new ReorderPlan(
            repositoryRoot,
            status.Branch,
            status.HeadId,
            normalizedLastRetainedId,
            normalizedBeforeId,
            history.Select(ToPlanCommit).ToArray(),
            selected,
            retained);
    }

    /// <summary>
    /// Captures a reorder plan and derives the smallest replay base from the
    /// current branch's first-parent ancestry. This overload keeps callers from
    /// using a history-list window or guessing a parent from an adjacent row.
    /// </summary>
    public async Task<ReorderPlan> CaptureReorderPlanFromCurrentHistoryAsync(
        string root,
        IReadOnlyList<string> selectedCommitIds,
        string? beforeCommitId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedCommitIds);
        var normalizedSelectedIds = ValidateSelectedReorderIds(selectedCommitIds);
        var normalizedBeforeId = ValidateOptionalReorderId(beforeCommitId, nameof(beforeCommitId));
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        var firstParentHistory = await ReadReorderHistoryAsync(
            repositoryRoot,
            lastRetainedCommitId: null,
            cancellationToken,
            firstParentOnly: true).ConfigureAwait(false);
        if (firstParentHistory.Count == 0)
        {
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.InvalidTarget,
                "The current branch does not contain any commits.");
        }

        if (firstParentHistory.Count > MaximumReorderPlanCommits)
        {
            throw new GitOutputLimitException("reorder first-parent history");
        }

        var relevantIds = normalizedSelectedIds.ToList();
        if (normalizedBeforeId is not null)
        {
            relevantIds.Add(normalizedBeforeId);
        }

        var earliestIndex = int.MaxValue;
        foreach (var relevantId in relevantIds)
        {
            var index = -1;
            for (var candidateIndex = 0; candidateIndex < firstParentHistory.Count; candidateIndex++)
            {
                if (string.Equals(
                        firstParentHistory[candidateIndex].Id,
                        relevantId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    index = candidateIndex;
                    break;
                }
            }

            if (index >= 0)
            {
                earliestIndex = Math.Min(earliestIndex, index);
            }
        }

        var lastRetainedCommitId = earliestIndex == int.MaxValue
            ? null
            : firstParentHistory[earliestIndex].FirstParentId;
        return await CaptureReorderPlanAsync(
            repositoryRoot,
            normalizedSelectedIds,
            normalizedBeforeId,
            lastRetainedCommitId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replays an immutable plan through Git's interactive rebase machinery.
    /// The plan is recaptured inside the mutation gate before the temporary
    /// todo is created, so a changed branch, HEAD, or selected range is refused.
    /// </summary>
    public async Task<RebaseOperationResult> ReorderAsync(
        string root,
        ReorderPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(repositoryRoot, plan.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.StalePlan,
                "The selected repository changed; refresh the reorder preview before applying it.");
        }

        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var currentPlan = await CaptureReorderPlanAsync(
                    path,
                    plan.SelectedCommitIds,
                    plan.BeforeCommitId,
                    plan.LastRetainedCommitId,
                    cancellationToken).ConfigureAwait(false);
                EnsureReorderPlansMatch(plan, currentPlan);

                var before = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureRebaseCanStart(before, plan.ExpectedHeadId);

                var todoDirectory = CreateOwnedTodoDirectory();
                try
                {
                    var todoPath = Path.Combine(todoDirectory, "reorder.todo");
                    var todo = string.Join(
                        Environment.NewLine,
                        plan.ReplayCommits.Select(commit => $"pick {commit.Id}"))
                        + Environment.NewLine;
                    await File.WriteAllTextAsync(
                        todoPath,
                        todo,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        cancellationToken).ConfigureAwait(false);

                    try
                    {
                        await processRunner.RunAsync(
                            path,
                            BuildInteractiveReorderArguments(todoPath, plan.LastRetainedCommitId),
                            cancellationToken,
                            environmentOverrides: ReorderEditorEnvironment).ConfigureAwait(false);
                    }
                    catch (GitCommandException)
                    {
                        var failed = await ReadRebaseStateAsync(path, cancellationToken).ConfigureAwait(false);
                        if (failed.IsInProgress)
                        {
                            return new RebaseOperationResult(
                                failed.HasUnresolvedConflicts
                                    ? RebaseOutcome.Conflicts
                                    : RebaseOutcome.InProgress,
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
                        : string.Equals(
                            before.CurrentHeadId,
                            after.CurrentHeadId,
                            StringComparison.OrdinalIgnoreCase)
                            ? RebaseOutcome.AlreadyUpToDate
                            : RebaseOutcome.Completed;
                    return new RebaseOperationResult(outcome, after.CurrentHeadId, after);
                }
                finally
                {
                    DeleteOwnedTodoDirectory(todoDirectory);
                }
            }).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ReorderHistoryEntry>> ReadReorderHistoryAsync(
        string repositoryRoot,
        string? lastRetainedCommitId,
        CancellationToken cancellationToken,
        bool firstParentOnly = false)
    {
        if (lastRetainedCommitId is not null)
        {
            var ancestor = await processRunner.RunAsync(
                repositoryRoot,
                ["merge-base", "--is-ancestor", lastRetainedCommitId, "HEAD"],
                cancellationToken,
                expectedExitCodes: [1]).ConfigureAwait(false);
            EnsureComplete(ancestor, "reorder base check");
            if (ancestor.ExitCode != 0)
            {
                throw new ReorderOperationBlockedException(
                    ReorderOperationFailureReason.ForeignCommit,
                    "The retained base is not an ancestor of the current branch.",
                    lastRetainedCommitId);
            }
        }

        var arguments = new List<string>
        {
            "log",
            "--reverse",
            "--no-show-signature",
            "--no-color",
            "--no-decorate",
            "--no-notes",
            "--format=%H%x00%P%x00%s%x00",
            lastRetainedCommitId is null
                ? "HEAD"
                : $"{lastRetainedCommitId}..HEAD",
            "--",
        };
        if (firstParentOnly)
        {
            arguments.Insert(1, "--first-parent");
        }
        var result = await processRunner.RunAsync(
            repositoryRoot,
            arguments,
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "reorder history");
        if (result.StandardErrorTruncated)
        {
            throw new GitOutputLimitException("reorder history diagnostics");
        }

        var fields = DecodeUtf8(result.StandardOutput, "reorder history")
            .Split('\0', StringSplitOptions.None);
        var entries = new List<ReorderHistoryEntry>();
        for (var index = 0; index + 2 < fields.Length; index += 3)
        {
            var id = fields[index].Trim();
            var parents = fields[index + 1].Trim();
            var summary = fields[index + 2].TrimEnd('\r', '\n');
            if (id.Length == 0)
            {
                continue;
            }

            ValidateObjectId(id, "reorder commit ID");
            var parentIds = parents.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            entries.Add(new ReorderHistoryEntry(
                id,
                summary,
                parentIds.Length,
                parentIds.Length == 0 ? null : parentIds[0]));
        }

        return entries;
    }

    private async Task ThrowForCommitOutsideRangeAsync(
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
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.MissingCommit,
                "The selected commit no longer exists in this repository.",
                commitId);
        }

        throw new ReorderOperationBlockedException(
            ReorderOperationFailureReason.ForeignCommit,
            "The selected commit is not in the current branch replay range.",
            resolved);
    }

    private async Task<string?> TryResolveCommitIdAsync(
        string repositoryRoot,
        string commitId,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "--quiet", "--end-of-options", commitId + "^{commit}"],
            cancellationToken,
            expectedExitCodes: [1, 128]).ConfigureAwait(false);
        EnsureComplete(result, "reorder commit lookup");
        if (result.ExitCode != 0)
        {
            return null;
        }

        var resolved = DecodeUtf8(result.StandardOutput, "reorder commit lookup").Trim();
        ValidateObjectId(resolved, "resolved reorder commit ID");
        return resolved;
    }

    private static IReadOnlyList<string> ValidateSelectedReorderIds(
        IReadOnlyList<string> selectedCommitIds)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(selectedCommitIds.Count);
        foreach (var commitId in selectedCommitIds)
        {
            ValidateObjectId(commitId, nameof(selectedCommitIds));
            if (!seen.Add(commitId))
            {
                throw new ReorderOperationBlockedException(
                    ReorderOperationFailureReason.DuplicateSelection,
                    "A commit cannot be selected more than once.",
                    commitId);
            }

            normalized.Add(commitId);
        }

        return normalized;
    }

    private static string? ValidateOptionalReorderId(
        string? commitId,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(commitId))
        {
            return null;
        }

        ValidateObjectId(commitId, parameterName);
        return commitId;
    }

    private static void EnsureReorderRepositoryReady(RepositoryStatus status)
    {
        if (status.IsUnborn || status.HeadId.Length == 0)
        {
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.UnbornRepository,
                "Reordering is unavailable because the repository has no current commit.");
        }

        if (status.IsDetached || status.Branch.Length == 0)
        {
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.DetachedHead,
                "Reordering is available only on a local branch.");
        }

        if (status.Changes.Count > 0)
        {
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.DirtyWorktree,
                "Commit or set aside every local change before reordering commits.");
        }
    }

    private static void EnsureReorderPlansMatch(
        ReorderPlan expected,
        ReorderPlan actual)
    {
        if (!string.Equals(expected.RootPath, actual.RootPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.Branch, actual.Branch, StringComparison.Ordinal)
            || !string.Equals(expected.ExpectedHeadId, actual.ExpectedHeadId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.LastRetainedCommitId, actual.LastRetainedCommitId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.BeforeCommitId, actual.BeforeCommitId, StringComparison.OrdinalIgnoreCase)
            || !expected.SelectedCommitIds.SequenceEqual(actual.SelectedCommitIds, StringComparer.OrdinalIgnoreCase)
            || !expected.ReplayCommitIds.SequenceEqual(actual.ReplayCommitIds, StringComparer.OrdinalIgnoreCase))
        {
            throw new ReorderOperationBlockedException(
                ReorderOperationFailureReason.StalePlan,
                "The branch, HEAD, or selected commits changed; refresh the reorder preview before applying it.");
        }
    }

    private static ReorderPlanCommit ToPlanCommit(ReorderHistoryEntry entry) =>
        new(entry.Id, entry.Summary);

    private static List<string> BuildInteractiveReorderArguments(
        string todoPath,
        string? lastRetainedCommitId)
    {
        var sequenceEditor = $"cat \"{EscapeGitShellDoubleQuoted(todoPath)}\" >";
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

    private static string EscapeGitShellDoubleQuoted(string path) =>
        path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private static string CreateOwnedTodoDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinGit.Native");
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, "reorder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteOwnedTodoDirectory(string directory)
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

    private sealed record ReorderHistoryEntry(
        string Id,
        string Summary,
        int ParentCount,
        string? FirstParentId);
}
