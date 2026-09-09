using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WinGit.Core.Codex;

namespace WinGit.Core;

/// <summary>
/// One caller-selected changed file.  A whole-file selection has no line
/// selections; a partial selection must contain at least one hunk or line.
/// </summary>
public sealed class SelectedChangesReviewFileSelection
{
    public SelectedChangesReviewFileSelection(
        FileChange file,
        bool includeWholeFile,
        IReadOnlyCollection<PartialDiffSelection>? selection = null,
        SelectedChangesReviewDiffSnapshot? diffSnapshot = null)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        IncludeWholeFile = includeWholeFile;
        Selection = new List<PartialDiffSelection>(selection ?? []).AsReadOnly();
        DiffSnapshot = diffSnapshot;

        if (includeWholeFile && Selection.Count > 0)
        {
            throw new ArgumentException(
                "A whole-file review selection cannot also contain line selections.",
                nameof(selection));
        }

        if (includeWholeFile && diffSnapshot is not null)
        {
            throw new ArgumentException(
                "A whole-file review selection cannot contain a partial diff snapshot.",
                nameof(diffSnapshot));
        }

        if (!includeWholeFile && Selection.Count == 0)
        {
            throw new ArgumentException(
                "A partial review selection must contain at least one hunk or line.",
                nameof(selection));
        }

        if (!includeWholeFile && diffSnapshot is null)
        {
            throw new ArgumentException(
                "Partial review selections must include the exact diff snapshot used for their line identities.",
                nameof(diffSnapshot));
        }

        if (diffSnapshot is not null
            && (!string.Equals(diffSnapshot.Diff.File.Path, file.Path, StringComparison.Ordinal)
                || !string.Equals(diffSnapshot.Diff.File.OldPath, file.OldPath, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The partial diff snapshot does not belong to the selected file.",
                nameof(diffSnapshot));
        }
    }

    public SelectedChangesReviewFileSelection(
        SelectedChangesReviewDiffSnapshot diffSnapshot,
        IReadOnlyCollection<PartialDiffSelection> selection)
        : this(
            (diffSnapshot ?? throw new ArgumentNullException(nameof(diffSnapshot))).Diff.File,
            includeWholeFile: false,
            selection,
            diffSnapshot)
    {
    }

    public FileChange File { get; }

    public bool IncludeWholeFile { get; }

    public IReadOnlyList<PartialDiffSelection> Selection { get; }

    /// <summary>The exact HEAD-to-worktree diff from which partial identities came.</summary>
    public SelectedChangesReviewDiffSnapshot? DiffSnapshot { get; }

}

/// <summary>
/// The immutable, combined HEAD-to-worktree diff used to bind partial review
/// line selections.  Its raw patch stays inside Core so callers cannot rebuild
/// a patch from display text or apply indices to a different diff.
/// </summary>
public sealed class SelectedChangesReviewDiffSnapshot
{
    internal SelectedChangesReviewDiffSnapshot(PartialFileDiff diff, string rawPatch)
    {
        Diff = diff ?? throw new ArgumentNullException(nameof(diff));
        RawPatch = rawPatch ?? throw new ArgumentNullException(nameof(rawPatch));
    }

    public PartialFileDiff Diff { get; }

    public string Fingerprint => Diff.Snapshot.Fingerprint;

    internal string RawPatch { get; }
}

/// <summary>
/// An immutable selected-changes review snapshot and the repository identities
/// that make it safe for a caller to submit to Codex.
/// </summary>
public sealed class SelectedChangesReviewSnapshot
{
    public SelectedChangesReviewSnapshot(
        string rootPath,
        string headId,
        string headTreeId,
        string indexFingerprint,
        string workingTreeFingerprint,
        IReadOnlyList<SelectedChangesReviewFileSelection> selections,
        CodexSelectedChangesReviewSnapshot codexSnapshot)
    {
        RootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        HeadId = headId ?? throw new ArgumentNullException(nameof(headId));
        HeadTreeId = headTreeId ?? throw new ArgumentNullException(nameof(headTreeId));
        IndexFingerprint = indexFingerprint ?? throw new ArgumentNullException(nameof(indexFingerprint));
        WorkingTreeFingerprint = workingTreeFingerprint ?? throw new ArgumentNullException(nameof(workingTreeFingerprint));
        ArgumentNullException.ThrowIfNull(selections);
        CodexSnapshot = codexSnapshot ?? throw new ArgumentNullException(nameof(codexSnapshot));

        Selections = new List<SelectedChangesReviewFileSelection>(selections).AsReadOnly();
    }

    public string RootPath { get; }

