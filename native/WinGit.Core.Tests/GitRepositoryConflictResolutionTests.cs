using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryConflictResolutionTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryConflictResolutionTests()
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
    public async Task SnapshotAndApplyPreserveOutsideTextAndDefaultIndexPolicy()
    {
        WriteFile("shared.txt", "prefix\nbase\nsuffix\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("shared.txt", "prefix\nfeature\nsuffix\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "feature edit", null, amend: false, CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("shared.txt", "prefix\nmain\nsuffix\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "main edit", null, amend: false, CancellationToken.None);
        RunGit(repositoryRoot, "config", "merge.conflictStyle", "diff3");
        var mainHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        var conflict = await service.MergeBranchAsync(
            repositoryRoot,
            "feature",
            mainHead,
            CancellationToken.None);
        Assert.Equal(MergeOutcome.Conflicts, conflict.Outcome);

        var snapshot = await service.GetConflictFileSnapshotAsync(
            repositoryRoot,
            "shared.txt",
            CancellationToken.None);
        Assert.True(snapshot.IsSupported);
        Assert.Equal(ConflictFileKind.Text, snapshot.Kind);
        Assert.Equal(mainHead, snapshot.ExpectedHeadId);
        Assert.Equal(3, snapshot.IndexStages.Count);
        Assert.Equal([1, 2, 3], snapshot.IndexStages.Select(stage => stage.StageNumber));
        var hunk = Assert.Single(snapshot.Hunks);
        Assert.Equal("base", hunk.BaseContent);
        Assert.Equal("prefix", hunk.ContextBefore);
        Assert.Equal("suffix", hunk.ContextAfter);

        var defaultPolicy = await service.ApplyConflictResolutionAsync(
            repositoryRoot,
            snapshot,
            new ConflictResolutionRequest(
                [new ConflictHunkReplacement(0, "main + feature")]),
            CancellationToken.None);
        Assert.False(defaultPolicy.WasDeleted);
        Assert.False(defaultPolicy.WasStaged);
        Assert.NotEmpty(defaultPolicy.RemainingIndexStages);
        Assert.Equal(
            "prefix\nmain + feature\nsuffix\n",
            File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));

        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var stillUnmerged = Assert.Single(status.Changes);
        Assert.Equal(ChangeKind.Conflicted, stillUnmerged.Kind);
    }

    [Fact]
    public async Task ApplyRefusesAStaleWorkingFileWithoutWritingIt()
    {
        WriteFile("shared.txt", "prefix\nbase\nsuffix\n");
        Commit("root");

        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("shared.txt", "prefix\nfeature\nsuffix\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "feature edit", null, amend: false, CancellationToken.None);

        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("shared.txt", "prefix\nmain\nsuffix\n");
        await service.StageFilesAsync(repositoryRoot, ["shared.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "main edit", null, amend: false, CancellationToken.None);
        var mainHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        var conflict = await service.MergeBranchAsync(
            repositoryRoot,
            "feature",
            mainHead,
            CancellationToken.None);
        Assert.Equal(MergeOutcome.Conflicts, conflict.Outcome);

        var snapshot = await service.GetConflictFileSnapshotAsync(
            repositoryRoot,
            "shared.txt",
            CancellationToken.None);
        var staleContents = "prefix\ncaller changed the file\nsuffix\n";
        WriteFile("shared.txt", staleContents);

        var exception = await Assert.ThrowsAsync<ConflictFileSnapshotStaleException>(
            () => service.ApplyConflictResolutionAsync(
                repositoryRoot,
                snapshot,
                new ConflictResolutionRequest(
                    [new ConflictHunkReplacement(0, "should not be written")]),
                CancellationToken.None));
        Assert.Equal("shared.txt", exception.Path);
        Assert.Equal(staleContents, File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));
        Assert.Equal(3, (await service.GetConflictFileSnapshotAsync(
            repositoryRoot,
            "shared.txt",
            CancellationToken.None)).IndexStages.Count);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ApplyConflictResolutionAsync(
                repositoryRoot,
                snapshot,
                new ConflictResolutionRequest(
                    [new ConflictHunkReplacement(0, "must not be written")]),
                canceled.Token));
        Assert.Equal(staleContents, File.ReadAllText(Path.Combine(repositoryRoot, "shared.txt")));
        Assert.Empty(Directory.EnumerateFiles(
            repositoryRoot,
            ".shared.txt.wingit-*.tmp",
            SearchOption.TopDirectoryOnly));
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
