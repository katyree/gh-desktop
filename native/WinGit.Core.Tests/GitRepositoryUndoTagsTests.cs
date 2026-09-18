using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryUndoTagsTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;
    private readonly List<string> linkedWorktrees = new();

    public GitRepositoryUndoTagsTests()
    {
        Directory.CreateDirectory(FixtureParent);
        repositoryRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        RunGit(repositoryRoot, "init", "-b", "main");
        RunGit(repositoryRoot, "config", "user.name", "Test User");
        RunGit(repositoryRoot, "config", "user.email", "test-user@example.invalid");
        RunGit(repositoryRoot, "config", "commit.gpgsign", "false");
        RunGit(repositoryRoot, "config", "tag.gpgSign", "false");
        var hooksPath = Path.Combine(repositoryRoot, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(repositoryRoot, "config", "core.hooksPath", hooksPath);
    }

    [Fact]
    public async Task UndoLatestCommitPreservesDirtyAndUntrackedFilesAndReturnsMessage()
    {
        var trackedPath = Path.Combine(repositoryRoot, "tracked.txt");
        WriteFile(trackedPath, "base\n");
        var service = new GitRepositoryService();
        await service.StageFilesAsync(repositoryRoot, ["tracked.txt"], CancellationToken.None);
        var firstCommitId = await service.CommitAsync(
            repositoryRoot,
            "First commit",
            "First details",
            amend: false,
            CancellationToken.None);

        WriteFile(trackedPath, "second\n");
        await service.StageFilesAsync(repositoryRoot, ["tracked.txt"], CancellationToken.None);
        var secondCommitId = await service.CommitAsync(
            repositoryRoot,
            "Second commit",
            "Details to restore",
            amend: false,
            CancellationToken.None);

        WriteFile(trackedPath, "dirty after commit\n");
        var untrackedPath = Path.Combine(repositoryRoot, "untracked.txt");
        WriteFile(untrackedPath, "keep me\n");
        var before = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(secondCommitId, before.HeadId);

        var staleUndo = await Assert.ThrowsAsync<UndoCommitBlockedException>(
            () => service.UndoCommitAsync(
                repositoryRoot,
                firstCommitId,
                CancellationToken.None));
        Assert.Equal(UndoCommitFailureReason.StaleHead, staleUndo.Reason);
        Assert.True(staleUndo.State.HasWorkingChanges);

        var result = await service.UndoCommitAsync(
            repositoryRoot,
            secondCommitId,
            CancellationToken.None);

        Assert.Equal(secondCommitId, result.CommitId);
        Assert.Equal(firstCommitId, result.ParentCommitId);
        Assert.Equal("Second commit", result.Summary);
        Assert.Equal("Details to restore", result.Description);
        Assert.Equal("Test User", result.Author);
        Assert.False(result.WasInitialCommit);
        Assert.True(result.StateBefore.HasWorkingChanges);
        Assert.False(result.StateBefore.HasUnmergedChanges);
        var after = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(firstCommitId, after.HeadId);
        Assert.Equal("M", Assert.Single(after.Changes, change => change.Path == "tracked.txt").WorkTreeStatus);
        Assert.Equal("?", Assert.Single(after.Changes, change => change.Path == "untracked.txt").WorkTreeStatus);
        Assert.Equal("dirty after commit\n", File.ReadAllText(trackedPath));
        Assert.Equal("keep me\n", File.ReadAllText(untrackedPath));
    }

    [Fact]
    public async Task UndoInitialCommitLeavesAttachedBranchUnbornAndPreservesFiles()
    {
        var trackedPath = Path.Combine(repositoryRoot, "tracked.txt");
        WriteFile(trackedPath, "initial\n");
        var untrackedPath = Path.Combine(repositoryRoot, "untracked.txt");
        WriteFile(untrackedPath, "keep me\n");
        var service = new GitRepositoryService();
        await service.StageFilesAsync(repositoryRoot, ["tracked.txt"], CancellationToken.None);
        var commitId = await service.CommitAsync(
            repositoryRoot,
            "Initial commit",
            "Initial details",
            amend: false,
            CancellationToken.None);

        WriteFile(trackedPath, "dirty initial\n");
        var result = await service.UndoCommitAsync(
            repositoryRoot,
            commitId,
            CancellationToken.None);

        Assert.True(result.WasInitialCommit);
        Assert.Null(result.ParentCommitId);
        Assert.Equal("Initial commit", result.Summary);
        Assert.Equal("Initial details", result.Description);
        Assert.Equal("Test User", result.Author);
        Assert.True(result.StateBefore.HasWorkingChanges);
        var after = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        Assert.True(after.IsUnborn);
        Assert.False(after.IsDetached);
        Assert.Equal("main", after.Branch);
        Assert.Equal(string.Empty, after.HeadId);
        Assert.Contains(after.Changes, change => change.Path == "tracked.txt" && change.WorkTreeStatus == "?");
        Assert.Contains(after.Changes, change => change.Path == "untracked.txt" && change.WorkTreeStatus == "?");
        Assert.Equal("dirty initial\n", File.ReadAllText(trackedPath));
        Assert.Equal("keep me\n", File.ReadAllText(untrackedPath));
    }

    [Fact]
    public async Task UndoCommitInLinkedWorktreeTargetsOnlyThatWorktree()
    {
        var service = new GitRepositoryService();
        WriteFile(Path.Combine(repositoryRoot, "base.txt"), "base\n");
        await service.StageFilesAsync(repositoryRoot, ["base.txt"], CancellationToken.None);
        var baseCommitId = await service.CommitAsync(
            repositoryRoot,
            "Base commit",
            null,
            amend: false,
            CancellationToken.None);

        var linkedRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        linkedWorktrees.Add(linkedRoot);
        RunGit(repositoryRoot, "worktree", "add", "--detach", linkedRoot, baseCommitId);
        RunGit(linkedRoot, "checkout", "-b", "feature");

        WriteFile(Path.Combine(linkedRoot, "feature.txt"), "feature\n");
        await service.StageFilesAsync(linkedRoot, ["feature.txt"], CancellationToken.None);
        var featureCommitId = await service.CommitAsync(
            linkedRoot,
            "Feature commit",
            "Feature details",
            amend: false,
            CancellationToken.None);

        // A stale expected HEAD must fail without touching either worktree.
        var staleUndo = await Assert.ThrowsAsync<UndoCommitBlockedException>(
            () => service.UndoCommitAsync(
                linkedRoot,
                baseCommitId,
                CancellationToken.None));
        Assert.Equal(UndoCommitFailureReason.StaleHead, staleUndo.Reason);
        Assert.Equal(
            featureCommitId,
            (await service.GetStatusAsync(linkedRoot, CancellationToken.None)).HeadId);
        Assert.Equal(
            baseCommitId,
            (await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).HeadId);

        var result = await service.UndoCommitAsync(
            linkedRoot,
            featureCommitId,
            CancellationToken.None);

        Assert.Equal(featureCommitId, result.CommitId);
        Assert.Equal(baseCommitId, result.ParentCommitId);
        Assert.Equal("Feature commit", result.Summary);
        Assert.False(result.WasInitialCommit);

        var linkedAfter = await service.GetStatusAsync(linkedRoot, CancellationToken.None);
        Assert.Equal(baseCommitId, linkedAfter.HeadId);
        Assert.Equal("feature", linkedAfter.Branch);
        Assert.Contains(
            linkedAfter.Changes,
            change => change.Path == "feature.txt" && change.WorkTreeStatus == "?");
        Assert.Equal("feature\n", File.ReadAllText(Path.Combine(linkedRoot, "feature.txt")));

        // The main worktree is undisturbed: same branch tip, no new changes.
        var mainAfter = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(baseCommitId, mainAfter.HeadId);
        Assert.Equal("main", mainAfter.Branch);
        Assert.Empty(mainAfter.Changes);
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "feature.txt")));
    }

    [Fact]
    public async Task AnnotatedTagListsPeeledTargetAndRejectsStaleDelete()
    {
        WriteFile(Path.Combine(repositoryRoot, "tracked.txt"), "tag target\n");
        var service = new GitRepositoryService();
        await service.StageFilesAsync(repositoryRoot, ["tracked.txt"], CancellationToken.None);
        var commitId = await service.CommitAsync(
            repositoryRoot,
            "Tag target",
            null,
            amend: false,
            CancellationToken.None);

        var created = await service.CreateTagAsync(
            repositoryRoot,
            "v1.0.0",
            commitId,
            message: null,
            CancellationToken.None);

        Assert.Equal("v1.0.0", created.Name);
        Assert.Equal(commitId, created.TargetId);
        Assert.True(created.IsAnnotated);
        Assert.NotEqual(created.ObjectId, created.TargetId);
        var listed = Assert.Single(
            await service.GetTagsAsync(repositoryRoot, CancellationToken.None));
        Assert.Equal(created, listed);

        await Assert.ThrowsAsync<StaleTagException>(
            () => service.DeleteTagAsync(
                repositoryRoot,
                "v1.0.0",
                new string('0', 40),
                CancellationToken.None));
        Assert.Single(await service.GetTagsAsync(repositoryRoot, CancellationToken.None));

        await service.DeleteTagAsync(
            repositoryRoot,
            "v1.0.0",
            created.ObjectId,
            CancellationToken.None);
        Assert.Empty(await service.GetTagsAsync(repositoryRoot, CancellationToken.None));
    }

    public void Dispose()
    {
        foreach (var linkedRoot in linkedWorktrees)
        {
            try
            {
                RunGit(repositoryRoot, "worktree", "remove", "--force", linkedRoot);
            }
            catch (Exception)
            {
                // Best effort: the fixture directory cleanup below still applies.
            }

            DeleteDirectory(linkedRoot);
        }

        DeleteDirectory(repositoryRoot);
    }

    private static void WriteFile(string path, string contents)
    {
        File.WriteAllText(path, contents, Utf8NoBom);
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

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
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
