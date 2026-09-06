using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryCreationTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string fixtureRoot;

    public GitRepositoryCreationTests()
    {
        Directory.CreateDirectory(FixtureParent);
        fixtureRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
    }

    [Fact]
    public async Task InitializeTemplatesAndCloneAreUnbornSafeAndNonDestructive()
    {
        var collisionPath = Path.Combine(fixtureRoot, "collision");
        Directory.CreateDirectory(collisionPath);
        var existingReadme = Path.Combine(collisionPath, "README.md");
        File.WriteAllText(existingReadme, "keep this file\n", Utf8NoBom);
        var service = new GitRepositoryService();

        await Assert.ThrowsAsync<IOException>(
            () => service.InitializeAsync(
                collisionPath,
                "main",
                readmeContents: "replace me\n",
                cancellationToken: CancellationToken.None));
        Assert.Equal("keep this file\n", File.ReadAllText(existingReadme));
        Assert.False(Directory.Exists(Path.Combine(collisionPath, ".git")));

        var repositoryPath = Path.Combine(fixtureRoot, "initialized");
        var initialized = await service.InitializeAsync(
            repositoryPath,
            "main",
            readmeContents: "# Native repository\n",
            gitignoreContents: "*.tmp\n",
            licenseContents: "MIT\n",
            CancellationToken.None);
        Assert.Equal(Path.GetFullPath(repositoryPath), initialized.RootPath);
        Assert.Equal("main", initialized.Branch);
        Assert.True(initialized.IsUnborn);
        Assert.Equal(string.Empty, initialized.HeadId);
        Assert.Equal(3, initialized.Changes.Count);
        Assert.Equal("# Native repository\n", File.ReadAllText(Path.Combine(repositoryPath, "README.md")));
        Assert.Equal("*.tmp\n", File.ReadAllText(Path.Combine(repositoryPath, ".gitignore")));
        Assert.Equal("MIT\n", File.ReadAllText(Path.Combine(repositoryPath, "LICENSE")));

        ConfigureLocalCommitSafety(repositoryPath);
        RunGit(repositoryPath, "config", "user.name", "Test User");
        RunGit(repositoryPath, "config", "user.email", "test-user@example.invalid");
        RunGit(repositoryPath, "add", "--all");
        RunGit(repositoryPath, "commit", "--message", "initial content");
        var commitId = RunGit(repositoryPath, "rev-parse", "HEAD").Trim();

        var bareRemotePath = Path.Combine(fixtureRoot, "origin.git");
        RunGit(fixtureRoot, "init", "--bare", bareRemotePath);
        RunGit(repositoryPath, "remote", "add", "origin", bareRemotePath);
        RunGit(repositoryPath, "push", "origin", "main");

        var clonePath = Path.Combine(fixtureRoot, "clone");
        var cloned = await service.CloneAsync(
            bareRemotePath,
            clonePath,
            branch: "main",
            CancellationToken.None);
        Assert.Equal(Path.GetFullPath(clonePath), cloned.RootPath);
        Assert.Equal("main", cloned.Branch);
        Assert.False(cloned.IsUnborn);
        Assert.Equal(commitId, cloned.HeadId);
        Assert.Equal(
            "# Native repository\n",
            File.ReadAllText(Path.Combine(clonePath, "README.md")));
        Assert.True(
            PathsEqual(
                bareRemotePath,
                RunGit(clonePath, "remote", "get-url", "origin").Trim()));

        var occupiedPath = Path.Combine(fixtureRoot, "occupied");
        Directory.CreateDirectory(occupiedPath);
        var markerPath = Path.Combine(occupiedPath, "marker.txt");
        File.WriteAllText(markerPath, "preserve\n", Utf8NoBom);
        await Assert.ThrowsAsync<IOException>(
            () => service.CloneAsync(bareRemotePath, occupiedPath, "main", CancellationToken.None));
        Assert.Equal("preserve\n", File.ReadAllText(markerPath));
        Assert.False(Directory.Exists(Path.Combine(occupiedPath, ".git")));
    }

    public void Dispose()
    {
        DeleteDirectory(fixtureRoot);
    }

    private static void ConfigureLocalCommitSafety(string path)
    {
        var hooksPath = Path.Combine(path, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(path, "config", "core.hooksPath", hooksPath);
        RunGit(path, "config", "commit.gpgsign", "false");
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

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
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
