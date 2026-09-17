using System.Text;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private const string WholeFileStagingCheckChildName = "whole-file-staging-check";
    private const string WholeFileStagingCheckMarkerName = ".wingit-whole-file-staging-check";

    private async Task RunWholeFileStagingCheckAsync(NativeCaptureOptions options)
    {
        var fixtureParent = Path.GetFullPath(options.RepositoryPath!);
        var fixturePath = Path.Combine(fixtureParent, WholeFileStagingCheckChildName);
        var markerPath = Path.Combine(fixturePath, WholeFileStagingCheckMarkerName);
        var checksPath = options.OutputPath + ".checks.txt";
        var report = new StringBuilder();
        var previousDiagnosticMode = diagnosticCaptureMode;
        var cancellationToken = CancellationToken.None;
        var initialFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["visible-one.txt"] = "initial one\n",
            ["visible-two.txt"] = "initial two\n",
            ["hidden.txt"] = "initial hidden\n",
            [WholeFileStagingCheckMarkerName] = "WinGit whole-file staging check fixture\n",
        };
        var changedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["visible-one.txt"] = "changed one\n",
            ["visible-two.txt"] = "changed two\n",
            ["hidden.txt"] = "changed hidden\n",
        };

        try
        {
            diagnosticCaptureMode = true;
            report.AppendLine("whole-file-staging-check");
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
            report.AppendLine($"hooks-path={hooksPath}");

            foreach (var (path, contents) in initialFiles)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(fixturePath, path),
                    contents,
                    cancellationToken);
            }

            await repositoryService.StageFilesAsync(
                fixturePath,
                initialFiles.Keys.ToArray(),
                cancellationToken);
            var commitId = await repositoryService.CommitAsync(
                fixturePath,
                "Create whole-file staging fixture",
                null,
                amend: false,
                cancellationToken: cancellationToken);
            Check(report, !string.IsNullOrWhiteSpace(commitId), "fixture committed", commitId);

            var remotes = await repositoryService.GetRemotesAsync(fixturePath, cancellationToken);
            Check(report, remotes.Count == 0, "fixture has no remotes", $"count={remotes.Count}");

            foreach (var (path, contents) in changedFiles)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(fixturePath, path),
                    contents,
                    cancellationToken);
            }

            await OpenRepositoryAsync(fixturePath);
            await WaitForLatestOperationAsync();
            Check(
                report,
                !ErrorBar.IsOpen && SamePath(repositoryRoot, fixturePath),
                "fixture opened",
                FormatWholeFileContext());
            Check(
                report,
                currentStatus?.Changes.Count == changedFiles.Count,
                "initial changed rows",
                DescribeWholeFileStatus(currentStatus));
            await CheckWholeFileWorktreeAsync(report, fixturePath, changedFiles, cancellationToken);

            await RunWholeFileSelectedMutationAsync(stage: true, path: "visible-one.txt");
            CheckWholeFileStatus(report, "selected stage visible-one", "visible-one.txt", indexed: true, workTreeChanged: false);
            CheckWholeFileStatus(report, "selected stage leaves visible-two", "visible-two.txt", indexed: false, workTreeChanged: true);
            CheckWholeFileStatus(report, "selected stage leaves hidden", "hidden.txt", indexed: false, workTreeChanged: true);
            await CheckWholeFileIndexAsync(report, fixturePath, "visible-one.txt", changedFiles["visible-one.txt"], cancellationToken);
            await CheckWholeFileWorktreeAsync(report, fixturePath, changedFiles, cancellationToken);

            await RunWholeFileSelectedMutationAsync(stage: false, path: "visible-one.txt");
            CheckWholeFileStatus(report, "selected unstage visible-one", "visible-one.txt", indexed: false, workTreeChanged: true);
            await CheckWholeFileWorktreeAsync(report, fixturePath, changedFiles, cancellationToken);

            ChangesFilterBox.Text = "visible";
            ApplyChangeFilter();
            CheckVisibleWholeFileRows(report, "filtered unstaged rows", unstagedChangeRows, ["visible-one.txt", "visible-two.txt"]);
            Check(
                report,
                !unstagedChangeRows.Any(row => row.Path.Equals("hidden.txt", StringComparison.OrdinalIgnoreCase)),
                "filtered rows hide hidden file",
                FormatWholeFileRows(unstagedChangeRows));
            await RunFileMutationAsync(stage: true, allVisible: true);
            CheckWholeFileStatus(report, "filtered stage visible-one", "visible-one.txt", indexed: true, workTreeChanged: false);
            CheckWholeFileStatus(report, "filtered stage visible-two", "visible-two.txt", indexed: true, workTreeChanged: false);
            CheckWholeFileStatus(report, "filtered stage leaves hidden", "hidden.txt", indexed: false, workTreeChanged: true);
            await CheckWholeFileIndexAsync(report, fixturePath, "visible-one.txt", changedFiles["visible-one.txt"], cancellationToken);
            await CheckWholeFileIndexAsync(report, fixturePath, "visible-two.txt", changedFiles["visible-two.txt"], cancellationToken);
            await CheckWholeFileWorktreeAsync(report, fixturePath, changedFiles, cancellationToken);

            CheckVisibleWholeFileRows(report, "filtered staged rows", stagedChangeRows, ["visible-one.txt", "visible-two.txt"]);
            await RunFileMutationAsync(stage: false, allVisible: true);
            CheckWholeFileStatus(report, "filtered unstage visible-one", "visible-one.txt", indexed: false, workTreeChanged: true);
            CheckWholeFileStatus(report, "filtered unstage visible-two", "visible-two.txt", indexed: false, workTreeChanged: true);
            CheckWholeFileStatus(report, "filtered unstage leaves hidden", "hidden.txt", indexed: false, workTreeChanged: true);
            await CheckWholeFileWorktreeAsync(report, fixturePath, changedFiles, cancellationToken);

            ChangesFilterBox.Text = string.Empty;
            ApplyChangeFilter();
            CheckVisibleWholeFileRows(report, "unfiltered unstaged rows", unstagedChangeRows, changedFiles.Keys);
            await RunFileMutationAsync(stage: true, allVisible: true);
            foreach (var path in changedFiles.Keys)
            {
                CheckWholeFileStatus(report, $"unfiltered stage {path}", path, indexed: true, workTreeChanged: false);
                await CheckWholeFileIndexAsync(report, fixturePath, path, changedFiles[path], cancellationToken);
            }

            await CheckWholeFileWorktreeAsync(report, fixturePath, changedFiles, cancellationToken);
            CheckVisibleWholeFileRows(report, "unfiltered staged rows", stagedChangeRows, changedFiles.Keys);
            await RunFileMutationAsync(stage: false, allVisible: true);
            foreach (var path in changedFiles.Keys)
            {
                CheckWholeFileStatus(report, $"unfiltered unstage {path}", path, indexed: false, workTreeChanged: true);
            }

            await CheckWholeFileWorktreeAsync(report, fixturePath, changedFiles, cancellationToken);
            Check(report, string.IsNullOrEmpty(ChangesFilterBox.Text), "final filter cleared", ChangesFilterBox.Text);
            Check(report, ErrorBar.IsOpen == false, "final error bar closed", ErrorBar.Message);
            report.AppendLine($"final-context={FormatWholeFileContext()}");
            StatusText.Text = "Whole-file staging check passed";
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

    private async Task RunWholeFileSelectedMutationAsync(bool stage, string path)
    {
        var rows = stage ? unstagedChangeRows : stagedChangeRows;
        var list = stage ? UnstagedChangesList : StagedChangesList;
        var row = rows.FirstOrDefault(candidate =>
            candidate.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            throw new InvalidOperationException($"The {(stage ? "unstaged" : "staged")} row '{path}' was not visible.");
        }

        list.SelectedItems.Clear();
        list.SelectedItems.Add(row);
        await WaitForLatestOperationAsync();
        await RunFileMutationAsync(stage);
        await WaitForLatestOperationAsync();
    }

    private async Task CheckWholeFileIndexAsync(
        StringBuilder report,
        string root,
        string path,
        string expectedContents,
        CancellationToken cancellationToken)
    {
        var change = currentStatus?.Changes.FirstOrDefault(candidate =>
            candidate.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (change is null)
        {
            Check(report, false, $"index contents {path}", DescribeWholeFileStatus(currentStatus));
            return;
        }

        var diff = await repositoryService.GetIndexDiffAsync(root, change, cancellationToken);
        var addedContents = string.Join(
            "\n",
            diff.Lines
                .Where(line => line.Kind == DiffLineKind.Added)
                .Select(line => line.Text));
        var expectedLine = expectedContents.TrimEnd('\r', '\n');
        Check(
            report,
            !diff.IsBinary && !diff.IsTruncated && string.Equals(addedContents, expectedLine, StringComparison.Ordinal),
            $"index contents {path}",
            $"added={addedContents}; expected={expectedLine}");
    }

    private static async Task CheckWholeFileWorktreeAsync(
        StringBuilder report,
        string root,
        IReadOnlyDictionary<string, string> expectedFiles,
        CancellationToken cancellationToken)
    {
        foreach (var (path, expectedContents) in expectedFiles)
        {
            var actualContents = await File.ReadAllTextAsync(
                Path.Combine(root, path),
                cancellationToken);
            Check(
                report,
                string.Equals(actualContents, expectedContents, StringComparison.Ordinal),
                $"worktree contents {path}",
                $"actual={actualContents}; expected={expectedContents}");
        }
    }

    private void CheckWholeFileStatus(
        StringBuilder report,
        string label,
        string path,
        bool indexed,
        bool workTreeChanged)
    {
        var change = currentStatus?.Changes.FirstOrDefault(candidate =>
            candidate.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        var actualIndexed = change is not null && !string.IsNullOrWhiteSpace(change.IndexStatus);
        var actualWorkTreeChanged = change is not null && !string.IsNullOrWhiteSpace(change.WorkTreeStatus);
        Check(
            report,
            actualIndexed == indexed && actualWorkTreeChanged == workTreeChanged,
            label,
            $"{path}: index={change?.IndexStatus ?? "<missing>"}; worktree={change?.WorkTreeStatus ?? "<missing>"}");
    }

    private static void CheckVisibleWholeFileRows(
        StringBuilder report,
        string label,
        IEnumerable<ChangeRow> rows,
        IEnumerable<string> expectedPaths)
    {
        var actual = rows
            .Select(row => row.Path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expected = expectedPaths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Check(
            report,
            actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase),
            label,
            $"actual={FormatWholeFileRows(rows)}; expected={string.Join(",", expected)}");
    }

    private string FormatWholeFileContext() =>
        $"root={repositoryRoot ?? "<null>"}; changes={DescribeWholeFileStatus(currentStatus)}; filter={ChangesFilterBox.Text}; workspace={currentWorkspace}";

    private static string DescribeWholeFileStatus(RepositoryStatus? status) =>
        status is null
            ? "<null>"
            : string.Join(
                ",",
                status.Changes.Select(change =>
                    $"{change.Path}:{change.IndexStatus}/{change.WorkTreeStatus}"));

    private static string FormatWholeFileRows(IEnumerable<ChangeRow> rows) =>
        string.Join(",", rows.Select(row => row.Path));
}
