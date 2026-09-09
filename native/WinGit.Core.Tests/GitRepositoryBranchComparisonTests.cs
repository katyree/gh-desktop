using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryBranchComparisonTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string fixtureRoot;

    public GitRepositoryBranchComparisonTests()
    {
        Directory.CreateDirectory(FixtureParent);
        fixtureRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
    }

    [Fact]
    public async Task ComparisonCountsAndRangesUseCurrentBranchDirection()
    {
        var repositoryRoot = CreateDivergedRepository();
        var service = new GitRepositoryService();
        var baseHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        var comparisonHead = RunGit(repositoryRoot, "rev-parse", "feature").Trim();

        var ahead = await service.CaptureBranchComparisonAsync(
            repositoryRoot,
            "feature",
            ComparisonMode.Ahead,
            maxCommits: 100,
            CancellationToken.None);
        Assert.NotNull(ahead);
        Assert.Equal(baseHead, ahead.BaseHeadId);
        Assert.Equal(comparisonHead, ahead.ComparisonHeadId);
        Assert.Equal(2, ahead.Ahead);
        Assert.Equal(1, ahead.Behind);
        Assert.Equal($"{baseHead}...{comparisonHead}", ahead.SymmetricRange);
        Assert.Equal($"{comparisonHead}..{baseHead}", ahead.RevisionRange);
        Assert.Equal(2, ahead.Commits.Count);
        Assert.Equal("main-only-2", ahead.Commits[0].Summary);
        Assert.Equal("main-only", ahead.Commits[1].Summary);
        Assert.False(ahead.CommitsTruncated);

        var behind = await service.CaptureBranchComparisonAsync(
            repositoryRoot,
            "feature",
            ComparisonMode.Behind,
            maxCommits: 100,
            CancellationToken.None);
        Assert.NotNull(behind);
        Assert.Equal($"{baseHead}..{comparisonHead}", behind.RevisionRange);
        var behindCommit = Assert.Single(behind.Commits);
        Assert.Equal("feature-only", behindCommit.Summary);
    }

    [Fact]
    public async Task RevalidationRejectsComparisonReferenceAfterItMoves()
    {
        var repositoryRoot = CreateDivergedRepository();
        var service = new GitRepositoryService();
        var snapshot = await service.CaptureBranchComparisonAsync(
            repositoryRoot,
            "feature",
            ComparisonMode.Behind,
            maxCommits: 100,
            CancellationToken.None);
        Assert.NotNull(snapshot);
        await service.RevalidateBranchComparisonSnapshotAsync(
            repositoryRoot,
            snapshot,
            CancellationToken.None);

        var baseHead = RunGit(repositoryRoot, "rev-parse", "main").Trim();
        var featureHead = snapshot.ComparisonHeadId;
        RunGit(repositoryRoot, "branch", featureHead, baseHead);
        var fullIdComparison = await service.CaptureBranchComparisonAsync(
            repositoryRoot,
            featureHead,
            ComparisonMode.Behind,
            maxCommits: 1,
            CancellationToken.None);
        Assert.NotNull(fullIdComparison);
        Assert.Equal(featureHead, fullIdComparison.ComparisonHeadId);
        Assert.Equal(1, fullIdComparison.Behind);
        Assert.Equal("feature-only", Assert.Single(fullIdComparison.Commits).Summary);

        var explicitBranchComparison = await service.CaptureBranchComparisonAsync(
            repositoryRoot,
            $"refs/heads/{featureHead}",
            ComparisonMode.Behind,
            maxCommits: 1,
            CancellationToken.None);
        Assert.NotNull(explicitBranchComparison);
        Assert.Equal(baseHead, explicitBranchComparison.ComparisonHeadId);
        Assert.Equal(0, explicitBranchComparison.Behind);

        RunGit(repositoryRoot, "checkout", "feature");
        WriteFile(repositoryRoot, "feature-second.txt", "feature second\n");
        RunGit(repositoryRoot, "add", "--all");
        RunGit(repositoryRoot, "commit", "--message", "feature-second");
        RunGit(repositoryRoot, "checkout", "main");

        await Assert.ThrowsAsync<BranchComparisonSnapshotStaleException>(
            () => service.RevalidateBranchComparisonSnapshotAsync(
                repositoryRoot,
                snapshot,
                CancellationToken.None));
    }

    public void Dispose()
    {
        DeleteDirectory(fixtureRoot);
    }

    private string CreateDivergedRepository()
    {
        var repositoryRoot = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        RunGit(repositoryRoot, "init", "--initial-branch", "main");
        RunGit(repositoryRoot, "config", "user.name", "Test User");
        RunGit(repositoryRoot, "config", "user.email", "test-user@example.invalid");
        var hooksPath = Path.Combine(repositoryRoot, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(repositoryRoot, "config", "core.hooksPath", hooksPath);
        RunGit(repositoryRoot, "config", "commit.gpgsign", "false");

        WriteFile(repositoryRoot, "base.txt", "base\n");
        Commit(repositoryRoot, "initial");
        RunGit(repositoryRoot, "branch", "feature");

        WriteFile(repositoryRoot, "main.txt", "main\n");
        Commit(repositoryRoot, "main-only");

        WriteFile(repositoryRoot, "main-second.txt", "main second\n");
        Commit(repositoryRoot, "main-only-2");

        RunGit(repositoryRoot, "checkout", "feature");
        WriteFile(repositoryRoot, "feature.txt", "feature\n");
        Commit(repositoryRoot, "feature-only");
        RunGit(repositoryRoot, "checkout", "main");
        return repositoryRoot;
    }

    private static void Commit(string repositoryRoot, string message)
    {
        RunGit(repositoryRoot, "add", "--all");
        RunGit(repositoryRoot, "commit", "--message", message);
    }

    private static void WriteFile(string repositoryRoot, string relativePath, string contents)
    {
        var path = Path.Combine(repositoryRoot, relativePath);
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
