using System.Text;
using Microsoft.UI.Xaml;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private const string HandlerCheckSettingsVariable = "WINGIT_NATIVE_SETTINGS_DIRECTORY";

    private sealed record RepositoryOpenCheckContext(
        RepositoryStatus? Status,
        string? Root,
        string? SelectedPath,
        string DiffFile,
        string DiffSummary,
        int DiffRowCount,
        string DiffRows,
        string PartialDiffRows,
        string FilterText,
        string DraftSummary,
        string DraftDescription,
        string Workspace);

    private static string? TryGetHandlerCheckSettingsError()
    {
        var overridePath = Environment.GetEnvironmentVariable(HandlerCheckSettingsVariable);
        if (string.IsNullOrWhiteSpace(overridePath))
        {
            return $"{HandlerCheckSettingsVariable} must be a fully qualified isolated directory for handler checks.";
        }

        try
        {
            if (!Path.IsPathFullyQualified(overridePath))
            {
                return $"{HandlerCheckSettingsVariable} must be a fully qualified isolated directory for handler checks.";
            }

            var normalizedOverride = NormalizeDirectoryForComparison(overridePath);
            var sharedDirectory = NormalizeDirectoryForComparison(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinGit.Native"));
            return string.Equals(normalizedOverride, sharedDirectory, StringComparison.OrdinalIgnoreCase)
                ? $"{HandlerCheckSettingsVariable} must differ from the shared native settings directory."
                : null;
        }
        catch (Exception exception)
        {
            return $"{HandlerCheckSettingsVariable} is invalid: {exception.Message}";
        }
    }

    private async Task RunRepositoryOpenCheckAsync(NativeCaptureOptions options)
    {
        var fixtureParent = options.RepositoryPath!;
        var committed = Path.Combine(fixtureParent, "committed-main");
        var unborn = Path.Combine(fixtureParent, "unborn-main");
        var detached = Path.Combine(fixtureParent, "detached-head");
        var failedPreload = Path.Combine(fixtureParent, "failed-preload");
        var nonRepository = Path.Combine(fixtureParent, "non-repository");
        var missing = Path.Combine(fixtureParent, "missing-open-target");
        var checksPath = options.OutputPath + ".checks.txt";
        var report = new StringBuilder();
        var previousDiagnosticMode = diagnosticCaptureMode;

        try
        {
            diagnosticCaptureMode = false;
            report.AppendLine("repository-open-check");
            report.AppendLine($"settings-directory={Environment.GetEnvironmentVariable(HandlerCheckSettingsVariable)}");
            report.AppendLine($"fixture-parent={fixtureParent}");

            await OpenRepositoryAsync(committed);
            await WaitForLatestOperationAsync();
            Check(report, SamePath(repositoryRoot, committed), "baseline root", FormatContext(CaptureContext()));
            Check(report, currentWorkspace == "changes", "baseline workspace", FormatContext(CaptureContext()));

            ChangesFilterBox.Text = "tracked";
            var untrackedIndex = -1;
            for (var index = 0; index < unstagedChangeRows.Count; index++)
            {
                if (unstagedChangeRows[index].Path.Equals("untracked.txt", StringComparison.OrdinalIgnoreCase))
                {
                    untrackedIndex = index;
                    break;
                }
            }
            Check(report, untrackedIndex >= 0, "baseline untracked row", $"rows={unstagedChangeRows.Count}");
            UnstagedChangesList.SelectedIndex = untrackedIndex;
            await WaitForLatestOperationAsync();
            Check(report, string.Equals(selectedChangePath, "untracked.txt", StringComparison.OrdinalIgnoreCase), "baseline selected file", FormatContext(CaptureContext()));
            CommitSummaryBox.Text = "Synthetic handler-check draft";
            CommitDescriptionBox.Text = "Preserve this draft while repository opens fail or cancel.";
            var baseline = CaptureContext();
            report.AppendLine($"baseline-context={FormatContext(baseline)}");

            foreach (var (label, target) in new[]
            {
                ("non-repository", nonRepository),
                ("failed-preload", failedPreload),
                ("nonexistent", missing),
            })
            {
                ErrorBar.IsOpen = false;
                await OpenRepositoryAsync(target);
                await WaitForLatestOperationAsync();
                Check(report, ErrorBar.IsOpen, $"{label} error visible", ErrorBar.Message);
                CheckPreserved(report, label, baseline);
            }

            ErrorBar.IsOpen = false;
            var cancelledOpen = OpenRepositoryAsync(unborn);
            Check(report, !cancelledOpen.IsCompleted, "cancel target pending", cancelledOpen.Status.ToString());
            CancelOperationButton_Click(CancelOperationButton, new RoutedEventArgs());
            await cancelledOpen;
            await WaitForLatestOperationAsync();
            CheckPreserved(report, "cancelled open", baseline);

            ErrorBar.IsOpen = false;
            var obsoleteOpen = OpenRepositoryAsync(unborn);
            Check(report, !obsoleteOpen.IsCompleted, "obsolete target pending", obsoleteOpen.Status.ToString());
            var chooserOpen = OpenRepositoryChooserRowAsync(new RepositoryChooserRow(detached, repositoryRoot));
            await Task.WhenAll(obsoleteOpen, chooserOpen);
            await WaitForLatestOperationAsync();
            Check(report, SamePath(repositoryRoot, detached), "chooser target wins", FormatContext(CaptureContext()));
            Check(report, currentStatus is not null && SamePath(currentStatus.RootPath, detached), "chooser status wins", FormatContext(CaptureContext()));
            Check(report, !ErrorBar.IsOpen, "obsolete open has no late error", ErrorBar.Message);

            var normalizedCommitted = Path.Combine(
                committed,
                "nested",
                "open-here",
                "..",
                "open-here") + Path.DirectorySeparatorChar;
            await OpenRepositoryAsync(normalizedCommitted);
            await WaitForLatestOperationAsync();
            Check(report, SamePath(repositoryRoot, committed), "normalized committed root", FormatContext(CaptureContext()));
            await SaveSettingsAsync();
            var inMemoryCount = settings.RecentRepositories.Count(path => SamePath(path, committed));
            var reloaded = await NativeSettingsStore.LoadAsync();
            var persistedCount = reloaded.RecentRepositories.Count(path => SamePath(path, committed));
            Check(report, inMemoryCount == 1, "one in-memory recent", $"count={inMemoryCount}");
            Check(report, persistedCount == 1, "one persisted recent", $"count={persistedCount}");
            Check(report, !ErrorBar.IsOpen, "final error bar closed", ErrorBar.Message);
            report.AppendLine($"final-context={FormatContext(CaptureContext())}");
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

    private async Task RunRepositoryPickerCheckAsync(NativeCaptureOptions options)
    {
        var committed = Path.Combine(options.RepositoryPath!, "committed-main");
        var checksPath = options.OutputPath + ".checks.txt";
        var report = new StringBuilder();
        var previousDiagnosticMode = diagnosticCaptureMode;

        try
        {
            diagnosticCaptureMode = false;
            await OpenRepositoryAsync(committed);
            await WaitForLatestOperationAsync();
            var before = CaptureContext();
            var beforeRecent = settings.RecentRepositories.Count;
            report.AppendLine("repository-picker-check");
            report.AppendLine($"before-context={FormatContext(before)}");
            report.AppendLine($"before-recent-count={beforeRecent}");
            await WriteHandlerCheckReportAsync(checksPath, report);
            await OpenRepositoryPickerAsync();
            await WaitForLatestOperationAsync();
            await SaveSettingsAsync();
            report.AppendLine($"after-context={FormatContext(CaptureContext())}");
            report.AppendLine($"after-errorbar={ErrorBar.IsOpen}; title={ErrorBar.Title}; message={ErrorBar.Message}");
            report.AppendLine($"after-recent-count={settings.RecentRepositories.Count}");
            report.AppendLine("picker-invoked=true; selection or cancellation was user-controlled");
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

    private RepositoryOpenCheckContext CaptureContext() => new(
        currentStatus,
        repositoryRoot,
        selectedChangePath,
        DiffFileText.Text,
        DiffSummaryText.Text,
        diffRows.Count,
        SerializeDiffRows(),
        SerializePartialDiffRows(),
        ChangesFilterBox.Text,
        CommitSummaryBox.Text,
        CommitDescriptionBox.Text,
        currentWorkspace);

    private static void Check(StringBuilder report, bool condition, string label, string actual)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{label} failed: {actual}");
        }

        report.AppendLine($"PASS {label}: {actual}");
    }

    private void CheckPreserved(
        StringBuilder report,
        string label,
        RepositoryOpenCheckContext expected)
    {
        var actual = CaptureContext();
        Check(
            report,
            ReferenceEquals(actual.Status, expected.Status)
                && string.Equals(actual.Root, expected.Root, StringComparison.OrdinalIgnoreCase)
                && string.Equals(actual.SelectedPath, expected.SelectedPath, StringComparison.Ordinal)
                && string.Equals(actual.DiffFile, expected.DiffFile, StringComparison.Ordinal)
                && string.Equals(actual.DiffSummary, expected.DiffSummary, StringComparison.Ordinal)
                && actual.DiffRowCount == expected.DiffRowCount
                && string.Equals(actual.DiffRows, expected.DiffRows, StringComparison.Ordinal)
                && string.Equals(actual.PartialDiffRows, expected.PartialDiffRows, StringComparison.Ordinal)
                && string.Equals(actual.FilterText, expected.FilterText, StringComparison.Ordinal)
                && string.Equals(actual.DraftSummary, expected.DraftSummary, StringComparison.Ordinal)
                && string.Equals(actual.DraftDescription, expected.DraftDescription, StringComparison.Ordinal)
                && string.Equals(actual.Workspace, expected.Workspace, StringComparison.Ordinal),
            $"{label} preserved context",
            FormatContext(actual));
    }

    private static string FormatContext(RepositoryOpenCheckContext context) =>
        $"root={context.Root ?? "<null>"}; statusRoot={context.Status?.RootPath ?? "<null>"}; head={context.Status?.HeadId ?? "<null>"}; selected={context.SelectedPath ?? "<null>"}; filter={context.FilterText}; diff={context.DiffFile}|{context.DiffSummary}|rows={context.DiffRowCount}|{context.DiffRows}; partial={context.PartialDiffRows}; draft={context.DraftSummary}|{context.DraftDescription}; workspace={context.Workspace}";

    private string SerializeDiffRows() => string.Join(
        "\u001f",
        diffRows.Select(row => $"{EscapeCheckValue(row.Marker)}:{EscapeCheckValue(row.Text)}"));

    private string SerializePartialDiffRows() => string.Join(
        "\u001f",
        partialDiffRows.Select(row => row.IsHunk
            ? $"H:{EscapeCheckValue(row.Header)}"
            : $"L:{EscapeCheckValue(row.Marker)}:{EscapeCheckValue(row.Text)}"));

    private static string EscapeCheckValue(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\u001f", "\\u001f", StringComparison.Ordinal);

    private static bool SamePath(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(
            NormalizeDirectoryForComparison(left),
            NormalizeDirectoryForComparison(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryForComparison(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static async Task WriteHandlerCheckReportAsync(string path, StringBuilder report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, report.ToString());
    }
}
