using System.Text;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private const string PartialStagingCheckChildName = "partial-staging-check";
    private const string PartialStagingCheckMarkerName = ".wingit-partial-staging-check";
    private const string PartialStagingCheckTargetName = "partial-target.txt";
    private const string PartialStagingCheckUnrelatedName = "unrelated-staged.txt";
    private const string PartialStagingCheckUntrackedName = "untracked file.txt";

    private async Task RunPartialStagingCheckAsync(NativeCaptureOptions options)
    {
        var fixtureParent = Path.GetFullPath(options.RepositoryPath!);
        var fixturePath = Path.Combine(fixtureParent, PartialStagingCheckChildName);
        var checksPath = options.OutputPath + ".checks.txt";
        var report = new StringBuilder();
        var previousDiagnosticMode = diagnosticCaptureMode;
        var cancellationToken = CancellationToken.None;
        var baseline = string.Join(
            "\n",
            Enumerable.Range(1, 36).Select(index => $"line {index:D2}")) + "\n";
        var initialIndex = baseline.Replace("line 02", "line 02 staged", StringComparison.Ordinal);
        var worktree = initialIndex
            .Replace(
                "line 15\nline 16",
                "line 14b added\nline 14c added\nline 15\nline 16",
                StringComparison.Ordinal)
            .Replace("line 30", "line 30 worktree", StringComparison.Ordinal);
        const string unrelatedCommitted = "unrelated staged\n";
        const string unrelatedChanged = "unrelated staged\nunrelated added\n";
        const string untrackedContents = "untracked bytes\n";

        try
        {
            diagnosticCaptureMode = true;
            report.AppendLine("partial-staging-check");
            report.AppendLine($"settings-directory={Environment.GetEnvironmentVariable(HandlerCheckSettingsVariable)}");
            report.AppendLine($"fixture-parent={fixtureParent}");
            report.AppendLine($"fixture={fixturePath}");

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
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialStagingCheckTargetName),
                baseline,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialStagingCheckUnrelatedName),
                unrelatedCommitted,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialStagingCheckMarkerName),
                "WinGit partial staging check fixture\n",
                cancellationToken);

            await repositoryService.StageFilesAsync(
                fixturePath,
                [PartialStagingCheckTargetName, PartialStagingCheckUnrelatedName, PartialStagingCheckMarkerName],
                cancellationToken);
            var commitId = await repositoryService.CommitAsync(
                fixturePath,
                "Create partial staging fixture",
                null,
                amend: false,
                cancellationToken: cancellationToken);
            Check(report, !string.IsNullOrWhiteSpace(commitId), "fixture committed", commitId);

            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialStagingCheckTargetName),
                initialIndex,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialStagingCheckUnrelatedName),
                unrelatedChanged,
                cancellationToken);
            await repositoryService.StageFilesAsync(
                fixturePath,
                [PartialStagingCheckTargetName, PartialStagingCheckUnrelatedName],
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialStagingCheckTargetName),
                worktree,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialStagingCheckUntrackedName),
                untrackedContents,
                cancellationToken);

            await OpenRepositoryAsync(fixturePath);
            await WaitForLatestOperationAsync();
            Check(
                report,
                !ErrorBar.IsOpen && SamePath(repositoryRoot, fixturePath),
                "fixture opened",
                $"root={repositoryRoot ?? "<null>"}; error={ErrorBar.Message}");
            var unstagedTarget = unstagedChangeRows.FirstOrDefault(row =>
                row.Path.Equals(PartialStagingCheckTargetName, StringComparison.OrdinalIgnoreCase));
            var stagedTarget = stagedChangeRows.FirstOrDefault(row =>
                row.Path.Equals(PartialStagingCheckTargetName, StringComparison.OrdinalIgnoreCase));
            Check(report, unstagedTarget is not null, "unstaged target row", DescribePartialCheckStatus());
            Check(report, stagedTarget is not null, "staged target row", DescribePartialCheckStatus());
            if (unstagedTarget is null || stagedTarget is null)
            {
                throw new InvalidOperationException("The partial staging fixture did not open as MM.");
            }

            await CheckPartialCheckUnrelatedAsync(report, fixturePath, unrelatedChanged, untrackedContents, "baseline unrelated");

            await LoadSelectedChangeAsync(unstagedTarget);
            await WaitForLatestOperationAsync();
            Check(
                report,
                selectedPartialDiff?.IsSupported == true && selectedPartialDiff.Hunks.Count == 2,
                "unstaged partial diff has two hunks",
                $"hunks={selectedPartialDiff?.Hunks.Count.ToString() ?? "<null>"}; message={selectedPartialDiff?.Message ?? partialSelectionMessage ?? string.Empty}");
            var firstLine = partialDiffRows.FirstOrDefault(row =>
                row.IsSelectableLine && row.Text.Equals("line 14b added", StringComparison.Ordinal));
            Check(report, firstLine is not null, "first addition row selectable", $"rows={partialDiffRows.Count}");
            if (firstLine is null)
            {
                throw new InvalidOperationException("The first added line was not selectable.");
            }

            ApplyPartialSelection(firstLine, selected: true);
            Check(report, partialSelections.Count == 1, "one line selected", $"selections={partialSelections.Count}");
            Check(report, StagePartialButton.IsEnabled, "stage partial enabled", PartialSelectionStatusText.Text);
            await RunPartialMutationAsync(stage: true);
            await WaitForLatestOperationAsync();
            Check(report, !ErrorBar.IsOpen, "line stage error bar closed", ErrorBar.Message);
            Check(
                report,
                StatusText.Text == "Selected lines staged"
                    || StatusText.Text.StartsWith("Showing ", StringComparison.Ordinal),
                "line stage status",
                StatusText.Text);
            await CheckPartialCheckWorktreeAsync(report, fixturePath, worktree, unrelatedChanged, untrackedContents, "after line stage");
            await CheckPartialCheckStagedLinesAsync(
                report,
                fixturePath,
                ["line 02 staged", "line 14b added"],
                ["line 14c added", "line 30 worktree"],
                "line stage keeps preexisting staged change");
            await CheckPartialCheckUnrelatedAsync(report, fixturePath, unrelatedChanged, untrackedContents, "after line stage unrelated");

            var unstagedAfterLine = unstagedChangeRows.FirstOrDefault(row =>
                row.Path.Equals(PartialStagingCheckTargetName, StringComparison.OrdinalIgnoreCase));
            Check(report, unstagedAfterLine is not null, "target still unstaged after line stage", DescribePartialCheckStatus());
            if (unstagedAfterLine is null)
            {
                throw new InvalidOperationException("The target row left the unstaged list after a line stage.");
            }

            await LoadSelectedChangeAsync(unstagedAfterLine);
            await WaitForLatestOperationAsync();
            var hunk30Id = selectedPartialDiff?.Hunks
                .FirstOrDefault(hunk => hunk.Lines.Any(line => line.Text.Equals("line 30 worktree", StringComparison.Ordinal)))?.Id;
            Check(report, hunk30Id.HasValue, "line30 hunk found", $"hunks={selectedPartialDiff?.Hunks.Count.ToString() ?? "<null>"}");
            var hunk30Row = hunk30Id.HasValue
                ? partialDiffRows.FirstOrDefault(row => row.IsHunk && row.HunkId == hunk30Id.Value)
                : null;
            Check(report, hunk30Row is not null, "line30 hunk row found", $"rows={partialDiffRows.Count}");
            if (hunk30Row is null)
            {
                throw new InvalidOperationException("The line 30 hunk row was not visible.");
            }

            ApplyPartialSelection(hunk30Row, selected: true);
            await RunPartialMutationAsync(stage: true);
            await WaitForLatestOperationAsync();
            Check(report, !ErrorBar.IsOpen, "hunk stage error bar closed", ErrorBar.Message);
            await CheckPartialCheckWorktreeAsync(report, fixturePath, worktree, unrelatedChanged, untrackedContents, "after hunk stage");
            await CheckPartialCheckStagedLinesAsync(
                report,
                fixturePath,
                ["line 02 staged", "line 14b added", "line 30 worktree"],
                ["line 14c added"],
                "hunk stage adds only its hunk");

            var stagedAfterHunk = stagedChangeRows.FirstOrDefault(row =>
                row.Path.Equals(PartialStagingCheckTargetName, StringComparison.OrdinalIgnoreCase));
            Check(report, stagedAfterHunk is not null, "target staged after hunk stage", DescribePartialCheckStatus());
            if (stagedAfterHunk is null)
            {
                throw new InvalidOperationException("The target row left the staged list after a hunk stage.");
            }

            await LoadSelectedChangeAsync(stagedAfterHunk);
            await WaitForLatestOperationAsync();
            var stagedHunk30Id = selectedPartialDiff?.Hunks
                .FirstOrDefault(hunk => hunk.Lines.Any(line => line.Text.Equals("line 30 worktree", StringComparison.Ordinal)))?.Id;
            var stagedHunk30Row = stagedHunk30Id.HasValue
                ? partialDiffRows.FirstOrDefault(row => row.IsHunk && row.HunkId == stagedHunk30Id.Value)
                : null;
            Check(report, stagedHunk30Row is not null, "staged line30 hunk row found", $"rows={partialDiffRows.Count}");
            if (stagedHunk30Row is null)
            {
                throw new InvalidOperationException("The staged line 30 hunk row was not visible.");
            }

            ApplyPartialSelection(stagedHunk30Row, selected: true);
            Check(report, UnstagePartialButton.IsEnabled, "unstage partial enabled", PartialSelectionStatusText.Text);
            await RunPartialMutationAsync(stage: false);
            await WaitForLatestOperationAsync();
            Check(report, !ErrorBar.IsOpen, "hunk unstage error bar closed", ErrorBar.Message);
            Check(
                report,
                StatusText.Text == "Selected lines unstaged"
                    || StatusText.Text.StartsWith("Showing ", StringComparison.Ordinal),
                "hunk unstage status",
                StatusText.Text);
            await CheckPartialCheckWorktreeAsync(report, fixturePath, worktree, unrelatedChanged, untrackedContents, "after hunk unstage");
            await CheckPartialCheckStagedLinesAsync(
                report,
                fixturePath,
                ["line 02 staged", "line 14b added"],
                ["line 14c added", "line 30 worktree"],
                "hunk unstage retains other staged changes");
            await CheckPartialCheckUnrelatedAsync(report, fixturePath, unrelatedChanged, untrackedContents, "after hunk unstage unrelated");

            var unstagedForStale = unstagedChangeRows.FirstOrDefault(row =>
                row.Path.Equals(PartialStagingCheckTargetName, StringComparison.OrdinalIgnoreCase));
            Check(report, unstagedForStale is not null, "target unstaged before stale check", DescribePartialCheckStatus());
            if (unstagedForStale is null)
            {
                throw new InvalidOperationException("The target row left the unstaged list before the stale check.");
            }

            await LoadSelectedChangeAsync(unstagedForStale);
            await WaitForLatestOperationAsync();
            var staleLine = partialDiffRows.FirstOrDefault(row =>
                row.IsSelectableLine && row.Text.Equals("line 14c added", StringComparison.Ordinal));
            Check(report, staleLine is not null, "stale candidate row selectable", $"rows={partialDiffRows.Count}");
            if (staleLine is null)
            {
                throw new InvalidOperationException("The stale candidate line was not selectable.");
            }

            ApplyPartialSelection(staleLine, selected: true);
            var staleWorktree = worktree.Replace(
                "line 30 worktree",
                "line 30 stale edit",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialStagingCheckTargetName),
                staleWorktree,
                cancellationToken);
            var stagedBeforeStale = await ReadPartialCheckStagedLinesAsync(fixturePath, cancellationToken);
            await RunPartialMutationAsync(stage: true);
            await WaitForLatestOperationAsync();
            Check(report, ErrorBar.IsOpen, "stale stage opens error bar", ErrorBar.Message);
            Check(report, ErrorBar.Title == "Refresh required", "stale stage title", ErrorBar.Title);
            var stagedAfterStale = await ReadPartialCheckStagedLinesAsync(fixturePath, cancellationToken);
            Check(
                report,
                stagedBeforeStale.SequenceEqual(stagedAfterStale, StringComparer.Ordinal),
                "stale stage preserves index",
                $"before={string.Join("|", stagedBeforeStale)}; after={string.Join("|", stagedAfterStale)}");
            await CheckPartialCheckWorktreeAsync(report, fixturePath, staleWorktree, unrelatedChanged, untrackedContents, "stale stage preserves worktree");
            await CheckPartialCheckUnrelatedAsync(report, fixturePath, unrelatedChanged, untrackedContents, "stale stage unrelated");

            StatusText.Text = "Partial staging check passed";
            report.AppendLine($"final-context=root={repositoryRoot ?? "<null>"}; changes={DescribePartialCheckStatus()}");
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

    private async Task<IReadOnlyList<string>> ReadPartialCheckStagedLinesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var stagedRow = stagedChangeRows.FirstOrDefault(row =>
            row.Path.Equals(PartialStagingCheckTargetName, StringComparison.OrdinalIgnoreCase));
        if (stagedRow is null)
        {
            return [];
        }

        var diff = await repositoryService.GetIndexDiffAsync(root, stagedRow.Change, cancellationToken);
        return diff.Lines
            .Where(line => line.Kind == DiffLineKind.Added)
            .Select(line => line.Text)
            .ToArray();
    }

    private async Task CheckPartialCheckStagedLinesAsync(
        StringBuilder report,
        string root,
        IReadOnlyList<string> expectedAdded,
        IReadOnlyList<string> unexpectedAdded,
        string label)
    {
        var added = await ReadPartialCheckStagedLinesAsync(root, CancellationToken.None);
        Check(
            report,
            expectedAdded.All(expected => added.Contains(expected, StringComparer.Ordinal))
                && unexpectedAdded.All(unexpected => !added.Contains(unexpected, StringComparer.Ordinal)),
            label,
            $"added={string.Join("|", added)}");
    }

    private async Task CheckPartialCheckWorktreeAsync(
        StringBuilder report,
        string root,
        string expectedTarget,
        string expectedUnrelated,
        string expectedUntracked,
        string label)
    {
        var actualTarget = await File.ReadAllTextAsync(
            Path.Combine(root, PartialStagingCheckTargetName),
            CancellationToken.None);
        var actualUnrelated = await File.ReadAllTextAsync(
            Path.Combine(root, PartialStagingCheckUnrelatedName),
            CancellationToken.None);
        var actualUntracked = await File.ReadAllTextAsync(
            Path.Combine(root, PartialStagingCheckUntrackedName),
            CancellationToken.None);
        Check(
            report,
            string.Equals(actualTarget, expectedTarget, StringComparison.Ordinal)
                && string.Equals(actualUnrelated, expectedUnrelated, StringComparison.Ordinal)
                && string.Equals(actualUntracked, expectedUntracked, StringComparison.Ordinal),
            label,
            $"target={actualTarget.Length} chars; unrelated={actualUnrelated.Length} chars; untracked={actualUntracked.Length} chars");
    }

    private async Task CheckPartialCheckUnrelatedAsync(
        StringBuilder report,
        string root,
        string expectedUnrelatedWorktree,
        string expectedUntracked,
        string label)
    {
        var unrelatedRow = stagedChangeRows.FirstOrDefault(row =>
            row.Path.Equals(PartialStagingCheckUnrelatedName, StringComparison.OrdinalIgnoreCase));
        var added = unrelatedRow is null
            ? []
            : (await repositoryService.GetIndexDiffAsync(root, unrelatedRow.Change, CancellationToken.None)).Lines
                .Where(line => line.Kind == DiffLineKind.Added)
                .Select(line => line.Text)
                .ToArray();
        var actualUnrelated = await File.ReadAllTextAsync(
            Path.Combine(root, PartialStagingCheckUnrelatedName),
            CancellationToken.None);
        var actualUntracked = await File.ReadAllTextAsync(
            Path.Combine(root, PartialStagingCheckUntrackedName),
            CancellationToken.None);
        Check(
            report,
            added.SequenceEqual(["unrelated added"], StringComparer.Ordinal)
                && string.Equals(actualUnrelated, expectedUnrelatedWorktree, StringComparison.Ordinal)
                && string.Equals(actualUntracked, expectedUntracked, StringComparison.Ordinal),
            label,
            $"index-added={string.Join("|", added)}; unrelated={actualUnrelated.Length} chars");
    }

    private string DescribePartialCheckStatus() =>
        currentStatus is null
            ? "<null>"
            : string.Join(
                ",",
                currentStatus.Changes.Select(change =>
                    $"{change.Path}:{change.IndexStatus}/{change.WorkTreeStatus}"));
}
