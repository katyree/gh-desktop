using System.Globalization;
using System.Text.RegularExpressions;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private static readonly Regex BranchTrackAheadPattern = new(
        @"ahead (\d+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BranchTrackBehindPattern = new(
        @"behind (\d+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Reads local branches and their configured tracking/worktree metadata.</summary>
    public async Task<IReadOnlyList<BranchSummary>> GetBranchesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var result = await processRunner.RunAsync(
            repositoryRoot,
            [
                "for-each-ref",
                "--format=%(refname:short)%00%(refname)%00%(HEAD)%00%(upstream:short)%00%(upstream:remotename)%00%(upstream:track)%00%(worktreepath)%00",
                "refs/heads",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "branch list");

        return ParseBranches(repositoryRoot, result.StandardOutput);
    }

    /// <summary>Creates a local branch at the current HEAD or an explicit start point.</summary>
    public async Task CreateBranchAsync(
        string root,
        string name,
        string? startPoint,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var branchName = await ValidateBranchNameAsync(repositoryRoot, name, cancellationToken).ConfigureAwait(false);
        var normalizedStartPoint = ValidateStartPoint(startPoint);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var arguments = new List<string> { "branch", branchName };
                if (normalizedStartPoint is not null)
                {
                    arguments.Add(normalizedStartPoint);
                }

                await processRunner.RunAsync(path, arguments, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Renames a local branch without changing its worktree.</summary>
    public Task RenameBranchAsync(
        string root,
        string currentName,
        string newName,
        CancellationToken cancellationToken)
    {
        return RenameBranchAsync(root, currentName, newName, expectedContext: null, cancellationToken);
    }

    /// <summary>Renames a local branch after revalidating a captured dialog context.</summary>
    public async Task RenameBranchAsync(
        string root,
        string currentName,
        string newName,
        BranchMutationContext? expectedContext,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var normalizedCurrentName = await ValidateBranchNameAsync(repositoryRoot, currentName, cancellationToken).ConfigureAwait(false);
        var normalizedNewName = await ValidateBranchNameAsync(repositoryRoot, newName, cancellationToken).ConfigureAwait(false);
        if (string.Equals(normalizedCurrentName, normalizedNewName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The new branch name must be different from the current branch name.", nameof(newName));
        }

        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var sourceTip = await ReadRefIdAsync(
                    path,
                    $"refs/heads/{normalizedCurrentName}",
                    cancellationToken).ConfigureAwait(false);
                if (sourceTip is null)
                {
                    throw new InvalidOperationException($"The branch '{normalizedCurrentName}' no longer exists; refresh the branch list and try again.");
                }

                ValidateBranchMutationContext(path, normalizedCurrentName, sourceTip, expectedContext);

                var collisionTip = await ReadRefIdAsync(
                    path,
                    $"refs/heads/{normalizedNewName}",
                    cancellationToken).ConfigureAwait(false);
                // On a case-insensitive filesystem rev-parse can resolve the new
                // name through the source ref itself, which is a case-only rename
                // rather than a collision. Only an exact (case-sensitive) branch
                // name match counts as a collision, matching the Electron retry
                // behavior for case-only renames.
                var forceSpellingChange = false;
                if (collisionTip is not null)
                {
                    var existingNames = await ReadLocalBranchShortNamesAsync(path, cancellationToken).ConfigureAwait(false);
                    if (existingNames.Contains(normalizedNewName))
                    {
                        throw new InvalidOperationException($"A branch named '{normalizedNewName}' already exists; choose a different name.");
                    }

                    forceSpellingChange = true;
                }

                await EnsureBranchNotCheckedOutElsewhereAsync(
                    path,
                    normalizedCurrentName,
                    "rename",
                    cancellationToken).ConfigureAwait(false);

                // forceSpellingChange was decided above: only a rev-parse hit without
                // an exact branch-name match (a case-only rename on a
                // case-insensitive filesystem) forces the spelling change.
                await processRunner.RunAsync(
                    path,
                    ["branch", forceSpellingChange ? "-M" : "-m", normalizedCurrentName, normalizedNewName],
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Deletes a local branch, using safe -d unless force is explicitly requested.</summary>
    public Task DeleteBranchAsync(
        string root,
        string name,
        bool force,
        CancellationToken cancellationToken)
    {
        return DeleteBranchAsync(root, name, force, expectedContext: null, cancellationToken);
    }

    /// <summary>Deletes a local branch after revalidating a captured dialog context.</summary>
    /// <remarks>
    /// Safe deletion never silently becomes forced deletion: callers pass
    /// <c>force: false</c> and Git refuses when the branch has unmerged commits.
    /// Forced deletion is only available through an explicit <c>force: true</c>
    /// choice and the native UI does not offer one.
    /// </remarks>
    public async Task DeleteBranchAsync(
        string root,
        string name,
        bool force,
        BranchMutationContext? expectedContext,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var branchName = await ValidateBranchNameAsync(repositoryRoot, name, cancellationToken).ConfigureAwait(false);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var branchTip = await ReadRefIdAsync(
                    path,
                    $"refs/heads/{branchName}",
                    cancellationToken).ConfigureAwait(false);
                if (branchTip is null)
                {
                    throw new InvalidOperationException($"The branch '{branchName}' no longer exists; refresh the branch list and try again.");
                }

                ValidateBranchMutationContext(path, branchName, branchTip, expectedContext);

                var currentBranch = await ReadCurrentBranchAsync(path, cancellationToken).ConfigureAwait(false);
                if (string.Equals(currentBranch, branchName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"The branch '{branchName}' is currently checked out; switch to another branch before deleting it.");
                }

                var worktreePath = await ReadBranchWorktreePathAsync(path, branchName, cancellationToken).ConfigureAwait(false);
                if (worktreePath.Length != 0)
                {
                    throw new InvalidOperationException($"The branch '{branchName}' is checked out in the worktree at '{worktreePath}'; remove or switch that worktree before deleting it.");
                }

                var defaultBranchName = await TryGetDefaultBranchNameAsync(path, cancellationToken).ConfigureAwait(false);
                if (string.Equals(defaultBranchName, branchName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"The branch '{branchName}' appears to be the default branch; deleting it locally is not allowed from this view.");
                }

                await processRunner.RunAsync(
                    path,
                    ["branch", force ? "-D" : "-d", branchName],
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the repository default branch from a remote HEAD symbolic ref,
    /// preferring <c>origin</c>. Returns null when no remote HEAD is configured
    /// so callers never assume protection information that is unavailable.
    /// </summary>
    public async Task<string?> TryGetDefaultBranchNameAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var remotes = await GetRemotesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        foreach (var remote in remotes
                     .OrderByDescending(candidate => string.Equals(candidate.Name, "origin", StringComparison.Ordinal))
                     .ThenBy(candidate => candidate.Name, StringComparer.Ordinal))
        {
            if (!RemoteNamePattern.IsMatch(remote.Name))
            {
                continue;
            }

            var target = await ReadRemoteHeadTargetAsync(repositoryRoot, remote.Name, cancellationToken).ConfigureAwait(false);
            if (target is not null)
            {
                return target;
            }
        }

        return null;
    }

    private async Task<string?> ReadRemoteHeadTargetAsync(
        string repositoryRoot,
        string remoteName,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["symbolic-ref", "--quiet", $"refs/remotes/{remoteName}/HEAD"],
            cancellationToken,
            expectedExitCodes: [1]).ConfigureAwait(false);
        EnsureComplete(result, "remote HEAD lookup");
        if (result.ExitCode != 0)
        {
            return null;
        }

        var target = DecodeUtf8(result.StandardOutput, "remote HEAD lookup").Trim();
        var prefix = $"refs/remotes/{remoteName}/";
        if (!target.StartsWith(prefix, StringComparison.Ordinal) || target.Length == prefix.Length)
        {
            return null;
        }

        return target[prefix.Length..];
    }

    private async Task<string?> ReadCurrentBranchAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["symbolic-ref", "--quiet", "--short", "HEAD"],
            cancellationToken,
            expectedExitCodes: [1]).ConfigureAwait(false);
        EnsureComplete(result, "current branch lookup");
        if (result.ExitCode != 0)
        {
            return null;
        }

        var branch = DecodeUtf8(result.StandardOutput, "current branch lookup").Trim();
        return branch.Length == 0 ? null : branch;
    }

    private async Task<string> ReadBranchWorktreePathAsync(
        string repositoryRoot,
        string branchName,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["for-each-ref", "--format=%(worktreepath)", $"refs/heads/{branchName}"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "branch worktree lookup");
        return DecodeUtf8(result.StandardOutput, "branch worktree lookup").Trim();
    }

    /// <summary>
    /// Reads exact (case-sensitive) local branch short names for collision checks.
    /// </summary>
    private async Task<HashSet<string>> ReadLocalBranchShortNamesAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["for-each-ref", "--format=%(refname:short)", "refs/heads"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "branch name list");
        return new HashSet<string>(
            DecodeUtf8(result.StandardOutput, "branch name list")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Refuses to rename a branch that is checked out in another worktree.
    /// Renaming the branch checked out in the current worktree stays allowed.
    /// </summary>
    private async Task EnsureBranchNotCheckedOutElsewhereAsync(
        string repositoryRoot,
        string branchName,
        string operation,
        CancellationToken cancellationToken)
    {
        var worktreePath = await ReadBranchWorktreePathAsync(repositoryRoot, branchName, cancellationToken).ConfigureAwait(false);
        if (worktreePath.Length == 0)
        {
            return;
        }

        var currentBranch = await ReadCurrentBranchAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (string.Equals(currentBranch, branchName, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException($"The branch '{branchName}' is checked out in the worktree at '{worktreePath}'; switch or remove that worktree before attempting to {operation} it.");
    }

    private static void ValidateBranchMutationContext(
        string repositoryRoot,
        string branchName,
        string branchTip,
        BranchMutationContext? expectedContext)
    {
        if (expectedContext is null)
        {
            return;
        }

        if (!string.Equals(
                NormalizeRepositoryRoot(expectedContext.RootPath, expectedContext.RootPath),
                repositoryRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The repository changed while the branch dialog was open; refresh and try again.");
        }

        if (!string.Equals(expectedContext.BranchName, branchName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The selected branch changed while the branch dialog was open; refresh and try again.");
        }

        if (!string.Equals(expectedContext.ExpectedTipId, branchTip, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The branch changed while the branch dialog was open; refresh and try again.");
        }
    }

    /// <summary>Checks out a local branch using Git's normal dirty-worktree protection.</summary>
    public async Task CheckoutBranchAsync(
        string root,
        string name,
        CancellationToken cancellationToken)
    {
        await CheckoutBranchAsync(root, name, expectedContext: null, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks out a local branch after revalidating a captured repository context.</summary>
    public async Task CheckoutBranchAsync(
        string root,
        string name,
        BranchCheckoutContext? expectedContext,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var branchName = await ValidateBranchNameAsync(repositoryRoot, name, cancellationToken).ConfigureAwait(false);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                await ValidateCheckoutContextInMutationAsync(
                    path,
                    branchName,
                    expectedContext,
                    cancellationToken).ConfigureAwait(false);
                await SwitchBranchInMutationAsync(path, branchName, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Checks out a branch after saving dirty worktree changes in a named stash.</summary>
    public async Task CheckoutBranchWithStashAsync(
        string root,
        string name,
        CancellationToken cancellationToken)
    {
        await CheckoutBranchWithStashAsync(root, name, expectedContext: null, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks out a branch after saving dirty changes in a named stash and validating context.</summary>
    public async Task CheckoutBranchWithStashAsync(
        string root,
        string name,
        BranchCheckoutContext? expectedContext,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var branchName = await ValidateBranchNameAsync(repositoryRoot, name, cancellationToken).ConfigureAwait(false);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                await ValidateCheckoutContextInMutationAsync(
                    path,
                    branchName,
                    expectedContext,
                    cancellationToken).ConfigureAwait(false);
                var stash = await CreateStashInMutationAsync(
                    path,
                    BuildBranchSwitchStashMessage(branchName),
                    includeUntracked: true,
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    await SwitchBranchInMutationAsync(path, branchName, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Branch switch to '{branchName}' failed after saving stash {stash.Reference} ({stash.CommitId}). The stash remains available for manual recovery. Git reported: {exception.Message}",
                        exception);
                }
            }).ConfigureAwait(false);
    }

    /// <summary>Checks out a branch while carrying dirty changes when Git permits it.</summary>
    public async Task CheckoutBranchBringingChangesAsync(
        string root,
        string name,
        CancellationToken cancellationToken)
    {
        await CheckoutBranchBringingChangesAsync(root, name, expectedContext: null, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks out a branch while carrying dirty changes and validating the captured context.</summary>
    public async Task CheckoutBranchBringingChangesAsync(
        string root,
        string name,
        BranchCheckoutContext? expectedContext,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var branchName = await ValidateBranchNameAsync(repositoryRoot, name, cancellationToken).ConfigureAwait(false);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                await ValidateCheckoutContextInMutationAsync(
                    path,
                    branchName,
                    expectedContext,
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    await SwitchBranchInMutationAsync(path, branchName, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (GitCommandException exception) when (IsDirtyCheckoutFailure(exception))
                {
                    var stash = await CreateStashInMutationAsync(
                        path,
                        BuildTemporaryBranchSwitchStashMessage(branchName),
                        includeUntracked: true,
                        cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await SwitchBranchInMutationAsync(path, branchName, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception switchException)
                    {
                        throw new InvalidOperationException(
                            $"Branch switch to '{branchName}' failed after saving temporary stash {stash.Reference} ({stash.CommitId}). The stash remains available; return to the original branch and apply it manually. Git reported: {switchException.Message}",
                            switchException);
                    }

                    try
                    {
                        await ApplyStashInMutationAsync(path, stash.CommitId, restoreIndex: true, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception applyException)
                    {
                        var recovery = await IsStashApplyConflictAsync(
                                path,
                                applyException)
                            ? $"Resolve the conflicts on the target branch to finish bringing your changes. Stash {stash.Reference} ({stash.CommitId}) remains available as a recovery copy."
                            : $"Stash {stash.Reference} ({stash.CommitId}) remains available for manual recovery; apply it manually after reviewing the target branch.";
                        throw new InvalidOperationException(
                            $"Branch switch to '{branchName}' succeeded, but applying saved changes failed. {recovery} Git reported: {applyException.Message}",
                            applyException);
                    }

                    try
                    {
                        await DropStashInMutationAsync(path, stash.Reference, stash.CommitId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception dropException)
                    {
                        throw new InvalidOperationException(
                            $"Branch switch to '{branchName}' and applying changes succeeded, but cleanup of stash {stash.Reference} ({stash.CommitId}) failed. The stash remains available. Git reported: {dropException.Message}",
                            dropException);
                    }
                }
            }).ConfigureAwait(false);
    }

    private async Task SwitchBranchInMutationAsync(
        string repositoryRoot,
        string branchName,
        CancellationToken cancellationToken)
    {
        await processRunner.RunAsync(
            repositoryRoot,
            ["switch", "--no-guess", branchName],
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildBranchSwitchStashMessage(string branchName) =>
        $"WinGit: changes saved before switching to {branchName}";

    private static string BuildTemporaryBranchSwitchStashMessage(string branchName) =>
        $"WinGit: temporary changes while switching to {branchName}";

    private static bool IsDirtyCheckoutFailure(GitCommandException exception)
    {
        var error = exception.StandardError;
        return error.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase)
            || error.Contains("would be removed by checkout", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Please commit your changes or stash them", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStashApplyConflictHint(Exception exception)
    {
        if (exception is not GitCommandException gitException)
        {
            return false;
        }

        var error = gitException.StandardError;
        return error.Contains("conflict", StringComparison.OrdinalIgnoreCase)
            || error.Contains("CONFLICT", StringComparison.Ordinal)
            || error.Contains("patch failed", StringComparison.OrdinalIgnoreCase)
            || error.Contains("needs merge", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsStashApplyConflictAsync(
        string repositoryRoot,
        Exception exception)
    {
        if (HasStashApplyConflictHint(exception))
        {
            return true;
        }

        if (exception is OperationCanceledException)
        {
            return false;
        }

        try
        {
            var status = await GetStatusAsync(repositoryRoot, CancellationToken.None).ConfigureAwait(false);
            return status.Changes.Any(change =>
                change.Kind == ChangeKind.Conflicted
                || change.IndexStatus.Contains('U')
                || change.WorkTreeStatus.Contains('U'));
        }
        catch
        {
            // Preserve the original apply failure when Git status cannot be read.
            return false;
        }
    }

    private async Task ValidateCheckoutContextInMutationAsync(
        string repositoryRoot,
        string branchName,
        BranchCheckoutContext? expectedContext,
        CancellationToken cancellationToken)
    {
        if (expectedContext is null)
        {
            return;
        }

        if (!string.Equals(
                NormalizeRepositoryRoot(expectedContext.RootPath, expectedContext.RootPath),
                repositoryRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The repository changed while the branch switch was waiting for confirmation; refresh and try again.");
        }

        if (!string.Equals(expectedContext.TargetBranch, branchName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The selected target branch changed while the branch switch was waiting for confirmation; refresh and try again.");
        }

        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(status.Branch, expectedContext.SourceBranch, StringComparison.Ordinal)
            || !string.Equals(status.HeadId, expectedContext.SourceHeadId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The current branch or HEAD changed while the branch switch was waiting for confirmation; refresh and try again.");
        }

        var targetTip = await ReadRefIdAsync(
            repositoryRoot,
            $"refs/heads/{branchName}",
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(targetTip, expectedContext.TargetTipId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The target branch changed while the branch switch was waiting for confirmation; refresh and try again.");
        }
    }

    private async Task<string> ResolveRepositoryRootAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var candidate = ValidateDirectory(root, nameof(root));
        var result = await processRunner.RunAsync(
            candidate,
            ["rev-parse", "--show-toplevel", "--is-inside-work-tree"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "repository discovery");

        var lines = DecodeUtf8(result.StandardOutput, "repository discovery")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || !string.Equals(lines[1].Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected directory is not a Git work-tree repository.");
        }

        return NormalizeRepositoryRoot(lines[0].Trim(), candidate);
    }

    private async Task<string> ValidateBranchNameAsync(
        string repositoryRoot,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if ((name.Length > 0 && name[0] == '-')
            || name.IndexOf('\0') >= 0
            || name.Contains('\r')
            || name.Contains('\n'))
        {
            throw new ArgumentException("The branch name contains an invalid character.", nameof(name));
        }

        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["check-ref-format", "--branch", name],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "branch name validation");
        var checkedName = DecodeUtf8(result.StandardOutput, "branch name validation").Trim();
        if (!string.Equals(checkedName, name, StringComparison.Ordinal))
        {
            throw new ArgumentException("The branch name is not a canonical local branch name.", nameof(name));
        }

        return name;
    }

    private static string? ValidateStartPoint(string? startPoint)
    {
        if (string.IsNullOrWhiteSpace(startPoint))
        {
            return null;
        }

        if ((startPoint.Length > 0 && startPoint[0] == '-')
            || startPoint.IndexOf('\0') >= 0
            || startPoint.Contains('\r')
            || startPoint.Contains('\n'))
        {
            throw new ArgumentException("The branch start point contains an invalid character.", nameof(startPoint));
        }

        return startPoint;
    }

    private static IReadOnlyList<BranchSummary> ParseBranches(string repositoryRoot, byte[] output)
    {
        var text = DecodeUtf8(output, "branch list");
        var fields = text.Split('\0', StringSplitOptions.None);
        var branches = new List<BranchSummary>();
        for (var index = 0; index + 6 < fields.Length; index += 7)
        {
            var name = fields[index].Trim('\r', '\n');
            if (name.Length == 0)
            {
                continue;
            }

            var fullRef = fields[index + 1].Trim('\r', '\n');
            var isCurrent = string.Equals(fields[index + 2].Trim(), "*", StringComparison.Ordinal);
            var upstream = fields[index + 3].Trim('\r', '\n');
            var upstreamRemote = fields[index + 4].Trim('\r', '\n');
            var track = fields[index + 5].Trim('\r', '\n');
            var worktreeText = fields[index + 6].Trim('\r', '\n');
            var worktreePath = worktreeText.Length == 0
                ? null
                : Path.GetFullPath(
                    Path.IsPathRooted(worktreeText)
                        ? worktreeText
                        : Path.Combine(repositoryRoot, worktreeText));
            ParseTrack(track, out var ahead, out var behind);
            branches.Add(new BranchSummary(name, fullRef, isCurrent, upstreamRemote, upstream, ahead, behind, worktreePath));
        }

        return branches;
    }

    private static void ParseTrack(string track, out int ahead, out int behind)
    {
        ahead = ParseTrackValue(BranchTrackAheadPattern, track);
        behind = ParseTrackValue(BranchTrackBehindPattern, track);
    }

    private static int ParseTrackValue(Regex pattern, string track)
    {
        var match = pattern.Match(track);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }
}
