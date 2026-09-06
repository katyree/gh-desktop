using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinGit.Core;

/// <summary>
/// Git repository reads and serialized mutations for the native first slice. All
/// process creation and Git wire-format parsing stays here so WinUI callers only
/// consume immutable DTOs and explicit operations.
/// </summary>
public sealed partial class GitRepositoryService
{
    private const int MaximumHistoryLimit = 1_000;
    private const int MaximumRenderedLines = 100_000;
    private const int MaximumRenderedLineLength = 5_000;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Regex CommitIdPattern = new("^[0-9a-fA-F]{4,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HunkHeaderPattern = new(
        "^@@ -(?<old>\\d+)(?:,\\d+)? \\+(?<new>\\d+)(?:,\\d+)? @@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly GitProcessRunner processRunner;
    private readonly IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides;
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    public GitRepositoryService(string? gitExecutable = null)
        : this(gitExecutable, environmentOverrides: null)
    {
    }

    internal GitRepositoryService(
        string? gitExecutable,
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        processRunner = new GitProcessRunner(
            string.IsNullOrWhiteSpace(gitExecutable) ? "git" : gitExecutable);
        gitEnvironmentOverrides = environmentOverrides is null
            ? null
            : new Dictionary<string, string?>(
                environmentOverrides,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Opens a repository or a directory below one and returns its status.</summary>
    public async Task<RepositoryStatus> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(path, cancellationToken).ConfigureAwait(false);
        return await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads branch, HEAD, ahead/behind and porcelain-v2 path status.</summary>
    public async Task<RepositoryStatus> GetStatusAsync(string root, CancellationToken cancellationToken)
    {
        var repositoryRoot = ValidateDirectory(root, nameof(root));
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["status", "--porcelain=v2", "--branch", "--untracked-files=all", "-z"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "status");

        return ParseStatus(repositoryRoot, result.StandardOutput);
    }

    /// <summary>Reads the combined HEAD-to-work-tree diff for one changed path.</summary>
    public async Task<FileDiff> GetWorkingDiffAsync(
        string root,
        FileChange file,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var repositoryRoot = ValidateDirectory(root, nameof(root));
        var path = ValidateGitPath(repositoryRoot, file.Path, nameof(file));

        // Status gives us a stable unborn/working-tree decision without assuming
        // that HEAD exists. An unborn repository has no commit to diff against.
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (status.IsUnborn || IsUntracked(file))
        {
            var untrackedDiff = await ReadUntrackedFileDiffAsync(repositoryRoot, path, cancellationToken).ConfigureAwait(false);
            return await EnrichImageDiffAsync(
                repositoryRoot,
                file,
                untrackedDiff,
                new ImageDiffRequest(ImageDiffScope.Working),
                cancellationToken).ConfigureAwait(false);
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
            "HEAD",
            "--",
            ToLiteralPathSpec(path),
        };
        AddOldPath(arguments, repositoryRoot, file.OldPath, path);

        var result = await processRunner.RunAsync(
            repositoryRoot,
            arguments,
            cancellationToken).ConfigureAwait(false);
        var diff = ParseDiff(result.StandardOutput, result.StandardOutputTruncated);
        diff = await EnrichDirtySubmoduleDiffAsync(
            repositoryRoot,
            file,
            diff,
            result.StandardOutput,
            result.StandardOutputTruncated,
            ImageDiffScope.Working,
            cancellationToken).ConfigureAwait(false);
        return await EnrichImageDiffAsync(
            repositoryRoot,
            file,
            diff,
            new ImageDiffRequest(ImageDiffScope.Working),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stages exactly the supplied repository-relative paths.</summary>
    public async Task StageFilesAsync(
        string root,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = ValidateDirectory(root, nameof(root));
        var normalizedPaths = ValidatePaths(repositoryRoot, paths);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var status = await GetStatusAsync(path, cancellationToken).ConfigureAwait(false);
                var unresolvedMarkerPaths = await FindUnresolvedConflictMarkerPathsAsync(
                    path,
                    normalizedPaths,
                    status,
                    cancellationToken).ConfigureAwait(false);
                if (unresolvedMarkerPaths.Count > 0)
                {
                    throw new UnresolvedConflictMarkersException(unresolvedMarkerPaths);
                }

                var arguments = new List<string> { "add", "--" };
                arguments.AddRange(normalizedPaths.Select(ToLiteralPathSpec));
                await processRunner.RunAsync(path, arguments, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Removes exactly the supplied paths from the index while preserving work-tree files.</summary>
    public async Task UnstageFilesAsync(
        string root,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = ValidateDirectory(root, nameof(root));
        var normalizedPaths = ValidatePaths(repositoryRoot, paths);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var arguments = new List<string> { "reset", "--" };
                arguments.AddRange(normalizedPaths.Select(ToLiteralPathSpec));
                await processRunner.RunAsync(path, arguments, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Reads the index-to-HEAD patch for one path.</summary>
    public Task<FileDiff> GetIndexDiffAsync(
        string root,
        FileChange file,
        CancellationToken cancellationToken)
    {
        return GetScopedDiffAsync(root, file, staged: true, cancellationToken);
    }

    /// <summary>Reads the work-tree-to-index patch for one path.</summary>
    public Task<FileDiff> GetUnstagedDiffAsync(
        string root,
        FileChange file,
        CancellationToken cancellationToken)
    {
        return GetScopedDiffAsync(root, file, staged: false, cancellationToken);
    }

    /// <summary>Creates a commit from the current index and returns its full SHA.</summary>
    public async Task<string> CommitAsync(
        string root,
        string summary,
        string? description,
        bool amend,
        CancellationToken cancellationToken,
        string? expectedHeadId = null)
    {
        var repositoryRoot = ValidateDirectory(root, nameof(root));
        var message = BuildCommitMessage(summary, description);
        if (expectedHeadId is not null)
        {
            ValidateCommitId(expectedHeadId);
        }

        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                if (amend && expectedHeadId is not null)
                {
                    var status = await GetStatusAsync(path, cancellationToken).ConfigureAwait(false);
                    if (status.IsUnborn
                        || !string.Equals(status.HeadId, expectedHeadId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new CommitMessageSnapshotStaleException(
                            "HEAD changed while the amend message was open. Refresh the repository and review the current commit before amending.");
                    }
                }

                var arguments = new List<string> { "commit", "--file=-" };
                if (amend)
                {
                    arguments.Add("--amend");
                }

                await processRunner.RunAsync(
                    path,
                    arguments,
                    cancellationToken,
                    standardInput: message).ConfigureAwait(false);
                var head = await processRunner.RunAsync(
                    path,
                    ["rev-parse", "HEAD"],
                    cancellationToken).ConfigureAwait(false);
                EnsureComplete(head, "commit identity");
                var commitId = DecodeUtf8(head.StandardOutput, "commit identity").Trim();
                ValidateCommitId(commitId);
                return commitId;
            }).ConfigureAwait(false);
    }

    /// <summary>Reads the subject and body of one hexadecimal commit ID.</summary>
    public async Task<CommitMessage> GetCommitMessageAsync(
        string root,
        string commitId,
        CancellationToken cancellationToken)
    {
        ValidateCommitId(commitId);
        var repositoryRoot = ValidateDirectory(root, nameof(root));
        var result = await processRunner.RunAsync(
            repositoryRoot,
            [
                "show",
                "--quiet",
                "--no-color",
                "--no-show-signature",
                "--format=%s%x00%b%x00",
                "--end-of-options",
                commitId,
                "--",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "commit message");

        var fields = DecodeUtf8(result.StandardOutput, "commit message")
            .Split('\0', StringSplitOptions.None);
        if (fields.Length < 2)
        {
            throw new InvalidOperationException("Git returned an invalid commit message record.");
        }

        return new CommitMessage(
            fields[0].TrimEnd('\r', '\n'),
            fields[1].TrimEnd('\r', '\n'));
    }

    /// <summary>Reads the effective local Git name and email without changing configuration.</summary>
    public async Task<CommitIdentity> GetCommitIdentityAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = ValidateDirectory(root, nameof(root));
        var name = await ReadGitConfigValueAsync(repositoryRoot, "user.name", cancellationToken).ConfigureAwait(false);
        var email = await ReadGitConfigValueAsync(repositoryRoot, "user.email", cancellationToken).ConfigureAwait(false);
        return new CommitIdentity(name, email);
    }

    private async Task<FileDiff> GetScopedDiffAsync(
        string root,
        FileChange file,
        bool staged,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var repositoryRoot = ValidateDirectory(root, nameof(root));
        var path = ValidateGitPath(repositoryRoot, file.Path, nameof(file));
        if (!staged && IsUntracked(file))
        {
            var untrackedDiff = await ReadUntrackedFileDiffAsync(repositoryRoot, path, cancellationToken).ConfigureAwait(false);
            return await EnrichImageDiffAsync(
                repositoryRoot,
                file,
                untrackedDiff,
                new ImageDiffRequest(ImageDiffScope.Unstaged),
                cancellationToken).ConfigureAwait(false);
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
        };
        if (staged)
        {
            arguments.Add("--cached");
        }

        arguments.Add("--");
        arguments.Add(ToLiteralPathSpec(path));
        AddOldPath(arguments, repositoryRoot, file.OldPath, path);

        var result = await processRunner.RunAsync(
            repositoryRoot,
            arguments,
            cancellationToken).ConfigureAwait(false);
        var diff = ParseDiff(result.StandardOutput, result.StandardOutputTruncated);
        diff = await EnrichDirtySubmoduleDiffAsync(
            repositoryRoot,
            file,
            diff,
            result.StandardOutput,
            result.StandardOutputTruncated,
            staged ? ImageDiffScope.Index : ImageDiffScope.Unstaged,
            cancellationToken).ConfigureAwait(false);
        return await EnrichImageDiffAsync(
            repositoryRoot,
            file,
            diff,
            new ImageDiffRequest(staged ? ImageDiffScope.Index : ImageDiffScope.Unstaged),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadGitConfigValueAsync(
        string repositoryRoot,
        string key,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["config", "--get", key],
            cancellationToken,
            expectedExitCodes: [1]).ConfigureAwait(false);
        EnsureComplete(result, $"{key} lookup");
        return DecodeUtf8(result.StandardOutput, $"{key} lookup").Trim();
    }

    private async Task ExecuteMutationAsync(
        string repositoryRoot,
        CancellationToken cancellationToken,
        Func<string, Task> mutation)
    {
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                await mutation(path).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
    }

    private async Task<T> ExecuteMutationAsync<T>(
        string repositoryRoot,
        CancellationToken cancellationToken,
        Func<string, Task<T>> mutation)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await mutation(repositoryRoot).ConfigureAwait(false);
        }
        finally
        {
            // The caller owns the post-mutation status refresh. It can use a fresh
            // token and report a cancelled operation separately from that refresh.
            mutationGate.Release();
        }
    }

    private static string[] ValidatePaths(string repositoryRoot, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("At least one path must be selected.", nameof(paths));
        }

        var normalizedPaths = new List<string>(paths.Count);
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var normalizedPath = ValidateGitPath(repositoryRoot, path, nameof(paths));
            if (seen.Add(normalizedPath))
            {
                normalizedPaths.Add(normalizedPath);
            }
        }

        return normalizedPaths.ToArray();
    }

    private static string BuildCommitMessage(string summary, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (summary.IndexOf('\0') >= 0 || summary.Contains('\r') || summary.Contains('\n'))
        {
            throw new ArgumentException("The commit summary must be one line and cannot contain NUL.", nameof(summary));
        }

        var normalizedSummary = summary.Trim();
        if (normalizedSummary.Length == 0)
        {
            throw new ArgumentException("The commit summary cannot be empty.", nameof(summary));
        }

        if (description?.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("The commit description cannot contain NUL.", nameof(description));
        }

        var normalizedDescription = description?
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim() ?? string.Empty;
        return normalizedDescription.Length == 0
            ? normalizedSummary + "\n"
            : normalizedSummary + "\n\n" + normalizedDescription + "\n";
    }

    /// <summary>Reads a bounded first page of history, including the root commit.</summary>
    public async Task<IReadOnlyList<CommitSummary>> GetHistoryAsync(
        string root,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumHistoryLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), $"History limit must be between 1 and {MaximumHistoryLimit}.");
        }

        var repositoryRoot = ValidateDirectory(root, nameof(root));
        var repositoryStatus = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (repositoryStatus.IsUnborn)
        {
            return [];
        }

        var result = await processRunner.RunAsync(
            repositoryRoot,
            [
                "log",
                "--no-color",
                "--no-show-signature",
                "--date=iso-strict",
                "--format=%H%x00%h%x00%an%x00%aI%x00%s%x00",
                $"--max-count={limit}",
                "--",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "history");

        return ParseHistory(result.StandardOutput);
    }

    /// <summary>Reads the changed paths for one hexadecimal commit ID.</summary>
    public async Task<IReadOnlyList<FileChange>> GetCommitFilesAsync(
        string root,
        string commitId,
        CancellationToken cancellationToken)
    {
        ValidateCommitId(commitId);
        var repositoryRoot = ValidateDirectory(root, nameof(root));
        var result = await processRunner.RunAsync(
            repositoryRoot,
            [
                "diff-tree",
                "--root",
                "--no-commit-id",
                "--name-status",
                "-z",
                "-r",
                "-M",
                "-C",
                "--no-ext-diff",
                "--no-textconv",
                "--no-color",
                "--diff-merges=first-parent",
                commitId,
                "--",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "commit file list");

        return ParseCommitFiles(repositoryRoot, result.StandardOutput);
    }

    /// <summary>Reads a bounded patch for one path in one hexadecimal commit.</summary>
    public async Task<FileDiff> GetCommitDiffAsync(
        string root,
        string commitId,
        FileChange file,
        CancellationToken cancellationToken)
    {
        ValidateCommitId(commitId);
        ArgumentNullException.ThrowIfNull(file);
        var repositoryRoot = ValidateDirectory(root, nameof(root));
        var path = ValidateGitPath(repositoryRoot, file.Path, nameof(file));
        var arguments = new List<string>
        {
            "show",
            "--format=",
            "--no-ext-diff",
            "--no-textconv",
            "--no-color",
            "--binary",
            "--patch",
            "--unified=3",
            "--find-renames",
            "--diff-merges=first-parent",
            commitId,
            "--",
            ToLiteralPathSpec(path),
        };
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
            new ImageDiffRequest(ImageDiffScope.Commit, BaselineId: commitId),
            cancellationToken).ConfigureAwait(false);
    }

    private static RepositoryStatus ParseStatus(string repositoryRoot, byte[] output)
    {
        var text = DecodeUtf8(output, "status");
        var records = text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var branch = string.Empty;
        var headId = string.Empty;
        var upstream = string.Empty;
        var ahead = 0;
        var behind = 0;
        var isDetached = false;
        var isUnborn = false;
        var changes = new List<FileChange>();

        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                var value = record[13..].Trim();
                if (string.Equals(value, "(initial)", StringComparison.OrdinalIgnoreCase))
                {
                    isUnborn = true;
                }
                else if (!string.Equals(value, "(unknown)", StringComparison.OrdinalIgnoreCase))
                {
                    headId = value;
                }

                continue;
            }

            if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var value = record[14..].Trim();
                if (string.Equals(value, "(detached)", StringComparison.OrdinalIgnoreCase))
                {
                    isDetached = true;
                }
                else if (!string.Equals(value, "(unknown)", StringComparison.OrdinalIgnoreCase))
                {
                    branch = value;
                }

                continue;
            }

            if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = record[18..].Trim();
                continue;
            }

            if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                ParseAheadBehind(record[12..], out ahead, out behind);
                continue;
            }

