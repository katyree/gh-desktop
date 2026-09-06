using System.Text;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private enum ImageDiffScope
    {
        Working,
        Index,
        Unstaged,
        Commit,
        Selection,
    }

    private sealed record ImageDiffRequest(
        ImageDiffScope Scope,
        string? BaselineId = null,
        string? LastSelectedCommitId = null);

    private sealed record GitBlobEntry(string Mode, string ObjectId);

    private sealed record ImageDiffSide(
        ImageDiffContent? Content,
        bool WasImageCandidate,
        bool IsTruncated,
        string? Message);

    private async Task<FileDiff> EnrichImageDiffAsync(
        string repositoryRoot,
        FileChange file,
        FileDiff diff,
        ImageDiffRequest request,
        CancellationToken cancellationToken)
    {
        if (!diff.IsBinary || diff.IsTruncated)
        {
            return diff;
        }

        var comparison = await TryReadImageComparisonAsync(
            repositoryRoot,
            file,
            request,
            cancellationToken).ConfigureAwait(false);
        if (comparison is null)
        {
            return diff;
        }

        return new FileDiff(
            diff.Lines,
            diff.IsBinary,
            diff.IsTruncated || comparison.IsTruncated,
            diff.Message,
            comparison,
            diff.SubmoduleComparison);
    }

    private async Task<ImageComparison?> TryReadImageComparisonAsync(
        string repositoryRoot,
        FileChange file,
        ImageDiffRequest request,
        CancellationToken cancellationToken)
    {
        var beforePath = request.Scope == ImageDiffScope.Unstaged
            ? file.Path
            : file.OldPath ?? file.Path;
        var afterPath = file.Path;
        string? baselineId = null;
        GitBlobEntry? indexEntry = null;
        string? commitId = null;

        switch (request.Scope)
        {
            case ImageDiffScope.Working:
                baselineId = await ReadRefIdAsync(
                    repositoryRoot,
                    "HEAD^{commit}",
                    cancellationToken).ConfigureAwait(false);
                break;
            case ImageDiffScope.Index:
                baselineId = await ReadRefIdAsync(
                    repositoryRoot,
                    "HEAD^{commit}",
                    cancellationToken).ConfigureAwait(false);
                indexEntry = await ReadIndexEntryAsync(
                    repositoryRoot,
                    afterPath,
                    cancellationToken).ConfigureAwait(false);
                break;
            case ImageDiffScope.Unstaged:
                indexEntry = await ReadIndexEntryAsync(
                    repositoryRoot,
                    afterPath,
                    cancellationToken).ConfigureAwait(false);
                break;
            case ImageDiffScope.Commit:
                commitId = await ResolveCommitIdAsync(
                    repositoryRoot,
                    request.BaselineId ?? throw new InvalidOperationException("A commit ID is required."),
                    cancellationToken).ConfigureAwait(false);
                baselineId = await ReadRefIdAsync(
                    repositoryRoot,
                    commitId + "^1",
                    cancellationToken).ConfigureAwait(false);
                break;
            case ImageDiffScope.Selection:
                baselineId = request.BaselineId
                    ?? throw new InvalidOperationException("A selection baseline is required.");
                commitId = request.LastSelectedCommitId
                    ?? throw new InvalidOperationException("A selection commit is required.");
                ValidateObjectId(baselineId, "selection baseline object ID");
                ValidateObjectId(commitId, "selection commit ID");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Scope));
        }

        // The baseline/index identities are captured before either side's bytes
        // are read. The work tree is necessarily read from its current path, but
        // Git-backed sides use immutable object IDs from this point onward.
        var before = await ReadImageSideAsync(
            repositoryRoot,
            beforePath,
            baselineId,
            indexEntry: request.Scope is ImageDiffScope.Unstaged ? indexEntry : null,
            isWorkingTree: false,
            cancellationToken).ConfigureAwait(false);
        var after = await ReadImageSideAsync(
            repositoryRoot,
            afterPath,
            request.Scope == ImageDiffScope.Index ? null : commitId ?? baselineId,
            indexEntry,
            isWorkingTree: request.Scope is ImageDiffScope.Working or ImageDiffScope.Unstaged,
            cancellationToken).ConfigureAwait(false);

        if (!before.WasImageCandidate && !after.WasImageCandidate)
        {
            return null;
        }

        var message = before.Message ?? after.Message;
        var isTruncated = before.IsTruncated || after.IsTruncated;
        if (before.Content is null && after.Content is null && message is null)
        {
            message = "The image bytes could not be captured for the native viewer.";
        }

        return new ImageComparison(before.Content, after.Content, isTruncated, message);
    }

    private async Task<ImageDiffSide> ReadImageSideAsync(
        string repositoryRoot,
        string? path,
        string? treeish,
        GitBlobEntry? indexEntry,
        bool isWorkingTree,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(path))
        {
            return new ImageDiffSide(null, false, false, null);
        }

        var normalizedPath = ValidateGitPath(repositoryRoot, path, nameof(path));
        if (isWorkingTree)
        {
            return await ReadWorkingImageSideAsync(
                repositoryRoot,
                normalizedPath,
                cancellationToken).ConfigureAwait(false);
        }

        GitBlobEntry? entry;
        if (indexEntry is not null)
        {
            entry = indexEntry;
        }
        else if (treeish is not null)
        {
            entry = await ReadTreeBlobEntryAsync(
                repositoryRoot,
                treeish,
                normalizedPath,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            entry = null;
        }

        if (entry is null || !IsRegularBlob(entry.Mode))
        {
            return new ImageDiffSide(null, false, false, null);
        }

        return await ReadGitImageSideAsync(
            repositoryRoot,
            normalizedPath,
            entry.ObjectId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImageDiffSide> ReadWorkingImageSideAsync(
        string repositoryRoot,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = GetFullPathForGitPath(repositoryRoot, path);
        if (ContainsReparsePoint(repositoryRoot, fullPath))
        {
            return new ImageDiffSide(
                null,
                HasImageExtension(path),
                false,
                "Symbolic-link and reparse-point image bytes are not captured by the native viewer.");
        }

        if (!File.Exists(fullPath))
        {
            return new ImageDiffSide(null, false, false, null);
        }

        try
        {
            _ = File.GetAttributes(fullPath);
        }
        catch (FileNotFoundException)
        {
            return new ImageDiffSide(null, false, false, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new ImageDiffSide(null, false, false, null);
        }

        var bounded = await ReadBoundedFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (bounded.Truncated)
        {
            return new ImageDiffSide(
                null,
                GetImageMediaType(path, bounded.Bytes) is not null,
                true,
                "The image exceeds the native viewer limit and was not captured.");
        }

        return CreateImageSide(path, objectId: null, bounded.Bytes);
    }

    private async Task<ImageDiffSide> ReadGitImageSideAsync(
        string repositoryRoot,
        string path,
        string objectId,
        CancellationToken cancellationToken)
    {
        ValidateObjectId(objectId, "Git image blob ID");
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["cat-file", "blob", objectId],
            cancellationToken).ConfigureAwait(false);
        if (result.StandardOutputTruncated)
        {
            return new ImageDiffSide(
                null,
                HasImageExtension(path),
                true,
                "The image exceeds the native viewer limit and was not captured.");
        }

        return CreateImageSide(path, objectId, result.StandardOutput);
    }

    private async Task<GitBlobEntry?> ReadTreeBlobEntryAsync(
        string repositoryRoot,
        string treeish,
        string path,
        CancellationToken cancellationToken)
    {
        ValidateObjectId(treeish, "Git tree object ID");
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["ls-tree", "-z", "--full-tree", treeish, "--", ToLiteralPathSpec(path)],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "image tree entry");
        return ParseTreeBlobEntry(result.StandardOutput);
    }

    private async Task<GitBlobEntry?> ReadIndexEntryAsync(
        string repositoryRoot,
        string path,
        CancellationToken cancellationToken)
    {
        var normalizedPath = ValidateGitPath(repositoryRoot, path, nameof(path));
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["ls-files", "--stage", "--full-name", "-z", "--", ToLiteralPathSpec(normalizedPath)],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "image index entry");

        foreach (var record in SplitNullRecords(result.StandardOutput))
        {
            var tab = record.Span.IndexOf((byte)'\t');
            if (tab < 0)
            {
                throw new InvalidOperationException("Git returned a malformed image index entry.");
            }

            var fields = DecodeUtf8(record.Span[..tab].ToArray(), "image index entry")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3)
            {
                throw new InvalidOperationException("Git returned a malformed image index entry.");
            }

            if (!string.Equals(fields[2], "0", StringComparison.Ordinal))
            {
                continue;
            }

            ValidateObjectId(fields[1], "Git image index object ID");
            return new GitBlobEntry(fields[0], fields[1]);
        }

        return null;
    }

    private static GitBlobEntry? ParseTreeBlobEntry(byte[] output)
    {
        var record = SplitNullRecords(output).FirstOrDefault();
        if (record.IsEmpty)
        {
            return null;
        }

        var tab = record.Span.IndexOf((byte)'\t');
        if (tab < 0)
        {
            throw new InvalidOperationException("Git returned a malformed image tree entry.");
        }

        var fields = Encoding.UTF8.GetString(record.Span[..tab]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3)
        {
            throw new InvalidOperationException("Git returned a malformed image tree entry.");
        }

        ValidateObjectId(fields[2], "Git image tree object ID");
        return new GitBlobEntry(fields[0], fields[2]);
    }

    private static IEnumerable<ReadOnlyMemory<byte>> SplitNullRecords(byte[] output)
    {
        var start = 0;
        for (var index = 0; index < output.Length; index++)
        {
            if (output[index] != 0)
            {
                continue;
            }

            if (index > start)
            {
                yield return output.AsMemory(start, index - start);
            }

            start = index + 1;
        }

        if (start < output.Length)
        {
            yield return output.AsMemory(start);
        }
    }

    private static bool IsRegularBlob(string mode) =>
        mode.Length == 6
        && mode.StartsWith("100", StringComparison.Ordinal)
        && mode.All(static character => character is >= '0' and <= '7');

    private static ImageDiffSide CreateImageSide(string path, string? objectId, byte[] bytes)
    {
        var mediaType = GetImageMediaType(path, bytes);
        if (mediaType is null)
        {
            return new ImageDiffSide(
                null,
                false,
                false,
                "This revision is not a supported image.");
        }

        return new ImageDiffSide(
            new ImageDiffContent(path, mediaType, objectId, bytes),
            true,
            false,
            null);
    }

    private static bool HasImageExtension(string path) => GetImageMediaType(path, ReadOnlySpan<byte>.Empty) is not null;

    private static string? GetImageMediaType(string path, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 6
            && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            return "image/gif";
        }

        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            return "image/bmp";
        }

        if (bytes.Length >= 4
            && ((bytes[..4].SequenceEqual(new byte[] { 0x49, 0x49, 0x2A, 0x00 }))
                || bytes[..4].SequenceEqual(new byte[] { 0x4D, 0x4D, 0x00, 0x2A })))
        {
            return "image/tiff";
        }

        if (bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x00, 0x00, 0x01, 0x00 }))
        {
            return "image/x-icon";
        }

        if (bytes.Length >= 12
            && bytes[..4].SequenceEqual("RIFF"u8)
            && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".ico" => "image/x-icon",
            ".webp" => "image/webp",
            _ => null,
        };
    }
}