    /// <summary>Empty for an unborn repository.</summary>
    public string HeadId { get; }

    /// <summary>The immutable tree used as the review base.</summary>
    public string HeadTreeId { get; }

    /// <summary>SHA-256 of the complete real index stage listing.</summary>
    public string IndexFingerprint { get; }

    /// <summary>SHA-256 of the selected current and old working paths.</summary>
    public string WorkingTreeFingerprint { get; }

    public IReadOnlyList<SelectedChangesReviewFileSelection> Selections { get; }

    public CodexSelectedChangesReviewSnapshot CodexSnapshot { get; }

    public string Diff => CodexSnapshot.Diff;

    public IReadOnlyList<CodexSelectedChangesReviewFile> Files => CodexSnapshot.Files;
}

/// <summary>Base class for safe selected-review collection failures.</summary>
public class SelectedChangesReviewSnapshotException : InvalidOperationException
{
    public SelectedChangesReviewSnapshotException(string message)
        : base(message)
    {
    }
}

/// <summary>Raised when a selected-review capture no longer matches its input state.</summary>
public sealed class SelectedChangesReviewSnapshotStaleException
    : SelectedChangesReviewSnapshotException
{
    public SelectedChangesReviewSnapshotStaleException(string message)
        : base(message)
    {
    }
}

