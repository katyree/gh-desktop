using System.Text;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private const string PartialDiscardCheckChildName = "partial-discard-check";
    private const string PartialDiscardCheckMarkerName = ".wingit-partial-discard-check";
    private const string PartialDiscardCheckTargetName = "partial-discard-target.txt";
    private const string PartialDiscardCheckUnrelatedName = "unrelated-staged.txt";
    private const string PartialDiscardCheckUntrackedName = "untracked file.txt";
    private const string PartialDiscardCheckCrlfName = "crlf-target.txt";
    private const string PartialDiscardCheckNoNewlineName = "no-newline-target.txt";
    private const string PartialDiscardCheckBinaryName = "binary-target.bin";

    private async Task RunPartialDiscardCheckAsync(NativeCaptureOptions options)
    {
        var fixtureParent = Path.GetFullPath(options.RepositoryPath!);
        var fixturePath = Path.Combine(fixtureParent, PartialDiscardCheckChildName);
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
                "line 12\nline 13\nline 14\nline 15\nline 16",
                "line 13\nline 14\nline 14b added\nline 14c added\nline 15\nline 16",
                StringComparison.Ordinal)
            .Replace("line 30", "line 30 keep", StringComparison.Ordinal);
        const string unrelatedCommitted = "unrelated staged\n";
        const string unrelatedChanged = "unrelated staged\nunrelated added\n";
        const string untrackedContents = "untracked bytes\n";
        var crlfBaseline = "crlf one\r\ncrlf two\r\ncrlf three\r\n";
        var crlfWorktree = "crlf one\r\ncrlf two edited\r\ncrlf three\r\n";
        const string noNewlineBaseline = "old";
        const string noNewlineWorktree = "new";

        try
        {
            diagnosticCaptureMode = true;
            report.AppendLine("partial-discard-check");
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
                Path.Combine(fixturePath, PartialDiscardCheckTargetName),
                baseline,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialDiscardCheckUnrelatedName),
                unrelatedCommitted,
                cancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(fixturePath, PartialDiscardCheckCrlfName),
                Encoding.UTF8.GetBytes(crlfBaseline),
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialDiscardCheckNoNewlineName),
                noNewlineBaseline,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(fixturePath, PartialDiscardCheckBinaryName),
                [0x00, 0x01, 0x02, 0x03],
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialDiscardCheckMarkerName),
                "WinGit partial discard check fixture\n",
                cancellationToken);

            await repositoryService.StageFilesAsync(
                fixturePath,
                [PartialDiscardCheckTargetName, PartialDiscardCheckUnrelatedName, PartialDiscardCheckCrlfName, PartialDiscardCheckNoNewlineName, PartialDiscardCheckBinaryName, PartialDiscardCheckMarkerName],
                cancellationToken);
            var commitId = await repositoryService.CommitAsync(
                fixturePath,
                "Create partial discard fixture",
                null,
                amend: false,
                cancellationToken: cancellationToken);
            Check(report, !string.IsNullOrWhiteSpace(commitId), "fixture committed", commitId);

            // A preexisting staged change the discard path must preserve.
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialDiscardCheckTargetName),
                initialIndex,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialDiscardCheckUnrelatedName),
                unrelatedChanged,
                cancellationToken);
            await repositoryService.StageFilesAsync(
                fixturePath,
                [PartialDiscardCheckTargetName, PartialDiscardCheckUnrelatedName],
                cancellationToken);

            // Unstaged worktree edits: two hunks in the target, plus CRLF,
            // no-newline, and binary edits. The unrelated index stays clean.
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialDiscardCheckTargetName),
                worktree,
                cancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(fixturePath, PartialDiscardCheckCrlfName),
                Encoding.UTF8.GetBytes(crlfWorktree),
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialDiscardCheckNoNewlineName),
                noNewlineWorktree,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(fixturePath, PartialDiscardCheckBinaryName),
                [0x00, 0x01, 0x02, 0x04],
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialDiscardCheckUntrackedName),
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
                row.Path.Equals(PartialDiscardCheckTargetName, StringComparison.OrdinalIgnoreCase));
            var stagedTarget = stagedChangeRows.FirstOrDefault(row =>
                row.Path.Equals(PartialDiscardCheckTargetName, StringComparison.OrdinalIgnoreCase));
            Check(report, unstagedTarget is not null, "unstaged target row", DescribePartialDiscardStatus());
            Check(report, stagedTarget is not null, "staged target row", DescribePartialDiscardStatus());
            if (unstagedTarget is null || stagedTarget is null)
            {
                throw new InvalidOperationException("The partial discard fixture did not open as MM.");
            }

            await CheckPartialDiscardUnrelatedAsync(report, fixturePath, unrelatedChanged, untrackedContents, "baseline unrelated");

            // Selected-line discard with explicit confirmation scope.
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
            var confirmation = BuildPartialDiscardConfirmationContent(unstagedTarget, lineCount: 1, hunkCount: 0);
            Check(
                report,
                PartialDiscardConfirmationMentions(confirmation, PartialDiscardCheckTargetName, "1 selected changed line"),
                "confirmation identifies file and line scope",
                DescribePartialDiscardConfirmation(confirmation));

            // Cancel leaves everything untouched.
            var worktreeBeforeCancel = await File.ReadAllTextAsync(
                Path.Combine(fixturePath, PartialDiscardCheckTargetName),
                CancellationToken.None);
            var stagedBeforeCancel = await ReadPartialDiscardStagedLinesAsync(fixturePath, PartialDiscardCheckTargetName, CancellationToken.None);
            Check(
                report,
                string.Equals(worktreeBeforeCancel, worktree, StringComparison.Ordinal)
                    && stagedBeforeCancel.Contains("line 02 staged", StringComparer.Ordinal),
                "cancel preserves worktree and index",
                $"worktree={worktreeBeforeCancel.Length} chars; staged={string.Join("|", stagedBeforeCancel)}");

            await RunPartialDiscardCheckMutationAsync(fixturePath, expectStale: false, cancellationToken);
            await WaitForLatestOperationAsync();
            Check(report, !ErrorBar.IsOpen, "line discard error bar closed", ErrorBar.Message);
            var expectedAfterLine = initialIndex.Replace("line 12\n", string.Empty, StringComparison.Ordinal)
                .Replace(
                    "line 14\nline 15",
                    "line 14\nline 14c added\nline 15",
                    StringComparison.Ordinal)
                .Replace("line 30", "line 30 keep", StringComparison.Ordinal);
            await CheckPartialDiscardWorktreeAsync(report, fixturePath, expectedAfterLine, "after line discard target");
            await CheckPartialDiscardStagedLinesAsync(
                report,
                fixturePath,
                PartialDiscardCheckTargetName,
                ["line 02 staged"],
                ["line 14b added", "line 14c added", "line 30 keep"],
                "line discard keeps preexisting staged change");
            await CheckPartialDiscardUnrelatedAsync(report, fixturePath, unrelatedChanged, untrackedContents, "after line discard unrelated");

            // Hunk discard reverses the remaining first hunk, including its deletion.
            var unstagedAfterLine = unstagedChangeRows.FirstOrDefault(row =>
                row.Path.Equals(PartialDiscardCheckTargetName, StringComparison.OrdinalIgnoreCase));
            Check(report, unstagedAfterLine is not null, "target still unstaged after line discard", DescribePartialDiscardStatus());
            if (unstagedAfterLine is null)
            {
                throw new InvalidOperationException("The target row left the unstaged list after a line discard.");
            }

            await LoadSelectedChangeAsync(unstagedAfterLine);
            await WaitForLatestOperationAsync();
            var firstHunkId = selectedPartialDiff?.Hunks
                .FirstOrDefault(hunk => hunk.Lines.Any(line => line.Text.Equals("line 14c added", StringComparison.Ordinal)))?.Id;
            Check(report, firstHunkId.HasValue, "remaining first hunk found", $"hunks={selectedPartialDiff?.Hunks.Count.ToString() ?? "<null>"}");
            var firstHunkRow = firstHunkId.HasValue
                ? partialDiffRows.FirstOrDefault(row => row.IsHunk && row.HunkId == firstHunkId.Value)
                : null;
            Check(report, firstHunkRow is not null, "remaining first hunk row found", $"rows={partialDiffRows.Count}");
            if (firstHunkRow is null)
            {
                throw new InvalidOperationException("The remaining first hunk row was not visible.");
            }

            ApplyPartialSelection(firstHunkRow, selected: true);
            var hunkConfirmation = BuildPartialDiscardConfirmationContent(unstagedAfterLine, lineCount: 2, hunkCount: 1);
            Check(
                report,
                PartialDiscardConfirmationMentions(hunkConfirmation, PartialDiscardCheckTargetName, "1 selected hunk"),
                "hunk confirmation identifies file and hunk scope",
                DescribePartialDiscardConfirmation(hunkConfirmation));
            await RunPartialDiscardCheckMutationAsync(fixturePath, expectStale: false, cancellationToken);
            await WaitForLatestOperationAsync();
            Check(report, !ErrorBar.IsOpen, "hunk discard error bar closed", ErrorBar.Message);
            var expectedAfterHunk = initialIndex.Replace("line 30", "line 30 keep", StringComparison.Ordinal);
            await CheckPartialDiscardWorktreeAsync(report, fixturePath, expectedAfterHunk, "after hunk discard target");
            await CheckPartialDiscardStagedLinesAsync(
                report,
                fixturePath,
                PartialDiscardCheckTargetName,
                ["line 02 staged"],
                ["line 14c added", "line 30 keep"],
                "hunk discard restores deletion and preserves index");
            await CheckPartialDiscardUnrelatedAsync(report, fixturePath, unrelatedChanged, untrackedContents, "after hunk discard unrelated");

            // Stale selections are rejected before any write.
            var unstagedForStale = unstagedChangeRows.FirstOrDefault(row =>
                row.Path.Equals(PartialDiscardCheckTargetName, StringComparison.OrdinalIgnoreCase));
            Check(report, unstagedForStale is not null, "target unstaged before stale check", DescribePartialDiscardStatus());
            if (unstagedForStale is null)
            {
                throw new InvalidOperationException("The target row left the unstaged list before the stale check.");
            }

            await LoadSelectedChangeAsync(unstagedForStale);
            await WaitForLatestOperationAsync();
            var staleLine = partialDiffRows.FirstOrDefault(row =>
                row.IsSelectableLine && row.Text.Equals("line 30 keep", StringComparison.Ordinal));
            Check(report, staleLine is not null, "stale candidate row selectable", $"rows={partialDiffRows.Count}");
            if (staleLine is null)
            {
                throw new InvalidOperationException("The stale candidate line was not selectable.");
            }

            ApplyPartialSelection(staleLine, selected: true);
            var staleWorktree = expectedAfterHunk.Replace(
                "line 30 keep",
                "line 30 stale edit",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialDiscardCheckTargetName),
                staleWorktree,
                cancellationToken);
            var stagedBeforeStale = await ReadPartialDiscardStagedLinesAsync(fixturePath, PartialDiscardCheckTargetName, CancellationToken.None);
            await RunPartialDiscardCheckMutationAsync(fixturePath, expectStale: true, cancellationToken);
            await WaitForLatestOperationAsync();
            Check(report, ErrorBar.IsOpen, "stale discard opens error bar", ErrorBar.Message);
            Check(report, ErrorBar.Title == "Partial discard stopped; refresh required", "stale discard title", ErrorBar.Title);
            var stagedAfterStale = await ReadPartialDiscardStagedLinesAsync(fixturePath, PartialDiscardCheckTargetName, CancellationToken.None);
            Check(
                report,
                stagedBeforeStale.SequenceEqual(stagedAfterStale, StringComparer.Ordinal),
                "stale discard preserves index",
                $"before={string.Join("|", stagedBeforeStale)}; after={string.Join("|", stagedAfterStale)}");
            await CheckPartialDiscardWorktreeAsync(report, fixturePath, staleWorktree, "stale discard preserves worktree");
            await CheckPartialDiscardUnrelatedAsync(report, fixturePath, unrelatedChanged, untrackedContents, "stale discard unrelated");
            ErrorBar.IsOpen = false;
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, PartialDiscardCheckTargetName),
                expectedAfterHunk,
                cancellationToken);
            await RefreshAfterMutationAsync(fixturePath);
            await WaitForLatestOperationAsync();

            // CRLF line endings round-trip exactly.
            var crlfRow = unstagedChangeRows.FirstOrDefault(row =>
                row.Path.Equals(PartialDiscardCheckCrlfName, StringComparison.OrdinalIgnoreCase));
            Check(report, crlfRow is not null, "crlf row unstaged", DescribePartialDiscardStatus());
            if (crlfRow is not null)
            {
                await LoadSelectedChangeAsync(crlfRow);
                await WaitForLatestOperationAsync();
                Check(report, selectedPartialDiff?.IsSupported == true, "crlf diff supported", selectedPartialDiff?.Message ?? string.Empty);
                // A modified line is a deletion plus an addition. Discarding
                // only one side would leave a partial line set, so the CRLF
                // round-trip discards the whole hunk back to the baseline.
                var crlfHunkRow = selectedPartialDiff?.Hunks.Count > 0
                    ? partialDiffRows.FirstOrDefault(row => row.IsHunk && row.HunkId == selectedPartialDiff.Hunks[0].Id)
                    : null;
                Check(report, crlfHunkRow is not null, "crlf hunk selectable", $"rows={partialDiffRows.Count}");
                if (crlfHunkRow is not null)
                {
                    ApplyPartialSelection(crlfHunkRow, selected: true);
                    await RunPartialDiscardCheckMutationAsync(fixturePath, expectStale: false, cancellationToken);
                    await WaitForLatestOperationAsync();
                    Check(report, !ErrorBar.IsOpen, "crlf discard error bar closed", ErrorBar.Message);
                    var crlfBytes = await File.ReadAllBytesAsync(
                        Path.Combine(fixturePath, PartialDiscardCheckCrlfName),
                        CancellationToken.None);
                    var crlfExpected = Encoding.UTF8.GetBytes(crlfBaseline);
                    Check(
                        report,
                        Convert.ToHexString(crlfBytes) == Convert.ToHexString(crlfExpected),
                        "crlf discard preserves line endings",
                        $"bytes={Convert.ToHexString(crlfBytes)}");
                }
            }

            // Final-newline behavior round-trips without adding a newline.
            var noNewlineRow = unstagedChangeRows.FirstOrDefault(row =>
                row.Path.Equals(PartialDiscardCheckNoNewlineName, StringComparison.OrdinalIgnoreCase));
            Check(report, noNewlineRow is not null, "no-newline row unstaged", DescribePartialDiscardStatus());
            if (noNewlineRow is not null)
            {
                await LoadSelectedChangeAsync(noNewlineRow);
                await WaitForLatestOperationAsync();
                Check(report, selectedPartialDiff?.IsSupported == true, "no-newline diff supported", selectedPartialDiff?.Message ?? string.Empty);
                // Same modified-line pairing as the CRLF case: discard the
                // whole hunk so the file returns to the baseline without a
                // trailing newline.
                var noNewlineHunkRow = selectedPartialDiff?.Hunks.Count > 0
                    ? partialDiffRows.FirstOrDefault(row => row.IsHunk && row.HunkId == selectedPartialDiff.Hunks[0].Id)
                    : null;
                Check(report, noNewlineHunkRow is not null, "no-newline hunk selectable", $"rows={partialDiffRows.Count}");
                if (noNewlineHunkRow is not null)
                {
                    ApplyPartialSelection(noNewlineHunkRow, selected: true);
                    await RunPartialDiscardCheckMutationAsync(fixturePath, expectStale: false, cancellationToken);
                    await WaitForLatestOperationAsync();
                    Check(report, !ErrorBar.IsOpen, "no-newline discard error bar closed", ErrorBar.Message);
                    var noNewlineBytes = await File.ReadAllBytesAsync(
                        Path.Combine(fixturePath, PartialDiscardCheckNoNewlineName),
                        CancellationToken.None);
                    var noNewlineExpected = Encoding.UTF8.GetBytes(noNewlineBaseline);
                    Check(
                        report,
                        Convert.ToHexString(noNewlineBytes) == Convert.ToHexString(noNewlineExpected),
                        "no-newline discard preserves final newline",
                        $"bytes={Convert.ToHexString(noNewlineBytes)}");
                }
            }

            // Binary selections stay unavailable and never fall back to whole-file discard.
            var binaryRow = unstagedChangeRows.FirstOrDefault(row =>
                row.Path.Equals(PartialDiscardCheckBinaryName, StringComparison.OrdinalIgnoreCase));
            Check(report, binaryRow is not null, "binary row unstaged", DescribePartialDiscardStatus());
            if (binaryRow is not null)
            {
                await LoadSelectedChangeAsync(binaryRow);
                await WaitForLatestOperationAsync();
                var binaryStatus = PartialSelectionStatusText.Text;
                var binaryUnavailable = selectedPartialDiff?.IsSupported == false
                    || binaryStatus.StartsWith("Line selection unavailable", StringComparison.Ordinal);
                Check(
                    report,
                    binaryUnavailable,
                    "binary selection unavailable",
                    $"supported={selectedPartialDiff?.IsSupported.ToString() ?? "<null>"}; message={selectedPartialDiff?.Message ?? partialSelectionMessage ?? string.Empty}; status={binaryStatus}");
                var binaryBytes = await File.ReadAllBytesAsync(
                    Path.Combine(fixturePath, PartialDiscardCheckBinaryName),
                    CancellationToken.None);
                Check(
                    report,
                    Convert.ToHexString(binaryBytes) == Convert.ToHexString([0x00, 0x01, 0x02, 0x04]),
                    "binary worktree preserved without fallback",
                    $"bytes={Convert.ToHexString(binaryBytes)}");
            }

            await CheckPartialDiscardUnrelatedAsync(report, fixturePath, unrelatedChanged, untrackedContents, "final unrelated");
            StatusText.Text = "Partial discard check passed";
            report.AppendLine($"final-context=root={repositoryRoot ?? "<null>"}; changes={DescribePartialDiscardStatus()}");
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

    private async Task RunPartialDiscardCheckMutationAsync(string root, bool expectStale, CancellationToken cancellationToken)
    {
        if (repositoryRoot is null
            || selectedPartialDiff?.IsSupported != true
            || partialSelections.Count == 0
            || selectedChange is null
            || selectedChangeIsStaged)
        {
            throw new InvalidOperationException("The partial discard check has no supported selection to apply.");
        }

        var diff = selectedPartialDiff;
        var selection = partialSelections.ToArray();
        mutationInProgress = true;
        ErrorBar.IsOpen = false;
        try
        {
            await repositoryService.DiscardSelectedChangesAsync(
                root,
                diff,
                selection,
                cancellationToken);
            if (expectStale)
            {
                throw new InvalidOperationException("A stale partial discard unexpectedly succeeded.");
            }

            StatusText.Text = "Selected lines discarded; staged content was unchanged.";
        }
        catch (StaleDiffSnapshotException exception)
        {
            if (!expectStale)
            {
                throw;
            }

            ShowError("Partial discard stopped; refresh required", exception);
        }
        finally
        {
            try
            {
                await RefreshAfterMutationAsync(root);
            }
            catch (Exception refreshException)
            {
                ShowError("Unable to refresh repository after partial discard", refreshException);
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
        }
    }

    private async Task<IReadOnlyList<string>> ReadPartialDiscardStagedLinesAsync(
        string root,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var stagedRow = stagedChangeRows.FirstOrDefault(row =>
            row.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
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

    private async Task CheckPartialDiscardStagedLinesAsync(
        StringBuilder report,
        string root,
        string relativePath,
        IReadOnlyList<string> expectedAdded,
        IReadOnlyList<string> unexpectedAdded,
        string label)
    {
        var added = await ReadPartialDiscardStagedLinesAsync(root, relativePath, CancellationToken.None);
        Check(
            report,
            expectedAdded.All(expected => added.Contains(expected, StringComparer.Ordinal))
                && unexpectedAdded.All(unexpected => !added.Contains(unexpected, StringComparer.Ordinal)),
            label,
            $"added={string.Join("|", added)}");
    }

    private async Task CheckPartialDiscardWorktreeAsync(
        StringBuilder report,
        string root,
        string expectedTarget,
        string label)
    {
        var actualTarget = await File.ReadAllTextAsync(
            Path.Combine(root, PartialDiscardCheckTargetName),
            CancellationToken.None);
        // Git applies worktree writes through the repository's text conversion,
        // so a discard may check out CRLF while the selected lines are exactly
        // the requested ones. Normalize for the line comparison; the CRLF
        // fixture below asserts exact bytes separately. Electron shells to the
        // same git-apply worktree path.
        actualTarget = actualTarget.Replace("\r\n", "\n", StringComparison.Ordinal);
        Check(
            report,
            string.Equals(actualTarget, expectedTarget, StringComparison.Ordinal),
            label,
            $"target={actualTarget.Length} chars");
    }

    private async Task CheckPartialDiscardUnrelatedAsync(
        StringBuilder report,
        string root,
        string expectedUnrelatedWorktree,
        string expectedUntracked,
        string label)
    {
        var unrelatedRow = stagedChangeRows.FirstOrDefault(row =>
            row.Path.Equals(PartialDiscardCheckUnrelatedName, StringComparison.OrdinalIgnoreCase));
        var added = unrelatedRow is null
            ? []
            : (await repositoryService.GetIndexDiffAsync(root, unrelatedRow.Change, CancellationToken.None)).Lines
                .Where(line => line.Kind == DiffLineKind.Added)
                .Select(line => line.Text)
                .ToArray();
        var actualUnrelated = await File.ReadAllTextAsync(
            Path.Combine(root, PartialDiscardCheckUnrelatedName),
            CancellationToken.None);
        var actualUntracked = await File.ReadAllTextAsync(
            Path.Combine(root, PartialDiscardCheckUntrackedName),
            CancellationToken.None);
        Check(
            report,
            added.SequenceEqual(["unrelated added"], StringComparer.Ordinal)
                && string.Equals(actualUnrelated, expectedUnrelatedWorktree, StringComparison.Ordinal)
                && string.Equals(actualUntracked, expectedUntracked, StringComparison.Ordinal),
            label,
            $"index-added={string.Join("|", added)}; unrelated={actualUnrelated.Length} chars");
    }

    private static bool PartialDiscardConfirmationMentions(StackPanel content, string path, string scope)
    {
        var text = DescribePartialDiscardConfirmation(content);
        return text.Contains(path, StringComparison.Ordinal)
            && text.Contains(scope, StringComparison.Ordinal);
    }

    private static string DescribePartialDiscardConfirmation(StackPanel content)
    {
        var builder = new StringBuilder();
        foreach (var child in content.Children)
        {
            if (child is TextBlock block)
            {
                builder.Append(block.Text).Append(' ');
            }
            else if (child is ScrollViewer viewer && viewer.Content is StackPanel nested)
            {
                builder.Append(DescribePartialDiscardConfirmation(nested)).Append(' ');
            }
        }

        return builder.ToString();
    }

    private string DescribePartialDiscardStatus() =>
        currentStatus is null
            ? "<null>"
            : string.Join(
                ",",
                currentStatus.Changes.Select(change =>
                    $"{change.Path}:{change.IndexStatus}/{change.WorkTreeStatus}"));
}
