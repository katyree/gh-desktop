using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;
using Windows.Foundation;

namespace WinGit.Native;

/// <summary>
/// Native, per-request selection state for the combined HEAD-to-working-tree
/// review diff. It deliberately has no relationship to the staging selector;
/// staging rows use a different (index/worktree) coordinate space.
/// </summary>
internal sealed class SelectedChangesReviewSelectionFileState
{
    public SelectedChangesReviewSelectionFileState(
        FileChange file,
        SelectedChangesReviewDiffSnapshot diffSnapshot)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        DiffSnapshot = diffSnapshot ?? throw new ArgumentNullException(nameof(diffSnapshot));
        DiffRows = PartialDiffRow.CreateRows(diffSnapshot.Diff).ToArray();
        IsIncluded = diffSnapshot.Diff.IsSupported;
    }

    public FileChange File { get; }

    public SelectedChangesReviewDiffSnapshot DiffSnapshot { get; }

    public IReadOnlyList<PartialDiffRow> DiffRows { get; }

    public HashSet<PartialDiffSelection> Selections { get; } = [];

    public bool IsIncluded { get; set; }

    public bool IncludeWholeFile { get; set; } = true;

    public bool IsSupported => DiffSnapshot.Diff.IsSupported;

    public bool IsReady => IsIncluded && (IncludeWholeFile || Selections.Count > 0);

    public int SelectedHunkCount => Selections.Count(selection => selection.LineIndex is null);

    public int SelectedLineCount => Selections.Count(selection => selection.LineIndex is not null);

    public SelectedChangesReviewFileSelection ToCoreSelection()
    {
        if (!IsIncluded)
        {
            throw new InvalidOperationException("An excluded review file cannot be submitted.");
        }

        return IncludeWholeFile
            ? new SelectedChangesReviewFileSelection(File, includeWholeFile: true)
            : new SelectedChangesReviewFileSelection(
                File,
                includeWholeFile: false,
                Selections.ToArray(),
                DiffSnapshot);
    }
}

internal sealed class SelectedChangesReviewSelectionControls
{
    public required SelectedChangesReviewSelectionFileState State { get; init; }

    public required FrameworkElement Panel { get; init; }

    public required CheckBox IncludeCheckBox { get; init; }

    public required CheckBox WholeFileCheckBox { get; init; }

    public required Button LineSelectionButton { get; init; }

    public required TextBlock SummaryText { get; init; }

    public required StackPanel DiffHost { get; init; }

    public Dictionary<PartialDiffRow, CheckBox> RowCheckBoxes { get; } = [];

    public ListView? DiffList { get; set; }
}

public sealed partial class MainWindow
{
    private async Task<IReadOnlyList<SelectedChangesReviewFileSelection>?>
        ShowSelectedChangesReviewSelectionAsync(
            long requestId,
            string root,
            IReadOnlyList<FileChange> files,
            string modelId,
            string modelSlug,
            string? reasoningEffort,
            IReadOnlyList<SelectedChangesReviewSelectionFileState> states,
            CancellationToken cancellationToken)
    {
        if (!IsSelectedReviewRequestCurrent(
                requestId,
                root,
                files,
                modelId,
                modelSlug,
                reasoningEffort,
                cancellationToken))
        {
            return null;
        }

        var content = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 980,
        };
        content.Children.Add(new TextBlock
        {
            Text = "Choose the exact changes to send to Codex. Whole-file review is selected by default; uncheck it to choose changed hunks or lines from that file.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "These controls use the immutable combined HEAD-to-working-tree diff captured for this request. They do not change the repository, index, working tree, or commit draft.",
            TextWrapping = TextWrapping.Wrap,
        });

