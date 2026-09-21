using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryLfsTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryLfsTests()
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
    public async Task LfsVersionReportsInstalledFilter()
    {
        var service = new GitRepositoryService();
        var version = await service.GetLfsVersionAsync(repositoryRoot, CancellationToken.None);

        Assert.NotNull(version);
        Assert.Matches(new Regex(@"^\d+\.\d+"), version);
    }

    [Fact]
    public async Task LfsUsageAndPathTrackingFollowAttributes()
    {
        var service = new GitRepositoryService();
        Assert.False(await service.IsUsingLfsAsync(repositoryRoot, CancellationToken.None));

        WriteFile(".gitattributes", "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        Assert.True(await service.IsUsingLfsAsync(repositoryRoot, CancellationToken.None));
        Assert.True(await service.IsTrackedByLfsAsync(repositoryRoot, "asset.bin", CancellationToken.None));
        Assert.False(await service.IsTrackedByLfsAsync(repositoryRoot, "notes.txt", CancellationToken.None));
        // Basename patterns match at any depth; this also covers subdirectory paths.
        Assert.True(await service.IsTrackedByLfsAsync(repositoryRoot, "nested/asset.bin", CancellationToken.None));
    }

    [Fact]
    public async Task InstallLfsHooksWritesRepositoryHooks()
    {
        var service = new GitRepositoryService();
        await service.InstallLfsHooksAsync(repositoryRoot, force: false, CancellationToken.None);

        var hooksDirectory = RunGit(repositoryRoot, "rev-parse", "--git-path", "hooks").Trim();
        var prePush = File.ReadAllText(Path.Combine(repositoryRoot, hooksDirectory, "pre-push"));
        Assert.Contains("git lfs pre-push", prePush, StringComparison.Ordinal);
        Assert.False(await service.IsUsingLfsAsync(repositoryRoot, CancellationToken.None));
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
