using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryCommitComposerTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string fixtureRoot;

    public GitRepositoryCommitComposerTests()
    {
        Directory.CreateDirectory(FixtureParent);
        fixtureRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
    }

    [Fact]
    public async Task CommitMessageWithCoAuthorsAndSignOffProducesIntendedMetadata()
    {
        var repositoryRoot = Path.Combine(fixtureRoot, "attribution");
        CreateRepository(repositoryRoot);
        WriteFile(repositoryRoot, "staged.txt", "staged\n");
        WriteFile(repositoryRoot, "other.txt", "other\n");
        RunGit(repositoryRoot, "add", "--all");
        RunGit(repositoryRoot, "commit", "--message", "initial");

        WriteFile(repositoryRoot, "staged.txt", "staged change\n");
        WriteFile(repositoryRoot, "unstaged.txt", "unstaged change\n");
        var service = new GitRepositoryService();
        await service.StageFilesAsync(repositoryRoot, ["staged.txt"], CancellationToken.None);

        var coAuthor = GitRepositoryService.ParseCoAuthorTrailer("A11y Ally <ally@example.invalid>");
        var commitId = await service.CommitAsync(
            repositoryRoot,
            "Composer summary",
            "First paragraph\n\nSecond paragraph",
            amend: false,
            CancellationToken.None,
            expectedHeadId: null,
            trailers: [coAuthor],
            signOff: true);

        Assert.Equal(RunGit(repositoryRoot, "rev-parse", "HEAD").Trim(), commitId);
        var body = RunGit(repositoryRoot, "log", "-1", "--format=%B");
        Assert.Contains("Composer summary", body);
        Assert.Contains("First paragraph", body);
        Assert.Contains("Second paragraph", body);
        Assert.Contains("Co-Authored-By: A11y Ally <ally@example.invalid>", body);
        Assert.Contains("Signed-off-by: Test User <test-user@example.invalid>", body);

        var author = RunGit(repositoryRoot, "log", "-1", "--format=%an%x00%ae").Trim();
        Assert.Equal("Test User\0test-user@example.invalid", author);

        var files = await service.GetCommitFilesAsync(repositoryRoot, commitId, CancellationToken.None);
        var file = Assert.Single(files);
        Assert.Equal("staged.txt", file.Path);

        // Unrelated unstaged worktree content and the untracked file stay put.
        Assert.Equal("staged change\n", File.ReadAllText(Path.Combine(repositoryRoot, "staged.txt")));
        Assert.Equal("unstaged change\n", File.ReadAllText(Path.Combine(repositoryRoot, "unstaged.txt")));
        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var remaining = Assert.Single(status.Changes);
        Assert.Equal("unstaged.txt", remaining.Path);
    }

    [Fact]
    public async Task InvalidCoAuthorIsRejectedWithoutChangingIndex()
    {
        var repositoryRoot = Path.Combine(fixtureRoot, "invalid-coauthor");
        CreateRepository(repositoryRoot);
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        RunGit(repositoryRoot, "add", "--all");
        RunGit(repositoryRoot, "commit", "--message", "initial");
        WriteFile(repositoryRoot, "tracked.txt", "staged\n");
        RunGit(repositoryRoot, "add", "--", "tracked.txt");
        var indexBefore = RunGit(repositoryRoot, "rev-parse", ":tracked.txt").Trim();

        var service = new GitRepositoryService();
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CommitAsync(
                repositoryRoot,
                "summary",
                null,
                amend: false,
                CancellationToken.None,
                expectedHeadId: null,
                trailers: [new CommitTrailer("", "missing-token")],
                signOff: false));

        Assert.Throws<ArgumentException>(() => GitRepositoryService.ParseCoAuthorTrailer("missing-brackets"));
        Assert.Equal(indexBefore, RunGit(repositoryRoot, "rev-parse", ":tracked.txt").Trim());
        Assert.Equal("initial", RunGit(repositoryRoot, "show", "-s", "--format=%s", "HEAD").Trim());
    }

    [Fact]
    public async Task AmendWithAttributionKeepsMessageAndStaysClean()
    {
        var repositoryRoot = Path.Combine(fixtureRoot, "amend-attribution");
        CreateRepository(repositoryRoot);
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        RunGit(repositoryRoot, "add", "--all");
        RunGit(repositoryRoot, "commit", "--message", "initial");
        var before = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        WriteFile(repositoryRoot, "tracked.txt", "amended\n");
        var service = new GitRepositoryService();
        await service.StageFilesAsync(repositoryRoot, ["tracked.txt"], CancellationToken.None);

        var coAuthor = GitRepositoryService.ParseCoAuthorTrailer("A11y Ally <ally@example.invalid>");
        var amendedId = await service.CommitAsync(
            repositoryRoot,
            "Amended summary",
            "Amended body",
            amend: true,
            CancellationToken.None,
            expectedHeadId: before,
            trailers: [coAuthor],
            signOff: true);

        Assert.NotEqual(before, amendedId);
        var body = RunGit(repositoryRoot, "log", "-1", "--format=%B");
        Assert.Contains("Amended summary", body);
        Assert.Contains("Amended body", body);
        Assert.Contains("Co-Authored-By: A11y Ally <ally@example.invalid>", body);
        Assert.Contains("Signed-off-by:", body);
        Assert.True((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes.Count == 0);
    }

    public void Dispose()
    {
        DeleteDirectory(fixtureRoot);
    }

    private static void CreateRepository(string path)
    {
        Directory.CreateDirectory(path);
        RunGit(path, "init", "--initial-branch", "main");
        RunGit(path, "config", "user.name", "Test User");
        RunGit(path, "config", "user.email", "test-user@example.invalid");
        var hooksPath = Path.Combine(path, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(path, "config", "core.hooksPath", hooksPath);
        RunGit(path, "config", "commit.gpgsign", "false");
    }

    private static void WriteFile(string repositoryRoot, string relativePath, string contents)
    {
        var fullPath = Path.Combine(repositoryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
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
