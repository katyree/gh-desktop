using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryCommitMessageSnapshotTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string fixtureRoot;

    public GitRepositoryCommitMessageSnapshotTests()
    {
        Directory.CreateDirectory(FixtureParent);
        fixtureRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
    }

    [Fact]
    public async Task SnapshotUsesStagedBytesRejectsRestageAndUsesAmendParent()
    {
        var repositoryRoot = Path.Combine(fixtureRoot, "repository");
        CreateRepository(repositoryRoot);
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        WriteFile(repositoryRoot, "other.txt", "other base\n");
        RunGit(repositoryRoot, "add", "--all");
        RunGit(repositoryRoot, "commit", "--message", "initial");
        var initialHead = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        var branch = RunGit(repositoryRoot, "branch", "--show-current").Trim();

        WriteFile(repositoryRoot, "tracked.txt", "staged\n");
        RunGit(repositoryRoot, "add", "--", "tracked.txt");
        WriteFile(repositoryRoot, "tracked.txt", "staged plus unstaged\n");

        var service = new GitRepositoryService();
        var snapshot = await service.CaptureCommitMessageSnapshotAsync(
            repositoryRoot,
            amend: false,
            CancellationToken.None);
        var rawPatch = Encoding.UTF8.GetString(snapshot.RawPatch.Span);
        Assert.Equal(Path.GetFullPath(repositoryRoot), snapshot.RootPath);
        Assert.Equal(initialHead, snapshot.HeadId);
        Assert.Equal(branch, snapshot.Branch);
        Assert.False(snapshot.IsDetached);
        Assert.False(snapshot.IsUnborn);
        Assert.Equal(initialHead, snapshot.BaselineId);
        Assert.NotEmpty(snapshot.IndexFingerprint);
        Assert.Contains("staged", rawPatch);
        Assert.DoesNotContain("unstaged", rawPatch);
        await service.RevalidateCommitMessageSnapshotAsync(
            repositoryRoot,
            snapshot,
            CancellationToken.None);

        // Keep the same M/M status while changing the staged bytes. The index
        // fingerprint, rather than display status, must invalidate the capture.
        WriteFile(repositoryRoot, "tracked.txt", "restaged\n");
        RunGit(repositoryRoot, "add", "--", "tracked.txt");
        WriteFile(repositoryRoot, "tracked.txt", "restaged plus unstaged\n");
        var restagedStatus = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var restagedChange = Assert.Single(restagedStatus.Changes);
        Assert.Equal("M", restagedChange.IndexStatus);
        Assert.Equal("M", restagedChange.WorkTreeStatus);
        await Assert.ThrowsAsync<CommitMessageSnapshotStaleException>(
            () => service.RevalidateCommitMessageSnapshotAsync(
                repositoryRoot,
                snapshot,
                CancellationToken.None));

        await service.CommitAsync(
            repositoryRoot,
            "second",
            description: null,
            amend: false,
            CancellationToken.None);

        // The index now exactly restores the first commit while the current
        // HEAD has a second commit. An amend patch must therefore be empty.
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        await service.StageFilesAsync(repositoryRoot, ["tracked.txt"], CancellationToken.None);
        var amendSnapshot = await service.CaptureCommitMessageSnapshotAsync(
            repositoryRoot,
            amend: true,
            CancellationToken.None);
        Assert.Equal(initialHead, amendSnapshot.BaselineId);
        Assert.Empty(amendSnapshot.RawPatch.ToArray());

        var unbornRoot = Path.Combine(fixtureRoot, "unborn");
        CreateRepository(unbornRoot);
        WriteFile(unbornRoot, "new.txt", "new staged content\n");
        await service.StageFilesAsync(unbornRoot, ["new.txt"], CancellationToken.None);
        var unbornSnapshot = await service.CaptureCommitMessageSnapshotAsync(
            unbornRoot,
            amend: false,
            CancellationToken.None);
        Assert.True(unbornSnapshot.IsUnborn);
        Assert.Empty(unbornSnapshot.HeadId);
        Assert.Equal(RunGitWithEmptyInput(unbornRoot), unbornSnapshot.BaselineId);
        Assert.Contains("new staged content", Encoding.UTF8.GetString(unbornSnapshot.RawPatch.Span));

        // The empty tree is object-format dependent. Keep this in the same
        // focused root/unborn check so SHA-256 repositories do not regress to
        // the SHA-1 constant used by older Git repositories.
        var sha256Root = Path.Combine(fixtureRoot, "sha256-unborn");
        CreateRepository(sha256Root, sha256: true);
        WriteFile(sha256Root, "new.txt", "sha256 staged content\n");
        await service.StageFilesAsync(sha256Root, ["new.txt"], CancellationToken.None);
        var sha256Snapshot = await service.CaptureCommitMessageSnapshotAsync(
            sha256Root,
            amend: false,
            CancellationToken.None);
        Assert.True(sha256Snapshot.IsUnborn);
        Assert.Equal(RunGitWithEmptyInput(sha256Root), sha256Snapshot.BaselineId);
    }

    public void Dispose()
    {
        DeleteDirectory(fixtureRoot);
    }

    private static void CreateRepository(string path, bool sha256 = false)
    {
        Directory.CreateDirectory(path);
        if (sha256)
        {
            RunGit(path, "init", "--object-format=sha256", "--initial-branch", "main");
        }
        else
        {
            RunGit(path, "init", "--initial-branch", "main");
        }

        RunGit(path, "config", "user.name", "Test User");
        RunGit(path, "config", "user.email", "test-user@example.invalid");
        var hooksPath = Path.Combine(path, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(path, "config", "core.hooksPath", hooksPath);
        RunGit(path, "config", "commit.gpgsign", "false");
    }

    private static void WriteFile(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    private static string RunGitWithEmptyInput(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("hash-object");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("tree");
        startInfo.ArgumentList.Add("--stdin");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Git fixture.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git fixture command failed: {error}");
        }

        return output.Trim();
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
