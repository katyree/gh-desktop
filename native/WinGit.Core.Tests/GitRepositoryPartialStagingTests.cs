using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryPartialStagingTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryPartialStagingTests()
    {
        Directory.CreateDirectory(FixtureParent);
        repositoryRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        RunGit(repositoryRoot, "init");
        RunGit(repositoryRoot, "config", "user.name", "Test User");
        RunGit(repositoryRoot, "config", "user.email", "test-user@example.invalid");
        ConfigureLocalCommitSafety(repositoryRoot);
    }

    [Fact]
    public async Task SelectedLineStagesOnlyItsHunkUnstageRestoresItAndStaleSelectionFails()
    {
        var path = Path.Combine(repositoryRoot, "nested", "partial file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var baseline = string.Join(
            "\n",
            Enumerable.Range(1, 12).Select(index => $"line {index}")) + "\n";
        WriteFile(path, baseline);
        Commit("initial");

        var changed = baseline
            .Replace("line 2", "line 2 changed\nline 2 extra", StringComparison.Ordinal)
            .Replace("line 10", "line 10 changed", StringComparison.Ordinal);
        WriteFile(path, changed);

        var service = new GitRepositoryService();
        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var file = Assert.Single(status.Changes);
        var unstaged = await service.GetPartialDiffAsync(
            repositoryRoot,
            file,
            staged: false,
            CancellationToken.None);

        Assert.True(unstaged.IsSupported);
        Assert.Equal(2, unstaged.Hunks.Count);
        var selectedHunk = unstaged.Hunks[1];
        var firstAddedLine = Assert.Single(
            selectedHunk.Lines,
            line => line.Kind == DiffLineKind.Added);
        Assert.Equal(selectedHunk.Id, firstAddedLine.HunkId);
        await service.StageSelectedChangesAsync(
            repositoryRoot,
            unstaged,
            [PartialDiffSelection.ForLine(selectedHunk.Id, firstAddedLine.LineIndex)],
            CancellationToken.None);

        Assert.Equal(changed, File.ReadAllText(path));
        var partiallyStagedStatus = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var partiallyStaged = Assert.Single(partiallyStagedStatus.Changes);
        Assert.Equal("M", partiallyStaged.IndexStatus);
        Assert.Equal("M", partiallyStaged.WorkTreeStatus);
        var stagedDiff = await service.GetIndexDiffAsync(repositoryRoot, partiallyStaged, CancellationToken.None);
        Assert.Contains(stagedDiff.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "line 10 changed");
        Assert.DoesNotContain(stagedDiff.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "line 2 extra");
        var remainingDiff = await service.GetUnstagedDiffAsync(repositoryRoot, partiallyStaged, CancellationToken.None);
        Assert.Contains(remainingDiff.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "line 2 extra");

        var stagedPartial = await service.GetPartialDiffAsync(
            repositoryRoot,
            partiallyStaged,
            staged: true,
            CancellationToken.None);
        Assert.Single(stagedPartial.Hunks);
        await service.UnstageSelectedChangesAsync(
            repositoryRoot,
            stagedPartial,
            [PartialDiffSelection.ForHunk(stagedPartial.Hunks[0].Id)],
            CancellationToken.None);

        var unstageStatus = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var fullyUnstaged = Assert.Single(unstageStatus.Changes);
        Assert.Equal(string.Empty, fullyUnstaged.IndexStatus);
        Assert.Equal("M", fullyUnstaged.WorkTreeStatus);
        Assert.Equal(changed, File.ReadAllText(path));

        var stalePartial = await service.GetPartialDiffAsync(
            repositoryRoot,
            fullyUnstaged,
            staged: false,
            CancellationToken.None);
        WriteFile(
            path,
            changed.Replace("line 2 changed", "line 2 changed again", StringComparison.Ordinal));

        await Assert.ThrowsAsync<StaleDiffSnapshotException>(
            () => service.StageSelectedChangesAsync(
                repositoryRoot,
                stalePartial,
                [PartialDiffSelection.ForHunk(stalePartial.Hunks[0].Id)],
                CancellationToken.None));

        var staleStatus = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var staleFile = Assert.Single(staleStatus.Changes);
        Assert.Equal(string.Empty, staleFile.IndexStatus);
        Assert.Equal("M", staleFile.WorkTreeStatus);
        Assert.Equal(
            "line 2 changed again",
            File.ReadAllLines(path)[1]);
    }

    [Fact]
    public async Task TextAddDeleteNewFileAndNoNewlineSelectionsPreserveIndexAndWorkTree()
    {
        WriteFile(
            Path.Combine(repositoryRoot, "base.txt"),
            "base one\nbase two\nbase three\n");
        WriteFile(
            Path.Combine(repositoryRoot, "delete-me.txt"),
            "delete one\ndelete two\n");
        WriteFile(Path.Combine(repositoryRoot, "no newline.txt"), "old");
        Commit("initial text cases");

        var service = new GitRepositoryService();

        var untrackedPath = Path.Combine(repositoryRoot, "new file.txt");
        WriteFile(untrackedPath, "new one\nnew two\n");
        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var untracked = Assert.Single(status.Changes, change => change.Path == "new file.txt");
        var untrackedDiff = await service.GetPartialDiffAsync(
            repositoryRoot,
            untracked,
            staged: false,
            CancellationToken.None);
        Assert.True(untrackedDiff.IsSupported, untrackedDiff.Message);
        var untrackedLine = Assert.Single(
            untrackedDiff.Hunks[0].Lines,
            line => line.Kind == DiffLineKind.Added && line.Text == "new one");
        await service.StageSelectedChangesAsync(
            repositoryRoot,
            untrackedDiff,
            [PartialDiffSelection.ForLine(untrackedLine.HunkId, untrackedLine.LineIndex)],
            CancellationToken.None);
        Assert.Equal("new one\nnew two\n", File.ReadAllText(untrackedPath));
        status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var stagedUntracked = Assert.Single(status.Changes, change => change.Path == "new file.txt");
        Assert.Equal("A", stagedUntracked.IndexStatus);
        Assert.Equal("M", stagedUntracked.WorkTreeStatus);

        var stagedUntrackedDiff = await service.GetPartialDiffAsync(
            repositoryRoot,
            stagedUntracked,
            staged: true,
            CancellationToken.None);
        await service.UnstageSelectedChangesAsync(
            repositoryRoot,
            stagedUntrackedDiff,
            [PartialDiffSelection.ForHunk(stagedUntrackedDiff.Hunks[0].Id)],
            CancellationToken.None);
        status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var restoredUntracked = Assert.Single(status.Changes, change => change.Path == "new file.txt");
        Assert.Equal("?", restoredUntracked.WorkTreeStatus);
        Assert.Equal("new one\nnew two\n", File.ReadAllText(untrackedPath));
        File.Delete(untrackedPath);

        var stagedAdditionPath = Path.Combine(repositoryRoot, "staged addition.txt");
        WriteFile(stagedAdditionPath, "added one\nadded two\n");
        await service.StageFilesAsync(
            repositoryRoot,
            ["staged addition.txt"],
            CancellationToken.None);
        status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var stagedAddition = Assert.Single(status.Changes, change => change.Path == "staged addition.txt");
        var stagedAdditionDiff = await service.GetPartialDiffAsync(
            repositoryRoot,
            stagedAddition,
            staged: true,
            CancellationToken.None);
        Assert.True(stagedAdditionDiff.IsSupported, stagedAdditionDiff.Message);
        await service.UnstageSelectedChangesAsync(
            repositoryRoot,
            stagedAdditionDiff,
            [PartialDiffSelection.ForLine(0, 0)],
            CancellationToken.None);
        status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var unstageAddition = Assert.Single(status.Changes, change => change.Path == "staged addition.txt");
        Assert.Equal("A", unstageAddition.IndexStatus);
        Assert.Equal("M", unstageAddition.WorkTreeStatus);
        Assert.Equal(
            "added two\n",
            RunGit(repositoryRoot, "show", ":staged addition.txt"));
        Assert.Equal("added one\nadded two\n", File.ReadAllText(stagedAdditionPath));
        File.Delete(stagedAdditionPath);

        File.Delete(Path.Combine(repositoryRoot, "delete-me.txt"));
        status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var deleted = Assert.Single(status.Changes, change => change.Path == "delete-me.txt");
        var deletedDiff = await service.GetPartialDiffAsync(
            repositoryRoot,
            deleted,
            staged: false,
            CancellationToken.None);
        Assert.True(deletedDiff.IsSupported, deletedDiff.Message);
        var deletedLine = Assert.Single(
            deletedDiff.Hunks[0].Lines,
            line => line.Kind == DiffLineKind.Removed && line.Text == "delete one");
        await service.StageSelectedChangesAsync(
            repositoryRoot,
            deletedDiff,
            [PartialDiffSelection.ForLine(deletedLine.HunkId, deletedLine.LineIndex)],
            CancellationToken.None);
        status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var stagedDeletion = Assert.Single(status.Changes, change => change.Path == "delete-me.txt");
        Assert.Equal("M", stagedDeletion.IndexStatus);
        Assert.Equal("D", stagedDeletion.WorkTreeStatus);
        Assert.Equal(
            "delete two\n",
            RunGit(repositoryRoot, "show", ":delete-me.txt"));
        var stagedDeletionDiff = await service.GetPartialDiffAsync(
            repositoryRoot,
            stagedDeletion,
            staged: true,
            CancellationToken.None);
        await service.UnstageSelectedChangesAsync(
            repositoryRoot,
            stagedDeletionDiff,
            [PartialDiffSelection.ForHunk(stagedDeletionDiff.Hunks[0].Id)],
            CancellationToken.None);
        status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var restoredDeletion = Assert.Single(status.Changes, change => change.Path == "delete-me.txt");
        Assert.Equal(string.Empty, restoredDeletion.IndexStatus);
        Assert.Equal("D", restoredDeletion.WorkTreeStatus);

        var noNewlinePath = Path.Combine(repositoryRoot, "no newline.txt");
        WriteFile(noNewlinePath, "new");
        status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var noNewline = Assert.Single(status.Changes, change => change.Path == "no newline.txt");
        var noNewlineDiff = await service.GetPartialDiffAsync(
            repositoryRoot,
            noNewline,
            staged: false,
            CancellationToken.None);
        Assert.True(noNewlineDiff.IsSupported, noNewlineDiff.Message);
        Assert.Contains(
            noNewlineDiff.Hunks[0].Lines,
            line => line.Kind == DiffLineKind.NoNewline);
        var noNewlineLine = Assert.Single(
            noNewlineDiff.Hunks[0].Lines,
            line => line.Kind == DiffLineKind.Added && line.Text == "new");
        await service.StageSelectedChangesAsync(
            repositoryRoot,
            noNewlineDiff,
            [PartialDiffSelection.ForHunk(noNewlineLine.HunkId)],
            CancellationToken.None);
        Assert.Equal("new", File.ReadAllText(noNewlinePath));
        status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var stagedNoNewline = Assert.Single(status.Changes, change => change.Path == "no newline.txt");
        Assert.Equal("M", stagedNoNewline.IndexStatus);
        Assert.Equal(string.Empty, stagedNoNewline.WorkTreeStatus);
    }

    public void Dispose()
    {
        DeleteDirectory(repositoryRoot);
    }

    private static void ConfigureLocalCommitSafety(string path)
    {
        var hooksPath = Path.Combine(path, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(path, "config", "core.hooksPath", hooksPath);
        RunGit(path, "config", "commit.gpgsign", "false");
    }

    private static void WriteFile(string path, string contents)
    {
        File.WriteAllText(path, contents, Utf8NoBom);
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
