using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryCommitSelectionTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string fixtureRoot;

    public GitRepositoryCommitSelectionTests()
    {
        Directory.CreateDirectory(FixtureParent);
        fixtureRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
    }

    [Fact]
    public async Task LinearSelectionUsesRootBaselineAndBoundedPerFileDiff()
    {
        var repositoryRoot = Path.Combine(fixtureRoot, "linear");
        CreateRepository(repositoryRoot);

        WriteFile(repositoryRoot, "root.txt", "root\n");
        Commit(repositoryRoot, "root");
        var rootCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        WriteFile(repositoryRoot, "root.txt", "root\nsecond\n");
        WriteFile(repositoryRoot, Path.Combine("nested", "second.txt"), "second file\n");
        Commit(repositoryRoot, "second");
        var secondCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        WriteFile(repositoryRoot, "root.txt", "root\nsecond\nthird\n");
        Commit(repositoryRoot, "third");
        var thirdCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        var service = new GitRepositoryService();
        var snapshot = await service.CaptureCommitSelectionAsync(
            repositoryRoot,
            [rootCommit, secondCommit, thirdCommit],
            isContiguous: true,
            maxFiles: 100,
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(repositoryRoot), snapshot.RootPath);
        Assert.Equal([rootCommit, secondCommit, thirdCommit], snapshot.SelectedCommitIds);
        Assert.Equal([rootCommit, secondCommit, thirdCommit], snapshot.ReachableSelectedCommitIds);
        Assert.Empty(snapshot.OmittedSelectedCommitIds);
        Assert.Equal(rootCommit, snapshot.FirstSelectedCommitId);
        Assert.Equal(thirdCommit, snapshot.LastSelectedCommitId);
        Assert.Equal(RunGitWithEmptyInput(repositoryRoot), snapshot.FirstParentBaselineId);
        Assert.False(snapshot.ChangedFilesTruncated);
        Assert.Contains(snapshot.ChangedFiles, file => file.Path == "root.txt");
        Assert.Contains(snapshot.ChangedFiles, file => file.Path == "nested/second.txt");

        var rootFile = Assert.Single(snapshot.ChangedFiles, file => file.Path == "root.txt");
        var diff = await service.GetCommitSelectionFileDiffAsync(
            repositoryRoot,
            snapshot,
            rootFile,
            CancellationToken.None);
        Assert.Contains(diff.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "third");
        Assert.DoesNotContain(diff.Lines, line => line.Text.Contains('\u001b'));

        await service.RevalidateCommitSelectionAsync(
            repositoryRoot,
            snapshot,
            CancellationToken.None);

        var tail = await service.CaptureCommitSelectionAsync(
            repositoryRoot,
            [secondCommit, thirdCommit],
            isContiguous: true,
            cancellationToken: CancellationToken.None);
        Assert.Equal(rootCommit, tail.FirstParentBaselineId);
    }

    [Fact]
    public async Task MergeSideCommitsStayOmittedUntilMergeTipIsSelected()
    {
        var repositoryRoot = Path.Combine(fixtureRoot, "merge");
        CreateRepository(repositoryRoot);

        WriteFile(repositoryRoot, "base.txt", "base\n");
        Commit(repositoryRoot, "root");
        WriteFile(repositoryRoot, "main-before.txt", "main before\n");
        Commit(repositoryRoot, "main before");
        RunGit(repositoryRoot, "branch", "feature");

        RunGit(repositoryRoot, "checkout", "feature");
        WriteFile(repositoryRoot, "feature-one.txt", "feature one\n");
        Commit(repositoryRoot, "feature one");
        var featureOneCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        WriteFile(repositoryRoot, "feature-two.txt", "feature two\n");
        Commit(repositoryRoot, "feature two");
        var featureTwoCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        RunGit(repositoryRoot, "checkout", "main");
        WriteFile(repositoryRoot, "main-after.txt", "main after\n");
        Commit(repositoryRoot, "main after");
        var mainCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        RunGit(repositoryRoot, "merge", "--no-ff", "feature", "--message", "merge feature");
        var mergeCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        var service = new GitRepositoryService();
        var selectedBeforeMerge = await service.CaptureCommitSelectionAsync(
            repositoryRoot,
            [featureOneCommit, featureTwoCommit, mainCommit],
            isContiguous: true,
            cancellationToken: CancellationToken.None);

        Assert.Equal([mainCommit], selectedBeforeMerge.ReachableSelectedCommitIds);
        Assert.Equal([featureOneCommit, featureTwoCommit], selectedBeforeMerge.OmittedSelectedCommitIds);
        Assert.Contains(selectedBeforeMerge.ChangedFiles, file => file.Path == "main-after.txt");
        Assert.DoesNotContain(selectedBeforeMerge.ChangedFiles, file => file.Path == "feature-one.txt");
        Assert.DoesNotContain(selectedBeforeMerge.ChangedFiles, file => file.Path == "feature-two.txt");

        var selectedThroughMerge = await service.CaptureCommitSelectionAsync(
            repositoryRoot,
            [featureOneCommit, featureTwoCommit, mainCommit, mergeCommit],
            isContiguous: true,
            cancellationToken: CancellationToken.None);

        Assert.Equal(
            [featureOneCommit, featureTwoCommit, mainCommit, mergeCommit],
            selectedThroughMerge.ReachableSelectedCommitIds);
        Assert.Empty(selectedThroughMerge.OmittedSelectedCommitIds);
        var featureFile = Assert.Single(
            selectedThroughMerge.ChangedFiles,
            file => file.Path == "feature-one.txt");
        var featureDiff = await service.GetCommitSelectionFileDiffAsync(
            repositoryRoot,
            selectedThroughMerge,
            featureFile,
            CancellationToken.None);
        Assert.Contains(featureDiff.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "feature one");

        // Electron does not load a combined file list or diff for a
        // non-contiguous multi-selection; it keeps the selected rows
        // informational instead.
        var nonContiguous = await service.CaptureCommitSelectionAsync(
            repositoryRoot,
            [featureOneCommit, mainCommit],
            isContiguous: false,
            cancellationToken: CancellationToken.None);
        Assert.False(nonContiguous.HasCombinedDiff);
        Assert.Equal([featureOneCommit, mainCommit], nonContiguous.ReachableSelectedCommitIds);
        Assert.Empty(nonContiguous.OmittedSelectedCommitIds);
        Assert.Empty(nonContiguous.ChangedFiles);
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

    private static void Commit(string repositoryRoot, string message)
    {
        RunGit(repositoryRoot, "add", "--all");
        RunGit(repositoryRoot, "commit", "--message", message);
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
