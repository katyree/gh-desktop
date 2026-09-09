namespace WinGit.Core;

/// <summary>
/// An immutable view of one top-level submodule gitlink and its local worktree.
/// </summary>
public sealed class SubmoduleSnapshot
{
    public SubmoduleSnapshot(
        string rootPath,
        string path,
        string expectedIndexCommitId,
        string? checkedOutHeadCommitId,
        bool isInitialized,
        bool isDirty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIndexCommitId);

        RootPath = System.IO.Path.GetFullPath(rootPath);
        Path = path.Replace('\\', '/');
        ExpectedIndexCommitId = NormalizeFullCommitId(expectedIndexCommitId, nameof(expectedIndexCommitId));

        if (checkedOutHeadCommitId is not null)
        {
            CheckedOutHeadCommitId = NormalizeFullCommitId(
                checkedOutHeadCommitId,
                nameof(checkedOutHeadCommitId));
        }

        if (isInitialized && CheckedOutHeadCommitId is null)
        {
            throw new ArgumentException(
                "An initialized submodule must have a checked-out HEAD commit.",
                nameof(checkedOutHeadCommitId));
        }

        if (!isInitialized && CheckedOutHeadCommitId is not null)
        {
            throw new ArgumentException(
                "An uninitialized submodule cannot have a checked-out HEAD commit.",
                nameof(checkedOutHeadCommitId));
        }

        IsInitialized = isInitialized;
        IsDirty = isDirty;
    }

    public string RootPath { get; }

    /// <summary>The repository-relative submodule path, using '/' separators.</summary>
    public string Path { get; }

    /// <summary>The exact commit recorded in the containing repository index.</summary>
    public string ExpectedIndexCommitId { get; }

    /// <summary>The local submodule HEAD, or null when the worktree is uninitialized.</summary>
    public string? CheckedOutHeadCommitId { get; }

    public bool IsInitialized { get; }

    /// <summary>True when the local submodule has tracked, untracked, or unmerged changes.</summary>
    public bool IsDirty { get; }

    private static string NormalizeFullCommitId(string commitId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitId, parameterName);
        if (commitId.Length is not (40 or 64)
            || !commitId.All(IsHexCharacter))
        {
            throw new ArgumentException(
                "Submodule commit IDs must be full 40- or 64-character hexadecimal IDs.",
                parameterName);
        }

        return commitId.ToLowerInvariant();
    }

    private static bool IsHexCharacter(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';
}

/// <summary>Raised when the containing repository no longer matches a captured submodule view.</summary>
public sealed class SubmoduleSnapshotStaleException : InvalidOperationException
{
    public SubmoduleSnapshotStaleException(string path, string message)
        : base($"The submodule snapshot for '{path}' is stale: {message}")
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>Raised when an update would overwrite local submodule changes.</summary>
public sealed class SubmoduleUpdateBlockedException : InvalidOperationException
{
    public SubmoduleUpdateBlockedException(string path, string message)
        : base($"The submodule '{path}' was not updated: {message}")
    {
        Path = path;
    }

    public string Path { get; }
}