public sealed partial class GitRepositoryService
{
    private const string EmptyTreeObjectId = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private const int MaximumReviewFileBytes = 10 * 1024 * 1024;
    private static readonly Regex ReviewDiffSectionPattern = new(
        "^diff --git ",
        RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Reads the exact combined HEAD-to-worktree text diff that supplies the
    /// hunk and line identities for a partial review selection.  Callers pass
    /// the returned object back to the review capture method unchanged.
    /// </summary>
    public async Task<SelectedChangesReviewDiffSnapshot> GetSelectedChangesReviewDiffAsync(
        string root,
        FileChange file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var normalizedFile = NormalizeSelectedReviewFile(repositoryRoot, file);
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var actual = FindSelectedReviewChange(repositoryRoot, status, normalizedFile);
        if (actual is null || !SelectedReviewFileChangesMatch(repositoryRoot, normalizedFile, actual))
        {
            throw new SelectedChangesReviewSnapshotStaleException(
                $"The selected change for '{normalizedFile.Path}' is stale. Refresh the repository status.");
        }

        if (actual.Kind == ChangeKind.Conflicted)
        {
            throw new SelectedChangesReviewSnapshotException(
                $"Conflicted path '{actual.Path}' cannot be reviewed partially.");
        }

        var headTreeId = await ReadSelectedReviewHeadTreeAsync(
            repositoryRoot,
            status,
            cancellationToken).ConfigureAwait(false);
        await ComputeSelectedReviewWorkingFingerprintAsync(
            repositoryRoot,
            [new SelectedChangesReviewFileSelection(actual, includeWholeFile: true)],
            cancellationToken).ConfigureAwait(false);
        var rawPatch = await ReadSelectedReviewPatchAsync(
            repositoryRoot,
            headTreeId,
            actual,
            cancellationToken).ConfigureAwait(false);
        return CreateSelectedReviewDiffSnapshot(repositoryRoot, headTreeId, actual, rawPatch);
    }

    /// <summary>
    /// Captures caller-selected whole files or partial text changes into an
    /// isolated temporary index.  The repository index and work tree are never
    /// written by this method.
    /// </summary>
    public async Task<SelectedChangesReviewSnapshot> GetSelectedChangesReviewSnapshotAsync(
        string root,
        IReadOnlyList<SelectedChangesReviewFileSelection> selections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var requestedSelections = NormalizeReviewSelections(repositoryRoot, selections);
        var before = await ReadSelectedReviewStateAsync(
            repositoryRoot,
            requestedSelections,
            cancellationToken).ConfigureAwait(false);

        var temporaryIndex = CreateReviewTemporaryIndexPath();
        try
        {
            var environment = CreateReviewIndexEnvironment(temporaryIndex);
            await processRunner.RunAsync(
                repositoryRoot,
                ["read-tree", before.HeadTreeId],
                cancellationToken,
                environmentOverrides: environment).ConfigureAwait(false);

            foreach (var selection in before.Selections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawPatch = selection.IncludeWholeFile
                    ? await ReadSelectedReviewPatchAsync(
                        repositoryRoot,
                        before.HeadTreeId,
                        selection.File,
                        cancellationToken).ConfigureAwait(false)
                    : selection.DiffSnapshot!.RawPatch;
                if (rawPatch.Length == 0)
                {
                    throw new SelectedChangesReviewSnapshotException(
                        $"The selected change for '{selection.File.Path}' is no longer available.");
                }

                var patchToApply = selection.IncludeWholeFile
                    ? EnsureWholeReviewPatchSupported(selection.File.Path, rawPatch)
                    : BuildSelectedReviewPatch(repositoryRoot, selection, rawPatch);
                await ApplySelectedReviewPatchAsync(
                    repositoryRoot,
                    temporaryIndex,
                    patchToApply,
                    cancellationToken).ConfigureAwait(false);
            }

            var codexSnapshot = await ReadSelectedReviewDiffAsync(
                repositoryRoot,
                temporaryIndex,
                before.HeadTreeId,
                before.Selections,
                cancellationToken).ConfigureAwait(false);
            var after = await ReadSelectedReviewStateAsync(
                repositoryRoot,
                before.Selections,
                cancellationToken).ConfigureAwait(false);
            if (!SelectedReviewStatesMatch(before, after))
            {
                throw new SelectedChangesReviewSnapshotStaleException(
                    "The selected changes changed while the review snapshot was being captured. Refresh and try again.");
            }

            return new SelectedChangesReviewSnapshot(
                before.RootPath,
                before.HeadId,
                before.HeadTreeId,
                before.IndexFingerprint,
                before.WorkingTreeFingerprint,
                before.Selections,
                codexSnapshot);
        }
        finally
        {
            DeleteOwnedReviewIndex(temporaryIndex);
        }
    }

    /// <summary>
    /// Re-reads the guarded repository identities without touching Git's real
    /// index or work tree.  False means the caller must discard its review.
    /// </summary>
    public async Task<bool> RevalidateSelectedChangesReviewSnapshotAsync(
        string root,
        SelectedChangesReviewSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var candidate = ValidateDirectory(root, nameof(root));
        var repositoryRoot = await ResolveRepositoryRootAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!PathsEqual(repositoryRoot, snapshot.RootPath))
        {
            return false;
        }

        try
        {
            var current = await ReadSelectedReviewStateAsync(
                repositoryRoot,
                NormalizeReviewSelections(repositoryRoot, snapshot.Selections),
                cancellationToken).ConfigureAwait(false);
            return SelectedReviewStatesMatch(
                CreateSelectedReviewStateFromSnapshot(snapshot),
                current);
        }
        catch (SelectedChangesReviewSnapshotException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<SelectedReviewState> ReadSelectedReviewStateAsync(
        string repositoryRoot,
        IReadOnlyList<SelectedChangesReviewFileSelection> requestedSelections,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var boundSelections = BindReviewSelections(repositoryRoot, status, requestedSelections);
        var headTreeId = await ReadSelectedReviewHeadTreeAsync(
            repositoryRoot,
            status,
            cancellationToken).ConfigureAwait(false);
        await ValidateSelectedReviewDiffSnapshotsAsync(
            repositoryRoot,
            headTreeId,
            boundSelections,
            cancellationToken).ConfigureAwait(false);
        var indexFingerprint = await ReadSelectedReviewIndexFingerprintAsync(
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        var workingTreeFingerprint = await ComputeSelectedReviewWorkingFingerprintAsync(
            repositoryRoot,
            boundSelections,
            cancellationToken).ConfigureAwait(false);
        return new SelectedReviewState(
            repositoryRoot,
            status.HeadId,
            headTreeId,
            indexFingerprint,
            workingTreeFingerprint,
            boundSelections);
    }

    private static IReadOnlyList<SelectedChangesReviewFileSelection> NormalizeReviewSelections(
        string repositoryRoot,
        IReadOnlyList<SelectedChangesReviewFileSelection> selections)
    {
        var normalized = new List<SelectedChangesReviewFileSelection>(selections.Count);
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            ArgumentNullException.ThrowIfNull(selection);
            var path = ValidateGitPath(repositoryRoot, selection.File.Path, nameof(selections));
            var oldPath = selection.File.OldPath is null
                ? null
                : ValidateGitPath(repositoryRoot, selection.File.OldPath, nameof(selections));
            var file = new FileChange(
                path,
                selection.File.Kind,
                selection.File.IndexStatus,
                selection.File.WorkTreeStatus,
                oldPath);
            if (!seen.Add(path))
            {
                throw new ArgumentException(
                    $"The selected path '{path}' appears more than once.",
                    nameof(selections));
            }

            normalized.Add(new SelectedChangesReviewFileSelection(
                file,
                selection.IncludeWholeFile,
                selection.Selection,
                selection.DiffSnapshot));
        }

        return normalized.AsReadOnly();
    }

    private static IReadOnlyList<SelectedChangesReviewFileSelection> BindReviewSelections(
        string repositoryRoot,
        RepositoryStatus status,
        IReadOnlyList<SelectedChangesReviewFileSelection> requestedSelections)
    {
        var bound = new List<SelectedChangesReviewFileSelection>(requestedSelections.Count);
        foreach (var requested in requestedSelections)
        {
            var actual = status.Changes.FirstOrDefault(change =>
                PathsEqual(
                    GetFullPathForGitPath(repositoryRoot, change.Path),
                    GetFullPathForGitPath(repositoryRoot, requested.File.Path)));
            if (actual is null || !SelectedReviewFileChangesMatch(repositoryRoot, requested.File, actual))
            {
                throw new SelectedChangesReviewSnapshotStaleException(
                    $"The selected change for '{requested.File.Path}' is stale. Refresh the repository status.");
            }

            if (actual.Kind == ChangeKind.Conflicted)
            {
                throw new SelectedChangesReviewSnapshotException(
                    $"Conflicted path '{actual.Path}' cannot be included in a selected review.");
            }

            bound.Add(new SelectedChangesReviewFileSelection(
                actual,
                requested.IncludeWholeFile,
                requested.Selection,
                requested.DiffSnapshot));
        }

        return bound.AsReadOnly();
    }

    private static FileChange NormalizeSelectedReviewFile(
        string repositoryRoot,
        FileChange file)
    {
        var path = ValidateGitPath(repositoryRoot, file.Path, nameof(file));
        var oldPath = file.OldPath is null
            ? null
            : ValidateGitPath(repositoryRoot, file.OldPath, nameof(file));
        return new FileChange(path, file.Kind, file.IndexStatus, file.WorkTreeStatus, oldPath);
    }

    private static FileChange? FindSelectedReviewChange(
        string repositoryRoot,
        RepositoryStatus status,
        FileChange requested)
    {
        var requestedPath = GetFullPathForGitPath(repositoryRoot, requested.Path);
        return status.Changes.FirstOrDefault(change =>
            PathsEqual(
                GetFullPathForGitPath(repositoryRoot, change.Path),
                requestedPath));
    }

    private async Task ValidateSelectedReviewDiffSnapshotsAsync(
        string repositoryRoot,
        string headTreeId,
        IReadOnlyList<SelectedChangesReviewFileSelection> selections,
        CancellationToken cancellationToken)
    {
        foreach (var selection in selections)
        {
            if (selection.IncludeWholeFile)
            {
                continue;
            }

            var source = selection.DiffSnapshot;
            if (source is null || !source.Diff.IsSupported)
            {
                throw new SelectedChangesReviewSnapshotException(
                    $"The partial review diff for '{selection.File.Path}' is unavailable.");
            }

            if (!PathsEqual(repositoryRoot, source.Diff.Snapshot.RootPath)
                || source.Diff.Snapshot.Staged
                || !SelectedReviewFileChangesMatch(repositoryRoot, source.Diff.File, selection.File))
            {
                throw new SelectedChangesReviewSnapshotStaleException(
                    $"The partial review selection for '{selection.File.Path}' belongs to a different file or repository state.");
            }

            var currentPatch = await ReadSelectedReviewPatchAsync(
                repositoryRoot,
                headTreeId,
                selection.File,
                cancellationToken).ConfigureAwait(false);
            var currentFingerprint = ComputeSelectedReviewDiffFingerprint(
                repositoryRoot,
                headTreeId,
                selection.File,
                currentPatch);
            if (!string.Equals(
                    source.Fingerprint,
                    currentFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SelectedChangesReviewSnapshotStaleException(
                    $"The partial review selection for '{selection.File.Path}' is stale. Refresh the diff before reviewing.");
            }
        }
    }

    private static SelectedChangesReviewDiffSnapshot CreateSelectedReviewDiffSnapshot(
        string repositoryRoot,
        string headTreeId,
        FileChange file,
        string rawPatch)
    {
        PartialDiffParseResult parsed;
        try
        {
            parsed = ParsePartialPatch(rawPatch);
        }
        catch (InvalidOperationException exception)
        {
            throw new SelectedChangesReviewSnapshotException(
                $"The selected review diff for '{file.Path}' is malformed: {exception.Message}");
        }

        var snapshot = new PartialDiffSnapshot(
            repositoryRoot,
            file.Path,
            file.OldPath,
            staged: false,
            ComputeSelectedReviewDiffFingerprint(repositoryRoot, headTreeId, file, rawPatch));
        var diff = new PartialFileDiff(
            file,
            snapshot,
            parsed.Hunks,
            parsed.IsBinary,
            parsed.IsTruncated,
            parsed.Message);
        return new SelectedChangesReviewDiffSnapshot(diff, rawPatch);
    }

    private async Task<string> ReadSelectedReviewHeadTreeAsync(
        string repositoryRoot,
        RepositoryStatus status,
        CancellationToken cancellationToken)
    {
        if (status.IsUnborn || status.HeadId.Length == 0)
        {
            return EmptyTreeObjectId;
        }

        ValidateCommitId(status.HeadId);
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "--quiet", "--end-of-options", status.HeadId + "^{tree}"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "selected review HEAD tree");
        var treeId = DecodeUtf8(result.StandardOutput, "selected review HEAD tree").Trim();
        if (treeId.Length != 40 || !treeId.All(IsHexCharacter))
        {
            throw new InvalidOperationException("Git returned an invalid selected-review tree ID.");
        }

        return treeId.ToLowerInvariant();
    }

    private async Task<string> ReadSelectedReviewIndexFingerprintAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["ls-files", "--stage", "--full-name", "-z", "--"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "selected review index");
        return ComputeReviewHash(result.StandardOutput);
    }

    private async Task<string> ComputeSelectedReviewWorkingFingerprintAsync(
        string repositoryRoot,
        IReadOnlyList<SelectedChangesReviewFileSelection> selections,
        CancellationToken cancellationToken)
    {
        var paths = selections
            .SelectMany(selection => GetSelectedReviewWorkingPaths(selection.File))
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData([0]);
            var fullPath = GetFullPathForGitPath(repositoryRoot, path);
            if (ContainsReparsePoint(repositoryRoot, fullPath))
            {
                throw new SelectedChangesReviewSnapshotException(
                    $"Reparse-point path '{path}' cannot be captured for review.");
            }

            if (Directory.Exists(fullPath))
            {
                throw new SelectedChangesReviewSnapshotException(
                    $"Directory path '{path}' cannot be captured as a file review.");
            }

            if (!File.Exists(fullPath))
            {
                hash.AppendData([2, 0]);
                continue;
            }

            var file = await ReadBoundedFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (file.Truncated || file.Bytes.Length > MaximumReviewFileBytes)
            {
                throw new SelectedChangesReviewSnapshotException(
                    $"Selected file '{path}' exceeds the 10 MiB review limit.");
            }

            hash.AppendData([1, 0]);
            hash.AppendData(file.Bytes);
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<string> ReadSelectedReviewPatchAsync(
        string repositoryRoot,
        string headTreeId,
        FileChange file,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "diff",
            "--no-ext-diff",
            "--no-textconv",
            "--no-color",
            "--full-index",
            "--patch",
            "--unified=3",
        };
        IReadOnlyCollection<int>? expectedExitCodes = null;
        if (IsUntracked(file) || file.Kind == ChangeKind.Copied)
        {
            arguments.Add("--no-index");
            arguments.Add("--");
            arguments.Add("/dev/null");
            arguments.Add(file.Path);
            expectedExitCodes = [1];
        }
        else
        {
            arguments.Add("--find-renames");
            arguments.Add("--find-copies");
            arguments.Add(headTreeId);
            arguments.Add("--");
            arguments.Add(ToLiteralPathSpec(file.Path));
            AddOldPath(arguments, repositoryRoot, file.OldPath, file.Path);
        }

        var result = await processRunner.RunAsync(
            repositoryRoot,
            arguments,
            cancellationToken,
            expectedExitCodes).ConfigureAwait(false);
        EnsureComplete(result, "selected review diff");
        if (result.StandardOutput.Length > MaximumReviewFileBytes)
        {
            throw new SelectedChangesReviewSnapshotException(
                $"The selected review diff for '{file.Path}' exceeds the 10 MiB limit.");
        }

        return DecodeUtf8(result.StandardOutput, "selected review diff");
    }

    private static string EnsureWholeReviewPatchSupported(string path, string patch)
    {
        var parsed = ParseDiffForCodexReview(patch);
        if (parsed.IsBinary)
        {
            throw new SelectedChangesReviewSnapshotException(
                $"Binary file '{path}' cannot be included in a selected review.");
        }

        if (parsed.IsTruncated)
        {
            throw new SelectedChangesReviewSnapshotException(
                $"The selected review diff for '{path}' is too large to render.");
        }

        return patch;
    }

    private static string BuildSelectedReviewPatch(
        string repositoryRoot,
        SelectedChangesReviewFileSelection selection,
        string rawPatch)
    {
        PartialDiffParseResult parsed;
        try
        {
            parsed = ParsePartialPatch(rawPatch);
        }
        catch (InvalidOperationException exception)
        {
            throw new SelectedChangesReviewSnapshotException(
                $"The selected partial review for '{selection.File.Path}' is malformed: {exception.Message}");
        }

        var partialSnapshot = new PartialDiffSnapshot(
            repositoryRoot,
            selection.File.Path,
            selection.File.OldPath,
            staged: false,
            ComputeReviewHash(Encoding.UTF8.GetBytes(rawPatch)));
        var diff = new PartialFileDiff(
            selection.File,
            partialSnapshot,
            parsed.Hunks,
            parsed.IsBinary,
            parsed.IsTruncated,
            parsed.Message);
        try
        {
            EnsurePartialDiffSupported(diff);
            var validSelection = ValidateSelection(diff, selection.Selection);
            return BuildSelectedPatch(
                new PartialDiffReadResult(diff, parsed.RawPatch, parsed.HeaderLines),
                validSelection,
                reverse: false);
        }
        catch (PartialStagingUnsupportedException exception)
        {
            throw new SelectedChangesReviewSnapshotException(
                $"The selected partial review for '{selection.File.Path}' is unavailable: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            throw new SelectedChangesReviewSnapshotException(
                $"The selected partial review for '{selection.File.Path}' is stale or invalid: {exception.Message}");
        }
    }

    private async Task ApplySelectedReviewPatchAsync(
        string repositoryRoot,
        string temporaryIndex,
        string patch,
        CancellationToken cancellationToken)
    {
        if (patch.Length == 0)
        {
            throw new SelectedChangesReviewSnapshotException("The selected review patch is empty.");
        }

        await processRunner.RunAsync(
            repositoryRoot,
            ["apply", "--cached", "--unidiff-zero", "--whitespace=nowarn", "-"],
            cancellationToken,
            standardInput: patch,
            environmentOverrides: CreateReviewIndexEnvironment(temporaryIndex)).ConfigureAwait(false);
    }

    private async Task<CodexSelectedChangesReviewSnapshot> ReadSelectedReviewDiffAsync(
        string repositoryRoot,
        string temporaryIndex,
        string headTreeId,
        IReadOnlyList<SelectedChangesReviewFileSelection> selections,
        CancellationToken cancellationToken)
    {
        if (selections.Count == 0)
        {
            return new CodexSelectedChangesReviewSnapshot(string.Empty, []);
        }

        var pathSpecs = selections
            .SelectMany(selection => GetSelectedReviewDiffPaths(selection.File))
            .Where(path => path is not null)
            .Select(path => ToLiteralPathSpec(path!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var diffArguments = new List<string>
        {
            "diff",
            "--cached",
            "--no-ext-diff",
            "--no-textconv",
            "--no-color",
            "--full-index",
            "--patch",
            "--unified=3",
            "--find-renames",
            "--find-copies",
            headTreeId,
            "--",
        };
        diffArguments.AddRange(pathSpecs);
        var diffResult = await processRunner.RunAsync(
            repositoryRoot,
            diffArguments,
            cancellationToken,
            environmentOverrides: CreateReviewIndexEnvironment(temporaryIndex)).ConfigureAwait(false);
        EnsureComplete(diffResult, "selected review diff");
        if (diffResult.StandardOutput.Length > MaximumReviewFileBytes)
        {
            throw new SelectedChangesReviewSnapshotException(
                "The selected review diff exceeds the 10 MiB limit.");
        }

        var combinedDiff = DecodeUtf8(diffResult.StandardOutput, "selected review diff");
        var sections = SplitReviewDiffSections(combinedDiff);
        var records = await ReadSelectedReviewNameRecordsAsync(
            repositoryRoot,
            temporaryIndex,
            headTreeId,
            pathSpecs,
            cancellationToken).ConfigureAwait(false);
        if (sections.Count != records.Count)
        {
            throw new SelectedChangesReviewSnapshotException(
                "Git returned an inconsistent selected-review file list.");
        }

        var owners = CreateReviewPathOwners(selections);
        var files = new List<CodexSelectedChangesReviewFile>(sections.Count);
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            var parsed = ParseDiffForCodexReview(section);
            if (parsed.IsBinary)
            {
                throw new SelectedChangesReviewSnapshotException(
                    $"Binary file in selected review '{records[index].Path}' cannot be sent to Codex.");
            }

            if (parsed.IsTruncated)
            {
                throw new SelectedChangesReviewSnapshotException(
                    $"Selected review file '{records[index].Path}' exceeds the native rendering limit.");
            }

            var record = records[index];
            var recordPath = record.Path;
            var recordOldPath = record.OldPath;
            if (!owners.TryGetValue(recordPath, out var owner)
                && (recordOldPath is null
                    || !owners.TryGetValue(recordOldPath, out owner)))
            {
                throw new SelectedChangesReviewSnapshotException(
                    $"Git returned an unselected path '{recordPath}'.");
            }

            files.Add(new CodexSelectedChangesReviewFile(owner.File.Path, section));
        }

        return new CodexSelectedChangesReviewSnapshot(combinedDiff, files);
    }

    private async Task<IReadOnlyList<ReviewNameRecord>> ReadSelectedReviewNameRecordsAsync(
        string repositoryRoot,
        string temporaryIndex,
        string headTreeId,
        IReadOnlyList<string> pathSpecs,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "diff",
            "--cached",
            "--no-ext-diff",
            "--no-textconv",
            "--no-color",
            "--name-status",
            "-z",
            "--find-renames",
            "--find-copies",
            headTreeId,
            "--",
        };
        arguments.AddRange(pathSpecs);
        var result = await processRunner.RunAsync(
            repositoryRoot,
            arguments,
            cancellationToken,
            environmentOverrides: CreateReviewIndexEnvironment(temporaryIndex)).ConfigureAwait(false);
        EnsureComplete(result, "selected review file list");
        var text = DecodeUtf8(result.StandardOutput, "selected review file list");
        var fields = text.Split('\0', StringSplitOptions.None);
        var records = new List<ReviewNameRecord>();
        for (var index = 0; index < fields.Length && fields[index].Length > 0;)
        {
            var status = fields[index++];
            if (status.Length == 0)
            {
                continue;
            }

            if (index >= fields.Length || fields[index].Length == 0)
            {
                throw new SelectedChangesReviewSnapshotException(
                    "Git returned an incomplete selected-review file list.");
            }

            var firstPath = ValidateGitPath(repositoryRoot, fields[index++], "selected review path");
            if (status[0] is 'R' or 'C')
            {
                if (index >= fields.Length || fields[index].Length == 0)
                {
                    throw new SelectedChangesReviewSnapshotException(
                        "Git returned an incomplete selected-review rename record.");
                }

                var newPath = ValidateGitPath(repositoryRoot, fields[index++], "selected review path");
                records.Add(new ReviewNameRecord(status, newPath, firstPath));
            }
            else
            {
                records.Add(new ReviewNameRecord(status, firstPath, null));
            }
        }

        return records.AsReadOnly();
    }

    private static Dictionary<string, SelectedChangesReviewFileSelection> CreateReviewPathOwners(
        IReadOnlyList<SelectedChangesReviewFileSelection> selections)
    {
        var owners = new Dictionary<string, SelectedChangesReviewFileSelection>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            if (!owners.TryAdd(selection.File.Path, selection)
                || (selection.File.Kind != ChangeKind.Copied
                    && selection.File.OldPath is not null
                    && !owners.TryAdd(selection.File.OldPath, selection)))
            {
                throw new SelectedChangesReviewSnapshotException(
                    $"The selected review paths for '{selection.File.Path}' overlap.");
            }
        }

        return owners;
    }

