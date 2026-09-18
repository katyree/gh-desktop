using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private bool discardControlsInitialized;
    private readonly HashSet<ListViewItem> discardBoundContainers = [];

    /// <summary>
    /// Installs the discard commands on the existing native change and
    /// partial-diff lists. The hook only wires controls. It never reads or
    /// changes the repository until the user chooses a command.
    /// </summary>
    private void InitializeDiscardControls()
    {
        if (discardControlsInitialized)
        {
            return;
        }

        discardControlsInitialized = true;
        StagedChangesList.ContainerContentChanging += ChangeList_ContainerContentChanging;
        UnstagedChangesList.ContainerContentChanging += ChangeList_ContainerContentChanging;
        PartialDiffList.ContainerContentChanging += PartialDiffList_ContainerContentChanging;

        StagedChangesList.ContextFlyout = CreateDiscardListFlyout(StagedChangesList);
        UnstagedChangesList.ContextFlyout = CreateDiscardListFlyout(UnstagedChangesList);
        PartialDiffList.ContextFlyout = CreatePartialDiscardFlyout();
    }

    private void ChangeList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem item)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            item.ContextFlyout = null;
            return;
        }

        if (item.Content is not ChangeRow)
        {
            return;
        }

        var list = ReferenceEquals(sender, StagedChangesList)
            ? StagedChangesList
            : UnstagedChangesList;
        item.ContextFlyout = CreateDiscardItemFlyout(list, item);
        if (discardBoundContainers.Add(item))
        {
            item.AddHandler(
                UIElement.RightTappedEvent,
                new RightTappedEventHandler((_, _) => SelectDiscardContextItem(list, item)),
                handledEventsToo: true);
        }
    }

    private void PartialDiffList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem item)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            item.ContextFlyout = null;
            return;
        }

        if (item.Content is not PartialDiffRow)
        {
            return;
        }

        item.ContextFlyout = CreatePartialDiscardItemFlyout(item);
        if (discardBoundContainers.Add(item))
        {
            item.AddHandler(
                UIElement.RightTappedEvent,
                new RightTappedEventHandler((_, _) => SelectPartialDiscardContextItem(item)),
                handledEventsToo: true);
        }
    }

    private MenuFlyout CreateDiscardListFlyout(ListView list)
    {
        var menu = new MenuFlyout();
        var discardItem = new MenuFlyoutItem
        {
            Text = "Discard selected files…",
        };
        discardItem.Click += (_, _) =>
        {
            var rows = GetDiscardRows(list, contextItem: null);
            _ = StartFullDiscardAsync(GetDistinctDiscardFiles(rows));
        };
        menu.Opening += (_, _) =>
        {
            discardItem.IsEnabled = CanStartDiscard()
                && GetDistinctDiscardFiles(GetDiscardRows(list, contextItem: null)).Count > 0;
        };
        menu.Items.Add(discardItem);
        AddSelectedReviewMenuItem(menu);
        return menu;
    }

    private MenuFlyout CreateDiscardItemFlyout(ListView list, ListViewItem contextItem)
    {
        var menu = new MenuFlyout();
        var discardItem = new MenuFlyoutItem
        {
            Text = "Discard selected files…",
        };
        discardItem.Click += (_, _) =>
        {
            var rows = GetDiscardRows(list, contextItem);
            _ = StartFullDiscardAsync(GetDistinctDiscardFiles(rows));
        };
        menu.Opening += (_, _) =>
        {
            discardItem.IsEnabled = CanStartDiscard()
                && GetDistinctDiscardFiles(GetDiscardRows(list, contextItem)).Count > 0;
        };
        menu.Items.Add(discardItem);
        AddSelectedReviewMenuItem(menu);
        return menu;
    }

    private MenuFlyout CreatePartialDiscardFlyout()
    {
        var menu = new MenuFlyout();
        var discardItem = new MenuFlyoutItem
        {
            Text = "Discard selected lines…",
        };
        discardItem.Click += (_, _) => _ = StartPartialDiscardAsync();
        menu.Opening += (_, _) => discardItem.IsEnabled = CanStartPartialDiscard();
        menu.Items.Add(discardItem);
        return menu;
    }

    private MenuFlyout CreatePartialDiscardItemFlyout(ListViewItem contextItem)
    {
        var menu = new MenuFlyout();
        var discardItem = new MenuFlyoutItem
        {
            Text = "Discard selected lines…",
        };
        discardItem.Click += (_, _) => _ = StartPartialDiscardAsync();
        menu.Opening += (_, _) =>
        {
            if (contextItem.Content is PartialDiffRow row
                && row.CanSelect
                && !IsPartialRowSelected(row))
            {
                ApplyPartialSelection(row, selected: true);
            }

            discardItem.IsEnabled = CanStartPartialDiscard();
        };
        menu.Items.Add(discardItem);
        return menu;
    }

    private void SelectDiscardContextItem(ListView list, ListViewItem item)
    {
        if (item.Content is not ChangeRow row
            || list.SelectedItems.Contains(row))
        {
            return;
        }

        list.SelectedItems.Clear();
        list.SelectedItem = row;
    }

    private void SelectPartialDiscardContextItem(ListViewItem item)
    {
        if (item.Content is PartialDiffRow row
            && row.CanSelect
            && !IsPartialRowSelected(row))
        {
            ApplyPartialSelection(row, selected: true);
        }
    }

    private bool IsPartialRowSelected(PartialDiffRow row) =>
        partialSelections.Contains(row.Selection)
        || partialSelections.Contains(PartialDiffSelection.ForHunk(row.HunkId));

    private bool CanStartDiscard() => !diagnosticCaptureMode
        && repositoryRoot is not null
        && !mutationInProgress
        && currentWorkspace == "changes"
        && ChangesWorkspace.Visibility == Visibility.Visible
        && activeGitOperationKind == GitOperationKind.None;

    private bool CanStartPartialDiscard() => CanStartDiscard()
        && !selectedChangeIsStaged
        && selectedChange is not null
        && !IsConflictChange(selectedChange.Change)
        && selectedPartialDiff?.IsSupported == true
        && partialSelections.Count > 0;

    private IReadOnlyList<ChangeRow> GetDiscardRows(ListView list, ListViewItem? contextItem)
    {
        var selected = list.SelectedItems.OfType<ChangeRow>().ToList();
        if (contextItem?.Content is ChangeRow contextRow
            && !selected.Contains(contextRow))
        {
            selected.Clear();
            selected.Add(contextRow);
        }

        return selected;
    }

    private static IReadOnlyList<FileChange> GetDistinctDiscardFiles(
        IReadOnlyList<ChangeRow> rows)
    {
        var files = new List<FileChange>(rows.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (seen.Add(row.Change.Path))
            {
                files.Add(row.Change);
            }
        }

        return files;
    }

    private async Task StartFullDiscardAsync(IReadOnlyList<FileChange> files)
    {
        if (!CanStartDiscard() || files.Count == 0 || repositoryRoot is null)
        {
            return;
        }

        CancelCommitMessageGeneration();
        var root = repositoryRoot;
        var operation = BeginOperation("Checking selected changes…");
        DiscardSnapshot? snapshot = null;
        try
        {
            snapshot = await repositoryService.CaptureDiscardSnapshotAsync(
                root,
                files,
                operation.Token);
            if (!IsDiscardSessionCurrent(operation.Generation, operation.Token, root))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            return;
        }
        catch (DiscardSnapshotStaleException exception)
        {
            ShowError("Refresh required", exception);
            return;
        }
        catch (DiscardException exception)
        {
            ShowError("Unable to prepare discard", exception);
            return;
        }
        catch (Exception exception)
        {
            ShowError("Unable to prepare discard", exception);
            return;
        }
        finally
        {
            EndOperation(operation.Generation);
        }

        if (snapshot is null
            || !IsDiscardSessionCurrent(operation.Generation, operation.Token, root))
        {
            return;
        }

        ContentDialogResult result;
        try
        {
            var dialog = CreateDialog(
                $"Discard {snapshot.Files.Count} selected file{(snapshot.Files.Count == 1 ? string.Empty : "s")}?",
                "Discard changes",
                BuildFullDiscardConfirmationContent(snapshot));
            result = await dialog.ShowAsync();
        }
        catch (Exception exception)
        {
            ShowError("Unable to show discard confirmation", exception);
            return;
        }

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        if (!IsDiscardSessionCurrent(operation.Generation, operation.Token, root))
        {
            ShowError(
                "Discard selection changed",
                new InvalidOperationException(
                    "The repository or selected paths changed while the confirmation was open. Refresh and review the changes again."));
            return;
        }

        await RunFullDiscardMutationAsync(root, snapshot);
    }

    private async Task RunFullDiscardMutationAsync(string root, DiscardSnapshot snapshot)
    {
        if (!CanStartDiscard()
            || repositoryRoot is null
            || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        mutationInProgress = true;
        ErrorBar.IsOpen = false;
        var operation = BeginOperation(
            $"Discarding {snapshot.Files.Count} selected file{(snapshot.Files.Count == 1 ? string.Empty : "s")}…");
        try
        {
            await repositoryService.DiscardChangesAsync(
                root,
                snapshot,
                new WindowsRecycleBinFileRecycler(),
                operation.Token);
            StatusText.Text = "Selected changes discarded; repository refreshed.";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            StatusText.Text = "Discard cancelled; refreshing repository. Review selected files for partial progress.";
        }
        catch (DiscardSnapshotStaleException exception)
        {
            ShowError("Discard stopped; refresh required", exception);
        }
        catch (DiscardException exception)
        {
            ShowError("Discard stopped", exception);
            StatusText.Text = "Discard stopped; refreshing repository. Review which selected files remain.";
        }
        catch (Exception exception)
        {
            ShowError("Discard stopped", exception);
            StatusText.Text = "Discard stopped; refreshing repository. Review which selected files remain.";
        }
        finally
        {
            try
            {
                await RefreshAfterMutationAsync(root);
            }
            catch (Exception refreshException)
            {
                ShowError("Unable to refresh repository after discard", refreshException);
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
        }
    }

    private async Task StartPartialDiscardAsync()
    {
        if (!CanStartPartialDiscard() || repositoryRoot is null || selectedChange is null)
        {
            return;
        }

        var root = repositoryRoot;
        var row = selectedChange;
        var displayedDiff = selectedPartialDiff;
        var selection = partialSelections.ToArray();
        if (displayedDiff is null || GetSelectedPartialLineCount(displayedDiff, selection) == 0)
        {
            return;
        }

        CancelCommitMessageGeneration();
        var operation = BeginOperation("Checking selected lines…");
        PartialFileDiff? freshDiff = null;
        try
        {
            freshDiff = await repositoryService.GetPartialDiffAsync(
                root,
                row.Change,
                staged: false,
                operation.Token);
            if (!IsPartialDiscardSessionCurrent(
                    operation.Generation,
                    operation.Token,
                    root,
                    row,
                    displayedDiff,
                    selection))
            {
                return;
            }

            if (!AreSamePartialSnapshot(displayedDiff.Snapshot, freshDiff.Snapshot))
            {
                throw new StaleDiffSnapshotException(row.Path);
            }

            if (!freshDiff.IsSupported)
            {
                throw new PartialStagingUnsupportedException(
                    row.Path,
                    freshDiff.Message
                        ?? (freshDiff.IsBinary
                            ? "binary files are not supported"
                            : freshDiff.IsTruncated
                                ? "the diff is too large"
                                : "the diff does not contain selectable text changes"));
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            return;
        }
        catch (StaleDiffSnapshotException exception)
        {
            ShowError("Refresh required", exception);
            return;
        }
        catch (PartialStagingUnsupportedException exception)
        {
            ShowError("Partial selection unavailable", exception);
            return;
        }
        catch (Exception exception)
        {
            ShowError("Unable to prepare partial discard", exception);
            return;
        }
        finally
        {
            EndOperation(operation.Generation);
        }

        if (freshDiff is null
            || !IsPartialDiscardSessionCurrent(
                operation.Generation,
                operation.Token,
                root,
                row,
                displayedDiff,
                selection))
        {
            return;
        }

        ContentDialogResult result;
        try
        {
            var lineCount = GetSelectedPartialLineCount(freshDiff, selection);
            var hunkCount = selection.Count(item => item.LineIndex is null);
            var dialog = CreateDialog(
                $"Discard {lineCount} selected line{(lineCount == 1 ? string.Empty : "s")}?",
                "Discard lines",
                BuildPartialDiscardConfirmationContent(
                    row,
                    lineCount,
                    hunkCount));
            result = await dialog.ShowAsync();
        }
        catch (Exception exception)
        {
            ShowError("Unable to show partial discard confirmation", exception);
            return;
        }

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        if (!IsPartialDiscardSessionCurrent(
                operation.Generation,
                operation.Token,
                root,
                row,
                displayedDiff,
                selection))
        {
            ShowError(
                "Partial selection changed",
                new InvalidOperationException(
                    "The selected diff changed while the confirmation was open. Refresh and select the lines again."));
            return;
        }

        await RunPartialDiscardMutationAsync(root, freshDiff, selection);
    }

    private async Task RunPartialDiscardMutationAsync(
        string root,
        PartialFileDiff diff,
        IReadOnlyCollection<PartialDiffSelection> selection)
    {
        if (!CanStartPartialDiscard()
            || repositoryRoot is null
            || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        mutationInProgress = true;
        ErrorBar.IsOpen = false;
        var operation = BeginOperation("Discarding selected lines…");
        try
        {
            await repositoryService.DiscardSelectedChangesAsync(
                root,
                diff,
                selection,
                operation.Token);
            StatusText.Text = "Selected lines discarded; staged content was unchanged.";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            StatusText.Text = "Partial discard cancelled; refreshing repository. Review the current worktree.";
        }
        catch (StaleDiffSnapshotException exception)
        {
            ShowError("Partial discard stopped; refresh required", exception);
        }
        catch (PartialStagingUnsupportedException exception)
        {
            ShowError("Partial discard unavailable", exception);
        }
        catch (Exception exception)
        {
            ShowError("Partial discard stopped", exception);
            StatusText.Text = "Partial discard stopped; refreshing repository. Review the current worktree.";
        }
        finally
        {
            try
            {
                await RefreshAfterMutationAsync(root);
            }
            catch (Exception refreshException)
            {
                ShowError("Unable to refresh repository after partial discard", refreshException);
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
        }
    }

    private bool IsDiscardSessionCurrent(long generation, CancellationToken token, string root) =>
        IsCurrent(generation, token)
        && repositoryRoot is not null
        && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase)
        && currentWorkspace == "changes"
        && ChangesWorkspace.Visibility == Visibility.Visible;

    private bool IsPartialDiscardSessionCurrent(
        long generation,
        CancellationToken token,
        string root,
        ChangeRow row,
        PartialFileDiff diff,
        IReadOnlyCollection<PartialDiffSelection> selection) =>
        IsDiscardSessionCurrent(generation, token, root)
        && ReferenceEquals(selectedChange, row)
        && !selectedChangeIsStaged
        && ReferenceEquals(selectedPartialDiff, diff)
        && partialSelections.SetEquals(selection);

    private static bool AreSamePartialSnapshot(
        PartialDiffSnapshot expected,
        PartialDiffSnapshot actual) =>
        string.Equals(expected.RootPath, actual.RootPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.Path, actual.Path, StringComparison.Ordinal)
        && string.Equals(expected.OldPath, actual.OldPath, StringComparison.Ordinal)
        && expected.Staged == actual.Staged
        && string.Equals(expected.Fingerprint, actual.Fingerprint, StringComparison.Ordinal);

    private static int GetSelectedPartialLineCount(
        PartialFileDiff diff,
        IEnumerable<PartialDiffSelection> selection)
    {
        var selectedLines = new HashSet<(int HunkId, int LineIndex)>();
        foreach (var selected in selection)
        {
            var hunk = diff.Hunks.FirstOrDefault(candidate => candidate.Id == selected.HunkId);
            if (hunk is null)
            {
                continue;
            }

            if (selected.LineIndex is int lineIndex)
            {
                var line = hunk.Lines.FirstOrDefault(candidate => candidate.LineIndex == lineIndex);
                if (line?.IsSelectable == true)
                {
                    selectedLines.Add((hunk.Id, line.LineIndex));
                }

                continue;
            }

            foreach (var line in hunk.Lines.Where(candidate => candidate.IsSelectable))
            {
                selectedLines.Add((hunk.Id, line.LineIndex));
            }
        }

        return selectedLines.Count;
    }

    private static StackPanel BuildFullDiscardConfirmationContent(DiscardSnapshot snapshot)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "WinGit will move the selected working files to the Windows Recycle Bin, then restore their tracked Git index and worktree state. Staged changes on these paths are reset as part of the discard. Untracked files have no Git content to restore; they are moved to the Recycle Bin only. You can restore the moved files from the Recycle Bin if needed.",
            TextWrapping = TextWrapping.Wrap,
        });

        if (snapshot.IsUnborn)
        {
            content.Children.Add(new TextBlock
            {
                Text = "This repository has no commit yet, so Git has no HEAD content to restore.",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var pathPanel = new StackPanel { Spacing = 5 };
        foreach (var file in snapshot.Files)
        {
            pathPanel.Children.Add(new TextBlock
            {
                Text = $"{file.Path}  ·  {FormatDiscardFileKind(file)}  ·  {FormatDiscardContentScope(file)}",
                TextWrapping = TextWrapping.Wrap,
            });
            if (file.Kind is ChangeKind.Renamed or ChangeKind.Copied
                && !string.IsNullOrEmpty(file.OldPath))
            {
                pathPanel.Children.Add(new TextBlock
                {
                    Text = $"Original path: {file.OldPath}",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.8,
                });
            }
        }

        content.Children.Add(new TextBlock
        {
            Text = "Affected paths",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new ScrollViewer
        {
            Content = pathPanel,
            MaxHeight = 280,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        return content;
    }

    private static StackPanel BuildPartialDiscardConfirmationContent(
        ChangeRow row,
        int lineCount,
        int hunkCount)
    {
        var hunkText = hunkCount == 0
            ? string.Empty
            : $" across {hunkCount} selected hunk{(hunkCount == 1 ? string.Empty : "s")}";
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = $"Discard {lineCount} selected changed line{(lineCount == 1 ? string.Empty : "s")}{hunkText} in \"{row.Path}\"? WinGit will update only the working-tree content. Staged content and the index stay unchanged.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Git will revalidate the displayed diff before applying it. If the file changed, WinGit stops and asks you to refresh the selection.",
            TextWrapping = TextWrapping.Wrap,
        });
        return content;
    }

    private static string FormatDiscardFileKind(FileChange file) => file.Kind switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Deleted => "deleted",
        ChangeKind.Renamed => "renamed",
        ChangeKind.Copied => "copied",
        ChangeKind.Untracked => "untracked",
        ChangeKind.Conflicted => "conflict",
        ChangeKind.TypeChanged => "type changed",
        _ => "modified",
    };

    /// <summary>
    /// Describes which content a whole-file discard affects so the
    /// confirmation names staged, unstaged, or untracked scope per path,
    /// matching the Electron discard product semantics.
    /// </summary>
    private static string FormatDiscardContentScope(FileChange file)
    {
        if (file.Kind == ChangeKind.Untracked
            || string.Equals(file.IndexStatus, "?", StringComparison.Ordinal)
            || string.Equals(file.WorkTreeStatus, "?", StringComparison.Ordinal))
        {
            return "untracked content will be moved to the Recycle Bin";
        }

        if (file.Kind == ChangeKind.Deleted)
        {
            return HasIndexChanges(file)
                ? "staged deletion will be reset and the file restored from Git"
                : "deleted file will be restored from Git";
        }

        var staged = HasIndexChanges(file);
        var unstaged = HasWorkTreeChanges(file);
        return (staged, unstaged) switch
        {
            (true, true) => "staged and unstaged changes will be discarded",
            (true, false) => "staged changes will be discarded",
            (false, true) => "unstaged changes will be discarded",
            _ => "recorded change will be discarded",
        };
    }

    private static bool HasWorkTreeChanges(FileChange file) =>
        !string.IsNullOrWhiteSpace(file.WorkTreeStatus)
        && !string.Equals(file.WorkTreeStatus, "?", StringComparison.Ordinal)
        && !string.Equals(file.WorkTreeStatus, "!", StringComparison.Ordinal);
}
