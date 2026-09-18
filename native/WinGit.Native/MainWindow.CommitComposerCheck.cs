using System.Diagnostics;
using System.Text;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private const string CommitComposerCheckChildName = "commit-composer-check";
    private const string CommitComposerCheckSecondName = "commit-composer-second";
    private const string CommitComposerCheckMarkerName = ".wingit-commit-composer-check";

    private async Task RunCommitComposerCheckAsync(NativeCaptureOptions options)
    {
        var fixtureParent = Path.GetFullPath(options.RepositoryPath!);
        var fixturePath = Path.Combine(fixtureParent, CommitComposerCheckChildName);
        var secondPath = Path.Combine(fixtureParent, CommitComposerCheckSecondName);
        var checksPath = options.OutputPath + ".checks.txt";
        var report = new StringBuilder();
        var previousDiagnosticMode = diagnosticCaptureMode;
        var cancellationToken = CancellationToken.None;

        try
        {
            diagnosticCaptureMode = true;
            report.AppendLine("commit-composer-check");
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

            await repositoryService.InitializeAsync(fixturePath, "main", cancellationToken: cancellationToken);
            await repositoryService.SetGitConfigValueAsync(
                fixturePath, GitConfigScope.Local, GitConfigSetting.UserName, "Test User", cancellationToken);
            await repositoryService.SetGitConfigValueAsync(
                fixturePath, GitConfigScope.Local, GitConfigSetting.UserEmail, "test-user@example.invalid", cancellationToken);
            DisableCommitComposerCheckHooks(fixturePath);

            await File.WriteAllTextAsync(Path.Combine(fixturePath, "staged.txt"), "initial staged\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(fixturePath, "other.txt"), "initial other\n", cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, CommitComposerCheckMarkerName),
                "WinGit commit composer check fixture\n",
                cancellationToken);
            await repositoryService.StageFilesAsync(
                fixturePath, ["staged.txt", "other.txt", CommitComposerCheckMarkerName], cancellationToken);
            var initialId = await repositoryService.CommitAsync(
                fixturePath, "Create commit composer fixture", null, amend: false, cancellationToken);
            Check(report, !string.IsNullOrWhiteSpace(initialId), "fixture committed", initialId);

            await File.WriteAllTextAsync(Path.Combine(fixturePath, "staged.txt"), "staged change\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(fixturePath, "other.txt"), "other change\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(fixturePath, "untracked.txt"), "untracked change\n", cancellationToken);
            await repositoryService.StageFilesAsync(fixturePath, ["staged.txt"], cancellationToken);

            await OpenRepositoryAsync(fixturePath);
            await WaitForLatestOperationAsync();
            Check(report, !ErrorBar.IsOpen && SamePath(repositoryRoot, fixturePath), "fixture opened", $"root={repositoryRoot}");
            Check(report, currentStatus?.Changes.Count == 3, "initial changed rows", DescribeCommitComposerStatus(currentStatus));

            // Draft a full composer: summary, two-paragraph description,
            // one co-author line, and the sign-off trailer.
            CommitSummaryBox.Text = "Commit composer summary";
            CommitDescriptionBox.Text = "First paragraph\n\nSecond paragraph";
            CoAuthorsBox.Text = "A11y Ally <ally@example.invalid>";
            SignOffCheckBox.IsChecked = true;
            SelectCommitComposerRow(staged: true, path: "staged.txt");
            await WaitForLatestOperationAsync();

            await RunCommitMutationAsync(
                CommitSummaryBox.Text.Trim(),
                CommitDescriptionBox.Text.Trim(),
                ["A11y Ally <ally@example.invalid>"],
                signOff: true,
                amend: false,
                expectedHeadId: null);
            await WaitForLatestOperationAsync();
            Check(report, !ErrorBar.IsOpen, "commit succeeded without error", ErrorBar.Message);

            var head = RunCommitComposerGit(fixturePath, "rev-parse", "HEAD").Trim();
            Check(report, !string.Equals(head, initialId, StringComparison.OrdinalIgnoreCase), "HEAD advanced", head);
            var body = RunCommitComposerGit(fixturePath, "log", "-1", "--format=%B");
            Check(report, body.Contains("Commit composer summary", StringComparison.Ordinal), "message has summary", FirstLine(body));
            Check(report, body.Contains("First paragraph", StringComparison.Ordinal), "message has first paragraph", FirstLine(body));
            Check(report, body.Contains("Second paragraph", StringComparison.Ordinal), "message has second paragraph", FirstLine(body));
            Check(
                report,
                body.Contains("Co-Authored-By: A11y Ally <ally@example.invalid>", StringComparison.Ordinal),
                "message has co-author trailer",
                FirstLine(body));
            Check(
                report,
                body.Contains("Signed-off-by: Test User <test-user@example.invalid>", StringComparison.Ordinal),
                "message has sign-off trailer",
                FirstLine(body));
            var author = RunCommitComposerGit(fixturePath, "log", "-1", "--format=%an%x00%ae").Trim();
            Check(
                report,
                author == "Test User\0test-user@example.invalid",
                "commit author metadata",
                author.Replace("\0", "<NUL>"));

            var committedFiles = await repositoryService.GetCommitFilesAsync(fixturePath, head, cancellationToken);
            Check(
                report,
                committedFiles.Count == 1 && committedFiles[0].Path == "staged.txt",
                "commit contains only staged file",
                string.Join(",", committedFiles.Select(file => file.Path)));
            Check(
                report,
                (await File.ReadAllTextAsync(Path.Combine(fixturePath, "staged.txt"), cancellationToken)) == "staged change\n",
                "staged worktree preserved",
                "staged.txt");
            Check(
                report,
                (await File.ReadAllTextAsync(Path.Combine(fixturePath, "other.txt"), cancellationToken)) == "other change\n",
                "unstaged worktree preserved",
                "other.txt");
            Check(
                report,
                (await File.ReadAllTextAsync(Path.Combine(fixturePath, "untracked.txt"), cancellationToken)) == "untracked change\n",
                "untracked file preserved",
                "untracked.txt");
            Check(
                report,
                string.IsNullOrEmpty(CommitSummaryBox.Text)
                    && string.IsNullOrEmpty(CommitDescriptionBox.Text)
                    && string.IsNullOrEmpty(CoAuthorsBox.Text)
                    && SignOffCheckBox.IsChecked != true
                    && AmendCheckBox.IsChecked != true,
                "composer cleared after success",
                $"summary={CommitSummaryBox.Text}; coauthors={CoAuthorsBox.Text}; signoff={SignOffCheckBox.IsChecked}; amend={AmendCheckBox.IsChecked}");

            // Amend the new HEAD with attribution: the replacement keeps the
            // edited message and trailers while the worktree stays clean.
            await File.WriteAllTextAsync(Path.Combine(fixturePath, "staged.txt"), "amended change\n", cancellationToken);
            await repositoryService.StageFilesAsync(fixturePath, ["staged.txt"], cancellationToken);
            await RefreshRepositoryAsync();
            await WaitForLatestOperationAsync();
            var amendBase = RunCommitComposerGit(fixturePath, "rev-parse", "HEAD").Trim();
            AmendCheckBox.IsChecked = true;
            CommitSummaryBox.Text = "Amended composer summary";
            CommitDescriptionBox.Text = "Amended body";
            CoAuthorsBox.Text = "A11y Ally <ally@example.invalid>";
            SignOffCheckBox.IsChecked = true;
            await RunCommitMutationAsync(
                "Amended composer summary",
                "Amended body",
                ["A11y Ally <ally@example.invalid>"],
                signOff: true,
                amend: true,
                expectedHeadId: amendBase);
            await WaitForLatestOperationAsync();
            var amendedHead = RunCommitComposerGit(fixturePath, "rev-parse", "HEAD").Trim();
            Check(report, !string.Equals(amendedHead, amendBase, StringComparison.OrdinalIgnoreCase), "amend replaced HEAD", amendedHead);
            var amendedBody = RunCommitComposerGit(fixturePath, "log", "-1", "--format=%B");
            Check(report, amendedBody.Contains("Amended composer summary", StringComparison.Ordinal), "amend kept message", FirstLine(amendedBody));
            Check(
                report,
                amendedBody.Contains("Co-Authored-By: A11y Ally <ally@example.invalid>", StringComparison.Ordinal),
                "amend kept co-author",
                FirstLine(amendedBody));

            // A failed commit must retain the draft and the file selection so
            // the user can correct the issue and retry. A stale amend guard
            // is deterministic without depending on machine-global Git
            // configuration: the initial fixture ID is no longer HEAD.
            await File.WriteAllTextAsync(Path.Combine(fixturePath, "other.txt"), "other change two\n", cancellationToken);
            await repositoryService.StageFilesAsync(fixturePath, ["other.txt"], cancellationToken);
            await RefreshRepositoryAsync();
            await WaitForLatestOperationAsync();
            CommitSummaryBox.Text = "Retained draft summary";
            CommitDescriptionBox.Text = "Retained draft body";
            CoAuthorsBox.Text = "A11y Ally <ally@example.invalid>";
            SignOffCheckBox.IsChecked = false;
            SelectCommitComposerRow(staged: true, path: "other.txt");
            var failedHead = RunCommitComposerGit(fixturePath, "rev-parse", "HEAD").Trim();
            Check(
                report,
                !string.Equals(failedHead, initialId, StringComparison.OrdinalIgnoreCase),
                "failure fixture HEAD moved on",
                failedHead);
            await RunCommitMutationAsync(
                "Retained draft summary",
                "Retained draft body",
                ["A11y Ally <ally@example.invalid>"],
                signOff: false,
                amend: true,
                expectedHeadId: initialId);
            await WaitForLatestOperationAsync();
            Check(report, ErrorBar.IsOpen, "failed commit shows error", $"{ErrorBar.Title}: {ErrorBar.Message}");
            Check(
                report,
                RunCommitComposerGit(fixturePath, "rev-parse", "HEAD").Trim() == failedHead,
                "failed commit kept HEAD",
                failedHead);
            Check(
                report,
                CommitSummaryBox.Text == "Retained draft summary"
                    && CommitDescriptionBox.Text == "Retained draft body"
                    && CoAuthorsBox.Text == "A11y Ally <ally@example.invalid>",
                "failed commit retained draft",
                $"summary={CommitSummaryBox.Text}; description={CommitDescriptionBox.Text}; coauthors={CoAuthorsBox.Text}");
            Check(
                report,
                StagedChangesList.SelectedItems.OfType<ChangeRow>().Any(row => row.Path.Equals("other.txt", StringComparison.OrdinalIgnoreCase)),
                "failed commit retained selection",
                string.Join(",", StagedChangesList.SelectedItems.OfType<ChangeRow>().Select(row => row.Path)));
            ErrorBar.IsOpen = false;

            // Opening a different repository must not apply this draft,
            // selection, or result to the wrong repository.
            await repositoryService.InitializeAsync(secondPath, "main", cancellationToken: cancellationToken);
            await repositoryService.SetGitConfigValueAsync(
                secondPath, GitConfigScope.Local, GitConfigSetting.UserName, "Test User", cancellationToken);
            await repositoryService.SetGitConfigValueAsync(
                secondPath, GitConfigScope.Local, GitConfigSetting.UserEmail, "test-user@example.invalid", cancellationToken);
            DisableCommitComposerCheckHooks(secondPath);
            await File.WriteAllTextAsync(Path.Combine(secondPath, "second.txt"), "second\n", cancellationToken);
            await OpenRepositoryAsync(secondPath);
            await WaitForLatestOperationAsync();
            Check(report, SamePath(repositoryRoot, secondPath), "second repository opened", $"root={repositoryRoot}");
            Check(
                report,
                string.IsNullOrEmpty(CommitSummaryBox.Text) && string.IsNullOrEmpty(CommitDescriptionBox.Text),
                "switch cleared draft",
                $"summary={CommitSummaryBox.Text}; description={CommitDescriptionBox.Text}");
            Check(
                report,
                RunCommitComposerGit(fixturePath, "rev-parse", "HEAD").Trim() == failedHead,
                "first repository HEAD untouched by switch",
                failedHead);

            Check(report, !ErrorBar.IsOpen, "final error bar closed", ErrorBar.Message);
            report.AppendLine($"final-context=root={repositoryRoot}; head={failedHead}");
            StatusText.Text = "Commit composer check passed";
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

    private void SelectCommitComposerRow(bool staged, string path)
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

    private static void DisableCommitComposerCheckHooks(string root)
    {
        var hooksPath = Path.Combine(root, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunCommitComposerGit(root, "config", "core.hooksPath", hooksPath);
        RunCommitComposerGit(root, "config", "commit.gpgsign", "false");
    }

    private static string DescribeCommitComposerStatus(RepositoryStatus? status) =>
        status is null
            ? "<null>"
            : string.Join(
                ",",
                status.Changes.Select(change =>
                    $"{change.Path}:{change.IndexStatus}/{change.WorkTreeStatus}"));

    private static string FirstLine(string value)
    {
        var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return line.Length <= 160 ? line : line[..160];
    }

    private static string RunCommitComposerGit(string workingDirectory, params string[] arguments)
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
