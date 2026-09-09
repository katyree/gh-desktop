using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryPartialDiscardTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryPartialDiscardTests()
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
    public async Task DiscardSelectedLinePreservesUnselectedWorkTreeAndStagedContent()
    {
        var path = Path.Combine(repositoryRoot, "partial-discard.txt");
        var baseline = string.Join(
            "\n",
            Enumerable.Range(1, 20).Select(index => $"line {index}")) + "\n";
        WriteFile(path, baseline);
        Commit("initial");

        var stagedContent = baseline.Replace(
            "line 2",
            "line 2 staged",
            StringComparison.Ordinal);
        WriteFile(path, stagedContent);
        var service = new GitRepositoryService();
        await service.StageFilesAsync(
            repositoryRoot,
            ["partial-discard.txt"],
            CancellationToken.None);

        var workTreeContent = stagedContent
            .Replace("line 5\n", "line 5\nline 5 selected\n", StringComparison.Ordinal)
            .Replace("line 15", "line 15 keep", StringComparison.Ordinal);
        WriteFile(path, workTreeContent);

        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var file = Assert.Single(status.Changes);
        var unstaged = await service.GetPartialDiffAsync(
            repositoryRoot,
            file,
            staged: false,
            CancellationToken.None);
        Assert.True(unstaged.IsSupported, unstaged.Message);
        var selectedLine = Assert.Single(
            unstaged.Hunks.SelectMany(hunk => hunk.Lines),
            line => line.Kind == DiffLineKind.Added && line.Text == "line 5 selected");

        await service.DiscardSelectedChangesAsync(
            repositoryRoot,
            unstaged,
            [PartialDiffSelection.ForLine(selectedLine.HunkId, selectedLine.LineIndex)],
            CancellationToken.None);

        var expected = stagedContent.Replace(
            "line 15",
            "line 15 keep",
            StringComparison.Ordinal);
        Assert.Equal(expected, File.ReadAllText(path));

        var after = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var changed = Assert.Single(after.Changes);
        Assert.Equal("M", changed.IndexStatus);
        Assert.Equal("M", changed.WorkTreeStatus);
        Assert.Equal(stagedContent, RunGit(repositoryRoot, "show", ":partial-discard.txt"));

        var stagedDiff = await service.GetPartialDiffAsync(
            repositoryRoot,
            changed,
            staged: true,
            CancellationToken.None);
        Assert.Contains(
            stagedDiff.Hunks.SelectMany(hunk => hunk.Lines),
            line => line.Kind == DiffLineKind.Added && line.Text == "line 2 staged");
        Assert.DoesNotContain(
            stagedDiff.Hunks.SelectMany(hunk => hunk.Lines),
            line => line.Text is "line 5 selected" or "line 15 keep");
    }

    [Fact]
    public async Task DiscardSelectedChangesRejectsStaleSnapshotBeforeWrite()
    {
        var path = Path.Combine(repositoryRoot, "stale-discard.txt");
        var baseline = string.Join(
            "\n",
            Enumerable.Range(1, 12).Select(index => $"line {index}")) + "\n";
        WriteFile(path, baseline);
        Commit("initial");

        WriteFile(
            path,
            baseline
                .Replace("line 3", "line 3 selected", StringComparison.Ordinal)
                .Replace("line 10", "line 10 keep", StringComparison.Ordinal));
        var service = new GitRepositoryService();
        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var file = Assert.Single(status.Changes);
        var snapshot = await service.GetPartialDiffAsync(
            repositoryRoot,
            file,
            staged: false,
            CancellationToken.None);
        var selectedLine = Assert.Single(
            snapshot.Hunks.SelectMany(hunk => hunk.Lines),
            line => line.Kind == DiffLineKind.Added && line.Text == "line 3 selected");

        var changedAfterSnapshot = baseline
            .Replace("line 3", "line 3 changed after snapshot", StringComparison.Ordinal)
            .Replace("line 10", "line 10 keep", StringComparison.Ordinal);
        WriteFile(path, changedAfterSnapshot);

        await Assert.ThrowsAsync<StaleDiffSnapshotException>(
            () => service.DiscardSelectedChangesAsync(
                repositoryRoot,
                snapshot,
                [PartialDiffSelection.ForLine(selectedLine.HunkId, selectedLine.LineIndex)],
                CancellationToken.None));

        Assert.Equal(changedAfterSnapshot, File.ReadAllText(path));
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
