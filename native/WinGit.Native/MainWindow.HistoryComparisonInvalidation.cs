using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private void ClearStaleHistoryComparison(string message)
    {
        var staleMessage = string.IsNullOrWhiteSpace(message)
            ? "The history comparison changed. Refresh the repository and choose it again."
            : message;

        historyCacheRoot = null;
        historyCacheHeadId = null;
        historyComparisonMode = ComparisonMode.Behind;
        ClearHistoryComparisonState(clearHistoryRows: true, clearBranchSelection: true);
        HistoryCommitText.Text = "Refresh required";
        HistoryCommitSummaryText.Text = staleMessage;
        ShowHistoryDiffMessage("Refresh required", staleMessage);
        StatusText.Text = "Refresh required";
    }
}
