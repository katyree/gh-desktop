using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryMergeTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryMergeTests()
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
    public async Task CleanMergeCompletesWithoutOpeningAnEditor()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("feature.txt", "feature change\n");
        await service.StageFilesAsync(repositoryRoot, ["feature.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "feature change", null, amend: false, CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        var mainHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        var result = await service.MergeBranchAsync(
            repositoryRoot,
            "feature",
            mainHead,
            CancellationToken.None);

        Assert.Equal(MergeOutcome.Completed, result.Outcome);
        Assert.Equal(RunGit(repositoryRoot, "rev-parse", "HEAD").Trim(), result.HeadId);
        Assert.False(result.State.IsInProgress);
        Assert.Empty(result.State.UnmergedPaths);
        Assert.Equal("feature change\n", File.ReadAllText(Path.Combine(repositoryRoot, "feature.txt")));
        Assert.Contains("feature change", RunGit(repositoryRoot, "log", "-1", "--format=%s"));
    }

    [Fact]
    public async Task CapturedCommitIdWinsOverSameNamedBranchAndReadsEveryMergeHead()
    {
        WriteFile("base.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("feature.txt", "feature\n");
        await service.StageFilesAsync(repositoryRoot, ["feature.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "feature change", null, amend: false, CancellationToken.None);
        var featureHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        var mainHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        await service.CreateBranchAsync(repositoryRoot, "alternate", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "alternate", CancellationToken.None);
        WriteFile("alternate.txt", "alternate\n");
        await service.StageFilesAsync(repositoryRoot, ["alternate.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "alternate change", null, amend: false, CancellationToken.None);
        var alternateHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        // A full object ID is an immutable caller capture.  A local branch with
        // the same 40-hex spelling must not shadow it during merge resolution.
        RunGit(repositoryRoot, "branch", featureHead, "alternate");
        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);

        var result = await service.MergeBranchAsync(
            repositoryRoot,
            featureHead,
            mainHead,
            CancellationToken.None);

        Assert.Equal(MergeOutcome.Completed, result.Outcome);
        Assert.True(File.Exists(Path.Combine(repositoryRoot, "feature.txt")));
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "alternate.txt")));

        var mergeHeadPath = ResolveGitPath("MERGE_HEAD");
        File.WriteAllText(
            mergeHeadPath,
            featureHead + Environment.NewLine + alternateHead + Environment.NewLine,
            Utf8NoBom);
        try
        {
            var state = await service.GetMergeStateAsync(repositoryRoot, CancellationToken.None);
            Assert.Equal(GitOperationKind.Merge, state.OperationKind);
            Assert.Equal([featureHead, alternateHead], state.MergeHeadIds);
        }
        finally
        {
            if (File.Exists(mergeHeadPath))
            {
                File.Delete(mergeHeadPath);
            }
        }
    }

    [Fact]
    public async Task ConflictIsTypedContinueRefusesAndAbortRestoresCurrentBranch()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("shared.txt", "feature\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "feature edit", null, amend: false, CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("shared.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "main edit", null, amend: false, CancellationToken.None);
        var mainHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        var conflict = await service.MergeBranchAsync(
            repositoryRoot,
            "feature",
            mainHead,
            CancellationToken.None);

        Assert.Equal(MergeOutcome.Conflicts, conflict.Outcome);
        Assert.True(conflict.State.IsMergeInProgress);
        var unmerged = Assert.Single(conflict.State.UnmergedPaths);
        Assert.Equal("shared.txt", unmerged.Path);
        Assert.Equal(ChangeKind.Conflicted, unmerged.Kind);
        Assert.NotEmpty(conflict.State.MergeHeadIds);

        var continueError = await Assert.ThrowsAsync<MergeOperationBlockedException>(
            () => service.ContinueMergeAsync(repositoryRoot, CancellationToken.None));
        Assert.Equal(MergeOperationFailureReason.UnresolvedConflicts, continueError.Reason);
        Assert.Single(continueError.State.UnmergedPaths);

        var aborted = await service.AbortMergeAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(MergeOutcome.Aborted, aborted.Outcome);
        Assert.False(aborted.State.IsInProgress);
        Assert.Empty(aborted.State.UnmergedPaths);
        Assert.Equal("main\n", File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));
        Assert.Equal(mainHead, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());

        var conflictAgain = await service.MergeBranchAsync(
            repositoryRoot,
            "feature",
            mainHead,
            CancellationToken.None);
        Assert.Equal(MergeOutcome.Conflicts, conflictAgain.Outcome);
        WriteFile("shared.txt", "resolved\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var continued = await service.ContinueMergeAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(MergeOutcome.Completed, continued.Outcome);
        Assert.False(continued.State.IsInProgress);
        Assert.Empty(continued.State.UnmergedPaths);
        Assert.Equal("resolved\n", File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));
    }

    [Fact]
    public async Task StagingRejectsConflictMarkersButAllowsLiteralMarkersInOrdinaryFiles()
    {
        WriteFile(".gitattributes", "shared.txt conflict-marker-size=10\n");
        WriteFile("shared.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("shared.txt", "feature\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "feature edit", null, amend: false, CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("shared.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "main edit", null, amend: false, CancellationToken.None);
        var mainHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        var conflict = await service.MergeBranchAsync(
            repositoryRoot,
            "feature",
            mainHead,
            CancellationToken.None);
        Assert.Equal(MergeOutcome.Conflicts, conflict.Outcome);
        Assert.Contains(
            "<<<<<<<<<< HEAD",
            File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));

        WriteFile("literal-markers.txt", "<<<<<<< literal\n=======\n>>>>>>> literal\n");
        await service.StageFilesAsync(repositoryRoot, ["literal-markers.txt"], CancellationToken.None);

        var markerError = await Assert.ThrowsAsync<UnresolvedConflictMarkersException>(
            () => service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None));
        Assert.Equal(["shared.txt"], markerError.Paths);
        var unresolvedStatus = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var unresolved = Assert.Single(unresolvedStatus.Changes, change => change.Path == "shared.txt");
        Assert.Equal(ChangeKind.Conflicted, unresolved.Kind);
        Assert.Equal("A", Assert.Single(
            unresolvedStatus.Changes,
            change => change.Path == "literal-markers.txt").IndexStatus);

        WriteFile("shared.txt", "resolved\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        var continued = await service.ContinueMergeAsync(repositoryRoot, CancellationToken.None);

        Assert.Equal(MergeOutcome.Completed, continued.Outcome);
        Assert.Equal("resolved\n", File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));
        Assert.Equal(
            "<<<<<<< literal\n=======\n>>>>>>> literal\n",
            File.ReadAllText(Path.Combine(repositoryRoot, "literal-markers.txt")));
    }

    [Fact]
    public async Task OperationStatePrefersRebaseAndTreatsSquashMessageAsNonActive()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("shared.txt", "feature\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "feature edit", null, amend: false, CancellationToken.None);
        var featureHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("shared.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "main edit", null, amend: false, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);

        Assert.Equal(1, RunGitExitCode(repositoryRoot, "rebase", "main"));
        var rebaseDirectory = ResolveGitPath("rebase-merge");
        Assert.True(Directory.Exists(rebaseDirectory));
        var cherryPickHead = ResolveGitPath("CHERRY_PICK_HEAD");
        // Older Git rebase backends may leave this marker alongside the rebase
        // directory when applying a conflicted commit.
        File.WriteAllText(cherryPickHead, featureHead + "\n", Utf8NoBom);
        var mergeHead = ResolveGitPath("MERGE_HEAD");
        File.WriteAllText(mergeHead, featureHead + "\n", Utf8NoBom);

        var rebaseState = await service.GetMergeStateAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(GitOperationKind.Rebase, rebaseState.OperationKind);
        Assert.True(rebaseState.HasUnresolvedConflicts);

        File.Delete(cherryPickHead);
        File.Delete(mergeHead);
        RunGit(repositoryRoot, "rebase", "--abort");

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        await service.CreateBranchAsync(repositoryRoot, "squash-source", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "squash-source", CancellationToken.None);
        WriteFile("squash.txt", "squash content\n");
        await service.StageFilesAsync(repositoryRoot, ["squash.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "squash source", null, amend: false, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        Assert.Equal(0, RunGitExitCode(repositoryRoot, "merge", "--squash", "squash-source"));

        var squashState = await service.GetMergeStateAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(GitOperationKind.None, squashState.OperationKind);
        Assert.True(squashState.IsSquash);
        Assert.False(squashState.IsInProgress);
        var abortError = await Assert.ThrowsAsync<MergeOperationBlockedException>(
            () => service.AbortMergeAsync(repositoryRoot, CancellationToken.None));
        Assert.Equal(MergeOperationFailureReason.NotInProgress, abortError.Reason);
        RunGit(repositoryRoot, "reset", "--hard", "HEAD");
    }

    [Fact]
    public async Task ConfiguredNoCommitMergeReportsInProgressInsteadOfAlreadyUpToDate()
    {
        WriteFile("base.txt", "base\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("feature.txt", "feature\n");
        await service.StageFilesAsync(repositoryRoot, ["feature.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "feature edit", null, amend: false, CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("main.txt", "main\n");
        await service.StageFilesAsync(repositoryRoot, ["main.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "main edit", null, amend: false, CancellationToken.None);
        RunGit(repositoryRoot, "config", "branch.main.mergeOptions", "--no-commit --no-ff");
        var mainHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        var result = await service.MergeBranchAsync(
            repositoryRoot,
            "feature",
            mainHead,
            CancellationToken.None);

        Assert.Equal(MergeOutcome.InProgress, result.Outcome);
        Assert.Equal(mainHead, result.HeadId);
        Assert.True(result.State.IsMergeInProgress);
        Assert.Empty(result.State.UnmergedPaths);

        var aborted = await service.AbortMergeAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(MergeOutcome.Aborted, aborted.Outcome);
        Assert.False(aborted.State.IsInProgress);
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

    private static int RunGitExitCode(string workingDirectory, params string[] arguments)
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
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(outputTask, errorTask);
        return process.ExitCode;
    }

    private string ResolveGitPath(string pathName)
    {
        var reportedPath = RunGit(repositoryRoot, "rev-parse", "--git-path", pathName).Trim();
        return Path.GetFullPath(
            Path.IsPathRooted(reportedPath)
                ? reportedPath
                : Path.Combine(repositoryRoot, reportedPath));
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
