using System.Globalization;

namespace WinGit.Core;

/// <summary>The side of a branch comparison whose commits should be listed.</summary>
public enum ComparisonMode
{
    /// <summary>Commits reachable from the current branch but not the comparison branch.</summary>
    Ahead,

    /// <summary>Commits reachable from the comparison branch but not the current branch.</summary>
    Behind,
}

/// <summary>
/// An immutable comparison of the current branch and a selected branch or
/// commit reference. The endpoints are full object IDs so subsequent Git
/// reads cannot silently follow a moving branch.
/// </summary>
public sealed class BranchComparisonSnapshot
{
    internal BranchComparisonSnapshot(
        string rootPath,
        string baseBranch,
        bool isDetached,
        string baseHeadId,
        string comparisonReference,
        string comparisonHeadId,
        int ahead,
        int behind,
        ComparisonMode comparisonMode,
        string symmetricRange,
        string revisionRange,
        IReadOnlyList<CommitSummary> commits,
        bool commitsTruncated)
    {
        RootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        BaseBranch = baseBranch ?? throw new ArgumentNullException(nameof(baseBranch));
        IsDetached = isDetached;
        BaseHeadId = baseHeadId ?? throw new ArgumentNullException(nameof(baseHeadId));
        ComparisonReference = comparisonReference ?? throw new ArgumentNullException(nameof(comparisonReference));
        ComparisonHeadId = comparisonHeadId ?? throw new ArgumentNullException(nameof(comparisonHeadId));
        Ahead = ahead;
        Behind = behind;
        Mode = comparisonMode;
        SymmetricRange = symmetricRange ?? throw new ArgumentNullException(nameof(symmetricRange));
        RevisionRange = revisionRange ?? throw new ArgumentNullException(nameof(revisionRange));
        Commits = new List<CommitSummary>(commits ?? throw new ArgumentNullException(nameof(commits))).AsReadOnly();
        CommitsTruncated = commitsTruncated;
    }

    public string RootPath { get; }

    /// <summary>Current branch name; empty when the current HEAD is detached.</summary>
    public string BaseBranch { get; }

    public bool IsDetached { get; }

    /// <summary>Full immutable ID of the current branch tip when captured.</summary>
    public string BaseHeadId { get; }

    /// <summary>The caller-selected local/remote branch or commit reference.</summary>
    public string ComparisonReference { get; }

    /// <summary>Full immutable ID resolved from ComparisonReference.</summary>
    public string ComparisonHeadId { get; }

    /// <summary>Commits reachable only from the current branch.</summary>
    public int Ahead { get; }

    /// <summary>Commits reachable only from the comparison branch.</summary>
    public int Behind { get; }

    public ComparisonMode Mode { get; }

    /// <summary>The captured symmetric-difference range used for counts.</summary>
    public string SymmetricRange { get; }

    /// <summary>The captured two-dot range used for Commits.</summary>
    public string RevisionRange { get; }

    public IReadOnlyList<CommitSummary> Commits { get; }

    /// <summary>True when the selected side has more commits than the requested bound.</summary>
    public bool CommitsTruncated { get; }
}

/// <summary>Raised when a comparison's current or selected endpoint no longer matches its capture.</summary>
public sealed class BranchComparisonSnapshotStaleException : InvalidOperationException
{
    public BranchComparisonSnapshotStaleException(string message)
        : base(message)
    {
    }
}

public sealed partial class GitRepositoryService
{
    /// <summary>
    /// Captures the current branch against a selected branch or commit. Ahead
    /// is the current branch-only side; Behind is the comparison-only side,
    /// matching the Electron compare view.
    /// </summary>
    public async Task<BranchComparisonSnapshot?> CaptureBranchComparisonAsync(
        string root,
        string comparisonReference,
        ComparisonMode comparisonMode,
        int maxCommits = 100,
        CancellationToken cancellationToken = default)
    {
        ValidateComparisonReference(comparisonReference);
        ValidateComparisonLimit(maxCommits);

        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        var before = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (before.IsUnborn || before.HeadId.Length == 0)
        {
            return null;
        }

        ValidateFullCommitId(before.HeadId, "current branch HEAD");
        var comparisonHeadId = await ResolveComparisonReferenceAsync(
            repositoryRoot,
            comparisonReference,
            cancellationToken).ConfigureAwait(false);
        if (comparisonHeadId is null)
        {
            return null;
        }

        var baseHeadId = before.HeadId.ToLowerInvariant();
        var symmetricRange = $"{baseHeadId}...{comparisonHeadId}";
        var (ahead, behind) = await ReadComparisonCountsAsync(
            repositoryRoot,
            symmetricRange,
            cancellationToken).ConfigureAwait(false);
        var selectedCount = comparisonMode == ComparisonMode.Ahead ? ahead : behind;
        var revisionRange = comparisonMode == ComparisonMode.Ahead
            ? $"{comparisonHeadId}..{baseHeadId}"
            : $"{baseHeadId}..{comparisonHeadId}";
        var commits = selectedCount == 0
            ? Array.Empty<CommitSummary>()
            : await ReadComparisonCommitsAsync(
                repositoryRoot,
                revisionRange,
                Math.Min(selectedCount, maxCommits),
                cancellationToken).ConfigureAwait(false);

        var after = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var comparisonHeadAfter = await ResolveComparisonReferenceAsync(
            repositoryRoot,
            comparisonReference,
            cancellationToken).ConfigureAwait(false);
        if (comparisonHeadAfter is null
            || !ComparisonBaseStatesMatch(before, after)
            || !string.Equals(comparisonHeadId, comparisonHeadAfter, StringComparison.OrdinalIgnoreCase))
        {
            throw new BranchComparisonSnapshotStaleException(
                "The repository or comparison reference changed while the comparison was being read.");
        }

        return new BranchComparisonSnapshot(
            repositoryRoot,
            before.Branch,
            before.IsDetached,
            baseHeadId,
            comparisonReference,
            comparisonHeadId,
            ahead,
            behind,
            comparisonMode,
            symmetricRange,
            revisionRange,
            commits,
            selectedCount > commits.Count);
    }

