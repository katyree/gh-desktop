using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private static readonly Regex PartialHunkHeaderPattern = new(
        "^@@ -(?<oldStart>\\d+)(?:,(?<oldCount>\\d+))? \\+(?<newStart>\\d+)(?:,(?<newCount>\\d+))? @@(?<heading>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Reads the exact text patch and typed line identities used by a partial index update.</summary>
    public async Task<PartialFileDiff> GetPartialDiffAsync(
        string root,
        FileChange file,
        bool staged,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var normalizedFile = NormalizePartialFile(repositoryRoot, file);
        var result = await ReadPartialDiffAsync(
            repositoryRoot,
            normalizedFile,
            staged,
            cancellationToken).ConfigureAwait(false);
        return result.Diff;
    }

    /// <summary>Stages only the selected hunks or changed lines from an unstaged text diff.</summary>
    public async Task StageSelectedChangesAsync(
        string root,
        PartialFileDiff diff,
        IReadOnlyCollection<PartialDiffSelection> selection,
        CancellationToken cancellationToken)
    {
        await ApplySelectedChangesAsync(
            root,
            diff,
            selection,
            staged: false,
            reverse: false,
            applyToIndex: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Unstages only the selected hunks or changed lines from a staged text diff.</summary>
    public async Task UnstageSelectedChangesAsync(
        string root,
        PartialFileDiff diff,
        IReadOnlyCollection<PartialDiffSelection> selection,
        CancellationToken cancellationToken)
    {
        await ApplySelectedChangesAsync(
            root,
            diff,
            selection,
            staged: true,
            reverse: true,
            applyToIndex: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplySelectedChangesAsync(
        string root,
        PartialFileDiff diff,
        IReadOnlyCollection<PartialDiffSelection> selection,
        bool staged,
        bool reverse,
        bool applyToIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Count == 0)
        {
            throw new ArgumentException("At least one hunk or changed line must be selected.", nameof(selection));
        }

        var repositoryRoot = ValidateDirectory(root, nameof(root));
        var normalizedFile = NormalizePartialFile(repositoryRoot, diff.File);
        EnsureSnapshotRequestMatches(repositoryRoot, normalizedFile, staged, diff.Snapshot);

        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                // Re-read while holding the same gate as every other index mutation.
                // This closes the gap between the UI's diff read and its apply.
                var current = await ReadPartialDiffAsync(
                    path,
                    normalizedFile,
                    staged,
                    cancellationToken).ConfigureAwait(false);

                EnsureSnapshotMatches(diff.Snapshot, current.Diff.Snapshot);
                EnsurePartialDiffSupported(current.Diff);
                var normalizedSelection = ValidateSelection(current.Diff, selection);
                var patch = BuildSelectedPatch(current, normalizedSelection, reverse);

                var arguments = new List<string>
                {
                    "apply",
                    "--unidiff-zero",
                    "--whitespace=nowarn",
                };
                if (applyToIndex)
                {
                    arguments.Insert(1, "--cached");
                }
                if (reverse)
                {
                    arguments.Add("--reverse");
                }

                arguments.Add("-");
                await processRunner.RunAsync(
                    path,
                    arguments,
                    cancellationToken,
                    standardInput: patch).ConfigureAwait(false);

                // A reverse patch can empty an index entry that was created by
                // a partial stage.  Git keeps that empty blob as a tracked
                // entry, so explicitly reset only this newly-created path to
                // return it to the untracked state while preserving its
                // work-tree contents.  This runs inside the same mutation gate
                // and only when every staged addition was selected.
                if (applyToIndex
                    && reverse
                    && IsCreatedFilePatch(current)
                    && SelectionIncludesEveryChange(current.Diff, normalizedSelection))
                {
                    await processRunner.RunAsync(
                        path,
                        ["reset", "--", ToLiteralPathSpec(current.Diff.File.Path)],
                        cancellationToken).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }

    private async Task<PartialDiffReadResult> ReadPartialDiffAsync(
        string repositoryRoot,
        FileChange file,
        bool staged,
        CancellationToken cancellationToken)
    {
        var isUntracked = !staged && IsUntracked(file);
        if (isUntracked)
        {
            var unsupportedReason = GetUntrackedPartialUnsupportedReason(repositoryRoot, file.Path);
            if (unsupportedReason is not null)
            {
                var unsupportedSnapshot = new PartialDiffSnapshot(
                    repositoryRoot,
                    file.Path,
                    file.OldPath,
                    staged,
                    ComputePartialFingerprint(repositoryRoot, file, staged, []));
                return new PartialDiffReadResult(
                    new PartialFileDiff(
                        file,
                        unsupportedSnapshot,
                        [],
                        isBinary: false,
                        isTruncated: false,
                        unsupportedReason),
                    string.Empty,
                    []);
            }
        }

        var arguments = new List<string>
        {
            "diff",
            "--no-ext-diff",
            "--no-textconv",
            "--no-color",
            "--binary",
            "--full-index",
            "--patch",
            "--unified=3",
        };
        IReadOnlyCollection<int>? expectedExitCodes = null;
        if (isUntracked)
        {
            arguments.Add("--no-index");
            arguments.Add("--");
            arguments.Add("/dev/null");
            // --no-index compares two paths rather than pathspecs. The process
            // boundary does not perform wildcard expansion, and -- already
            // protects names that begin with a dash.
            arguments.Add(file.Path);
            expectedExitCodes = [1];
        }
        else
        {
            arguments.Add("--find-renames");
            if (staged)
            {
                arguments.Add("--cached");
            }

            arguments.Add("--");
            arguments.Add(ToLiteralPathSpec(file.Path));
            AddOldPath(arguments, repositoryRoot, file.OldPath, file.Path);
        }

        var result = await processRunner.RunAsync(
            repositoryRoot,
            arguments,
            cancellationToken,
            expectedExitCodes).ConfigureAwait(false);

        var fingerprint = ComputePartialFingerprint(repositoryRoot, file, staged, result.StandardOutput);
        var snapshot = new PartialDiffSnapshot(
            repositoryRoot,
            file.Path,
            file.OldPath,
            staged,
            fingerprint);

        if (result.StandardOutputTruncated)
        {
            return new PartialDiffReadResult(
                new PartialFileDiff(
                    file,
                    snapshot,
                    [],
                    isBinary: false,
                    isTruncated: true,
                    "The diff exceeds the native partial-staging limit and was not rendered."),
                string.Empty,
                []);
        }

        if (result.StandardOutput.Length == 0)
        {
            return new PartialDiffReadResult(
                new PartialFileDiff(
                    file,
                    snapshot,
                    [],
                    isBinary: false,
                    isTruncated: false,
                    "No textual changes are available for partial staging."),
                string.Empty,
                []);
        }

        if (result.StandardOutput.AsSpan().IndexOf((byte)0) >= 0)
        {
            return new PartialDiffReadResult(
                new PartialFileDiff(
                    file,
                    snapshot,
                    [],
                    isBinary: true,
                    isTruncated: false,
                    "Binary file cannot be partially staged."),
                string.Empty,
                []);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(result.StandardOutput);
        }
        catch (DecoderFallbackException)
        {
            return new PartialDiffReadResult(
                new PartialFileDiff(
                    file,
                    snapshot,
                    [],
                    isBinary: true,
                    isTruncated: false,
                    "Binary file cannot be partially staged."),
                string.Empty,
                []);
        }

        var submoduleComparison = TryParseSubmoduleComparison(text)
            ?? await TryReadDirtySubmoduleComparisonAsync(
                repositoryRoot,
                file,
                staged ? ImageDiffScope.Index : ImageDiffScope.Unstaged,
                text,
                cancellationToken).ConfigureAwait(false);
        if (submoduleComparison is not null)
        {
            return new PartialDiffReadResult(
                new PartialFileDiff(
                    file,
                    snapshot,
                    [],
                    isBinary: false,
                    isTruncated: false,
                    "Submodule changes cannot be partially staged."),
                text,
                []);
        }

        var parsed = ParsePartialPatch(text);
        var partialDiff = new PartialFileDiff(
            file,
            snapshot,
            parsed.Hunks,
            parsed.IsBinary,
            parsed.IsTruncated,
            parsed.Message);
        return new PartialDiffReadResult(partialDiff, parsed.RawPatch, parsed.HeaderLines);
    }

    private static FileChange NormalizePartialFile(string repositoryRoot, FileChange file)
    {
        var path = ValidateGitPath(repositoryRoot, file.Path, nameof(file));
        var oldPath = file.OldPath is null
            ? null
            : ValidateGitPath(repositoryRoot, file.OldPath, nameof(file));
        return new FileChange(path, file.Kind, file.IndexStatus, file.WorkTreeStatus, oldPath);
    }

    private static string? GetUntrackedPartialUnsupportedReason(
        string repositoryRoot,
        string path)
    {
        var fullPath = GetFullPathForGitPath(repositoryRoot, path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return "the file is no longer present";
        }

        try
        {
            if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
            {
                return "symbolic-link and reparse-point files are not supported";
            }
        }
        catch (IOException)
        {
            return "the file could not be inspected";
        }
        catch (UnauthorizedAccessException)
        {
            return "the file could not be inspected";
        }

        if (!File.Exists(fullPath))
        {
            return "directories cannot be partially staged as files";
        }

        return null;
    }

    private static void EnsureSnapshotRequestMatches(
        string repositoryRoot,
        FileChange file,
        bool staged,
        PartialDiffSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!PathsEqual(repositoryRoot, snapshot.RootPath)
            || !string.Equals(file.Path, snapshot.Path, StringComparison.Ordinal)
            || !string.Equals(file.OldPath, snapshot.OldPath, StringComparison.Ordinal)
            || snapshot.Staged != staged)
        {
            throw new StaleDiffSnapshotException(file.Path);
        }
    }

    private static void EnsureSnapshotMatches(
        PartialDiffSnapshot expected,
        PartialDiffSnapshot actual)
    {
        if (!PathsEqual(expected.RootPath, actual.RootPath)
            || !string.Equals(expected.Path, actual.Path, StringComparison.Ordinal)
            || !string.Equals(expected.OldPath, actual.OldPath, StringComparison.Ordinal)
            || expected.Staged != actual.Staged
            || !string.Equals(expected.Fingerprint, actual.Fingerprint, StringComparison.Ordinal))
        {
            throw new StaleDiffSnapshotException(expected.Path);
        }
    }

    private static void EnsurePartialDiffSupported(PartialFileDiff diff)
    {
        if (diff.IsSupported)
        {
            return;
        }

        throw new PartialStagingUnsupportedException(
            diff.File.Path,
            diff.Message
                ?? (diff.IsBinary
                    ? "binary files are not supported"
                    : diff.IsTruncated
                        ? "the diff is too large"
                        : "the diff does not contain selectable text changes"));
    }

    private static PartialSelectionSet ValidateSelection(
        PartialFileDiff diff,
        IReadOnlyCollection<PartialDiffSelection> selection)
    {
        var hunkIds = new HashSet<int>();
        var lineKeys = new HashSet<PartialSelectionKey>();
        var hunksById = diff.Hunks.ToDictionary(hunk => hunk.Id);

        foreach (var item in selection)
        {
            if (!hunksById.TryGetValue(item.HunkId, out var hunk))
            {
                throw new ArgumentException(
                    $"The selected hunk {item.HunkId} is not present in the diff.",
                    nameof(selection));
            }

            if (item.LineIndex is null)
            {
                if (!hunk.Lines.Any(line => line.IsSelectable))
                {
                    throw new ArgumentException(
                        $"The selected hunk {item.HunkId} has no changed lines.",
                        nameof(selection));
                }

                hunkIds.Add(item.HunkId);
                continue;
            }

            var lineIndex = item.LineIndex.Value;
            if (lineIndex < 0 || lineIndex >= hunk.Lines.Count)
            {
                throw new ArgumentException(
                    $"The selected line {lineIndex} is not present in hunk {item.HunkId}.",
                    nameof(selection));
            }

            var line = hunk.Lines[lineIndex];
            if (!line.IsSelectable)
            {
                throw new ArgumentException(
                    $"Only changed lines can be selected; hunk {item.HunkId}, line {lineIndex} is {line.Kind}.",
                    nameof(selection));
            }

            lineKeys.Add(new PartialSelectionKey(item.HunkId, lineIndex));
        }

        if (hunkIds.Count == 0 && lineKeys.Count == 0)
        {
            throw new ArgumentException("At least one changed line must be selected.", nameof(selection));
        }

        return new PartialSelectionSet(hunkIds, lineKeys);
    }

    private static string BuildSelectedPatch(
        PartialDiffReadResult current,
        PartialSelectionSet selection,
        bool reverse)
    {
        var builder = new StringBuilder();
        var headerLines = GetPatchHeaderLines(current, selection, reverse);
        foreach (var headerLine in headerLines)
        {
            builder.Append(headerLine).Append('\n');
        }

        var wroteHunk = false;
        foreach (var hunk in current.Diff.Hunks)
        {
            var selectedHunk = selection.HunkIds.Contains(hunk.Id);
            var selectedLines = new List<PartialPatchLine>();
            var oldCount = 0;
            var newCount = 0;
            var hasSelectedChange = false;
            var previousSourceLineWasEmitted = false;

            foreach (var line in hunk.Lines)
            {
                var selected = selectedHunk
                    || selection.LineKeys.Contains(new PartialSelectionKey(hunk.Id, line.LineIndex));
                switch (line.Kind)
                {
                    case DiffLineKind.Context:
                        selectedLines.Add(new PartialPatchLine(' ', line.PatchText));
                        oldCount++;
                        newCount++;
                        previousSourceLineWasEmitted = true;
                        break;

                    case DiffLineKind.Added:
                        if (selected)
                        {
                            selectedLines.Add(new PartialPatchLine('+', line.PatchText));
                            newCount++;
                            hasSelectedChange = true;
                            previousSourceLineWasEmitted = true;
                        }
                        else if (reverse)
                        {
                            // During a reverse apply, an unselected addition is
                            // already present in the current index and must remain
                            // as context so it is not accidentally removed.
                            selectedLines.Add(new PartialPatchLine(' ', line.PatchText));
                            oldCount++;
                            newCount++;
                            previousSourceLineWasEmitted = true;
                        }
                        else
                        {
                            previousSourceLineWasEmitted = false;
                        }
                        break;

                    case DiffLineKind.Removed:
                        if (selected)
                        {
                            selectedLines.Add(new PartialPatchLine('-', line.PatchText));
                            oldCount++;
                            hasSelectedChange = true;
                            previousSourceLineWasEmitted = true;
                        }
                        else if (!reverse)
                        {
                            // A deletion omitted from a forward stage patch remains
                            // deleted in the target side, so represent it as context
                            // against the current work tree.
                            selectedLines.Add(new PartialPatchLine(' ', line.PatchText));
                            oldCount++;
                            newCount++;
                            previousSourceLineWasEmitted = true;
                        }
                        else
                        {
                            previousSourceLineWasEmitted = false;
                        }
                        break;

                    case DiffLineKind.NoNewline:
                        if (previousSourceLineWasEmitted)
                        {
                            var markerText = line.Text.StartsWith('\\')
                                ? line.Text[1..]
                                : line.Text;
                            selectedLines.Add(new PartialPatchLine('\\', markerText));
                        }

                        previousSourceLineWasEmitted = false;
                        break;

                    default:
                        throw new PartialStagingUnsupportedException(
                            current.Diff.File.Path,
                            "the diff contains a line form that cannot be safely patched");
                }
            }

            if (!hasSelectedChange)
            {
                continue;
            }

            builder.Append(FormatPartialHunkHeader(hunk, oldCount, newCount));
            foreach (var line in selectedLines)
            {
                builder.Append(line.Prefix).Append(line.Content).Append('\n');
            }

            wroteHunk = true;
        }

        if (!wroteHunk)
        {
            throw new ArgumentException("The selection does not contain a changed line.", nameof(selection));
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> GetPatchHeaderLines(
        PartialDiffReadResult current,
        PartialSelectionSet selection,
        bool reverse)
    {
        var hasNewFileMode = current.HeaderLines.Any(line =>
            line.StartsWith("new file mode ", StringComparison.Ordinal));
        var hasDeletedFileMode = current.HeaderLines.Any(line =>
            line.StartsWith("deleted file mode ", StringComparison.Ordinal));

        if (reverse && hasNewFileMode)
        {
            return NormalizeSamePathHeaderLines(current.HeaderLines, nullOldPath: true);
        }

        if (!reverse
            && hasDeletedFileMode
            && !SelectionIncludesEveryChange(current.Diff, selection))
        {
            return NormalizeSamePathHeaderLines(current.HeaderLines, nullOldPath: false);
        }

        return current.HeaderLines;
    }

    private static IReadOnlyList<string> NormalizeSamePathHeaderLines(
        IReadOnlyList<string> headerLines,
        bool nullOldPath)
    {
        // Git's creation/deletion mode metadata requires a complete file
        // transition.  Partial selection turns it into an ordinary same-path
        // patch so the selected hunk can leave the index partially populated.
        // Keep the path spelling Git emitted, including quoting for unusual
        // names, while removing only the mode/index metadata.
        var oldPathLine = headerLines.FirstOrDefault(line =>
            line.StartsWith("--- ", StringComparison.Ordinal));
        var newPathLine = headerLines.FirstOrDefault(line =>
            line.StartsWith("+++ ", StringComparison.Ordinal));
        var normalized = new List<string>(headerLines.Count);
        foreach (var line in headerLines)
        {
            if (line.StartsWith("new file mode ", StringComparison.Ordinal)
                || line.StartsWith("deleted file mode ", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal))
            {
                continue;
            }

            if (nullOldPath
                && line.StartsWith("--- /dev/null", StringComparison.Ordinal)
                && newPathLine is not null)
            {
                normalized.Add("--- " + newPathLine[4..]);
                continue;
            }

            if (!nullOldPath
                && line.StartsWith("+++ /dev/null", StringComparison.Ordinal)
                && oldPathLine is not null)
            {
                normalized.Add("+++ " + oldPathLine[4..]);
                continue;
            }

            normalized.Add(line);
        }

        return normalized;
    }

    private static bool IsCreatedFilePatch(PartialDiffReadResult current) =>
        current.Diff.File.OldPath is null
        && current.HeaderLines.Any(line =>
            line.StartsWith("new file mode ", StringComparison.Ordinal));

    private static bool SelectionIncludesEveryChange(
        PartialFileDiff diff,
        PartialSelectionSet selection)
    {
        return diff.Hunks
            .SelectMany(hunk => hunk.Lines.Select(line => (HunkId: hunk.Id, Line: line)))
            .Where(item => item.Line.IsSelectable)
            .All(item =>
                selection.HunkIds.Contains(item.HunkId)
                || selection.LineKeys.Contains(new PartialSelectionKey(item.HunkId, item.Line.LineIndex)));
    }

    private static string FormatPartialHunkHeader(
        PartialDiffHunk hunk,
        int oldCount,
        int newCount)
    {
        var oldRange = FormatPartialRange(hunk.OldStartLine, oldCount);
        var newRange = FormatPartialRange(hunk.NewStartLine, newCount);
        var marker = hunk.Header.IndexOf("@@", 2, StringComparison.Ordinal);
        var heading = marker >= 0
            ? hunk.Header[(marker + 2)..]
            : string.Empty;
        return $"@@ -{oldRange} +{newRange} @@{heading}\n";
    }

    private static string FormatPartialRange(int start, int count) =>
        count == 1 ? start.ToString() : $"{start},{count}";

    private static PartialDiffParseResult ParsePartialPatch(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.None).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var headerLines = new List<string>();
        var hunks = new List<PartialDiffHunk>();
        List<PartialDiffLine>? currentLines = null;
        var currentHunkId = -1;
        var currentOldStart = 0;
        var currentOldCount = 0;
        var currentNewStart = 0;
        var currentNewCount = 0;
        var currentHeader = string.Empty;
        var oldLineNumber = 0;
        var newLineNumber = 0;
        var sawDiffHeader = false;
        var sawBinaryHeader = false;
        var renderedLineCount = 0;

        foreach (var rawLine in lines)
        {
            renderedLineCount++;
            if (renderedLineCount > MaximumRenderedLines || rawLine.Length > MaximumRenderedLineLength + 1)
            {
                return new PartialDiffParseResult(
                    text,
                    headerLines,
                    hunks,
                    IsBinary: false,
                    IsTruncated: true,
                    "The diff contains too many or too-long lines for partial staging.");
            }

            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FinishPartialHunk(
                    hunks,
                    ref currentLines,
                    ref currentHunkId,
                    ref currentHeader,
                    ref currentOldStart,
                    ref currentOldCount,
                    ref currentNewStart,
                    ref currentNewCount);
                if (sawDiffHeader)
                {
                    return new PartialDiffParseResult(
                        text,
                        headerLines,
                        hunks,
                        IsBinary: false,
                        IsTruncated: false,
                        "The selected path produced more than one diff section.");
                }

                sawDiffHeader = true;
                headerLines.Add(line);
                continue;
            }

            if (currentLines is null)
            {
                if (line.StartsWith("Binary files ", StringComparison.Ordinal)
                    || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                {
                    sawBinaryHeader = true;
                }

                var hunkMatch = PartialHunkHeaderPattern.Match(line);
                if (hunkMatch.Success)
                {
                    currentHunkId = hunks.Count;
                    currentHeader = line;
                    currentOldStart = ParsePartialNumber(hunkMatch, "oldStart");
                    currentOldCount = ParsePartialNumber(hunkMatch, "oldCount", 1);
                    currentNewStart = ParsePartialNumber(hunkMatch, "newStart");
                    currentNewCount = ParsePartialNumber(hunkMatch, "newCount", 1);
                    oldLineNumber = currentOldStart;
                    newLineNumber = currentNewStart;
                    currentLines = new List<PartialDiffLine>();
                    continue;
                }

                headerLines.Add(line);
                continue;
            }

            var nextHunkMatch = PartialHunkHeaderPattern.Match(line);
            if (nextHunkMatch.Success)
            {
                FinishPartialHunk(
                    hunks,
                    ref currentLines,
                    ref currentHunkId,
                    ref currentHeader,
                    ref currentOldStart,
                    ref currentOldCount,
                    ref currentNewStart,
                    ref currentNewCount);
                currentHunkId = hunks.Count;
                currentHeader = line;
                currentOldStart = ParsePartialNumber(nextHunkMatch, "oldStart");
                currentOldCount = ParsePartialNumber(nextHunkMatch, "oldCount", 1);
                currentNewStart = ParsePartialNumber(nextHunkMatch, "newStart");
                currentNewCount = ParsePartialNumber(nextHunkMatch, "newCount", 1);
                oldLineNumber = currentOldStart;
                newLineNumber = currentNewStart;
                currentLines = new List<PartialDiffLine>();
                continue;
            }

            if (line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal))
            {
                currentLines.Add(new PartialDiffLine(
                    currentHunkId,
                    currentLines.Count,
                    null,
                    null,
                    DiffLineKind.NoNewline,
                    line));
                continue;
            }

            if (line.Length == 0 || line[0] is not (' ' or '+' or '-'))
            {
                return new PartialDiffParseResult(
                    text,
                    headerLines,
                    hunks,
                    IsBinary: false,
                    IsTruncated: false,
                    "Git returned a malformed text patch.");
            }

            var contentWithLineEnding = rawLine.Length > 0 ? rawLine[1..] : string.Empty;
            var content = contentWithLineEnding.EndsWith('\r')
                ? contentWithLineEnding[..^1]
                : contentWithLineEnding;
            var kind = line[0] switch
            {
                ' ' => DiffLineKind.Context,
                '+' => DiffLineKind.Added,
                '-' => DiffLineKind.Removed,
                _ => DiffLineKind.FileHeader,
            };
            var partialLine = new PartialDiffLine(
                currentHunkId,
                currentLines.Count,
                kind is DiffLineKind.Context or DiffLineKind.Removed ? oldLineNumber : null,
                kind is DiffLineKind.Context or DiffLineKind.Added ? newLineNumber : null,
                kind,
                content)
            {
                PatchText = contentWithLineEnding,
            };
            currentLines.Add(partialLine);
            if (kind is DiffLineKind.Context or DiffLineKind.Removed)
            {
                oldLineNumber++;
            }

            if (kind is DiffLineKind.Context or DiffLineKind.Added)
            {
                newLineNumber++;
            }
        }

        FinishPartialHunk(
            hunks,
            ref currentLines,
            ref currentHunkId,
            ref currentHeader,
            ref currentOldStart,
            ref currentOldCount,
            ref currentNewStart,
            ref currentNewCount);

        if (sawBinaryHeader)
        {
            return new PartialDiffParseResult(
                text,
                headerLines,
                hunks,
                IsBinary: true,
                IsTruncated: false,
                "Binary file cannot be partially staged.");
        }

        return new PartialDiffParseResult(
            text,
            headerLines,
            hunks,
            IsBinary: false,
            IsTruncated: false,
            hunks.Count == 0
                ? "No textual changes are available for partial staging."
                : null);
    }

    private static int ParsePartialNumber(
        Match match,
        string groupName,
        int defaultValue = 0)
    {
        var group = match.Groups[groupName];
        return group.Success
            ? int.Parse(group.Value, System.Globalization.CultureInfo.InvariantCulture)
            : defaultValue;
    }

    private static void FinishPartialHunk(
        List<PartialDiffHunk> hunks,
        ref List<PartialDiffLine>? lines,
        ref int hunkId,
        ref string header,
        ref int oldStart,
        ref int oldCount,
        ref int newStart,
        ref int newCount)
    {
        if (lines is null)
        {
            return;
        }

        if (lines.Count == 0)
        {
            throw new InvalidOperationException("Git returned an empty partial diff hunk.");
        }

        hunks.Add(new PartialDiffHunk(
            hunkId,
            oldStart,
            oldCount,
            newStart,
            newCount,
            header,
            lines));
        lines = null;
        hunkId = -1;
        header = string.Empty;
        oldStart = 0;
        oldCount = 0;
        newStart = 0;
        newCount = 0;
    }

    private static string ComputePartialFingerprint(
        string repositoryRoot,
        FileChange file,
        bool staged,
        byte[] patch)
    {
        var identity = string.Join(
            '\0',
            repositoryRoot,
            file.Path,
            file.OldPath ?? string.Empty,
            staged ? "staged" : "unstaged",
            file.Kind.ToString(),
            file.IndexStatus,
            file.WorkTreeStatus);
        var identityBytes = Encoding.UTF8.GetBytes(identity);
        var input = new byte[identityBytes.Length + 1 + patch.Length];
        identityBytes.CopyTo(input, 0);
        input[identityBytes.Length] = 0;
        patch.CopyTo(input, identityBytes.Length + 1);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            comparison);
    }

    private sealed record PartialDiffReadResult(
        PartialFileDiff Diff,
        string RawPatch,
        IReadOnlyList<string> HeaderLines);

    private sealed record PartialDiffParseResult(
        string RawPatch,
        IReadOnlyList<string> HeaderLines,
        IReadOnlyList<PartialDiffHunk> Hunks,
        bool IsBinary,
        bool IsTruncated,
        string? Message);

    private readonly record struct PartialSelectionKey(int HunkId, int LineIndex);

    private sealed record PartialSelectionSet(
        HashSet<int> HunkIds,
        HashSet<PartialSelectionKey> LineKeys);

    private readonly record struct PartialPatchLine(char Prefix, string Content);
}
