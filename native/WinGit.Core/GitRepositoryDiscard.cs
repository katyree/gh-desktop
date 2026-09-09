using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace WinGit.Core;

/// <summary>
/// The operating-system action used to preserve a file before Git restores a
/// discarded working-tree path. The Core layer never implements a permanent
/// deletion fallback.
/// </summary>
public interface IFileRecycler
{
    Task RecycleAsync(string fullPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// An immutable, content-bound preview of a full-file discard operation.
/// </summary>
public sealed class DiscardSnapshot
{
    internal DiscardSnapshot(
        string rootPath,
        string headId,
        string branch,
        bool isDetached,
        bool isUnborn,
        string indexFingerprint,
        IReadOnlyList<FileChange> files,
        IReadOnlyDictionary<string, DiscardPathState> pathStates,
        IReadOnlyDictionary<string, DiscardSubmoduleState> submoduleStates)
    {
        RootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        HeadId = headId ?? throw new ArgumentNullException(nameof(headId));
        Branch = branch ?? throw new ArgumentNullException(nameof(branch));
        IndexFingerprint = indexFingerprint ?? throw new ArgumentNullException(nameof(indexFingerprint));
        Files = new List<FileChange>(files ?? throw new ArgumentNullException(nameof(files))).AsReadOnly();
        PathStates = new ReadOnlyDictionary<string, DiscardPathState>(
            new Dictionary<string, DiscardPathState>(
                pathStates ?? throw new ArgumentNullException(nameof(pathStates)),
                StringComparer.Ordinal));
        SubmoduleStates = new ReadOnlyDictionary<string, DiscardSubmoduleState>(
            new Dictionary<string, DiscardSubmoduleState>(
                submoduleStates ?? throw new ArgumentNullException(nameof(submoduleStates)),
                StringComparer.Ordinal));
        IsDetached = isDetached;
        IsUnborn = isUnborn;

        if (Files.Count == 0)
        {
            throw new ArgumentException("A discard snapshot must contain at least one file.", nameof(files));
        }
    }

    public string RootPath { get; }

    /// <summary>Empty when the repository has no current commit.</summary>
    public string HeadId { get; }

    /// <summary>Empty when HEAD is detached or the repository is unborn.</summary>
    public string Branch { get; }

    public bool IsDetached { get; }

    public bool IsUnborn { get; }

    /// <summary>SHA-256 of the complete index stage listing at capture time.</summary>
    public string IndexFingerprint { get; }

    /// <summary>The exact status entries selected for discard.</summary>
    public IReadOnlyList<FileChange> Files { get; }

    internal IReadOnlyDictionary<string, DiscardPathState> PathStates { get; }

    internal IReadOnlyDictionary<string, DiscardSubmoduleState> SubmoduleStates { get; }
}

/// <summary>Base error for a discard preview or operation.</summary>
public class DiscardException : InvalidOperationException
{
    public DiscardException(string message)
        : base(message)
    {
    }

    public DiscardException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when discard inputs no longer describe the repository state.</summary>
public sealed class DiscardSnapshotStaleException : DiscardException
{
    public DiscardSnapshotStaleException(string message)
        : base(message)
    {
    }
}

public sealed partial class GitRepositoryService
{
    private const int MaximumDiscardFiles = 1_000;

    /// <summary>
    /// Captures the selected status entries and the full path content identity
    /// required to safely discard them later.
    /// </summary>
    public async Task<DiscardSnapshot> CaptureDiscardSnapshotAsync(
        string root,
        IReadOnlyList<FileChange> files,
        CancellationToken cancellationToken = default)
    {
        var selectedFiles = ValidateDiscardFileInput(files);
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var matchedFiles = MatchDiscardFiles(repositoryRoot, status, selectedFiles);
        var submodulePaths = await ReadSubmodulePathsAsync(
            repositoryRoot,
            GetDiscardPaths(matchedFiles).ToHashSet(StringComparer.Ordinal),
            status,
            cancellationToken).ConfigureAwait(false);
        var submoduleStates = await ReadSubmoduleStatesAsync(
            repositoryRoot,
            submodulePaths,
            cancellationToken).ConfigureAwait(false);
        if (submoduleStates.Values.Any(state => state.IsDirty))
        {
            throw new DiscardException(
                "Discarding a submodule with nested working-tree changes is not supported safely; review or stash the nested changes first.");
        }

        var indexFingerprint = await ReadCommitMessageIndexFingerprintAsync(
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        var paths = GetDiscardPaths(matchedFiles);
        var pathStates = await ReadDiscardPathStatesAsync(
            repositoryRoot,
            paths,
            cancellationToken).ConfigureAwait(false);

        EnsureDiscardPathTypesSupported(matchedFiles, pathStates, submodulePaths);
        return new DiscardSnapshot(
            repositoryRoot,
            status.HeadId,
            status.Branch,
            status.IsDetached,
            status.IsUnborn,
            indexFingerprint,
            matchedFiles,
            pathStates,
            submoduleStates);
    }

    /// <summary>
    /// Moves eligible current files to the injected Recycle Bin adapter, then
    /// restores Git's selected index and tracked work-tree paths. Every
    /// repository mutation is serialized and preceded by an in-gate snapshot
    /// check. A recycler failure never falls back to permanent deletion.
    /// </summary>
    public async Task DiscardChangesAsync(
        string root,
        DiscardSnapshot snapshot,
        IFileRecycler recycler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(recycler);

        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        EnsureDiscardSnapshotRepository(repositoryRoot, snapshot);

        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var before = await ReadDiscardObservedStateAsync(
                    path,
                    snapshot,
                    cancellationToken).ConfigureAwait(false);
                EnsureDiscardSnapshotMatches(snapshot, before, allowRecycledPaths: false);

                var plan = await BuildDiscardPlanAsync(
                    path,
                    snapshot.Files,
                    before.Status,
                    cancellationToken).ConfigureAwait(false);
                var recycledPaths = new List<string>(plan.RecyclePaths.Count);
                foreach (var relativePath in plan.RecyclePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullPath = GetFullPathForGitPath(path, relativePath);
                    try
                    {
                        await recycler.RecycleAsync(fullPath, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        throw new DiscardException(
                            $"Unable to move '{relativePath}' to the Recycle Bin. No Git restore was attempted.",
                            exception);
                    }

                    recycledPaths.Add(relativePath);
                }

                var afterRecycle = await ReadDiscardObservedStateAsync(
                    path,
                    snapshot,
                    cancellationToken).ConfigureAwait(false);
                EnsureDiscardSnapshotMatches(
                    snapshot,
                    afterRecycle,
                    allowRecycledPaths: true,
                    recycledPaths);

                if (plan.SubmodulePaths.Count > 0)
                {
                    var submoduleArguments = new List<string>
                    {
                        "submodule",
                        "update",
                        "--recursive",
                        "--force",
                        "--",
                    };
                    submoduleArguments.AddRange(plan.SubmodulePaths.Select(ToLiteralPathSpec));
                    await processRunner.RunAsync(
                        path,
                        submoduleArguments,
                        cancellationToken).ConfigureAwait(false);
                }

                if (plan.IndexResetPaths.Count > 0)
                {
                    await processRunner.RunAsync(
                        path,
                        [
                            "reset",
                            "--pathspec-from-file=-",
                            "--pathspec-file-nul",
                        ],
                            cancellationToken,
                        standardInput: string.Join(
                            '\0',
                            plan.IndexResetPaths.Select(ToLiteralPathSpec)) + "\0").ConfigureAwait(false);
                }

                if (plan.WorkTreeRestorePaths.Count > 0)
                {
                    await processRunner.RunAsync(
                        path,
                        [
                            "checkout-index",
                            "-f",
                            "-u",
                            "-q",
                            "--stdin",
                            "-z",
                        ],
                        cancellationToken,
                        expectedExitCodes: [0, 1],
                        standardInput: string.Join('\0', plan.WorkTreeRestorePaths) + "\0").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }

    private async Task<DiscardObservedState> ReadDiscardObservedStateAsync(
        string repositoryRoot,
        DiscardSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var indexFingerprint = await ReadCommitMessageIndexFingerprintAsync(
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        var pathStates = await ReadDiscardPathStatesAsync(
            repositoryRoot,
            snapshot.PathStates.Keys,
            cancellationToken).ConfigureAwait(false);
        var submoduleStates = await ReadSubmoduleStatesAsync(
            repositoryRoot,
            snapshot.SubmoduleStates.Keys,
            cancellationToken).ConfigureAwait(false);
        return new DiscardObservedState(status, indexFingerprint, pathStates, submoduleStates);
    }

    private async Task<DiscardPlan> BuildDiscardPlanAsync(
        string repositoryRoot,
        IReadOnlyList<FileChange> files,
        RepositoryStatus status,
        CancellationToken cancellationToken)
    {
        var pathsToRecycle = new List<string>();
        var pathsToReset = new List<string>();
        var pathsToRestore = new List<string>();
        var selectedPathSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            AddUniquePath(pathsToReset, file.Path);
            selectedPathSet.Add(file.Path);

            if (file.Kind is ChangeKind.Renamed or ChangeKind.Copied)
            {
                if (string.IsNullOrEmpty(file.OldPath))
                {
                    throw new DiscardException(
                        $"Git did not provide the original path for '{file.Path}'.");
                }

                AddUniquePath(pathsToReset, file.OldPath);
                AddUniquePath(pathsToRestore, file.OldPath);
                selectedPathSet.Add(file.OldPath);
            }
            else
            {
                AddUniquePath(pathsToRestore, file.Path);
            }

            if (file.Kind != ChangeKind.Deleted)
            {
                AddUniquePath(pathsToRecycle, file.Path);
            }
        }

        var submodulePaths = await ReadSubmodulePathsAsync(
            repositoryRoot,
            selectedPathSet,
            status,
            cancellationToken).ConfigureAwait(false);
        pathsToRecycle.RemoveAll(path => submodulePaths.Contains(path));
        pathsToRestore.RemoveAll(path => submodulePaths.Contains(path));

        var indexChangedPaths = GetIndexChangedPaths(files);
        var indexResetPaths = pathsToReset
            .Where(indexChangedPaths.Contains)
            .ToArray();
        var addedIndexPaths = files
            .Where(file => IsIndexAdded(file))
            .Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
        var workTreeRestorePaths = pathsToRestore
            .Where(path => !submodulePaths.Contains(path) || !addedIndexPaths.Contains(path))
            .ToArray();

        return new DiscardPlan(
            pathsToRecycle,
            indexResetPaths,
            workTreeRestorePaths,
            submodulePaths.ToArray());
    }

    private async Task<HashSet<string>> ReadSubmodulePathsAsync(
        string repositoryRoot,
        IReadOnlySet<string> selectedPaths,
        RepositoryStatus status,
        CancellationToken cancellationToken)
    {
        if (selectedPaths.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["ls-files", "--stage", "-z", "--"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "discard submodule lookup");
        if (result.StandardErrorTruncated)
        {
            throw new GitOutputLimitException("discard submodule diagnostics");
        }

        var submodulePaths = ReadSubmodulePathsFromListing(
            repositoryRoot,
            selectedPaths,
            result.StandardOutput,
            "discard index path");

        // A staged deletion removes the gitlink from the index before discard
        // runs, so also inspect the captured HEAD tree. This keeps the normal
        // submodule restore path available for both index and work-tree deletes.
        if (!status.IsUnborn && status.HeadId.Length > 0)
        {
            var headResult = await processRunner.RunAsync(
                repositoryRoot,
                ["ls-tree", "-r", "-z", "--full-tree", status.HeadId, "--"],
                cancellationToken).ConfigureAwait(false);
            EnsureComplete(headResult, "discard HEAD submodule lookup");
            if (headResult.StandardErrorTruncated)
            {
                throw new GitOutputLimitException("discard HEAD submodule diagnostics");
            }

            foreach (var path in ReadSubmodulePathsFromListing(
                repositoryRoot,
                selectedPaths,
                headResult.StandardOutput,
                "discard HEAD path"))
            {
                submodulePaths.Add(path);
            }
        }

        return submodulePaths;
    }

    private static HashSet<string> ReadSubmodulePathsFromListing(
        string repositoryRoot,
        IReadOnlySet<string> selectedPaths,
        byte[] output,
        string operation)
    {
        var submodulePaths = new HashSet<string>(StringComparer.Ordinal);
        var records = DecodeUtf8(output, operation)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var record in records)
        {
            var separator = record.IndexOf('\t');
            if (separator < 0)
            {
                throw new InvalidOperationException("Git returned an invalid submodule record.");
            }

            var metadata = record[..separator].Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);
            var path = ValidateGitPath(
                repositoryRoot,
                record[(separator + 1)..],
                operation);
            if (metadata.Length >= 1
                && metadata[0] == "160000"
                && selectedPaths.Contains(path))
            {
                submodulePaths.Add(path);
            }
        }

        return submodulePaths;
    }

    private async Task<Dictionary<string, DiscardSubmoduleState>> ReadSubmoduleStatesAsync(
        string repositoryRoot,
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, DiscardSubmoduleState>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedPath = ValidateGitPath(repositoryRoot, path, "submodule path");
            var fullPath = GetFullPathForGitPath(repositoryRoot, normalizedPath);
            states[normalizedPath] = await ReadSubmoduleStateAsync(
                normalizedPath,
                fullPath,
                cancellationToken).ConfigureAwait(false);
        }

        return states;
    }

    private async Task<DiscardSubmoduleState> ReadSubmoduleStateAsync(
        string relativePath,
        string fullPath,
        CancellationToken cancellationToken)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (FileNotFoundException)
        {
            return DiscardSubmoduleState.Missing(relativePath);
        }
        catch (DirectoryNotFoundException)
        {
            return DiscardSubmoduleState.Missing(relativePath);
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new DiscardException(
                $"Reparse-point submodule path '{relativePath}' cannot be discarded safely.");
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            return DiscardSubmoduleState.Uninitialized(relativePath);
        }

        RepositoryStatus nestedStatus;
        try
        {
            nestedStatus = await GetStatusAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (GitCommandException exception)
        {
            throw new DiscardException(
                $"The submodule '{relativePath}' is not initialized as a readable Git worktree.",
                exception);
        }

        var strictStatus = await processRunner.RunAsync(
            fullPath,
            [
                "status",
                "--porcelain=v2",
                "--branch",
                "--untracked-files=all",
                "--ignored=matching",
                "-z",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(strictStatus, "discard submodule status");
        if (strictStatus.StandardErrorTruncated)
        {
            throw new GitOutputLimitException("discard submodule diagnostics");
        }

        var records = DecodeUtf8(strictStatus.StandardOutput, "discard submodule status")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var isDirty = records.Any(record => !record.StartsWith("# ", StringComparison.Ordinal));
        var indexFingerprint = await ReadCommitMessageIndexFingerprintAsync(
            fullPath,
            cancellationToken).ConfigureAwait(false);
        var statusFingerprint = Convert.ToHexString(
            SHA256.HashData(strictStatus.StandardOutput)).ToLowerInvariant();
        return new DiscardSubmoduleState(
            relativePath,
            true,
            nestedStatus.HeadId,
            indexFingerprint,
            statusFingerprint,
            isDirty);
    }

    private static HashSet<string> GetIndexChangedPaths(IReadOnlyList<FileChange> files)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (HasIndexChanges(file))
            {
                paths.Add(file.Path);
                if (file.Kind is ChangeKind.Renamed or ChangeKind.Copied
                    && !string.IsNullOrEmpty(file.OldPath))
                {
                    paths.Add(file.OldPath);
                }
            }
        }

        return paths;
    }

    private static bool HasIndexChanges(FileChange file) =>
        !string.IsNullOrEmpty(file.IndexStatus)
        && file.IndexStatus is not "?" and not "!";

    private static bool IsIndexAdded(FileChange file) =>
        file.IndexStatus.Contains('A', StringComparison.Ordinal);

    private static IEnumerable<string> GetFileAndOriginalPaths(FileChange file)
    {
        yield return file.Path;
        if (file.Kind is ChangeKind.Renamed or ChangeKind.Copied
            && !string.IsNullOrEmpty(file.OldPath))
        {
            yield return file.OldPath;
        }
    }

    private async Task<Dictionary<string, DiscardPathState>> ReadDiscardPathStatesAsync(
        string repositoryRoot,
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, DiscardPathState>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedPath = ValidateGitPath(repositoryRoot, path, nameof(paths));
            var fullPath = GetFullPathForGitPath(repositoryRoot, normalizedPath);
            states[normalizedPath] = await ReadDiscardPathStateAsync(
                normalizedPath,
                fullPath,
                cancellationToken).ConfigureAwait(false);
        }

        return states;
    }

    private static async Task<DiscardPathState> ReadDiscardPathStateAsync(
        string relativePath,
        string fullPath,
        CancellationToken cancellationToken)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (FileNotFoundException)
        {
            return DiscardPathState.Missing(relativePath);
        }
        catch (DirectoryNotFoundException)
        {
            return DiscardPathState.Missing(relativePath);
        }

        var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        if (isReparsePoint)
        {
            return new DiscardPathState(
                relativePath,
                true,
                isDirectory,
                true,
                0,
                $"reparse:{attributes}");
        }

        if (isDirectory)
        {
            var directoryTime = Directory.GetLastWriteTimeUtc(fullPath).Ticks;
            return new DiscardPathState(
                relativePath,
                true,
                true,
                false,
                0,
                $"directory:{attributes}:{directoryTime}");
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new DiscardPathState(
            relativePath,
            true,
            false,
            false,
            stream.Length,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static void EnsureDiscardPathTypesSupported(
        IReadOnlyList<FileChange> files,
        IReadOnlyDictionary<string, DiscardPathState> pathStates,
        IReadOnlySet<string> submodulePaths)
    {
        foreach (var file in files)
        {
            foreach (var path in GetFileAndOriginalPaths(file))
            {
                var state = pathStates[path];
                if (state.IsReparsePoint)
                {
                    throw new DiscardException(
                        $"Reparse-point path '{path}' cannot be discarded safely by the native workflow.");
                }

                if (state.IsDirectory
                    && file.Kind is not ChangeKind.Deleted)
                {
                    // A Git submodule is restored through Git and is the one
                    // supported directory-shaped status. Other directories
                    // cannot be passed to the file Recycle Bin adapter.
                    if (!submodulePaths.Contains(path)
                        || file.Path != path
                        || file.Kind == ChangeKind.Untracked)
                    {
                        throw new DiscardException(
                            $"Directory path '{path}' cannot be discarded as a file.");
                    }
                }
            }
        }
    }

    private static List<FileChange> MatchDiscardFiles(
        string repositoryRoot,
        RepositoryStatus status,
        IReadOnlyList<FileChange> selectedFiles)
    {
        var matched = new List<FileChange>(selectedFiles.Count);
        foreach (var selected in selectedFiles)
        {
            var normalizedPath = ValidateGitPath(repositoryRoot, selected.Path, nameof(selectedFiles));
            var normalizedOldPath = string.IsNullOrEmpty(selected.OldPath)
                ? null
                : ValidateGitPath(repositoryRoot, selected.OldPath, nameof(selectedFiles));
            var current = status.Changes.FirstOrDefault(change =>
                string.Equals(change.Path, normalizedPath, StringComparison.Ordinal)
                && string.Equals(change.OldPath, normalizedOldPath, StringComparison.Ordinal)
                && change.Kind == selected.Kind
                && string.Equals(change.IndexStatus, selected.IndexStatus, StringComparison.Ordinal)
                && string.Equals(change.WorkTreeStatus, selected.WorkTreeStatus, StringComparison.Ordinal));
            if (current is null)
            {
                throw new DiscardSnapshotStaleException(
                    $"The selected change '{normalizedPath}' is stale. Refresh the repository before discarding it.");
            }

            matched.Add(current);
        }

        return matched;
    }

    private static List<FileChange> ValidateDiscardFileInput(IReadOnlyList<FileChange> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count is < 1 or > MaximumDiscardFiles)
        {
            throw new ArgumentException(
                $"A discard selection must contain between 1 and {MaximumDiscardFiles} files.",
                nameof(files));
        }

        var selected = new List<FileChange>(files.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (!seen.Add(file.Path))
            {
                throw new ArgumentException(
                    "A discard selection cannot contain the same path more than once.",
                    nameof(files));
            }

            selected.Add(file);
        }

        return selected;
    }

    private static IReadOnlyList<string> GetDiscardPaths(IReadOnlyList<FileChange> files)
    {
        var paths = new List<string>(files.Count * 2);
        foreach (var file in files)
        {
            AddUniquePath(paths, file.Path);
            if (file.Kind is ChangeKind.Renamed or ChangeKind.Copied)
            {
                if (string.IsNullOrEmpty(file.OldPath))
                {
                    throw new DiscardException(
                        $"Git did not provide the original path for '{file.Path}'.");
                }

                AddUniquePath(paths, file.OldPath);
            }
        }

        return paths;
    }

    private static void EnsureDiscardSnapshotRepository(
        string repositoryRoot,
        DiscardSnapshot snapshot)
    {
        if (!PathsEqual(repositoryRoot, snapshot.RootPath))
        {
            throw new DiscardSnapshotStaleException(
                "The discard snapshot belongs to a different repository.");
        }
    }

    private static void EnsureDiscardSnapshotMatches(
        DiscardSnapshot snapshot,
        DiscardObservedState observed,
        bool allowRecycledPaths,
        IReadOnlyCollection<string>? recycledPaths = null)
    {
        if (!string.Equals(snapshot.HeadId, observed.Status.HeadId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.Branch, observed.Status.Branch, StringComparison.Ordinal)
            || snapshot.IsDetached != observed.Status.IsDetached
            || snapshot.IsUnborn != observed.Status.IsUnborn
            || !string.Equals(snapshot.IndexFingerprint, observed.IndexFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new DiscardSnapshotStaleException(
                "The repository HEAD, branch state, or index changed while discard was preparing.");
        }

        var recycled = recycledPaths is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : recycledPaths.ToHashSet(StringComparer.Ordinal);
        foreach (var (path, expected) in snapshot.PathStates)
        {
            var actual = observed.PathStates[path];
            if (allowRecycledPaths && recycled.Contains(path))
            {
                if (actual.Exists)
                {
                    throw new DiscardSnapshotStaleException(
                        $"The recycled path '{path}' is still present; Git restore was not attempted.");
                }

                continue;
            }

            if (!DiscardPathStatesMatch(expected, actual))
            {
                throw new DiscardSnapshotStaleException(
                    $"The selected path '{path}' changed while discard was preparing.");
            }
        }

        foreach (var (path, expected) in snapshot.SubmoduleStates)
        {
            var actual = observed.SubmoduleStates[path];
            if (!DiscardSubmoduleStatesMatch(expected, actual))
            {
                throw new DiscardSnapshotStaleException(
                    $"The selected submodule '{path}' changed while discard was preparing.");
            }
        }

        if (!allowRecycledPaths)
        {
            foreach (var expectedFile in snapshot.Files)
            {
                var current = observed.Status.Changes.FirstOrDefault(change =>
                    string.Equals(change.Path, expectedFile.Path, StringComparison.Ordinal)
                    && string.Equals(change.OldPath, expectedFile.OldPath, StringComparison.Ordinal));
                if (current is null
                    || current.Kind != expectedFile.Kind
                    || !string.Equals(current.IndexStatus, expectedFile.IndexStatus, StringComparison.Ordinal)
                    || !string.Equals(current.WorkTreeStatus, expectedFile.WorkTreeStatus, StringComparison.Ordinal))
                {
                    throw new DiscardSnapshotStaleException(
                        $"The selected change '{expectedFile.Path}' changed while discard was preparing.");
                }
            }
        }
    }

    private static bool DiscardPathStatesMatch(
        DiscardPathState expected,
        DiscardPathState actual) =>
        expected.Exists == actual.Exists
        && expected.IsDirectory == actual.IsDirectory
        && expected.IsReparsePoint == actual.IsReparsePoint
        && expected.Length == actual.Length
        && string.Equals(expected.Fingerprint, actual.Fingerprint, StringComparison.Ordinal);

    private static bool DiscardSubmoduleStatesMatch(
        DiscardSubmoduleState expected,
        DiscardSubmoduleState actual) =>
        expected.Exists == actual.Exists
        && expected.IsDirty == actual.IsDirty
        && string.Equals(expected.HeadId, actual.HeadId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.IndexFingerprint, actual.IndexFingerprint, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.StatusFingerprint, actual.StatusFingerprint, StringComparison.OrdinalIgnoreCase);

    private static void AddUniquePath(List<string> paths, string path)
    {
        if (!paths.Contains(path, StringComparer.Ordinal))
        {
            paths.Add(path);
        }
    }

    private sealed record DiscardObservedState(
        RepositoryStatus Status,
        string IndexFingerprint,
        IReadOnlyDictionary<string, DiscardPathState> PathStates,
        IReadOnlyDictionary<string, DiscardSubmoduleState> SubmoduleStates);

    private sealed record DiscardPlan(
        IReadOnlyList<string> RecyclePaths,
        IReadOnlyList<string> IndexResetPaths,
        IReadOnlyList<string> WorkTreeRestorePaths,
        IReadOnlyList<string> SubmodulePaths);

}

internal sealed record DiscardPathState(
    string Path,
    bool Exists,
    bool IsDirectory,
    bool IsReparsePoint,
    long Length,
    string Fingerprint)
{
    internal static DiscardPathState Missing(string path) =>
        new(path, false, false, false, 0, "missing");
}

internal sealed record DiscardSubmoduleState(
    string Path,
    bool Exists,
    string HeadId,
    string IndexFingerprint,
    string StatusFingerprint,
    bool IsDirty)
{
    internal static DiscardSubmoduleState Missing(string path) =>
        new(path, false, string.Empty, string.Empty, string.Empty, false);

    internal static DiscardSubmoduleState Uninitialized(string path) =>
        new(path, true, string.Empty, string.Empty, "uninitialized", true);
}
