using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryReorderTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryReorderTests()
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
    public async Task RootPlanPreservesChronologicalSelectionAndReplaysExactOrder()
    {
        WriteFile("root.txt", "root\n");
        Commit("root");
        var rootCommit = Head();

        WriteFile("second.txt", "second\n");
        Commit("second");
        var secondCommit = Head();

        WriteFile("third.txt", "third\n");
        Commit("third");
        var thirdCommit = Head();

        WriteFile("fourth.txt", "fourth\n");
        Commit("fourth");
        var fourthCommit = Head();

        var service = new GitRepositoryService();
        var plan = await service.CaptureReorderPlanAsync(
            repositoryRoot,
            [fourthCommit, secondCommit],
            thirdCommit,
            lastRetainedCommitId: null);

        Assert.Equal([secondCommit, fourthCommit], plan.SelectedCommitIds);
        Assert.Equal(
            [rootCommit, secondCommit, fourthCommit, thirdCommit],
            plan.ReplayCommitIds);
        Assert.Equal(
            ["root", "second", "fourth", "third"],
            plan.ReplayCommits.Select(commit => commit.Summary));

        var configPath = Path.Combine(repositoryRoot, ".git", "config");
        var configBefore = File.ReadAllBytes(configPath);
        var inheritedSequenceEditor = Environment.GetEnvironmentVariable("GIT_SEQUENCE_EDITOR");
        Environment.SetEnvironmentVariable("GIT_SEQUENCE_EDITOR", ":");
        RebaseOperationResult result;
        try
        {
            result = await service.ReorderAsync(repositoryRoot, plan);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_SEQUENCE_EDITOR", inheritedSequenceEditor);
        }

        Assert.Equal(RebaseOutcome.Completed, result.Outcome);
        Assert.False(result.State.IsInProgress);
        Assert.Equal(Convert.ToHexString(configBefore), Convert.ToHexString(File.ReadAllBytes(configPath)));
        var history = await service.GetHistoryAsync(repositoryRoot, 20);
        Assert.Equal(
            ["third", "fourth", "second", "root"],
            history.Select(commit => commit.Summary));
    }

    [Fact]
    public async Task CapturePlanRefusesUnchangedOrder()
    {
        WriteFile("root.txt", "root\n");
        Commit("root");

        WriteFile("second.txt", "second\n");
        Commit("second");
        var secondCommit = Head();

        WriteFile("third.txt", "third\n");
        Commit("third");
        var thirdCommit = Head();
        var headBefore = Head();

        var service = new GitRepositoryService();

        // Selecting the middle commit before its current successor replays
        // history unchanged; running an interactive rebase for that would
        // rewrite every commit ID for nothing.
        var beforeTarget = await Assert.ThrowsAsync<ReorderOperationBlockedException>(
            () => service.CaptureReorderPlanAsync(
                repositoryRoot,
                [secondCommit],
                thirdCommit,
                lastRetainedCommitId: null));
        Assert.Equal(ReorderOperationFailureReason.NoChange, beforeTarget.Reason);

        var moveToEnd = await Assert.ThrowsAsync<ReorderOperationBlockedException>(
            () => service.CaptureReorderPlanAsync(
                repositoryRoot,
                [thirdCommit],
                beforeCommitId: null,
                lastRetainedCommitId: null));
        Assert.Equal(ReorderOperationFailureReason.NoChange, moveToEnd.Reason);

        Assert.Equal(headBefore, Head());
    }

    [Fact]
    public async Task ReorderToEndUsesRetainedBaseAndRejectsInvalidOrStalePlans()
    {
        WriteFile("root.txt", "root\n");
        Commit("root");
        var rootCommit = Head();

        WriteFile("second.txt", "second\n");
        Commit("second");
        var secondCommit = Head();

        WriteFile("third.txt", "third\n");
        Commit("third");
        var thirdCommit = Head();

        WriteFile("fourth.txt", "fourth\n");
        Commit("fourth");
        var fourthCommit = Head();

        var service = new GitRepositoryService();
        var derivedEndPlan = await service.CaptureReorderPlanFromCurrentHistoryAsync(
            repositoryRoot,
            [thirdCommit],
            beforeCommitId: null);
        Assert.Equal(secondCommit, derivedEndPlan.LastRetainedCommitId);
        Assert.Equal([fourthCommit, thirdCommit], derivedEndPlan.ReplayCommitIds);

        var plan = await service.CaptureReorderPlanAsync(
            repositoryRoot,
            [thirdCommit],
            beforeCommitId: null,
            lastRetainedCommitId: rootCommit);
        Assert.Equal([secondCommit, fourthCommit, thirdCommit], plan.ReplayCommitIds);

        var duplicate = await Assert.ThrowsAsync<ReorderOperationBlockedException>(
            () => service.CaptureReorderPlanAsync(
                repositoryRoot,
                [secondCommit, secondCommit],
                fourthCommit,
                rootCommit));
        Assert.Equal(ReorderOperationFailureReason.DuplicateSelection, duplicate.Reason);

        var missing = await Assert.ThrowsAsync<ReorderOperationBlockedException>(
            () => service.CaptureReorderPlanAsync(
                repositoryRoot,
                [new string('0', 40)],
                fourthCommit,
                rootCommit));
        Assert.Equal(ReorderOperationFailureReason.MissingCommit, missing.Reason);

        RunGit(repositoryRoot, "switch", "-c", "side");
        WriteFile("side.txt", "side\n");
        Commit("side");
        var foreignCommit = Head();
        RunGit(repositoryRoot, "switch", "main");

        var foreignTarget = await Assert.ThrowsAsync<ReorderOperationBlockedException>(
            () => service.CaptureReorderPlanAsync(
                repositoryRoot,
                [secondCommit],
                foreignCommit,
                rootCommit));
        Assert.Equal(ReorderOperationFailureReason.ForeignCommit, foreignTarget.Reason);

        WriteFile("after-plan.txt", "after plan\n");
        Commit("after plan");
        var stale = await Assert.ThrowsAsync<ReorderOperationBlockedException>(
            () => service.ReorderAsync(repositoryRoot, plan));
        Assert.Equal(ReorderOperationFailureReason.StalePlan, stale.Reason);
        Assert.Equal("after plan", RunGit(repositoryRoot, "show", "--format=%s", "--no-patch", "HEAD").Trim());
    }

    [Fact]
    public async Task AutoBaseReorderKeepsAnOlderMergeOutsideTheReplayRange()
    {
        WriteFile("root.txt", "root\n");
        Commit("root");

        RunGit(repositoryRoot, "switch", "-c", "feature");
        WriteFile("feature.txt", "feature\n");
        Commit("feature");
        RunGit(repositoryRoot, "switch", "main");

        WriteFile("main-before-merge.txt", "main before merge\n");
        Commit("main before merge");
        RunGit(repositoryRoot, "merge", "--no-ff", "feature", "--message", "older merge");
        var olderMerge = Head();

        WriteFile("after-one.txt", "after one\n");
        Commit("after one");
        var afterOne = Head();

        WriteFile("after-two.txt", "after two\n");
        Commit("after two");
        var afterTwo = Head();

        WriteFile("after-three.txt", "after three\n");
        Commit("after three");

        var service = new GitRepositoryService();
        var plan = await service.CaptureReorderPlanFromCurrentHistoryAsync(
            repositoryRoot,
            [afterTwo],
            beforeCommitId: afterOne);

        Assert.Equal(olderMerge, plan.LastRetainedCommitId);
        Assert.Equal(
            ["after one", "after two", "after three"],
            plan.OriginalCommits.Select(commit => commit.Summary));
        Assert.Equal(
            [afterTwo, afterOne, Head()],
            plan.ReplayCommitIds);

        var result = await service.ReorderAsync(repositoryRoot, plan);

        Assert.Equal(RebaseOutcome.Completed, result.Outcome);
        Assert.Equal(
            ["after three", "after one", "after two", "older merge"],
            RunGit(repositoryRoot, "log", "--first-parent", "--format=%s", "-n", "4")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(olderMerge, RunGit(repositoryRoot, "rev-parse", "HEAD~3").Trim());
    }

    [Fact]
    public async Task ReorderReportsConflictsAndReusesRebaseAbortRecovery()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root");

        WriteFile("shared.txt", "second\n");
        Commit("second");
        var secondCommit = Head();

        WriteFile("shared.txt", "third\n");
        Commit("third");
        var thirdCommit = Head();

        var service = new GitRepositoryService();
        var plan = await service.CaptureReorderPlanAsync(
            repositoryRoot,
            [thirdCommit],
            secondCommit,
            lastRetainedCommitId: null);

        var conflict = await service.ReorderAsync(repositoryRoot, plan);

        Assert.Equal(RebaseOutcome.Conflicts, conflict.Outcome);
        Assert.True(conflict.State.IsInProgress);
        Assert.Equal(GitOperationKind.Rebase, conflict.State.OperationKind);
        Assert.Contains(conflict.State.UnmergedPaths, change => change.Path == "shared.txt");

        var aborted = await service.AbortRebaseAsync(repositoryRoot, CancellationToken.None);

        Assert.Equal(RebaseOutcome.Aborted, aborted.Outcome);
        Assert.False(aborted.State.IsInProgress);
        Assert.Equal("third", RunGit(repositoryRoot, "show", "--format=%s", "--no-patch", "HEAD").Trim());
        Assert.Equal("third\n", File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));
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

    private string Head() => RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

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