            if (record.StartsWith("# ", StringComparison.Ordinal))
            {
                continue;
            }

            var type = record.Length == 0 ? '\0' : record[0];
            if (type is '1' or '2' or 'u')
            {
                var tokenCount = type switch
                {
                    '1' => 8,
                    '2' => 9,
                    _ => 10,
                };
                if (!TryReadFixedPrefix(record, tokenCount, out var tokens, out var pathText))
                {
                    throw new InvalidOperationException("Git returned an invalid porcelain-v2 status record.");
                }

                if (tokens.Length < 2 || tokens[1].Length != 2)
                {
                    throw new InvalidOperationException("Git returned an invalid porcelain-v2 status code.");
                }

                var path = ValidateGitPath(repositoryRoot, pathText, "status path");
                var oldPath = (string?)null;
                if (type == '2')
                {
                    if (index + 1 >= records.Length)
                    {
                        throw new InvalidOperationException("Git returned an incomplete rename status record.");
                    }

                    oldPath = ValidateGitPath(repositoryRoot, records[++index], "status original path");
                }

                var indexStatus = NormalizeStatus(tokens[1][0]);
                var workTreeStatus = NormalizeStatus(tokens[1][1]);
                var kind = MapChangeKind(indexStatus, workTreeStatus, type);
                changes.Add(new FileChange(path, kind, indexStatus, workTreeStatus, oldPath));
                continue;
            }

