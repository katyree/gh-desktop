using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositorySubmodulesTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string fixtureRoot;

    public GitRepositorySubmodulesTests()
    {
        Directory.CreateDirectory(FixtureParent);
        fixtureRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
    }

    [Fact]
    public async Task ReportsDifferentGitlinkUpdatesSafelyAndRejectsStaleOrDirtySnapshots()
    {
        var sourceRoot = Path.Combine(fixtureRoot, "submodule-source");
        var repositoryRoot = Path.Combine(fixtureRoot, "super");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(repositoryRoot);
        ConfigureRepository(sourceRoot);
        ConfigureRepository(repositoryRoot);

        WriteFile(sourceRoot, "tracked.txt", "first\n");
        Commit(sourceRoot, "submodule first");
        var firstCommit = RunGit(sourceRoot, "rev-parse", "HEAD").Trim();
        WriteFile(sourceRoot, "tracked.txt", "second\n");
        Commit(sourceRoot, "submodule second");
        var secondCommit = RunGit(sourceRoot, "rev-parse", "HEAD").Trim();

        const string submodulePath = "modules/submodule[fixture]";
        RunGit(
            repositoryRoot,
            "-c",
            "protocol.file.allow=always",
            "submodule",
            "add",
            sourceRoot,
            submodulePath);
        var checkedOutPath = Path.Combine(
            repositoryRoot,
            submodulePath.Replace('/', Path.DirectorySeparatorChar));
        RunGit(checkedOutPath, "checkout", "--detach", firstCommit);
        RunGit(repositoryRoot, "add", "--", submodulePath);
        Commit(repositoryRoot, "add submodule");

        RunGit(checkedOutPath, "checkout", "--detach", secondCommit);

        var service = new GitRepositoryService();
        var differentGitlink = Assert.Single(
            await service.GetSubmodulesAsync(repositoryRoot, CancellationToken.None));
        Assert.Equal(submodulePath, differentGitlink.Path);
        Assert.Equal(firstCommit, differentGitlink.ExpectedIndexCommitId);
        Assert.Equal(secondCommit, differentGitlink.CheckedOutHeadCommitId);
        Assert.True(differentGitlink.IsInitialized);
        Assert.False(differentGitlink.IsDirty);

        await service.UpdateSubmoduleAsync(
            repositoryRoot,
            differentGitlink,
            CancellationToken.None);
        Assert.Equal(firstCommit, RunGit(checkedOutPath, "rev-parse", "HEAD").Trim());

        var cleanSnapshot = Assert.Single(
            await service.GetSubmodulesAsync(repositoryRoot, CancellationToken.None));
        RunGit(
            repositoryRoot,
            "update-index",
            "--add",
            "--cacheinfo",
            $"160000,{secondCommit},{submodulePath}");
        await Assert.ThrowsAsync<SubmoduleSnapshotStaleException>(
            () => service.UpdateSubmoduleAsync(repositoryRoot, cleanSnapshot, CancellationToken.None));
        Assert.Equal(firstCommit, RunGit(checkedOutPath, "rev-parse", "HEAD").Trim());

        RunGit(
            repositoryRoot,
            "update-index",
            "--add",
            "--cacheinfo",
            $"160000,{firstCommit},{submodulePath}");
        WriteFile(checkedOutPath, "tracked.txt", "local edit\n");
        var dirtySnapshot = Assert.Single(
            await service.GetSubmodulesAsync(repositoryRoot, CancellationToken.None));
        Assert.True(dirtySnapshot.IsDirty);
        await Assert.ThrowsAsync<SubmoduleUpdateBlockedException>(
            () => service.UpdateSubmoduleAsync(repositoryRoot, dirtySnapshot, CancellationToken.None));
        Assert.Equal("local edit\n", File.ReadAllText(Path.Combine(checkedOutPath, "tracked.txt")));

        Directory.Delete(checkedOutPath, recursive: true);
        Directory.CreateDirectory(checkedOutPath);
        WriteFile(checkedOutPath, "keep.txt", "must stay\n");
        var uninitializedSnapshot = Assert.Single(
            await service.GetSubmodulesAsync(repositoryRoot, CancellationToken.None));
        Assert.False(uninitializedSnapshot.IsInitialized);
        Assert.Null(uninitializedSnapshot.CheckedOutHeadCommitId);
        Assert.True(uninitializedSnapshot.IsDirty);
        await Assert.ThrowsAsync<SubmoduleUpdateBlockedException>(
            () => service.UpdateSubmoduleAsync(repositoryRoot, uninitializedSnapshot, CancellationToken.None));
        Assert.Equal("must stay\n", File.ReadAllText(Path.Combine(checkedOutPath, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(checkedOutPath, ".git")));

        var reparseTargetRoot = Path.Combine(fixtureRoot, "reparse-target");
        var reparseTargetPath = Path.Combine(reparseTargetRoot, "submodule[fixture]");
        Directory.CreateDirectory(reparseTargetPath);
        WriteFile(reparseTargetPath, "outside.txt", "must remain\n");
        const string reparseSubmodulePath = "linked-modules/submodule[fixture]";
        RunGit(
            repositoryRoot,
            "update-index",
            "--add",
            "--cacheinfo",
            $"160000,{firstCommit},{reparseSubmodulePath}");
        var reparseParent = Path.Combine(repositoryRoot, "linked-modules");
        CreateJunction(reparseParent, reparseTargetRoot);
        try
        {
            var reparseException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.GetSubmodulesAsync(repositoryRoot, CancellationToken.None));
            Assert.Contains("reparse point", reparseException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                "must remain\n",
                File.ReadAllText(Path.Combine(reparseTargetPath, "outside.txt")));
        }
        finally
        {
            Directory.Delete(reparseParent, recursive: false);
        }
    }

    [Fact]
    public async Task TypedSubmoduleDiffsDistinguishGitlinksFromTextAndBlockPartialStaging()
    {
        var sourceRoot = Path.Combine(fixtureRoot, "diff-submodule-source");
        var repositoryRoot = Path.Combine(fixtureRoot, "diff-super");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(repositoryRoot);
        ConfigureRepository(sourceRoot);
        ConfigureRepository(repositoryRoot);

        WriteFile(sourceRoot, "tracked.txt", "first\n");
        Commit(sourceRoot, "submodule first");
        var firstCommit = RunGit(sourceRoot, "rev-parse", "HEAD").Trim();
        WriteFile(sourceRoot, "tracked.txt", "second\n");
        Commit(sourceRoot, "submodule second");
        var secondCommit = RunGit(sourceRoot, "rev-parse", "HEAD").Trim();

        const string submodulePath = "modules/submodule[diff]";
        RunGit(
            repositoryRoot,
            "-c",
            "protocol.file.allow=always",
            "submodule",
            "add",
            sourceRoot,
            submodulePath);
        var checkedOutPath = Path.Combine(
            repositoryRoot,
            submodulePath.Replace('/', Path.DirectorySeparatorChar));
        RunGit(checkedOutPath, "checkout", "--detach", firstCommit);
        WriteFile(repositoryRoot, "mimic.txt", $"Subproject commit {firstCommit}\n");
        Commit(repositoryRoot, "add submodule and text mimic");
        var addCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        var service = new GitRepositoryService();
        var addedChange = Assert.Single(
            await service.GetCommitFilesAsync(repositoryRoot, addCommit, CancellationToken.None),
            change => change.Path == submodulePath);
        var addedDiff = await service.GetCommitDiffAsync(
            repositoryRoot,
            addCommit,
            addedChange,
            CancellationToken.None);
        Assert.False(addedDiff.IsBinary);
        Assert.NotNull(addedDiff.SubmoduleComparison);
        Assert.Null(addedDiff.SubmoduleComparison!.OldCommitId);
        Assert.Equal(firstCommit, addedDiff.SubmoduleComparison.NewCommitId);
        Assert.True(addedDiff.SubmoduleComparison.IsAdded);
        Assert.False(addedDiff.SubmoduleComparison.IsRemoved);

        WriteFile(checkedOutPath, "tracked.txt", "local edit\n");
        var dirtyChange = Assert.Single(
            (await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes,
            change => change.Path == submodulePath);
        var dirtyDiff = await service.GetWorkingDiffAsync(
            repositoryRoot,
            dirtyChange,
            CancellationToken.None);
        Assert.False(dirtyDiff.IsBinary);
        Assert.NotNull(dirtyDiff.SubmoduleComparison);
        Assert.Equal(firstCommit, dirtyDiff.SubmoduleComparison!.OldCommitId);
        Assert.Equal(firstCommit, dirtyDiff.SubmoduleComparison.NewCommitId);
        Assert.False(dirtyDiff.SubmoduleComparison.OldIsDirty);
        Assert.True(dirtyDiff.SubmoduleComparison.NewIsDirty);
        Assert.True(dirtyDiff.SubmoduleComparison.IsDirtyOnly);

        var submodulePartial = await service.GetPartialDiffAsync(
            repositoryRoot,
            dirtyChange,
            staged: false,
            CancellationToken.None);
        Assert.False(submodulePartial.IsSupported);
        Assert.Equal("Submodule changes cannot be partially staged.", submodulePartial.Message);

        RunGit(checkedOutPath, "checkout", "--", "tracked.txt");
        RunGit(checkedOutPath, "checkout", "--detach", secondCommit);
        var changedGitlink = Assert.Single(
            (await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes,
            change => change.Path == submodulePath);
        var changedDiff = await service.GetWorkingDiffAsync(
            repositoryRoot,
            changedGitlink,
            CancellationToken.None);
        Assert.NotNull(changedDiff.SubmoduleComparison);
        Assert.Equal(firstCommit, changedDiff.SubmoduleComparison!.OldCommitId);
        Assert.Equal(secondCommit, changedDiff.SubmoduleComparison.NewCommitId);
        Assert.True(changedDiff.SubmoduleComparison.HasRevisionChange);
        Assert.False(changedDiff.SubmoduleComparison.IsDirtyOnly);

        WriteFile(repositoryRoot, "mimic.txt", $"Subproject commit {secondCommit}\n");
        var mimicChange = Assert.Single(
            (await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes,
            change => change.Path == "mimic.txt");
        var mimicDiff = await service.GetWorkingDiffAsync(
            repositoryRoot,
            mimicChange,
            CancellationToken.None);
        Assert.False(mimicDiff.IsBinary);
        Assert.Null(mimicDiff.SubmoduleComparison);
        var mimicPartial = await service.GetPartialDiffAsync(
            repositoryRoot,
            mimicChange,
            staged: false,
            CancellationToken.None);
        Assert.True(mimicPartial.IsSupported, mimicPartial.Message);

        RunGit(repositoryRoot, "rm", "-f", "--", submodulePath);
        Commit(repositoryRoot, "remove submodule");
        var removeCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        var removedChange = Assert.Single(
            await service.GetCommitFilesAsync(repositoryRoot, removeCommit, CancellationToken.None),
            change => change.Path == submodulePath);
        var removedDiff = await service.GetCommitDiffAsync(
            repositoryRoot,
            removeCommit,
            removedChange,
            CancellationToken.None);
        Assert.NotNull(removedDiff.SubmoduleComparison);
        Assert.Equal(firstCommit, removedDiff.SubmoduleComparison!.OldCommitId);
        Assert.Null(removedDiff.SubmoduleComparison.NewCommitId);
        Assert.True(removedDiff.SubmoduleComparison.IsRemoved);
        Assert.False(removedDiff.SubmoduleComparison.IsAdded);
    }

    public void Dispose() => DeleteDirectory(fixtureRoot);

    private static void ConfigureRepository(string path)
    {
        RunGit(path, "init", "-b", "main");
        RunGit(path, "config", "user.name", "Test User");
        RunGit(path, "config", "user.email", "test-user@example.invalid");
        var hooksPath = Path.Combine(path, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(path, "config", "core.hooksPath", hooksPath);
        RunGit(path, "config", "commit.gpgsign", "false");
    }

    private static void WriteFile(string root, string relativePath, string contents)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
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

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to create junction fixture.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Junction fixture command failed: {output}{error}");
        }
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
