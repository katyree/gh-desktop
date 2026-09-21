using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryWorktreeStashTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;
    private string? secondaryWorktreePath;

    public GitRepositoryWorktreeStashTests()
    {
        Directory.CreateDirectory(FixtureParent);
        repositoryRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        RunGit(repositoryRoot, "init");
        RunGit(repositoryRoot, "config", "user.name", "Test User");
        RunGit(repositoryRoot, "config", "user.email", "test-user@example.invalid");
        ConfigureLocalCommitSafety(repositoryRoot);
    }

    [Fact]
    public async Task CreateAndApplyStashRestoresIndexWorkTreeAndUntrackedFile()
    {
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        Commit(repositoryRoot, "initial");

        WriteFile(repositoryRoot, "tracked.txt", "staged\n");
        RunGit(repositoryRoot, "add", "--", "tracked.txt");
        var indexObjectBefore = RunGit(repositoryRoot, "rev-parse", ":tracked.txt").Trim();
        WriteFile(repositoryRoot, "tracked.txt", "staged and unstaged\n");
        WriteFile(repositoryRoot, "untracked.txt", "untracked content\n");

        var service = new GitRepositoryService();
        var stashId = await service.CreateStashAsync(
            repositoryRoot,
            "native first slice",
            includeUntracked: true,
            CancellationToken.None);

        var stashes = await service.GetStashesAsync(repositoryRoot, CancellationToken.None);
        var stash = Assert.Single(stashes);
        Assert.Equal(stashId, stash.CommitId);
        Assert.Equal("stash@{0}", stash.Reference);
        Assert.Contains("native first slice", stash.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "untracked.txt")));
        Assert.Empty((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateStashAsync(repositoryRoot, "clean repeat", includeUntracked: true, CancellationToken.None));
        Assert.Single(await service.GetStashesAsync(repositoryRoot, CancellationToken.None));

        await service.ApplyStashAsync(repositoryRoot, stashId, restoreIndex: true, CancellationToken.None);

        Assert.Equal("staged and unstaged\n", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
        Assert.Equal("untracked content\n", File.ReadAllText(Path.Combine(repositoryRoot, "untracked.txt")));
        Assert.Equal(indexObjectBefore, RunGit(repositoryRoot, "rev-parse", ":tracked.txt").Trim());
        var restoredStatus = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var tracked = Assert.Single(restoredStatus.Changes, change => change.Path == "tracked.txt");
        Assert.Equal("M", tracked.IndexStatus);
        Assert.Equal("M", tracked.WorkTreeStatus);
        Assert.Contains(restoredStatus.Changes, change => change.Path == "untracked.txt");
        Assert.Single(await service.GetStashesAsync(repositoryRoot, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DropStashAsync(repositoryRoot, stash.Reference, new string('0', stashId.Length), CancellationToken.None));
        Assert.Single(await service.GetStashesAsync(repositoryRoot, CancellationToken.None));
        await service.DropStashAsync(repositoryRoot, stash.Reference, stashId, CancellationToken.None);
        Assert.Empty(await service.GetStashesAsync(repositoryRoot, CancellationToken.None));
    }

    [Fact]
    public async Task DirtyCheckoutBringsChangesOrRetainsFailedNamedStash()
    {
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        Commit(repositoryRoot, "initial");
        var initialBranch = RunGit(repositoryRoot, "symbolic-ref", "--short", "HEAD").Trim();

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        WriteFile(repositoryRoot, "carry.txt", "carry this change\n");

        await service.CheckoutBranchBringingChangesAsync(
            repositoryRoot,
            "feature",
            CancellationToken.None);

        var carriedStatus = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal("feature", carriedStatus.Branch);
        Assert.Contains(carriedStatus.Changes, change => change.Path == "carry.txt");
        Assert.Equal("carry this change\n", File.ReadAllText(Path.Combine(repositoryRoot, "carry.txt")));

        File.Delete(Path.Combine(repositoryRoot, "carry.txt"));
        await service.CheckoutBranchAsync(repositoryRoot, initialBranch, CancellationToken.None);

        await service.CreateBranchAsync(repositoryRoot, "conflict-target", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "conflict-target", CancellationToken.None);
        WriteFile(repositoryRoot, "tracked.txt", "target branch content\n");
        Commit(repositoryRoot, "target branch edit");
        await service.CheckoutBranchAsync(repositoryRoot, initialBranch, CancellationToken.None);
        WriteFile(repositoryRoot, "tracked.txt", "user content\n");

        var conflictException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CheckoutBranchBringingChangesAsync(
                repositoryRoot,
                "conflict-target",
                CancellationToken.None));
        var conflictStash = Assert.Single(await service.GetStashesAsync(repositoryRoot, CancellationToken.None));
        Assert.Contains("temporary changes while switching to conflict-target", conflictStash.Summary, StringComparison.Ordinal);
        Assert.Contains(conflictStash.Reference, conflictException.Message, StringComparison.Ordinal);
        Assert.Contains(conflictStash.CommitId, conflictException.Message, StringComparison.Ordinal);
        Assert.Contains("Resolve the conflicts on the target branch", conflictException.Message, StringComparison.Ordinal);
        Assert.Equal("conflict-target", (await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Branch);

        RunGit(repositoryRoot, "reset", "--hard", "HEAD");
        await service.CheckoutBranchAsync(repositoryRoot, initialBranch, CancellationToken.None);
        await service.ApplyStashAsync(repositoryRoot, conflictStash.CommitId, restoreIndex: true, CancellationToken.None);
        Assert.Equal("user content\n", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
        await service.DropStashAsync(repositoryRoot, conflictStash.Reference, conflictStash.CommitId, CancellationToken.None);
        RunGit(repositoryRoot, "reset", "--hard", "HEAD");

        await service.CreateBranchAsync(repositoryRoot, "saved", null, CancellationToken.None);
        WriteFile(repositoryRoot, "stash.txt", "save this change\n");
        await service.CheckoutBranchWithStashAsync(repositoryRoot, "saved", CancellationToken.None);
        var savedStatus = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal("saved", savedStatus.Branch);
        Assert.Empty(savedStatus.Changes);
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "stash.txt")));
        var savedStash = Assert.Single(await service.GetStashesAsync(repositoryRoot, CancellationToken.None));
        Assert.Contains("WinGit: changes saved before switching to saved", savedStash.Summary, StringComparison.Ordinal);

        await service.CheckoutBranchAsync(repositoryRoot, initialBranch, CancellationToken.None);
        await service.ApplyStashAsync(repositoryRoot, savedStash.CommitId, restoreIndex: true, CancellationToken.None);
        Assert.Equal("save this change\n", File.ReadAllText(Path.Combine(repositoryRoot, "stash.txt")));
        await service.DropStashAsync(repositoryRoot, savedStash.Reference, savedStash.CommitId, CancellationToken.None);
        File.Delete(Path.Combine(repositoryRoot, "stash.txt"));

        await service.CreateBranchAsync(repositoryRoot, "blocked", null, CancellationToken.None);
        secondaryWorktreePath = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        await service.AddWorktreeAsync(
            repositoryRoot,
            secondaryWorktreePath,
            "blocked",
            newBranchName: null,
            CancellationToken.None);

        WriteFile(repositoryRoot, "recover.txt", "recover this change\n");
        var checkoutException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CheckoutBranchWithStashAsync(repositoryRoot, "blocked", CancellationToken.None));
        Assert.Contains("already checked out", checkoutException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blocked", checkoutException.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(await service.GetStashesAsync(repositoryRoot, CancellationToken.None));
        Assert.Equal(initialBranch, (await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Branch);
        Assert.Equal("recover this change\n", File.ReadAllText(Path.Combine(repositoryRoot, "recover.txt")));

        File.Delete(Path.Combine(repositoryRoot, "recover.txt"));
        await service.RemoveWorktreeAsync(repositoryRoot, secondaryWorktreePath, CancellationToken.None);
        secondaryWorktreePath = null;
    }

    [Fact]
    public async Task AddListRemoveWorktreeAndBlockDirtyRemoval()
    {
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        Commit(repositoryRoot, "initial");
        var headId = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        RunGit(repositoryRoot, "branch", "feature/worktree");
        secondaryWorktreePath = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));

        var service = new GitRepositoryService();
        await service.AddWorktreeAsync(
            repositoryRoot,
            secondaryWorktreePath,
            "feature/worktree",
            newBranchName: null,
            CancellationToken.None);

        var worktrees = await service.GetWorktreesAsync(repositoryRoot, CancellationToken.None);
        var secondary = Assert.Single(
            worktrees,
            worktree => PathsEqual(worktree.Path, secondaryWorktreePath));
        Assert.Equal("feature/worktree", secondary.Branch);
        Assert.Equal(headId, secondary.HeadId);
        Assert.False(secondary.IsLocked);
        Assert.False(secondary.IsPrunable);

        WriteFile(secondaryWorktreePath, "tracked.txt", "dirty secondary\n");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RemoveWorktreeAsync(secondaryWorktreePath, secondaryWorktreePath, CancellationToken.None));
        await Assert.ThrowsAsync<GitCommandException>(
            () => service.RemoveWorktreeAsync(repositoryRoot, secondaryWorktreePath, CancellationToken.None));
        Assert.True(Directory.Exists(secondaryWorktreePath));
        Assert.Equal(
            "dirty secondary\n",
            File.ReadAllText(Path.Combine(secondaryWorktreePath, "tracked.txt")));

        WriteFile(secondaryWorktreePath, "tracked.txt", "base\n");
        await service.RemoveWorktreeAsync(repositoryRoot, secondaryWorktreePath, CancellationToken.None);
        Assert.False(Directory.Exists(secondaryWorktreePath));
        Assert.DoesNotContain(
            await service.GetWorktreesAsync(repositoryRoot, CancellationToken.None),
            worktree => PathsEqual(worktree.Path, secondaryWorktreePath));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RemoveWorktreeAsync(repositoryRoot, repositoryRoot, CancellationToken.None));
    }

    [Fact]
    public async Task PopStashAppliesAndDropsOnSuccess()
    {
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        Commit(repositoryRoot, "initial");
        WriteFile(repositoryRoot, "tracked.txt", "stashed\n");

        var service = new GitRepositoryService();
        var stashId = await service.CreateStashAsync(
            repositoryRoot,
            "pop success",
            includeUntracked: false,
            CancellationToken.None);
        Assert.Empty((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);

        var popped = await service.PopStashAsync(repositoryRoot, stashId, CancellationToken.None);

        Assert.True(popped.Dropped);
        Assert.Equal("stash@{0}", popped.Reference);
        Assert.Equal(stashId, popped.CommitId);
        Assert.Empty(popped.Conflicts);
        Assert.Equal("stashed\n", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
        Assert.Empty(await service.GetStashesAsync(repositoryRoot, CancellationToken.None));
    }

    [Fact]
    public async Task PopStashRetainsEntryAndReportsConflicts()
    {
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        Commit(repositoryRoot, "initial");
        WriteFile(repositoryRoot, "tracked.txt", "stashed\n");

        var service = new GitRepositoryService();
        var stashId = await service.CreateStashAsync(
            repositoryRoot,
            "pop conflict",
            includeUntracked: false,
            CancellationToken.None);

        // Commit a conflicting change so the worktree is clean but the apply
        // merges into a conflict instead of refusing on dirty files.
        WriteFile(repositoryRoot, "tracked.txt", "committed\n");
        RunGit(repositoryRoot, "add", "--", "tracked.txt");
        Commit(repositoryRoot, "conflicting commit");

        var popped = await service.PopStashAsync(repositoryRoot, stashId, CancellationToken.None);

        Assert.False(popped.Dropped);
        Assert.Equal(stashId, popped.CommitId);
        var conflict = Assert.Single(popped.Conflicts);
        Assert.Equal("tracked.txt", conflict.Path);
        var retained = Assert.Single(await service.GetStashesAsync(repositoryRoot, CancellationToken.None));
        Assert.Equal(stashId, retained.CommitId);
        Assert.Contains("<<<<<<<", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PopStashRefusesMissingEntryWithoutChangingState()
    {
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        Commit(repositoryRoot, "initial");
        var headBefore = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        var service = new GitRepositoryService();
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PopStashAsync(repositoryRoot, new string('0', 40), CancellationToken.None));

        Assert.Contains("no longer exists", missing.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(headBefore, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
        Assert.Empty(await service.GetStashesAsync(repositoryRoot, CancellationToken.None));
    }

    [Fact]
    public async Task AddWorktreeRefusesBranchCheckedOutElsewhere()
    {
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        Commit(repositoryRoot, "initial");
        RunGit(repositoryRoot, "branch", "feature/worktree");

        var service = new GitRepositoryService();
        secondaryWorktreePath = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        await service.AddWorktreeAsync(
            repositoryRoot,
            secondaryWorktreePath,
            "feature/worktree",
            newBranchName: null,
            CancellationToken.None);
        try
        {
            var thirdPath = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.AddWorktreeAsync(
                    repositoryRoot,
                    thirdPath,
                    "feature/worktree",
                    newBranchName: null,
                    CancellationToken.None));
            Assert.Contains("already checked out", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("feature/worktree", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(thirdPath));

            // Creating a new branch from the in-use branch stays allowed.
            var freshPath = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
            try
            {
                await service.AddWorktreeAsync(
                    repositoryRoot,
                    freshPath,
                    "feature/worktree",
                    "fresh-from-feature",
                    CancellationToken.None);
                Assert.True(Directory.Exists(freshPath));
            }
            finally
            {
                await service.RemoveWorktreeAsync(repositoryRoot, freshPath, CancellationToken.None);
            }

            Assert.Contains(
                await service.GetWorktreesAsync(repositoryRoot, CancellationToken.None),
                worktree => PathsEqual(worktree.Path, secondaryWorktreePath));
        }
        finally
        {
            await service.RemoveWorktreeAsync(repositoryRoot, secondaryWorktreePath, CancellationToken.None);
            secondaryWorktreePath = null;
        }
    }

    public void Dispose()
    {
        DeleteDirectory(secondaryWorktreePath);
        DeleteDirectory(repositoryRoot);
    }

    private static void ConfigureLocalCommitSafety(string path)
    {
        var hooksPath = Path.Combine(path, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(path, "config", "core.hooksPath", hooksPath);
        RunGit(path, "config", "commit.gpgsign", "false");
    }

    private static void WriteFile(string root, string relativePath, string contents)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents, Utf8NoBom);
    }

    private static void Commit(string root, string message)
    {
        RunGit(root, "add", "--all");
        RunGit(root, "commit", "--message", message);
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Git fixture.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git fixture command failed: {error}");
        }

        return output;
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static void DeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fixtureRoot = FixtureParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativePath = Path.GetRelativePath(fixtureRoot, fullPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath is "." or ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Fixture cleanup target is outside the test fixture directory.");
        }

        foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        foreach (var directory in Directory.EnumerateDirectories(fullPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }

        Directory.Delete(fullPath, recursive: true);
    }
}