        var filePanel = new StackPanel
        {
            Spacing = 12,
        };
        content.Children.Add(new ScrollViewer
        {
            Content = filePanel,
            MaxHeight = 600,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        ContentDialog? dialog = null;
        var controlGroups = new List<SelectedChangesReviewSelectionControls>(states.Count);
        Action refresh = () => { };
        SelectedChangesReviewSelectionControls? activeLineEditor = null;
        void ShowLineEditor(SelectedChangesReviewSelectionControls group)
        {
            if (ReferenceEquals(activeLineEditor, group))
            {
                group.State.IncludeWholeFile = false;
                refresh();
                return;
            }

            if (activeLineEditor is not null)
            {
                activeLineEditor.DiffList = null;
                activeLineEditor.RowCheckBoxes.Clear();
                activeLineEditor.DiffHost.Children.Clear();
                activeLineEditor.DiffHost.Children.Add(new TextBlock
                {
                    Text = activeLineEditor.State.IncludeWholeFile
                        ? "Whole file selected by default. Choose lines or hunks to review only part of this file."
                        : "Partial selection saved. Choose lines or hunks to edit this file again.",
                    TextWrapping = TextWrapping.Wrap,
                });
                activeLineEditor.LineSelectionButton.Content = "Choose lines or hunks…";
            }

            activeLineEditor = group;
            group.State.IncludeWholeFile = false;
            group.DiffHost.Children.Clear();
            group.LineSelectionButton.Content = "Editing selected lines and hunks";
            BuildSelectedReviewLineEditor(group, refresh);
            refresh();
        }

        refresh = () =>
        {
            foreach (var group in controlGroups)
            {
                RefreshSelectedReviewSelectionControls(group);
            }

            if (dialog is not null)
            {
                dialog.IsPrimaryButtonEnabled = CanSubmitSelectedReviewSelections(states);
            }
        };

        foreach (var state in states)
        {
            var group = BuildSelectedReviewSelectionFilePanel(state, refresh, ShowLineEditor);
            controlGroups.Add(group);
            filePanel.Children.Add(group.Panel);
        }

        dialog = CreateDialog("Choose changes to review", "Review selected changes", content);
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.IsPrimaryButtonEnabled = CanSubmitSelectedReviewSelections(states);
        AutomationProperties.SetName(dialog, "Choose selected changes for Codex review");
        selectedChangesReviewDialog = dialog;
        selectedChangesReviewDialogClosed = false;

        TypedEventHandler<ContentDialog, ContentDialogClosedEventArgs> closedHandler = (_, _) =>
        {
            selectedChangesReviewDialogClosed = true;
        };
        dialog.Closed += closedHandler;

        try
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary
                || !IsSelectedReviewRequestCurrent(
                    requestId,
                    root,
                    files,
                    modelId,
                    modelSlug,
                    reasoningEffort,
                    cancellationToken))
            {
                return null;
            }

            if (!CanSubmitSelectedReviewSelections(states))
            {
                return null;
            }

            var selections = states
                .Where(state => state.IsIncluded)
                .Select(state => state.ToCoreSelection())
                .ToArray();
            return selections.Length == 0 ? null : selections;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ShowError("Unable to choose selected changes", exception);
            }