    /// <summary>
    /// Re-resolves both references in a captured comparison. A moved current
    /// branch, changed branch state, or moved/deleted comparison reference is
    /// reported as a typed stale snapshot.
    /// </summary>
    public async Task RevalidateBranchComparisonSnapshotAsync(
        string root,
        BranchComparisonSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateComparisonReference(snapshot.ComparisonReference);
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!PathsEqual(repositoryRoot, snapshot.RootPath))
        {
            throw new BranchComparisonSnapshotStaleException(
                "The branch comparison belongs to a different repository.");
        }

        var current = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var comparisonHeadId = await ResolveComparisonReferenceAsync(
            repositoryRoot,
            snapshot.ComparisonReference,
            cancellationToken).ConfigureAwait(false);
        if (comparisonHeadId is null
            || !ComparisonBaseStatesMatch(
                new RepositoryStatus(
                    snapshot.RootPath,
                    snapshot.BaseBranch,
                    snapshot.BaseHeadId,
                    [])
                {
                    IsDetached = snapshot.IsDetached,
                },
                current)
            || !string.Equals(snapshot.ComparisonHeadId, comparisonHeadId, StringComparison.OrdinalIgnoreCase))
        {
            throw new BranchComparisonSnapshotStaleException(
                "The repository or comparison reference no longer matches the branch comparison.");
        }
    }

    private async Task<string?> ResolveComparisonReferenceAsync(
        string repositoryRoot,
        string comparisonReference,
        CancellationToken cancellationToken)
    {
        if (IsFullObjectId(comparisonReference))
        {
            var objectResult = await processRunner.RunAsync(
                repositoryRoot,
                ["cat-file", "-e", comparisonReference + "^{commit}"],
                cancellationToken,
                expectedExitCodes: [1]).ConfigureAwait(false);
            EnsureComplete(objectResult, "comparison commit reference");
            if (objectResult.ExitCode != 0)
            {
                return null;
            }

            return comparisonReference.ToLowerInvariant();
        }

        var result = await processRunner.RunAsync(
            repositoryRoot,
            [
                "rev-parse",
                "--verify",
                "--quiet",
                "--end-of-options",
                comparisonReference + "^{commit}",
            ],
            cancellationToken,
            expectedExitCodes: [1]).ConfigureAwait(false);
        EnsureComplete(result, "comparison reference");
        if (result.ExitCode != 0)
        {
            return null;
        }

        var commitId = DecodeUtf8(result.StandardOutput, "comparison reference").Trim();
        ValidateFullCommitId(commitId, "comparison reference");
        return commitId.ToLowerInvariant();
    }

    private async Task<(int Ahead, int Behind)> ReadComparisonCountsAsync(
        string repositoryRoot,
        string symmetricRange,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["rev-list", "--left-right", "--count", symmetricRange, "--"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "branch comparison counts");
        var fields = DecodeUtf8(result.StandardOutput, "branch comparison counts")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ahead)
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var behind)
            || ahead < 0
            || behind < 0)
        {
            throw new InvalidOperationException("Git returned an invalid branch comparison count.");
        }

        return (ahead, behind);
    }

    private async Task<IReadOnlyList<CommitSummary>> ReadComparisonCommitsAsync(
        string repositoryRoot,
        string revisionRange,
        int maxCommits,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            [
                "log",
                revisionRange,
                "--date=iso-strict",
                "--format=%H%x00%h%x00%an%x00%aI%x00%s%x00",
                $"--max-count={maxCommits}",
                "--no-show-signature",
                "--no-color",
                "--",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "branch comparison history");
        return ParseHistory(result.StandardOutput);
    }

    private static bool ComparisonBaseStatesMatch(
        RepositoryStatus expected,
        RepositoryStatus actual) =>
        PathsEqual(expected.RootPath, actual.RootPath)
        && string.Equals(expected.Branch, actual.Branch, StringComparison.Ordinal)
        && string.Equals(expected.HeadId, actual.HeadId, StringComparison.OrdinalIgnoreCase)
        && expected.IsDetached == actual.IsDetached
        && expected.IsUnborn == actual.IsUnborn;

    private static void ValidateComparisonReference(string comparisonReference)
    {
        var reference = ValidateRevisionArgument(comparisonReference, nameof(comparisonReference));
        if (reference.IndexOfAny(['^', '~', ':', '?', '*', '[', ']', '\\']) >= 0
            || reference.Contains("..", StringComparison.Ordinal)
            || reference.Contains("@{", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The comparison reference must be a branch name, remote-tracking ref, or commit ID.",
                nameof(comparisonReference));
        }
    }

    private static void ValidateComparisonLimit(int maxCommits)
    {
        if (maxCommits is < 0 or > MaximumHistoryLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCommits),
                $"The comparison commit limit must be between 0 and {MaximumHistoryLimit}.");
        }
    }

    private static bool IsFullObjectId(string value) =>
        value.Length is 40 or 64 && value.All(IsHexCharacter);
}
