namespace WinGit.Core;

/// <summary>
/// An immutable, repository-relative history selection. The selected IDs are
/// caller-provided full object IDs in oldest-to-newest (chronological) order,
/// so this snapshot never follows a moving branch name while the native
/// history view is being populated. Native history rows may be displayed
/// newest-first; callers must reverse those rows before passing this list.
/// </summary>
public sealed class CommitSelectionSnapshot
{
    internal CommitSelectionSnapshot(
        string rootPath,
        IReadOnlyList<string> selectedCommitIds,
        bool isContiguous,
        IReadOnlyList<string> reachableSelectedCommitIds,
        IReadOnlyList<string> omittedSelectedCommitIds,
        string firstSelectedCommitId,
        string firstParentBaselineId,
        string lastSelectedCommitId,
        IReadOnlyList<FileChange> changedFiles,
        bool changedFilesTruncated)
    {
        RootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        SelectedCommitIds = new List<string>(selectedCommitIds ?? throw new ArgumentNullException(nameof(selectedCommitIds))).AsReadOnly();
        if (SelectedCommitIds.Count == 0)
        {
            throw new ArgumentException("A commit selection must contain at least one commit.", nameof(selectedCommitIds));
        }

        IsContiguous = isContiguous;
        ReachableSelectedCommitIds = new List<string>(reachableSelectedCommitIds ?? throw new ArgumentNullException(nameof(reachableSelectedCommitIds))).AsReadOnly();
        OmittedSelectedCommitIds = new List<string>(omittedSelectedCommitIds ?? throw new ArgumentNullException(nameof(omittedSelectedCommitIds))).AsReadOnly();
        FirstSelectedCommitId = firstSelectedCommitId ?? throw new ArgumentNullException(nameof(firstSelectedCommitId));
        FirstParentBaselineId = firstParentBaselineId ?? throw new ArgumentNullException(nameof(firstParentBaselineId));
        LastSelectedCommitId = lastSelectedCommitId ?? throw new ArgumentNullException(nameof(lastSelectedCommitId));
        ChangedFiles = new List<FileChange>(changedFiles ?? throw new ArgumentNullException(nameof(changedFiles))).AsReadOnly();
        ChangedFilesTruncated = changedFilesTruncated;
    }

    public string RootPath { get; }

    /// <summary>
    /// The full commit IDs in oldest-to-newest (chronological) order.
    /// </summary>
    public IReadOnlyList<string> SelectedCommitIds { get; }

    /// <summary>Whether the caller selected one contiguous history range.</summary>
    public bool IsContiguous { get; }

    /// <summary>
    /// Selected commits on the ancestry path used by the original multi-commit
    /// diff. For a non-contiguous selection this is the selected list because
    /// the original UI does not render a combined diff.
    /// </summary>
    public IReadOnlyList<string> ReachableSelectedCommitIds { get; }

    /// <summary>Selected commits omitted from the combined range's ancestry path.</summary>
    public IReadOnlyList<string> OmittedSelectedCommitIds { get; }

    public string FirstSelectedCommitId { get; }

    /// <summary>
    /// The first selected commit's first parent, or the repository-format
    /// specific empty-tree object ID for a root commit.
    /// </summary>
    public string FirstParentBaselineId { get; }

    public string LastSelectedCommitId { get; }

    /// <summary>
    /// Files changed by comparing FirstParentBaselineId with the last selected
    /// tree. Empty for a non-contiguous multi-commit selection, matching the
    /// Electron history view.
    /// </summary>
    public IReadOnlyList<FileChange> ChangedFiles { get; }

    public bool ChangedFilesTruncated { get; }

    /// <summary>True when the original view can load a combined selection diff.</summary>
    public bool HasCombinedDiff => SelectedCommitIds.Count == 1 || IsContiguous;
}

/// <summary>Raised when a captured history selection no longer resolves identically.</summary>
public sealed class CommitSelectionSnapshotStaleException : InvalidOperationException
{
    public CommitSelectionSnapshotStaleException(string message)
        : base(message)
    {
    }
}

public sealed partial class GitRepositoryService
{
    private const int MaximumCommitSelectionFiles = 1_000;

