using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryBranchCreationTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryBranchCreationTests()
    {
        repositoryRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        RunGit(repositoryRoot, "init");
        RunGit(repositoryRoot, "config", "user.name", "Test User");
        RunGit(repositoryRoot, "config", "user.email", "test-user@example.invalid");
        var hooksPath = Path.Combine(repositoryRoot, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(repositoryRoot, "config", "core.hooksPath", hooksPath);
        RunGit(repositoryRoot, "config", "commit.gpgsign", "false");
    }

    [Fact]
    public async Task CreateFromValidatedStartCommitLeavesHeadAndWorktreeUnchanged()
    {
        WriteFile("root.txt", "first\n");
        Commit("first");
        var firstCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        var currentName = RunGit(repositoryRoot, "branch", "--show-current").Trim();
        WriteFile("root.txt", "second\n");
        Commit("second");
        var secondCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        var service = new GitRepositoryService();

        var plan = await service.CaptureBranchCreationPlanAsync(
            repositoryRoot,
            "feature/created",
            firstCommit,
            CancellationToken.None);
        Assert.Equal(firstCommit, plan.StartCommitId);

        var result = await service.CreateBranchAsync(repositoryRoot, plan, CancellationToken.None);
        Assert.Equal("feature/created", result.BranchName);
        Assert.Equal(firstCommit, result.CommitId);

        var branches = await service.GetBranchesAsync(repositoryRoot, CancellationToken.None);
        var created = Assert.Single(branches, branch => branch.Name == "feature/created");
        Assert.Equal($"refs/heads/feature/created", created.FullRef);
        Assert.False(created.IsCurrent);
        Assert.Equal(firstCommit, RunGit(repositoryRoot, "rev-parse", "refs/heads/feature/created").Trim());

        // The checkout is untouched: HEAD stays on the original branch tip.
        Assert.Equal(currentName, RunGit(repositoryRoot, "branch", "--show-current").Trim());
        Assert.Equal(secondCommit, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());
        Assert.Equal("second\n", File.ReadAllText(Path.Combine(repositoryRoot, "root.txt")));
        Assert.Empty((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
    }

    [Fact]
    public async Task InvalidAndExistingNamesFailWithoutChangingRepository()
    {
        WriteFile("root.txt", "root\n");
        Commit("root");
        var head = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        var currentName = RunGit(repositoryRoot, "branch", "--show-current").Trim();
        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature/taken", null, CancellationToken.None);

        // check-ref-format rejects this name through the existing Git boundary.
        await Assert.ThrowsAnyAsync<Exception>(
            () => service.CaptureBranchCreationPlanAsync(
                repositoryRoot,
                "feature..invalid",
                null,
                CancellationToken.None));

        var existingError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CaptureBranchCreationPlanAsync(
                repositoryRoot,
                "feature/taken",
                null,
                CancellationToken.None));
        Assert.Contains("already exists", existingError.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(head, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());
        Assert.Equal(currentName, RunGit(repositoryRoot, "branch", "--show-current").Trim());
        Assert.Equal(
            head,
            RunGit(repositoryRoot, "rev-parse", "refs/heads/feature/taken").Trim());
        Assert.DoesNotContain(
            await service.GetBranchesAsync(repositoryRoot, CancellationToken.None),
            branch => branch.Name == "feature..invalid");
        Assert.Equal("root\n", File.ReadAllText(Path.Combine(repositoryRoot, "root.txt")));
        Assert.Empty((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
    }

    [Fact]
    public async Task ChangedStartingRefRefusesCreationAndPreservesState()
    {
        WriteFile("root.txt", "first\n");
        Commit("first");
        var currentName = RunGit(repositoryRoot, "branch", "--show-current").Trim();
        var service = new GitRepositoryService();

        var plan = await service.CaptureBranchCreationPlanAsync(
            repositoryRoot,
            "feature/stale-start",
            currentName,
            CancellationToken.None);

        // Advance the starting branch after the plan was captured.
        WriteFile("root.txt", "second\n");
        Commit("second");
        var advancedHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        var staleError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateBranchAsync(repositoryRoot, plan, CancellationToken.None));
        Assert.Contains("changed", staleError.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            await service.GetBranchesAsync(repositoryRoot, CancellationToken.None),
            branch => branch.Name == "feature/stale-start");
        Assert.Equal(currentName, RunGit(repositoryRoot, "branch", "--show-current").Trim());
        Assert.Equal(advancedHead, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());
        Assert.Equal("second\n", File.ReadAllText(Path.Combine(repositoryRoot, "root.txt")));
        Assert.Empty((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
    }

    [Fact]
    public async Task MissingStartingRefFailsWithoutChangingRepository()
    {
        WriteFile("root.txt", "root\n");
        Commit("root");
        var head = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        var currentName = RunGit(repositoryRoot, "branch", "--show-current").Trim();
        var service = new GitRepositoryService();

        var missingError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CaptureBranchCreationPlanAsync(
                repositoryRoot,
                "feature/missing-start",
                "refs/heads/does-not-exist",
                CancellationToken.None));
        Assert.Contains("starting point", missingError.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            await service.GetBranchesAsync(repositoryRoot, CancellationToken.None),
            branch => branch.Name == "feature/missing-start");
        Assert.Equal(head, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());
        Assert.Equal(currentName, RunGit(repositoryRoot, "branch", "--show-current").Trim());
        Assert.Empty((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
    }

    public void Dispose()
    {
        DeleteDirectory(repositoryRoot);
    }

    private void WriteFile(string relativePath, string contents)
    {
        var fullPath = Path.Combine(repositoryRoot, relativePath);
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
