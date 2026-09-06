using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;
using WinGit.Core.Codex;
using Windows.Foundation;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private const int SelectedReviewContextLineRadius = 3;
    private static readonly Regex SelectedReviewHunkPattern = new(
        "^@@ -(?<old>\\d+)(?:,\\d+)? \\+(?<new>\\d+)(?:,\\d+)? @@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ObservableCollection<SelectedChangesReviewFindingRow>
        selectedChangesReviewFindingRows = [];
    private bool selectedChangesReviewControlsInitialized;
    private long selectedChangesReviewRequestId;
    private CancellationTokenSource? selectedChangesReviewCancellation;
    private TaskCompletionSource<bool>? selectedChangesReviewCompletion;
    private ContentDialog? selectedChangesReviewDialog;
    private bool selectedChangesReviewDialogClosed;
    private SelectedChangesReviewSnapshot? selectedChangesReviewSnapshot;

    /// <summary>
    /// Installs the whole-file review command beside the existing native
    /// change menus. It deliberately does not replace the discard flyouts or
    /// bind review to partial-diff row coordinates.
    /// </summary>
    private void InitializeSelectedChangesReviewControls()
    {
        if (selectedChangesReviewControlsInitialized)
        {
            return;
        }

        selectedChangesReviewControlsInitialized = true;
        StagedChangesList.SelectionChanged += SelectedReview_SelectionChanged;
        UnstagedChangesList.SelectionChanged += SelectedReview_SelectionChanged;
        MainNavigation.SelectionChanged += SelectedReview_NavigationChanged;
        CodexModelComboBox.SelectionChanged += SelectedReview_ModelChanged;
        CodexReasoningComboBox.SelectionChanged += SelectedReview_ModelChanged;
        OpenRepositoryButton.Click += SelectedReview_RepositoryChanging;
        RefreshButton.Click += SelectedReview_RepositoryChanging;
        Closed += SelectedReview_Closed;

        var workspaceMenu = ChangesWorkspace.ContextFlyout as MenuFlyout
            ?? new MenuFlyout();
        AddSelectedReviewMenuItem(workspaceMenu);
        ChangesWorkspace.ContextFlyout = workspaceMenu;

        // The discard list menus are created before this hook. Add a sibling
        // action to those list menus without replacing their existing items.
        AddSelectedReviewMenuItem(StagedChangesList.ContextFlyout as MenuFlyout);
        AddSelectedReviewMenuItem(UnstagedChangesList.ContextFlyout as MenuFlyout);
    }

    private void AddSelectedReviewMenuItem(MenuFlyout? menu)
    {
        if (menu is null || menu.Items.OfType<MenuFlyoutItem>().Any(item =>
                string.Equals(item.Text, "Review selected files with Codex…", StringComparison.Ordinal)))
        {
            return;
        }

        var item = new MenuFlyoutItem
        {
            Text = "Review selected files with Codex…",
        };
        AutomationProperties.SetName(item, "Review selected files with Codex");
        item.Click += (_, _) => _ = StartSelectedChangesReviewAsync();
        menu.Opening += (_, _) => item.IsEnabled = CanStartSelectedChangesReview();
        menu.Items.Add(item);
    }

    private bool CanStartSelectedChangesReview() =>
        !diagnosticCaptureMode
        && repositoryRoot is not null
        && currentWorkspace == "changes"
        && ChangesWorkspace.Visibility == Visibility.Visible
        && !mutationInProgress
        && !BusyRing.IsActive
        && activeGitOperationKind == GitOperationKind.None
        && selectedChangesReviewCancellation is null
        && partialSelections.Count == 0
        && GetSelectedWholeReviewFiles().Count > 0;

    private IReadOnlyList<FileChange> GetSelectedWholeReviewFiles()
    {
        var files = new List<FileChange>();
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        foreach (var row in StagedChangesList.SelectedItems.OfType<ChangeRow>()
                     .Concat(UnstagedChangesList.SelectedItems.OfType<ChangeRow>()))
        {
            var key = row.Path + "\0" + (row.Change.OldPath ?? string.Empty);
            if (seen.Add(key))
            {
                files.Add(row.Change);
            }
        }

        return files.AsReadOnly();
    }

    private async Task StartSelectedChangesReviewAsync()
    {
        if (repositoryRoot is null
            || diagnosticCaptureMode
            || currentWorkspace != "changes"
            || ChangesWorkspace.Visibility != Visibility.Visible
            || mutationInProgress
            || BusyRing.IsActive
            || activeGitOperationKind != GitOperationKind.None
            || selectedChangesReviewCancellation is not null)
        {
            if (partialSelections.Count > 0)
            {
                ShowError(
                    "Selected review unavailable",
                    new InvalidOperationException(
                        "Clear the staging line selection before starting a selected review."));
            }

            return;
        }

        if (partialSelections.Count > 0)
        {
            ShowError(
                "Selected review unavailable",
                new InvalidOperationException(
                    "Clear the staging line selection before starting a selected review."));
            return;
        }

        var files = GetSelectedWholeReviewFiles();
        if (files.Count == 0)
        {
            ShowError(
                "No files selected",
                new InvalidOperationException(
                    "Select one or more staged or unstaged files before starting a review."));
            return;
        }

        if (files.Any(IsConflictChange))
        {
            ShowError(
                "Selected review unavailable",
                new InvalidOperationException(
                    "Resolve conflicted files before sending a selected review."));
            return;
        }

        if (codexAccount.Status != CodexAccountStatus.SignedIn)
        {
            ShowError(
                "Codex sign-in required",
                new InvalidOperationException(
                    "Sign in to Codex in Settings before reviewing selected changes."));
            return;
        }

        if (CodexModelComboBox.SelectedItem is not CodexModelRow modelRow)
        {
            ShowError(
                "Codex model unavailable",
                new InvalidOperationException(
                    "Refresh the Codex model list in Settings before reviewing selected changes."));
            return;
        }

        CancelCommitMessageGeneration();
        var root = repositoryRoot;
        var modelId = modelRow.Model.Id;
        var modelSlug = modelRow.Model.Model;
        var reasoningEffort =
            (CodexReasoningComboBox.SelectedItem as CodexReasoningRow)?.ReasoningEffort;
        var requestId = ++selectedChangesReviewRequestId;
        var cancellation = new CancellationTokenSource();
        selectedChangesReviewCancellation = cancellation;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        selectedChangesReviewCompletion = completion;
        selectedChangesReviewSnapshot = null;
        selectedChangesReviewFindingRows.Clear();
        try
        {
            IReadOnlyList<SelectedChangesReviewSelectionFileState>? selectionStates = null;
            var previewOperation = BeginOperation("Reading selected review diffs…");
            using (var previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                previewOperation.Token,
                cancellation.Token))
            {
                try
                {
                    var states = new List<SelectedChangesReviewSelectionFileState>(files.Count);
                    foreach (var file in files)
                    {
                        previewCancellation.Token.ThrowIfCancellationRequested();
                        var diffSnapshot = await repositoryService.GetSelectedChangesReviewDiffAsync(
                            root,
                            file,
                            previewCancellation.Token);
                        states.Add(new SelectedChangesReviewSelectionFileState(file, diffSnapshot));
                    }

                    selectionStates = states.AsReadOnly();
                    if (!IsSelectedReviewRequestCurrent(
                            requestId,
                            root,
                            files,
                            modelId,
                            modelSlug,
                            reasoningEffort,
                            cancellation.Token))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (
                    cancellation.IsCancellationRequested
                    || previewOperation.Token.IsCancellationRequested)
                {
                    return;
                }
                catch (SelectedChangesReviewSnapshotStaleException exception)
                {
                    ShowError("Selected changes changed", exception);
                    return;
                }
                catch (SelectedChangesReviewSnapshotException exception)
                {
                    ShowError("Unable to read selected changes", exception);
                    return;
                }
                catch (Exception exception)
                {
                    ShowError("Unable to read selected changes", exception);
                    return;
                }
                finally
                {
                    EndOperation(previewOperation.Generation);
                }
            }

            if (selectionStates is null
                || !IsSelectedReviewRequestCurrent(
                    requestId,
                    root,
                    files,
                    modelId,
                    modelSlug,
                    reasoningEffort,
                    cancellation.Token))
            {
                return;
            }

            var selections = await ShowSelectedChangesReviewSelectionAsync(
                requestId,
                root,
                files,
                modelId,
                modelSlug,
                reasoningEffort,
                selectionStates,
                cancellation.Token);
            if (selections is null
                || !IsSelectedReviewRequestCurrent(
                    requestId,
                    root,
                    files,
                    modelId,
                    modelSlug,
                    reasoningEffort,
                    cancellation.Token))
            {
                return;
            }

            SelectedChangesReviewSnapshot? snapshot = null;
            var operation = BeginOperation("Capturing selected changes…");
            using (var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                operation.Token,
                cancellation.Token))
            {
                try
                {
                    if (!await RevalidateSelectedReviewSelectionPreviewsAsync(
                            root,
                            selectionStates,
                            captureCancellation.Token))
                    {
                        ShowError(
                            "Selected changes changed",
                            new InvalidOperationException(
                                "The selected files changed while you were choosing review lines. Refresh and try again."));
                        return;
                    }

                    snapshot = await repositoryService.GetSelectedChangesReviewSnapshotAsync(
                        root,
                        selections,
                        captureCancellation.Token);
                    if (!IsSelectedReviewRequestCurrent(
                            requestId,
                            root,
                            files,
                            modelId,
                            modelSlug,
                            reasoningEffort,
                            cancellation.Token))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (
                    cancellation.IsCancellationRequested
                    || operation.Token.IsCancellationRequested)
                {
                    return;
                }
                catch (SelectedChangesReviewSnapshotStaleException exception)
                {
                    ShowError("Selected changes changed", exception);
                    return;
                }
                catch (SelectedChangesReviewSnapshotException exception)
                {
                    ShowError("Unable to capture selected changes", exception);
                    return;
                }
                catch (Exception exception)
                {
                    ShowError("Unable to capture selected changes", exception);
                    return;
                }
                finally
                {
                    EndOperation(operation.Generation);
                }
            }

            if (snapshot is null
                || !IsSelectedReviewRequestCurrent(
                    requestId,
                    root,
                    files,
                    modelId,
                    modelSlug,
                    reasoningEffort,
                    cancellation.Token))
            {
                return;
            }

            var reviewFiles = selections.Select(selection => selection.File).ToArray();

            // Selected reviews deliberately use a separate, per-repository
            // consent. The commit-message receipt promises that unstaged
            // files are not sent, while this review can include them.
            var reviewConsentRequired =
                !HasSelectedChangesReviewConsent(root);
            if (reviewConsentRequired &&
                !await ShowSelectedReviewConsentAsync(
                    requestId,
                    root,
                    reviewFiles,
                    files,
                    modelId,
                    modelSlug,
                    reasoningEffort,
                    cancellation.Token))
            {
                return;
            }

            if (!IsSelectedReviewRequestCurrent(
                    requestId,
                    root,
                    files,
                    modelId,
                    modelSlug,
                    reasoningEffort,
                    cancellation.Token))
            {
                return;
            }

            bool snapshotStillCurrent;
            try
            {
                snapshotStillCurrent = await repositoryService.RevalidateSelectedChangesReviewSnapshotAsync(
                    root,
                    snapshot,
                    cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ShowError("Unable to verify selected changes", exception);
                return;
            }

            if (!snapshotStillCurrent)
            {
                ShowError(
                    "Selected changes changed",
                    new InvalidOperationException(
                        "The selected files changed while confirmation was open. Refresh and review them again."));
                return;
            }

            if (reviewConsentRequired)
            {
                AcknowledgeSelectedChangesReviewConsent(root);
            }

            selectedChangesReviewSnapshot = snapshot;
            await RunSelectedChangesReviewGenerationAsync(
                requestId,
                root,
                reviewFiles,
                files,
                snapshot,
                modelId,
                modelSlug,
                reasoningEffort,
                cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The dialog owns the visible cancellation state. Repository and
            // window lifecycle invalidation simply closes stale dialogs.
        }
        finally
        {
            if (ReferenceEquals(selectedChangesReviewCancellation, cancellation))
            {
                selectedChangesReviewCancellation = null;
            }

            cancellation.Dispose();
            completion.TrySetResult(true);
            if (ReferenceEquals(selectedChangesReviewCompletion, completion))
            {
                selectedChangesReviewCompletion = null;
            }
        }
    }

    private bool HasSelectedChangesReviewConsent(string root) =>
        NativeSettingsStore.HasSelectedChangesReviewConsent(settings, root);

    private void AcknowledgeSelectedChangesReviewConsent(string root)
    {
        NativeSettingsStore.AcknowledgeSelectedChangesReviewConsent(settings, root);
        _ = SaveSettingsAsync();
    }

    private async Task<bool> ShowSelectedReviewConsentAsync(
        long requestId,
        string root,
        IReadOnlyList<FileChange> includedFiles,
        IReadOnlyList<FileChange> requestFiles,
        string modelId,
        string modelSlug,
        string? reasoningEffort,
        CancellationToken cancellationToken)
    {
        var pathList = string.Join(
            Environment.NewLine,
            includedFiles.Take(8).Select(file => $"• {file.Path}"));
        if (includedFiles.Count > 8)
        {
            pathList += $"{Environment.NewLine}• … and {includedFiles.Count - 8} more";
        }

        var dialog = CreateDialog(
            "Review Codex data sharing",
            "Allow and review",
            new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "WinGit will send the selected whole-file or partial HEAD-to-working-tree diff to OpenAI through your local Codex account. The selection can include unstaged changes.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = $"Included paths ({includedFiles.Count} file{(includedFiles.Count == 1 ? string.Empty : "s")}):",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = pathList,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                    },
                    new TextBlock
                    {
                        Text = "The previous selection dialog showed the whole files and any chosen hunks or lines included here. This review does not write the repository, stage or discard changes, or alter the commit draft.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "Only this immutable diff is included. Codex does not inspect the repository or use tools for this request.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            });
        selectedChangesReviewDialog = dialog;
        selectedChangesReviewDialogClosed = false;
        try
        {
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary
                && IsSelectedReviewRequestCurrent(
                    requestId,
                    root,
                    requestFiles,
                    modelId,
                    modelSlug,
                    reasoningEffort,
                    cancellationToken);
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ShowError("Unable to show review confirmation", exception);
            }

            return false;
        }
        finally
        {
            if (ReferenceEquals(selectedChangesReviewDialog, dialog))
            {
                selectedChangesReviewDialog = null;
            }
        }
    }

    private async Task RunSelectedChangesReviewGenerationAsync(
        long requestId,
        string root,
        IReadOnlyList<FileChange> includedFiles,
        IReadOnlyList<FileChange> requestFiles,
        SelectedChangesReviewSnapshot snapshot,
        string modelId,
        string modelSlug,
        string? reasoningEffort,
        CancellationTokenSource cancellation)
    {
        var content = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 820,
        };
        var dialog = new ContentDialog
        {
            Title = "Review selected changes",
            PrimaryButtonText = "Cancel review",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
        };
        AutomationProperties.SetName(dialog, "Review selected changes");
        selectedChangesReviewDialog = dialog;
        selectedChangesReviewDialogClosed = false;
        RenderSelectedReviewPending(content, includedFiles.Count);

        var allowClose = false;
        TypedEventHandler<ContentDialog, ContentDialogButtonClickEventArgs>
            cancelHandler = (_, args) =>
            {
                if (allowClose)
                {
                    return;
                }

                args.Cancel = true;
                cancellation.Cancel();
        };
        dialog.PrimaryButtonClick += cancelHandler;

        TypedEventHandler<ContentDialog, ContentDialogClosingEventArgs>
            closingHandler = (_, args) =>
            {
                if (allowClose
                    || requestId != selectedChangesReviewRequestId
                    || !ReferenceEquals(selectedChangesReviewDialog, dialog))
                {
                    return;
                }

                // Escape, the window close gesture, and any non-button
                // dismissal must leave the dialog visible while the request
                // is canceled so the user gets an explicit terminal result.
                args.Cancel = true;
                cancellation.Cancel();
            };
        TypedEventHandler<ContentDialog, ContentDialogClosedEventArgs>
            closedHandler = (_, _) =>
            {
                selectedChangesReviewDialogClosed = true;
                if (!allowClose && requestId == selectedChangesReviewRequestId)
                {
                    cancellation.Cancel();
                }
            };
        dialog.Closing += closingHandler;
        dialog.Closed += closedHandler;

        IAsyncOperation<ContentDialogResult>? showOperation = null;
        try
        {
            showOperation = dialog.ShowAsync();
            var client = await EnsureCodexClientAsync(cancellation.Token);
            if (client is null)
            {
                throw new InvalidOperationException("The Codex app server is unavailable.");
            }

            if (!IsSelectedReviewRequestCurrent(
                    requestId,
                    root,
                    requestFiles,
                    modelId,
                    modelSlug,
                    reasoningEffort,
                    cancellation.Token))
            {
                allowClose = true;
                HideSelectedReviewDialog(dialog);
                return;
            }

            RenderSelectedReviewGenerating(content);
            var generator = new CodexSelectedChangesReviewGenerator(client);
            var findings = await generator.ReviewAsync(
                new CodexSelectedChangesReviewGenerationRequest(
                    snapshot.CodexSnapshot,
                    new CodexModelSelectionSnapshot(modelSlug, reasoningEffort)),
                cancellation.Token);

            if (!IsSelectedReviewRequestCurrent(
                    requestId,
                    root,
                    requestFiles,
                    modelId,
                    modelSlug,
                    reasoningEffort,
                    cancellation.Token))
            {
                allowClose = true;
                HideSelectedReviewDialog(dialog);
                return;
            }

            RenderSelectedReviewChecking(content);
            if (!await repositoryService.RevalidateSelectedChangesReviewSnapshotAsync(
                    root,
                    snapshot,
                    cancellation.Token))
            {
                selectedChangesReviewSnapshot = null;
                selectedChangesReviewFindingRows.Clear();
                allowClose = true;
                RenderSelectedReviewOutdated(content, dialog);
            }
            else if (IsSelectedReviewRequestCurrent(
                         requestId,
                         root,
                         requestFiles,
                         modelId,
                         modelSlug,
                         reasoningEffort,
                         cancellation.Token))
            {
                selectedChangesReviewSnapshot = snapshot;
                selectedChangesReviewFindingRows.Clear();
                foreach (var finding in findings)
                {
                    selectedChangesReviewFindingRows.Add(
                        new SelectedChangesReviewFindingRow(finding));
                }

                allowClose = true;
                RenderSelectedReviewComplete(
                    content,
                    dialog,
                    snapshot,
                    selectedChangesReviewFindingRows);
            }
            else
            {
                allowClose = true;
                HideSelectedReviewDialog(dialog);
                return;
            }
        }
        catch (CodexSelectedChangesReviewGenerationCancelledException)
        {
            if (IsSelectedReviewDialogCurrent(requestId, dialog))
            {
                allowClose = true;
                RenderSelectedReviewCancelled(content, dialog);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (IsSelectedReviewDialogCurrent(requestId, dialog))
            {
                allowClose = true;
                RenderSelectedReviewCancelled(content, dialog);
            }
        }
        catch (SelectedChangesReviewSnapshotStaleException)
        {
            if (IsSelectedReviewDialogCurrent(requestId, dialog))
            {
                selectedChangesReviewSnapshot = null;
                selectedChangesReviewFindingRows.Clear();
                allowClose = true;
                RenderSelectedReviewOutdated(content, dialog);
            }
        }
        catch (CodexSelectedChangesReviewGenerationException exception)
        {
            if (IsSelectedReviewDialogCurrent(requestId, dialog))
            {
                allowClose = true;
                RenderSelectedReviewError(content, dialog, exception.Message);
            }
        }
        catch (Exception exception)
        {
            if (IsSelectedReviewDialogCurrent(requestId, dialog))
            {
                allowClose = true;
                RenderSelectedReviewError(
                    content,
                    dialog,
                    GetCodexFailureMessage(exception));
            }
        }
        finally
        {
            dialog.PrimaryButtonClick -= cancelHandler;
            dialog.Closing -= closingHandler;
            dialog.Closed -= closedHandler;
        }

        if (showOperation is not null)
        {
            try
            {
                await showOperation;
            }
            catch (Exception exception)
            {
                if (!cancellation.IsCancellationRequested
                    && IsSelectedReviewDialogCurrent(requestId, dialog))
                {
                    ShowError("Unable to show selected review", exception);
                }
            }
        }

        if (ReferenceEquals(selectedChangesReviewDialog, dialog))
        {
            selectedChangesReviewDialog = null;
        }
    }

    private bool IsSelectedReviewRequestCurrent(
        long requestId,
        string root,
        IReadOnlyList<FileChange> files,
        string modelId,
        string modelSlug,
        string? reasoningEffort,
        CancellationToken cancellationToken) =>
        requestId == selectedChangesReviewRequestId
        && !cancellationToken.IsCancellationRequested
        && repositoryRoot is not null
        && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
        && currentWorkspace == "changes"
        && ChangesWorkspace.Visibility == Visibility.Visible
        && !mutationInProgress
        && activeGitOperationKind == GitOperationKind.None
        && codexAccount.Status == CodexAccountStatus.SignedIn
        && partialSelections.Count == 0
        && AreSelectedReviewFilesCurrent(files)
        && string.Equals(
            (CodexModelComboBox.SelectedItem as CodexModelRow)?.Model.Id,
            modelId,
            StringComparison.Ordinal)
        && string.Equals(
            (CodexModelComboBox.SelectedItem as CodexModelRow)?.Model.Model,
            modelSlug,
            StringComparison.Ordinal)
        && string.Equals(
            (CodexReasoningComboBox.SelectedItem as CodexReasoningRow)?.ReasoningEffort,
            reasoningEffort,
            StringComparison.Ordinal);

    private bool AreSelectedReviewFilesCurrent(IReadOnlyList<FileChange> expected)
    {
        var actual = GetSelectedWholeReviewFiles();
        if (actual.Count != expected.Count)
        {
            return false;
        }

        foreach (var file in expected)
        {
            if (!actual.Any(candidate => SelectedReviewFilesMatch(file, candidate)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SelectedReviewFilesMatch(FileChange expected, FileChange actual) =>
        string.Equals(expected.Path, actual.Path, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.OldPath, actual.OldPath, StringComparison.OrdinalIgnoreCase)
        && expected.Kind == actual.Kind
        && string.Equals(expected.IndexStatus, actual.IndexStatus, StringComparison.Ordinal)
        && string.Equals(expected.WorkTreeStatus, actual.WorkTreeStatus, StringComparison.Ordinal);

    private bool IsSelectedReviewDialogCurrent(long requestId, ContentDialog dialog) =>
        requestId == selectedChangesReviewRequestId
        && ReferenceEquals(selectedChangesReviewDialog, dialog)
        && !selectedChangesReviewDialogClosed;

    private void RenderSelectedReviewPending(StackPanel content, int fileCount)
    {
        content.Children.Clear();
        content.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Reviewing the selected changes…",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Codex is reviewing {fileCount} immutable file diff{(fileCount == 1 ? string.Empty : "s")}. The review does not change your commit, index, or working tree.",
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private static void RenderSelectedReviewGenerating(StackPanel content)
    {
        if (content.Children.Count > 1
            && content.Children[1] is TextBlock title)
        {
            title.Text = "Codex is reviewing the selected changes…";
        }
    }

    private static void RenderSelectedReviewChecking(StackPanel content)
    {
        if (content.Children.Count > 1
            && content.Children[1] is TextBlock title)
        {
            title.Text = "Checking the immutable review result…";
        }
    }

    private void RenderSelectedReviewComplete(
        StackPanel content,
        ContentDialog dialog,
        SelectedChangesReviewSnapshot snapshot,
        IReadOnlyList<SelectedChangesReviewFindingRow> rows)
    {
        dialog.PrimaryButtonText = "Close";
        content.Children.Clear();
        content.Children.Add(new TextBlock
        {
            Text = rows.Count == 0
                ? "No findings in the selected changes."
                : $"{rows.Count} finding{(rows.Count == 1 ? string.Empty : "s")} in the selected changes.",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = "This review used only the selected HEAD-to-working-tree changes. An empty result does not claim that the changes are safe.",
            TextWrapping = TextWrapping.Wrap,
        });

        var contextPanel = new StackPanel
        {
            Spacing = 6,
        };
        if (rows.Count > 0)
        {
            var findingsPanel = new StackPanel
            {
                Spacing = 10,
            };
            foreach (var row in rows)
            {
                findingsPanel.Children.Add(BuildSelectedReviewFindingRow(
                    row,
                    snapshot,
                    contextPanel));
            }

            content.Children.Add(new ScrollViewer
            {
                Content = findingsPanel,
                MaxHeight = 390,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });
        }

        content.Children.Add(contextPanel);
        AddSelectedReviewRetryButton(content, dialog);
    }

    private FrameworkElement BuildSelectedReviewFindingRow(
        SelectedChangesReviewFindingRow row,
        SelectedChangesReviewSnapshot snapshot,
        StackPanel contextPanel)
    {
        var body = new StackPanel
        {
            Spacing = 4,
        };
        var location = new Button
        {
            Content = row.Location,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4, 8, 4),
        };
        AutomationProperties.SetName(location, $"Open immutable diff context for {row.Location}");
        location.Click += (_, _) => ShowSelectedReviewContext(
            row.Finding,
            snapshot,
            contextPanel);
        body.Children.Add(location);
        body.Children.Add(new TextBlock
        {
            Text = row.SideLabel,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = row.Finding.Title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = row.Finding.Explanation,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(row.Finding.Suggestion))
        {
            body.Children.Add(new TextBlock
            {
                Text = $"Suggested correction: {row.Finding.Suggestion}",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new Border
        {
            Padding = new Thickness(8),
            Child = body,
        };
    }

    private void RenderSelectedReviewOutdated(
        StackPanel content,
        ContentDialog dialog)
    {
        dialog.PrimaryButtonText = "Close";
        content.Children.Clear();
        content.Children.Add(new TextBlock
        {
            Text = "The selected files changed while the review was running.",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "The findings were cleared because they belong to the immutable earlier diff. Review the current selection again.",
            TextWrapping = TextWrapping.Wrap,
        });
        AddSelectedReviewRetryButton(content, dialog);
    }

    private void RenderSelectedReviewCancelled(
        StackPanel content,
        ContentDialog dialog)
    {
        dialog.PrimaryButtonText = "Close";
        content.Children.Clear();
        content.Children.Add(new TextBlock
        {
            Text = "Review cancelled.",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = "No repository files, staged changes, or commit text were changed. The selected diff may already have reached Codex before cancellation.",
            TextWrapping = TextWrapping.Wrap,
        });
        AddSelectedReviewRetryButton(content, dialog);
    }

    private void RenderSelectedReviewError(
        StackPanel content,
        ContentDialog dialog,
        string message)
    {
        dialog.PrimaryButtonText = "Close";
        content.Children.Clear();
        content.Children.Add(new TextBlock
        {
            Text = "The selected review could not be completed.",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(message)
                ? "Try again after checking the Codex account and repository state."
                : message,
            TextWrapping = TextWrapping.Wrap,
        });
        AddSelectedReviewRetryButton(content, dialog);
    }

    private void AddSelectedReviewRetryButton(StackPanel content, ContentDialog dialog)
    {
        var button = new Button
        {
            Content = "Review again",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(button, "Review selected changes again");
        button.Click += (_, _) =>
        {
            var retryRequestId = selectedChangesReviewRequestId;
            button.IsEnabled = false;
            dialog.Hide();
            _ = RestartSelectedChangesReviewAfterDialogAsync(retryRequestId);
        };
        content.Children.Add(button);
    }

    private async Task RestartSelectedChangesReviewAfterDialogAsync(long requestId)
    {
        try
        {
            var completion = selectedChangesReviewCompletion?.Task;
            if (completion is not null)
            {
                await completion;
            }

            if (requestId != selectedChangesReviewRequestId)
            {
                return;
            }

            await StartSelectedChangesReviewAsync();
        }
        catch (OperationCanceledException)
        {
            // Repository/window lifecycle invalidation owns cancellation.
        }
        catch (Exception exception)
        {
            ShowError("Unable to restart selected review", exception);
        }
    }

    private void ShowSelectedReviewContext(
        CodexSelectedChangesReviewFinding finding,
        SelectedChangesReviewSnapshot snapshot,
        StackPanel contextPanel)
    {
        contextPanel.Children.Clear();
        var file = snapshot.Files.FirstOrDefault(item =>
            string.Equals(item.Path, finding.Path, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            contextPanel.Children.Add(new TextBlock
            {
                Text = "The immutable diff context is unavailable for this finding.",
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var parsed = ParseSelectedReviewPatch(file.Diff);
        var targetIndex = parsed.FindIndex(line =>
            finding.Side == CodexSelectedChangesReviewSide.New
                ? line.Kind == DiffLineKind.Added
                    && line.NewLineNumber == finding.Line
                : line.Kind == DiffLineKind.Removed
                    && line.OldLineNumber == finding.Line);
        if (targetIndex < 0)
        {
            contextPanel.Children.Add(new TextBlock
            {
                Text = "The finding's line is not present in the immutable diff.",
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var target = parsed[targetIndex];
        contextPanel.Children.Add(new TextBlock
        {
            Text = $"Immutable diff context · {finding.Path}:{finding.Line} ({(finding.Side == CodexSelectedChangesReviewSide.New ? "new" : "old")})",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        var firstInHunk = parsed.FindIndex(line => line.HunkId == target.HunkId);
        var firstAfterHunk = parsed.FindIndex(
            targetIndex + 1,
            line => line.HunkId != target.HunkId);
        if (firstAfterHunk < 0)
        {
            firstAfterHunk = parsed.Count;
        }

        var start = Math.Max(
            firstInHunk + 1,
            targetIndex - SelectedReviewContextLineRadius);
        var end = Math.Min(
            firstAfterHunk,
            targetIndex + SelectedReviewContextLineRadius + 1);
        var linesPanel = new StackPanel
        {
            Spacing = 0,
        };
        if (firstInHunk >= 0)
        {
            AddSelectedReviewContextLine(linesPanel, parsed[firstInHunk], isTarget: false);
        }

        if (start > firstInHunk + 1)
        {
            linesPanel.Children.Add(new TextBlock
            {
                Text = "… earlier lines omitted …",
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
            });
        }

        for (var index = start; index < end; index++)
        {
            var line = parsed[index];
            AddSelectedReviewContextLine(
                linesPanel,
                line,
                isTarget: index == targetIndex);
        }

        if (end < firstAfterHunk)
        {
            linesPanel.Children.Add(new TextBlock
            {
                Text = "… later lines omitted …",
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
            });
        }

        contextPanel.Children.Add(new ScrollViewer
        {
            Content = linesPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 220,
        });
    }

    private static void AddSelectedReviewContextLine(
        StackPanel linesPanel,
        SelectedReviewPatchLine line,
        bool isTarget)
    {
        var marker = line.Kind switch
        {
            DiffLineKind.Added => "+",
            DiffLineKind.Removed => "−",
            DiffLineKind.HunkHeader => "@@",
            _ => " ",
        };
        var lineText = $"{line.OldLineNumber?.ToString() ?? string.Empty,4} {line.NewLineNumber?.ToString() ?? string.Empty,4} {marker} {line.Text}";
        linesPanel.Children.Add(new TextBlock
        {
            Text = lineText,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
            FontWeight = isTarget
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
        });
    }

    private static List<SelectedReviewPatchLine> ParseSelectedReviewPatch(string patch)
    {
        var lines = new List<SelectedReviewPatchLine>();
        var oldLine = 0;
        var newLine = 0;
        var hunkId = -1;
        var normalized = patch.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var rawLine in normalized.Split('\n'))
        {
            var hunkMatch = SelectedReviewHunkPattern.Match(rawLine);
            if (hunkMatch.Success
                && int.TryParse(hunkMatch.Groups["old"].Value, out oldLine)
                && int.TryParse(hunkMatch.Groups["new"].Value, out newLine))
            {
                hunkId++;
                lines.Add(new SelectedReviewPatchLine(
                    hunkId,
                    null,
                    null,
                    DiffLineKind.HunkHeader,
                    rawLine));
                continue;
            }

            if (hunkId < 0 || rawLine.Length == 0)
            {
                continue;
            }

            switch (rawLine[0])
            {
                case ' ':
                    lines.Add(new SelectedReviewPatchLine(
                        hunkId,
                        oldLine++,
                        newLine++,
                        DiffLineKind.Context,
                        rawLine[1..]));
                    break;
                case '-':
                    lines.Add(new SelectedReviewPatchLine(
                        hunkId,
                        oldLine++,
                        null,
                        DiffLineKind.Removed,
                        rawLine[1..]));
                    break;
                case '+':
                    lines.Add(new SelectedReviewPatchLine(
                        hunkId,
                        null,
                        newLine++,
                        DiffLineKind.Added,
                        rawLine[1..]));
                    break;
                case '\\':
                    lines.Add(new SelectedReviewPatchLine(
                        hunkId,
                        null,
                        null,
                        DiffLineKind.NoNewline,
                        rawLine));
                    break;
            }
        }

        return lines;
    }

    private void SelectedReview_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        InvalidateSelectedChangesReviewState();

    private void SelectedReview_NavigationChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args) =>
        InvalidateSelectedChangesReviewState();

    private void SelectedReview_ModelChanged(object sender, SelectionChangedEventArgs e) =>
        InvalidateSelectedChangesReviewState();

    private void SelectedReview_RepositoryChanging(object sender, RoutedEventArgs e) =>
        InvalidateSelectedChangesReviewState();

    private void SelectedReview_Closed(object sender, WindowEventArgs args) =>
        InvalidateSelectedChangesReviewState();

    /// <summary>
    /// Backend lifecycle callers can invoke this hook when a refresh, open,
    /// mutation, or repository close changes state without changing list
    /// selection. It clears findings and cancels the owned Codex request.
    /// </summary>
    private void InvalidateSelectedChangesReviewState()
    {
        selectedChangesReviewRequestId++;
        selectedChangesReviewSnapshot = null;
        selectedChangesReviewFindingRows.Clear();
        selectedChangesReviewCancellation?.Cancel();
        if (selectedChangesReviewDialog is { } dialog)
        {
            HideSelectedReviewDialog(dialog);
            selectedChangesReviewDialog = null;
        }
    }

    private async Task CancelSelectedChangesReviewAndWaitAsync()
    {
        InvalidateSelectedChangesReviewState();
        var completion = selectedChangesReviewCompletion?.Task;
        if (completion is not null)
        {
            await completion;
        }
    }

    private static void HideSelectedReviewDialog(ContentDialog dialog)
    {
        try
        {
            dialog.Hide();
        }
        catch (Exception)
        {
            // The dialog may already have closed during window teardown.
        }
    }

    private sealed record SelectedReviewPatchLine(
        int HunkId,
        int? OldLineNumber,
        int? NewLineNumber,
        DiffLineKind Kind,
        string Text);
}

internal sealed class SelectedChangesReviewFindingRow
{
    public SelectedChangesReviewFindingRow(CodexSelectedChangesReviewFinding finding)
    {
        Finding = finding ?? throw new ArgumentNullException(nameof(finding));
    }

    public CodexSelectedChangesReviewFinding Finding { get; }

    public string Location => $"{Finding.Path}:{Finding.Line}";

    public string SideLabel => Finding.Side == CodexSelectedChangesReviewSide.New
        ? "Added line"
        : "Removed line";
}
