using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

/// <summary>
/// Task 21: a failing commit hook must surface hook/operation context with the
/// actual post-failure HEAD, leave HEAD unchanged, preserve hook-made worktree
/// changes, and allow a retry with hooks enabled to succeed.
/// </summary>
public sealed class GitRepositoryCommitHookFailureTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string fixtureRoot;

    public GitRepositoryCommitHookFailureTests()
    {
        Directory.CreateDirectory(FixtureParent);
        fixtureRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
    }

    [Fact]
    public async Task FailingPreCommitProducesHookFailureWithoutCreatingCommit()
    {
        var repositoryRoot = Path.Combine(fixtureRoot, "pre-commit-fails");
        CreateRepository(repositoryRoot);
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        RunGit(repositoryRoot, "add", "--all");
        RunGit(repositoryRoot, "commit", "--message", "initial");
        var before = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        WriteFile(repositoryRoot, "tracked.txt", "staged change\n");
        var service = new GitRepositoryService();
        await service.StageFilesAsync(repositoryRoot, ["tracked.txt"], CancellationToken.None);
        // The hook rejects the commit and also touches the worktree; both the
        // staged entry and the hook-made change must survive the failure.
        WriteHook(repositoryRoot, "pre-commit", "echo hook says no >&2\necho hook-note >> tracked.txt\nexit 1\n");

        var failure = await Assert.ThrowsAsync<CommitHookFailureException>(
            () => service.CommitAsync(
                repositoryRoot, "Hook summary", null, amend: false, CancellationToken.None));
        Assert.Equal("commit", failure.Operation);
        Assert.Contains("hook says no", failure.DiagnosticOutput);
        Assert.Contains("pre-commit", failure.InstalledHooks);
        Assert.Equal(before, failure.ActualHeadId);
        Assert.True(failure.HeadUnchanged);

        Assert.Equal(before, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());
        Assert.Contains("tracked.txt", RunGit(repositoryRoot, "diff", "--cached", "--name-only"));
        Assert.Contains("hook-note", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
    }

    [Fact]
    public async Task FixingHookThenRetryingCreatesCommitWithHooksEnabled()
    {
        var repositoryRoot = Path.Combine(fixtureRoot, "hook-retry");
        CreateRepository(repositoryRoot);
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        RunGit(repositoryRoot, "add", "--all");
        RunGit(repositoryRoot, "commit", "--message", "initial");
        var before = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        WriteFile(repositoryRoot, "tracked.txt", "staged change\n");
        var service = new GitRepositoryService();
        await service.StageFilesAsync(repositoryRoot, ["tracked.txt"], CancellationToken.None);
        WriteHook(repositoryRoot, "pre-commit", "echo hook says no >&2\nexit 1\n");

        await Assert.ThrowsAsync<CommitHookFailureException>(
            () => service.CommitAsync(
                repositoryRoot, "Hook summary", null, amend: false, CancellationToken.None));
        Assert.Equal(before, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());

        // Correct the problem (a passing hook) and retry through the same
        // guarded path: hooks run again and the commit is created.
        WriteHook(repositoryRoot, "pre-commit", "exit 0\n");
        var commitId = await service.CommitAsync(
            repositoryRoot, "Hook summary", null, amend: false, CancellationToken.None);

        Assert.Equal(RunGit(repositoryRoot, "rev-parse", "HEAD").Trim(), commitId);
        Assert.NotEqual(before, commitId);
        Assert.Contains("Hook summary", RunGit(repositoryRoot, "log", "-1", "--format=%B"));
        var files = await service.GetCommitFilesAsync(repositoryRoot, commitId, CancellationToken.None);
        Assert.Equal("tracked.txt", Assert.Single(files).Path);
    }

    [Fact]
    public async Task FailingPreCommitDuringAmendReportsAmendWithoutAdvancingHead()
    {
        var repositoryRoot = Path.Combine(fixtureRoot, "amend-hook-fails");
        CreateRepository(repositoryRoot);
        WriteFile(repositoryRoot, "tracked.txt", "base\n");
        RunGit(repositoryRoot, "add", "--all");
        RunGit(repositoryRoot, "commit", "--message", "initial");
        var before = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        WriteHook(repositoryRoot, "pre-commit", "echo amend hook says no >&2\nexit 1\n");

        var service = new GitRepositoryService();
        var failure = await Assert.ThrowsAsync<CommitHookFailureException>(
            () => service.CommitAsync(
                repositoryRoot, "Amended summary", null, amend: true, CancellationToken.None, expectedHeadId: before));
        Assert.Equal("amend", failure.Operation);
        Assert.Contains("amend hook says no", failure.DiagnosticOutput);
        Assert.Equal(before, failure.ActualHeadId);
        Assert.True(failure.HeadUnchanged);
        Assert.Equal(before, RunGit(repositoryRoot, "rev-parse", "HEAD").Trim());
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

    private static void WriteHook(string repositoryRoot, string hookName, string scriptBody)
    {
        // Git for Windows runs extensionless hooks through sh; LF endings only.
        var hooksPath = RunGit(repositoryRoot, "config", "core.hooksPath").Trim();
        var fullPath = Path.Combine(
            Path.IsPathRooted(hooksPath) ? hooksPath : Path.Combine(repositoryRoot, hooksPath),
            hookName);
        File.WriteAllText(fullPath, "#!/bin/sh\n" + scriptBody.Replace("\r\n", "\n"), Utf8NoBom);
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
