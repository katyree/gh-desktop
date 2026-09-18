using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

/// <summary>
/// Task 22 (generic operation lifecycle): prove stale-context protection and
/// safe retry for the representative branch-switch and merge workflows.
/// Retries revalidate captured context; failures preserve local work and never
/// repeat a mutation automatically.
/// </summary>
public sealed class GitRepositoryOperationLifecycleTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryOperationLifecycleTests()
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
    public async Task StaleCheckoutContextRefusesWithoutSwitching()
    {
        WriteFile("tracked.txt", "base\n");
        Commit("root");
        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var targetTip = RunGit(repositoryRoot, "rev-parse", "feature").Trim();

        var staleContext = new BranchCheckoutContext(
            status.RootPath,
            status.Branch,
            status.HeadId,
            "feature",
            targetTip);

        // The repository advances after the dialog capture: stale retry must
        // refuse without switching branches or touching the worktree.
        WriteFile("tracked.txt", "base\nsecond\n");
        Commit("advance main");

        var advancedHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        Assert.NotEqual(status.HeadId, advancedHead);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CheckoutBranchAsync(
                repositoryRoot,
                "feature",
                staleContext,
                CancellationToken.None));
        Assert.Contains("changed while", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("main", RunGit(repositoryRoot, "branch", "--show-current").Trim());
        Assert.Equal(advancedHead, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());
        Assert.Equal("base\nsecond\n", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));

        // A fresh capture (the safe retry path) succeeds.
        var fresh = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var freshTip = RunGit(repositoryRoot, "rev-parse", "feature").Trim();
        await service.CheckoutBranchAsync(
            repositoryRoot,
            "feature",
            new BranchCheckoutContext(fresh.RootPath, fresh.Branch, fresh.HeadId, "feature", freshTip),
            CancellationToken.None);
        Assert.Equal("feature", RunGit(repositoryRoot, "branch", "--show-current").Trim());
    }

    [Fact]
    public async Task FailedCheckoutPreservesLocalWorkForSafeRetry()
    {
        WriteFile("tracked.txt", "base\n");
        Commit("root");
        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);

        // Diverge the branches so a dirty checkout is refused by Git.
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("tracked.txt", "feature side\n");
        Commit("feature change");
        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        WriteFile("tracked.txt", "main side\n");
        Commit("main change");
        WriteFile("tracked.txt", "dirty local edit\n");

        // The failed mutation must not switch branches and must not discard
        // local work. Retry is an explicit caller action with fresh state.
        await Assert.ThrowsAsync<GitCommandException>(
            () => service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None));
        Assert.Equal("main", RunGit(repositoryRoot, "branch", "--show-current").Trim());
        Assert.Equal("dirty local edit\n", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
    }

    [Fact]
    public async Task StaleMergeHeadRefusesWithoutMutating()
    {
        WriteFile("shared.txt", "base\n");
        Commit("root");
        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature", null, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "feature", CancellationToken.None);
        WriteFile("feature.txt", "feature\n");
        await service.StageFilesAsync(repositoryRoot, ["feature.txt"], CancellationToken.None);
        await service.CommitAsync(repositoryRoot, "feature change", null, amend: false, CancellationToken.None);
        await service.CheckoutBranchAsync(repositoryRoot, "main", CancellationToken.None);
        var mainHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        WriteFile("shared.txt", "base\nmain advance\n");
        Commit("advance main");
        var advancedHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        Assert.NotEqual(mainHead, advancedHead);

        // A retry with the stale captured HEAD must refuse before running Git.
        var blocked = await Assert.ThrowsAsync<MergeOperationBlockedException>(
            () => service.MergeBranchAsync(repositoryRoot, "feature", mainHead, CancellationToken.None));
        Assert.Equal(MergeOperationFailureReason.StaleHead, blocked.Reason);
        Assert.Equal(advancedHead, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "feature.txt")));

        // A fresh capture succeeds and reports the real Git result.
        var result = await service.MergeBranchAsync(repositoryRoot, "feature", advancedHead, CancellationToken.None);
        Assert.Equal(MergeOutcome.Completed, result.Outcome);
        Assert.Equal("feature\n", File.ReadAllText(Path.Combine(repositoryRoot, "feature.txt")));
    }

    public void Dispose()
    {
        DeleteDirectory(repositoryRoot);
    }

    private void WriteFile(string relativePath, string contents)
    {
        File.WriteAllText(Path.Combine(repositoryRoot, relativePath), contents, Utf8NoBom);
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

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
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
