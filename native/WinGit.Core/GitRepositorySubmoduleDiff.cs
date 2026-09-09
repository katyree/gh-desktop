namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private const string SubprojectCommitPrefix = "Subproject commit ";

    private static SubmoduleComparison? TryParseSubmoduleComparison(
        string text,
        bool requireGitLinkMode = true)
    {
        var sectionCount = 0;
        var sawDiffHeader = false;
        var inHunk = false;
        bool? oldGitLinkMode = null;
        bool? newGitLinkMode = null;
        string? oldCommitId = null;
        string? newCommitId = null;
        var oldIsDirty = false;
        var newIsDirty = false;
        var sawOldCommit = false;
        var sawNewCommit = false;
        var renderedLineCount = 0;

        foreach (var rawLine in text.Split('\n', StringSplitOptions.None))
        {
            renderedLineCount++;
            if (renderedLineCount > MaximumRenderedLines
                || rawLine.Length > MaximumRenderedLineLength + 1)
            {
                return null;
            }

            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                sectionCount++;
                if (sectionCount > 1)
                {
                    return null;
                }

                sawDiffHeader = true;
                inHunk = false;
                continue;
            }

            if (!sawDiffHeader)
            {
                continue;
            }

            if (!inHunk)
            {
                if (line.StartsWith("@@ ", StringComparison.Ordinal))
                {
                    inHunk = true;
                    continue;
                }

                if (!TryReadSubmoduleModeHeader(
                        line,
                        ref oldGitLinkMode,
                        ref newGitLinkMode))
                {
                    return null;
                }

                continue;
            }

            if (line.StartsWith("-" + SubprojectCommitPrefix, StringComparison.Ordinal))
            {
                if (sawOldCommit
                    || !TryReadSubprojectCommit(
                        line,
                        '-',
                        out oldCommitId,
                        out oldIsDirty))
                {
                    return null;
                }

                sawOldCommit = true;
            }
            else if (line.StartsWith("+" + SubprojectCommitPrefix, StringComparison.Ordinal))
            {
                if (sawNewCommit
                    || !TryReadSubprojectCommit(
                        line,
                        '+',
                        out newCommitId,
                        out newIsDirty))
                {
                    return null;
                }

                sawNewCommit = true;
            }
        }

        var hasGitLinkMode = oldGitLinkMode is not null || newGitLinkMode is not null;
        if (!sawDiffHeader
            || sectionCount != 1
            || !inHunk
            || (!sawOldCommit && !sawNewCommit)
            || (hasGitLinkMode
                && !HasExpectedGitLinkModes(
                    oldCommitId,
                    newCommitId,
                    oldGitLinkMode,
                    newGitLinkMode))
            || (requireGitLinkMode
                && !HasExpectedGitLinkModes(
                    oldCommitId,
                    newCommitId,
                    oldGitLinkMode,
                    newGitLinkMode)))
        {
            return null;
        }

        return new SubmoduleComparison(
            oldCommitId,
            newCommitId,
            oldIsDirty,
            newIsDirty);
    }

    private async Task<SubmoduleComparison?> TryReadDirtySubmoduleComparisonAsync(
        string repositoryRoot,
        FileChange file,
        ImageDiffScope scope,
        string diffText,
        CancellationToken cancellationToken)
    {
        var comparison = TryParseSubmoduleComparison(
            diffText,
            requireGitLinkMode: false);
        if (comparison is null || !comparison.IsDirtyOnly)
        {
            return null;
        }

        var expectedCommitId = await ReadDirtySubmoduleCommitIdAsync(
            repositoryRoot,
            file,
            scope,
            cancellationToken).ConfigureAwait(false);
        return expectedCommitId is not null
            && string.Equals(
                expectedCommitId,
                comparison.OldCommitId,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                expectedCommitId,
                comparison.NewCommitId,
                StringComparison.OrdinalIgnoreCase)
            ? comparison
            : null;
    }

    private async Task<FileDiff> EnrichDirtySubmoduleDiffAsync(
        string repositoryRoot,
        FileChange file,
        FileDiff diff,
        byte[] output,
        bool outputTruncated,
        ImageDiffScope scope,
        CancellationToken cancellationToken)
    {
        if (outputTruncated
            || diff.IsTruncated
            || diff.IsBinary
            || diff.SubmoduleComparison is not null
            || output.Length == 0)
        {
            return diff;
        }

        var comparison = await TryReadDirtySubmoduleComparisonAsync(
            repositoryRoot,
            file,
            scope,
            StrictUtf8.GetString(output),
            cancellationToken).ConfigureAwait(false);
        return comparison is null
            ? diff
            : new FileDiff(
                diff.Lines,
                diff.IsBinary,
                diff.IsTruncated,
                diff.Message,
                diff.ImageComparison,
                comparison);
    }

    private async Task<string?> ReadDirtySubmoduleCommitIdAsync(
        string repositoryRoot,
        FileChange file,
        ImageDiffScope scope,
        CancellationToken cancellationToken)
    {
        var currentPath = ValidateGitPath(repositoryRoot, file.Path, nameof(file));
        if (scope == ImageDiffScope.Unstaged)
        {
            var indexEntry = await ReadIndexEntryAsync(
                repositoryRoot,
                currentPath,
                cancellationToken).ConfigureAwait(false);
            return IsGitLinkEntry(indexEntry) ? indexEntry!.ObjectId : null;
        }

        if (scope is not (ImageDiffScope.Working or ImageDiffScope.Index))
        {
            return null;
        }

        var oldPath = ValidateGitPath(
            repositoryRoot,
            file.OldPath ?? file.Path,
            nameof(file));
        var headId = await ReadRefIdAsync(
            repositoryRoot,
            "HEAD^{commit}",
            cancellationToken).ConfigureAwait(false);
        if (headId is null)
        {
            return null;
        }

        var headEntry = await ReadTreeBlobEntryAsync(
            repositoryRoot,
            headId,
            oldPath,
            cancellationToken).ConfigureAwait(false);
        var indexEntryForComparison = await ReadIndexEntryAsync(
            repositoryRoot,
            currentPath,
            cancellationToken).ConfigureAwait(false);
        if (!IsGitLinkEntry(headEntry)
            || !IsGitLinkEntry(indexEntryForComparison)
            || !string.Equals(
                headEntry!.ObjectId,
                indexEntryForComparison!.ObjectId,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return headEntry.ObjectId;
    }

    private static bool IsGitLinkEntry(GitBlobEntry? entry) =>
        entry is not null
        && string.Equals(entry.Mode, SubmoduleGitLinkMode, StringComparison.Ordinal);

    private static bool TryReadSubmoduleModeHeader(
        string line,
        ref bool? oldGitLinkMode,
        ref bool? newGitLinkMode)
    {
        if (line.StartsWith("old mode ", StringComparison.Ordinal))
        {
            return SetSubmoduleMode(
                ref oldGitLinkMode,
                string.Equals(line["old mode ".Length..], SubmoduleGitLinkMode, StringComparison.Ordinal));
        }

        if (line.StartsWith("new mode ", StringComparison.Ordinal))
        {
            return SetSubmoduleMode(
                ref newGitLinkMode,
                string.Equals(line["new mode ".Length..], SubmoduleGitLinkMode, StringComparison.Ordinal));
        }

        if (line.StartsWith("new file mode ", StringComparison.Ordinal))
        {
            return SetSubmoduleMode(
                ref newGitLinkMode,
                string.Equals(line["new file mode ".Length..], SubmoduleGitLinkMode, StringComparison.Ordinal));
        }

        if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
        {
            return SetSubmoduleMode(
                ref oldGitLinkMode,
                string.Equals(line["deleted file mode ".Length..], SubmoduleGitLinkMode, StringComparison.Ordinal));
        }

        if (!line.StartsWith("index ", StringComparison.Ordinal))
        {
            return true;
        }

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length >= 3
            && string.Equals(fields[^1], SubmoduleGitLinkMode, StringComparison.Ordinal))
        {
            return SetSubmoduleMode(ref oldGitLinkMode, true)
                && SetSubmoduleMode(ref newGitLinkMode, true);
        }

        return true;
    }

    private static bool SetSubmoduleMode(ref bool? mode, bool value)
    {
        if (mode is not null && mode.Value != value)
        {
            return false;
        }

        mode = value;
        return true;
    }

    private static bool HasExpectedGitLinkModes(
        string? oldCommitId,
        string? newCommitId,
        bool? oldGitLinkMode,
        bool? newGitLinkMode)
    {
        if (oldCommitId is null)
        {
            return newCommitId is not null && newGitLinkMode == true;
        }

        if (newCommitId is null)
        {
            return oldGitLinkMode == true;
        }

        return oldGitLinkMode == true && newGitLinkMode == true;
    }

    private static bool TryReadSubprojectCommit(
        string line,
        char marker,
        out string? commitId,
        out bool isDirty)
    {
        commitId = null;
        isDirty = false;
        var prefix = marker + SubprojectCommitPrefix;
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var value = line[prefix.Length..];
        if (value.EndsWith("-dirty", StringComparison.Ordinal))
        {
            isDirty = true;
            value = value[..^"-dirty".Length];
        }

        if (value.Length is not (40 or 64)
            || !value.All(IsHexCharacter))
        {
            return false;
        }

        commitId = value.ToLowerInvariant();
        return true;
    }
}
