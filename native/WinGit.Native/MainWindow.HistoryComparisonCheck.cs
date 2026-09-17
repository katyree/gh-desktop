using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private sealed record HistoryComparisonCheckManifest(
        string RunRoot,
        string RepositoryRoot,
        string MainHead,
        string FeatureHead,
        string CommonRoot,
        IReadOnlyList<string> MainExclusive,
        IReadOnlyList<string> FeatureExclusive,
        string ComparisonFile,
        IReadOnlyList<string> MainPaths,
        IReadOnlyList<string> FeaturePaths,
        string GitConfigGlobal,
        string GitConfigNoSystem,
        string GitTerminalPrompt);

    private sealed record DiagnosticGitResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private async Task RunHistoryComparisonCheckAsync(NativeCaptureOptions options)
    {
        var fixtureParent = options.RepositoryPath!;
        var checksPath = options.OutputPath + ".checks.txt";
        var report = new StringBuilder();
        var previousDiagnosticMode = diagnosticCaptureMode;

        try
        {
            var settingsError = TryGetHandlerCheckSettingsError();
            Check(
                report,
                settingsError is null,
                "isolated settings directory",
                settingsError ?? Environment.GetEnvironmentVariable(HandlerCheckSettingsVariable)!);

            var manifestPath = Path.Combine(fixtureParent, "fixture-manifest.json");
            var manifest = ReadHistoryComparisonCheckManifest(manifestPath);
            var normalizedFixtureParent = NormalizeDirectoryForComparison(fixtureParent);
            Check(
                report,
                SamePath(manifest.RunRoot, fixtureParent),
                "manifest run root",
                $"manifest={manifest.RunRoot}; argument={fixtureParent}");
            Check(
                report,
                SamePath(manifest.RepositoryRoot, Path.Combine(fixtureParent, "fixtures", "history-comparison")),
                "manifest repository root",
                manifest.RepositoryRoot);
            EnsurePathWithin(normalizedFixtureParent, manifest.RepositoryRoot, "manifest repository root");

            report.AppendLine($"history-comparison-check");
            report.AppendLine($"fixture-parent={fixtureParent}");
            report.AppendLine($"manifest={manifestPath}");
            report.AppendLine($"baseline-repository={manifest.RepositoryRoot}");
            report.AppendLine($"settings-directory={Environment.GetEnvironmentVariable(HandlerCheckSettingsVariable)}");

            var diagnosticRoot = Path.Combine(
                fixtureParent,
                "diagnostic-owned",
                $"history-comparison-{Guid.NewGuid():N}");
            EnsurePathWithin(normalizedFixtureParent, diagnosticRoot, "diagnostic clone");
            Directory.CreateDirectory(Path.GetDirectoryName(diagnosticRoot)!);

            var gitEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GIT_CONFIG_GLOBAL"] = manifest.GitConfigGlobal,
                ["GIT_CONFIG_NOSYSTEM"] = manifest.GitConfigNoSystem,
                ["GIT_TERMINAL_PROMPT"] = manifest.GitTerminalPrompt,
            };

            await RunDiagnosticGitAsync(
                normalizedFixtureParent,
                normalizedFixtureParent,
                ["clone", "--no-local", "--quiet", manifest.RepositoryRoot, diagnosticRoot],
                gitEnvironment);
            report.AppendLine($"diagnostic-clone={diagnosticRoot}");

            var hooksPath = Path.Combine(diagnosticRoot, ".hooks-disabled");
            EnsurePathWithin(diagnosticRoot, hooksPath, "diagnostic hooks");
            Directory.CreateDirectory(hooksPath);
            await ConfigureDiagnosticRepositoryAsync(diagnosticRoot, hooksPath, gitEnvironment, normalizedFixtureParent);
            await RunDiagnosticGitAsync(
                normalizedFixtureParent,
                diagnosticRoot,
                ["branch", "feature", manifest.FeatureHead],
                gitEnvironment);

            var independent = await ReadIndependentComparisonAsync(
                diagnosticRoot,
                gitEnvironment,
                normalizedFixtureParent,
                manifest);
            report.AppendLine(
                $"independent-endpoints=main:{independent.MainHead}; feature:{independent.FeatureHead}; merge-base:{independent.CommonRoot}");
            report.AppendLine(
                $"independent-counts=ahead:{independent.Ahead}; behind:{independent.Behind}");
            report.AppendLine($"independent-main-paths={string.Join(",", independent.MainPaths)}");
            report.AppendLine($"independent-feature-paths={string.Join(",", independent.FeaturePaths)}");

            Check(
                report,
                string.Equals(independent.MainHead, manifest.MainHead, StringComparison.OrdinalIgnoreCase),
                "manifest main endpoint",
                independent.MainHead);
            Check(
                report,
                string.Equals(independent.FeatureHead, manifest.FeatureHead, StringComparison.OrdinalIgnoreCase),
                "manifest feature endpoint",
                independent.FeatureHead);
            Check(
                report,
                string.Equals(independent.CommonRoot, manifest.CommonRoot, StringComparison.OrdinalIgnoreCase),
                "manifest merge base",
                independent.CommonRoot);
            Check(report, independent.Ahead == 2 && independent.Behind == 2, "independent side counts", $"ahead={independent.Ahead}; behind={independent.Behind}");
            Check(report, independent.MainPaths.SetEquals(manifest.MainPaths), "independent main paths", string.Join(",", independent.MainPaths));
            Check(report, independent.FeaturePaths.SetEquals(manifest.FeaturePaths), "independent feature paths", string.Join(",", independent.FeaturePaths));

            diagnosticCaptureMode = true;
            await OpenRepositoryAsync(diagnosticRoot);
            MainNavigation.SelectedItem = MainNavigation.MenuItems[1];
            await WaitForDiagnosticIdleAsync("navigate to history");

            Check(report, currentWorkspace == "history", "history workspace loaded", currentWorkspace);
            Check(report, historyComparisonBranchRows.Count >= 2, "comparison branches loaded", $"rows={historyComparisonBranchRows.Count}");
            var featureRow = historyComparisonBranchRows.FirstOrDefault(row =>
                string.Equals(row.Name, "feature", StringComparison.OrdinalIgnoreCase));
            Check(report, featureRow is not null, "feature comparison branch available", string.Join(",", historyComparisonBranchRows.Select(row => row.Name)));
            if (featureRow is null)
            {
                throw new InvalidOperationException("The synthetic fixture did not expose the feature comparison branch.");
            }

            await SelectHistoryComparisonBranchForCheckAsync(featureRow, report);
            var behindSnapshot = historyComparisonSnapshot;
            Check(report, behindSnapshot is not null, "behind snapshot captured", FormatComparisonSnapshot(behindSnapshot));
            Check(report, behindSnapshot!.Mode == ComparisonMode.Behind, "behind mode", FormatComparisonSnapshot(behindSnapshot));
            Check(report, behindSnapshot.Ahead == independent.Ahead && behindSnapshot.Behind == independent.Behind, "behind counts match independent Git", FormatComparisonSnapshot(behindSnapshot));
            Check(report, string.Equals(behindSnapshot.BaseHeadId, independent.MainHead, StringComparison.OrdinalIgnoreCase), "behind base endpoint", behindSnapshot.BaseHeadId);
            Check(report, string.Equals(behindSnapshot.ComparisonHeadId, independent.FeatureHead, StringComparison.OrdinalIgnoreCase), "behind comparison endpoint", behindSnapshot.ComparisonHeadId);
            Check(report, behindSnapshot.Commits.Count == independent.Behind, "behind commit count", $"rows={behindSnapshot.Commits.Count}");
            Check(report, HistoryComparisonDetailsPanel.Visibility == Visibility.Visible, "comparison details visible", HistoryComparisonDetailsPanel.Visibility.ToString());
            Check(report, HistoryComparisonEndpointsText.Text.Contains(independent.MainHead[..7], StringComparison.OrdinalIgnoreCase)
                && HistoryComparisonEndpointsText.Text.Contains(independent.FeatureHead[..7], StringComparison.OrdinalIgnoreCase), "endpoint text rendered", HistoryComparisonEndpointsText.Text);
            Check(report, HistoryComparisonBehindButton.Content?.ToString()?.Contains("2", StringComparison.Ordinal) == true, "behind count rendered", HistoryComparisonBehindButton.Content?.ToString() ?? string.Empty);
            Check(report, HistoryComparisonAheadButton.Content?.ToString()?.Contains("2", StringComparison.Ordinal) == true, "ahead count rendered", HistoryComparisonAheadButton.Content?.ToString() ?? string.Empty);

            HistoryComparisonAheadButton_Click(HistoryComparisonAheadButton, new RoutedEventArgs());
            await WaitForDiagnosticIdleAsync("ahead comparison");
            var aheadSnapshot = historyComparisonSnapshot;
            Check(report, aheadSnapshot is not null, "ahead snapshot captured", FormatComparisonSnapshot(aheadSnapshot));
            Check(report, aheadSnapshot!.Mode == ComparisonMode.Ahead, "ahead mode", FormatComparisonSnapshot(aheadSnapshot));
            Check(report, aheadSnapshot.Ahead == independent.Ahead && aheadSnapshot.Behind == independent.Behind, "ahead counts match independent Git", FormatComparisonSnapshot(aheadSnapshot));
            Check(report, string.Equals(aheadSnapshot.BaseHeadId, independent.MainHead, StringComparison.OrdinalIgnoreCase)
                && string.Equals(aheadSnapshot.ComparisonHeadId, independent.FeatureHead, StringComparison.OrdinalIgnoreCase), "ahead endpoints remain captured", FormatComparisonSnapshot(aheadSnapshot));
            Check(report, aheadSnapshot.Commits.Count == independent.Ahead, "ahead commit count", $"rows={aheadSnapshot.Commits.Count}");
            Check(report, CommitIdsMatch(aheadSnapshot.Commits, manifest.MainExclusive), "ahead commit IDs match independent fixture", string.Join(",", aheadSnapshot.Commits.Select(commit => commit.Id)));

            HistoryComparisonBehindButton_Click(HistoryComparisonBehindButton, new RoutedEventArgs());
            await WaitForDiagnosticIdleAsync("behind comparison restore");
            behindSnapshot = historyComparisonSnapshot;
            Check(report, behindSnapshot is not null && behindSnapshot.Mode == ComparisonMode.Behind, "behind comparison restored", FormatComparisonSnapshot(behindSnapshot));
            Check(report, CommitIdsMatch(behindSnapshot!.Commits, manifest.FeatureExclusive), "behind commit IDs match independent fixture", string.Join(",", behindSnapshot.Commits.Select(commit => commit.Id)));

            HistoryBranchFilterBox.Text = "  FeAtUrE  ";
            await WaitForLayoutAsync();
            Check(report, filteredHistoryComparisonBranchRows.Count == 1
                && string.Equals(filteredHistoryComparisonBranchRows[0].Name, "feature", StringComparison.OrdinalIgnoreCase), "branch filter trims and ignores case", string.Join(",", filteredHistoryComparisonBranchRows.Select(row => row.Name)));
            HistoryBranchFilterBox.Text = "does-not-match";
            await WaitForLayoutAsync();
            Check(report, filteredHistoryComparisonBranchRows.Count == 0, "branch filter hides nonmatching branches", $"rows={filteredHistoryComparisonBranchRows.Count}");
            Check(report, HistoryComparisonBranchList.SelectedIndex == -1 && selectedHistoryComparisonBranch is null, "nonmatch clears selected branch", $"selectedIndex={HistoryComparisonBranchList.SelectedIndex}; selected={selectedHistoryComparisonBranch?.Name ?? "<null>"}");
            Check(report, !historyComparisonActive && historyComparisonSnapshot is null && commitRows.Count == 0, "nonmatch clears comparison state", $"active={historyComparisonActive}; snapshot={historyComparisonSnapshot is not null}; commits={commitRows.Count}");
            Check(report, HistoryComparisonDetailsPanel.Visibility == Visibility.Collapsed, "nonmatch hides comparison details", HistoryComparisonDetailsPanel.Visibility.ToString());
            HistoryBranchFilterBox.Text = string.Empty;
            await WaitForLayoutAsync();
            Check(report, filteredHistoryComparisonBranchRows.Count == historyComparisonBranchRows.Count, "clearing branch filter restores rows", $"rows={filteredHistoryComparisonBranchRows.Count}");

            await SelectHistoryComparisonBranchForCheckAsync(featureRow, report);
            HistoryList.SelectedItems.Clear();
            await WaitForDiagnosticIdleAsync("clear history selection");
            var comparisonRows = commitRows.Take(2).ToArray();
            Check(report, comparisonRows.Length == 2, "consecutive comparison rows available", $"rows={commitRows.Count}");
            HistoryList.SelectedItems.Add(comparisonRows[0]);
            await WaitForDiagnosticIdleAsync("first history selection");
            HistoryList.SelectedItems.Add(comparisonRows[1]);
            await WaitForDiagnosticIdleAsync("consecutive history selection");
            var selectionSnapshot = historyCommitSelectionSnapshot;
            Check(report, selectionSnapshot is not null && selectionSnapshot.HasCombinedDiff, "consecutive selection has combined diff", FormatSelectionSnapshot(selectionSnapshot));
            Check(report, selectionSnapshot!.FirstSelectedCommitId.Equals(manifest.FeatureExclusive[0], StringComparison.OrdinalIgnoreCase)
                && selectionSnapshot.LastSelectedCommitId.Equals(manifest.FeatureExclusive[1], StringComparison.OrdinalIgnoreCase), "combined selection endpoints are oldest to newest", FormatSelectionSnapshot(selectionSnapshot));
            Check(report, selectionSnapshot.FirstParentBaselineId.Equals(independent.CommonRoot, StringComparison.OrdinalIgnoreCase), "combined selection baseline is merge base", selectionSnapshot.FirstParentBaselineId);
            Check(report, CommitFilePathsMatch(selectionSnapshot.ChangedFiles, independent.FeaturePaths), "combined file list matches independent Git", string.Join(",", selectionSnapshot.ChangedFiles.Select(file => file.Path)));
            var comparisonFile = commitFileRows.FirstOrDefault(row =>
                string.Equals(row.Path, manifest.ComparisonFile, StringComparison.Ordinal));
            Check(report, comparisonFile is not null, "combined comparison file available", string.Join(",", commitFileRows.Select(row => row.Path)));
            HistoryFilesList.SelectedIndex = -1;
            await WaitForDiagnosticIdleAsync("clear combined file selection");
            HistoryFilesList.SelectedItem = comparisonFile;
            await WaitForDiagnosticIdleAsync("combined comparison file diff");
            var independentDiff = await RunDiagnosticGitAsync(
                normalizedFixtureParent,
                diagnosticRoot,
                [
                    "diff",
                    "--no-ext-diff",
                    "--no-textconv",
                    "--no-color",
                    "--binary",
                    "--patch",
                    "--unified=3",
                    "--find-renames",
                    "--find-copies",
                    independent.CommonRoot,
                    independent.FeatureHead,
                    "--",
                    manifest.ComparisonFile,
                ],
                gitEnvironment);
            var expectedHunks = NormalizeGitHunks(independentDiff);
            var renderedHunks = NormalizeRenderedHunks(historyDiffRows);
            Check(report, expectedHunks.Count > 0, "independent combined diff has hunks", independentDiff.Trim());
            Check(report, renderedHunks.Count > 0, "combined diff rendered", $"rows={historyDiffRows.Count}");
            Check(report, renderedHunks.SequenceEqual(expectedHunks, StringComparer.Ordinal), "native combined diff matches independent Git", $"expected={string.Join("\\n", expectedHunks)}; actual={string.Join("\\n", renderedHunks)}");
            report.AppendLine($"combined-diff-endpoints={selectionSnapshot.FirstParentBaselineId}..{selectionSnapshot.LastSelectedCommitId}");

            ClearHistoryComparisonButton_Click(ClearHistoryComparisonButton, new RoutedEventArgs());
            await WaitForDiagnosticIdleAsync("clear comparison");
            Check(report, !historyComparisonActive && historyComparisonSnapshot is null, "clear comparison removes snapshot", $"active={historyComparisonActive}; snapshot={historyComparisonSnapshot is not null}");
            Check(report, HistoryComparisonBranchList.SelectedIndex == -1, "clear comparison clears branch selection", $"selectedIndex={HistoryComparisonBranchList.SelectedIndex}");
            Check(report, HistoryComparisonDetailsPanel.Visibility == Visibility.Collapsed, "clear comparison hides details", HistoryComparisonDetailsPanel.Visibility.ToString());
            Check(report, HistoryComparisonSummaryText.Text.Length == 0 && HistoryComparisonEndpointsText.Text.Length == 0, "clear comparison clears summary", $"summary={HistoryComparisonSummaryText.Text}; endpoints={HistoryComparisonEndpointsText.Text}");

            await SelectHistoryComparisonBranchForCheckAsync(featureRow, report);
            var mainHeadBeforeMove = historyComparisonSnapshot!.BaseHeadId;
            var featureHeadBeforeMove = historyComparisonSnapshot.ComparisonHeadId;
            var movedFeatureHead = await MoveDiagnosticFeatureBranchAsync(
                diagnosticRoot,
                gitEnvironment,
                normalizedFixtureParent,
                manifest.ComparisonFile,
                report);
            Check(report, movedFeatureHead is not null && !movedFeatureHead.Equals(featureHeadBeforeMove, StringComparison.OrdinalIgnoreCase), "diagnostic comparison branch moved", $"before={featureHeadBeforeMove}; after={movedFeatureHead}");
            RefreshButton_Click(RefreshButton, new RoutedEventArgs());
            await WaitForDiagnosticIdleAsync("refresh after comparison branch move");
            var refreshedMainHead = await RunDiagnosticGitAsync(
                normalizedFixtureParent,
                diagnosticRoot,
                ["rev-parse", "HEAD"],
                gitEnvironment);
            Check(report, refreshedMainHead.Equals(mainHeadBeforeMove, StringComparison.OrdinalIgnoreCase), "refresh keeps current HEAD unchanged", refreshedMainHead);
            Check(report, !historyComparisonActive && historyComparisonSnapshot is null, "refresh clears moved comparison", $"active={historyComparisonActive}; snapshot={historyComparisonSnapshot is not null}");
            Check(report, HistoryComparisonBranchList.SelectedIndex == -1 && commitRows.Count == 0, "refresh clears stale comparison rows", $"selectedIndex={HistoryComparisonBranchList.SelectedIndex}; commits={commitRows.Count}");
            Check(report, HistoryComparisonDetailsPanel.Visibility == Visibility.Collapsed, "refresh hides stale comparison details", HistoryComparisonDetailsPanel.Visibility.ToString());

            var secondRepository = diagnosticRoot + "-switch-target";
            EnsurePathWithin(normalizedFixtureParent, secondRepository, "repository switch target");
            await RunDiagnosticGitAsync(
                diagnosticRoot,
                diagnosticRoot,
                ["clone", "--no-local", "--quiet", diagnosticRoot, secondRepository],
                gitEnvironment);
            await ConfigureDiagnosticRepositoryAsync(
                secondRepository,
                Path.Combine(secondRepository, ".hooks-disabled"),
                gitEnvironment,
                normalizedFixtureParent);
            await SelectHistoryComparisonBranchForCheckAsync(featureRow, report);
            await OpenRepositoryAsync(secondRepository);
            await WaitForLayoutAsync();
            Check(report, SamePath(repositoryRoot, secondRepository), "repository switch changes root", repositoryRoot ?? "<null>");
            Check(report, !historyComparisonActive && historyComparisonSnapshot is null, "repository switch clears comparison", $"active={historyComparisonActive}; snapshot={historyComparisonSnapshot is not null}");
            Check(report, historyComparisonBranchRows.Count == 0 && HistoryComparisonBranchList.SelectedIndex == -1, "repository switch clears branch rows", $"rows={historyComparisonBranchRows.Count}; selectedIndex={HistoryComparisonBranchList.SelectedIndex}");

            await OpenRepositoryAsync(diagnosticRoot);
            MainNavigation.SelectedItem = MainNavigation.MenuItems[1];
            await WaitForDiagnosticIdleAsync("return to history after repository switch");
            var finalFeatureRow = historyComparisonBranchRows.FirstOrDefault(row => string.Equals(row.Name, "feature", StringComparison.OrdinalIgnoreCase));
            Check(report, finalFeatureRow is not null, "navigation fixture branch available", string.Join(",", historyComparisonBranchRows.Select(row => row.Name)));
            await SelectHistoryComparisonBranchForCheckAsync(finalFeatureRow!, report);
            MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
            await WaitForDiagnosticIdleAsync("navigation switch to changes");
            Check(report, currentWorkspace == "changes", "navigation switch changes workspace", currentWorkspace);
            Check(report, ChangesWorkspace.Visibility == Visibility.Visible && HistoryWorkspace.Visibility == Visibility.Collapsed, "navigation switch updates visible workspace", $"changes={ChangesWorkspace.Visibility}; history={HistoryWorkspace.Visibility}");
            Check(report, !historyComparisonActive && historyComparisonSnapshot is null, "navigation switch clears comparison", $"active={historyComparisonActive}; snapshot={historyComparisonSnapshot is not null}");

            await RunDiagnosticGitAsync(
                normalizedFixtureParent,
                diagnosticRoot,
                ["branch", "--force", "feature", manifest.FeatureHead],
                gitEnvironment);
            await OpenRepositoryAsync(diagnosticRoot);
            MainNavigation.SelectedItem = MainNavigation.MenuItems[1];
            await WaitForDiagnosticIdleAsync("navigate to final history capture");
            finalFeatureRow = historyComparisonBranchRows.FirstOrDefault(row => string.Equals(row.Name, "feature", StringComparison.OrdinalIgnoreCase));
            await SelectHistoryComparisonBranchForCheckAsync(finalFeatureRow!, report);
            Check(report, historyComparisonSnapshot!.BaseHeadId.Equals(manifest.MainHead, StringComparison.OrdinalIgnoreCase)
                && historyComparisonSnapshot.ComparisonHeadId.Equals(manifest.FeatureHead, StringComparison.OrdinalIgnoreCase), "final capture restores fixture endpoints", FormatComparisonSnapshot(historyComparisonSnapshot));
            HistoryList.SelectedItems.Clear();
            await WaitForDiagnosticIdleAsync("final clear history selection");
            var finalRows = commitRows.Take(2).ToArray();
            HistoryList.SelectedItems.Add(finalRows[0]);
            await WaitForDiagnosticIdleAsync("final first history selection");
            HistoryList.SelectedItems.Add(finalRows[1]);
            await WaitForDiagnosticIdleAsync("final consecutive history selection");
            var finalFile = commitFileRows.FirstOrDefault(row => string.Equals(row.Path, manifest.ComparisonFile, StringComparison.Ordinal));
            HistoryFilesList.SelectedIndex = -1;
            await WaitForDiagnosticIdleAsync("final clear file selection");
            HistoryFilesList.SelectedItem = finalFile;
            await WaitForDiagnosticIdleAsync("final combined diff");

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

    private async Task SelectHistoryComparisonBranchForCheckAsync(
        HistoryComparisonBranchRow branch,
        StringBuilder report)
    {
        if (HistoryComparisonBranchList.SelectedItem is not null)
        {
            HistoryComparisonBranchList.SelectedIndex = -1;
            await WaitForDiagnosticIdleAsync("clear comparison branch selection");
        }

        HistoryComparisonBranchList.SelectedItem = branch;
        await WaitForDiagnosticIdleAsync($"select {branch.Name} comparison branch");
        Check(report, ReferenceEquals(selectedHistoryComparisonBranch, branch), "comparison branch selection handler", selectedHistoryComparisonBranch?.Name ?? "<null>");
        Check(report, historyComparisonActive && historyComparisonSnapshot is not null, "comparison branch selection loads snapshot", FormatComparisonSnapshot(historyComparisonSnapshot));
    }

    private async Task ConfigureDiagnosticRepositoryAsync(
        string repositoryPath,
        string hooksPath,
        IReadOnlyDictionary<string, string> gitEnvironment,
        string guardRoot)
    {
        EnsurePathWithin(guardRoot, repositoryPath, "diagnostic repository");
        EnsurePathWithin(repositoryPath, hooksPath, "diagnostic hooks");
        Directory.CreateDirectory(hooksPath);
        await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["config", "--local", "user.name", "Test User"], gitEnvironment);
        await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["config", "--local", "user.email", "synthetic@example.invalid"], gitEnvironment);
        await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["config", "--local", "commit.gpgSign", "false"], gitEnvironment);
        await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["config", "--local", "tag.gpgSign", "false"], gitEnvironment);
        await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["config", "--local", "core.hooksPath", hooksPath], gitEnvironment);
    }

    private async Task<string> MoveDiagnosticFeatureBranchAsync(
        string repositoryPath,
        IReadOnlyDictionary<string, string> gitEnvironment,
        string guardRoot,
        string comparisonFile,
        StringBuilder report)
    {
        var mainBefore = await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["rev-parse", "refs/heads/main"], gitEnvironment);
        var featureBefore = await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["rev-parse", "refs/heads/feature"], gitEnvironment);
        await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["switch", "feature"], gitEnvironment);
        var mutationPath = Path.Combine(repositoryPath, "feature-exclusive-three.txt");
        EnsurePathWithin(repositoryPath, mutationPath, "feature branch mutation file");
        await File.WriteAllTextAsync(mutationPath, "feature branch commit three\n");
        await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["add", "--all"], gitEnvironment);
        await RunDiagnosticGitAsync(
            guardRoot,
            repositoryPath,
            ["commit", "--no-verify", "--no-gpg-sign", "--message", "Feature exclusive change three"],
            gitEnvironment);
        var featureAfter = await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["rev-parse", "refs/heads/feature"], gitEnvironment);
        Check(report, File.Exists(mutationPath), "diagnostic mutation file is scoped", mutationPath);
        await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["switch", "main"], gitEnvironment);
        var mainAfter = await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["rev-parse", "refs/heads/main"], gitEnvironment);
        Check(report, mainBefore.Equals(mainAfter, StringComparison.OrdinalIgnoreCase), "diagnostic mutation preserves main endpoint", $"before={mainBefore}; after={mainAfter}");
        Check(report, !featureBefore.Equals(featureAfter, StringComparison.OrdinalIgnoreCase), "diagnostic mutation advances feature endpoint", $"before={featureBefore}; after={featureAfter}");
        Check(report, !File.Exists(mutationPath), "diagnostic mutation file absent on main", mutationPath);
        report.AppendLine($"diagnostic-feature-move={featureBefore}->{featureAfter}; current-head={mainAfter}; file={comparisonFile}");
        return featureAfter;
    }

    private async Task<(
        string MainHead,
        string FeatureHead,
        string CommonRoot,
        int Ahead,
        int Behind,
        HashSet<string> MainPaths,
        HashSet<string> FeaturePaths)> ReadIndependentComparisonAsync(
        string repositoryPath,
        IReadOnlyDictionary<string, string> gitEnvironment,
        string guardRoot,
        HistoryComparisonCheckManifest manifest)
    {
        var mainHead = await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["rev-parse", "refs/heads/main"], gitEnvironment);
        var featureHead = await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["rev-parse", "refs/heads/feature"], gitEnvironment);
        var commonRoot = await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["merge-base", "refs/heads/main", "refs/heads/feature"], gitEnvironment);
        var countsText = await RunDiagnosticGitAsync(guardRoot, repositoryPath, ["rev-list", "--left-right", "--count", "refs/heads/main...refs/heads/feature"], gitEnvironment);
        var counts = countsText.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (counts.Length != 2 || !int.TryParse(counts[0], out var ahead) || !int.TryParse(counts[1], out var behind))
        {
            throw new InvalidOperationException($"Git returned invalid comparison counts: {countsText}");
        }

        var mainPaths = await ReadGitPathSetAsync(
            guardRoot,
            repositoryPath,
            ["diff", "--name-only", commonRoot, mainHead, "--"],
            gitEnvironment);
        var featurePaths = await ReadGitPathSetAsync(
            guardRoot,
            repositoryPath,
            ["diff", "--name-only", commonRoot, featureHead, "--"],
            gitEnvironment);
        return (mainHead, featureHead, commonRoot, ahead, behind, mainPaths, featurePaths);
    }

    private static async Task<HashSet<string>> ReadGitPathSetAsync(
        string guardRoot,
        string repositoryPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> gitEnvironment)
    {
        var output = await RunDiagnosticGitAsync(guardRoot, repositoryPath, arguments, gitEnvironment);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<string> RunDiagnosticGitAsync(
        string guardRoot,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        int expectedExitCode = 0)
    {
        EnsurePathWithin(guardRoot, workingDirectory, "diagnostic Git working directory");
        var startInfo = new ProcessStartInfo
        {
            FileName = "git.exe",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Git did not start for the history comparison diagnostic.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var result = new DiagnosticGitResult(
            process.ExitCode,
            (await stdoutTask).Trim(),
            (await stderrTask).Trim());
        if (result.ExitCode != expectedExitCode)
        {
            throw new InvalidOperationException(
                $"Git command failed in '{workingDirectory}' with exit code {result.ExitCode}: {result.StandardError}");
        }

        return result.StandardOutput;
    }

    private async Task WaitForDiagnosticIdleAsync(string operationName)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            await WaitForLatestOperationAsync();
            if (!BusyRing.IsActive)
            {
                await WaitForLayoutAsync();
                if (!BusyRing.IsActive)
                {
                    return;
                }
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"The native UI did not settle after {operationName}.");
    }

    private static IReadOnlyList<string> NormalizeGitHunks(string patch)
    {
        var hunks = new List<string>();
        var inHunk = false;
        foreach (var line in patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) && inHunk)
            {
                break;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
            }

            if (inHunk)
            {
                hunks.Add(line);
            }
        }

        return hunks;
    }

    private static IReadOnlyList<string> NormalizeRenderedHunks(IEnumerable<DiffRow> rows) =>
        rows.Select(row => row.Line.Kind switch
            {
                DiffLineKind.HunkHeader => row.Text,
                DiffLineKind.Context => $" {row.Text}",
                DiffLineKind.Added => $"+{row.Text}",
                DiffLineKind.Removed => $"-{row.Text}",
                DiffLineKind.NoNewline => row.Text,
                _ => null,
            })
            .Where(line => line is not null)
            .Select(line => line!)
            .ToArray();

    private static HistoryComparisonCheckManifest ReadHistoryComparisonCheckManifest(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var schemaVersion = GetManifestValue(root, "schemaVersion").GetInt32();
        if (schemaVersion != 1)
        {
            throw new InvalidOperationException($"Unsupported history comparison fixture manifest schema: {schemaVersion}.");
        }

        return new HistoryComparisonCheckManifest(
            GetManifestString(root, "runRoot"),
            GetManifestString(root, "repositoryRoot"),
            GetManifestString(root, "branches", "main"),
            GetManifestString(root, "branches", "feature"),
            GetManifestString(root, "commitGraph", "commonRoot"),
            GetManifestStringArray(root, "commitGraph", "mainExclusive"),
            GetManifestStringArray(root, "commitGraph", "featureExclusive"),
            GetManifestString(root, "comparisonFile"),
            GetManifestStringArray(root, "mainExclusivePaths"),
            GetManifestStringArray(root, "featureExclusivePaths"),
            GetManifestString(root, "launchEnvironment", "GIT_CONFIG_GLOBAL"),
            GetManifestString(root, "launchEnvironment", "GIT_CONFIG_NOSYSTEM"),
            GetManifestString(root, "launchEnvironment", "GIT_TERMINAL_PROMPT"));
    }

    private static string GetManifestString(JsonElement root, params string[] path)
    {
        var value = GetManifestValue(root, path);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidOperationException($"Fixture manifest value '{string.Join('.', path)}' is missing.");
    }

    private static IReadOnlyList<string> GetManifestStringArray(JsonElement root, params string[] path)
    {
        var value = GetManifestValue(root, path);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Fixture manifest value '{string.Join('.', path)}' is not an array.");
        }

        return value.EnumerateArray()
            .Select(entry => entry.ValueKind == JsonValueKind.String
                ? entry.GetString()
                : throw new InvalidOperationException($"Fixture manifest array '{string.Join('.', path)}' contains a non-string value."))
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry!)
            .ToArray();
    }

    private static JsonElement GetManifestValue(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                throw new InvalidOperationException($"Fixture manifest value '{string.Join('.', path)}' is missing.");
            }
        }

        return current;
    }

    private static bool CommitIdsMatch(IReadOnlyList<CommitSummary> commits, IReadOnlyList<string> expectedIds) =>
        commits.Select(commit => commit.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(expectedIds);

    private static bool CommitFilePathsMatch(IReadOnlyList<FileChange> files, IEnumerable<string> expectedPaths) =>
        files.Select(file => file.Path.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal)
            .SetEquals(expectedPaths.Select(path => path.Replace('\\', '/')));

    private static string FormatComparisonSnapshot(BranchComparisonSnapshot? snapshot) => snapshot is null
        ? "<null>"
        : $"mode={snapshot.Mode}; base={snapshot.BaseHeadId}; comparison={snapshot.ComparisonHeadId}; ahead={snapshot.Ahead}; behind={snapshot.Behind}; commits={snapshot.Commits.Count}; range={snapshot.RevisionRange}";

    private static string FormatSelectionSnapshot(CommitSelectionSnapshot? snapshot) => snapshot is null
        ? "<null>"
        : $"first={snapshot.FirstSelectedCommitId}; baseline={snapshot.FirstParentBaselineId}; last={snapshot.LastSelectedCommitId}; contiguous={snapshot.IsContiguous}; files={snapshot.ChangedFiles.Count}";

    private static void EnsurePathWithin(string parent, string child, string label)
    {
        var normalizedParent = NormalizeDirectoryForComparison(parent);
        var normalizedChild = NormalizeDirectoryForComparison(child);
        var parentPrefix = normalizedParent + Path.DirectorySeparatorChar;
        if (!string.Equals(normalizedParent, normalizedChild, StringComparison.OrdinalIgnoreCase)
            && !normalizedChild.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{label} escaped its guarded fixture directory: {child}");
        }
    }
}
