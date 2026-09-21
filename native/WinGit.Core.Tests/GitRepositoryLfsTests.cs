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

    [Fact]
    public void LfsProgressParserReadsDocumentedShapes()
    {
        var updates = GitRepositoryService.ParseLfsProgressLines([
            "download 1/2 1024/51200 asset.bin",
            "not a progress line",
            "upload 2/2 51200/51200 asset.bin",
            "checkout 1/1 51200/51200 asset.bin",
            "download x/y broken",
        ]);

        Assert.Equal(3, updates.Count);
        var first = updates[0];
        Assert.Equal(LfsTransferDirection.Download, first.Direction);
        Assert.Equal("asset.bin", first.FileName);
        Assert.Equal(1024, first.TransferredBytes);
        Assert.Equal(51200, first.TotalBytes);
        Assert.Equal(0, first.FinishedFiles);
        Assert.Equal(2, first.EstimatedFileCount);
        Assert.Contains("Downloading asset.bin", first.Text, StringComparison.Ordinal);

        var second = updates[1];
        Assert.Equal(LfsTransferDirection.Upload, second.Direction);
        Assert.Equal(51200, second.TransferredBytes);
        Assert.Equal(1, second.FinishedFiles);
        Assert.Contains("Uploading asset.bin", second.Text, StringComparison.Ordinal);

        var third = updates[2];
        Assert.Equal(LfsTransferDirection.Checkout, third.Direction);
        Assert.Contains("Checking out asset.bin", third.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void LfsFailureIdentificationNamesTheOperation()
    {
        var smudge = GitRepositoryService.IdentifyLfsFailure(
            new GitCommandException("failed", 2, "error: smudge filter lfs failed"),
            "fetch LFS objects");
        Assert.Contains("fetch LFS objects", smudge.Message, StringComparison.Ordinal);
        Assert.Contains("download", smudge.Message, StringComparison.OrdinalIgnoreCase);

        var auth = GitRepositoryService.IdentifyLfsFailure(
            new GitCommandException("failed", 128, "batch response: Authentication required"),
            "push LFS objects");
        Assert.Contains("push LFS objects", auth.Message, StringComparison.Ordinal);
        Assert.Contains("authentication", auth.Message, StringComparison.OrdinalIgnoreCase);

        var missing = GitRepositoryService.IdentifyLfsFailure(
            new GitCommandException("failed", 2, "This repository is configured for Git LFS but 'git-lfs' was not found on your path."),
            "fetch LFS objects");
        Assert.Contains("not available", missing.Message, StringComparison.OrdinalIgnoreCase);

        var plain = GitRepositoryService.IdentifyLfsFailure(
            new GitCommandException("failed", 128, "fatal: not a git repository"),
            "fetch LFS objects");
        Assert.Contains("fetch LFS objects", plain.Message, StringComparison.Ordinal);
        Assert.Contains("not a git repository", plain.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchLfsObjectsTransfersFromLocalRemote()
    {
        var fixtureRoot = Path.GetDirectoryName(repositoryRoot)!;
        var sourceRoot = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"));
        var cloneRoot = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceRoot);
        try
        {
            RunGit(sourceRoot, "init", "-b", "main");
            RunGit(sourceRoot, "config", "user.name", "Test User");
            RunGit(sourceRoot, "config", "user.email", "test-user@example.invalid");
            RunGit(sourceRoot, "config", "commit.gpgsign", "false");
            RunGit(sourceRoot, "lfs", "install", "--local");

            WriteFileAt(sourceRoot, ".gitattributes", "*.bin filter=lfs diff=lfs merge=lfs -text\n");
            var payload = new byte[51200];
            new Random(42).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(sourceRoot, "asset.bin"), payload);
            RunGit(sourceRoot, "add", "--all");
            RunGit(sourceRoot, "commit", "--message", "lfs asset");

            var environment = new Dictionary<string, string?> { ["GIT_LFS_SKIP_SMUDGE"] = "1" };
            RunGit(Path.GetTempPath(), environment, "clone", "--quiet", sourceRoot, cloneRoot);
            Assert.Equal(130L, new FileInfo(Path.Combine(cloneRoot, "asset.bin")).Length);

            var service = new GitRepositoryService();
            var fetched = await service.FetchLfsObjectsAsync(cloneRoot, remote: null, includeAll: false, CancellationToken.None);

            Assert.NotNull(fetched.ProgressUpdates);
            var objects = Directory.EnumerateFiles(
                Path.Combine(cloneRoot, ".git", "lfs", "objects"),
                "*",
                SearchOption.AllDirectories);
            Assert.NotEmpty(objects);
            Assert.True(await service.IsTrackedByLfsAsync(cloneRoot, "asset.bin", CancellationToken.None));
        }
        finally
        {
            DeleteDirectory(sourceRoot);
            DeleteDirectory(cloneRoot);
        }
    }

    public void Dispose()
    {
        DeleteDirectory(repositoryRoot);
    }

    private void WriteFile(string relativePath, string contents)
    {
        WriteFileAt(repositoryRoot, relativePath, contents);
    }

    private void WriteFileAt(string root, string relativePath, string contents)
    {
        var fullPath = Path.Combine(root, relativePath);
        File.WriteAllText(fullPath, contents, Utf8NoBom);
    }

    private static string RunGit(string workingDirectory, params string[] arguments) =>
        RunGit(workingDirectory, environment: null, arguments);

    private static string RunGit(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
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
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

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
