using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryServiceTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryServiceTests()
    {
        repositoryRoot = Path.Combine(
            Path.GetTempPath(),
            "WinGit.Core.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);

        RunGit(repositoryRoot, "init");
        RunGit(repositoryRoot, "config", "user.name", "Test User");
        RunGit(repositoryRoot, "config", "user.email", "test-user@example.invalid");
        ConfigureLocalCommitSafety(repositoryRoot);
    }

    [Fact]
    public async Task StatusAndWorkingDiffLeaveIndexAndWorkTreeUnchanged()
    {
        WriteFile("tracked.txt", "before\n");
        Commit("initial");
        WriteFile("tracked.txt", "staged\n");
        RunGit(repositoryRoot, "add", "--", "tracked.txt");
        var indexObjectBefore = RunGit(repositoryRoot, "rev-parse", ":tracked.txt").Trim();
        WriteFile("tracked.txt", "after\n");
        RunGit(repositoryRoot, "config", "color.ui", "always");

        var service = new GitRepositoryService();
        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var change = Assert.Single(status.Changes);
        Assert.Equal(ChangeKind.Modified, change.Kind);
        Assert.Equal("M", change.IndexStatus);
        Assert.Equal("M", change.WorkTreeStatus);

        var diff = await service.GetWorkingDiffAsync(repositoryRoot, change, CancellationToken.None);
        Assert.False(diff.IsBinary);
        Assert.False(diff.IsTruncated);
        Assert.Contains(diff.Lines, line => line.Kind == DiffLineKind.Removed && line.Text == "before");
        Assert.Contains(diff.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "after");
        Assert.DoesNotContain(diff.Lines, line => line.Text.Contains('\u001b'));

        Assert.Equal(indexObjectBefore, RunGit(repositoryRoot, "rev-parse", ":tracked.txt").Trim());
        Assert.Equal("after\n", File.ReadAllText(Path.Combine(repositoryRoot, "tracked.txt")));
    }

    [Fact]
    public async Task WorkingDiffCanHideWhitespaceOnlyChangesWithoutChangingDefault()
    {
        WriteFile(
            "tracked.txt",
            "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\nnine\nten\neleven\ntwelve\n");
        Commit("initial");
        WriteFile(
            "tracked.txt",
            "one\n two  \nthree\nfour\nfive\nsix\nseven\neight\nchanged\nten\neleven\ntwelve\n");

        var service = new GitRepositoryService();
        var change = Assert.Single((await service.GetStatusAsync(repositoryRoot, CancellationToken.None)).Changes);

        var baseline = await service.GetUnstagedDiffAsync(repositoryRoot, change, CancellationToken.None);
        Assert.Contains(baseline.Lines, line => line.Kind == DiffLineKind.Added && line.Text == " two  ");
        Assert.Contains(baseline.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "changed");

        var hidden = await service.GetUnstagedDiffAsync(
            repositoryRoot,
            change,
            CancellationToken.None,
            hideWhitespaceChanges: true);
        Assert.DoesNotContain(hidden.Lines, line => line.Text == " two  ");
        Assert.Contains(hidden.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "changed");
        Assert.Equal(" two  ", File.ReadAllLines(Path.Combine(repositoryRoot, "tracked.txt"))[1]);
    }

    [Fact]
    public void StatusAheadBehindParsingPreservesDivergedCounts()
    {
        GitRepositoryService.ParseAheadBehind("+3 -2", out var ahead, out var behind);

        Assert.Equal(3, ahead);
        Assert.Equal(2, behind);
    }

    [Fact]
    public async Task StatusReportsStagedRenameAndUntrackedPathAndOpenFindsNestedRoot()
    {
        WriteFile("old.txt", "same content\n");
        Commit("initial");
        RunGit(repositoryRoot, "mv", "old.txt", "renamed.txt");
        WriteFile("untracked.txt", "new content\n");
        var nestedDirectory = Path.Combine(repositoryRoot, "nested");
        Directory.CreateDirectory(nestedDirectory);

        var service = new GitRepositoryService();
        var opened = await service.OpenAsync(nestedDirectory, CancellationToken.None);
        Assert.Equal(Path.GetFullPath(repositoryRoot), opened.RootPath);

        var rename = Assert.Single(opened.Changes, change => change.Kind == ChangeKind.Renamed);
        Assert.Equal("renamed.txt", rename.Path);
        Assert.Equal("old.txt", rename.OldPath);
        var untracked = Assert.Single(opened.Changes, change => change.Kind == ChangeKind.Untracked);
        Assert.Equal("untracked.txt", untracked.Path);
    }

    [Fact]
    public async Task HistoryAndCommitFilesIncludeRootCommitAndNestedFile()
    {
        WriteFile(Path.Combine("nested", "first.txt"), "root content\n");
        Commit("root commit");
        var rootCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        var mainBranch = RunGit(repositoryRoot, "branch", "--show-current").Trim();
        RunGit(repositoryRoot, "branch", "side");
        WriteFile(Path.Combine("nested", "first.txt"), "second content\n");
        Commit("second commit");
        RunGit(repositoryRoot, "checkout", "side");
        WriteFile("side.txt", "side content\n");
        Commit("side commit");
        RunGit(repositoryRoot, "checkout", mainBranch);
        RunGit(repositoryRoot, "merge", "--no-ff", "side", "--message", "merge commit");
        var mergeCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
        RunGit(repositoryRoot, "config", "color.ui", "always");

        var service = new GitRepositoryService();
        var history = await service.GetHistoryAsync(repositoryRoot, 100, CancellationToken.None);
        Assert.Contains(history, commit => commit.Id == rootCommit && commit.Summary == "root commit");
        Assert.DoesNotContain(history, commit => commit.Summary.Contains('\u001b'));

        var files = await service.GetCommitFilesAsync(repositoryRoot, rootCommit, CancellationToken.None);
        var file = Assert.Single(files);
        Assert.Equal("nested/first.txt", file.Path);
        Assert.Equal(ChangeKind.Added, file.Kind);

        var diff = await service.GetCommitDiffAsync(repositoryRoot, rootCommit, file, CancellationToken.None);
        Assert.Contains(diff.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "root content");
        Assert.DoesNotContain(diff.Lines, line => line.Text.Contains('\u001b'));

        var mergeFiles = await service.GetCommitFilesAsync(repositoryRoot, mergeCommit, CancellationToken.None);
        var mergeFile = Assert.Single(mergeFiles);
        Assert.Equal("side.txt", mergeFile.Path);
        var mergeDiff = await service.GetCommitDiffAsync(repositoryRoot, mergeCommit, mergeFile, CancellationToken.None);
        Assert.Contains(mergeDiff.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "side content");
    }

    [Fact]
    public async Task UnbornHistoryIsEmptyAndPreCancelledCallsAreHonored()
    {
        var unbornRoot = Path.Combine(
            Path.GetTempPath(),
            "WinGit.Core.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unbornRoot);
        try
        {
            RunGit(unbornRoot, "init");
            var service = new GitRepositoryService();
            var history = await service.GetHistoryAsync(unbornRoot, cancellationToken: CancellationToken.None);
            Assert.Empty(history);

            WriteUnbornFile(unbornRoot, "new.txt", "unborn content\n");
            var unbornStatus = await service.GetStatusAsync(unbornRoot, CancellationToken.None);
            var unbornChange = Assert.Single(unbornStatus.Changes);
            var unbornDiff = await service.GetWorkingDiffAsync(unbornRoot, unbornChange, CancellationToken.None);
            Assert.Contains(unbornDiff.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "unborn content");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.GetStatusAsync(repositoryRoot, cancellation.Token));
        }
        finally
        {
            DeleteDirectory(unbornRoot);
        }
    }

    [Fact]
    public async Task StageAndCommitOnlySelectedFileLeavesOtherWorkingChange()
    {
        WriteFile("selected.txt", "selected before\n");
        WriteFile("other.txt", "other before\n");
        Commit("initial");
        WriteFile("selected.txt", "selected after\n");
        WriteFile("other.txt", "other after\n");

        var service = new GitRepositoryService();
        await service.StageFilesAsync(repositoryRoot, ["selected.txt"], CancellationToken.None);
        var stagedStatus = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var staged = Assert.Single(stagedStatus.Changes, change => change.Path == "selected.txt");
        var other = Assert.Single(stagedStatus.Changes, change => change.Path == "other.txt");
        Assert.Equal("M", staged.IndexStatus);
        Assert.Equal(string.Empty, staged.WorkTreeStatus);
        Assert.Equal(string.Empty, other.IndexStatus);
        Assert.Equal("M", other.WorkTreeStatus);

        var indexDiff = await service.GetIndexDiffAsync(repositoryRoot, staged, CancellationToken.None);
        Assert.Contains(indexDiff.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "selected after");
        var unstagedDiff = await service.GetUnstagedDiffAsync(repositoryRoot, other, CancellationToken.None);
        Assert.Contains(unstagedDiff.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "other after");

        var commitId = await service.CommitAsync(
            repositoryRoot,
            "select one",
            "Keep the other edit in the work tree.",
            amend: false,
            CancellationToken.None);
        Assert.Equal(RunGit(repositoryRoot, "rev-parse", "HEAD").Trim(), commitId);
        Assert.Contains("select one", RunGit(repositoryRoot, "log", "-1", "--format=%B"));
        var committedFiles = await service.GetCommitFilesAsync(repositoryRoot, commitId, CancellationToken.None);
        var committedFile = Assert.Single(committedFiles);
        Assert.Equal("selected.txt", committedFile.Path);
        Assert.Equal("selected after\n", File.ReadAllText(Path.Combine(repositoryRoot, "selected.txt")));
        Assert.Equal("other after\n", File.ReadAllText(Path.Combine(repositoryRoot, "other.txt")));

        var remainingStatus = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var remaining = Assert.Single(remainingStatus.Changes);
        Assert.Equal("other.txt", remaining.Path);
        Assert.Equal("M", remaining.WorkTreeStatus);
    }

    [Fact]
    public async Task ImageDiffsCaptureHeadIndexAndWorkingPngVersionsWithoutTextDecoding()
    {
        var headPng = CreatePngFixture(0x10);
        var indexPng = CreatePngFixture(0x20);
        var workingPng = CreatePngFixture(0x30);
        WriteBytes("image.dat", headPng);
        Commit("initial image");
        WriteBytes("image.dat", indexPng);
        RunGit(repositoryRoot, "add", "--", "image.dat");
        WriteBytes("image.dat", workingPng);

        var service = new GitRepositoryService();
        var status = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var file = Assert.Single(status.Changes);

        var workingDiff = await service.GetWorkingDiffAsync(repositoryRoot, file, CancellationToken.None);
        AssertImageBytes(workingDiff, headPng, workingPng, afterObjectId: null);

        var stagedDiff = await service.GetIndexDiffAsync(repositoryRoot, file, CancellationToken.None);
        AssertImageBytes(stagedDiff, headPng, indexPng, afterObjectId: RunGit(repositoryRoot, "rev-parse", ":image.dat").Trim());

        var unstagedDiff = await service.GetUnstagedDiffAsync(repositoryRoot, file, CancellationToken.None);
        AssertImageBytes(unstagedDiff, indexPng, workingPng, afterObjectId: null);

        WriteBytes("opaque.bin", [0x00, 0x01, 0xFF, 0x7F]);
        var opaqueStatus = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var opaqueFile = Assert.Single(opaqueStatus.Changes, change => change.Path == "opaque.bin");
        var opaqueDiff = await service.GetWorkingDiffAsync(repositoryRoot, opaqueFile, CancellationToken.None);
        Assert.True(opaqueDiff.IsBinary);
        Assert.Null(opaqueDiff.ImageComparison);
        File.Delete(Path.Combine(repositoryRoot, "opaque.bin"));

        var commitId = await service.CommitAsync(
            repositoryRoot,
            "commit image",
            null,
            amend: false,
            CancellationToken.None);
        var committedFile = Assert.Single(await service.GetCommitFilesAsync(
            repositoryRoot,
            commitId,
            CancellationToken.None));
        var commitDiff = await service.GetCommitDiffAsync(
            repositoryRoot,
            commitId,
            committedFile,
            CancellationToken.None);
        AssertImageBytes(commitDiff, headPng, indexPng, afterObjectId: RunGit(repositoryRoot, "rev-parse", $"{commitId}:image.dat").Trim());

        WriteBytes("image.dat", [0x00, 0x01, 0xFF, 0x7F]);
        var unsupportedStatus = await service.GetStatusAsync(repositoryRoot, CancellationToken.None);
        var unsupportedFile = Assert.Single(
            unsupportedStatus.Changes,
            change => change.Path == "image.dat");
        var unsupportedDiff = await service.GetWorkingDiffAsync(
            repositoryRoot,
            unsupportedFile,
            CancellationToken.None);
        Assert.True(unsupportedDiff.IsBinary);
        Assert.NotNull(unsupportedDiff.ImageComparison);
        Assert.Equal(indexPng, unsupportedDiff.ImageComparison!.Before!.Bytes.ToArray());
        Assert.Null(unsupportedDiff.ImageComparison.After);
        Assert.Equal(
            "This revision is not a supported image.",
            unsupportedDiff.ImageComparison.Message);
    }

    [Fact]
    public async Task CommitAndAmendNormalizeCrOnlyDescriptionLineEndings()
    {
        WriteFile("tracked.txt", "initial\n");
        Commit("initial");
        WriteFile("tracked.txt", "committed\n");
        RunGit(repositoryRoot, "add", "--", "tracked.txt");

        var service = new GitRepositoryService();
        var commitId = await service.CommitAsync(
            repositoryRoot,
            "normal commit",
            "first paragraph\r\rsecond paragraph",
            amend: false,
            CancellationToken.None);
        var normalMessage = RunGit(repositoryRoot, "show", "--quiet", "--format=%s%x00%b", commitId);
        var normalParts = normalMessage.Split(['\0'], 2, StringSplitOptions.None);
        Assert.Equal("normal commit", normalParts[0].TrimEnd('\r', '\n'));
        Assert.Equal("first paragraph\n\nsecond paragraph", normalParts[1].Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n'));
        Assert.DoesNotContain('\r', normalParts[1]);

        WriteFile("tracked.txt", "amended\n");
        RunGit(repositoryRoot, "add", "--", "tracked.txt");
        var amendedId = await service.CommitAsync(
            repositoryRoot,
            "amended commit",
            "amended first\ramended second",
            amend: true,
            CancellationToken.None);
        var amendedMessage = RunGit(repositoryRoot, "show", "--quiet", "--format=%s%x00%b", amendedId);
        var amendedParts = amendedMessage.Split(['\0'], 2, StringSplitOptions.None);
        Assert.Equal("amended commit", amendedParts[0].TrimEnd('\r', '\n'));
        Assert.Equal("amended first\namended second", amendedParts[1].Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n'));
        Assert.DoesNotContain('\r', amendedParts[1]);
    }

    [Fact]
    public async Task CommitMessageReadsSubjectAndBodyWithoutTrailingLineEndings()
    {
        WriteFile("tracked.txt", "initial\n");
        Commit("initial subject\n\nfirst paragraph\n\nsecond paragraph");
        var headId = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        var service = new GitRepositoryService();
        var message = await service.GetCommitMessageAsync(
            repositoryRoot,
            headId,
            CancellationToken.None);

        Assert.Equal("initial subject", message.Summary);
        Assert.Equal("first paragraph\n\nsecond paragraph", message.Description);
    }

    [Fact]
    public async Task AmendRejectsWhenExpectedHeadChanged()
    {
        WriteFile("tracked.txt", "initial\n");
        Commit("initial");
        var expectedHeadId = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();

        WriteFile("tracked.txt", "external\n");
        RunGit(repositoryRoot, "add", "--", "tracked.txt");
        Commit("external commit");
        WriteFile("tracked.txt", "pending amend\n");
        RunGit(repositoryRoot, "add", "--", "tracked.txt");

        var service = new GitRepositoryService();
        await Assert.ThrowsAsync<CommitMessageSnapshotStaleException>(
            () => service.CommitAsync(
                repositoryRoot,
                "stale amend",
                null,
                amend: true,
                CancellationToken.None,
                expectedHeadId));

        Assert.Equal("external commit", RunGit(repositoryRoot, "show", "-s", "--format=%s", "HEAD").Trim());
    }

    [Fact]
    public async Task UnstageWorksBeforeTheFirstCommit()
    {
        var unbornRoot = CreateUnbornRepository();
        try
        {
            WriteUnbornFile(unbornRoot, "new.txt", "new content\n");
            var service = new GitRepositoryService();
            await service.StageFilesAsync(unbornRoot, ["new.txt"], CancellationToken.None);
            var stagedStatus = await service.GetStatusAsync(unbornRoot, CancellationToken.None);
            var staged = Assert.Single(stagedStatus.Changes);
            Assert.Equal(ChangeKind.Added, staged.Kind);
            Assert.Equal("A", staged.IndexStatus);

            await service.UnstageFilesAsync(unbornRoot, ["new.txt"], CancellationToken.None);
            var unstageStatus = await service.GetStatusAsync(unbornRoot, CancellationToken.None);
            var untracked = Assert.Single(unstageStatus.Changes);
            Assert.Equal(ChangeKind.Untracked, untracked.Kind);
            Assert.Equal("?", untracked.WorkTreeStatus);
            Assert.Equal("new content\n", File.ReadAllText(Path.Combine(unbornRoot, "new.txt")));
        }
        finally
        {
            DeleteDirectory(unbornRoot);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(repositoryRoot))
        {
            DeleteDirectory(repositoryRoot);
        }
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

    private static byte[] CreatePngFixture(byte marker) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, marker, 0x00, 0x01, 0x02, 0x03, 0x00,
    ];

    private static void AssertImageBytes(
        FileDiff diff,
        byte[] expectedBefore,
        byte[] expectedAfter,
        string? afterObjectId)
    {
        Assert.True(diff.IsBinary);
        Assert.NotNull(diff.ImageComparison);
        Assert.False(diff.ImageComparison!.IsTruncated);
        Assert.Equal(expectedBefore, diff.ImageComparison.Before!.Bytes.ToArray());
        Assert.Equal(expectedAfter, diff.ImageComparison.After!.Bytes.ToArray());
        Assert.Equal("image/png", diff.ImageComparison.Before.MediaType);
        Assert.Equal("image/png", diff.ImageComparison.After.MediaType);
        Assert.NotNull(diff.ImageComparison.Before.ObjectId);
        Assert.Equal(afterObjectId, diff.ImageComparison.After.ObjectId);
    }

    private void Commit(string message)
    {
        RunGit(repositoryRoot, "add", "--all");
        RunGit(repositoryRoot, "commit", "--message", message);
    }

    private static string CreateUnbornRepository()
    {
        Directory.CreateDirectory(FixtureParent);
        var path = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        RunGit(path, "init");
        RunGit(path, "config", "user.name", "Test User");
        RunGit(path, "config", "user.email", "test-user@example.invalid");
        ConfigureLocalCommitSafety(path);
        return path;
    }

    private static void ConfigureLocalCommitSafety(string path)
    {
        var hooksPath = Path.Combine(path, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(path, "config", "core.hooksPath", hooksPath);
        RunGit(path, "config", "commit.gpgsign", "false");
    }

    private static void WriteUnbornFile(string root, string relativePath, string contents)
    {
        var fullPath = Path.Combine(root, relativePath);
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