            if (type is '?' or '!')
            {
                if (!TryReadFixedPrefix(record, 1, out _, out var pathText))
                {
                    throw new InvalidOperationException("Git returned an invalid untracked status record.");
                }

                var path = ValidateGitPath(repositoryRoot, pathText, "status path");
                var status = type == '?' ? "?" : "!";
                changes.Add(new FileChange(
                    path,
                    type == '?' ? ChangeKind.Untracked : ChangeKind.Unknown,
                    status,
                    status));
                continue;
            }

            throw new InvalidOperationException("Git returned an unsupported porcelain-v2 status record.");
        }

        if (isUnborn)
        {
            headId = string.Empty;
        }

        return new RepositoryStatus(repositoryRoot, branch, headId, changes)
        {
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            IsDetached = isDetached,
            IsUnborn = isUnborn,
        };
    }

    private static IReadOnlyList<CommitSummary> ParseHistory(byte[] output)
    {
        var text = DecodeUtf8(output, "history");
        var fields = text.Split('\0', StringSplitOptions.None);
        var commits = new List<CommitSummary>();

        for (var index = 0; index + 4 < fields.Length; index += 5)
        {
            var id = fields[index].Trim('\r', '\n', ' ', '\t');
            if (id.Length == 0)
            {
                continue;
            }

            var shortId = fields[index + 1].Trim('\r', '\n', ' ', '\t');
            var author = fields[index + 2].Trim('\r', '\n');
            var dateText = fields[index + 3].Trim('\r', '\n', ' ', '\t');
            var summary = fields[index + 4].TrimEnd('\r', '\n');
            if (!CommitIdPattern.IsMatch(id)
                || !DateTimeOffset.TryParse(
                    dateText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var date))
            {
                throw new InvalidOperationException("Git returned an invalid history record.");
            }

            commits.Add(new CommitSummary(id, shortId, summary, author, date));
        }

        return commits;
    }

    private static IReadOnlyList<FileChange> ParseCommitFiles(string repositoryRoot, byte[] output)
    {
        var text = DecodeUtf8(output, "commit file list");
        var records = text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<FileChange>();

        for (var index = 0; index < records.Length;)
        {
            var status = records[index++].Trim('\r', '\n', ' ', '\t');
            if (status.Length == 0)
            {
                continue;
            }

            if (index >= records.Length)
            {
                throw new InvalidOperationException("Git returned an incomplete commit file record.");
            }

            var firstPath = ValidateGitPath(repositoryRoot, records[index++], "commit path");
            var kind = MapCommitChangeKind(status[0]);
            string path;
            string? oldPath = null;
            if (status[0] is 'R' or 'C')
            {
                oldPath = firstPath;
                if (index >= records.Length)
                {
                    throw new InvalidOperationException("Git returned an incomplete rename commit record.");
                }

                path = ValidateGitPath(repositoryRoot, records[index++], "commit path");
            }
            else
            {
                path = firstPath;
            }

            files.Add(new FileChange(path, kind, status[0].ToString(), string.Empty, oldPath));
        }

        return files;
    }

    private static FileDiff ParseDiff(byte[] output, bool outputTruncated)
    {
        if (outputTruncated)
        {
            return new FileDiff(
                [],
                isBinary: false,
                isTruncated: true,
                "The diff exceeds the native viewer limit and was not rendered.");
        }

        if (output.Length == 0)
        {
            return new FileDiff([], isBinary: false, isTruncated: false);
        }

        if (output.AsSpan().IndexOf((byte)0) >= 0)
        {
            return new FileDiff([], isBinary: true, isTruncated: false, "Binary file cannot be rendered.");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(output);
        }
        catch (DecoderFallbackException)
        {
            return new FileDiff([], isBinary: true, isTruncated: false, "Binary file cannot be rendered.");
        }

        var submoduleComparison = TryParseSubmoduleComparison(text);
        var lines = new List<DiffLine>();
        var oldLineNumber = 0;
        var newLineNumber = 0;
        var hasHunk = false;
        var renderedLineCount = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            renderedLineCount++;
            if (renderedLineCount > MaximumRenderedLines || line.Length > MaximumRenderedLineLength)
            {
                return new FileDiff(
                    [],
                    isBinary: false,
                    isTruncated: true,
                    "The diff contains too many or too-long lines and was not rendered.");
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                hasHunk = false;
                oldLineNumber = 0;
                newLineNumber = 0;
                lines.Add(new DiffLine(null, null, DiffLineKind.FileHeader, line));
                continue;
            }

            if (!hasHunk
                && (line.StartsWith("Binary files ", StringComparison.Ordinal)
                    || line.StartsWith("GIT binary patch", StringComparison.Ordinal)))
            {
                return new FileDiff([], isBinary: true, isTruncated: false, "Binary file cannot be rendered.");
            }

            var hunkMatch = HunkHeaderPattern.Match(line);
            if (hunkMatch.Success)
            {
                oldLineNumber = int.Parse(hunkMatch.Groups["old"].Value, CultureInfo.InvariantCulture);
                newLineNumber = int.Parse(hunkMatch.Groups["new"].Value, CultureInfo.InvariantCulture);
                hasHunk = true;
                lines.Add(new DiffLine(null, null, DiffLineKind.HunkHeader, line));
                continue;
            }

            if (!hasHunk
                && (line.StartsWith("Binary files ", StringComparison.Ordinal)
                    || line.StartsWith("GIT binary patch", StringComparison.Ordinal)))
            {
                return new FileDiff([], isBinary: true, isTruncated: false, "Binary file cannot be rendered.");
            }

            if (hasHunk && line.Length > 0 && line[0] == ' ')
            {
                lines.Add(new DiffLine(oldLineNumber, newLineNumber, DiffLineKind.Context, line[1..]));
                oldLineNumber++;
                newLineNumber++;
                continue;
            }

            if (hasHunk && line.Length > 0 && line[0] == '+')
            {
                lines.Add(new DiffLine(null, newLineNumber, DiffLineKind.Added, line[1..]));
                newLineNumber++;
                continue;
            }

            if (hasHunk && line.Length > 0 && line[0] == '-')
            {
                lines.Add(new DiffLine(oldLineNumber, null, DiffLineKind.Removed, line[1..]));
                oldLineNumber++;
                continue;
            }

            if (line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(null, null, DiffLineKind.NoNewline, line));
                continue;
            }

            lines.Add(new DiffLine(null, null, DiffLineKind.FileHeader, line));
        }

        return new FileDiff(
            lines,
            isBinary: false,
            isTruncated: false,
            submoduleComparison: submoduleComparison);
    }

    private static async Task<FileDiff> ReadUntrackedFileDiffAsync(
        string repositoryRoot,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = GetFullPathForGitPath(repositoryRoot, path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return new FileDiff([], isBinary: false, isTruncated: false, "The file is no longer present.");
        }

        try
        {
            if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
            {
                return new FileDiff(
                    [],
                    isBinary: false,
                    isTruncated: false,
                    "Symbolic-link and reparse-point diffs are not rendered by the native first slice.");
            }
        }
        catch (IOException)
        {
            return new FileDiff([], isBinary: false, isTruncated: false, "The file could not be inspected.");
        }
        catch (UnauthorizedAccessException)
        {
            return new FileDiff([], isBinary: false, isTruncated: false, "The file could not be inspected.");
        }

        if (!File.Exists(fullPath))
        {
            return new FileDiff([], isBinary: false, isTruncated: false, "The path is a directory and cannot be rendered as a file.");
        }

        var bounded = await ReadBoundedFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (bounded.Truncated)
        {
            return new FileDiff(
                [],
                isBinary: false,
                isTruncated: true,
                "The file exceeds the native viewer limit and was not rendered.");
        }

        if (bounded.Bytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            return new FileDiff([], isBinary: true, isTruncated: false, "Binary file cannot be rendered.");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bounded.Bytes);
        }
        catch (DecoderFallbackException)
        {
            return new FileDiff([], isBinary: true, isTruncated: false, "Binary file cannot be rendered.");
        }

        var lines = new List<DiffLine>();
        var lineNumber = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (lineNumber > MaximumRenderedLines || line.Length > MaximumRenderedLineLength)
            {
                return new FileDiff(
                    [],
                    isBinary: false,
                    isTruncated: true,
                    "The file contains too many or too-long lines and was not rendered.");
            }

            lines.Add(new DiffLine(null, lineNumber, DiffLineKind.Added, line));
        }

        return new FileDiff(lines, isBinary: false, isTruncated: false);
    }

    private static async Task<BoundedFile> ReadBoundedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        using var bytes = new MemoryStream(capacity: 64 * 1024);
        var buffer = new byte[64 * 1024];
        var truncated = false;
        var maximumBytesToProbe = GitProcessRunner.MaxOutputBytes + 1L;
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var remaining = maximumBytesToProbe - bytes.Length;
            if (remaining > 0)
            {
                var toCopy = (int)Math.Min(remaining, count);
                bytes.Write(buffer, 0, toCopy);
                truncated |= bytes.Length > GitProcessRunner.MaxOutputBytes || toCopy != count;
                if (bytes.Length > GitProcessRunner.MaxOutputBytes)
                {
                    break;
                }
            }
            else
            {
                truncated = true;
                break;
            }
        }

        return new BoundedFile(bytes.ToArray(), truncated);
    }

    private static void AddOldPath(List<string> arguments, string repositoryRoot, string? oldPath, string path)
    {
        if (!string.IsNullOrEmpty(oldPath))
        {
            var normalizedOldPath = ValidateGitPath(repositoryRoot, oldPath, nameof(oldPath));
            if (!string.Equals(normalizedOldPath, path, StringComparison.Ordinal))
            {
                arguments.Add(ToLiteralPathSpec(normalizedOldPath));
            }
        }
    }

    private static string NormalizeRepositoryRoot(string reportedRoot, string candidate)
    {
        if (string.IsNullOrWhiteSpace(reportedRoot))
        {
            throw new InvalidOperationException("Git did not report a repository root.");
        }

        var root = Path.IsPathRooted(reportedRoot)
            ? Path.GetFullPath(reportedRoot)
            : Path.GetFullPath(Path.Combine(candidate, reportedRoot));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("Git reported a repository root that is not present.");
        }

        return TrimDirectorySeparator(root);
    }

    private static string ValidateDirectory(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        var fullPath = TrimDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory '{fullPath}' does not exist.");
        }

        return fullPath;
    }

    private static string ValidateGitPath(string repositoryRoot, string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A Git path cannot contain NUL.", parameterName);
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/')
            || Regex.IsMatch(normalized, "^[a-zA-Z]:/", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Git paths must be relative to the repository root.", parameterName);
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException("Git paths cannot contain dot traversal segments.", parameterName);
            }
        }

        var fullPath = GetFullPathForGitPath(repositoryRoot, normalized);
        var relativePath = Path.GetRelativePath(repositoryRoot, fullPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (Path.IsPathRooted(relativePath)
            || string.Equals(relativePath, "..", comparison)
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, comparison))
        {
            throw new ArgumentException("The Git path is outside the repository root.", parameterName);
        }

        return normalized;
    }

    private static string GetFullPathForGitPath(string repositoryRoot, string path)
    {
        return Path.GetFullPath(Path.Combine(repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string ToLiteralPathSpec(string path) => ":(literal)" + path;

    private static string TrimDirectorySeparator(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string DecodeUtf8(byte[] bytes, string operation)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException($"Git returned invalid UTF-8 for {operation}.", exception);
        }
    }

    private static void EnsureComplete(GitProcessResult result, string operation)
    {
        if (result.StandardOutputTruncated)
        {
            throw new GitOutputLimitException(operation);
        }
    }

    private static bool IsUntracked(FileChange file) =>
        file.Kind == ChangeKind.Untracked
        || file.IndexStatus == "?"
        || file.WorkTreeStatus == "?";

    private static bool TryReadFixedPrefix(
        string record,
        int tokenCount,
        out string[] tokens,
        out string remainder)
    {
        var parsedTokens = new string[tokenCount];
        var offset = 0;
        for (var index = 0; index < tokenCount; index++)
        {
            var separator = record.IndexOf(' ', offset);
            if (separator < 0)
            {
                tokens = [];
                remainder = string.Empty;
                return false;
            }

            parsedTokens[index] = record[offset..separator];
            offset = separator + 1;
        }

        tokens = parsedTokens;
        remainder = record[offset..];
        return remainder.Length > 0;
    }

    private static string NormalizeStatus(char status) => status is '.' or ' ' ? string.Empty : status.ToString();

    private static ChangeKind MapChangeKind(string indexStatus, string workTreeStatus, char recordType)
    {
        if (recordType == 'u' || indexStatus.Contains('U') || workTreeStatus.Contains('U'))
        {
            return ChangeKind.Conflicted;
        }

        if (recordType == '2' || indexStatus.Contains('R') || workTreeStatus.Contains('R'))
        {
            return indexStatus.Contains('C') || workTreeStatus.Contains('C')
                ? ChangeKind.Copied
                : ChangeKind.Renamed;
        }

        if (indexStatus.Contains('C') || workTreeStatus.Contains('C'))
        {
            return ChangeKind.Copied;
        }

        if (indexStatus.Contains('A') || workTreeStatus.Contains('A'))
        {
            return ChangeKind.Added;
        }

        if (indexStatus.Contains('D') || workTreeStatus.Contains('D'))
        {
            return ChangeKind.Deleted;
        }

        if (indexStatus.Contains('T') || workTreeStatus.Contains('T'))
        {
            return ChangeKind.TypeChanged;
        }

        if (indexStatus.Contains('M') || workTreeStatus.Contains('M'))
        {
            return ChangeKind.Modified;
        }

        return ChangeKind.Unknown;
    }

    private static ChangeKind MapCommitChangeKind(char status) => status switch
    {
        'A' => ChangeKind.Added,
        'M' => ChangeKind.Modified,
        'D' => ChangeKind.Deleted,
        'R' => ChangeKind.Renamed,
        'C' => ChangeKind.Copied,
        'T' => ChangeKind.TypeChanged,
        'U' => ChangeKind.Conflicted,
        _ => ChangeKind.Unknown,
    };

    internal static void ParseAheadBehind(string text, out int ahead, out int behind)
    {
        ahead = 0;
        behind = 0;
        var values = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (values.Length >= 2)
        {
            if (int.TryParse(values[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedAhead))
            {
                ahead = Math.Max(parsedAhead, 0);
            }

            if (int.TryParse(values[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedBehind))
            {
                behind = (int)Math.Min(Math.Abs((long)parsedBehind), int.MaxValue);
            }
        }
    }

    private static void ValidateCommitId(string commitId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitId);
        if (!CommitIdPattern.IsMatch(commitId))
        {
            throw new ArgumentException("Commit IDs must contain only hexadecimal characters.", nameof(commitId));
        }
    }

    private sealed record BoundedFile(byte[] Bytes, bool Truncated);
}
