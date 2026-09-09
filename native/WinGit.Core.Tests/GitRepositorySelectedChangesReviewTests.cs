using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositorySelectedChangesReviewTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositorySelectedChangesReviewTests()
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
    public async Task CapturesWholeAndPartialChangesWithoutTouchingRealIndexOrWorkTree()
    {
        var baseline = string.Join(
            "\n",
            Enumerable.Range(1, 12).Select(index => $"line {index}")) + "\n";
        var trackedPath = Path.Combine(repositoryRoot, "tracked.txt");
        WriteFile(trackedPath, baseline);
        WriteFile(
            Path.Combine(repositoryRoot, "renamed from.txt"),
            "rename one\nrename two\nrename three\nrename four\n");
        Commit("initial");

        var changed = baseline
            .Replace("line 2", "line 2 changed\nline 2 extra", StringComparison.Ordinal)
            .Replace("line 10", "line 10 changed", StringComparison.Ordinal);
        WriteFile(trackedPath, changed);
        RunGit(repositoryRoot, "mv", "renamed from.txt", "renamed to.txt");
        WriteFile(
            Path.Combine(repositoryRoot, "renamed to.txt"),
            "rename one changed\nrename two\nrename three\nrename four\n");
        var untrackedPath = Path.Combine(repositoryRoot, "new file.txt");
        WriteFile(untrackedPath, "new one\nnew two\n");

        var service = new GitRepositoryService();
        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var tracked = Assert.Single(status.Changes, change => change.Path == "tracked.txt");
        var renamed = Assert.Single(status.Changes, change => change.Path == "renamed to.txt");
        Assert.Equal("renamed from.txt", renamed.OldPath);
        var untracked = Assert.Single(status.Changes, change => change.Path == "new file.txt");
        var partialSnapshot = await service.GetSelectedChangesReviewDiffAsync(
            repositoryRoot,
            tracked,
            CancellationToken.None);
        var partial = partialSnapshot.Diff;
        Assert.True(partial.IsSupported, partial.Message);
        var firstHunk = partial.Hunks[0];
        var selectedAddedLine = Assert.Single(
            firstHunk.Lines,
            line => line.Kind == DiffLineKind.Added && line.Text == "line 2 changed");

        var indexBefore = RunGit(repositoryRoot, "ls-files", "--stage", "--full-name", "-z");
        var statusBefore = RunGit(
            repositoryRoot,
            "status",
            "--porcelain=v2",
            "--branch",
            "--untracked-files=all",
            "-z");
        var snapshot = await service.GetSelectedChangesReviewSnapshotAsync(
            repositoryRoot,
            [
                new SelectedChangesReviewFileSelection(
                    partialSnapshot,
                    [PartialDiffSelection.ForLine(firstHunk.Id, selectedAddedLine.LineIndex)]),
                new SelectedChangesReviewFileSelection(renamed, includeWholeFile: true),
                new SelectedChangesReviewFileSelection(untracked, includeWholeFile: true),
            ],
            CancellationToken.None);

        var trackedReview = Assert.Single(snapshot.Files, file => file.Path == "tracked.txt");
        var renamedReview = Assert.Single(snapshot.Files, file => file.Path == "renamed to.txt");
        var untrackedReview = Assert.Single(snapshot.Files, file => file.Path == "new file.txt");
        Assert.StartsWith("diff --git ", trackedReview.Diff, StringComparison.Ordinal);
        Assert.Contains("line 2 changed", trackedReview.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("line 2 extra", trackedReview.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("line 10 changed", trackedReview.Diff, StringComparison.Ordinal);
        Assert.Contains("rename from renamed from.txt", renamedReview.Diff, StringComparison.Ordinal);
        Assert.Contains("rename to renamed to.txt", renamedReview.Diff, StringComparison.Ordinal);
        Assert.Contains("rename one changed", renamedReview.Diff, StringComparison.Ordinal);
        Assert.Contains("new file mode", untrackedReview.Diff, StringComparison.Ordinal);
        Assert.Contains("new one", snapshot.Diff, StringComparison.Ordinal);
        Assert.True(await service.RevalidateSelectedChangesReviewSnapshotAsync(
            repositoryRoot,
            snapshot,
            CancellationToken.None));

        Assert.Equal(indexBefore, RunGit(repositoryRoot, "ls-files", "--stage", "--full-name", "-z"));
        Assert.Equal(
            statusBefore,
            RunGit(
                repositoryRoot,
                "status",
                "--porcelain=v2",
                "--branch",
                "--untracked-files=all",
                "-z"));
        Assert.Equal(changed, File.ReadAllText(trackedPath));
        Assert.Equal(
            "rename one changed\nrename two\nrename three\nrename four\n",
            File.ReadAllText(Path.Combine(repositoryRoot, "renamed to.txt")));
        Assert.Equal("new one\nnew two\n", File.ReadAllText(untrackedPath));
    }

    [Fact]
    public async Task RevalidationRejectsWorkingAndIndependentIndexChanges()
    {
        var trackedPath = Path.Combine(repositoryRoot, "tracked.txt");
        var otherPath = Path.Combine(repositoryRoot, "other.txt");
        WriteFile(trackedPath, "before\n");
        WriteFile(otherPath, "other before\n");
        Commit("initial");
        WriteFile(trackedPath, "after\n");

        var service = new GitRepositoryService();
        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var tracked = Assert.Single(status.Changes, change => change.Path == "tracked.txt");
        var reviewDiff = await service.GetSelectedChangesReviewDiffAsync(
            repositoryRoot,
            tracked,
            CancellationToken.None);
        var selectedLine = Assert.Single(
            reviewDiff.Diff.Hunks[0].Lines,
            line => line.Kind == DiffLineKind.Added && line.Text == "after");
        var selection = new SelectedChangesReviewFileSelection(
            reviewDiff,
            [PartialDiffSelection.ForLine(selectedLine.HunkId, selectedLine.LineIndex)]);
        var snapshot = await service.GetSelectedChangesReviewSnapshotAsync(
            repositoryRoot,
            [selection],
            CancellationToken.None);
        Assert.True(await service.RevalidateSelectedChangesReviewSnapshotAsync(
            repositoryRoot,
            snapshot,
            CancellationToken.None));

        WriteFile(trackedPath, "after changed\n");
        await Assert.ThrowsAsync<SelectedChangesReviewSnapshotStaleException>(
            () => service.GetSelectedChangesReviewSnapshotAsync(
                repositoryRoot,
                [selection],
                CancellationToken.None));
        Assert.False(await service.RevalidateSelectedChangesReviewSnapshotAsync(
            repositoryRoot,
            snapshot,
            CancellationToken.None));

        WriteFile(trackedPath, "after\n");
        WriteFile(otherPath, "other staged\n");
        await service.StageFilesAsync(repositoryRoot, ["other.txt"], CancellationToken.None);
        Assert.False(await service.RevalidateSelectedChangesReviewSnapshotAsync(
            repositoryRoot,
            snapshot,
            CancellationToken.None));
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
