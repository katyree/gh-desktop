using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryRemotesTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string fixtureRoot;
    private readonly string bareRemotePath;
    private readonly string editedRemotePath;
    private readonly string seedPath;
    private readonly string firstClonePath;
    private readonly string secondClonePath;

    public GitRepositoryRemotesTests()
    {
        Directory.CreateDirectory(FixtureParent);
        fixtureRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        bareRemotePath = Path.Combine(fixtureRoot, "remote.git");
        editedRemotePath = Path.Combine(fixtureRoot, "edited.git");
        seedPath = Path.Combine(fixtureRoot, "seed");
        firstClonePath = Path.Combine(fixtureRoot, "first");
        secondClonePath = Path.Combine(fixtureRoot, "second");
    }

    [Fact]
    public async Task LocalRemoteTransportSupportsEditPushFetchPullAndFfOnlyRefusal()
    {
        RunGit(fixtureRoot, "init", "--bare", bareRemotePath);
        RunGit(fixtureRoot, "init", "--bare", editedRemotePath);
        Directory.CreateDirectory(seedPath);
        RunGit(seedPath, "init", "-b", "main");
        ConfigureLocalCommitSafety(seedPath);
        RunGit(seedPath, "config", "user.name", "Test User");
        RunGit(seedPath, "config", "user.email", "test-user@example.invalid");
        WriteFile(seedPath, "tracked.txt", "seed\n");
        Commit(seedPath, "seed");

        var seedService = new GitRepositoryService();
        var added = await seedService.AddRemoteAsync(
            seedPath,
            "mirror",
            bareRemotePath,
            CancellationToken.None);
        Assert.Equal("mirror", added.Name);
        Assert.Equal(Path.GetFullPath(bareRemotePath), added.Url);
        Assert.Contains(
            await seedService.GetRemotesAsync(seedPath, CancellationToken.None),
            remote => remote.Name == "mirror" && remote.Url == Path.GetFullPath(bareRemotePath));

        var edited = await seedService.SetRemoteUrlAsync(
            seedPath,
            "mirror",
            editedRemotePath,
            CancellationToken.None);
        Assert.Equal(Path.GetFullPath(editedRemotePath), edited.Url);
        Assert.Contains(
            await seedService.GetRemotesAsync(seedPath, CancellationToken.None),
            remote => remote.Name == "mirror" && remote.Url == Path.GetFullPath(editedRemotePath));
        await seedService.RemoveRemoteAsync(seedPath, "mirror", CancellationToken.None);
        Assert.DoesNotContain(
            await seedService.GetRemotesAsync(seedPath, CancellationToken.None),
            remote => remote.Name == "mirror");

        await seedService.AddRemoteAsync(seedPath, "origin", bareRemotePath, CancellationToken.None);
        await seedService.PushAsync(seedPath, "origin", "main", "main", CancellationToken.None);
        RunGit(fixtureRoot, "clone", "--branch", "main", bareRemotePath, firstClonePath);
        RunGit(fixtureRoot, "clone", "--branch", "main", bareRemotePath, secondClonePath);
        ConfigureClone(firstClonePath);
        ConfigureClone(secondClonePath);

        WriteFile(firstClonePath, "from-first.txt", "first remote commit\n");
        Commit(firstClonePath, "first remote commit");
        var firstService = new GitRepositoryService();
        await firstService.PushAsync(firstClonePath, "origin", "main", "main", CancellationToken.None);
        var firstCommitId = RunGit(firstClonePath, "rev-parse", "HEAD").Trim();

        var secondService = new GitRepositoryService();
        await secondService.FetchAsync(secondClonePath, "origin", CancellationToken.None);
        Assert.Equal(
            firstCommitId,
            RunGit(secondClonePath, "rev-parse", "refs/remotes/origin/main").Trim());
        await secondService.PullFastForwardOnlyAsync(
            secondClonePath,
            "origin",
            "main",
            "main",
            CancellationToken.None);
        Assert.Equal(
            "first remote commit\n",
            File.ReadAllText(Path.Combine(secondClonePath, "from-first.txt")));

        WriteFile(secondClonePath, "from-second.txt", "second local commit\n");
        Commit(secondClonePath, "second local commit");
        var secondCommitId = RunGit(secondClonePath, "rev-parse", "HEAD").Trim();
        WriteFile(firstClonePath, "from-first-again.txt", "new remote commit\n");
        Commit(firstClonePath, "new remote commit");
        await firstService.PushAsync(firstClonePath, "origin", "main", "main", CancellationToken.None);
        WriteFile(secondClonePath, "keep-dirty.txt", "keep this local edit\n");

        await Assert.ThrowsAsync<GitCommandException>(
            () => secondService.PullFastForwardOnlyAsync(
                secondClonePath,
                "origin",
                "main",
                "main",
                CancellationToken.None));
        Assert.Equal(secondCommitId, RunGit(secondClonePath, "rev-parse", "HEAD").Trim());
        Assert.Equal(
            "second local commit\n",
            File.ReadAllText(Path.Combine(secondClonePath, "from-second.txt")));
        Assert.Equal(
            "keep this local edit\n",
            File.ReadAllText(Path.Combine(secondClonePath, "keep-dirty.txt")));
    }

    public void Dispose()
    {
        DeleteDirectory(fixtureRoot);
    }

    private static void ConfigureClone(string path)
    {
        RunGit(path, "config", "user.name", "Test User");
        RunGit(path, "config", "user.email", "test-user@example.invalid");
        ConfigureLocalCommitSafety(path);
    }

    private static void ConfigureLocalCommitSafety(string path)
    {
        var hooksPath = Path.Combine(path, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(path, "config", "core.hooksPath", hooksPath);
        RunGit(path, "config", "commit.gpgsign", "false");
    }

    private static void WriteFile(string root, string relativePath, string contents)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents, Utf8NoBom);
    }

    private static void Commit(string root, string message)
    {
        RunGit(root, "add", "--all");
        RunGit(root, "commit", "--message", message);
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
