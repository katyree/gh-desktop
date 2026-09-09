using System.Security.Cryptography;

namespace WinGit.Core;

/// <summary>
/// An immutable, index-bound input for native commit-message generation.
/// RawPatch is the exact bounded patch returned by Git; callers do not need to
/// rebuild it from rendered diff rows.
/// </summary>
public sealed class CommitMessageSnapshot
{
    internal CommitMessageSnapshot(
        string rootPath,
        string headId,
        string branch,
        bool isDetached,
        bool isUnborn,
        bool amend,
        string baselineId,
        string indexFingerprint,
        byte[] rawPatch)
    {
        RootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        HeadId = headId ?? throw new ArgumentNullException(nameof(headId));
        Branch = branch ?? throw new ArgumentNullException(nameof(branch));
        IsDetached = isDetached;
        IsUnborn = isUnborn;
        Amend = amend;
        BaselineId = baselineId ?? throw new ArgumentNullException(nameof(baselineId));
        IndexFingerprint = indexFingerprint ?? throw new ArgumentNullException(nameof(indexFingerprint));
        RawPatch = new ReadOnlyMemory<byte>(rawPatch ?? throw new ArgumentNullException(nameof(rawPatch)));
    }

    public string RootPath { get; }

    /// <summary>Empty when the captured repository was unborn.</summary>
    public string HeadId { get; }

    /// <summary>Empty when HEAD was detached or the repository was unborn.</summary>
    public string Branch { get; }

    public bool IsDetached { get; }

    public bool IsUnborn { get; }

    public bool Amend { get; }

    /// <summary>
    /// The immutable tree compared by RawPatch: captured HEAD for a normal
    /// commit, its first parent for amend, or the empty tree for a root commit.
    /// </summary>
    public string BaselineId { get; }

    /// <summary>SHA-256 of the complete `git ls-files --stage -z` output.</summary>
    public string IndexFingerprint { get; }

    /// <summary>Exact bounded bytes from `git diff --cached`.</summary>
    public ReadOnlyMemory<byte> RawPatch { get; }
}

/// <summary>Raised when the index or repository identity no longer matches a captured commit-message snapshot.</summary>
public sealed class CommitMessageSnapshotStaleException : InvalidOperationException
{
    public CommitMessageSnapshotStaleException(string message)
        : base(message)
    {
    }
}

public sealed partial class GitRepositoryService
{
    private const int MaximumCommitMessagePatchBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Captures the staged patch without changing Git's index or work tree.
    /// The read is rejected if HEAD, branch state, or the full index listing
    /// changes while Git produces the bounded patch.
    /// </summary>
    public async Task<CommitMessageSnapshot> CaptureCommitMessageSnapshotAsync(
        string root,
        bool amend,
        CancellationToken cancellationToken = default)
    {
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        var before = await ReadCommitMessageSnapshotStateAsync(
            repositoryRoot,
            amend,
            cancellationToken).ConfigureAwait(false);
        var patch = await ReadCommitMessagePatchAsync(
            repositoryRoot,
            before.BaselineId,
            cancellationToken).ConfigureAwait(false);
        var after = await ReadCommitMessageSnapshotStateAsync(
            repositoryRoot,
            amend,
            cancellationToken).ConfigureAwait(false);

        if (!CommitMessageSnapshotStatesMatch(before, after))
        {
            throw new CommitMessageSnapshotStaleException(
                "The repository or index changed while the commit-message patch was captured.");
        }

        return new CommitMessageSnapshot(
            repositoryRoot,
            before.HeadId,
            before.Branch,
            before.IsDetached,
            before.IsUnborn,
            amend,
            before.BaselineId,
            before.IndexFingerprint,
            patch);
    }

    /// <summary>
    /// Revalidates the immutable identity used for a prior commit-message
    /// capture. A changed HEAD, branch state, or index throws a typed stale
    /// exception so callers cannot submit a patch for different content.
    /// </summary>
    public async Task RevalidateCommitMessageSnapshotAsync(
        string root,
        CommitMessageSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!PathsEqual(repositoryRoot, snapshot.RootPath))
        {
            throw new CommitMessageSnapshotStaleException(
                "The commit-message snapshot belongs to a different repository.");
        }