    private static IEnumerable<string?> GetSelectedReviewWorkingPaths(FileChange file)
    {
        yield return file.Path;
        if (file.Kind != ChangeKind.Copied)
        {
            yield return file.OldPath;
        }
    }

    private static IEnumerable<string?> GetSelectedReviewDiffPaths(FileChange file)
    {
        yield return file.Path;
        if (file.Kind != ChangeKind.Copied)
        {
            yield return file.OldPath;
        }
    }

    private static IReadOnlyList<string> SplitReviewDiffSections(string combinedDiff)
    {
        if (combinedDiff.Length == 0)
        {
            return [];
        }

        var matches = ReviewDiffSectionPattern.Matches(combinedDiff);
        if (matches.Count == 0 || matches[0].Index != 0)
        {
            throw new SelectedChangesReviewSnapshotException(
                "Git returned a selected-review patch without a valid file header.");
        }

        var sections = new List<string>(matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            var start = matches[index].Index;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : combinedDiff.Length;
            sections.Add(combinedDiff[start..end]);
        }

        return sections.AsReadOnly();
    }

    private static bool SelectedReviewStatesMatch(SelectedReviewState left, SelectedReviewState right) =>
        PathsEqual(left.RootPath, right.RootPath)
        && string.Equals(left.HeadId, right.HeadId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.HeadTreeId, right.HeadTreeId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.IndexFingerprint, right.IndexFingerprint, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.WorkingTreeFingerprint, right.WorkingTreeFingerprint, StringComparison.OrdinalIgnoreCase)
        && SelectedReviewSelectionsMatch(left.Selections, right.Selections);

