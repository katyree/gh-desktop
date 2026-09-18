using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private const string WholeFileDiscardCheckChildName = "whole-file-discard-check";
    private const string WholeFileDiscardCheckMarkerName = ".wingit-whole-file-discard-check";

    private async Task RunWholeFileDiscardCheckAsync(NativeCaptureOptions options)
    {
        var fixtureParent = Path.GetFullPath(options.RepositoryPath!);
        var fixturePath = Path.Combine(fixtureParent, WholeFileDiscardCheckChildName);
        var markerPath = Path.Combine(fixturePath, WholeFileDiscardCheckMarkerName);
        var trashPath = Path.Combine(fixtureParent, "whole-file-discard-trash");
        var checksPath = options.OutputPath + ".checks.txt";
        var report = new StringBuilder();
        var previousDiagnosticMode = diagnosticCaptureMode;
        var cancellationToken = CancellationToken.None;
        var initialFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["staged-and-unstaged.txt"] = "base both\n",
            ["unstaged-only.txt"] = "base unstaged\n",
            ["staged-only.txt"] = "base staged\n",
            ["deleted-tracked.txt"] = "base deleted\n",
            ["unrelated.txt"] = "base unrelated\n",
            [WholeFileDiscardCheckMarkerName] = "WinGit whole-file discard check fixture\n",
        };
        var binaryBase = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0x0A };
        var binaryEdited = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01, 0xFE, 0x0A };
        var unstagedDiscardPaths = new[]
        {
            "binary-tracked.bin",
            "deleted-tracked.txt",
            "staged-and-unstaged.txt",
            "unstaged-only.txt",
            "untracked-note.txt",
        };

        try
        {
            diagnosticCaptureMode = true;
            report.AppendLine("whole-file-discard-check");
            report.AppendLine($"settings-directory={Environment.GetEnvironmentVariable(HandlerCheckSettingsVariable)}");
            report.AppendLine($"fixture-parent={fixtureParent}");
            report.AppendLine($"fixture={fixturePath}");
            report.AppendLine($"marker={markerPath}");

            Check(report, Directory.Exists(fixtureParent), "fixture parent exists", fixtureParent);
            Check(
                report,
                !Directory.Exists(Path.Combine(fixtureParent, ".git"))
                    && !File.Exists(Path.Combine(fixtureParent, ".git")),
                "fixture parent is not a repository",
                fixtureParent);
            Check(
                report,
                !Directory.Exists(fixturePath) && !File.Exists(fixturePath),
                "fixture child is new",
                fixturePath);

            await repositoryService.InitializeAsync(
                fixturePath,
                "main",
                cancellationToken: cancellationToken);
            await repositoryService.SetGitConfigValueAsync(
                fixturePath,
                GitConfigScope.Local,
                GitConfigSetting.UserName,
                "Test User",
                cancellationToken: cancellationToken);
            await repositoryService.SetGitConfigValueAsync(
                fixturePath,
                GitConfigScope.Local,
                GitConfigSetting.UserEmail,
                "test-user@example.invalid",
                cancellationToken: cancellationToken);

            var hooksPath = Path.Combine(fixturePath, ".hooks");
            Directory.CreateDirectory(hooksPath);
            var localConfigPath = Path.Combine(fixturePath, ".git", "config");
            await File.AppendAllTextAsync(
                localConfigPath,
                string.Join(
                    Environment.NewLine,
                    [
                        string.Empty,
                        "[core]",
                        $"\thooksPath = {hooksPath.Replace('\\', '/')}",
                        "[commit]",
                        "\tgpgSign = false",
                        string.Empty,
                    ]),
                cancellationToken);

            foreach (var (path, contents) in initialFiles)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(fixturePath, path),
                    contents,
                    cancellationToken);
            }

            await File.WriteAllBytesAsync(
                Path.Combine(fixturePath, "binary-tracked.bin"),
                binaryBase,
                cancellationToken);
            await repositoryService.StageFilesAsync(
                fixturePath,
                [.. initialFiles.Keys, "binary-tracked.bin"],
                cancellationToken);
            var commitId = await repositoryService.CommitAsync(
                fixturePath,
                "Create whole-file discard fixture",
                null,
                amend: false,
                cancellationToken: cancellationToken);
            Check(report, !string.IsNullOrWhiteSpace(commitId), "fixture committed", commitId);

            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, "staged-and-unstaged.txt"),
                "staged both\n",
                cancellationToken);
            await repositoryService.StageFilesAsync(
                fixturePath,
                ["staged-and-unstaged.txt"],
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, "staged-and-unstaged.txt"),
                "working both\n",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, "unstaged-only.txt"),
                "working unstaged\n",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, "staged-only.txt"),
                "staged edit\n",
                cancellationToken);
            await repositoryService.StageFilesAsync(
                fixturePath,
                ["staged-only.txt"],
                cancellationToken);
            File.Delete(Path.Combine(fixturePath, "deleted-tracked.txt"));
            await File.WriteAllBytesAsync(
                Path.Combine(fixturePath, "binary-tracked.bin"),
                binaryEdited,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, "untracked-note.txt"),
                "untracked note\n",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, "unrelated.txt"),
                "staged unrelated\n",
                cancellationToken);
            await repositoryService.StageFilesAsync(
                fixturePath,
                ["unrelated.txt"],
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, "unrelated.txt"),
                "working unrelated\n",
                cancellationToken);

            await OpenRepositoryAsync(fixturePath);
            await WaitForLatestOperationAsync();
            Check(
                report,
                !ErrorBar.IsOpen && SamePath(repositoryRoot, fixturePath),
                "fixture opened",
                DescribeWholeDiscardStatus(currentStatus));
            Check(
                report,
                currentStatus?.Changes.Count == unstagedDiscardPaths.Length + 2,
                "initial changed rows",
                DescribeWholeDiscardStatus(currentStatus));

            CommitSummaryBox.Text = "Discard check draft";
            CommitDescriptionBox.Text = "Discard check details";

            // The native Changes view keeps staged and unstaged selection in
            // separate lists: choosing a row in one list clears the other.
            // Whole-file discard therefore runs once per explicit list
            // selection, and this check mirrors both rounds.
            SelectWholeDiscardRows(report, UnstagedChangesList, unstagedChangeRows, unstagedDiscardPaths);
            await WaitForLatestOperationAsync();
            var unstagedDiscardFiles = GetDistinctDiscardFiles(
                GetDiscardRows(UnstagedChangesList, contextItem: null));
            Check(
                report,
                unstagedDiscardFiles.Count == unstagedDiscardPaths.Length
                    && unstagedDiscardPaths.All(path => unstagedDiscardFiles.Any(file =>
                        string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))),
                "unstaged discard files are the explicit selection",
                string.Join(",", unstagedDiscardFiles.Select(file => file.Path)));

            var snapshot = await repositoryService.CaptureDiscardSnapshotAsync(
                fixturePath,
                unstagedDiscardFiles,
                cancellationToken);
            Check(
                report,
                snapshot.Files.Count == unstagedDiscardPaths.Length,
                "discard snapshot captures selection",
                string.Join(",", snapshot.Files.Select(file => file.Path)));

            var confirmationTexts = CollectWholeDiscardConfirmationTexts(
                BuildFullDiscardConfirmationContent(snapshot));
            var confirmationJoined = string.Join("\n", confirmationTexts);
            Check(
                report,
                confirmationJoined.Contains("Recycle Bin", StringComparison.Ordinal),
                "confirmation describes Recycle Bin recovery",
                confirmationJoined);
            CheckWholeDiscardConfirmationLine(
                report,
                confirmationTexts,
                "binary-tracked.bin  ·  modified  ·  unstaged changes will be discarded");
            CheckWholeDiscardConfirmationLine(
                report,
                confirmationTexts,
                "deleted-tracked.txt  ·  deleted  ·  deleted file will be restored from Git");
            CheckWholeDiscardConfirmationLine(
                report,
                confirmationTexts,
                "staged-and-unstaged.txt  ·  modified  ·  staged and unstaged changes will be discarded");
            CheckWholeDiscardConfirmationLine(
                report,
                confirmationTexts,
                "unstaged-only.txt  ·  modified  ·  unstaged changes will be discarded");
            CheckWholeDiscardConfirmationLine(
                report,
                confirmationTexts,
                "untracked-note.txt  ·  untracked  ·  untracked content will be moved to the Recycle Bin");

            // Cancel is the snapshot path without a mutation: files, index,
            // selection, and drafts must all be unchanged.
            var cancelWorktree = await ReadWholeDiscardWorktreeAsync(
                fixturePath,
                [.. unstagedDiscardPaths, "staged-only.txt", "unrelated.txt"],
                cancellationToken);
            var cancelStatus = DescribeWholeDiscardStatus(currentStatus);
            Check(report, !ErrorBar.IsOpen, "cancel leaves error bar closed", ErrorBar.Message);
            Check(
                report,
                string.Equals(DescribeWholeDiscardStatus(currentStatus), cancelStatus, StringComparison.Ordinal),
                "cancel leaves status unchanged",
                cancelStatus);
            Check(
                report,
                (await ReadWholeDiscardWorktreeAsync(
                    fixturePath,
                    [.. unstagedDiscardPaths, "staged-only.txt", "unrelated.txt"],
                    cancellationToken)).SequenceEqual(cancelWorktree),
                "cancel leaves files unchanged",
                cancelStatus);
            Check(
                report,
                string.Equals(CommitSummaryBox.Text, "Discard check draft", StringComparison.Ordinal)
                    && string.Equals(CommitDescriptionBox.Text, "Discard check details", StringComparison.Ordinal),
                "cancel leaves drafts unchanged",
                $"{CommitSummaryBox.Text}|{CommitDescriptionBox.Text}");
            var cancelSelection = UnstagedChangesList.SelectedItems.OfType<ChangeRow>()
                .Select(row => row.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check(
                report,
                unstagedDiscardPaths.All(path => cancelSelection.Contains(path))
                    && StagedChangesList.SelectedItems.Count == 0,
                "cancel leaves selection unchanged",
                string.Join(",", cancelSelection));

            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, "staged-and-unstaged.txt"),
                "stale edit\n",
                cancellationToken);
            var staleRecycler = new WholeDiscardFixtureRecycler(trashPath);
            var staleRejected = false;
            try
            {
                await repositoryService.DiscardChangesAsync(
                    fixturePath,
                    snapshot,
                    staleRecycler,
                    cancellationToken);
            }
            catch (DiscardSnapshotStaleException exception)
            {
                staleRejected = !string.IsNullOrWhiteSpace(exception.Message);
            }

            Check(report, staleRejected, "stale snapshot refused", "stale edit after confirmation");
            Check(report, staleRecycler.Paths.Count == 0, "stale discard recycles nothing", $"recycled={staleRecycler.Paths.Count}");
            Check(
                report,
                string.Equals(
                    await File.ReadAllTextAsync(Path.Combine(fixturePath, "staged-and-unstaged.txt"), cancellationToken),
                    "stale edit\n",
                    StringComparison.Ordinal),
                "stale discard preserves worktree",
                "staged-and-unstaged.txt");
            await CheckWholeDiscardIndexAsync(
                report,
                fixturePath,
                "staged-and-unstaged.txt",
                "staged both",
                cancellationToken);

            var freshSnapshot = await repositoryService.CaptureDiscardSnapshotAsync(
                fixturePath,
                unstagedDiscardFiles,
                cancellationToken);
            var recycler = new WholeDiscardFixtureRecycler(trashPath);
            await repositoryService.DiscardChangesAsync(
                fixturePath,
                freshSnapshot,
                recycler,
                cancellationToken);
            Check(report, recycler.Paths.Count == unstagedDiscardPaths.Length - 1, "unstaged discard recycles selected files", $"recycled={recycler.Paths.Count}");

            await CheckWholeDiscardWorktreeTextAsync(report, fixturePath, "staged-and-unstaged.txt", "base both\n", cancellationToken);
            await CheckWholeDiscardWorktreeTextAsync(report, fixturePath, "unstaged-only.txt", "base unstaged\n", cancellationToken);
            var restoredBinary = await File.ReadAllBytesAsync(
                Path.Combine(fixturePath, "binary-tracked.bin"),
                cancellationToken);
            Check(
                report,
                restoredBinary.SequenceEqual(binaryBase),
                "discard restores binary bytes",
                $"length={restoredBinary.Length}");
            await CheckWholeDiscardWorktreeTextAsync(report, fixturePath, "deleted-tracked.txt", "base deleted\n", cancellationToken);
            Check(
                report,
                !File.Exists(Path.Combine(fixturePath, "untracked-note.txt")),
                "discard removes untracked file",
                "untracked-note.txt");
            Check(
                report,
                Directory.Exists(trashPath)
                    && Directory.EnumerateFiles(trashPath).Any(path =>
                        path.EndsWith("untracked-note.txt", StringComparison.OrdinalIgnoreCase)),
                "discard preserves untracked file aside",
                trashPath);
            await CheckWholeDiscardWorktreeTextAsync(report, fixturePath, "staged-only.txt", "staged edit\n", cancellationToken);
            await CheckWholeDiscardWorktreeTextAsync(report, fixturePath, "unrelated.txt", "working unrelated\n", cancellationToken);
            await CheckWholeDiscardIndexAsync(report, fixturePath, "unrelated.txt", "staged unrelated", cancellationToken);

            await RefreshAfterMutationAsync(fixturePath);
            await WaitForLatestOperationAsync();
            var remainingAfterUnstaged = (currentStatus?.Changes ?? Array.Empty<FileChange>())
                .Select(change => $"{change.Path}:{change.IndexStatus}/{change.WorkTreeStatus}")
                .ToHashSet(StringComparer.Ordinal);
            Check(
                report,
                remainingAfterUnstaged.SetEquals(["staged-only.txt:M/", "unrelated.txt:M/M"]),
                "unstaged discard leaves staged-only and unrelated",
                DescribeWholeDiscardStatus(currentStatus));
            Check(report, !ErrorBar.IsOpen, "unstaged refresh leaves error bar closed", ErrorBar.Message);

            SelectWholeDiscardRows(report, StagedChangesList, stagedChangeRows, ["staged-only.txt"]);
            await WaitForLatestOperationAsync();
            var stagedDiscardFiles = GetDistinctDiscardFiles(
                GetDiscardRows(StagedChangesList, contextItem: null));
            AssertSingleWholeDiscardFile(report, stagedDiscardFiles, "staged-only.txt");
            var stagedSnapshot = await repositoryService.CaptureDiscardSnapshotAsync(
                fixturePath,
                stagedDiscardFiles,
                cancellationToken);
            var stagedConfirmationTexts = CollectWholeDiscardConfirmationTexts(
                BuildFullDiscardConfirmationContent(stagedSnapshot));
            CheckWholeDiscardConfirmationLine(
                report,
                stagedConfirmationTexts,
                "staged-only.txt  ·  modified  ·  staged changes will be discarded");

            var stagedRecycler = new WholeDiscardFixtureRecycler(trashPath);
            await repositoryService.DiscardChangesAsync(
                fixturePath,
                stagedSnapshot,
                stagedRecycler,
                cancellationToken);
            Check(report, stagedRecycler.Paths.Count == 1, "staged discard recycles one file", $"recycled={stagedRecycler.Paths.Count}");
            await CheckWholeDiscardWorktreeTextAsync(report, fixturePath, "staged-only.txt", "base staged\n", cancellationToken);
            await CheckWholeDiscardWorktreeTextAsync(report, fixturePath, "unrelated.txt", "working unrelated\n", cancellationToken);
            await CheckWholeDiscardIndexAsync(report, fixturePath, "unrelated.txt", "staged unrelated", cancellationToken);
            Check(
                report,
                string.Equals(CommitSummaryBox.Text, "Discard check draft", StringComparison.Ordinal)
                    && string.Equals(CommitDescriptionBox.Text, "Discard check details", StringComparison.Ordinal),
                "confirm leaves drafts unchanged",
                $"{CommitSummaryBox.Text}|{CommitDescriptionBox.Text}");

            await RefreshAfterMutationAsync(fixturePath);
            await WaitForLatestOperationAsync();
            var remaining = currentStatus?.Changes.ToArray() ?? [];
            Check(
                report,
                remaining.Length == 1
                    && string.Equals(remaining[0].Path, "unrelated.txt", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(remaining[0].IndexStatus, "M", StringComparison.Ordinal)
                    && string.Equals(remaining[0].WorkTreeStatus, "M", StringComparison.Ordinal),
                "refresh leaves only unrelated change",
                DescribeWholeDiscardStatus(currentStatus));
            Check(report, !ErrorBar.IsOpen, "refresh leaves error bar closed", ErrorBar.Message);

            var unrelatedRow = unstagedChangeRows.FirstOrDefault(row =>
                row.Path.Equals("unrelated.txt", StringComparison.OrdinalIgnoreCase));
            Check(report, unrelatedRow is not null, "unrelated row visible", DescribeWholeDiscardStatus(currentStatus));
            UnstagedChangesList.SelectedItems.Clear();
            UnstagedChangesList.SelectedItems.Add(unrelatedRow!);
            await WaitForLatestOperationAsync();
            Check(
                report,
                string.Equals(selectedChangePath, "unrelated.txt", StringComparison.OrdinalIgnoreCase)
                    && DiffFileText.Text.Contains("unrelated.txt", StringComparison.Ordinal),
                "refresh reloads selected diff",
                $"{selectedChangePath}|{DiffFileText.Text}");

            report.AppendLine($"final-context=root={repositoryRoot}; changes={DescribeWholeDiscardStatus(currentStatus)}");
            StatusText.Text = "Whole-file discard check passed";
        }
        catch (Exception exception)
        {
            report.AppendLine($"FAIL {exception.Message}");
            throw;
        }
        finally
        {
            diagnosticCaptureMode = previousDiagnosticMode;
            await WriteHandlerCheckReportAsync(checksPath, report);
        }

        await CaptureRootGridAsync(options.OutputPath);
    }

    private void SelectWholeDiscardRows(
        StringBuilder report,
        ListView list,
        IReadOnlyList<ChangeRow> rows,
        IReadOnlyList<string> paths)
    {
        // Selecting a row in one Changes list clears the other list, so each
        // discard round selects within a single list like the real menus do.
        StagedChangesList.SelectedItems.Clear();
        UnstagedChangesList.SelectedItems.Clear();
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (wanted.Contains(row.Path) && !list.SelectedItems.Contains(row))
            {
                list.SelectedItems.Add(row);
            }
        }

        var selected = list.SelectedItems.OfType<ChangeRow>()
            .Select(row => row.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(
            report,
            paths.All(path => selected.Contains(path)),
            "discard rows selected",
            string.Join(",", selected));
    }

    private static void AssertSingleWholeDiscardFile(
        StringBuilder report,
        IReadOnlyList<FileChange> files,
        string expectedPath)
    {
        var file = files.Count == 1 ? files[0] : null;
        Check(
            report,
            file is not null && string.Equals(file.Path, expectedPath, StringComparison.OrdinalIgnoreCase),
            "staged discard file is the explicit selection",
            string.Join(",", files.Select(candidate => candidate.Path)));
    }

    private static void CheckWholeDiscardConfirmationLine(
        StringBuilder report,
        List<string> texts,
        string expectedLine)
    {
        Check(
            report,
            texts.Any(text => string.Equals(text, expectedLine, StringComparison.Ordinal)),
            $"confirmation line: {expectedLine}",
            string.Join("\n", texts));
    }

    private async Task CheckWholeDiscardIndexAsync(
        StringBuilder report,
        string root,
        string path,
        string expectedLine,
        CancellationToken cancellationToken)
    {
        var change = currentStatus?.Changes.FirstOrDefault(candidate =>
            candidate.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (change is null)
        {
            Check(report, false, $"discard index contents {path}", DescribeWholeDiscardStatus(currentStatus));
            return;
        }

        var diff = await repositoryService.GetIndexDiffAsync(root, change, cancellationToken);
        var addedContents = string.Join(
            "\n",
            diff.Lines
                .Where(line => line.Kind == DiffLineKind.Added)
                .Select(line => line.Text));
        Check(
            report,
            !diff.IsBinary && !diff.IsTruncated && string.Equals(addedContents, expectedLine, StringComparison.Ordinal),
            $"discard index contents {path}",
            $"added={addedContents}; expected={expectedLine}");
    }

    private static async Task CheckWholeDiscardWorktreeTextAsync(
        StringBuilder report,
        string root,
        string path,
        string expectedContents,
        CancellationToken cancellationToken)
    {
        var actualContents = NormalizeWholeDiscardText(await File.ReadAllTextAsync(
            Path.Combine(root, path),
            cancellationToken));
        Check(
            report,
            string.Equals(actualContents, expectedContents, StringComparison.Ordinal),
            $"discard worktree contents {path}",
            $"actual={actualContents}; expected={expectedContents}");
    }

    private static async Task<List<string>> ReadWholeDiscardWorktreeAsync(
        string root,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var contents = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            var fullPath = Path.Combine(root, path);
            contents.Add(File.Exists(fullPath)
                ? NormalizeWholeDiscardText(await File.ReadAllTextAsync(fullPath, cancellationToken))
                : "<missing>");
        }

        return contents;
    }

    /// <summary>
    /// Normalizes restored worktree line endings before comparison. The pinned
    /// bundled Git runtime ships core.autocrlf=true, so a Git restore writes
    /// CRLF while the fixture authors LF. The comparison targets content, not
    /// the platform checkout convention.
    /// </summary>
    private static string NormalizeWholeDiscardText(string contents) =>
        contents.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static List<string> CollectWholeDiscardConfirmationTexts(DependencyObject element)
    {
        var texts = new List<string>();
        CollectWholeDiscardConfirmationText(element, texts);
        return texts;
    }

    private static void CollectWholeDiscardConfirmationText(DependencyObject element, List<string> texts)
    {
        if (element is TextBlock block && !string.IsNullOrEmpty(block.Text))
        {
            texts.Add(block.Text);
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            CollectWholeDiscardConfirmationText(VisualTreeHelper.GetChild(element, index), texts);
        }

        // The confirmation content is inspected before it joins a live visual
        // tree, so also walk the logical children of detached containers.
        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                CollectWholeDiscardConfirmationText(child, texts);
            }
        }
        else if (element is ContentControl contentControl
            && contentControl.Content is DependencyObject content)
        {
            CollectWholeDiscardConfirmationText(content, texts);
        }
    }

    private static string DescribeWholeDiscardStatus(RepositoryStatus? status) =>
        status is null
            ? "<null>"
            : string.Join(
                ",",
                status.Changes.Select(change =>
                    $"{change.Path}:{change.IndexStatus}/{change.WorkTreeStatus}"));

    private sealed class WholeDiscardFixtureRecycler(string trashDirectory) : IFileRecycler
    {
        private int recycledCount;

        public List<string> Paths { get; } = [];

        public Task RecycleAsync(string fullPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(fullPath);
            recycledCount++;
            Directory.CreateDirectory(trashDirectory);
            var destination = Path.Combine(
                trashDirectory,
                $"{recycledCount:D2}-{Path.GetFileName(fullPath)}");
            File.Move(fullPath, destination);
            return Task.CompletedTask;
        }
    }
}
