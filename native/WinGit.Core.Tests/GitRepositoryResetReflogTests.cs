using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryResetReflogTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryResetReflogTests()
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
    public async Task ResetModesPreserveOrDiscardTheExpectedFilesAndIndex()
    {
        WriteFile("tracked.txt", "base\n");
        Commit("root");
        var rootHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        WriteFile("tracked.txt", "committed\n");
        Commit("second");
        var secondHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        WriteFile("tracked.txt", "dirty\n");

        var service = new GitRepositoryService();
        var soft = await service.ResetAsync(
            repositoryRoot,
            GitResetMode.Soft,
            rootHead,
            secondHead,
            CancellationToken.None);

        Assert.Equal(GitResetMode.Soft, soft.Mode);
        Assert.Equal(secondHead, soft.PreviousHeadId);
        Assert.Equal(rootHead, soft.CurrentHeadId);
        Assert.Equal("main", soft.Branch);
        Assert.Equal("dirty\n", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
        var afterSoft = Assert.Single((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
        Assert.Equal("M", afterSoft.IndexStatus);
        Assert.Equal("M", afterSoft.WorkTreeStatus);

        var mixed = await service.ResetAsync(
            repositoryRoot,
            GitResetMode.Mixed,
            rootHead,
            rootHead,
            CancellationToken.None);
        Assert.Equal(GitResetMode.Mixed, mixed.Mode);
        Assert.Equal(rootHead, mixed.PreviousHeadId);
        Assert.Equal(rootHead, mixed.CurrentHeadId);
        Assert.Equal("dirty\n", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
        var afterMixed = Assert.Single((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
        Assert.Equal(string.Empty, afterMixed.IndexStatus);
        Assert.Equal("M", afterMixed.WorkTreeStatus);

        var hard = await service.ResetAsync(
            repositoryRoot,
            GitResetMode.Hard,
            rootHead,
            rootHead,
            CancellationToken.None);
        Assert.Equal(GitResetMode.Hard, hard.Mode);
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
        Assert.Empty((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
    }

    [Fact]
    public async Task StaleHeadIsBlockedAndReflogExposesRecoverableCommit()
    {
        WriteFile("tracked.txt", "root\n");
        Commit("root");
        var rootHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        WriteFile("tracked.txt", "second\n");
        Commit("second");
        var secondHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        var service = new GitRepositoryService();
        var stale = await Assert.ThrowsAsync<ResetOperationBlockedException>(
            () => service.ResetAsync(
                repositoryRoot,
                GitResetMode.Mixed,
                rootHead,
                rootHead,
                CancellationToken.None));
        Assert.Equal(ResetOperationFailureReason.StaleHead, stale.Reason);
        Assert.Equal(secondHead, stale.State.CurrentHeadId);

        var reset = await service.ResetAsync(
            repositoryRoot,
            GitResetMode.Mixed,
            rootHead,
            secondHead,
            CancellationToken.None);
        Assert.Equal(rootHead, reset.CurrentHeadId);

        var entries = await service.GetHeadReflogAsync(
            repositoryRoot,
            limit: 20,
            CancellationToken.None);
        Assert.Contains(entries, entry => entry.CommitId == secondHead);
        Assert.NotEmpty(entries);
        var latest = entries[0];
        Assert.StartsWith("HEAD@{", latest.Selector, StringComparison.Ordinal);
        Assert.NotEqual(default, latest.Date);
        Assert.NotEmpty(latest.Subject);
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