        var current = await ReadCommitMessageSnapshotStateAsync(
            repositoryRoot,
            snapshot.Amend,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current.BaselineId, snapshot.BaselineId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.HeadId, snapshot.HeadId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.Branch, snapshot.Branch, StringComparison.Ordinal)
            || current.IsDetached != snapshot.IsDetached
            || current.IsUnborn != snapshot.IsUnborn
            || !string.Equals(current.IndexFingerprint, snapshot.IndexFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new CommitMessageSnapshotStaleException(
                "The repository or index no longer matches the commit-message snapshot.");
        }
    }

    private async Task<CommitMessageSnapshotState> ReadCommitMessageSnapshotStateAsync(
        string repositoryRoot,
        bool amend,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (status.Changes.Any(IsUnmergedChange))
        {
            throw new InvalidOperationException(
                "A commit-message snapshot cannot be captured while the index contains unmerged paths.");
        }

        var headId = status.HeadId;
        if (status.IsUnborn)
        {
            if (headId.Length != 0)
            {
                throw new InvalidOperationException("Git reported an unborn repository with a HEAD ID.");
            }

            if (amend)
            {
                throw new InvalidOperationException(
                    "An amend commit-message snapshot requires an existing HEAD commit.");
            }
        }
        else
        {
            ValidateFullCommitId(headId, "repository HEAD");
        }

        var indexFingerprint = await ReadCommitMessageIndexFingerprintAsync(
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        var baselineId = await ResolveCommitMessageBaselineAsync(
            repositoryRoot,
            headId,
            status.IsUnborn,
            amend,
            cancellationToken).ConfigureAwait(false);
        return new CommitMessageSnapshotState(
            repositoryRoot,
            headId.ToLowerInvariant(),
            status.Branch,
            status.IsDetached,
            status.IsUnborn,
            baselineId,
            indexFingerprint);
    }

    private async Task<string> ResolveCommitMessageBaselineAsync(
        string repositoryRoot,
        string headId,
        bool isUnborn,
        bool amend,
        CancellationToken cancellationToken)
    {
        if (isUnborn || headId.Length == 0)
        {
            return await ReadEmptyTreeObjectIdAsync(
                repositoryRoot,
                cancellationToken).ConfigureAwait(false);
        }

        if (!amend)
        {
            return headId.ToLowerInvariant();
        }

        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["rev-list", "--parents", "-n", "1", "--end-of-options", headId],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "commit-message amend baseline");
        var fields = DecodeUtf8(result.StandardOutput, "commit-message amend baseline")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0 || !string.Equals(fields[0], headId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Git returned an invalid amend baseline record.");
        }

        if (fields.Length == 1)
        {
            return await ReadEmptyTreeObjectIdAsync(
                repositoryRoot,
                cancellationToken).ConfigureAwait(false);
        }

        ValidateFullCommitId(fields[1], "commit-message amend parent");
        return fields[1].ToLowerInvariant();
    }

    private async Task<string> ReadEmptyTreeObjectIdAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        // Do not embed the SHA-1 empty-tree ID: repositories initialized with
        // Git's SHA-256 object format use a different immutable tree ID.
        // hash-object computes only because -w is intentionally absent.
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["hash-object", "-t", "tree", "--stdin"],
            cancellationToken,
            standardInput: string.Empty).ConfigureAwait(false);
        EnsureComplete(result, "commit-message empty-tree baseline");
        if (result.StandardErrorTruncated)
        {
            throw new GitOutputLimitException("commit-message empty-tree diagnostics");
        }

        var objectId = DecodeUtf8(result.StandardOutput, "commit-message empty-tree baseline").Trim();
        ValidateFullCommitId(objectId, "empty-tree object ID");
        return objectId.ToLowerInvariant();
    }

    private async Task<string> ReadCommitMessageIndexFingerprintAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["ls-files", "--stage", "-z", "--"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "commit-message index");
        if (result.StandardErrorTruncated)
        {
            throw new GitOutputLimitException("commit-message index diagnostics");
        }

        return Convert.ToHexString(SHA256.HashData(result.StandardOutput)).ToLowerInvariant();
    }

    private async Task<byte[]> ReadCommitMessagePatchAsync(
        string repositoryRoot,
        string baselineId,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            [
                "diff",
                "--cached",
                "--no-ext-diff",
                "--no-textconv",
                "--no-color",
                "--patch-with-raw",
                "--full-index",
                baselineId,
                "--",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "commit-message patch");
        if (result.StandardErrorTruncated || result.StandardOutput.Length > MaximumCommitMessagePatchBytes)
        {
            throw new GitOutputLimitException("commit-message patch");
        }

        return result.StandardOutput.ToArray();
    }

    private static bool CommitMessageSnapshotStatesMatch(
        CommitMessageSnapshotState before,
        CommitMessageSnapshotState after) =>
        PathsEqual(before.RootPath, after.RootPath)
        && string.Equals(before.HeadId, after.HeadId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(before.Branch, after.Branch, StringComparison.Ordinal)
        && before.IsDetached == after.IsDetached
        && before.IsUnborn == after.IsUnborn
        && string.Equals(before.BaselineId, after.BaselineId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(before.IndexFingerprint, after.IndexFingerprint, StringComparison.OrdinalIgnoreCase);

    private static void ValidateFullCommitId(string commitId, string operation)
    {
        if (commitId.Length is not (40 or 64)
            || !commitId.All(IsHexCharacter))
        {
            throw new InvalidOperationException($"Git returned an invalid {operation}.");
        }
    }

    private sealed record CommitMessageSnapshotState(
        string RootPath,
        string HeadId,
        string Branch,
        bool IsDetached,
        bool IsUnborn,
        string BaselineId,
        string IndexFingerprint);
}
