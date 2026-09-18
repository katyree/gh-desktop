using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryDiscardTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;
    private readonly string trashRoot;

    public GitRepositoryDiscardTests()
    {
        Directory.CreateDirectory(FixtureParent);
        repositoryRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        trashRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
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
    public async Task DiscardSelectedFilesRestoresSelectedPathsAndPreservesUnrelatedChanges()
    {
        WriteFile("tracked.txt", "tracked base\n");
        WriteFile("other.txt", "other base\n");
        WriteFile("rename-old.txt", "rename base\n");
        WriteFile("deleted.txt", "deleted base\n");
        Commit("initial");

        WriteFile("tracked.txt", "tracked staged\n");
        RunGit(repositoryRoot, "add", "--", "tracked.txt");
        WriteFile("tracked.txt", "tracked working\n");

        WriteFile("other.txt", "other staged\n");
        RunGit(repositoryRoot, "add", "--", "other.txt");
        WriteFile("other.txt", "other working\n");

        RunGit(repositoryRoot, "mv", "--", "rename-old.txt", "rename-new.txt");
        WriteFile("new.txt", "new file\n");
        File.Delete(Path.Combine(repositoryRoot, "deleted.txt"));

        var service = new GitRepositoryService();
        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var selected = status.Changes
            .Where(change => change.Path is "tracked.txt" or "rename-new.txt" or "new.txt" or "deleted.txt")
            .ToArray();
        Assert.Equal(4, selected.Length);
        Assert.Contains(selected, change => change.Kind == ChangeKind.Renamed
            && change.Path == "rename-new.txt"
            && change.OldPath == "rename-old.txt");

        var snapshot = await service.CaptureDiscardSnapshotAsync(
            repositoryRoot,
            selected,
            CancellationToken.None);
        var recycler = new FixtureRecycler(trashRoot);

        await service.DiscardChangesAsync(
            repositoryRoot,
            snapshot,
            recycler,
            CancellationToken.None);

        Assert.Equal("tracked base\n", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
        Assert.Equal("tracked base\n", RunGit(repositoryRoot, "show", ":tracked.txt"));
        Assert.Equal("rename base\n", File.ReadAllText(Path.Combine(repositoryRoot, "rename-old.txt")));
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "rename-new.txt")));
        Assert.Equal("deleted base\n", File.ReadAllText(Path.Combine(repositoryRoot, "deleted.txt")));
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "new.txt")));
        Assert.Equal(3, recycler.Paths.Count);
        Assert.Equal(3, Directory.EnumerateFiles(trashRoot).Count());

        Assert.Equal("other working\n", File.ReadAllText(Path.Combine(repositoryRoot, "other.txt")));
        Assert.Equal("other staged\n", RunGit(repositoryRoot, "show", ":other.txt"));
        var remaining = Assert.Single(
            (await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
        Assert.Equal("other.txt", remaining.Path);
        Assert.Equal("M", remaining.IndexStatus);
        Assert.Equal("M", remaining.WorkTreeStatus);
    }

    [Fact]
    public async Task DiscardRejectsStaleSnapshotAndRecyclerFailureBeforeGitWrites()
    {
        WriteFile("tracked.txt", "base\n");
        Commit("initial");
        WriteFile("tracked.txt", "first edit\n");

        var service = new GitRepositoryService();
        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var file = Assert.Single(status.Changes);
        var snapshot = await service.CaptureDiscardSnapshotAsync(
            repositoryRoot,
            [file],
            CancellationToken.None);

        WriteFile("tracked.txt", "second edit with the same status\n");
        var staleRecycler = new FixtureRecycler(trashRoot);
        await Assert.ThrowsAsync<DiscardSnapshotStaleException>(
            () => service.DiscardChangesAsync(
                repositoryRoot,
                snapshot,
                staleRecycler,
                CancellationToken.None));
        Assert.Empty(staleRecycler.Paths);
        Assert.Equal(
            "second edit with the same status\n",
            File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
        Assert.Equal("base\n", RunGit(repositoryRoot, "show", ":tracked.txt"));

        var refreshedStatus = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var refreshedSnapshot = await service.CaptureDiscardSnapshotAsync(
            repositoryRoot,
            [Assert.Single(refreshedStatus.Changes)],
            CancellationToken.None);
        var failingRecycler = new FixtureRecycler(
            trashRoot,
            new IOException("synthetic Recycle Bin failure"));
        await Assert.ThrowsAsync<DiscardException>(
            () => service.DiscardChangesAsync(
                repositoryRoot,
                refreshedSnapshot,
                failingRecycler,
                CancellationToken.None));
        Assert.Single(failingRecycler.Paths);
        Assert.Equal(
            "second edit with the same status\n",
            File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
        Assert.Equal("base\n", RunGit(repositoryRoot, "show", ":tracked.txt"));
    }

    [Fact]
    public async Task DiscardBinaryAndStagedOnlyFilesRestoresGitStateAndPreservesUnrelatedIndex()
    {
        var binaryBase = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0x0A };
        WriteBytes("binary-tracked.bin", binaryBase);
        WriteFile("staged-only.txt", "staged base\n");
        WriteFile("unrelated.txt", "unrelated base\n");
        Commit("initial");
        var binaryBaseBlob = RunGit(repositoryRoot, "rev-parse", "HEAD:binary-tracked.bin").Trim();

        WriteBytes("binary-tracked.bin", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01, 0xFE, 0x0A });
        WriteFile("staged-only.txt", "staged edit\n");
        RunGit(repositoryRoot, "add", "--", "staged-only.txt");
        WriteFile("unrelated.txt", "unrelated staged\n");
        RunGit(repositoryRoot, "add", "--", "unrelated.txt");
        WriteFile("unrelated.txt", "unrelated working\n");

        var service = new GitRepositoryService();
        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var selected = status.Changes
            .Where(change => change.Path is "binary-tracked.bin" or "staged-only.txt")
            .ToArray();
        Assert.Equal(2, selected.Length);

        var snapshot = await service.CaptureDiscardSnapshotAsync(
            repositoryRoot,
            selected,
            CancellationToken.None);
        var recycler = new FixtureRecycler(trashRoot);
        await service.DiscardChangesAsync(
            repositoryRoot,
            snapshot,
            recycler,
            CancellationToken.None);

        Assert.Equal(
            binaryBase,
            await File.ReadAllBytesAsync(Path.Combine(repositoryRoot, "binary-tracked.bin")));
        Assert.Equal(
            binaryBaseBlob,
            RunGit(repositoryRoot, "rev-parse", ":binary-tracked.bin").Trim());
        Assert.Equal("staged base\n", File.ReadAllText(Path.Combine(repositoryRoot, "staged-only.txt")));
        Assert.Equal(2, recycler.Paths.Count);

        Assert.Equal("unrelated working\n", File.ReadAllText(Path.Combine(repositoryRoot, "unrelated.txt")));
        Assert.Equal("unrelated staged\n", RunGit(repositoryRoot, "show", ":unrelated.txt"));
        var remaining = Assert.Single(
            (await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);
        Assert.Equal("unrelated.txt", remaining.Path);
        Assert.Equal("M", remaining.IndexStatus);
        Assert.Equal("M", remaining.WorkTreeStatus);
    }

    [Fact]
    public async Task DiscardWithUntrackedSelectionLeavesCleanStatusUnderAutocrlf()
    {
        RunGit(repositoryRoot, "config", "core.autocrlf", "true");
        WriteFile("tracked.txt", "tracked base\n");
        Commit("initial");

        WriteFile("tracked.txt", "tracked edit\n");
        WriteFile("added-untracked.txt", "untracked\n");

        var service = new GitRepositoryService();
        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        Assert.Equal(2, status.Changes.Count);

        var snapshot = await service.CaptureDiscardSnapshotAsync(
            repositoryRoot,
            status.Changes,
            CancellationToken.None);
        var recycler = new FixtureRecycler(trashRoot);
        await service.DiscardChangesAsync(
            repositoryRoot,
            snapshot,
            recycler,
            CancellationToken.None);

        Assert.Equal(
            "tracked base\n",
            File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "added-untracked.txt")));
        Assert.Equal(2, recycler.Paths.Count);

        var remaining = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        Assert.Empty(remaining.Changes);
    }

    public void Dispose()
    {
        DeleteDirectory(repositoryRoot);
        DeleteDirectory(trashRoot);
    }

    private void WriteFile(string relativePath, string contents)
    {
        var fullPath = Path.Combine(repositoryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents, Utf8NoBom);
    }

    private void WriteBytes(string relativePath, byte[] contents)
    {
        var fullPath = Path.Combine(repositoryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, contents);
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

    private sealed class FixtureRecycler : IFileRecycler
    {
        private readonly string trashDirectory;
        private readonly Exception? failure;

        public FixtureRecycler(string trashDirectory, Exception? failure = null)
        {
            this.trashDirectory = trashDirectory;
            this.failure = failure;
        }

        public List<string> Paths { get; } = [];

        public Task RecycleAsync(string fullPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(fullPath);
            if (failure is not null)
            {
                throw failure;
            }

            Directory.CreateDirectory(trashDirectory);
            var destination = Path.Combine(
                trashDirectory,
                $"{Paths.Count:D2}-{Path.GetFileName(fullPath)}");
            File.Move(fullPath, destination);
            return Task.CompletedTask;
        }
    }
}
