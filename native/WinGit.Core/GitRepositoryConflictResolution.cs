using System.Security.Cryptography;
using System.Text;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private const int MaximumConflictFileBytes = 10 * 1024 * 1024;
    private const int MaximumConflictLineLength = 5_000;
    private const int MaximumConflictContentCharacters = 256 * 1024;
    private const int ConflictContextLineCount = 3;
    private static readonly UTF8Encoding StrictConflictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Reads one caller-selected unmerged path and captures the exact state
    /// required for a later, review-driven resolution.
    /// </summary>
    public async Task<ConflictFileSnapshot> GetConflictFileSnapshotAsync(
        string root,
        string path,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var normalizedPath = ValidateGitPath(repositoryRoot, path, nameof(path));
        var fullPath = GetFullPathForGitPath(repositoryRoot, normalizedPath);
        if (ContainsReparsePoint(repositoryRoot, fullPath))
        {
            return CreateUnsupportedConflictSnapshot(
                repositoryRoot,
                normalizedPath,
                string.Empty,
                [],
                null,
                null,
                ConflictFileKind.Unsupported,
                null,
                "The conflicted path or one of its parent directories is a reparse point.");
        }

        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var change = status.Changes.FirstOrDefault(
            candidate => PathsEqual(candidate.Path, normalizedPath));
        if (change is null || !IsUnmergedChange(change))
        {
            throw new ConflictSnapshotException(
                normalizedPath,
                "the selected path is not currently unmerged in the index.");
        }

        if (status.IsUnborn || status.HeadId.Length == 0)
        {
            throw new ConflictSnapshotException(
                normalizedPath,
                "the repository has no current HEAD to guard the conflict snapshot.");
        }

        var stages = await ReadConflictIndexStagesAsync(
            repositoryRoot,
            normalizedPath,
            cancellationToken).ConfigureAwait(false);
        if (stages.Count == 0)
        {
            throw new ConflictSnapshotException(
                normalizedPath,
                "Git did not return conflict stages for the selected path.");
        }

        var stageShape = DetermineConflictShape(stages);
        var fileRead = await ReadConflictFileAsync(repositoryRoot, fullPath, cancellationToken).ConfigureAwait(false);
        if (fileRead.IsReparsePoint)
        {
            return CreateUnsupportedConflictSnapshot(
                repositoryRoot,
                normalizedPath,
                status.HeadId,
                stages,
                null,
                null,
                stageShape.Kind,
                stageShape.DeletedSide,
                "The conflicted path is a reparse point.");
        }

        if (fileRead.IsDirectory)
        {
            return CreateUnsupportedConflictSnapshot(
                repositoryRoot,
                normalizedPath,
                status.HeadId,
                stages,
                null,
                null,
                stageShape.Kind,
                stageShape.DeletedSide,
                "The conflicted path is a directory.");
        }

        if (fileRead.IsInaccessible)
        {
            return CreateUnsupportedConflictSnapshot(
                repositoryRoot,
                normalizedPath,
                status.HeadId,
                stages,
                null,
                null,
                ConflictFileKind.Unsupported,
                stageShape.DeletedSide,
                "The conflicted working file could not be read.");
        }

        if (fileRead.IsTruncated)
        {
            return CreateUnsupportedConflictSnapshot(
                repositoryRoot,
                normalizedPath,
                status.HeadId,
                stages,
                null,
                null,
                stageShape.Kind,
                stageShape.DeletedSide,
                "The working file exceeds the native conflict read limit.");
        }

        if (stageShape.Kind == ConflictFileKind.Unsupported)
        {
            return CreateUnsupportedConflictSnapshot(
                repositoryRoot,
                normalizedPath,
                status.HeadId,
                stages,
                fileRead.Bytes,
                fileRead.Bytes is null ? null : ComputeSha256(fileRead.Bytes),
                stageShape.Kind,
                stageShape.DeletedSide,
                "Git returned an unsupported conflict stage shape.");
        }

        var workingBytes = fileRead.Bytes;
        var workingHash = workingBytes is null ? null : ComputeSha256(workingBytes);
        if (stageShape.Kind == ConflictFileKind.DeleteModify)
        {
            return new ConflictFileSnapshot(
                repositoryRoot,
                normalizedPath,
                status.HeadId,
                stages,
                workingBytes,
                workingHash,
                stageShape.Kind,
                [],
                stageShape.DeletedSide);
        }

        if (workingBytes is null)
        {
            return CreateUnsupportedConflictSnapshot(
                repositoryRoot,
                normalizedPath,
                status.HeadId,
                stages,
                null,
                null,
                ConflictFileKind.Unsupported,
                null,
                "The conflicted working file is missing.");
        }

        if (workingBytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            return CreateUnsupportedConflictSnapshot(
                repositoryRoot,
                normalizedPath,
                status.HeadId,
                stages,
                workingBytes,
                workingHash,
                ConflictFileKind.Binary,
                null,
                "Binary conflict files are not rendered by the native text resolver.");
        }

        ConflictTextParseResult parsed;
        try
        {
            parsed = ParseConflictText(workingBytes);
        }
        catch (DecoderFallbackException)
        {
            return CreateUnsupportedConflictSnapshot(
                repositoryRoot,
                normalizedPath,
                status.HeadId,
                stages,
                workingBytes,
                workingHash,
                ConflictFileKind.Unsupported,
                null,
                "The conflicted working file is not valid UTF-8.");
        }

        if (parsed.UnsupportedReason is not null)
        {
            return CreateUnsupportedConflictSnapshot(
                repositoryRoot,
                normalizedPath,
                status.HeadId,
                stages,
                workingBytes,
                workingHash,
                ConflictFileKind.Unsupported,
                null,
                parsed.UnsupportedReason,
                parsed.HasUtf8Bom);
        }

        return new ConflictFileSnapshot(
            repositoryRoot,
            normalizedPath,
            status.HeadId,
            stages,
            workingBytes,
            workingHash,
            ConflictFileKind.Text,
            parsed.Hunks,
            hasUtf8Bom: parsed.HasUtf8Bom);
    }

    /// <summary>
    /// Applies only caller-reviewed replacements or a delete/keep choice.  The
    /// index is left unresolved unless <paramref name="request"/> explicitly
    /// asks this method to stage the named path.
    /// </summary>
    public async Task<ConflictResolutionResult> ApplyConflictResolutionAsync(
        string root,
        ConflictFileSnapshot snapshot,
        ConflictResolutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        if (!snapshot.IsSupported)
        {
            throw new ConflictResolutionUnsupportedException(
                snapshot.Path,
                snapshot.UnsupportedReason ?? "the captured conflict is unsupported.");
        }

        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var normalizedPath = ValidateGitPath(repositoryRoot, snapshot.Path, nameof(snapshot));
        if (!PathsEqual(repositoryRoot, snapshot.RootPath)
            || !string.Equals(normalizedPath, snapshot.Path, StringComparison.Ordinal))
        {
            throw new ConflictFileSnapshotStaleException(
                snapshot.Path,
                "the snapshot belongs to a different repository or path.");
        }

        ValidateResolutionRequest(snapshot, request);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var fullPath = GetFullPathForGitPath(path, normalizedPath);
                var before = await EnsureCurrentConflictSnapshotAsync(
                    path,
                    normalizedPath,
                    snapshot,
                    cancellationToken).ConfigureAwait(false);

                var resolvedBytes = await BuildResolvedBytesAsync(
                    path,
                    normalizedPath,
                    snapshot,
                    request,
                    before.File.Bytes,
                    cancellationToken).ConfigureAwait(false);
                var wasDeleted = resolvedBytes is null;
                var keepExistingFile = snapshot.Kind == ConflictFileKind.DeleteModify
                    && request.DeleteAction == ConflictResolutionAction.Keep
                    && before.File.Bytes is not null;

                string? temporaryPath = null;
                try
                {
                    if (wasDeleted)
                    {
                        // Keep/Delete can read a stage blob asynchronously.  The
                        // final guarded read must therefore happen immediately
                        // before deleting the named file.
                        var beforeDelete = await EnsureCurrentConflictSnapshotAsync(
                            path,
                            normalizedPath,
                            snapshot,
                            cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (beforeDelete.File.Bytes is not null)
                        {
                            DeleteConflictFileSafely(fullPath);
                        }
                    }
                    else if (keepExistingFile)
                    {
                        // Keeping an already present delete/modify file needs no
                        // rewrite.  Preserve its metadata and readonly behavior,
                        // while still performing the final guarded read below.
                        await EnsureCurrentConflictSnapshotAsync(
                            path,
                            normalizedPath,
                            snapshot,
                            cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        temporaryPath = await CreateConflictTempFileAsync(
                            fullPath,
                            resolvedBytes!,
                            cancellationToken).ConfigureAwait(false);
                        // The temp write is asynchronous.  Re-read all guarded
                        // state after it completes and immediately before the
                        // atomic replacement so a concurrent editor cannot be
                        // overwritten with a stale resolution.
                        await EnsureCurrentConflictSnapshotAsync(
                            path,
                            normalizedPath,
                            snapshot,
                            cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        ReplaceConflictTempFile(temporaryPath, fullPath);
                        temporaryPath = null;
                    }
                }
                finally
                {
                    DeleteConflictTempFile(temporaryPath);
                }

                IReadOnlyList<ConflictIndexStage> remainingStages = before.Stages;
                if (request.Stage)
                {
                    await processRunner.RunAsync(
                        path,
                        ["add", "--", ToLiteralPathSpec(normalizedPath)],
                        cancellationToken).ConfigureAwait(false);
                    remainingStages = await ReadConflictIndexStagesAsync(
                        path,
                        normalizedPath,
                        cancellationToken).ConfigureAwait(false);
                }

                var finalHash = resolvedBytes is null ? null : ComputeSha256(resolvedBytes);
                return new ConflictResolutionResult(
                    path,
                    normalizedPath,
                    wasDeleted,
                    request.Stage,
                    finalHash,
                    remainingStages);
            }).ConfigureAwait(false);
    }

    private async Task<ConflictStateCheck> EnsureCurrentConflictSnapshotAsync(
        string repositoryRoot,
        string path,
        ConflictFileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (status.IsUnborn
            || !string.Equals(status.HeadId, snapshot.ExpectedHeadId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictFileSnapshotStaleException(
                path,
                "HEAD changed after the snapshot was captured.");
        }

        var change = status.Changes.FirstOrDefault(candidate => PathsEqual(candidate.Path, path));
        if (change is null || !IsUnmergedChange(change))
        {
            throw new ConflictFileSnapshotStaleException(
                path,
                "the path is no longer an unresolved index conflict.");
        }

        var stages = await ReadConflictIndexStagesAsync(
            repositoryRoot,
            path,
            cancellationToken).ConfigureAwait(false);
        EnsureSameConflictStages(snapshot, stages);

        var fullPath = GetFullPathForGitPath(repositoryRoot, path);
        if (ContainsReparsePoint(repositoryRoot, fullPath))
        {
            throw new ConflictFileSnapshotStaleException(
                path,
                "the path or one of its parent directories is a reparse point.");
        }

        var file = await ReadConflictFileAsync(repositoryRoot, fullPath, cancellationToken).ConfigureAwait(false);
        if (file.IsReparsePoint || file.IsDirectory || file.IsTruncated || file.IsInaccessible)
        {
            throw new ConflictFileSnapshotStaleException(
                path,
                "the working file is no longer safely readable.");
        }

        var currentHash = file.Bytes is null ? null : ComputeSha256(file.Bytes);
        if (!string.Equals(currentHash, snapshot.WorkingFileSha256, StringComparison.OrdinalIgnoreCase)
            || (file.Bytes is null) != (snapshot.OriginalWorkingFileBytes is null))
        {
            throw new ConflictFileSnapshotStaleException(
                path,
                "the working-file bytes changed after the snapshot was captured.");
        }

        return new ConflictStateCheck(file, stages);
    }

    private static async Task<string> CreateConflictTempFileAsync(
        string targetPath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (directory is null)
        {
            throw new InvalidOperationException("The conflicted file has no parent directory.");
        }

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.wingit-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan))
            {
                created = true;
                await stream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return tempPath;
        }
        catch
        {
            if (created)
            {
                DeleteConflictTempFile(tempPath);
            }

            throw;
        }
    }

    private static void ReplaceConflictTempFile(string tempPath, string targetPath)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (targetDirectory is null || ContainsReparsePoint(targetDirectory, targetPath))
        {
            throw new IOException("The conflicted target path is no longer safe to replace.");
        }

        if (File.Exists(targetPath))
        {
            var attributes = File.GetAttributes(targetPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("The conflicted target became a reparse point.");
            }

            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                throw new UnauthorizedAccessException("The conflicted target is read-only.");
            }

            var preservedAttributes = attributes & (
                FileAttributes.Hidden
                | FileAttributes.System
                | FileAttributes.Archive
                | FileAttributes.NotContentIndexed);
            File.SetAttributes(
                tempPath,
                preservedAttributes == 0 ? FileAttributes.Normal : preservedAttributes);
            File.Replace(
                tempPath,
                targetPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: false);
            return;
        }

        File.Move(tempPath, targetPath);
    }

    private static void DeleteConflictFileSafely(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null || ContainsReparsePoint(parent, path))
        {
            throw new IOException("The conflicted target path is no longer safe to delete.");
        }

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The conflicted target became a reparse point.");
        }

        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            throw new UnauthorizedAccessException("The conflicted target is read-only.");
        }

        File.Delete(path);
    }

    private static void DeleteConflictTempFile(string? path)
    {
        if (path is null)
        {
            return;
        }

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
            // Cleanup must not replace the original write or cancellation error.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup must not replace the original write or cancellation error.
        }
    }

    private async Task<byte[]?> BuildResolvedBytesAsync(
        string repositoryRoot,
        string path,
        ConflictFileSnapshot snapshot,
        ConflictResolutionRequest request,
        byte[]? currentBytes,
        CancellationToken cancellationToken)
    {
        if (snapshot.Kind == ConflictFileKind.DeleteModify)
        {
            if (request.DeleteAction == ConflictResolutionAction.Delete)
            {
                return null;
            }

            if (currentBytes is not null)
            {
                return currentBytes;
            }

            var stage = snapshot.DeletedSide == ConflictDeletedSide.Ours
                ? snapshot.IndexStages.SingleOrDefault(candidate => candidate.StageNumber == 3)
                : snapshot.IndexStages.SingleOrDefault(candidate => candidate.StageNumber == 2);
            if (stage is null)
            {
                throw new ConflictResolutionUnsupportedException(
                    path,
                    "the non-deleted conflict stage is unavailable.");
            }

            if (!IsRegularFileMode(stage.Mode))
            {
                throw new ConflictResolutionUnsupportedException(
                    path,
                    "the non-deleted conflict stage is not a regular file.");
            }

            var result = await processRunner.RunAsync(
                repositoryRoot,
                ["cat-file", "blob", stage.BlobId],
                cancellationToken).ConfigureAwait(false);
            EnsureComplete(result, "conflict stage blob");
            if (result.StandardOutput.Length > MaximumConflictFileBytes)
            {
                throw new ConflictResolutionUnsupportedException(
                    path,
                    "the non-deleted conflict stage exceeds the native write limit.");
            }

            return result.StandardOutput;
        }

        if (snapshot.Kind != ConflictFileKind.Text || currentBytes is null)
        {
            throw new ConflictResolutionUnsupportedException(
                path,
                "the captured conflict is not a supported text conflict.");
        }

        ConflictTextParseResult parsed;
        try
        {
            parsed = ParseConflictText(currentBytes);
        }
        catch (DecoderFallbackException)
        {
            throw new ConflictResolutionUnsupportedException(path, "the current file is not valid UTF-8.");
        }

        if (parsed.UnsupportedReason is not null)
        {
            throw new ConflictResolutionUnsupportedException(path, parsed.UnsupportedReason);
        }

        var replacementByIndex = request.HunkReplacements.ToDictionary(
            replacement => replacement.HunkIndex);
        var body = new StringBuilder(parsed.Text.Length);
        var cursor = 0;
        foreach (var block in parsed.Blocks)
        {
            body.Append(parsed.Text, cursor, block.StartOffset - cursor);
            var replacement = replacementByIndex[block.Hunk.Index];
            body.Append(NormalizeReplacement(replacement.ResolvedContent, parsed.NewLine));
            cursor = block.EndOffset;
        }

        body.Append(parsed.Text, cursor, parsed.Text.Length - cursor);
        var encoded = StrictConflictUtf8.GetBytes(body.ToString());
        if (encoded.Length > MaximumConflictFileBytes)
        {
            throw new ConflictResolutionUnsupportedException(
                path,
                "the resolved file exceeds the native write limit.");
        }

        if (!parsed.HasUtf8Bom)
        {
            return encoded;
        }

        var withBom = new byte[encoded.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        encoded.CopyTo(withBom, 3);
        return withBom;
    }

    private static void ValidateResolutionRequest(
        ConflictFileSnapshot snapshot,
        ConflictResolutionRequest request)
    {
        if (snapshot.Kind == ConflictFileKind.DeleteModify)
        {
            if (request.DeleteAction is null || request.HunkReplacements.Count != 0)
            {
                throw new ArgumentException(
                    "Delete-vs-modify conflicts require exactly one Keep or Delete action.",
                    nameof(request));
            }

            return;
        }

        if (request.DeleteAction is not null)
        {
            throw new ArgumentException(
                "Keep/Delete actions are only valid for delete-vs-modify conflicts.",
                nameof(request));
        }

        if (request.HunkReplacements.Count != snapshot.Hunks.Count)
        {
            throw new ArgumentException(
                "A text conflict requires one replacement for every captured hunk.",
                nameof(request));
        }

        var seen = new HashSet<int>();
        foreach (var replacement in request.HunkReplacements)
        {
            ArgumentNullException.ThrowIfNull(replacement);
            if (replacement.HunkIndex < 0
                || replacement.HunkIndex >= snapshot.Hunks.Count
                || !seen.Add(replacement.HunkIndex))
            {
                throw new ArgumentException(
                    "Conflict hunk replacements must address each captured hunk exactly once.",
                    nameof(request));
            }

            if (replacement.ResolvedContent is null
                || replacement.ResolvedContent.IndexOf('\0') >= 0
                || ContainsConflictMarkerLine(replacement.ResolvedContent))
            {
                throw new ArgumentException(
                    "A resolved hunk must be text without conflict marker lines.",
                    nameof(request));
            }

            if (StrictConflictUtf8.GetByteCount(replacement.ResolvedContent) > MaximumConflictFileBytes)
            {
                throw new ArgumentException(
                    "A resolved hunk exceeds the native write limit.",
                    nameof(request));
            }
        }
    }

    private static void EnsureSameConflictStages(
        ConflictFileSnapshot snapshot,
        IReadOnlyList<ConflictIndexStage> actualStages)
    {
        if (snapshot.IndexStages.Count != actualStages.Count
            || snapshot.IndexStages.Zip(actualStages).Any(pair =>
                pair.First.StageNumber != pair.Second.StageNumber
                || !string.Equals(pair.First.Mode, pair.Second.Mode, StringComparison.Ordinal)
                || !string.Equals(pair.First.BlobId, pair.Second.BlobId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConflictFileSnapshotStaleException(
                snapshot.Path,
                "the conflict stages in the index changed after the snapshot was captured.");
        }
    }

    private static ConflictFileSnapshot CreateUnsupportedConflictSnapshot(
        string repositoryRoot,
        string path,
        string expectedHeadId,
        IReadOnlyList<ConflictIndexStage> stages,
        byte[]? workingBytes,
        string? workingHash,
        ConflictFileKind kind,
        ConflictDeletedSide? deletedSide,
        string reason,
        bool hasUtf8Bom = false)
    {
        return new ConflictFileSnapshot(
            repositoryRoot,
            path,
            expectedHeadId.Length == 0 ? new string('0', 40) : expectedHeadId,
            stages,
            workingBytes,
            workingHash,
            kind,
            [],
            deletedSide,
            hasUtf8Bom,
            reason);
    }

    private async Task<IReadOnlyList<ConflictIndexStage>> ReadConflictIndexStagesAsync(
        string repositoryRoot,
        string path,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["ls-files", "--unmerged", "-z", "--", ToLiteralPathSpec(path)],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "conflict index stages");
        var text = DecodeUtf8(result.StandardOutput, "conflict index stages");
        var stages = new List<ConflictIndexStage>();
        foreach (var record in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab <= 0 || tab == record.Length - 1)
            {
                throw new InvalidOperationException("Git returned a malformed conflict stage record.");
            }

            var fields = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3
                || !int.TryParse(fields[2], out var stageNumber)
                || stageNumber is < 1 or > 3)
            {
                throw new InvalidOperationException("Git returned an invalid conflict stage record.");
            }

            var stagePath = ValidateGitPath(repositoryRoot, record[(tab + 1)..], "conflict stage path");
            if (!string.Equals(stagePath, path, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Git returned a conflict stage for an unexpected path.");
            }

            ValidateObjectId(fields[1], "conflict stage object ID");
            if (!fields[0].All(character => character is >= '0' and <= '9'))
            {
                throw new InvalidOperationException("Git returned an invalid conflict stage mode.");
            }

            stages.Add(new ConflictIndexStage(stageNumber, fields[0], fields[1]));
        }

        if (stages.GroupBy(stage => stage.StageNumber).Any(group => group.Count() != 1))
        {
            throw new InvalidOperationException("Git returned duplicate conflict stages.");
        }

        return stages.OrderBy(stage => stage.StageNumber).ToArray();
    }

    private static (ConflictFileKind Kind, ConflictDeletedSide? DeletedSide) DetermineConflictShape(
        IReadOnlyList<ConflictIndexStage> stages)
    {
        var hasOurs = stages.Any(stage => stage.StageNumber == 2);
        var hasTheirs = stages.Any(stage => stage.StageNumber == 3);
        if (hasOurs && hasTheirs)
        {
            return (ConflictFileKind.Text, null);
        }

        if (hasOurs)
        {
            return (ConflictFileKind.DeleteModify, ConflictDeletedSide.Theirs);
        }

        if (hasTheirs)
        {
            return (ConflictFileKind.DeleteModify, ConflictDeletedSide.Ours);
        }

        return (ConflictFileKind.Unsupported, null);
    }

    private static bool IsRegularFileMode(string mode) =>
        string.Equals(mode, "100644", StringComparison.Ordinal)
        || string.Equals(mode, "100755", StringComparison.Ordinal);

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool ContainsReparsePoint(string repositoryRoot, string fullPath)
    {
        var current = fullPath;
        while (true)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                // A missing leaf is valid for a delete/modify conflict; inspect
                // its parent on the next iteration.
            }
            catch (DirectoryNotFoundException)
            {
                // Inspect the nearest existing parent.
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            if (PathsEqual(current, repositoryRoot))
            {
                return false;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || PathsEqual(parent, current))
            {
                return false;
            }

            current = parent;
        }
    }

    private static async Task<ConflictFileRead> ReadConflictFileAsync(
        string repositoryRoot,
        string fullPath,
        CancellationToken cancellationToken)
    {
        if (ContainsReparsePoint(repositoryRoot, fullPath))
        {
            return new ConflictFileRead(null, false, false, false, true);
        }

        if (Directory.Exists(fullPath))
        {
            return new ConflictFileRead(null, true, false, false, false);
        }

        if (!File.Exists(fullPath))
        {
            return new ConflictFileRead(null, false, false, false, false);
        }

        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            using var bytes = new MemoryStream(capacity: 64 * 1024);
            var buffer = new byte[64 * 1024];
            var maximumBytesToProbe = MaximumConflictFileBytes + 1L;
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var remaining = maximumBytesToProbe - bytes.Length;
                if (remaining <= 0)
                {
                    return new ConflictFileRead(null, false, false, true, false);
                }

                var toCopy = (int)Math.Min(remaining, count);
                bytes.Write(buffer, 0, toCopy);
                if (bytes.Length > MaximumConflictFileBytes || toCopy != count)
                {
                    return new ConflictFileRead(null, false, false, true, false);
                }
            }

            return new ConflictFileRead(bytes.ToArray(), false, false, false, false);
        }
        catch (FileNotFoundException)
        {
            return new ConflictFileRead(null, false, false, false, false);
        }
        catch (DirectoryNotFoundException)
        {
            return new ConflictFileRead(null, false, false, false, false);
        }
        catch (UnauthorizedAccessException)
        {
            return new ConflictFileRead(null, false, false, false, true);
        }
        catch (IOException)
        {
            return new ConflictFileRead(null, false, false, false, true);
        }
    }

    private static ConflictTextParseResult ParseConflictText(byte[] bytes)
    {
        var hasBom = bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF;
        var textBytes = hasBom ? bytes[3..] : bytes;
        var text = StrictConflictUtf8.GetString(textBytes);
        var lines = SplitConflictLines(text);
        var blocks = new List<ParsedConflictBlock>();
        var hunkIndex = 0;
        var totalContent = 0;

        for (var index = 0; index < lines.Count; index++)
        {
            if (!IsOursMarker(lines[index].Text))
            {
                continue;
            }

            var markerStart = index;
            var ours = new List<string>();
            var baseLines = new List<string>();
            var theirs = new List<string>();
            var hasBase = false;
            var separatorFound = false;
            var closingIndex = -1;
            index++;
            while (index < lines.Count)
            {
                if (IsBaseMarker(lines[index].Text))
                {
                    hasBase = true;
                    index++;
                    break;
                }

                if (IsSeparatorMarker(lines[index].Text))
                {
                    separatorFound = true;
                    index++;
                    break;
                }

                ours.Add(lines[index++].Text);
            }

            if (hasBase)
            {
                while (index < lines.Count)
                {
                    if (IsSeparatorMarker(lines[index].Text))
                    {
                        separatorFound = true;
                        index++;
                        break;
                    }

                    baseLines.Add(lines[index++].Text);
                }
            }

            if (separatorFound)
            {
                while (index < lines.Count)
                {
                    if (IsTheirsMarker(lines[index].Text))
                    {
                        closingIndex = index;
                        break;
                    }

                    theirs.Add(lines[index++].Text);
                }
            }

            if (!separatorFound || closingIndex < 0)
            {
                return new ConflictTextParseResult(
                    text,
                    hasBom,
                    GetPreferredNewLine(text),
                    [],
                    [],
                    "The working file contains a malformed conflict marker block.");
            }

            foreach (var contentLine in ours.Concat(baseLines).Concat(theirs))
            {
                if (contentLine.Length > MaximumConflictLineLength)
                {
                    return new ConflictTextParseResult(
                        text,
                        hasBom,
                        GetPreferredNewLine(text),
                        [],
                        [],
                        "A conflict hunk contains a line that exceeds the native line limit.");
                }

                totalContent += contentLine.Length;
                if (totalContent > MaximumConflictContentCharacters)
                {
                    return new ConflictTextParseResult(
                        text,
                        hasBom,
                        GetPreferredNewLine(text),
                        [],
                        [],
                        "The conflict regions exceed the native content limit.");
                }
            }

            var contextBefore = ReadConflictContext(lines, markerStart, before: true);
            var contextAfter = ReadConflictContext(lines, closingIndex, before: false);
            var hunk = new ConflictHunk(
                hunkIndex,
                string.Join('\n', ours),
                string.Join('\n', theirs),
                hasBase ? string.Join('\n', baseLines) : null,
                contextBefore,
                contextAfter);
            blocks.Add(new ParsedConflictBlock(
                hunk,
                lines[markerStart].StartOffset,
                lines[closingIndex].EndOffset));
            hunkIndex++;
            index = closingIndex;
        }

        if (blocks.Count == 0)
        {
            return new ConflictTextParseResult(
                text,
                hasBom,
                GetPreferredNewLine(text),
                [],
                [],
                "No supported conflict markers were found in the working file.");
        }

        return new ConflictTextParseResult(
            text,
            hasBom,
            GetPreferredNewLine(text),
            blocks.Select(block => block.Hunk).ToArray(),
            blocks,
            null);
    }

    private static IReadOnlyList<ConflictLine> SplitConflictLines(string text)
    {
        var lines = new List<ConflictLine>();
        var offset = 0;
        while (offset < text.Length)
        {
            var start = offset;
            var newline = text.IndexOf('\n', offset);
            var end = newline < 0 ? text.Length : newline;
            if (end > start && text[end - 1] == '\r')
            {
                end--;
            }

            var next = newline < 0 ? text.Length : newline + 1;
            lines.Add(new ConflictLine(text[start..end], start, end));
            offset = next;
        }

        return lines;
    }

    private static string ReadConflictContext(
        IReadOnlyList<ConflictLine> lines,
        int markerIndex,
        bool before)
    {
        var context = new List<string>();
        if (before)
        {
            for (var index = markerIndex - 1; index >= 0 && context.Count < ConflictContextLineCount; index--)
            {
                if (IsAnyConflictMarker(lines[index].Text))
                {
                    break;
                }

                context.Insert(0, lines[index].Text);
            }
        }
        else
        {
            for (var index = markerIndex + 1; index < lines.Count && context.Count < ConflictContextLineCount; index++)
            {
                if (IsAnyConflictMarker(lines[index].Text))
                {
                    break;
                }

                context.Add(lines[index].Text);
            }
        }

        return string.Join('\n', context);
    }

    private static string NormalizeReplacement(string content, string newLine)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return string.Equals(newLine, "\n", StringComparison.Ordinal)
            ? normalized
            : normalized.Replace("\n", newLine, StringComparison.Ordinal);
    }

    private static string GetPreferredNewLine(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static bool ContainsConflictMarkerLine(string text) =>
        SplitConflictLines(text).Any(line => IsAnyConflictMarker(line.Text));

    private static bool IsAnyConflictMarker(string line) =>
        IsOursMarker(line)
        || IsBaseMarker(line)
        || IsSeparatorMarker(line)
        || IsTheirsMarker(line);

    private static bool IsOursMarker(string line) =>
        line.StartsWith("<<<<<<<", StringComparison.Ordinal)
        && (line.Length == 7 || char.IsWhiteSpace(line[7]));

    private static bool IsBaseMarker(string line) =>
        line.StartsWith("|||||||", StringComparison.Ordinal)
        && (line.Length == 7 || char.IsWhiteSpace(line[7]));

    private static bool IsSeparatorMarker(string line) =>
        string.Equals(line, "=======", StringComparison.Ordinal);

    private static bool IsTheirsMarker(string line) =>
        line.StartsWith(">>>>>>>", StringComparison.Ordinal)
        && (line.Length == 7 || char.IsWhiteSpace(line[7]));

    private sealed record ConflictFileRead(
        byte[]? Bytes,
        bool IsDirectory,
        bool IsReparsePoint,
        bool IsTruncated,
        bool IsInaccessible);

    private sealed record ConflictStateCheck(
        ConflictFileRead File,
        IReadOnlyList<ConflictIndexStage> Stages);

    private sealed record ConflictLine(string Text, int StartOffset, int EndOffset);

    private sealed record ParsedConflictBlock(
        ConflictHunk Hunk,
        int StartOffset,
        int EndOffset);

    private sealed record ConflictTextParseResult(
        string Text,
        bool HasUtf8Bom,
        string NewLine,
        IReadOnlyList<ConflictHunk> Hunks,
        IReadOnlyList<ParsedConflictBlock> Blocks,
        string? UnsupportedReason);
}
