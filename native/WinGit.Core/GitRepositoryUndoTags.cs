using System.Globalization;
using System.Text.RegularExpressions;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private static readonly Regex GitObjectIdPattern = new(
        "^[0-9a-fA-F]{40,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] UndoOperationPaths =
    [
        "MERGE_HEAD",
        "CHERRY_PICK_HEAD",
        "REVERT_HEAD",
        "sequencer",
        "rebase-merge",
        "rebase-apply",
    ];

    /// <summary>Reads local tags, including the immutable tag object and peeled target IDs.</summary>
    public async Task<IReadOnlyList<TagSummary>> GetTagsAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["show-ref", "--tags", "--dereference"],
            cancellationToken,
            expectedExitCodes: [1]).ConfigureAwait(false);
        EnsureComplete(result, "tag list");
        return ParseTags(result.StandardOutput);
    }

    /// <summary>Creates an annotated tag using the repository's configured identity and signing policy.</summary>
    public async Task<TagSummary> CreateTagAsync(
        string root,
        string name,
        string targetCommitId,
        string? message,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var tagName = await ValidateTagNameAsync(repositoryRoot, name, cancellationToken).ConfigureAwait(false);
        ValidateCommitId(targetCommitId);
        var tagMessage = ValidateTagMessage(message);

        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var targetId = await ResolveCommitIdAsync(path, targetCommitId, cancellationToken).ConfigureAwait(false);
                await processRunner.RunAsync(
                    path,
                    ["tag", "--annotate", "--message", tagMessage, tagName, targetId],
                    cancellationToken).ConfigureAwait(false);

                return await ReadTagAsync(path, tagName, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Git created a tag that could not be read back.");
            }).ConfigureAwait(false);
    }

    /// <summary>Deletes a tag only when its object ID still matches the UI snapshot.</summary>
    public async Task DeleteTagAsync(
        string root,
        string name,
        string expectedObjectId,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var tagName = await ValidateTagNameAsync(repositoryRoot, name, cancellationToken).ConfigureAwait(false);
        ValidateObjectId(expectedObjectId, nameof(expectedObjectId));

        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var actualObjectId = await ReadTagObjectIdAsync(path, tagName, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualObjectId, expectedObjectId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new StaleTagException(tagName, expectedObjectId, actualObjectId);
                }

                // Delete the ref with Git's old-object-id guard. Reading the
                // object first gives callers a typed stale-tag error, while
                // update-ref makes the check part of the actual mutation so a
                // concurrent replacement cannot delete the wrong tag.
                try
                {
                    await processRunner.RunAsync(
                        path,
                        ["update-ref", "-d", $"refs/tags/{tagName}", expectedObjectId, "-m", "Delete tag"],
                        cancellationToken).ConfigureAwait(false);
                }
                catch (GitCommandException)
                {
                    // If the guarded update lost a race, report the same typed
                    // stale result as the initial read. Preserve unrelated Git
                    // failures such as a locked ref or an unavailable object.
                    var currentObjectId = await ReadTagObjectIdAsync(
                        path,
                        tagName,
                        cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(currentObjectId, expectedObjectId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new StaleTagException(tagName, expectedObjectId, currentObjectId);
                    }

                    throw;
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Undoes the current tip on its attached branch with a mixed reset, preserving
    /// the work tree.  An initial commit is removed from its symbolic branch ref
    /// and the index is emptied without touching work-tree files.
    /// </summary>
    public async Task<UndoCommitResult> UndoCommitAsync(
        string root,
        string expectedHeadId,
        CancellationToken cancellationToken)
    {
        ValidateExpectedHeadId(expectedHeadId);
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);

        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var status = await GetStatusAsync(path, cancellationToken).ConfigureAwait(false);
                var hasInProgressOperation = await HasInProgressOperationAsync(path, cancellationToken).ConfigureAwait(false);
                var state = CreateUndoRepositoryState(status, hasInProgressOperation);

                if (status.IsUnborn)
                {
                    throw CreateUndoBlockedException(
                        UndoCommitFailureReason.UnbornRepository,
                        state,
                        "Undo is unavailable because the repository has no current commit.");
                }

                if (!string.Equals(status.HeadId, expectedHeadId, StringComparison.OrdinalIgnoreCase))
                {
                    throw CreateUndoBlockedException(
                        UndoCommitFailureReason.StaleHead,
                        state,
                        "Undo was not applied because the current commit changed; refresh the repository first.");
                }

                if (status.IsDetached || string.IsNullOrEmpty(status.Branch))
                {
                    throw CreateUndoBlockedException(
                        UndoCommitFailureReason.DetachedHead,
                        state,
                        "Undo is available only on an attached local branch.");
                }

                if (state.HasInProgressOperation)
                {
                    throw CreateUndoBlockedException(
                        UndoCommitFailureReason.InProgressOperation,
                        state,
                        "Undo is unavailable while another Git operation is in progress.");
                }

                if (state.HasUnmergedChanges)
                {
                    throw CreateUndoBlockedException(
                        UndoCommitFailureReason.UnmergedChanges,
                        state,
                        "Undo is unavailable while the index contains unresolved conflicts.");
                }

                var head = await ReadHeadCommitAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(head.Id, expectedHeadId, StringComparison.OrdinalIgnoreCase))
                {
                    throw CreateUndoBlockedException(
                        UndoCommitFailureReason.StaleHead,
                        state,
                        "Undo was not applied because the current commit changed; refresh the repository first.");
                }

                var parentId = head.ParentIds.FirstOrDefault();
                if (parentId is not null)
                {
                    await processRunner.RunAsync(
                        path,
                        ["reset", "--mixed", parentId],
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var headRef = await ReadAttachedHeadRefAsync(path, cancellationToken).ConfigureAwait(false);
                    if (headRef is null)
                    {
                        throw CreateUndoBlockedException(
                            UndoCommitFailureReason.DetachedHead,
                            state,
                            "Undo is available only on an attached local branch.");
                    }

                    await processRunner.RunAsync(
                        path,
                        ["update-ref", "-d", headRef, expectedHeadId, "-m", "Undo initial commit"],
                        cancellationToken).ConfigureAwait(false);
                    await processRunner.RunAsync(
                        path,
                        ["read-tree", "--empty"],
                        cancellationToken).ConfigureAwait(false);
                }

                return new UndoCommitResult(
                    head.Id,
                    parentId,
                    head.Summary,
                    head.Description,
                    head.Author,
                    head.Date,
                    parentId is null,
                    state);
            }).ConfigureAwait(false);
    }

    private static IReadOnlyList<TagSummary> ParseTags(byte[] output)
    {
        var text = DecodeUtf8(output, "tag list");
        var accumulators = new Dictionary<string, TagAccumulator>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = rawLine.IndexOf(' ');
            if (separator <= 0 || separator == rawLine.Length - 1)
            {
                throw new InvalidOperationException("Git returned a malformed tag record.");
            }

            var objectId = rawLine[..separator];
            ValidateObjectId(objectId, "tag object ID");
            var refName = rawLine[(separator + 1)..];
            if (!refName.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Git returned a tag outside refs/tags.");
            }

            var isPeeled = refName.EndsWith("^{}", StringComparison.Ordinal);
            var tagName = isPeeled
                ? refName[10..^3]
                : refName[10..];
            ValidateParsedTagName(tagName);
            if (!accumulators.TryGetValue(tagName, out var accumulator))
            {
                accumulator = new TagAccumulator();
                accumulators.Add(tagName, accumulator);
                order.Add(tagName);
            }

            if (isPeeled)
            {
                if (accumulator.TargetId is not null)
                {
                    throw new InvalidOperationException("Git returned duplicate peeled tag records.");
                }

                accumulator.TargetId = objectId;
            }
            else
            {
                if (accumulator.ObjectId is not null)
                {
                    throw new InvalidOperationException("Git returned duplicate tag records.");
                }

                accumulator.ObjectId = objectId;
            }
        }

        var tags = new List<TagSummary>(order.Count);
        foreach (var name in order)
        {
            var accumulator = accumulators[name];
            if (accumulator.ObjectId is null)
            {
                throw new InvalidOperationException("Git returned a peeled tag without its tag object.");
            }

            tags.Add(new TagSummary(
                name,
                accumulator.ObjectId,
                accumulator.TargetId ?? accumulator.ObjectId,
                accumulator.TargetId is not null));
        }

        return tags;
    }

    private async Task<TagSummary?> ReadTagAsync(
        string repositoryRoot,
        string tagName,
        CancellationToken cancellationToken)
    {
        var objectId = await ReadTagObjectIdAsync(repositoryRoot, tagName, cancellationToken).ConfigureAwait(false);
        if (objectId is null)
        {
            return null;
        }

        var targetId = await ReadRefIdAsync(
            repositoryRoot,
            $"refs/tags/{tagName}^{{}}",
            cancellationToken).ConfigureAwait(false);
        return new TagSummary(tagName, objectId, targetId ?? objectId, targetId is not null);
    }

    private Task<string?> ReadTagObjectIdAsync(
        string repositoryRoot,
        string tagName,
        CancellationToken cancellationToken)
    {
        return ReadRefIdAsync(repositoryRoot, $"refs/tags/{tagName}", cancellationToken);
    }

    private async Task<string?> ReadRefIdAsync(
        string repositoryRoot,
        string reference,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "--quiet", "--end-of-options", reference],
            cancellationToken,
            expectedExitCodes: [1, 128]).ConfigureAwait(false);
        EnsureComplete(result, "ref lookup");
        if (result.ExitCode != 0)
        {
            return null;
        }

        var objectId = DecodeUtf8(result.StandardOutput, "ref lookup").Trim();
        ValidateObjectId(objectId, "ref object ID");
        return objectId;
    }

    private async Task<string> ResolveCommitIdAsync(
        string repositoryRoot,
        string commitId,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "--quiet", "--end-of-options", commitId + "^{commit}"],
            cancellationToken,
            expectedExitCodes: [1, 128]).ConfigureAwait(false);
        EnsureComplete(result, "commit lookup");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("The selected target is not a commit in this repository.");
        }

        var resolved = DecodeUtf8(result.StandardOutput, "commit lookup").Trim();
        ValidateObjectId(resolved, "resolved commit ID");
        return resolved;
    }

    private async Task<string> ValidateTagNameAsync(
        string repositoryRoot,
        string name,
        CancellationToken cancellationToken)
    {
        ValidateTagNameInput(name);
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["check-ref-format", "refs/tags/" + name],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "tag name validation");
        return name;
    }

    private static string ValidateTagMessage(string? message)
    {
        if (message?.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A tag message cannot contain NUL.", nameof(message));
        }

        return message ?? string.Empty;
    }

    private static void ValidateTagNameInput(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name[0] == '-'
            || name.IndexOf('\0') >= 0
            || name.Contains('\r')
            || name.Contains('\n'))
        {
            throw new ArgumentException("The tag name contains an invalid character.", nameof(name));
        }
    }

    private static void ValidateParsedTagName(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name[0] == '-'
            || name.IndexOf('\0') >= 0
            || name.Contains('\r')
            || name.Contains('\n'))
        {
            throw new InvalidOperationException("Git returned an invalid tag name.");
        }
    }

    private static void ValidateObjectId(string objectId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId, parameterName);
        if (!GitObjectIdPattern.IsMatch(objectId))
        {
            throw new ArgumentException("Git object IDs must contain 40 to 64 hexadecimal characters.", parameterName);
        }
    }

    private static void ValidateExpectedHeadId(string expectedHeadId)
    {
        ValidateObjectId(expectedHeadId, nameof(expectedHeadId));
    }

    private static UndoRepositoryState CreateUndoRepositoryState(
        RepositoryStatus status,
        bool hasInProgressOperation)
    {
        return new UndoRepositoryState(
            status.HeadId,
            status.Branch,
            status.IsDetached,
            status.IsUnborn,
            status.Changes.Any(change => change.WorkTreeStatus.Length > 0),
            status.Changes.Any(change => change.IndexStatus.Length > 0),
            status.Changes.Any(change =>
                change.Kind == ChangeKind.Conflicted
                || change.IndexStatus.Contains("U", StringComparison.Ordinal)
                || change.WorkTreeStatus.Contains("U", StringComparison.Ordinal)),
            hasInProgressOperation);
    }

    private static UndoCommitBlockedException CreateUndoBlockedException(
        UndoCommitFailureReason reason,
        UndoRepositoryState state,
        string message) =>
        new(reason, state, message);

    private async Task<bool> HasInProgressOperationAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        foreach (var pathName in UndoOperationPaths)
        {
            var path = await ReadGitPathAsync(repositoryRoot, pathName, cancellationToken).ConfigureAwait(false);
            if (path is not null && (File.Exists(path) || Directory.Exists(path)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string?> ReadGitPathAsync(
        string repositoryRoot,
        string pathName,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["rev-parse", "--git-path", pathName],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "Git operation state");
        var reportedPath = DecodeUtf8(result.StandardOutput, "Git operation state").Trim();
        if (reportedPath.Length == 0)
        {
            return null;
        }

        return Path.GetFullPath(
            Path.IsPathRooted(reportedPath)
                ? reportedPath
                : Path.Combine(repositoryRoot, reportedPath));
    }

    private async Task<string?> ReadAttachedHeadRefAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["symbolic-ref", "--quiet", "HEAD"],
            cancellationToken,
            expectedExitCodes: [1]).ConfigureAwait(false);
        EnsureComplete(result, "HEAD ref lookup");
        if (result.ExitCode != 0)
        {
            return null;
        }

        var headRef = DecodeUtf8(result.StandardOutput, "HEAD ref lookup").Trim();
        return headRef.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? headRef
            : null;
    }

    private async Task<HeadCommitMetadata> ReadHeadCommitAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            [
                "show",
                "--quiet",
                "--no-show-signature",
                "--format=%H%x00%P%x00%an%x00%aI%x00%B",
                "HEAD",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "HEAD commit metadata");

        var fields = DecodeUtf8(result.StandardOutput, "HEAD commit metadata")
            .Split('\0', 5);
        if (fields.Length != 5)
        {
            throw new InvalidOperationException("Git returned malformed HEAD commit metadata.");
        }

        var id = fields[0].Trim();
        ValidateObjectId(id, "HEAD commit ID");
        var parentIds = fields[1]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(parent =>
            {
                ValidateObjectId(parent, "parent commit ID");
                return parent;
            })
            .ToArray();
        var author = fields[2].TrimEnd('\r', '\n');
        if (author.Length == 0)
        {
            throw new InvalidOperationException("Git returned an empty commit author.");
        }

        var dateText = fields[3].Trim();
        if (!DateTimeOffset.TryParse(
                dateText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var date))
        {
            throw new InvalidOperationException("Git returned an invalid commit date.");
        }

        var message = fields[4].TrimEnd('\r', '\n');
        var separator = message.IndexOf('\n');
        var summary = separator < 0
            ? message.TrimEnd('\r')
            : message[..separator].TrimEnd('\r');
        var description = separator < 0
            ? string.Empty
            : message[(separator + 1)..].Trim('\r', '\n');
        if (summary.Length == 0)
        {
            throw new InvalidOperationException("Git returned an empty commit summary.");
        }

        return new HeadCommitMetadata(id, parentIds, summary, description, author, date);
    }

    private sealed class TagAccumulator
    {
        public string? ObjectId { get; set; }

        public string? TargetId { get; set; }
    }

    private sealed record HeadCommitMetadata(
        string Id,
        IReadOnlyList<string> ParentIds,
        string Summary,
        string Description,
        string Author,
        DateTimeOffset Date);
}