    private static bool SelectedReviewSelectionsMatch(
        IReadOnlyList<SelectedChangesReviewFileSelection> left,
        IReadOnlyList<SelectedChangesReviewFileSelection> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var leftSelection = left[index];
            var rightSelection = right[index];
            if (!string.Equals(leftSelection.File.Path, rightSelection.File.Path, StringComparison.Ordinal)
                || !string.Equals(leftSelection.File.OldPath, rightSelection.File.OldPath, StringComparison.Ordinal)
                || leftSelection.File.Kind != rightSelection.File.Kind
                || !string.Equals(leftSelection.File.IndexStatus, rightSelection.File.IndexStatus, StringComparison.Ordinal)
                || !string.Equals(leftSelection.File.WorkTreeStatus, rightSelection.File.WorkTreeStatus, StringComparison.Ordinal)
                || leftSelection.IncludeWholeFile != rightSelection.IncludeWholeFile
                || !leftSelection.Selection.SequenceEqual(rightSelection.Selection))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SelectedReviewFileChangesMatch(
        string repositoryRoot,
        FileChange expected,
        FileChange actual) =>
        PathsEqual(
            GetFullPathForGitPath(repositoryRoot, expected.Path),
            GetFullPathForGitPath(repositoryRoot, actual.Path))
        && SelectedReviewOptionalPathMatches(repositoryRoot, expected.OldPath, actual.OldPath)
        && expected.Kind == actual.Kind
        && string.Equals(expected.IndexStatus, actual.IndexStatus, StringComparison.Ordinal)
        && string.Equals(expected.WorkTreeStatus, actual.WorkTreeStatus, StringComparison.Ordinal);

    private static bool SelectedReviewOptionalPathMatches(
        string repositoryRoot,
        string? expected,
        string? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        return PathsEqual(
            GetFullPathForGitPath(repositoryRoot, expected),
            GetFullPathForGitPath(repositoryRoot, actual));
    }

    private static SelectedReviewState CreateSelectedReviewStateFromSnapshot(
        SelectedChangesReviewSnapshot snapshot) =>
        new(
            snapshot.RootPath,
            snapshot.HeadId,
            snapshot.HeadTreeId,
            snapshot.IndexFingerprint,
            snapshot.WorkingTreeFingerprint,
            snapshot.Selections);

    private static Dictionary<string, string?> CreateReviewIndexEnvironment(string temporaryIndex) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_INDEX_FILE"] = temporaryIndex,
        };

