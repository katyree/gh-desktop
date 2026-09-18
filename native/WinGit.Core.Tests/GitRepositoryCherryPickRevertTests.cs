using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryCherryPickRevertTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryCherryPickRevertTests()
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
    public async Task CleanCherryPickAndRevertApplyExpectedInverseAndReplay()
    {
        WriteFile("base.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "source", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "source", CancellationToken.None);

        WriteFile("first.txt", "first\n");
        await service.StageFilesAsync(repositoryRoot, ["first.txt"], CancellationToken.None);
        var firstCommit = await service.CommitAsync(
            repositoryRoot,
            "first source change",
            null,
            amend: false,
            CancellationToken.None);

        WriteFile("second.txt", "second\n");
        await service.StageFilesAsync(repositoryRoot, ["second.txt"], CancellationToken.None);
        var secondCommit = await service.CommitAsync(
            repositoryRoot,
            "second source change",
            null,
            amend: false,
            CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        var mainHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        var picked = await service.CherryPickAsync(
            repositoryRoot,
            [firstCommit, secondCommit],
            mainHead,
            CancellationToken.None);

        Assert.Equal(CherryPickOutcome.Completed, picked.Outcome);
        Assert.False(picked.State.IsInProgress);
        Assert.Equal("first\n", File.ReadAllText(Path.Combine(repositoryRoot, "first.txt")));
        Assert.Equal("second\n", File.ReadAllText(Path.Combine(repositoryRoot, "second.txt")));

        var reverted = await service.RevertCommitAsync(
            repositoryRoot,
            secondCommit,
            picked.HeadId,
            CancellationToken.None);

        Assert.Equal(RevertOutcome.Completed, reverted.Outcome);
        Assert.False(reverted.State.IsInProgress);
        Assert.True(File.Exists(Path.Combine(repositoryRoot, "first.txt")));
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "second.txt")));
        Assert.Equal(
            "Revert \"second source change\"",
            RunGit(repositoryRoot, "show", "--format=%s", "--no-patch", reverted.HeadId).Trim());
        Assert.Equal(GitOperationKind.None, (await service.GetCherryPickStateAsync(repositoryRoot, CancellationToken.None)).OperationKind);
        Assert.Equal(GitOperationKind.None, (await service.GetRevertStateAsync(repositoryRoot, CancellationToken.None)).OperationKind);
    }

    [Fact]
    public async Task CherryPickConflictReportsStateRoutesContinueAndAbortSafely()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "source", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "source", CancellationToken.None);
        WriteFile("shared.txt", "source\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var sourceCommit = await service.CommitAsync(
            repositoryRoot,
            "source conflict",
            null,
            amend: false,
            CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("shared.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var mainCommit = await service.CommitAsync(
            repositoryRoot,
            "main conflict",
            null,
            amend: false,
            CancellationToken.None);

        var conflict = await service.CherryPickAsync(
            repositoryRoot,
            [sourceCommit],
            mainCommit,
            CancellationToken.None);

        Assert.Equal(CherryPickOutcome.Conflicts, conflict.Outcome);
        Assert.Equal(GitOperationKind.CherryPick, conflict.State.OperationKind);
        Assert.Equal(sourceCommit, conflict.State.CurrentCommitId);
        Assert.Equal(mainCommit, conflict.State.OriginalHeadId);
        Assert.Equal("main", conflict.State.Branch);
        Assert.Equal(1, conflict.State.CurrentStep);
        Assert.Equal(1, conflict.State.TotalSteps);
        Assert.Equal("shared.txt", Assert.Single(conflict.State.UnmergedPaths).Path);

        var wrongContinue = await Assert.ThrowsAsync<RevertOperationBlockedException>(
            () => service.ContinueRevertAsync(repositoryRoot, CancellationToken.None));
        Assert.Equal(RevertOperationFailureReason.DifferentOperationInProgress, wrongContinue.Reason);

        var unresolved = await Assert.ThrowsAsync<CherryPickOperationBlockedException>(
            () => service.ContinueCherryPickAsync(repositoryRoot, CancellationToken.None));
        Assert.Equal(CherryPickOperationFailureReason.UnresolvedConflicts, unresolved.Reason);

        WriteFile("shared.txt", "resolved\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var continued = await service.ContinueCherryPickAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(CherryPickOutcome.Completed, continued.Outcome);
        Assert.Equal("resolved\n", File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));

        await service.CreateBranchAsync(repositoryRoot, "abort-source", continued.HeadId, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "abort-source", CancellationToken.None);
        WriteFile("abort.txt", "source\n");
        await service.StageFilesAsync(repositoryRoot, ["abort.txt"], CancellationToken.None);
        var abortSourceCommit = await service.CommitAsync(
            repositoryRoot,
            "abort source",
            null,
            amend: false,
            CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("abort.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["abort.txt"], CancellationToken.None);
        var abortMainCommit = await service.CommitAsync(
            repositoryRoot,
            "abort main",
            null,
            amend: false,
            CancellationToken.None);
        var abortConflict = await service.CherryPickAsync(
            repositoryRoot,
            [abortSourceCommit],
            abortMainCommit,
            CancellationToken.None);
        Assert.Equal(CherryPickOutcome.Conflicts, abortConflict.Outcome);

        var aborted = await service.AbortCherryPickAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(CherryPickOutcome.Aborted, aborted.Outcome);
        Assert.Equal(abortMainCommit, aborted.HeadId);
        Assert.Equal("main\n", File.ReadAllText(Path.Combine(repositoryRoot, "abort.txt")));
        Assert.Equal(GitOperationKind.None, aborted.State.OperationKind);
        Assert.Equal("main", RunGit(repositoryRoot, "branch", "--show-current").Trim());
    }

    [Fact]
    public async Task ContinueCherryPickCommitsPickEmptiedByResolution()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "source", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "source", CancellationToken.None);
        WriteFile("shared.txt", "source\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var sourceCommit = await service.CommitAsync(
            repositoryRoot,
            "source conflict",
            null,
            amend: false,
            CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("shared.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var mainCommit = await service.CommitAsync(
            repositoryRoot,
            "main conflict",
            null,
            amend: false,
            CancellationToken.None);

        var conflict = await service.CherryPickAsync(
            repositoryRoot,
            [sourceCommit],
            mainCommit,
            CancellationToken.None);
        Assert.Equal(CherryPickOutcome.Conflicts, conflict.Outcome);

        // Resolve by accepting the base version, leaving nothing to commit.
        // Continuing must record the empty commit so the picked commit stays
        // visible in history instead of failing on `cherry-pick --continue`.
        WriteFile("shared.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var continued = await service.ContinueCherryPickAsync(repositoryRoot, CancellationToken.None);

        Assert.Equal(CherryPickOutcome.Completed, continued.Outcome);
        Assert.False(continued.State.IsInProgress);
        var newHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        Assert.NotEqual(mainCommit, newHead);
        Assert.Equal(mainCommit, RunGit(repositoryRoot, "rev-parse", $"{newHead}^").Trim());
        Assert.Equal("source conflict", RunGit(repositoryRoot, "log", "-1", "--format=%s").Trim());
        Assert.Empty(RunGit(repositoryRoot, "diff", mainCommit, newHead).Trim());
        Assert.Equal("main\n", File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));
        Assert.Empty((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
    }

    [Fact]
    public async Task ContinueCherryPickAdvancesSequenceAfterEmptyCommit()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "source", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "source", CancellationToken.None);
        WriteFile("shared.txt", "source\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var firstSourceCommit = await service.CommitAsync(
            repositoryRoot,
            "first source",
            null,
            amend: false,
            CancellationToken.None);
        WriteFile("second.txt", "second\n");
        await service.StageFilesAsync(repositoryRoot, ["second.txt"], CancellationToken.None);
        var secondSourceCommit = await service.CommitAsync(
            repositoryRoot,
            "second source",
            null,
            amend: false,
            CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("shared.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var mainCommit = await service.CommitAsync(
            repositoryRoot,
            "main conflict",
            null,
            amend: false,
            CancellationToken.None);

        var conflict = await service.CherryPickAsync(
            repositoryRoot,
            [firstSourceCommit, secondSourceCommit],
            mainCommit,
            CancellationToken.None);
        Assert.Equal(CherryPickOutcome.Conflicts, conflict.Outcome);

        WriteFile("shared.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var continued = await service.ContinueCherryPickAsync(repositoryRoot, CancellationToken.None);

        Assert.Equal(CherryPickOutcome.Completed, continued.Outcome);
        Assert.False(continued.State.IsInProgress);
        Assert.Equal("main", RunGit(repositoryRoot, "branch", "--show-current").Trim());
        Assert.Equal(
            "second source\nfirst source\nmain conflict\n",
            RunGit(repositoryRoot, "log", "--format=%s", "-3").Replace("\r\n", "\n"));
        Assert.Empty(RunGit(repositoryRoot, "diff", mainCommit, "HEAD~1").Trim());
        Assert.Equal("second\n", File.ReadAllText(Path.Combine(repositoryRoot, "second.txt")));
        Assert.Empty((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
    }

    [Fact]
    public async Task MultiCommitCherryPickReportsCommitTwoAndTypedConflictAfterSkip()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "source", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "source", CancellationToken.None);

        WriteFile("first.txt", "first\n");
        await service.StageFilesAsync(repositoryRoot, ["first.txt"], CancellationToken.None);
        var firstCommit = await service.CommitAsync(
            repositoryRoot,
            "first source change",
            null,
            amend: false,
            CancellationToken.None);

        WriteFile("shared.txt", "source two\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var secondCommit = await service.CommitAsync(
            repositoryRoot,
            "second source change",
            null,
            amend: false,
            CancellationToken.None);

        WriteFile("shared.txt", "source three\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var thirdCommit = await service.CommitAsync(
            repositoryRoot,
            "third source change",
            null,
            amend: false,
            CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("shared.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var mainHead = await service.CommitAsync(
            repositoryRoot,
            "main conflict",
            null,
            amend: false,
            CancellationToken.None);

        var firstConflict = await service.CherryPickAsync(
            repositoryRoot,
            [firstCommit, secondCommit, thirdCommit],
            mainHead,
            CancellationToken.None);

        Assert.Equal(CherryPickOutcome.Conflicts, firstConflict.Outcome);
        Assert.Equal(2, firstConflict.State.CurrentStep);
        Assert.Equal(3, firstConflict.State.TotalSteps);
        Assert.Equal(secondCommit, firstConflict.State.CurrentCommitId);
        // Git keeps the current and remaining picks in todo and does not write
        // a done file; progress comes from head..abort-safety plus todo.
        Assert.Equal(0, ReadSequenceCommandCount("done"));
        Assert.Equal(2, ReadSequenceCommandCount("todo"));

        var secondConflict = await service.SkipCherryPickAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(CherryPickOutcome.Conflicts, secondConflict.Outcome);
        Assert.Equal(2, secondConflict.State.CurrentStep);
        Assert.Equal(2, secondConflict.State.TotalSteps);
        Assert.Equal(thirdCommit, secondConflict.State.CurrentCommitId);

        var aborted = await service.AbortCherryPickAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(CherryPickOutcome.Aborted, aborted.Outcome);
        Assert.Equal(mainHead, aborted.HeadId);
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "first.txt")));
        Assert.Equal("main\n", File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));
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

    private int ReadSequenceCommandCount(string fileName)
    {
        var relativePath = RunGit(repositoryRoot, "rev-parse", "--git-path", $"sequencer/{fileName}").Trim();
        var path = Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
        if (!File.Exists(path))
        {
            return 0;
        }

        return File.ReadAllLines(path).Count(line => line.Trim().Length > 0);
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