    /// <summary>
    /// Captures the selected commits and, for a single or contiguous selection,
    /// the same first-parent-to-last-tree range used by Electron's history view.
    /// Non-contiguous multi-selections intentionally expose no combined files
    /// or diff; they are informational selections only.
    /// </summary>
    public async Task<CommitSelectionSnapshot> CaptureCommitSelectionAsync(
        string root,
        IReadOnlyList<string> orderedCommitIds,
        bool isContiguous,
        int maxFiles = MaximumCommitSelectionFiles,
        CancellationToken cancellationToken = default)
    {
        var selection = ValidateCommitSelection(orderedCommitIds);
        ValidateCommitSelectionFileLimit(maxFiles);

        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        var parentIds = await ReadSelectionCommitParentsAsync(
            repositoryRoot,
            selection,
            cancellationToken).ConfigureAwait(false);

        var reachable = ComputeReachableSelectedIds(selection, isContiguous, parentIds);
        var reachableSet = reachable.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var omitted = isContiguous && selection.Count > 1
            ? selection.Where(id => !reachableSet.Contains(id)).ToArray()
            : Array.Empty<string>();
        var firstSelectedCommitId = selection[0];
        var lastSelectedCommitId = selection[^1];
        var firstParents = parentIds[firstSelectedCommitId];
        var firstParentBaselineId = firstParents.Count == 0
            ? await ReadEmptyTreeObjectIdAsync(repositoryRoot, cancellationToken).ConfigureAwait(false)
            : firstParents[0];

        IReadOnlyList<FileChange> changedFiles = [];
        var changedFilesTruncated = false;
        if (selection.Count == 1 || isContiguous)
        {
            (changedFiles, changedFilesTruncated) = await ReadCommitSelectionFilesAsync(
                repositoryRoot,
                firstParentBaselineId,
                lastSelectedCommitId,
                maxFiles,
                cancellationToken).ConfigureAwait(false);
        }

        return new CommitSelectionSnapshot(
            repositoryRoot,
            selection,
            isContiguous,
            reachable,
            omitted,
            firstSelectedCommitId,
            firstParentBaselineId,
            lastSelectedCommitId,
            changedFiles,
            changedFilesTruncated);
    }

