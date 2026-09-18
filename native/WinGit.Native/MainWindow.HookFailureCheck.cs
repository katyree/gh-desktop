using System.Diagnostics;
using System.Text;
using WinGit.Core;

namespace WinGit.Native;

/// <summary>
/// Task 21 handler check: drives the real commit/amend UI path against a
/// disposable fixture whose synthetic pre-commit hook first fails and then
/// passes. The failing attempt must create no commit, expose the hook failure,
/// and retain the draft, selection, and amend intent; an intentional retry
/// with a fixed hook must succeed through the same guarded path.
/// </summary>
public sealed partial class MainWindow
{
    private const string HookFailureCheckChildName = "hook-failure-check";

    private async Task RunHookFailureCheckAsync(NativeCaptureOptions options)
    {
        var fixtureParent = Path.GetFullPath(options.RepositoryPath!);
        var fixturePath = Path.Combine(fixtureParent, HookFailureCheckChildName);
        var checksPath = options.OutputPath + ".checks.txt";
        var report = new StringBuilder();
        var previousDiagnosticMode = diagnosticCaptureMode;
        var cancellationToken = CancellationToken.None;

        try
        {
            diagnosticCaptureMode = true;
            report.AppendLine("hook-failure-check");
            report.AppendLine($"settings-directory={Environment.GetEnvironmentVariable(HandlerCheckSettingsVariable)}");
            report.AppendLine($"fixture-parent={fixtureParent}");
            report.AppendLine($"fixture={fixturePath}");

            Check(report, Directory.Exists(fixtureParent), "fixture parent exists", fixtureParent);
            Check(
                report,
                !Directory.Exists(fixturePath) && !File.Exists(fixturePath),
                "fixture child is new",
                fixturePath);

            await repositoryService.InitializeAsync(fixturePath, "main", cancellationToken: cancellationToken);
            await repositoryService.SetGitConfigValueAsync(
                fixturePath, GitConfigScope.Local, GitConfigSetting.UserName, "Test User", cancellationToken);
            await repositoryService.SetGitConfigValueAsync(
                fixturePath, GitConfigScope.Local, GitConfigSetting.UserEmail, "test-user@example.invalid", cancellationToken);
            var hooksPath = Path.Combine(fixturePath, ".hooks");
            Directory.CreateDirectory(hooksPath);
            RunHookFailureGit(fixturePath, "config", "core.hooksPath", hooksPath);
            RunHookFailureGit(fixturePath, "config", "commit.gpgsign", "false");

            await File.WriteAllTextAsync(Path.Combine(fixturePath, "tracked.txt"), "base\n", cancellationToken);
            await repositoryService.StageFilesAsync(fixturePath, ["tracked.txt"], cancellationToken);
            var initialId = await repositoryService.CommitAsync(
                fixturePath, "Create hook failure fixture", null, amend: false, cancellationToken);
            Check(report, !string.IsNullOrWhiteSpace(initialId), "fixture committed", initialId);

            await File.WriteAllTextAsync(Path.Combine(fixturePath, "tracked.txt"), "staged change\n", cancellationToken);
            await repositoryService.StageFilesAsync(fixturePath, ["tracked.txt"], cancellationToken);
            await OpenRepositoryAsync(fixturePath);
            await WaitForLatestOperationAsync();
            Check(report, !ErrorBar.IsOpen && SamePath(repositoryRoot, fixturePath), "fixture opened", $"root={repositoryRoot}");

            // The synthetic hook rejects the commit and touches the worktree,
            // so the failure path must preserve both the staged entry and the
            // hook-made change instead of assuming files are unchanged.
            WriteHookFailureHook(hooksPath, "echo hook says no >&2\necho hook-note >> tracked.txt\nexit 1\n");
            CommitSummaryBox.Text = "Hook failure draft";
            CommitDescriptionBox.Text = string.Empty;
            CoAuthorsBox.Text = string.Empty;
            SignOffCheckBox.IsChecked = false;
            AmendCheckBox.IsChecked = false;
            SelectHookFailureRow(staged: true, path: "tracked.txt");
            var beforeFailure = RunHookFailureGit(fixturePath, "rev-parse", "HEAD").Trim();

            await RunCommitMutationAsync(
                CommitSummaryBox.Text.Trim(),
                description: null,
                coAuthors: [],
                signOff: false,
                amend: false,
                expectedHeadId: null);
            await WaitForLatestOperationAsync();

            Check(report, ErrorBar.IsOpen, "hook failure exposed", $"{ErrorBar.Title}: {ErrorBar.Message}");
            Check(
                report,
                ErrorBar.Title.Contains("hook", StringComparison.OrdinalIgnoreCase)
                    && ErrorBar.Message.Contains("hook says no", StringComparison.OrdinalIgnoreCase),
                "hook failure names hook context",
                $"{ErrorBar.Title}: {FirstHookFailureLine(ErrorBar.Message)}");
            Check(
                report,
                RunHookFailureGit(fixturePath, "rev-parse", "HEAD").Trim() == beforeFailure,
                "failed hook attempt created no commit",
                beforeFailure);
            Check(
                report,
                CommitSummaryBox.Text == "Hook failure draft",
                "failed hook attempt retained draft",
                CommitSummaryBox.Text);
            Check(
                report,
                AmendCheckBox.IsChecked != true,
                "failed hook attempt kept amend off",
                $"{AmendCheckBox.IsChecked}");
            Check(
                report,
                StagedChangesList.SelectedItems.OfType<ChangeRow>().Any(row => row.Path.Equals("tracked.txt", StringComparison.OrdinalIgnoreCase)),
                "failed hook attempt retained selection",
                string.Join(",", StagedChangesList.SelectedItems.OfType<ChangeRow>().Select(row => row.Path)));
            Check(
                report,
                (await File.ReadAllTextAsync(Path.Combine(fixturePath, "tracked.txt"), cancellationToken)).Contains("hook-note", StringComparison.Ordinal),
                "hook-made worktree change preserved",
                "tracked.txt");
            ErrorBar.IsOpen = false;

            // Intentional retry with the problem corrected: hooks run again
            // through the same guarded path and the commit succeeds.
            WriteHookFailureHook(hooksPath, "exit 0\n");
            SelectHookFailureRow(staged: true, path: "tracked.txt");
            await RunCommitMutationAsync(
                CommitSummaryBox.Text.Trim(),
                description: null,
                coAuthors: [],
                signOff: false,
                amend: false,
                expectedHeadId: null);
            await WaitForLatestOperationAsync();

            var retriedHead = RunHookFailureGit(fixturePath, "rev-parse", "HEAD").Trim();
            Check(report, !ErrorBar.IsOpen, "retry succeeded without error", ErrorBar.Message);
            Check(report, retriedHead != beforeFailure, "retry advanced HEAD", retriedHead);
            Check(
                report,
                RunHookFailureGit(fixturePath, "log", "-1", "--format=%s").Trim() == "Hook failure draft",
                "retry kept draft message",
                RunHookFailureGit(fixturePath, "log", "-1", "--format=%s").Trim());
            var retriedFiles = await repositoryService.GetCommitFilesAsync(fixturePath, retriedHead, cancellationToken);
            Check(
                report,
                retriedFiles.Count == 1 && retriedFiles[0].Path == "tracked.txt",
                "retry committed the staged file",
                string.Join(",", retriedFiles.Select(file => file.Path)));

            // Amend intent survives a hook rejection the same way: the draft,
            // selection, and amend choice stay in place for the retry.
            await File.WriteAllTextAsync(Path.Combine(fixturePath, "tracked.txt"), "amended change\n", cancellationToken);
            await repositoryService.StageFilesAsync(fixturePath, ["tracked.txt"], cancellationToken);
            await RefreshRepositoryAsync();
            await WaitForLatestOperationAsync();
            WriteHookFailureHook(hooksPath, "echo amend hook says no >&2\nexit 1\n");
            AmendCheckBox.IsChecked = true;
            CommitSummaryBox.Text = "Amended hook draft";
            SelectHookFailureRow(staged: true, path: "tracked.txt");
            await RunCommitMutationAsync(
                "Amended hook draft",
                description: null,
                coAuthors: [],
                signOff: false,
                amend: true,
                expectedHeadId: retriedHead);
            await WaitForLatestOperationAsync();

            Check(report, ErrorBar.IsOpen, "amend hook failure exposed", $"{ErrorBar.Title}: {ErrorBar.Message}");
            Check(
                report,
                ErrorBar.Title.Contains("Amend", StringComparison.OrdinalIgnoreCase),
                "amend failure names amend operation",
                ErrorBar.Title);
            Check(
                report,
                RunHookFailureGit(fixturePath, "rev-parse", "HEAD").Trim() == retriedHead,
                "failed amend kept HEAD",
                retriedHead);
            Check(
                report,
                CommitSummaryBox.Text == "Amended hook draft" && AmendCheckBox.IsChecked == true,
                "failed amend retained draft and intent",
                $"summary={CommitSummaryBox.Text}; amend={AmendCheckBox.IsChecked}");
            ErrorBar.IsOpen = false;

            WriteHookFailureHook(hooksPath, "exit 0\n");
            SelectHookFailureRow(staged: true, path: "tracked.txt");
            await RunCommitMutationAsync(
                CommitSummaryBox.Text.Trim(),
                description: null,
                coAuthors: [],
                signOff: false,
                amend: true,
                expectedHeadId: retriedHead);
            await WaitForLatestOperationAsync();

            var amendedHead = RunHookFailureGit(fixturePath, "rev-parse", "HEAD").Trim();
            Check(report, !ErrorBar.IsOpen, "amend retry succeeded", ErrorBar.Message);
            Check(report, amendedHead != retriedHead, "amend retry replaced HEAD", amendedHead);

            report.AppendLine($"final-context=root={repositoryRoot}; head={amendedHead}");
            StatusText.Text = "Hook failure check passed";
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

    private void SelectHookFailureRow(bool staged, string path)
    {
        var rows = staged ? stagedChangeRows : unstagedChangeRows;
        var list = staged ? StagedChangesList : UnstagedChangesList;
        var row = rows.FirstOrDefault(candidate =>
            candidate.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            throw new InvalidOperationException($"The {(staged ? "staged" : "unstaged")} row '{path}' was not visible.");
        }

        list.SelectedItems.Clear();
        list.SelectedItems.Add(row);
    }

    private static void WriteHookFailureHook(string hooksPath, string scriptBody)
    {
        // Git for Windows runs extensionless hooks through sh; LF endings only.
        var fullPath = Path.Combine(hooksPath, "pre-commit");
        File.WriteAllText(
            fullPath,
            "#!/bin/sh\n" + scriptBody.Replace("\r\n", "\n", StringComparison.Ordinal),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string FirstHookFailureLine(string value)
    {
        var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return line.Length <= 200 ? line : line[..200];
    }

    private static string RunHookFailureGit(string workingDirectory, params string[] arguments)
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Git check.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git check command failed: {error}");
        }

        return output;
    }
}