            return null;
        }
        finally
        {
            dialog.Closed -= closedHandler;
            if (ReferenceEquals(selectedChangesReviewDialog, dialog))
            {
                selectedChangesReviewDialog = null;
            }
        }
    }

    private static bool CanSubmitSelectedReviewSelections(
        IReadOnlyList<SelectedChangesReviewSelectionFileState> states) =>
        states.Any(state => state.IsIncluded)
        && states.Where(state => state.IsIncluded).All(state => state.IsReady);

    private async Task<bool> RevalidateSelectedReviewSelectionPreviewsAsync(
        string root,
        IReadOnlyList<SelectedChangesReviewSelectionFileState> states,
        CancellationToken cancellationToken)
    {
        foreach (var state in states.Where(state => state.IsIncluded))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await repositoryService.GetSelectedChangesReviewDiffAsync(
                root,
                state.File,
                cancellationToken);
            if (!string.Equals(
                    current.Fingerprint,
                    state.DiffSnapshot.Fingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private SelectedChangesReviewSelectionControls BuildSelectedReviewSelectionFilePanel(
        SelectedChangesReviewSelectionFileState state,
        Action refresh,
        Action<SelectedChangesReviewSelectionControls> showLineEditor)
    {
        var includeCheckBox = new CheckBox
        {
            Content = $"{state.File.Path} · {FormatSelectedReviewFileKind(state.File.Kind)}",
            IsChecked = state.IsIncluded,
            IsEnabled = state.IsSupported,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(
            includeCheckBox,
            $"Include {state.File.Path} in Codex review");

        var wholeFileCheckBox = new CheckBox
        {
            Content = "Review the whole file",
            IsChecked = state.IncludeWholeFile,
            IsEnabled = state.IsSupported && state.IsIncluded,
            Margin = new Thickness(24, 0, 0, 0),
        };
        AutomationProperties.SetName(
            wholeFileCheckBox,
            $"Review the whole file {state.File.Path}");

        var summaryText = new TextBlock
        {
            Margin = new Thickness(48, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var diffHost = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(48, 0, 0, 0),
        };
        diffHost.Children.Add(new TextBlock
        {
            Text = state.IsSupported
                ? "Whole file selected by default. Choose lines or hunks to review only part of this file."
                : state.DiffSnapshot.Diff.Message
                    ?? "This file does not contain selectable text changes.",
            TextWrapping = TextWrapping.Wrap,
        });

        includeCheckBox.Click += (_, _) =>
        {
            state.IsIncluded = includeCheckBox.IsChecked == true;
            if (!state.IsIncluded)
            {
                state.IncludeWholeFile = true;
                state.Selections.Clear();
            }

            refresh();
        };
        wholeFileCheckBox.Click += (_, _) =>
        {
            state.IncludeWholeFile = wholeFileCheckBox.IsChecked == true;
            if (state.IncludeWholeFile)
            {
                state.Selections.Clear();
            }

            refresh();
        };

        var lineSelectionButton = new Button
        {
            Content = "Choose lines or hunks…",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(24, 0, 0, 0),
            IsEnabled = state.IsSupported && state.IsIncluded,
        };
        AutomationProperties.SetName(
            lineSelectionButton,
            $"Choose lines or hunks from {state.File.Path}");
        SelectedChangesReviewSelectionControls? controls = null;
        lineSelectionButton.Click += (_, _) =>
        {
            if (controls is not null && state.IsSupported && state.IsIncluded)
            {
                showLineEditor(controls);
            }
        };

        var panel = new StackPanel
        {
            Spacing = 4,
        };
        panel.Children.Add(includeCheckBox);
        panel.Children.Add(wholeFileCheckBox);
        panel.Children.Add(lineSelectionButton);
        panel.Children.Add(summaryText);
        panel.Children.Add(diffHost);

        var result = new SelectedChangesReviewSelectionControls
        {
            State = state,
            Panel = panel,
            IncludeCheckBox = includeCheckBox,
            WholeFileCheckBox = wholeFileCheckBox,
            LineSelectionButton = lineSelectionButton,
            SummaryText = summaryText,
            DiffHost = diffHost,
        };
        controls = result;
        RefreshSelectedReviewSelectionControls(result);
        return result;
    }

    private void BuildSelectedReviewLineEditor(
        SelectedChangesReviewSelectionControls controls,
        Action refresh)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
            MaxHeight = 290,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Enabled);
        AutomationProperties.SetName(
            list,
            $"Choose changed lines or hunks from {controls.State.File.Path}");
        list.ContainerContentChanging += (_, args) =>
        {
            if (args.ItemContainer is not ListViewItem item)
            {
                return;
            }

            if (item.Tag is PartialDiffRow previousRow)
            {
                controls.RowCheckBoxes.Remove(previousRow);
                item.Tag = null;
            }

            if (args.InRecycleQueue)
            {
                item.Content = null;
                return;
            }

            if (args.Item is not PartialDiffRow row)
            {
                item.Content = null;
                return;
            }

            item.Tag = row;
            item.Content = BuildSelectedReviewLineElement(controls, row, refresh);
        };

        list.ItemsSource = controls.State.DiffRows;
        controls.DiffList = list;
        controls.DiffHost.Children.Add(list);
        RefreshSelectedReviewSelectionControls(controls);
    }

    private FrameworkElement BuildSelectedReviewLineElement(
        SelectedChangesReviewSelectionControls controls,
        PartialDiffRow row,
        Action refresh)
    {
        if (row.IsHunk)
        {
            var checkBox = new CheckBox
            {
                Content = row.Header,
                IsThreeState = true,
                IsChecked = GetSelectedReviewRowCheckState(controls.State, row),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(4, 2, 4, 2),
            };
            AutomationProperties.SetName(checkBox, row.SelectionAutomationName);
            checkBox.Click += (_, _) =>
            {
                SetSelectedReviewRowSelection(
                    controls.State,
                    row,
                    checkBox.IsChecked == true);
                refresh();
            };
            controls.RowCheckBoxes[row] = checkBox;
            return checkBox;
        }

        var lineGrid = new Grid
        {
            MinHeight = 24,
            Padding = new Thickness(4, 0, 4, 0),
        };
        lineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        lineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        lineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        lineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        lineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (row.CanSelect)
        {
            var checkBox = new CheckBox
            {
                IsChecked = GetSelectedReviewRowCheckState(controls.State, row) == true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
            };
            AutomationProperties.SetName(checkBox, row.SelectionAutomationName);
            checkBox.Click += (_, _) =>
            {
                SetSelectedReviewRowSelection(
                    controls.State,
                    row,
                    checkBox.IsChecked == true);
                refresh();
            };
            controls.RowCheckBoxes[row] = checkBox;
            Grid.SetColumn(checkBox, 0);
            lineGrid.Children.Add(checkBox);
        }

        AddSelectedReviewSelectionText(
            lineGrid,
            row.OldLineNumber,
            column: 1,
            horizontalAlignment: HorizontalAlignment.Right);
        AddSelectedReviewSelectionText(
            lineGrid,
            row.NewLineNumber,
            column: 2,
            horizontalAlignment: HorizontalAlignment.Right);
        AddSelectedReviewSelectionText(
            lineGrid,
            row.Marker,
            column: 3,
            horizontalAlignment: HorizontalAlignment.Center);
        AddSelectedReviewSelectionText(
            lineGrid,
            row.Text,
            column: 4,
            horizontalAlignment: HorizontalAlignment.Left,
            noWrap: true);
        return lineGrid;
    }

    private void RefreshSelectedReviewSelectionControls(
        SelectedChangesReviewSelectionControls controls)
    {
        var state = controls.State;
        controls.IncludeCheckBox.IsChecked = state.IsIncluded;
        controls.IncludeCheckBox.IsEnabled = state.IsSupported;
        controls.WholeFileCheckBox.IsChecked = state.IncludeWholeFile;
        controls.WholeFileCheckBox.IsEnabled = state.IsSupported && state.IsIncluded;
        controls.LineSelectionButton.IsEnabled = state.IsSupported && state.IsIncluded;
        if (controls.DiffList is not null)
        {
            controls.DiffList.IsEnabled = state.IsSupported
                && state.IsIncluded
                && !state.IncludeWholeFile;
        }
        foreach (var pair in controls.RowCheckBoxes)
        {
            var row = pair.Key;
            var checkBox = pair.Value;
            checkBox.IsEnabled = state.IsSupported
                && state.IsIncluded
                && !state.IncludeWholeFile
                && row.CanSelect;
            checkBox.IsChecked = GetSelectedReviewRowCheckState(state, row);
        }

        controls.SummaryText.Text = !state.IsIncluded
            ? "Excluded from this review."
            : state.IncludeWholeFile
                ? "Whole file selected (HEAD to working tree)."
                : state.Selections.Count == 0
                    ? "Choose one or more changed lines or hunks."
                    : FormatSelectedReviewSelectionSummary(state);
    }

    private static string FormatSelectedReviewSelectionSummary(
        SelectedChangesReviewSelectionFileState state)
    {
        var parts = new List<string>(2);
        if (state.SelectedHunkCount > 0)
        {
            parts.Add($"{state.SelectedHunkCount} hunk{(state.SelectedHunkCount == 1 ? string.Empty : "s")}");
        }

        if (state.SelectedLineCount > 0)
        {
            parts.Add($"{state.SelectedLineCount} line{(state.SelectedLineCount == 1 ? string.Empty : "s")}");
        }

        return string.Join(" and ", parts) + " selected.";
    }

    private static string FormatSelectedReviewFileKind(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Modified => "modified",
        ChangeKind.Deleted => "deleted",
        ChangeKind.Renamed => "renamed",
        ChangeKind.Copied => "copied",
        ChangeKind.Untracked => "untracked",
        ChangeKind.Conflicted => "conflicted",
        _ => "changed",
    };

    private static void AddSelectedReviewSelectionText(
        Grid grid,
        string text,
        int column,
        HorizontalAlignment horizontalAlignment,
        bool noWrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
            FontSize = 12,
            TextWrapping = noWrap ? TextWrapping.NoWrap : TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 6, 0),
        };
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private static bool? GetSelectedReviewRowCheckState(
        SelectedChangesReviewSelectionFileState state,
        PartialDiffRow row)
    {
        if (!row.CanSelect)
        {
            return null;
        }

        if (row.IsHunk)
        {
            var hunkSelected = state.Selections.Contains(
                PartialDiffSelection.ForHunk(row.HunkId));
            var selectedLineCount = state.Selections.Count(selection =>
                selection.HunkId == row.HunkId && selection.LineIndex is not null);
            var selectableLineCount = state.DiffSnapshot.Diff.Hunks
                .FirstOrDefault(hunk => hunk.Id == row.HunkId)?.Lines
                .Count(line => line.IsSelectable) ?? 0;
            var allLinesSelected = selectableLineCount > 0
                && selectedLineCount == selectableLineCount;
            return hunkSelected || allLinesSelected
                ? true
                : selectedLineCount > 0
                    ? null
                    : false;
        }

        return state.Selections.Contains(PartialDiffSelection.ForHunk(row.HunkId))
            || state.Selections.Contains(row.Selection);
    }

    private static void SetSelectedReviewRowSelection(
        SelectedChangesReviewSelectionFileState state,
        PartialDiffRow row,
        bool selected)
    {
        var hunkSelection = PartialDiffSelection.ForHunk(row.HunkId);
        if (row.IsHunk)
        {
            state.Selections.RemoveWhere(selection => selection.HunkId == row.HunkId);
            if (selected)
            {
                state.Selections.Add(hunkSelection);
            }

            return;
        }

        var hunkWasSelected = state.Selections.Contains(hunkSelection);
        state.Selections.Remove(hunkSelection);
        if (!selected && hunkWasSelected)
        {
            var hunk = state.DiffSnapshot.Diff.Hunks.FirstOrDefault(
                candidate => candidate.Id == row.HunkId);
            if (hunk is not null)
            {
                foreach (var line in hunk.Lines.Where(line =>
                             line.IsSelectable && line.LineIndex != row.LineIndex))
                {
                    state.Selections.Add(
                        PartialDiffSelection.ForLine(hunk.Id, line.LineIndex));
                }
            }
        }
        else if (selected)
        {
            state.Selections.Add(row.Selection);
        }
        else
        {
            state.Selections.Remove(row.Selection);
        }
    }
}
