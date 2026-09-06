using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<ConflictHunkRow> conflictHunkRows = [];
    private ConflictFileSnapshot? selectedConflictSnapshot;
    private string? selectedConflictRoot;
    private string? selectedConflictPath;
    private ConflictResolutionAction? selectedConflictDeleteAction;
    private bool conflictEditorOpen;
    private ConflictDraft? pendingConflictDraft;
    private ConflictFileSnapshot? staleConflictSnapshot;
    private ConflictDraft? staleConflictDraft;
    private bool conflictEditorStale;

    private sealed record ConflictHunkDraft(
        int Index,
        string ResolvedContent,
        bool HasResolution);

    private sealed record ConflictDraft(
        string RootPath,
        string Path,
        string ExpectedHeadId,
        string? WorkingFileSha256,
        ConflictFileKind Kind,
        IReadOnlyList<ConflictIndexStage> IndexStages,
        IReadOnlyList<ConflictHunkDraft> Hunks,
        ConflictResolutionAction? DeleteAction);

    private static bool IsConflictChange(FileChange change) =>
        change.Kind == ChangeKind.Conflicted
        || change.IndexStatus.Contains('U', StringComparison.OrdinalIgnoreCase)
        || change.WorkTreeStatus.Contains('U', StringComparison.OrdinalIgnoreCase);

    private void ClearConflictEditorState(bool discardPendingDraft = false)
    {
        if (discardPendingDraft)
        {
            pendingConflictDraft = null;
            staleConflictSnapshot = null;
            staleConflictDraft = null;
            conflictEditorStale = false;
        }

        selectedConflictSnapshot = null;
        selectedConflictRoot = null;
        selectedConflictPath = null;
        selectedConflictDeleteAction = null;
        conflictEditorOpen = false;
        conflictHunkRows.Clear();

        if (ConflictHunksList is null)
        {
            return;
        }

        ConflictHunksList.ItemsSource = conflictHunkRows;
        ConflictEditorPanel.Visibility = Visibility.Collapsed;
        OpenConflictEditorButton.Visibility = Visibility.Collapsed;
        ConflictHunksList.Visibility = Visibility.Collapsed;
        ConflictDeleteModifyPanel.Visibility = Visibility.Collapsed;
        ConflictUnsupportedPanel.Visibility = Visibility.Collapsed;
        ConflictEditorActionBar.Visibility = Visibility.Collapsed;
        ConflictStaleDraftPanel.Visibility = Visibility.Collapsed;
        ConflictStaleDraftTextBox.Text = string.Empty;
        UpdatePartialSelectionControls();
        UpdateConflictEditorControls();
    }

    private void DiscardConflictDraftIfPathChanged(string path)
    {
        if (pendingConflictDraft is not null
            && !string.Equals(pendingConflictDraft.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            pendingConflictDraft = null;
        }

        if (staleConflictDraft is not null
            && !string.Equals(staleConflictDraft.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            staleConflictSnapshot = null;
            staleConflictDraft = null;
            conflictEditorStale = false;
        }
    }

    private void ClearStaleConflictDraft()
    {
        staleConflictSnapshot = null;
        staleConflictDraft = null;
        conflictEditorStale = false;
        if (ConflictStaleDraftPanel is not null)
        {
            ConflictStaleDraftPanel.Visibility = Visibility.Collapsed;
            ConflictStaleDraftTextBox.Text = string.Empty;
        }
    }

    private async Task LoadConflictEditorAsync(
        ChangeRow row,
        long operationGeneration,
        CancellationToken cancellationToken)
    {
        if (!IsConflictChange(row.Change) || repositoryRoot is null)
        {
            ClearConflictEditorState(discardPendingDraft: true);
            return;
        }

        var root = repositoryRoot;
        var path = row.Path;
        try
        {
            if (conflictEditorStale
                && staleConflictSnapshot is not null
                && staleConflictDraft is not null
                && string.Equals(staleConflictSnapshot.RootPath, root, StringComparison.OrdinalIgnoreCase)
                && string.Equals(staleConflictSnapshot.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                if (IsCurrent(operationGeneration, cancellationToken))
                {
                    ApplyStaleConflictDraft(staleConflictSnapshot, staleConflictDraft);
                }

                return;
            }

            if (conflictEditorStale)
            {
                staleConflictSnapshot = null;
                staleConflictDraft = null;
                conflictEditorStale = false;
            }

            var snapshot = await repositoryService.GetConflictFileSnapshotAsync(
                root,
                path,
                cancellationToken);
            if (!IsCurrent(operationGeneration, cancellationToken)
                || !ReferenceEquals(selectedChange, row)
                || !string.Equals(selectedChangePath, path, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ApplyConflictSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Selecting another path cancels the snapshot read.
        }
        catch (Exception exception)
        {
            if (!IsCurrent(operationGeneration, cancellationToken)
                || !ReferenceEquals(selectedChange, row))
            {
                return;
            }

            ClearConflictEditorState(discardPendingDraft: true);
            ShowDiffMessage(
                "Conflict editor unavailable",
                $"{exception.Message} Use the normal diff and the existing Stage or Unstage controls.");
            ShowError("Unable to read conflict file", exception);
        }
    }

    private void ApplyConflictSnapshot(ConflictFileSnapshot snapshot)
    {
        conflictEditorStale = false;
        selectedConflictSnapshot = snapshot;
        selectedConflictRoot = snapshot.RootPath;
        selectedConflictPath = snapshot.Path;
        selectedConflictDeleteAction = null;
        conflictHunkRows.Clear();
        ConflictHunksList.ItemsSource = conflictHunkRows;

        var operationLabel = activeGitOperationKind switch
        {
            GitOperationKind.Merge => "Merge conflict",
            GitOperationKind.Rebase => "Rebase conflict",
            _ => "Git conflict",
        };
        ConflictEditorTitle.Text = operationLabel;
        ConflictEditorPathText.Text =
            $"{snapshot.Path}  ·  HEAD {ShortObjectId(snapshot.ExpectedHeadId)}";
        ConflictEditorSemanticsText.Text =
            "During a merge, Ours usually follows the current branch and Theirs follows the incoming branch. During a rebase, Git may assign those sides differently, so review both before applying.";

        ConflictHunksList.Visibility = Visibility.Collapsed;
        ConflictDeleteModifyPanel.Visibility = Visibility.Collapsed;
        ConflictUnsupportedPanel.Visibility = Visibility.Collapsed;
        ConflictEditorActionBar.Visibility = Visibility.Collapsed;
        KeepConflictRadio.IsChecked = false;
        DeleteConflictRadio.IsChecked = false;

        if (!snapshot.IsSupported)
        {
            ConflictUnsupportedText.Text = snapshot.UnsupportedReason
                ?? "This conflict cannot be safely edited by the native resolver.";
            ConflictUnsupportedPanel.Visibility = Visibility.Visible;
            conflictEditorOpen = true;
            UpdateConflictEditorControls();
            return;
        }

        if (snapshot.Kind == ConflictFileKind.DeleteModify)
        {
            var deletedSide = snapshot.DeletedSide == ConflictDeletedSide.Ours
                ? "Ours"
                : "Theirs";
            ConflictDeleteModifyDescription.Text =
                $"This is a delete-versus-modify conflict. Git reports that {deletedSide} deleted this path. Choose whether to keep the working file or delete it.";
            ConflictDeleteModifyPanel.Visibility = Visibility.Visible;
            ConflictEditorActionBar.Visibility = Visibility.Visible;
            conflictEditorOpen = true;
            RestorePendingConflictDraft(snapshot);
            ConflictEditorValidationText.Text =
                "Choose Keep or Delete, then apply the choice."
                + (selectedConflictDeleteAction is null ? string.Empty : "");
            UpdateConflictEditorControls();
            return;
        }

        foreach (var hunk in snapshot.Hunks)
        {
            conflictHunkRows.Add(new ConflictHunkRow(hunk));
        }

        ConflictHunksList.ItemsSource = conflictHunkRows;
        ConflictHunksList.Visibility = Visibility.Visible;
        ConflictEditorActionBar.Visibility = Visibility.Visible;
        ConflictEditorValidationText.Text =
            "Choose a side or edit the resolved text for every hunk before applying.";
        conflictEditorOpen = true;
        RestorePendingConflictDraft(snapshot);
        UpdateConflictEditorControls();
    }

    private void ApplyStaleConflictDraft(
        ConflictFileSnapshot snapshot,
        ConflictDraft draft)
    {
        ApplyConflictSnapshot(snapshot);

        if (snapshot.Kind == ConflictFileKind.Text)
        {
            foreach (var row in conflictHunkRows)
            {
                var saved = draft.Hunks.FirstOrDefault(item => item.Index == row.Index);
                if (saved is not null)
                {
                    row.SetResolvedContent(saved.ResolvedContent, saved.HasResolution);
                }
            }
        }
        else if (snapshot.Kind == ConflictFileKind.DeleteModify)
        {
            selectedConflictDeleteAction = draft.DeleteAction;
            KeepConflictRadio.IsChecked = selectedConflictDeleteAction == ConflictResolutionAction.Keep;
            DeleteConflictRadio.IsChecked = selectedConflictDeleteAction == ConflictResolutionAction.Delete;
        }

        conflictEditorStale = true;
        ConflictStaleDraftTextBox.Text = FormatConflictDraft(draft);
        ConflictStaleDraftPanel.Visibility = Visibility.Visible;
        ConflictEditorValidationText.Text =
            "The repository changed before Git could write this resolution. Your draft is preserved below for copying. Use Refresh, review the current conflict, and choose the resolutions again before applying.";
        UpdateConflictEditorControls();
    }

    private static string FormatConflictDraft(ConflictDraft draft)
    {
        if (draft.Kind == ConflictFileKind.DeleteModify)
        {
            return $"{draft.Path}{Environment.NewLine}"
                + (draft.DeleteAction is { } action
                    ? $"Delete-versus-modify choice: {action}."
                    : "No delete-versus-modify choice was selected.");
        }

        var sections = draft.Hunks.Select(hunk =>
            $"Conflict {hunk.Index + 1}{Environment.NewLine}"
            + (hunk.HasResolution
                ? string.IsNullOrEmpty(hunk.ResolvedContent) ? "(empty)" : hunk.ResolvedContent
                : "(no resolution selected)"));
        return $"{draft.Path}  ·  HEAD {ShortObjectId(draft.ExpectedHeadId)}"
            + Environment.NewLine
            + string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private void UpdateConflictEditorControls()
    {
        if (ConflictEditorPanel is null)
        {
            return;
        }

        var snapshot = selectedConflictSnapshot;
        var hasSnapshot = snapshot is not null;
        var supported = snapshot?.IsSupported == true;
        var editorVisible = hasSnapshot && conflictEditorOpen;
        var inChangesWorkspace = currentWorkspace == "changes"
            && ChangesWorkspace.Visibility == Visibility.Visible;
        var canEdit = editorVisible
            && supported
            && inChangesWorkspace
            && !mutationInProgress
            && !BusyRing.IsActive
            && !conflictEditorStale;

        ConflictEditorPanel.Visibility = editorVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpenConflictEditorButton.Visibility = hasSnapshot && !editorVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (editorVisible)
        {
            PartialSelectionBar.Visibility = Visibility.Collapsed;
            DiffList.Visibility = Visibility.Collapsed;
            PartialDiffList.Visibility = Visibility.Collapsed;
            DiffMessagePanel.Visibility = Visibility.Collapsed;
        }
        CommitPanel.Visibility = editorVisible ? Visibility.Collapsed : Visibility.Visible;
        ChangesCommitRow.Height = editorVisible
            ? new GridLength(0)
            : GridLength.Auto;
        ConflictStaleDraftPanel.Visibility = conflictEditorStale && editorVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ConflictStaleDraftTextBox.IsReadOnly = true;

        var isText = supported && snapshot?.Kind == ConflictFileKind.Text;
        var isDeleteModify = supported && snapshot?.Kind == ConflictFileKind.DeleteModify;
        var isReady = isText
            ? conflictHunkRows.Count == snapshot!.Hunks.Count
                && conflictHunkRows.All(row => row.HasResolution)
            : isDeleteModify && selectedConflictDeleteAction is not null;

        ConflictHunksList.IsEnabled = canEdit && isText;
        KeepConflictRadio.IsEnabled = canEdit && isDeleteModify;
        DeleteConflictRadio.IsEnabled = canEdit && isDeleteModify;
        ApplyConflictButton.IsEnabled = canEdit && isReady;
        ApplyAndStageConflictButton.IsEnabled = canEdit && isReady;
    }

    private void ApplyConflictEditorListVisibility()
    {
        if (ConflictEditorPanel is null
            || !conflictEditorOpen
            || selectedConflictSnapshot?.IsSupported != true)
        {
            return;
        }

        DiffList.Visibility = Visibility.Collapsed;
        PartialDiffList.Visibility = Visibility.Collapsed;
        DiffMessagePanel.Visibility = Visibility.Collapsed;
    }

    private void OpenConflictEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (selectedConflictSnapshot is not null)
        {
            conflictEditorOpen = true;
            UpdateConflictEditorControls();
        }
    }

    private void CloseConflictEditorButton_Click(object sender, RoutedEventArgs e)
    {
        conflictEditorOpen = false;
        UpdatePartialSelectionControls();
        UpdateConflictEditorControls();
    }

    private void ConflictUseOursButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is ConflictHunkRow row)
        {
            row.SetResolvedContent(row.Hunk.OursContent);
            ConflictEditorValidationText.Text = "Review the resolved text, then apply the conflict resolution.";
            UpdateConflictEditorControls();
        }
    }

    private void ConflictUseTheirsButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is ConflictHunkRow row)
        {
            row.SetResolvedContent(row.Hunk.TheirsContent);
            ConflictEditorValidationText.Text = "Review the resolved text, then apply the conflict resolution.";
            UpdateConflictEditorControls();
        }
    }

    private void ConflictUseBothButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is ConflictHunkRow row)
        {
            row.SetResolvedContent(JoinConflictSides(row.Hunk.OursContent, row.Hunk.TheirsContent));
            ConflictEditorValidationText.Text = "Review the resolved text, then apply the conflict resolution.";
            UpdateConflictEditorControls();
        }
    }

    private void ConflictResolvedTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox
            && textBox.DataContext is ConflictHunkRow row
            && !string.Equals(row.ResolvedContent, textBox.Text, StringComparison.Ordinal))
        {
            row.SetResolvedContent(textBox.Text);
        }

        UpdateConflictEditorControls();
    }

    private void KeepConflictRadio_Click(object sender, RoutedEventArgs e)
    {
        selectedConflictDeleteAction = ConflictResolutionAction.Keep;
        ConflictEditorValidationText.Text = "The working file will be kept. Choose Apply or Apply and stage.";
        UpdateConflictEditorControls();
    }

    private void DeleteConflictRadio_Click(object sender, RoutedEventArgs e)
    {
        selectedConflictDeleteAction = ConflictResolutionAction.Delete;
        ConflictEditorValidationText.Text = "The working file will be deleted after confirmation. Choose Apply or Apply and stage.";
        UpdateConflictEditorControls();
    }

    private async void ApplyConflictButton_Click(object sender, RoutedEventArgs e)
    {
        await ConfirmAndApplyConflictAsync(stage: false);
    }

    private async void ApplyAndStageConflictButton_Click(object sender, RoutedEventArgs e)
    {
        await ConfirmAndApplyConflictAsync(stage: true);
    }

    private async Task ConfirmAndApplyConflictAsync(bool stage)
    {
        var snapshot = selectedConflictSnapshot;
        if (snapshot is null || !snapshot.IsSupported)
        {
            return;
        }

        ConflictResolutionRequest? request;
        if (snapshot.Kind == ConflictFileKind.DeleteModify)
        {
            if (selectedConflictDeleteAction is not { } action)
            {
                ConflictEditorValidationText.Text = "Choose Keep or Delete before applying.";
                UpdateConflictEditorControls();
                return;
            }

            var stageEffect = stage
                ? " The resolved path will also be staged."
                : " The index will remain unresolved until you stage the path.";
            var dialog = CreateDialog(
                $"{(action == ConflictResolutionAction.Delete ? "Delete" : "Keep")} conflicted file?",
                stage ? $"{(action == ConflictResolutionAction.Delete ? "Delete" : "Keep")} and stage" : $"{(action == ConflictResolutionAction.Delete ? "Delete" : "Keep")} file",
                new TextBlock
                {
                    Text = $"{(action == ConflictResolutionAction.Delete ? "Delete" : "Keep")} the working file \"{snapshot.Path}\" from the captured delete-versus-modify conflict? Git will verify HEAD {ShortObjectId(snapshot.ExpectedHeadId)} and the conflict stages before writing.{stageEffect}",
                    TextWrapping = TextWrapping.Wrap,
                });
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            request = new ConflictResolutionRequest(deleteAction: action, stage: stage);
        }
        else
        {
            request = BuildConflictResolutionRequest(snapshot, stage);
            if (request is null)
            {
                return;
            }
        }

        await RunConflictResolutionAsync(snapshot, request);
    }

    private ConflictResolutionRequest? BuildConflictResolutionRequest(
        ConflictFileSnapshot snapshot,
        bool stage)
    {
        if (snapshot.Kind == ConflictFileKind.DeleteModify)
        {
            return selectedConflictDeleteAction is { } action
                ? new ConflictResolutionRequest(deleteAction: action, stage: stage)
                : null;
        }

        if (snapshot.Kind != ConflictFileKind.Text
            || conflictHunkRows.Count != snapshot.Hunks.Count
            || conflictHunkRows.Any(row => !row.HasResolution))
        {
            ConflictEditorValidationText.Text = "Choose a side or edit the resolved text for every hunk before applying.";
            UpdateConflictEditorControls();
            return null;
        }

        var replacements = conflictHunkRows
            .Select(row => new ConflictHunkReplacement(row.Index, row.ResolvedContent))
            .ToArray();
        return new ConflictResolutionRequest(replacements, stage: stage);
    }

    private async Task RunConflictResolutionAsync(
        ConflictFileSnapshot snapshot,
        ConflictResolutionRequest request)
    {
        var root = snapshot.RootPath;
        if (snapshot is null
            || root is null
            || !snapshot.IsSupported
            || !CanStartRepositoryWrite()
            || currentWorkspace != "changes"
            || !conflictEditorOpen
            || conflictEditorStale)
        {
            return;
        }

        var draft = CreateConflictDraft(snapshot);
        mutationInProgress = true;
        var operation = BeginOperation(request.Stage
            ? "Applying and staging conflict resolution…"
            : "Applying conflict resolution…");
        try
        {
            var selectionIsCurrent = IsCurrent(operation.Generation, operation.Token)
                && ReferenceEquals(selectedConflictSnapshot, snapshot)
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                && string.Equals(selectedConflictPath, snapshot.Path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(currentStatus?.HeadId, snapshot.ExpectedHeadId, StringComparison.OrdinalIgnoreCase);
            if (!selectionIsCurrent)
            {
                pendingConflictDraft = null;
                if (!operation.Token.IsCancellationRequested
                    && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    ShowError(
                        "Conflict changed",
                        new InvalidOperationException("The selected conflict changed before it could be applied. Refresh and review it again."));
                }

                return;
            }

            var result = await repositoryService.ApplyConflictResolutionAsync(
                root,
                snapshot,
                request,
                operation.Token);
            pendingConflictDraft = null;
            ErrorBar.IsOpen = false;
            StatusText.Text = result.WasDeleted
                ? result.WasStaged
                    ? "Conflict file deleted and staged"
                    : "Conflict file deleted"
                : result.WasStaged
                    ? "Conflict resolved and staged"
                    : "Conflict resolved; stage the file to mark it resolved";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            pendingConflictDraft = draft;
            StatusText.Text = "Conflict resolution canceled; your draft was kept while the repository refreshed.";
        }
        catch (ConflictFileSnapshotStaleException exception)
        {
            pendingConflictDraft = null;
            staleConflictSnapshot = snapshot;
            staleConflictDraft = draft;
            conflictEditorStale = true;
            conflictEditorOpen = true;
            ConflictStaleDraftTextBox.Text = FormatConflictDraft(draft);
            ConflictStaleDraftPanel.Visibility = Visibility.Visible;
            ConflictEditorValidationText.Text =
                "The repository changed before Git could write this resolution. Your draft is preserved below for copying. Use Refresh, review the current conflict, and choose the resolutions again before applying.";
            UpdateConflictEditorControls();
            ShowError("Refresh required", exception);
        }
        catch (ConflictResolutionUnsupportedException exception)
        {
            pendingConflictDraft = null;
            ShowError("Conflict resolution unavailable", exception);
        }
        catch (Exception exception)
        {
            pendingConflictDraft = draft;
            ShowError("Unable to apply conflict resolution", exception);
        }
        finally
        {
            try
            {
                await RefreshAfterMutationAsync(root);
            }
            catch (Exception refreshException)
            {
                ShowError("Unable to refresh repository after conflict resolution", refreshException);
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
        }
    }

    private ConflictDraft CreateConflictDraft(ConflictFileSnapshot snapshot) =>
        new(
            snapshot.RootPath,
            snapshot.Path,
            snapshot.ExpectedHeadId,
            snapshot.WorkingFileSha256,
            snapshot.Kind,
            snapshot.IndexStages.ToArray(),
            conflictHunkRows
                .Select(row => new ConflictHunkDraft(row.Index, row.ResolvedContent, row.HasResolution))
                .ToArray(),
            selectedConflictDeleteAction);

    private void RestorePendingConflictDraft(ConflictFileSnapshot snapshot)
    {
        var draft = pendingConflictDraft;
        if (draft is null)
        {
            return;
        }

        pendingConflictDraft = null;
        if (!SameConflictIdentity(draft, snapshot))
        {
            return;
        }

        if (snapshot.Kind == ConflictFileKind.DeleteModify)
        {
            selectedConflictDeleteAction = draft.DeleteAction;
        }
        else if (snapshot.Kind == ConflictFileKind.Text
            && draft.Hunks.Count == conflictHunkRows.Count)
        {
            foreach (var row in conflictHunkRows)
            {
                var saved = draft.Hunks.FirstOrDefault(item => item.Index == row.Index);
                if (saved is null)
                {
                    return;
                }

                row.SetResolvedContent(saved.ResolvedContent, saved.HasResolution);
            }
        }
        else
        {
            return;
        }

        ConflictEditorValidationText.Text =
            "Your draft was kept after the write failed. Review it and apply the resolution again.";
    }

    private static bool SameConflictIdentity(
        ConflictDraft draft,
        ConflictFileSnapshot snapshot) =>
        string.Equals(draft.RootPath, snapshot.RootPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(draft.Path, snapshot.Path, StringComparison.OrdinalIgnoreCase)
        && string.Equals(draft.ExpectedHeadId, snapshot.ExpectedHeadId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(draft.WorkingFileSha256, snapshot.WorkingFileSha256, StringComparison.OrdinalIgnoreCase)
        && draft.Kind == snapshot.Kind
        && draft.IndexStages.Count == snapshot.IndexStages.Count
        && draft.IndexStages.Zip(snapshot.IndexStages).All(pair =>
            pair.First.StageNumber == pair.Second.StageNumber
            && string.Equals(pair.First.Mode, pair.Second.Mode, StringComparison.Ordinal)
            && string.Equals(pair.First.BlobId, pair.Second.BlobId, StringComparison.OrdinalIgnoreCase));

    private static string JoinConflictSides(string ours, string theirs)
    {
        if (string.IsNullOrEmpty(ours))
        {
            return theirs;
        }

        if (string.IsNullOrEmpty(theirs))
        {
            return ours;
        }

        return ours + Environment.NewLine + theirs;
    }
}
