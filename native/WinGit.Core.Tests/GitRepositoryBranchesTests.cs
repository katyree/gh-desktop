using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryBranchesTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryBranchesTests()
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
    public async Task CreateRenameListAndSafeDeleteBranch()
    {
        WriteFile("root.txt", "root\n");
        Commit("root");
        var currentName = RunGit(repositoryRoot, "branch", "--show-current").Trim();
        var service = new GitRepositoryService();

        var initial = await service.GetBranchesAsync(repositoryRoot, CancellationToken.None);
        var current = Assert.Single(initial, branch => branch.IsCurrent);
        Assert.Equal(currentName, current.Name);
        Assert.Equal($"refs/heads/{currentName}", current.FullRef);
        Assert.Equal(Path.GetFullPath(repositoryRoot), current.WorktreePath);

        await service.CreateBranchAsync(repositoryRoot, "feature/demo", null, CancellationToken.None);
        var created = Assert.Single(
            await service.GetBranchesAsync(repositoryRoot, CancellationToken.None),
            branch => branch.Name == "feature/demo");
        Assert.Equal("refs/heads/feature/demo", created.FullRef);
        Assert.False(created.IsCurrent);

        await service.RenameBranchAsync(
            repositoryRoot,
            "feature/demo",
            "feature/renamed",
            CancellationToken.None);
        var renamed = await service.GetBranchesAsync(repositoryRoot, CancellationToken.None);
        Assert.DoesNotContain(renamed, branch => branch.Name == "feature/demo");
        Assert.Contains(renamed, branch => branch.Name == "feature/renamed");

        await service.DeleteBranchAsync(repositoryRoot, "feature/renamed", force: false, CancellationToken.None);
        Assert.DoesNotContain(
            await service.GetBranchesAsync(repositoryRoot, CancellationToken.None),
            branch => branch.Name == "feature/renamed");
    }

    [Fact]
    public async Task CheckoutPreservesDirtyBlockingBehavior()
    {
        WriteFile("tracked.txt", "base\n");
        Commit("root");
        var currentName = RunGit(repositoryRoot, "branch", "--show-current").Trim();
        var service = new GitRepositoryService();
        await service.CreateBranchAsync(repositoryRoot, "feature/checkout", null, CancellationToken.None);

        WriteFile("tracked.txt", "main branch\n");
        Commit("main change");
        WriteFile("tracked.txt", "dirty local edit\n");

        await Assert.ThrowsAsync<GitCommandException>(
            () => service.CheckoutBranchAsync(repositoryRoot, "feature/checkout", CancellationToken.None));
        Assert.Equal(currentName, RunGit(repositoryRoot, "branch", "--show-current").Trim());
        Assert.Equal("dirty local edit\n", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));

        await Assert.ThrowsAsync<GitCommandException>(
            () => service.CheckoutBranchAsync(repositoryRoot, "tracked.txt", CancellationToken.None));
        Assert.Equal(currentName, RunGit(repositoryRoot, "branch", "--show-current").Trim());
        Assert.Equal("dirty local edit\n", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
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
