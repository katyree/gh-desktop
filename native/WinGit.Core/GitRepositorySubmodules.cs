namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private const int MaximumSubmoduleEntries = 1_000;
    private const string SubmoduleGitLinkMode = "160000";

    /// <summary>
    /// Reads the containing repository's top-level gitlinks and their local
    /// worktree state. This method never initializes, fetches, or updates a
    /// submodule.
    /// </summary>
    public async Task<IReadOnlyList<SubmoduleSnapshot>> GetSubmodulesAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        var entries = await ReadSubmoduleIndexEntriesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var snapshots = new List<SubmoduleSnapshot>(entries.Count);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = GetFullPathForGitPath(repositoryRoot, entry.Path);
            var state = await ReadSubmoduleWorktreeStateAsync(
                repositoryRoot,
                entry.Path,
                fullPath,
                cancellationToken).ConfigureAwait(false);
            snapshots.Add(
                new SubmoduleSnapshot(
                    repositoryRoot,
                    entry.Path,
                    entry.ExpectedCommitId,
                    state.CheckedOutHeadCommitId,
                    state.IsInitialized,
                    state.IsDirty));
        }

        return snapshots.ToArray();
    }

    /// <summary>
    /// Updates one explicitly selected submodule to the commit recorded in the
    /// containing index. The captured gitlink and local HEAD are checked again
    /// inside the serialized mutation gate before Git can change the worktree.
    /// </summary>
    public async Task UpdateSubmoduleAsync(
        string root,
        SubmoduleSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!PathsEqual(repositoryRoot, snapshot.RootPath))
        {
            throw new SubmoduleSnapshotStaleException(
                snapshot.Path,
                "the snapshot belongs to a different repository");
        }

        var normalizedPath = ValidateGitPath(repositoryRoot, snapshot.Path, nameof(snapshot));
        if (!string.Equals(normalizedPath, snapshot.Path, StringComparison.Ordinal))
        {
            throw new SubmoduleSnapshotStaleException(
                snapshot.Path,
                "the captured path is not canonical");
        }

        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var currentEntry = await ReadSubmoduleIndexEntryAsync(
                    path,
                    normalizedPath,
                    cancellationToken).ConfigureAwait(false);
                if (currentEntry is null
                    || !string.Equals(
                        currentEntry.ExpectedCommitId,
                        snapshot.ExpectedIndexCommitId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new SubmoduleSnapshotStaleException(
                        normalizedPath,
                        "the containing repository's index gitlink changed");
                }

                var fullPath = GetFullPathForGitPath(path, normalizedPath);
                var currentState = await ReadSubmoduleWorktreeStateAsync(
                    path,
                    normalizedPath,
                    fullPath,
                    cancellationToken).ConfigureAwait(false);
                if (currentState.IsInitialized != snapshot.IsInitialized
                    || !string.Equals(
                        currentState.CheckedOutHeadCommitId,
                        snapshot.CheckedOutHeadCommitId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new SubmoduleSnapshotStaleException(
                        normalizedPath,
                        "the local submodule HEAD or initialization state changed");
                }

                if (snapshot.IsDirty || currentState.IsDirty)
                {
                    throw new SubmoduleUpdateBlockedException(
                        normalizedPath,
                        "local tracked, untracked, or unmerged changes would be overwritten; review or stash them first");
                }

                await processRunner.RunAsync(
                    path,
                    [
                        "--literal-pathspecs",
                        "submodule",
                        "update",
                        "--init",
                        "--checkout",
                        "--",
                        normalizedPath,
                    ],
                    cancellationToken).ConfigureAwait(false);

                var after = await ReadSubmoduleWorktreeStateAsync(
                    path,
                    normalizedPath,
                    fullPath,
                    cancellationToken).ConfigureAwait(false);
                if (!after.IsInitialized
                    || !string.Equals(
                        after.CheckedOutHeadCommitId,
                        snapshot.ExpectedIndexCommitId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Git did not update submodule '{normalizedPath}' to the index commit.");
                }

                if (after.IsDirty)
                {
                    throw new InvalidOperationException(
                        $"Submodule '{normalizedPath}' remains dirty after the update; review its local changes before continuing.");
                }
            }).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SubmoduleIndexEntry>> ReadSubmoduleIndexEntriesAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["ls-files", "--stage", "-z", "--"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "submodule index lookup");
        if (result.StandardErrorTruncated)
        {
            throw new GitOutputLimitException("submodule index diagnostics");
        }

        var entries = new List<SubmoduleIndexEntry>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var records = DecodeUtf8(result.StandardOutput, "submodule index lookup")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var record in records)
        {
            var separator = record.IndexOf('\t');
            if (separator <= 0)
            {
                throw new InvalidOperationException("Git returned an invalid submodule index record.");
            }

            var metadata = record[..separator].Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length < 3)
            {
                throw new InvalidOperationException("Git returned an incomplete submodule index record.");
            }

            var path = ValidateGitPath(
                repositoryRoot,
                record[(separator + 1)..],
                "submodule index path");
            if (!string.Equals(metadata[0], SubmoduleGitLinkMode, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(metadata[2], "0", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The submodule '{path}' has unresolved index stages; resolve the conflict before updating it.");
            }

            ValidateFullCommitId(metadata[1], "submodule index commit");
            if (!seenPaths.Add(path))
            {
                throw new InvalidOperationException($"Git returned duplicate submodule path '{path}'.");
            }

            if (entries.Count >= MaximumSubmoduleEntries)
            {
                throw new GitOutputLimitException("submodule list");
            }

            entries.Add(new SubmoduleIndexEntry(path, metadata[1].ToLowerInvariant()));
        }

        return entries
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<SubmoduleIndexEntry?> ReadSubmoduleIndexEntryAsync(
        string repositoryRoot,
        string path,
        CancellationToken cancellationToken)
    {
        var entries = await ReadSubmoduleIndexEntriesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        return entries.FirstOrDefault(entry => string.Equals(entry.Path, path, StringComparison.Ordinal));
    }

    private async Task<SubmoduleWorktreeState> ReadSubmoduleWorktreeStateAsync(
        string repositoryRoot,
        string relativePath,
        string fullPath,
        CancellationToken cancellationToken)
    {
        if (ContainsReparsePoint(repositoryRoot, fullPath))
        {
            throw new InvalidOperationException(
                $"The submodule path '{relativePath}' or one of its parent directories is a reparse point and cannot be inspected safely.");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (FileNotFoundException)
        {
            return SubmoduleWorktreeState.Uninitialized;
        }
        catch (DirectoryNotFoundException)
        {
            return SubmoduleWorktreeState.Uninitialized;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                $"The submodule path '{relativePath}' is a reparse point and cannot be inspected safely.");
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new InvalidOperationException(
                $"The submodule path '{relativePath}' exists but is not a directory.");
        }

        var metadataPath = Path.Combine(fullPath, ".git");
        if (!File.Exists(metadataPath) && !Directory.Exists(metadataPath))
        {
            bool hasExistingEntries;
            try
            {
                // One entry is enough to prevent Git from populating a
                // metadata-less directory around caller-owned files. Keep the
                // probe bounded instead of walking an arbitrary directory.
                hasExistingEntries = Directory.EnumerateFileSystemEntries(fullPath)
                    .Take(1)
                    .Any();
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException(
                    $"The contents of uninitialized submodule '{relativePath}' cannot be inspected safely.",
                    exception);
            }

            return new SubmoduleWorktreeState(false, null, hasExistingEntries);
        }

        var metadataAttributes = File.GetAttributes(metadataPath);
        if (metadataAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                $"The submodule metadata for '{relativePath}' is a reparse point and cannot be inspected safely.");
        }

        var identityResult = await processRunner.RunAsync(
            fullPath,
            ["rev-parse", "--show-toplevel", "--is-inside-work-tree"],
            cancellationToken,
            expectedExitCodes: [128]).ConfigureAwait(false);
        EnsureComplete(identityResult, "submodule worktree lookup");
        if (identityResult.StandardErrorTruncated)
        {
            throw new GitOutputLimitException("submodule worktree diagnostics");
        }

        if (identityResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The submodule '{relativePath}' has Git metadata but is not a readable worktree.");
        }

        var identityLines = DecodeUtf8(identityResult.StandardOutput, "submodule worktree lookup")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (identityLines.Length < 2
            || !string.Equals(identityLines[1].Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The submodule '{relativePath}' is not a Git worktree.");
        }

        var reportedRoot = identityLines[0].Trim();
        var worktreeRoot = Path.IsPathRooted(reportedRoot)
            ? Path.GetFullPath(reportedRoot)
            : Path.GetFullPath(Path.Combine(fullPath, reportedRoot));
        if (!PathsEqual(worktreeRoot, fullPath))
        {
            throw new InvalidOperationException(
                $"The submodule '{relativePath}' Git metadata resolves to a different worktree.");
        }

        var headResult = await processRunner.RunAsync(
            fullPath,
            ["rev-parse", "--verify", "--end-of-options", "HEAD^{commit}"],
            cancellationToken,
            expectedExitCodes: [128]).ConfigureAwait(false);
        EnsureComplete(headResult, "submodule HEAD lookup");
        if (headResult.StandardErrorTruncated)
        {
            throw new GitOutputLimitException("submodule HEAD diagnostics");
        }

        if (headResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The submodule '{relativePath}' has no readable checked-out HEAD.");
        }

        var headId = DecodeUtf8(headResult.StandardOutput, "submodule HEAD lookup").Trim();
        ValidateFullCommitId(headId, "submodule HEAD");

        var statusResult = await processRunner.RunAsync(
            fullPath,
            ["status", "--porcelain=v2", "--untracked-files=all", "-z"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(statusResult, "submodule status");
        if (statusResult.StandardErrorTruncated)
        {
            throw new GitOutputLimitException("submodule status diagnostics");
        }

        return new SubmoduleWorktreeState(
            true,
            headId.ToLowerInvariant(),
            statusResult.StandardOutput.Length > 0);
    }

    private sealed record SubmoduleIndexEntry(string Path, string ExpectedCommitId);

    private sealed record SubmoduleWorktreeState(
        bool IsInitialized,
        string? CheckedOutHeadCommitId,
        bool IsDirty)
    {
        internal static SubmoduleWorktreeState Uninitialized { get; } = new(false, null, false);
    }
}