    /// <summary>Reads one file from a captured single or contiguous selection range.</summary>
    public async Task<FileDiff> GetCommitSelectionFileDiffAsync(
        string root,
        CommitSelectionSnapshot snapshot,
        FileChange file,
        CancellationToken cancellationToken = default,
        bool hideWhitespaceChanges = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(file);
        if (!snapshot.HasCombinedDiff)
        {
            throw new InvalidOperationException(
                "Choose consecutive commits to compare their combined changes.");
        }

        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        EnsureCommitSelectionRepository(repositoryRoot, snapshot);
        var path = ValidateGitPath(repositoryRoot, file.Path, nameof(file));
        if (!snapshot.ChangedFiles.Any(changed =>
                string.Equals(changed.Path, path, StringComparison.Ordinal)
                && string.Equals(changed.OldPath, file.OldPath, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The selected file is not part of the captured history range.",
                nameof(file));
        }

        var arguments = new List<string>
        {
            "diff",
            "--no-ext-diff",
            "--no-textconv",
            "--no-color",
            "--binary",
            "--patch",
            "--unified=3",
            "--find-renames",
            "--find-copies",
            snapshot.FirstParentBaselineId,
            snapshot.LastSelectedCommitId,
        };
        if (hideWhitespaceChanges)
        {
            arguments.Add("--ignore-all-space");
        }

        arguments.Add("--");
        arguments.Add(ToLiteralPathSpec(path));
        AddOldPath(arguments, repositoryRoot, file.OldPath, path);

        var result = await processRunner.RunAsync(
            repositoryRoot,
            arguments,
            cancellationToken).ConfigureAwait(false);
        var diff = ParseDiff(result.StandardOutput, result.StandardOutputTruncated);
        return await EnrichImageDiffAsync(
            repositoryRoot,
            file,
            diff,
            new ImageDiffRequest(
                ImageDiffScope.Selection,
                snapshot.FirstParentBaselineId,
                snapshot.LastSelectedCommitId),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recomputes the bounded selection identity and rejects missing or changed
    /// commit objects before a caller uses a previously captured view.
    /// </summary>
    public async Task RevalidateCommitSelectionAsync(
        string root,
        CommitSelectionSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var maxFiles = Math.Max(snapshot.ChangedFiles.Count, 1);
        var current = await CaptureCommitSelectionAsync(
            root,
            snapshot.SelectedCommitIds,
            snapshot.IsContiguous,
            maxFiles,
            cancellationToken).ConfigureAwait(false);

        if (!CommitSelectionSnapshotsMatch(snapshot, current))
        {
            throw new CommitSelectionSnapshotStaleException(
                "The selected history commits or their bounded diff no longer match the captured selection.");
        }
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> ReadSelectionCommitParentsAsync(
        string repositoryRoot,
        IReadOnlyList<string> selection,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "rev-list",
            "--stdin",
            "--no-walk=unsorted",
            "--parents",
            "--end-of-options",
        };

        var result = await processRunner.RunAsync(
            repositoryRoot,
            arguments,
            cancellationToken,
            standardInput: string.Join("\n", selection) + "\n").ConfigureAwait(false);
        EnsureComplete(result, "commit selection parent lookup");
        if (result.StandardErrorTruncated)
        {
            throw new GitOutputLimitException("commit selection parent diagnostics");
        }

        var records = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var text = DecodeUtf8(result.StandardOutput, "commit selection parent lookup");
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0)
            {
                continue;
            }

            var commitId = fields[0].ToLowerInvariant();
            ValidateFullCommitId(commitId, "commit selection ID");
            var parents = new List<string>(Math.Max(fields.Length - 1, 0));
            foreach (var parent in fields.Skip(1))
            {
                var normalizedParent = parent.ToLowerInvariant();
                ValidateFullCommitId(normalizedParent, "commit selection parent ID");
                parents.Add(normalizedParent);
            }

            if (!records.TryAdd(commitId, parents.AsReadOnly()))
            {
                throw new InvalidOperationException("Git returned duplicate commit selection metadata.");
            }
        }

        if (records.Count != selection.Count || selection.Any(id => !records.ContainsKey(id)))
        {
            throw new InvalidOperationException(
                "Git did not return parent metadata for every selected commit.");
        }

        return records;
    }

    private async Task<(IReadOnlyList<FileChange> Files, bool Truncated)> ReadCommitSelectionFilesAsync(
        string repositoryRoot,
        string firstParentBaselineId,
        string lastSelectedCommitId,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            [
                "diff",
                "--no-ext-diff",
                "--no-textconv",
                "--no-color",
                "--name-status",
                "-z",
                "--find-renames",
                "--find-copies",
                firstParentBaselineId,
                lastSelectedCommitId,
                "--",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "commit selection file list");
        if (result.StandardErrorTruncated)
        {
            throw new GitOutputLimitException("commit selection file-list diagnostics");
        }

        var files = ParseCommitFiles(repositoryRoot, result.StandardOutput);
        if (files.Count <= maxFiles)
        {
            return (files, false);
        }

        return (files.Take(maxFiles).ToArray(), true);
    }

    private static IReadOnlyList<string> ComputeReachableSelectedIds(
        IReadOnlyList<string> selection,
        bool isContiguous,
        IReadOnlyDictionary<string, IReadOnlyList<string>> parentIds)
    {
        if (selection.Count == 1 || !isContiguous)
        {
            return selection.ToArray();
        }

        var selected = selection.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(selection[^1]);
        while (pending.Count > 0)
        {
            var commitId = pending.Pop();
            if (!reachable.Add(commitId))
            {
                continue;
            }

            foreach (var parentId in parentIds[commitId])
            {
                if (selected.Contains(parentId))
                {
                    pending.Push(parentId);
                }
            }
        }

        return selection.Where(reachable.Contains).ToArray();
    }

    private static List<string> ValidateCommitSelection(IReadOnlyList<string> orderedCommitIds)
    {
        ArgumentNullException.ThrowIfNull(orderedCommitIds);
        if (orderedCommitIds.Count is < 1 or > MaximumHistoryLimit)
        {
            throw new ArgumentException(
                $"A history selection must contain between 1 and {MaximumHistoryLimit} commits.",
                nameof(orderedCommitIds));
        }

        var ids = new List<string>(orderedCommitIds.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? idLength = null;
        foreach (var commitId in orderedCommitIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(commitId, nameof(orderedCommitIds));
            if (commitId != commitId.Trim()
                || commitId.Length is not (40 or 64)
                || !commitId.All(IsHexCharacter))
            {
                throw new ArgumentException(
                    "History selections must contain full 40- or 64-character hexadecimal commit IDs.",
                    nameof(orderedCommitIds));
            }

            idLength ??= commitId.Length;
            if (idLength.Value != commitId.Length)
            {
                throw new ArgumentException(
                    "History selections must use one Git object format.",
                    nameof(orderedCommitIds));
            }

            var normalized = commitId.ToLowerInvariant();
            if (!seen.Add(normalized))
            {
                throw new ArgumentException(
                    "A history selection cannot contain the same commit more than once.",
                    nameof(orderedCommitIds));
            }

            ids.Add(normalized);
        }

        return ids;
    }

    private static void ValidateCommitSelectionFileLimit(int maxFiles)
    {
        if (maxFiles is < 1 or > MaximumCommitSelectionFiles)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFiles),
                $"The selected history file limit must be between 1 and {MaximumCommitSelectionFiles}.");
        }
    }

    private static void EnsureCommitSelectionRepository(
        string repositoryRoot,
        CommitSelectionSnapshot snapshot)
    {
        if (!PathsEqual(repositoryRoot, snapshot.RootPath))
        {
            throw new CommitSelectionSnapshotStaleException(
                "The selected history snapshot belongs to a different repository.");
        }
    }

    private static bool CommitSelectionSnapshotsMatch(
        CommitSelectionSnapshot expected,
        CommitSelectionSnapshot actual) =>
        PathsEqual(expected.RootPath, actual.RootPath)
        && expected.IsContiguous == actual.IsContiguous
        && string.Equals(expected.FirstSelectedCommitId, actual.FirstSelectedCommitId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.FirstParentBaselineId, actual.FirstParentBaselineId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.LastSelectedCommitId, actual.LastSelectedCommitId, StringComparison.OrdinalIgnoreCase)
        && expected.SelectedCommitIds.SequenceEqual(actual.SelectedCommitIds, StringComparer.OrdinalIgnoreCase)
        && expected.ReachableSelectedCommitIds.SequenceEqual(actual.ReachableSelectedCommitIds, StringComparer.OrdinalIgnoreCase)
        && expected.OmittedSelectedCommitIds.SequenceEqual(actual.OmittedSelectedCommitIds, StringComparer.OrdinalIgnoreCase)
        && expected.ChangedFilesTruncated == actual.ChangedFilesTruncated
        && expected.ChangedFiles.SequenceEqual(actual.ChangedFiles);
}
