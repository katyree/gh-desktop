namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    /// <summary>Reads every registered worktree using Git's NUL-delimited porcelain format.</summary>
    public async Task<IReadOnlyList<WorktreeSummary>> GetWorktreesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ReadWorktreesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds a clean destination worktree at an existing branch or commit.</summary>
    public async Task AddWorktreeAsync(
        string root,
        string newPath,
        string existingRef,
        string? newBranchName,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var destinationPath = NormalizeWorktreeArgument(repositoryRoot, newPath, nameof(newPath));
        var revision = ValidateRevisionArgument(existingRef, nameof(existingRef));
        string? branchName = null;
        if (newBranchName is not null)
        {
            branchName = await ValidateBranchNameAsync(repositoryRoot, newBranchName, cancellationToken).ConfigureAwait(false);
        }

        await ValidateExistingRevisionAsync(repositoryRoot, revision, cancellationToken).ConfigureAwait(false);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                EnsureWorktreeDestinationAvailable(destinationPath);
                var arguments = new List<string> { "worktree", "add" };
                if (branchName is not null)
                {
                    arguments.Add("-b");
                    arguments.Add(branchName);
                }

                arguments.Add(destinationPath);
                arguments.Add(revision);
                await processRunner.RunAsync(path, arguments, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Removes only a registered secondary worktree, without force.</summary>
    public async Task RemoveWorktreeAsync(
        string root,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var requestedPath = NormalizeWorktreeArgument(repositoryRoot, worktreePath, nameof(worktreePath));
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var worktrees = await ReadWorktreesAsync(path, cancellationToken).ConfigureAwait(false);
                var mainWorktree = worktrees.FirstOrDefault();
                if (mainWorktree is not null && IsSamePath(mainWorktree.Path, requestedPath))
                {
                    throw new InvalidOperationException("The main worktree cannot be removed by this operation.");
                }

                if (IsSamePath(path, requestedPath))
                {
                    throw new InvalidOperationException("The current worktree cannot be removed while it is active.");
                }

                var registered = worktrees.FirstOrDefault(worktree => IsSamePath(worktree.Path, requestedPath));
                if (registered is null)
                {
                    throw new InvalidOperationException("The selected path is not a registered secondary worktree.");
                }

                await processRunner.RunAsync(
                    path,
                    ["worktree", "remove", registered.Path],
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<WorktreeSummary>> ReadWorktreesAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["worktree", "list", "--porcelain", "-z"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "worktree list");
        return ParseWorktrees(result.StandardOutput);
    }

    private async Task ValidateExistingRevisionAsync(
        string repositoryRoot,
        string revision,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "--quiet", "--end-of-options", revision + "^{commit}"],
            cancellationToken,
            expectedExitCodes: [1]).ConfigureAwait(false);
        EnsureComplete(result, "worktree revision validation");
        var commitId = DecodeUtf8(result.StandardOutput, "worktree revision validation").Trim();
        if (result.ExitCode != 0 || !CommitIdPattern.IsMatch(commitId))
        {
            throw new ArgumentException("The worktree start reference does not resolve to a commit.", nameof(revision));
        }
    }

    private static IReadOnlyList<WorktreeSummary> ParseWorktrees(byte[] output)
    {
        var fields = DecodeUtf8(output, "worktree list")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var worktrees = new List<WorktreeSummary>();
        string? path = null;
        string headId = string.Empty;
        string? branch = null;
        var isLocked = false;
        var isPrunable = false;

        void AddCurrent()
        {
            if (path is null)
            {
                return;
            }

            if (path.Length == 0)
            {
                throw new InvalidOperationException("Git returned a worktree without a path.");
            }

            worktrees.Add(new WorktreeSummary(Path.GetFullPath(path), branch, headId, isLocked, isPrunable));
            path = null;
            headId = string.Empty;
            branch = null;
            isLocked = false;
            isPrunable = false;
        }

        foreach (var rawField in fields)
        {
            var field = rawField.Trim('\r', '\n');
            if (field.StartsWith("worktree ", StringComparison.Ordinal))
            {
                AddCurrent();
                path = field["worktree ".Length..];
            }
            else if (field.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                headId = field["HEAD ".Length..];
            }
            else if (field.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = field["branch refs/heads/".Length..];
            }
            else if (field is "locked" || field.StartsWith("locked ", StringComparison.Ordinal))
            {
                isLocked = true;
            }
            else if (field is "prunable" || field.StartsWith("prunable ", StringComparison.Ordinal))
            {
                isPrunable = true;
            }
        }

        AddCurrent();
        return worktrees;
    }

    private static string NormalizeWorktreeArgument(
        string repositoryRoot,
        string path,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.IndexOf('\0') >= 0 || path.Contains('\r') || path.Contains('\n'))
        {
            throw new ArgumentException("A worktree path cannot contain NUL or line breaks.", parameterName);
        }

        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(repositoryRoot, path));
        return TrimDirectorySeparator(fullPath);
    }

    private static void EnsureWorktreeDestinationAvailable(string destinationPath)
    {
        var parent = Directory.GetParent(destinationPath)?.FullName;
        if (parent is null || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("The worktree destination parent directory does not exist.");
        }

        if (File.Exists(destinationPath)
            || Directory.Exists(destinationPath)
            || HasFilesystemEntry(destinationPath))
        {
            throw new IOException("The worktree destination already exists.");
        }
    }

    private static bool HasFilesystemEntry(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsSamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            TrimDirectorySeparator(Path.GetFullPath(left)),
            TrimDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }

    private static string ValidateRevisionArgument(string revision, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision, parameterName);
        if (revision[0] == '-'
            || revision.IndexOf('\0') >= 0
            || revision.Contains('\r')
            || revision.Contains('\n'))
        {
            throw new ArgumentException("The Git revision contains an invalid character.", parameterName);
        }

        return revision;
    }
}
