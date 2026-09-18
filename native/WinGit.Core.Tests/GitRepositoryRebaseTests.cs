using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryRebaseTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryRebaseTests()
    {
        Directory.CreateDirectory(FixtureParent);
        repositoryRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        RunGit(repositoryRoot, "init", "-b", "main");
        RunGit(repositoryRoot, "config", "user.name", "Test User");
        RunGit(repositoryRoot, "config", "user.email", "test-user@example.invalid");
        RunGit(repositoryRoot, "config", "commit.gpgsign", "false");
        var hooksPath = Path.Combine(repositoryRoot, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(repositoryRoot, "config", "core.hooksPath", hooksPath);
    }

    [Fact]
    public async Task CleanRebaseCompletesAndRewritesCurrentBranch()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("feature.txt", "feature\n");
        await service.StageFilesAsync(repositoryRoot, ["feature.txt"], CancellationToken.None);
        var featureHead = await service.CommitAsync(
            repositoryRoot,
            "feature edit",
            null,
            amend: false,
            CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("main.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["main.txt"], CancellationToken.None);
        var mainHead = await service.CommitAsync(
            repositoryRoot,
            "main edit",
            null,
            amend: false,
            CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);

        var result = await service.RebaseBranchAsync(
            repositoryRoot,
            "main",
            featureHead,
            CancellationToken.None);

        Assert.Equal(RebaseOutcome.Completed, result.Outcome);
        Assert.NotEqual(featureHead, result.HeadId);
        Assert.Equal(result.HeadId, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());
        Assert.Equal("feature", RunGit(repositoryRoot, "branch", "--show-current").Trim());
        Assert.Equal("main edit", RunGit(repositoryRoot, "show", "--format=%s", "--no-patch", mainHead).Trim());
        Assert.Equal("feature\n", File.ReadAllText(Path.Combine(repositoryRoot, "feature.txt")));
        Assert.False(result.State.IsInProgress);
        Assert.Empty(result.State.UnmergedPaths);
    }

    [Fact]
    public async Task ConflictRefusesUnstagedContinueThenCanContinueAndAbort()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("shared.txt", "feature\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var originalFeatureHead = await service.CommitAsync(
            repositoryRoot,
            "feature edit",
            null,
            amend: false,
            CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("shared.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var mainHead = await service.CommitAsync(
            repositoryRoot,
            "main edit",
            null,
            amend: false,
            CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);

        var conflict = await service.RebaseBranchAsync(
            repositoryRoot,
            "main",
            originalFeatureHead,
            CancellationToken.None);

        Assert.Equal(RebaseOutcome.Conflicts, conflict.Outcome);
        Assert.True(conflict.State.IsInProgress);
        Assert.Equal(GitOperationKind.Rebase, conflict.State.OperationKind);
        Assert.Equal(originalFeatureHead, conflict.State.OriginalBranchTip);
        Assert.Equal(mainHead, conflict.State.BaseBranchTip);
        Assert.Equal("feature", conflict.State.Branch);
        Assert.Equal(1, conflict.State.CurrentStep);
        Assert.Equal(1, conflict.State.TotalSteps);
        Assert.Equal(originalFeatureHead, conflict.State.CurrentCommitId);
        var unmerged = Assert.Single(conflict.State.UnmergedPaths);
        Assert.Equal("shared.txt", unmerged.Path);
        Assert.Equal(ChangeKind.Conflicted, unmerged.Kind);

        var unresolved = await Assert.ThrowsAsync<RebaseOperationBlockedException>(
            () => service.ContinueRebaseAsync(repositoryRoot, CancellationToken.None));
        Assert.Equal(RebaseOperationFailureReason.UnresolvedConflicts, unresolved.Reason);

        WriteFile("shared.txt", "resolved\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        WriteFile("shared.txt", "resolved but unstaged\n");
        var unstaged = await Assert.ThrowsAsync<RebaseOperationBlockedException>(
            () => service.ContinueRebaseAsync(repositoryRoot, CancellationToken.None));
        Assert.Equal(RebaseOperationFailureReason.UnstagedChanges, unstaged.Reason);

        WriteFile("shared.txt", "resolved\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var continued = await service.ContinueRebaseAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(RebaseOutcome.Completed, continued.Outcome);
        Assert.False(continued.State.IsInProgress);
        Assert.Equal("resolved\n", File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("shared.txt", "main again\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var secondMainHead = await service.CommitAsync(
            repositoryRoot,
            "main second edit",
            null,
            amend: false,
            CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("shared.txt", "feature again\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var secondFeatureHead = await service.CommitAsync(
            repositoryRoot,
            "feature second edit",
            null,
            amend: false,
            CancellationToken.None);

        var secondConflict = await service.RebaseBranchAsync(
            repositoryRoot,
            "main",
            secondFeatureHead,
            CancellationToken.None);
        Assert.Equal(RebaseOutcome.Conflicts, secondConflict.Outcome);
        Assert.Equal(secondMainHead, secondConflict.State.BaseBranchTip);

        var aborted = await service.AbortRebaseAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(RebaseOutcome.Aborted, aborted.Outcome);
        Assert.False(aborted.State.IsInProgress);
        Assert.Equal(secondFeatureHead, aborted.HeadId);
        Assert.Equal("feature again\n", File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));
        Assert.Equal("feature", RunGit(repositoryRoot, "branch", "--show-current").Trim());
    }

    [Fact]
    public async Task ContinueSkipsPickEmptiedByResolution()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("shared.txt", "feature\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var originalFeatureHead = await service.CommitAsync(
            repositoryRoot,
            "feature edit",
            null,
            amend: false,
            CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("shared.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var mainHead = await service.CommitAsync(
            repositoryRoot,
            "main edit",
            null,
            amend: false,
            CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);

        var conflict = await service.RebaseBranchAsync(
            repositoryRoot,
            "main",
            originalFeatureHead,
            CancellationToken.None);
        Assert.Equal(RebaseOutcome.Conflicts, conflict.Outcome);

        // Resolve by accepting the base version, leaving nothing to commit
        // for the replayed pick. Continuing must skip it instead of failing
        // on an empty commit.
        WriteFile("shared.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var continued = await service.ContinueRebaseAsync(repositoryRoot, CancellationToken.None);

        Assert.Equal(RebaseOutcome.Skipped, continued.Outcome);
        Assert.False(continued.State.IsInProgress);
        Assert.Equal(mainHead, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());
        Assert.Equal("feature", RunGit(repositoryRoot, "branch", "--show-current").Trim());
        Assert.Equal("main\n", File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));
        Assert.Empty((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
    }

    public void Dispose()
    {
        DeleteDirectory(repositoryRoot);
    }

    private void WriteFile(string relativePath, string contents)
    {
        var fullPath = Path.Combine(repositoryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents, Utf8NoBom);
    }

    private void Commit(string message)
    {
        RunGit(repositoryRoot, "add", "--all");
        RunGit(repositoryRoot, "commit", "--message", message);
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
