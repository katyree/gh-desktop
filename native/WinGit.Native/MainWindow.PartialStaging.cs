using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private readonly DiffRowCollection<PartialDiffRow> partialDiffRows = [];
    private readonly HashSet<PartialDiffSelection> partialSelections = [];
    private PartialFileDiff? selectedPartialDiff;
    private string? partialSelectionMessage;

    private void ClearPartialDiffState()
    {
        selectedPartialDiff = null;
        partialSelectionMessage = null;
        partialSelections.Clear();
        partialDiffRows.Clear();
        PartialDiffList.ItemsSource = partialDiffRows;
        PartialSelectionBar.Visibility = Visibility.Collapsed;
        PartialDiffList.Visibility = Visibility.Collapsed;
        PartialSelectionStatusText.Text = string.Empty;
        UpdatePartialSelectionControls();
    }

    private async Task LoadPartialDiffAsync(
        ChangeRow row,
        long operationGeneration,
        CancellationToken cancellationToken)
    {
        if (repositoryRoot is null)
        {
            return;
        }

        var root = repositoryRoot;
        try
        {
            var partialDiff = await repositoryService.GetPartialDiffAsync(
                root,
                row.Change,
                row.IsStaged,
                cancellationToken);
            if (!IsCurrent(operationGeneration, cancellationToken)
                || !ReferenceEquals(selectedChange, row)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                || selectedChangeIsStaged != row.IsStaged)
            {
                return;
            }

            selectedPartialDiff = partialDiff;
            partialSelectionMessage = null;
            partialSelections.Clear();
            if (partialDiff.IsSupported)
            {
                partialDiffRows.ReplaceAll(PartialDiffRow.CreateRows(partialDiff));
                PartialDiffList.ItemsSource = partialDiffRows;
            }
            else
            {
                partialDiffRows.Clear();
            }

            UpdatePartialSelectionControls();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Selecting another path cancels both diff reads.
        }
        catch (Exception exception)
        {
            if (!IsCurrent(operationGeneration, cancellationToken)
                || !ReferenceEquals(selectedChange, row))
            {
                return;
            }

            selectedPartialDiff = null;
            partialSelections.Clear();
            partialDiffRows.Clear();
            partialSelectionMessage = exception.Message;
            UpdatePartialSelectionControls();
        }
    }

    private void PartialRowCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox
            || checkBox.DataContext is not PartialDiffRow row
            || selectedPartialDiff?.IsSupported != true
            || !row.CanSelect
            || mutationInProgress
            || BusyRing.IsActive
            || currentWorkspace != "changes")
        {
            return;
        }

        ApplyPartialSelection(row, checkBox.IsChecked == true);
    }

    private void ApplyPartialSelection(PartialDiffRow row, bool selected)
    {
        if (selectedPartialDiff is null || !row.CanSelect)
        {
            return;
        }

        var hunkSelection = PartialDiffSelection.ForHunk(row.HunkId);
        if (row.IsHunk)
        {
            partialSelections.RemoveWhere(item => item.HunkId == row.HunkId);
            if (selected)
            {
                partialSelections.Add(hunkSelection);
            }
        }
        else
        {
            var hunkWasSelected = partialSelections.Contains(hunkSelection);
            partialSelections.Remove(hunkSelection);
            if (!selected && hunkWasSelected)
            {
                var hunk = selectedPartialDiff.Hunks.FirstOrDefault(item => item.Id == row.HunkId);
                if (hunk is not null)
                {
                    foreach (var line in hunk.Lines.Where(item =>
                        item.IsSelectable && item.LineIndex != row.LineIndex))
                    {
                        partialSelections.Add(
                            PartialDiffSelection.ForLine(hunk.Id, line.LineIndex));
                    }
                }
            }
            else
            {
                var lineSelection = PartialDiffSelection.ForLine(row.HunkId, row.LineIndex);
                if (selected)
                {
                    partialSelections.Add(lineSelection);
                }
                else
                {
                    partialSelections.Remove(lineSelection);
                }
            }
        }

        SyncPartialRowChecks();
        UpdatePartialSelectionControls();
    }

    private void SyncPartialRowChecks()
    {
        foreach (var row in partialDiffRows)
        {
            if (row.IsHunk)
            {
                var hunkSelected = partialSelections.Contains(
                    PartialDiffSelection.ForHunk(row.HunkId));
                var selectedLineCount = partialSelections.Count(item =>
                    item.HunkId == row.HunkId && item.LineIndex is not null);
                var selectableLineCount = selectedPartialDiff?.Hunks
                    .FirstOrDefault(item => item.Id == row.HunkId)?.Lines
                    .Count(item => item.IsSelectable) ?? 0;
                var allLinesSelected = selectableLineCount > 0
                    && selectedLineCount == selectableLineCount;
                row.SetCheckedFromSelection(
                    hunkSelected || allLinesSelected
                        ? true
                        : selectedLineCount > 0
                            ? null
                            : false);
                continue;
            }

            if (!row.CanSelect)
            {
                row.SetCheckedFromSelection(null);
                continue;
            }

            row.SetCheckedFromSelection(
                partialSelections.Contains(PartialDiffSelection.ForHunk(row.HunkId))
                || partialSelections.Contains(row.Selection));
        }

    }

    private void StagePartialButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RunPartialMutationAsync(stage: true);
    }

    private void UnstagePartialButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RunPartialMutationAsync(stage: false);
    }

    private void ClearPartialSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (mutationInProgress || BusyRing.IsActive)
        {
            return;
        }

        partialSelections.Clear();
        SyncPartialRowChecks();
        UpdatePartialSelectionControls();
    }

    private async Task RunPartialMutationAsync(bool stage)
    {
        if (mutationInProgress
            || BusyRing.IsActive
            || repositoryRoot is null
            || selectedPartialDiff?.IsSupported != true
            || partialSelections.Count == 0
            || currentWorkspace != "changes"
            || selectedChangeIsStaged == stage)
        {
            return;
        }

        // Keep an immutable request boundary while the UI is disabled. Core
        // re-reads and validates this snapshot under its mutation gate.
        var root = repositoryRoot;
        var diff = selectedPartialDiff;
        var selection = partialSelections.ToArray();
        mutationInProgress = true;
        ErrorBar.IsOpen = false;
        var operation = BeginOperation(stage
            ? "Staging selected lines…"
            : "Unstaging selected lines…");
        try
        {
            if (stage)
            {
                await repositoryService.StageSelectedChangesAsync(
                    root,
                    diff,
                    selection,
                    operation.Token);
            }
            else
            {
                await repositoryService.UnstageSelectedChangesAsync(
                    root,
                    diff,
                    selection,
                    operation.Token);
            }

            StatusText.Text = stage
                ? "Selected lines staged"
                : "Selected lines unstaged";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            StatusText.Text = stage
                ? "Partial stage cancelled; refreshing repository…"
                : "Partial unstage cancelled; refreshing repository…";
        }
        catch (StaleDiffSnapshotException exception)
        {
            ShowError("Refresh required", exception);
        }
        catch (PartialStagingUnsupportedException exception)
        {
            ShowError("Partial selection unavailable", exception);
        }
        catch (Exception exception)
        {
            ShowError(stage ? "Unable to stage selected lines" : "Unable to unstage selected lines", exception);
        }
        finally
        {
            try
            {
                await RefreshAfterMutationAsync(root);
            }
            catch (Exception refreshException)
            {
                ShowError("Unable to refresh repository after partial staging", refreshException);
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
        }
    }

    private void UpdatePartialSelectionControls()
    {
        if (PartialSelectionBar is null)
        {
            return;
        }

        var inChangesWorkspace = currentWorkspace == "changes"
            && ChangesWorkspace.Visibility == Visibility.Visible;
        var hasDiffState = selectedPartialDiff is not null || partialSelectionMessage is not null;
        var supported = selectedPartialDiff?.IsSupported == true;
        var selectionCount = partialSelections.Count;
        var selectionBlocked = supported && TextDiffBlocksPartialSelection();
        var canShowPartial = supported && inChangesWorkspace && !selectionBlocked;
        var canInteract = inChangesWorkspace
            && !mutationInProgress
            && !BusyRing.IsActive;

        PartialSelectionBar.Visibility = hasDiffState && inChangesWorkspace
            ? Visibility.Visible
            : Visibility.Collapsed;
        PartialDiffList.Visibility = canShowPartial
            ? Visibility.Visible
            : Visibility.Collapsed;
        PartialDiffList.IsEnabled = canInteract && canShowPartial;

        if (!hasDiffState)
        {
            PartialSelectionStatusText.Text = string.Empty;
        }
        else if (!supported)
        {
            PartialSelectionStatusText.Text = "Line selection unavailable: "
                + (partialSelectionMessage
                    ?? selectedPartialDiff?.Message
                    ?? "Git did not return selectable text changes.");
        }
        else if (selectionBlocked)
        {
            PartialSelectionStatusText.Text = hideWhitespaceChanges
                ? "Line selection is unavailable while hiding whitespace changes. Turn off Hide whitespace changes to select lines."
                : textDiffMode == "Split"
                    ? "Line selection is unavailable in Split view. Switch to Unified to stage or unstage selected lines."
                    : "Line selection is unavailable while searching. Clear the search to select lines.";
        }
        else if (selectionCount == 0)
        {
            PartialSelectionStatusText.Text = "Select changed lines or a whole hunk";
        }
        else
        {
            var hunkCount = partialSelections.Count(item => item.LineIndex is null);
            var lineCount = partialSelections.Count(item => item.LineIndex is not null);
            var parts = new List<string>(2);
            if (hunkCount > 0)
            {
                parts.Add($"{hunkCount} hunk{(hunkCount == 1 ? string.Empty : "s")}");
            }

            if (lineCount > 0)
            {
                parts.Add($"{lineCount} line{(lineCount == 1 ? string.Empty : "s")}");
            }

            PartialSelectionStatusText.Text = string.Join(" and ", parts) + " selected";
        }

        StagePartialButton.Visibility = supported
            && inChangesWorkspace
            && !selectionBlocked
            && !selectedChangeIsStaged
            ? Visibility.Visible
            : Visibility.Collapsed;
        UnstagePartialButton.Visibility = supported
            && inChangesWorkspace
            && !selectionBlocked
            && selectedChangeIsStaged
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClearPartialSelectionButton.Visibility = supported
            && inChangesWorkspace
            && !selectionBlocked
            && selectionCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        StagePartialButton.IsEnabled = canInteract
            && !selectionBlocked
            && !selectedChangeIsStaged
            && selectionCount > 0;
        UnstagePartialButton.IsEnabled = canInteract
            && !selectionBlocked
            && selectedChangeIsStaged
            && selectionCount > 0;
        ClearPartialSelectionButton.IsEnabled = canInteract && !selectionBlocked && selectionCount > 0;
        UpdateTextDiffListVisibility();
        ApplyConflictEditorListVisibility();
    }

    private void SelectFirstPartialLineForCapture()
    {
        if (!diagnosticCaptureMode
            || selectedPartialDiff?.IsSupported != true
            || TextDiffBlocksPartialSelection())
        {
            return;
        }

        var line = partialDiffRows.FirstOrDefault(row => row.IsSelectableLine);
        if (line is not null)
        {
            ApplyPartialSelection(line, selected: true);
        }
    }
}
