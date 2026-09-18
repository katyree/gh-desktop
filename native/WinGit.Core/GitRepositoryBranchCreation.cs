namespace WinGit.Core;

/// <summary>
/// The validated intent captured before the native branch-creation dialog is
/// confirmed. The start point is resolved to an immutable commit ID so the
/// later mutation cannot silently follow a moving branch or a changed HEAD.
/// </summary>
public sealed class BranchCreationPlan
{
    internal BranchCreationPlan(
        string rootPath,
        string branchName,
        string? requestedStartPoint,
        string startPointLabel,
        string startCommitId,
        string sourceBranch,
        string sourceHeadId,
        bool isDetached)
    {
        RootPath = rootPath;
        BranchName = branchName;
        RequestedStartPoint = requestedStartPoint;
        StartPointLabel = startPointLabel;
        StartCommitId = startCommitId;
        SourceBranch = sourceBranch;
        SourceHeadId = sourceHeadId;
        IsDetached = isDetached;
    }

    public string RootPath { get; }

    public string BranchName { get; }

    /// <summary>The raw start-point text supplied by the caller; null means HEAD.</summary>
    public string? RequestedStartPoint { get; }

    /// <summary>The display label resolved for the requested start point.</summary>
    public string StartPointLabel { get; }

    /// <summary>The immutable commit the new branch must point at.</summary>
    public string StartCommitId { get; }

    /// <summary>The current branch observed while capturing; empty when detached.</summary>
    public string SourceBranch { get; }

    public string SourceHeadId { get; }

    public bool IsDetached { get; }
}

/// <summary>The outcome of one guarded branch creation without a checkout.</summary>
public sealed record BranchCreationResult(string BranchName, string CommitId);

public sealed partial class GitRepositoryService
{
    /// <summary>
    /// Validates a proposed branch name against the existing Git boundary,
    /// rejects names that already exist, and resolves the start point to an
    /// immutable commit. This performs only reads; refs, HEAD, the index, and
    /// the worktree are unchanged.
    /// </summary>
    public async Task<BranchCreationPlan> CaptureBranchCreationPlanAsync(
        string root,
        string name,
        string? startPoint,
        CancellationToken cancellationToken)
    {
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);

        // The existing Git boundary rejects malformed names via check-ref-format.
        var branchName = await ValidateBranchNameAsync(repositoryRoot, name, cancellationToken).ConfigureAwait(false);
        var existing = await ReadRefIdAsync(
            repositoryRoot,
            $"refs/heads/{branchName}",
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"A branch named '{branchName}' already exists. Choose another name or switch to that branch.");
        }

        var requestedStartPoint = string.IsNullOrWhiteSpace(startPoint) ? null : startPoint.Trim();
        string startPointLabel;
        string startCommitId;
        if (requestedStartPoint is null)
        {
            if (status.IsUnborn || status.HeadId.Length == 0)
            {
                throw new InvalidOperationException(
                    "The repository has no commits yet, so a new branch cannot be based on HEAD.");
            }

            startPointLabel = status.IsDetached || status.Branch.Length == 0 ? "HEAD" : status.Branch;
            startCommitId = status.HeadId;
        }
        else
        {
            ValidateBranchStartPointInput(requestedStartPoint);
            try
            {
                startCommitId = await ResolveCommitIdAsync(
                    repositoryRoot,
                    requestedStartPoint,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"The starting point '{requestedStartPoint}' is missing or is not a commit in this repository; refresh and try again.",
                    exception);
            }

            startPointLabel = requestedStartPoint;
        }

        return new BranchCreationPlan(
            repositoryRoot,
            branchName,
            requestedStartPoint,
            startPointLabel,
            startCommitId,
            status.Branch,
            status.HeadId,
            status.IsDetached);
    }

    /// <summary>
    /// Creates the planned branch from its validated start commit after
    /// revalidating the captured repository state inside the serialized
    /// mutation. The new branch is created without changing the checkout;
    /// callers compose this with the existing checkout guards when the user
    /// explicitly requests a switch after creation.
    /// </summary>
    public async Task<BranchCreationResult> CreateBranchAsync(
        string root,
        BranchCreationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                NormalizeRepositoryRoot(plan.RootPath, plan.RootPath),
                repositoryRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The repository changed while the branch creation was waiting for confirmation; refresh and try again.");
        }

        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var status = await GetStatusAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(status.Branch, plan.SourceBranch, StringComparison.Ordinal)
                    || !string.Equals(status.HeadId, plan.SourceHeadId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The current branch or HEAD changed while the branch creation was waiting for confirmation; refresh and try again.");
                }

                string currentStartCommitId;
                if (plan.RequestedStartPoint is null)
                {
                    if (status.IsUnborn || status.HeadId.Length == 0)
                    {
                        throw new InvalidOperationException(
                            "The repository has no commits, so the branch can no longer be based on HEAD; refresh and try again.");
                    }

                    currentStartCommitId = status.HeadId;
                }
                else
                {
                    try
                    {
                        currentStartCommitId = await ResolveCommitIdAsync(
                            path,
                            plan.RequestedStartPoint,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException exception)
                    {
                        throw new InvalidOperationException(
                            $"The starting point '{plan.StartPointLabel}' is missing or is not a commit in this repository; refresh and try again.",
                            exception);
                    }
                }

                if (!string.Equals(currentStartCommitId, plan.StartCommitId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The starting point '{plan.StartPointLabel}' changed while the branch creation was waiting for confirmation; refresh and try again.");
                }

                var existing = await ReadRefIdAsync(
                    path,
                    $"refs/heads/{plan.BranchName}",
                    cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    throw new InvalidOperationException(
                        $"A branch named '{plan.BranchName}' already exists. Choose another name or switch to that branch.");
                }

                // Create from the validated commit ID rather than a symbolic
                // start point so a concurrently moving branch cannot redirect
                // the new branch to an unrelated commit.
                await processRunner.RunAsync(
                    path,
                    ["branch", "--no-track", plan.BranchName, plan.StartCommitId],
                    cancellationToken).ConfigureAwait(false);

                var created = await ReadRefIdAsync(
                    path,
                    $"refs/heads/{plan.BranchName}",
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(created, plan.StartCommitId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The branch '{plan.BranchName}' was not created at the intended starting commit; refresh the branch list before retrying.");
                }

                return new BranchCreationResult(plan.BranchName, plan.StartCommitId);
            }).ConfigureAwait(false);
    }

    private static void ValidateBranchStartPointInput(string startPoint)
    {
        if ((startPoint.Length > 0 && startPoint[0] == '-')
            || startPoint.IndexOf('\0') >= 0
            || startPoint.Contains('\r')
            || startPoint.Contains('\n'))
        {
            throw new ArgumentException("The branch start point contains an invalid character.", nameof(startPoint));
        }
    }
}
