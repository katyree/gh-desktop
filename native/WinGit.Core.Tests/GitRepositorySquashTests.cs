using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositorySquashTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositorySquashTests()
    {
        Directory.CreateDirectory(FixtureParent);
        repositoryRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        RunGit(repositoryRoot, "init", "-b", "main");
        RunGit(repositoryRoot, "config", "user.name", "Test Committer");
        RunGit(repositoryRoot, "config", "user.email", "test-committer@example.invalid");
        RunGit(repositoryRoot, "config", "commit.gpgsign", "false");
        var hooksPath = Path.Combine(repositoryRoot, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(repositoryRoot, "config", "core.hooksPath", hooksPath);
    }

    [Fact]
    public async Task SquashPreservesUnselectedOrderAndReplacementAuthor()
    {
        CommitFile("root", "root", "Root Author", "root-author@example.invalid");
        var rootCommit = Head();

        CommitFile("retained", "retained", "Retained Author", "retained@example.invalid");
        var retainedCommit = Head();

        CommitFile("early", "early", "Early Author", "early@example.invalid");
        var earlyCommit = Head();

        CommitFile("target", "target", "Target Author", "target@example.invalid");
        var targetCommit = Head();

        CommitFile("after", "after", "After Author", "after@example.invalid");
        var afterCommit = Head();

        CommitFile("late", "late", "Late Author", "late@example.invalid");
        var lateCommit = Head();

        var service = new GitRepositoryService();
        var plan = await service.CaptureSquashPlanFromCurrentHistoryAsync(
            repositoryRoot,
            [lateCommit, earlyCommit],
            targetCommit);

        Assert.Equal(retainedCommit, plan.LastRetainedCommitId);
        Assert.Equal([earlyCommit, lateCommit], plan.SelectedCommitIds);
        Assert.Equal(
            [earlyCommit, targetCommit, lateCommit, afterCommit],
            plan.ReplayCommitIds);
        Assert.Equal(earlyCommit, plan.ResultingCommit.Id);
        Assert.Equal("Early Author", plan.ResultingCommitAuthor.Name);
        Assert.Equal("early@example.invalid", plan.ResultingCommitAuthor.Email);
        Assert.Equal(
            [
                (SquashPlanAction.Pick, earlyCommit),
                (SquashPlanAction.Squash, targetCommit),
                (SquashPlanAction.Squash, lateCommit),
                (SquashPlanAction.Pick, afterCommit),
            ],
            plan.ReplaySteps.Select(step => (step.Action, step.Commit.Id)));

        var configPath = Path.Combine(repositoryRoot, ".git", "config");
        var configBefore = File.ReadAllBytes(configPath);
        var result = await service.SquashAsync(
            repositoryRoot,
            plan,
            "replacement summary\n\nreplacement body\n");

        Assert.Equal(RebaseOutcome.Completed, result.Outcome);
        Assert.False(result.State.IsInProgress);
        Assert.Equal(
            ["after", "replacement summary", "retained", "root"],
            (await service.GetHistoryAsync(repositoryRoot, 20)).Select(commit => commit.Summary));
        Assert.Equal(
            "Early Author\0early@example.invalid",
            RunGit(repositoryRoot, "show", "--no-patch", "--format=%an%x00%ae", "HEAD~1").Trim());
        Assert.Equal(
            "replacement summary\nreplacement body",
            RunGit(repositoryRoot, "show", "--no-patch", "--format=%s%n%b", "HEAD~1").Trim());
        Assert.Equal(Convert.ToHexString(configBefore), Convert.ToHexString(File.ReadAllBytes(configPath)));
        Assert.NotEqual(rootCommit, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task RootRangeCapturesRootReplayAndRejectsStaleOrDirtyPlan()
    {
        CommitFile("root", "root", "Root Author", "root@example.invalid");
        var rootCommit = Head();
        CommitFile("middle", "middle", "Middle Author", "middle@example.invalid");
        var middleCommit = Head();
        CommitFile("selected", "selected", "Selected Author", "selected@example.invalid");
        var selectedCommit = Head();

        var service = new GitRepositoryService();
        var rootPlan = await service.CaptureSquashPlanFromCurrentHistoryAsync(
            repositoryRoot,
            [selectedCommit],
            rootCommit);

        Assert.Null(rootPlan.LastRetainedCommitId);
        Assert.Equal(
            [rootCommit, selectedCommit, middleCommit],
            rootPlan.ReplayCommitIds);

        CommitFile("newer", "newer", "Newer Author", "newer@example.invalid");
        var stale = await Assert.ThrowsAsync<SquashOperationBlockedException>(
            () => service.SquashAsync(repositoryRoot, rootPlan, null));
        Assert.Equal(SquashOperationFailureReason.StalePlan, stale.Reason);

        File.WriteAllText(Path.Combine(repositoryRoot, "dirty.txt"), "dirty\n", Utf8NoBom);
        var dirty = await Assert.ThrowsAsync<SquashOperationBlockedException>(
            () => service.CaptureSquashPlanFromCurrentHistoryAsync(
                repositoryRoot,
                [selectedCommit],
                rootCommit));
        Assert.Equal(SquashOperationFailureReason.DirtyWorktree, dirty.Reason);
    }

    [Fact]
    public async Task SquashReportsConflictAndUsesRebaseAbortRecovery()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root", "Root Author", "root@example.invalid");

        WriteFile("shared.txt", "target\n");
        Commit("target", "Target Author", "target@example.invalid");
        var targetCommit = Head();

        WriteFile("shared.txt", "middle\n");
        Commit("middle", "Middle Author", "middle@example.invalid");

        WriteFile("shared.txt", "selected\n");
        Commit("selected", "Selected Author", "selected@example.invalid");
        var selectedCommit = Head();

        var service = new GitRepositoryService();
        var plan = await service.CaptureSquashPlanFromCurrentHistoryAsync(
            repositoryRoot,
            [selectedCommit],
            targetCommit);

        var conflict = await service.SquashAsync(repositoryRoot, plan, null);

        Assert.Equal(RebaseOutcome.Conflicts, conflict.Outcome);
        Assert.True(conflict.State.IsInProgress);
        Assert.Contains(conflict.State.UnmergedPaths, change => change.Path == "shared.txt");

        var aborted = await service.AbortRebaseAsync(repositoryRoot, CancellationToken.None);

        Assert.Equal(RebaseOutcome.Aborted, aborted.Outcome);
        Assert.False(aborted.State.IsInProgress);
        Assert.Equal(selectedCommit, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());
        Assert.Equal("selected\n", File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));
    }

    [Fact]
    public async Task SquashContinuationPreservesChosenMessageAndLaterPickMessage()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root", "Root Author", "root@example.invalid");

        WriteFile("shared.txt", "target\n");
        Commit("target", "Target Author", "target@example.invalid");
        var targetCommit = Head();

        WriteFile("shared.txt", "middle\n");
        Commit("middle", "Middle Author", "middle@example.invalid");

        WriteFile("shared.txt", "selected\n");
        Commit("selected", "Selected Author", "selected@example.invalid");
        var selectedCommit = Head();

        var service = new GitRepositoryService();
        var plan = await service.CaptureSquashPlanFromCurrentHistoryAsync(
            repositoryRoot,
            [selectedCommit],
            targetCommit);

        var conflict = await service.SquashAsync(repositoryRoot, plan, null);

        Assert.Equal(RebaseOutcome.Conflicts, conflict.Outcome);
        WriteFile("shared.txt", "selected\n");
        RunGit(repositoryRoot, "add", "--", "shared.txt");

        var continued = await service.ContinueSquashAsync(
            repositoryRoot,
            plan,
            "chosen summary\n\nchosen body\n");

        Assert.Equal(RebaseOutcome.Conflicts, continued.Outcome);
        WriteFile("shared.txt", "middle\n");
        RunGit(repositoryRoot, "add", "--", "shared.txt");

        continued = await service.ContinueSquashAsync(
            repositoryRoot,
            plan,
            "chosen summary\n\nchosen body\n");

        Assert.Equal(RebaseOutcome.Completed, continued.Outcome);
        Assert.Equal(
            ["middle", "chosen summary", "root"],
            (await service.GetHistoryAsync(repositoryRoot, 20)).Select(commit => commit.Summary));
        Assert.Equal(
            "chosen summary\nchosen body",
            RunGit(repositoryRoot, "show", "--no-patch", "--format=%s%n%b", "HEAD~1").Trim());
        Assert.Equal(
            "middle",
            RunGit(repositoryRoot, "show", "--no-patch", "--format=%s", "HEAD").Trim());
    }

    [Fact]
    public async Task SquashRecoverySurvivesServiceRestartAndSkipPreservesChosenMessage()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root", "Root Author", "root@example.invalid");
        var rootCommit = Head();

        WriteFile("shared.txt", "middle\n");
        Commit("middle", "Middle Author", "middle@example.invalid");

        WriteFile("shared.txt", "selected\n");
        Commit("selected", "Selected Author", "selected@example.invalid");
        var selectedCommit = Head();

        var service = new GitRepositoryService();
        var plan = await service.CaptureSquashPlanFromCurrentHistoryAsync(
            repositoryRoot,
            [selectedCommit],
            rootCommit);
        const string chosenMessage = "chosen summary\r\rchosen body\r";
        const string normalizedChosenMessage = "chosen summary\n\nchosen body\n";

        var conflict = await service.SquashAsync(repositoryRoot, plan, chosenMessage);

        Assert.Equal(RebaseOutcome.Conflicts, conflict.Outcome);

        var restartedService = new GitRepositoryService();
        var recovery = await restartedService.GetSquashRecoveryAsync(repositoryRoot);

        Assert.False(recovery.IsUnavailable);
        var loadedRecovery = recovery.Recovery;
        Assert.NotNull(loadedRecovery);
        Assert.Equal(normalizedChosenMessage, loadedRecovery.ReplacementMessage);
        Assert.Equal(plan.ReplayCommitIds, loadedRecovery.Plan.ReplayCommitIds);

        WriteFile("shared.txt", "selected\n");
        RunGit(repositoryRoot, "add", "--", "shared.txt");
        var continued = await restartedService.ContinueSquashAsync(
            repositoryRoot,
            loadedRecovery.Plan,
            loadedRecovery.ReplacementMessage);

        Assert.Equal(RebaseOutcome.Conflicts, continued.Outcome);
        var skipService = new GitRepositoryService();
        var resumedRecovery = await skipService.GetSquashRecoveryAsync(repositoryRoot);
        Assert.False(resumedRecovery.IsUnavailable);
        var resumed = resumedRecovery.Recovery;
        Assert.NotNull(resumed);

        var skipped = await skipService.SkipSquashAsync(
            repositoryRoot,
            resumed.Plan,
            resumed.ReplacementMessage);

        Assert.Equal(RebaseOutcome.Skipped, skipped.Outcome);
        Assert.Equal(
            ["chosen summary"],
            (await restartedService.GetHistoryAsync(repositoryRoot, 20)).Select(commit => commit.Summary));
        Assert.Equal(
            "chosen summary\nchosen body",
            RunGit(repositoryRoot, "show", "--no-patch", "--format=%s%n%b", "HEAD").Trim());
    }

    [Fact]
    public async Task UndoSquashRestoresPreSquashHistory()
    {
        CommitFile("root", "root", "Root Author", "root@example.invalid");
        CommitFile("first", "first", "First Author", "first@example.invalid");
        var firstCommit = Head();
        CommitFile("second", "second", "Second Author", "second@example.invalid");

        var service = new GitRepositoryService();
        var plan = await service.CaptureSquashPlanFromCurrentHistoryAsync(
            repositoryRoot,
            [Head()],
            firstCommit);
        var preSquashTip = Head();

        var result = await service.SquashAsync(repositoryRoot, plan, "squashed summary");
        Assert.Equal(RebaseOutcome.Completed, result.Outcome);
        var postSquashTip = result.HeadId;
        Assert.NotEqual(preSquashTip, postSquashTip);

        var undone = await service.UndoSquashAsync(repositoryRoot, plan, postSquashTip);
        Assert.Equal(RebaseOutcome.Completed, undone.Outcome);
        Assert.Equal(preSquashTip, undone.HeadId);
        Assert.Equal(preSquashTip, Head());
        Assert.Equal(
            ["second", "first", "root"],
            (await service.GetHistoryAsync(repositoryRoot, 20)).Select(commit => commit.Summary));
        Assert.Empty((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
    }

    [Fact]
    public async Task UndoSquashRefusesMovedHeadDirtyTreeAndReuse()
    {
        CommitFile("root", "root", "Root Author", "root@example.invalid");
        CommitFile("first", "first", "First Author", "first@example.invalid");
        var firstCommit = Head();
        CommitFile("second", "second", "Second Author", "second@example.invalid");

        var service = new GitRepositoryService();
        var plan = await service.CaptureSquashPlanFromCurrentHistoryAsync(
            repositoryRoot,
            [Head()],
            firstCommit);
        var preSquashTip = Head();

        var result = await service.SquashAsync(repositoryRoot, plan, "squashed summary");
        var postSquashTip = result.HeadId;

        CommitFile("newer", "newer", "Newer Author", "newer@example.invalid");
        var moved = await Assert.ThrowsAsync<SquashOperationBlockedException>(
            () => service.UndoSquashAsync(repositoryRoot, plan, postSquashTip));
        Assert.Equal(SquashOperationFailureReason.UndoUnavailable, moved.Reason);
        Assert.Equal("newer", (await service.GetHistoryAsync(repositoryRoot, 1)).Single().Summary);

        RunGit(repositoryRoot, "reset", "--hard", postSquashTip);
        WriteFile("dirty.txt", "dirty\n");
        var dirty = await Assert.ThrowsAsync<SquashOperationBlockedException>(
            () => service.UndoSquashAsync(repositoryRoot, plan, postSquashTip));
        Assert.Equal(SquashOperationFailureReason.DirtyWorktree, dirty.Reason);
        File.Delete(Path.Combine(repositoryRoot, "dirty.txt"));

        var undone = await service.UndoSquashAsync(repositoryRoot, plan, postSquashTip);
        Assert.Equal(RebaseOutcome.Completed, undone.Outcome);
        Assert.Equal(preSquashTip, Head());

        var reused = await Assert.ThrowsAsync<SquashOperationBlockedException>(
            () => service.UndoSquashAsync(repositoryRoot, plan, postSquashTip));
        Assert.Equal(SquashOperationFailureReason.UndoUnavailable, reused.Reason);
        Assert.Equal(preSquashTip, Head());
    }

    public void Dispose()
    {
        DeleteDirectory(repositoryRoot);
    }

    private void CommitFile(
        string fileName,
        string message,
        string authorName,
        string authorEmail)
    {
        WriteFile(fileName + ".txt", fileName + "\n");
        Commit(message, authorName, authorEmail);
    }

    private void Commit(string message, string authorName, string authorEmail)
    {
        RunGit(repositoryRoot, "add", "--all");
        RunGit(
            repositoryRoot,
            new Dictionary<string, string?>
            {
                ["GIT_AUTHOR_NAME"] = authorName,
                ["GIT_AUTHOR_EMAIL"] = authorEmail,
                ["GIT_COMMITTER_NAME"] = "Test Committer",
                ["GIT_COMMITTER_EMAIL"] = "test-committer@example.invalid",
            },
            "commit",
            "--message",
            message);
    }

    private void WriteFile(string relativePath, string contents)
    {
        var fullPath = Path.Combine(repositoryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents, Utf8NoBom);
    }

    private string Head() => RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

    private static string RunGit(string workingDirectory, params string[] arguments) =>
        RunGit(workingDirectory, environment: null, arguments);

    private static string RunGit(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
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
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

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