    private static string CreateReviewTemporaryIndexPath() =>
        Path.Combine(Path.GetTempPath(), $"wingit-selected-review-index-{Guid.NewGuid():N}");

    private static void DeleteOwnedReviewIndex(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var relativePath = Path.GetRelativePath(tempRoot, fullPath);
        if (Path.IsPathRooted(relativePath)
            || string.Equals(relativePath, ".", comparison)
            || string.Equals(relativePath, "..", comparison)
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, comparison)
            || !Path.GetFileName(fullPath).StartsWith("wingit-selected-review-index-", comparison))
        {
            return;
        }

        DeleteReviewTempFile(fullPath);
        DeleteReviewTempFile(fullPath + ".lock");
    }

    private static void DeleteReviewTempFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
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

    private static string ComputeReviewHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ComputeSelectedReviewDiffFingerprint(
        string repositoryRoot,
        string headTreeId,
        FileChange file,
        string rawPatch)
    {
        var identity = string.Join(
            '\0',
            repositoryRoot,
            headTreeId,
            file.Path,
            file.OldPath ?? string.Empty,
            file.Kind.ToString(),
            file.IndexStatus,
            file.WorkTreeStatus);
        var identityBytes = Encoding.UTF8.GetBytes(identity);
        var patchBytes = Encoding.UTF8.GetBytes(rawPatch);
        var input = new byte[identityBytes.Length + 1 + patchBytes.Length];
        identityBytes.CopyTo(input, 0);
        input[identityBytes.Length] = 0;
        patchBytes.CopyTo(input, identityBytes.Length + 1);
        return ComputeReviewHash(input);
    }

    private static bool IsHexCharacter(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';

    private sealed record SelectedReviewState(
        string RootPath,
        string HeadId,
        string HeadTreeId,
        string IndexFingerprint,
        string WorkingTreeFingerprint,
        IReadOnlyList<SelectedChangesReviewFileSelection> Selections);

    private sealed record ReviewNameRecord(string Status, string Path, string? OldPath);
}
