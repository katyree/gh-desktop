using System.Text;
using Microsoft.UI.Xaml;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private async Task RunHistorySelectionCheckAsync(NativeCaptureOptions options)
    {
        var fixtureParent = options.RepositoryPath!;
        var repositoryPath = Path.Combine(fixtureParent, "history-a");
        var switchedRepositoryPath = Path.Combine(fixtureParent, "history-b");
        var checksPath = options.OutputPath + ".checks.txt";
        var report = new StringBuilder();
        var previousDiagnosticMode = diagnosticCaptureMode;

        try
        {
            diagnosticCaptureMode = false;
            report.AppendLine("history-selection-check");
            report.AppendLine($"settings-directory={Environment.GetEnvironmentVariable(HandlerCheckSettingsVariable)}");
            report.AppendLine($"fixture-parent={fixtureParent}");
            report.AppendLine($"repository={repositoryPath}");
            report.AppendLine($"switched-repository={switchedRepositoryPath}");

            await OpenRepositoryAsync(repositoryPath);
            await WaitForLatestOperationAsync();
            Check(report, SamePath(repositoryRoot, repositoryPath), "history fixture opened", FormatHistorySelectionContext());

            ShowWorkspace("history");
            if (!historyComparisonBranchesLoaded)
            {
                latestOperationTask = LoadHistoryComparisonBranchesAsync();
                await latestOperationTask;
            }
            latestOperationTask = LoadHistoryAsync();
            await WaitForLatestOperationAsync();
            MainNavigation.SelectedItem = MainNavigation.MenuItems[1];
            await WaitForLatestOperationAsync();
            Check(report, commitRows.Count == 3, "history rows loaded", FormatHistorySelectionContext());

            var historyEmpty = commitRows.FirstOrDefault(row =>
                string.Equals(row.Summary, "history-a empty", StringComparison.Ordinal));
            var historySecond = commitRows.FirstOrDefault(row =>
                string.Equals(row.Summary, "history-a second", StringComparison.Ordinal));
            var historyFirst = commitRows.FirstOrDefault(row =>
                string.Equals(row.Summary, "history-a first", StringComparison.Ordinal));
            Check(report, historyEmpty is not null, "history empty row available", FormatHistorySelectionContext());
            Check(report, historySecond is not null, "history second row available", FormatHistorySelectionContext());
            Check(report, historyFirst is not null, "history first row available", FormatHistorySelectionContext());
            Check(
                report,
                selectedCommit is not null
                    && string.Equals(selectedCommit.Summary, "history-a empty", StringComparison.Ordinal)
                    && commitFileRows.Count == 0
                    && historyDiffRows.Count == 0
                    && string.Equals(HistoryDiffMessageTitle.Text, "No files in this commit", StringComparison.Ordinal),
                "empty commit has no files or diff",
                FormatHistorySelectionContext());

            HistoryList.SelectedItem = historySecond;
            var secondSelectionTask = latestOperationTask
                ?? throw new InvalidOperationException("Selecting the second history row did not start a read.");
            Check(
                report,
                !secondSelectionTask.IsCompleted && HistoryList.IsEnabled,
                "history list enabled while commit files are pending",
                FormatHistorySelectionContext());
            HistoryList.SelectedItem = historyFirst;
            var firstSelectionTask = latestOperationTask
                ?? throw new InvalidOperationException("Selecting the first history row did not start a read.");
            Check(
                report,
                !secondSelectionTask.IsCompleted && !ReferenceEquals(secondSelectionTask, firstSelectionTask),
                "rapid history selection starts distinct reads",
                FormatHistorySelectionContext());
            await Task.WhenAll(secondSelectionTask, firstSelectionTask);
            await WaitForLatestOperationAsync();
            Check(report, ReferenceEquals(selectedCommit, historyFirst), "latest history selection retained", FormatHistorySelectionContext());
            Check(report, HistoryCommitText.Text == "history-a first", "latest selection metadata retained", FormatHistorySelectionContext());
            Check(report, commitFileRows.Count > 0, "latest selection files loaded", FormatHistorySelectionContext());
            Check(
                report,
                historyDiffRows.Any(row => row.Text.Contains("history-a first", StringComparison.Ordinal))
                    && !historyDiffRows.Any(row => row.Text.Contains("history-a second", StringComparison.Ordinal)),
                "latest selection diff retained",
                FormatHistorySelectionContext());

            HistoryFilesList.SelectedIndex = -1;
            HistoryFilesList.SelectedIndex = 0;
            var pendingDiffClearTask = latestOperationTask
                ?? throw new InvalidOperationException("Selecting the history file did not start a diff read.");
            Check(report, !pendingDiffClearTask.IsCompleted, "clear diff read pending", FormatHistorySelectionContext());
            Check(report, HistoryList.IsEnabled, "history list enabled while diff is pending", FormatHistorySelectionContext());
            HistoryFilesList.SelectedIndex = -1;
            await WaitForLatestOperationAsync();
            Check(
                report,
                ReferenceEquals(selectedCommit, historyFirst)
                    && selectedCommitFile is null
                    && historyDiffRows.Count == 0
                    && string.Equals(HistoryDiffMessageTitle.Text, "Select a file", StringComparison.Ordinal)
                    && !ErrorBar.IsOpen,
                "cleared file keeps diff panel empty",
                FormatHistorySelectionContext());

            HistoryList.SelectedItem = historySecond;
            var hiddenHistoryRead = latestOperationTask
                ?? throw new InvalidOperationException("Selecting the second history row did not start a hidden-view read.");
            Check(report, !hiddenHistoryRead.IsCompleted, "hidden-view history read pending", FormatHistorySelectionContext());
            ShowWorkspace("changes");
            const string hiddenViewStatus = "history selection hidden-view sentinel";
            ErrorBar.IsOpen = false;
            StatusText.Text = hiddenViewStatus;
            await WaitForLatestOperationAsync();
            Check(
                report,
                currentWorkspace == "changes"
                    && string.Equals(StatusText.Text, hiddenViewStatus, StringComparison.Ordinal)
                    && !ErrorBar.IsOpen,
                "hidden history read does not replace current workspace",
                FormatHistorySelectionContext());

            ShowWorkspace("history");
            if (!historyComparisonBranchesLoaded)
            {
                latestOperationTask = LoadHistoryComparisonBranchesAsync();
                await latestOperationTask;
            }
            latestOperationTask = LoadHistoryAsync();
            await WaitForLatestOperationAsync();
            MainNavigation.SelectedItem = MainNavigation.MenuItems[1];
            await WaitForLatestOperationAsync();
            historyEmpty = commitRows.FirstOrDefault(row =>
                string.Equals(row.Summary, "history-a empty", StringComparison.Ordinal));
            historySecond = commitRows.FirstOrDefault(row =>
                string.Equals(row.Summary, "history-a second", StringComparison.Ordinal));
            Check(report, historyEmpty is not null, "reloaded history empty row available", FormatHistorySelectionContext());
            Check(report, historySecond is not null, "reloaded history second row available", FormatHistorySelectionContext());

            HistoryList.SelectedItem = historySecond;
            var clearSelectionTask = latestOperationTask
                ?? throw new InvalidOperationException("Selecting the second history row did not start a clear test read.");
            Check(report, !clearSelectionTask.IsCompleted, "clear test read pending", FormatHistorySelectionContext());
            HistoryList.SelectedItems.Clear();
            HistoryList.SelectedIndex = -1;
            await WaitForLatestOperationAsync();
            Check(
                report,
                selectedCommit is null
                    && selectedCommitFile is null
                    && commitFileRows.Count == 0
                    && historyDiffRows.Count == 0
                    && string.Equals(HistoryDiffMessageTitle.Text, "Select a commit", StringComparison.Ordinal)
                    && !ErrorBar.IsOpen,
                "cleared selection keeps panel empty",
                FormatHistorySelectionContext());

            HistoryList.SelectedItem = historyEmpty;
            await WaitForLatestOperationAsync();
            Check(
                report,
                selectedCommit is not null
                    && string.Equals(selectedCommit.Summary, "history-a empty", StringComparison.Ordinal)
                    && commitFileRows.Count == 0
                    && historyDiffRows.Count == 0
                    && string.Equals(HistoryDiffMessageTitle.Text, "No files in this commit", StringComparison.Ordinal),
                "empty selection remains fileless",
                FormatHistorySelectionContext());

            var refreshTask = RefreshRepositoryAsync();
            Check(
                report,
                !refreshTask.IsCompleted && !HistoryList.IsEnabled,
                "history list disabled while repository refresh is pending",
                FormatHistorySelectionContext());
            await refreshTask;
            await WaitForLatestOperationAsync();
            Check(
                report,
                currentWorkspace == "history" && !BusyRing.IsActive && !ErrorBar.IsOpen,
                "repository refresh completes in history",
                FormatHistorySelectionContext());

            HistoryList.SelectedItem = historySecond;
            var staleHistoryRead = latestOperationTask
                ?? throw new InvalidOperationException("Selecting the second history row did not start a switch test read.");
            var switchRepositoryTask = OpenRepositoryAsync(switchedRepositoryPath);
            Check(
                report,
                !switchRepositoryTask.IsCompleted && !HistoryList.IsEnabled,
                "history list disabled while repository switch is pending",
                FormatHistorySelectionContext());
            await Task.WhenAll(staleHistoryRead, switchRepositoryTask);
            await WaitForLatestOperationAsync();
            Check(report, SamePath(repositoryRoot, switchedRepositoryPath), "latest repository root wins", FormatHistorySelectionContext());
            Check(
                report,
                selectedCommit is null
                    && selectedCommitFile is null
                    && commitFileRows.Count == 0
                    && historyDiffRows.Count == 0
                    && !ErrorBar.IsOpen,
                "stale history read does not replace switched repository",
                FormatHistorySelectionContext());

            ShowWorkspace("history");
            if (!historyComparisonBranchesLoaded)
            {
                latestOperationTask = LoadHistoryComparisonBranchesAsync();
                await latestOperationTask;
            }
            MainNavigation.SelectedItem = MainNavigation.MenuItems[1];
            latestOperationTask = LoadHistoryAsync();
            await WaitForLatestOperationAsync();
            var switchedHistorySecond = commitRows.FirstOrDefault(row =>
                string.Equals(row.Summary, "history-b second", StringComparison.Ordinal));
            Check(report, switchedHistorySecond is not null, "switched history row available", FormatHistorySelectionContext());
            HistoryList.SelectedItem = switchedHistorySecond;
            await WaitForLatestOperationAsync();
            Check(report, SamePath(repositoryRoot, switchedRepositoryPath), "switched history root retained", FormatHistorySelectionContext());
            Check(report, HistoryCommitText.Text == "history-b second", "switched history metadata retained", FormatHistorySelectionContext());
            Check(
                report,
                historyDiffRows.Any(row => row.Text.Contains("history-b second", StringComparison.Ordinal))
                    && !historyDiffRows.Any(row => row.Text.Contains("history-a", StringComparison.Ordinal)),
                "switched history diff retained",
                FormatHistorySelectionContext());
            await WaitForLatestOperationAsync();
            await WaitForLayoutAsync();
            Check(
                report,
                ReferenceEquals(MainNavigation.SelectedItem, MainNavigation.MenuItems[1]),
                "history navigation selected",
                FormatHistorySelectionContext());
            Check(
                report,
                !BusyRing.IsActive
                    && currentHistoryDiffIsText
                    && HistoryDiffList.Visibility == Visibility.Visible
                    && ReferenceEquals(HistoryDiffList.ItemsSource, historyDiffRows)
                    && historyDiffRows.Count > 0,
                "history diff rendered before capture",
                FormatHistorySelectionContext());
            report.AppendLine($"final-context={FormatHistorySelectionContext()}");
            await WriteHandlerCheckReportAsync(checksPath, report);
        }
        catch (Exception exception)
        {
            report.AppendLine($"FAIL {exception.Message}");
            await WriteHandlerCheckReportAsync(checksPath, report);
            throw;
        }
        finally
        {
            diagnosticCaptureMode = previousDiagnosticMode;
        }

        await CaptureRootGridAsync(options.OutputPath);
    }

    private string FormatHistorySelectionContext() =>
        $"root={repositoryRoot ?? "<null>"}; workspace={currentWorkspace}; commits={commitRows.Count}; selected={selectedCommit?.Summary ?? "<null>"}; files={commitFileRows.Count}; diff-rows={historyDiffRows.Count}; diff-text={string.Join("\u001f", historyDiffRows.Select(row => EscapeCheckValue(row.Text)))}; history-list-enabled={HistoryList.IsEnabled}; status={StatusText.Text}; error={ErrorBar.IsOpen}|{ErrorBar.Message}";
}
