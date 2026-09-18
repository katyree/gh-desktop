using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;
using WinGit.Core.Codex;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private CancellationTokenSource? commitMessageGenerationCancellation;
    private long commitMessageGenerationId;
    private bool commitMessageGenerationInProgress;

    private async void GenerateCommitMessageButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (commitMessageGenerationInProgress)
        {
            CancelCommitMessageGeneration();
            return;
        }

        await GenerateCommitMessageAsync();
    }

    private void CodexClient_GenerationProgressReceived(
        object? sender,
        CodexGenerationProgress progress)
    {
        if (codexDisposed ||
            !ReferenceEquals(sender, codexClient) ||
            !commitMessageGenerationInProgress)
        {
            return;
        }

        var queuedGenerationId = commitMessageGenerationId;
        var queuedCancellation = commitMessageGenerationCancellation;
        RootGrid.DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Normal,
            () =>
            {
                if (!codexDisposed &&
                    commitMessageGenerationInProgress &&
                    queuedGenerationId == commitMessageGenerationId &&
                    ReferenceEquals(queuedCancellation, commitMessageGenerationCancellation) &&
                    queuedCancellation is { IsCancellationRequested: false })
                {
                    CommitMessageGenerationStatusText.Text = progress.Kind switch
                    {
                        CodexGenerationProgressKind.TurnStarted =>
                            "Codex is preparing a draft…",
                        CodexGenerationProgressKind.ItemStarted =>
                            "Codex is writing a draft…",
                        CodexGenerationProgressKind.ItemCompleted =>
                            "Codex is checking the draft…",
                        CodexGenerationProgressKind.TurnCompleted =>
                            "Codex finished. Reviewing the draft…",
                        _ => "Codex is generating a draft…",
                    };
                }
            });
    }

    private async Task GenerateCommitMessageAsync()
    {
        if (repositoryRoot is null ||
            currentStatus is null ||
            currentWorkspace != "changes" ||
            mutationInProgress ||
            BusyRing.IsActive ||
            activeGitOperationKind != GitOperationKind.None ||
            diagnosticCaptureMode)
        {
            return;
        }

        if (codexAccount.Status != CodexAccountStatus.SignedIn)
        {
            ShowError(
                "Codex sign-in required",
                new InvalidOperationException(
                    "Sign in to Codex in Settings before generating a commit message."));
            return;
        }

        if (CodexModelComboBox.SelectedItem is not CodexModelRow modelRow)
        {
            ShowError(
                "Codex model unavailable",
                new InvalidOperationException(
                    "Refresh the Codex model list in Settings before generating a commit message."));
            return;
        }

        // Capture every mutable UI input before any dialog can yield. The Core
        // snapshot below also captures HEAD and the complete index fingerprint.
        var root = repositoryRoot;
        var initialContext = new CommitMessageGenerationContext(
            root,
            currentStatus,
            modelRow.Model.Id,
            modelRow.Model.Model,
            (CodexReasoningComboBox.SelectedItem as CodexReasoningRow)?.ReasoningEffort,
            AmendCheckBox.IsChecked == true,
            CommitSummaryBox.Text,
            CommitDescriptionBox.Text);

        var operation = BeginOperation("Reading staged changes…");
        var generationId = ++commitMessageGenerationId;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
        commitMessageGenerationCancellation = cancellation;
        commitMessageGenerationInProgress = true;
        CommitMessageGenerationStatusText.Text = "Capturing staged changes…";
        UpdateMutationButtons();

        try
        {
            var snapshot = await repositoryService.CaptureCommitMessageSnapshotAsync(
                root,
                initialContext.Amend,
                cancellation.Token);
            if (!IsGenerationCurrentWithContext(
                    generationId,
                    operation.Generation,
                    root,
                    cancellation.Token,
                    initialContext))
            {
                return;
            }

            if (snapshot.RawPatch.Length == 0)
            {
                ShowError(
                    initialContext.Amend ? "No diff to summarize" : "No staged changes",
                    new InvalidOperationException(
                        initialContext.Amend
                            ? "Git returned no readable content for this amended commit."
                            : "Stage at least one file before generating a commit message."));
                return;
            }

            if (!string.IsNullOrWhiteSpace(initialContext.Summary) ||
                !string.IsNullOrWhiteSpace(initialContext.Description))
            {
                var replaceDialog = CreateDialog(
                    "Replace the current commit message?",
                    "Replace and generate",
                    new TextBlock
                    {
                        Text = "The generated title and description will replace the text you entered. You can review and edit the result before committing.",
                        TextWrapping = TextWrapping.Wrap,
                    });
                if (await replaceDialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                if (!IsGenerationCurrentWithContext(
                        generationId,
                        operation.Generation,
                        root,
                        cancellation.Token,
                        initialContext))
                {
                    return;
                }
            }

            var consentRequired = !HasCodexCommitMessageConsent(root);
            if (consentRequired)
            {
                var consentDialog = CreateDialog(
                    "Review Codex data sharing",
                    "Allow and generate",
                    new StackPanel
                    {
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "WinGit will send the staged diff to OpenAI through your local Codex account to draft a commit message. For an amend, this describes the full replacement commit. It will not send unstaged files, repository history, remotes, credentials, or environment variables.",
                                TextWrapping = TextWrapping.Wrap,
                            },
                            new TextBlock
                            {
                                Text = "Review and edit the result. WinGit never commits automatically. Repository-specific commit-message rules are not loaded by the native UI yet.",
                                TextWrapping = TextWrapping.Wrap,
                                Style = (Style)RootGrid.Resources["SecondaryTextStyle"],
                            },
                        },
                    });
                if (await consentDialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            if (!IsGenerationCurrentWithContext(
                    generationId,
                    operation.Generation,
                    root,
                    cancellation.Token,
                    initialContext))
            {
                return;
            }

            await repositoryService.RevalidateCommitMessageSnapshotAsync(
                root,
                snapshot,
                cancellation.Token);
            if (!IsGenerationCurrentWithContext(
                    generationId,
                    operation.Generation,
                    root,
                    cancellation.Token,
                    initialContext))
            {
                return;
            }

            if (consentRequired)
            {
                AcknowledgeCodexCommitMessageConsent(root);
            }

            string diff;
            try
            {
                diff = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(snapshot.RawPatch.Span);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidOperationException(
                    "Git returned invalid UTF-8 for commit-message generation.",
                    exception);
            }

            if (string.IsNullOrWhiteSpace(diff))
            {
                throw new InvalidOperationException(
                    "Git returned no readable staged content for this commit.");
            }

            if (!TrySetGenerationStatus(
                    generationId,
                    operation.Generation,
                    root,
                    cancellation.Token,
                    "Asking Codex for a draft…"))
            {
                return;
            }

            var client = await EnsureCodexClientAsync(cancellation.Token);
            if (client is null)
            {
                throw new InvalidOperationException("The Codex app server is unavailable.");
            }

            if (!IsGenerationCurrent(generationId, operation.Generation, root, cancellation.Token))
            {
                return;
            }

            var generator = new CodexCommitMessageGenerator(client);
            var message = await generator.GenerateAsync(
                new CodexCommitMessageGenerationRequest(
                    diff,
                    EnforcedRuleDescriptions: [],
                    ModelSelection: new CodexModelSelectionSnapshot(
                        initialContext.ModelSlug,
                        initialContext.ReasoningEffort)),
                cancellation.Token);

            if (!TrySetGenerationStatus(
                    generationId,
                    operation.Generation,
                    root,
                    cancellation.Token,
                    "Checking the staged changes…"))
            {
                return;
            }

            await repositoryService.RevalidateCommitMessageSnapshotAsync(
                root,
                snapshot,
                cancellation.Token);
            if (!IsGenerationCurrent(generationId, operation.Generation, root, cancellation.Token) ||
                !IsGenerationInputCurrent(initialContext))
            {
                return;
            }

            CommitSummaryBox.Text = message.Title;
            CommitDescriptionBox.Text = message.Description;
            ErrorBar.IsOpen = false;
            TrySetGenerationStatus(
                generationId,
                operation.Generation,
                root,
                cancellation.Token,
                "Generated draft ready for review. Nothing has been committed.");
        }
        catch (CommitMessageSnapshotStaleException exception)
        {
            if (IsGenerationCurrent(generationId, operation.Generation, root, cancellation.Token))
            {
                ShowError("Staged changes changed", exception);
            }
        }
        catch (CodexCommitMessageGenerationCancelledException)
        {
            if (IsGenerationOwnerCurrent(
                    generationId,
                    operation.Generation,
                    root,
                    cancellation))
            {
                CommitMessageGenerationStatusText.Text =
                    "Generation cancelled. Your commit message was not changed.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (IsGenerationOwnerCurrent(
                    generationId,
                    operation.Generation,
                    root,
                    cancellation))
            {
                CommitMessageGenerationStatusText.Text =
                    "Generation cancelled. Your commit message was not changed.";
            }
        }
        catch (CodexCommitMessageGenerationException exception)
        {
            if (IsGenerationCurrent(generationId, operation.Generation, root, cancellation.Token))
            {
                ShowError("Unable to generate commit message", exception);
                CommitMessageGenerationStatusText.Text = exception.Message;
            }
        }
        catch (Exception exception)
        {
            if (IsGenerationCurrent(generationId, operation.Generation, root, cancellation.Token))
            {
                ShowError("Unable to generate commit message", exception);
                CommitMessageGenerationStatusText.Text =
                    "Generation failed. Your commit message was not changed.";
            }
        }
        finally
        {
            if (ReferenceEquals(commitMessageGenerationCancellation, cancellation) &&
                generationId == commitMessageGenerationId)
            {
                commitMessageGenerationCancellation = null;
                commitMessageGenerationInProgress = false;
                UpdateMutationButtons();
            }

            EndOperation(operation.Generation);
        }
    }

    private void CancelCommitMessageGeneration()
    {
        var cancellation = commitMessageGenerationCancellation;
        if (!commitMessageGenerationInProgress || cancellation is null)
        {
            return;
        }

        CommitMessageGenerationStatusText.Text = "Cancelling generation…";
        cancellation.Cancel();
        if (ReferenceEquals(commitMessageGenerationCancellation, cancellation))
        {
            operationCancellation?.Cancel();
        }
    }

    private void UpdateCommitMessageGenerationControls()
    {
        if (GenerateCommitMessageButton is null)
        {
            return;
        }

        var generating = commitMessageGenerationInProgress;
        var hasStagedChanges = currentStatus?.Changes.Any(change =>
            HasIndexChanges(change) && !IsConflictChange(change)) == true;
        var canAmend = AmendCheckBox.IsChecked == true &&
            currentStatus?.IsUnborn == false &&
            !string.IsNullOrWhiteSpace(currentStatus.HeadId);
        var canStart = !diagnosticCaptureMode &&
            repositoryRoot is not null &&
            currentWorkspace == "changes" &&
            !mutationInProgress &&
            !BusyRing.IsActive &&
            activeGitOperationKind == GitOperationKind.None &&
            codexAccount.Status == CodexAccountStatus.SignedIn &&
            codexModelRows.Count > 0;
        GenerateCommitMessageButton.Content = generating
            ? "Cancel generation"
            : "Generate with Codex";
        GenerateCommitMessageButton.IsEnabled = generating ||
            (canStart && (hasStagedChanges || canAmend));

        CommitMessageGenerationRing.IsActive = generating;
        CommitMessageGenerationRing.Visibility = generating
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (generating)
        {
            CancelOperationButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Task 22: keep the shared Cancel control consistent with SetBusy.
            // Codex generation has its own cancel path; Git reads use the
            // shared cancellable operation, while mutations hide Cancel.
            var canCancel = BusyRing.IsActive && currentOperationSupportsCancellation && !mutationInProgress;
            CancelOperationButton.Visibility = canCancel
                ? Visibility.Visible
                : Visibility.Collapsed;
            CancelOperationButton.IsEnabled = canCancel;
        }

        if (generating)
        {
            return;
        }

        CommitMessageGenerationAvailabilityText.Text =
            repositoryRoot is null
                ? "Open a repository to generate a commit message."
                : codexAccount.Status != CodexAccountStatus.SignedIn
                    ? "Sign in to Codex in Settings to generate a draft."
                    : codexModelRows.Count == 0
                        ? "Refresh the Codex model list in Settings before generating."
                        : activeGitOperationKind != GitOperationKind.None
                            ? "Generation is unavailable during an active Git operation."
                            : hasStagedChanges || canAmend
                                ? "Uses all staged changes. Review the generated title and description; nothing is committed automatically. Native repository commit-message rules are not loaded yet."
                                : "Stage changes first, or choose Amend previous commit.";
    }

    private bool IsGenerationCurrent(
        long generationId,
        long operationId,
        string root,
        CancellationToken cancellationToken) =>
        commitMessageGenerationInProgress &&
        generationId == commitMessageGenerationId &&
        operationId == operationGeneration &&
        !cancellationToken.IsCancellationRequested &&
        commitMessageGenerationCancellation is { } currentCancellation &&
        currentCancellation.Token == cancellationToken &&
        IsGenerationRootCurrent(operationId, root);

    // Cancellation intentionally does not participate in this ownership check:
    // the cancellation handlers still need to report completion before finally
    // releases the run. Result/status updates elsewhere continue to use
    // IsGenerationCurrent, which requires an active token.
    private bool IsGenerationOwnerCurrent(
        long generationId,
        long operationId,
        string root,
        CancellationTokenSource cancellation) =>
        commitMessageGenerationInProgress &&
        generationId == commitMessageGenerationId &&
        operationId == operationGeneration &&
        ReferenceEquals(commitMessageGenerationCancellation, cancellation) &&
        IsGenerationRootCurrent(operationId, root);

    private bool TrySetGenerationStatus(
        long generationId,
        long operationId,
        string root,
        CancellationToken cancellationToken,
        string status)
    {
        if (!IsGenerationCurrent(generationId, operationId, root, cancellationToken))
        {
            return false;
        }

        CommitMessageGenerationStatusText.Text = status;
        return true;
    }

    private bool IsGenerationRootCurrent(long generation, string root) =>
        generation == operationGeneration &&
        string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase);

    private bool HasCodexCommitMessageConsent(string root)
    {
        var key = GetCodexCommitMessageConsentKey(root);
        var now = DateTimeOffset.UtcNow;
        return settings.CodexCommitMessageConsentTimestamps.TryGetValue(key, out var acceptedAt) &&
            acceptedAt >= now - TimeSpan.FromDays(30) &&
            acceptedAt <= now.AddMinutes(5);
    }

    private void AcknowledgeCodexCommitMessageConsent(string root)
    {
        var key = GetCodexCommitMessageConsentKey(root);
        settings.CodexCommitMessageConsentTimestamps[key] = DateTimeOffset.UtcNow;
        _ = SaveSettingsAsync();
    }

    private static string GetCodexCommitMessageConsentKey(string root)
    {
        var normalized = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private bool IsGenerationCurrentWithContext(
        long generationId,
        long operationId,
        string root,
        CancellationToken cancellationToken,
        CommitMessageGenerationContext context) =>
        IsGenerationCurrent(generationId, operationId, root, cancellationToken) &&
        IsGenerationInputCurrent(context) &&
        ReferenceEquals(currentStatus, context.Status) &&
        !mutationInProgress &&
        activeGitOperationKind == GitOperationKind.None;

    private bool IsGenerationInputCurrent(
        CommitMessageGenerationContext context) =>
        repositoryRoot is not null &&
        string.Equals(repositoryRoot, context.RootPath, StringComparison.OrdinalIgnoreCase) &&
        currentWorkspace == "changes" &&
        string.Equals(
            (CodexModelComboBox.SelectedItem as CodexModelRow)?.Model.Id,
            context.ModelId,
            StringComparison.Ordinal) &&
        string.Equals(
            (CodexModelComboBox.SelectedItem as CodexModelRow)?.Model.Model,
            context.ModelSlug,
            StringComparison.Ordinal) &&
        string.Equals(
            (CodexReasoningComboBox.SelectedItem as CodexReasoningRow)?.ReasoningEffort,
            context.ReasoningEffort,
            StringComparison.Ordinal) &&
        (AmendCheckBox.IsChecked == true) == context.Amend &&
        string.Equals(CommitSummaryBox.Text, context.Summary, StringComparison.Ordinal) &&
        string.Equals(CommitDescriptionBox.Text, context.Description, StringComparison.Ordinal);

    private sealed record CommitMessageGenerationContext(
        string RootPath,
        RepositoryStatus Status,
        string ModelId,
        string ModelSlug,
        string? ReasoningEffort,
        bool Amend,
        string Summary,
        string Description);
}
