using System.Collections.ObjectModel;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Text;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<DiffSideBySideRow> diffSideBySideRows = [];
    private readonly ObservableCollection<DiffSideBySideRow> historyDiffSideBySideRows = [];
    private readonly Dictionary<int, int> changesSplitRowByLine = [];
    private readonly Dictionary<int, int> historySplitRowByLine = [];
    private FileDiff? currentChangesDiff;
    private FileDiff? currentHistoryDiff;
    private bool currentChangesDiffIsText;
    private bool currentHistoryDiffIsText;
    private bool applyingTextDiffSettings;
    private bool resettingTextDiffSearch;
    private string textDiffMode = NativeSettings.DefaultTextDiffMode;
    private bool hideWhitespaceChanges;
    private string changesSearchQuery = string.Empty;
    private string historySearchQuery = string.Empty;
    private int changesSearchIndex = -1;
    private int historySearchIndex = -1;

    private void InitializeTextDiffControls()
    {
        DiffSideBySideList.ItemsSource = diffSideBySideRows;
        HistoryDiffSideBySideList.ItemsSource = historyDiffSideBySideRows;

        var searchAccelerator = new KeyboardAccelerator
        {
            Key = VirtualKey.F,
            Modifiers = VirtualKeyModifiers.Control,
        };
        searchAccelerator.Invoked += DiffSearchAccelerator_Invoked;
        RootGrid.KeyboardAccelerators.Add(searchAccelerator);
        UpdateTextDiffControls();
    }

    private void ApplyTextDiffSettingsToControls()
    {
        textDiffMode = NativeSettingsStore.NormalizeTextDiffMode(settings.TextDiffMode);
        settings.TextDiffMode = textDiffMode;
        hideWhitespaceChanges = settings.HideWhitespaceChanges;
        applyingTextDiffSettings = true;
        try
        {
            SetDiffModeComboValue(DiffModeComboBox, textDiffMode);
            SetDiffModeComboValue(HistoryDiffModeComboBox, textDiffMode);
            HideWhitespaceChangesCheckBox.IsChecked = hideWhitespaceChanges;
            HistoryHideWhitespaceChangesCheckBox.IsChecked = hideWhitespaceChanges;
        }
        finally
        {
            applyingTextDiffSettings = false;
        }

        UpdateTextDiffControls();
    }

    private static void SetDiffModeComboValue(ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                value,
                StringComparison.Ordinal));
    }

    private void DiffModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (applyingTextDiffSettings
            || sender is not ComboBox comboBox
            || comboBox.SelectedItem is not ComboBoxItem item
            || item.Tag is not string mode)
        {
            return;
        }

        textDiffMode = NativeSettingsStore.NormalizeTextDiffMode(mode);
        settings.TextDiffMode = textDiffMode;
        applyingTextDiffSettings = true;
        try
        {
            SetDiffModeComboValue(DiffModeComboBox, textDiffMode);
            SetDiffModeComboValue(HistoryDiffModeComboBox, textDiffMode);
        }
        finally
        {
            applyingTextDiffSettings = false;
        }

        RenderCurrentTextDiff(history: false);
        RenderCurrentTextDiff(history: true);
        UpdatePartialSelectionControls();
        if (!diagnosticCaptureMode)
        {
            _ = SaveSettingsAsync();
        }
    }

    private async void HideWhitespaceChangesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingTextDiffSettings || sender is not CheckBox checkBox)
        {
            return;
        }

        var value = checkBox.IsChecked == true;
        if (value == hideWhitespaceChanges)
        {
            return;
        }

        hideWhitespaceChanges = value;
        settings.HideWhitespaceChanges = value;
        InvalidateTextDiffCache(history: false);
        InvalidateTextDiffCache(history: true);
        if (value)
        {
            partialSelections.Clear();
            SyncPartialRowChecks();
        }

        applyingTextDiffSettings = true;
        try
        {
            HideWhitespaceChangesCheckBox.IsChecked = value;
            HistoryHideWhitespaceChangesCheckBox.IsChecked = value;
        }
        finally
        {
            applyingTextDiffSettings = false;
        }

        if (!diagnosticCaptureMode)
        {
            _ = SaveSettingsAsync();
        }

        if (currentWorkspace == "changes" && selectedChange is not null)
        {
            latestOperationTask = LoadWorkingDiffAsync(selectedChange);
            await latestOperationTask;
        }
        else if (currentWorkspace == "history" && selectedCommitFile is not null)
        {
            if (historyCommitSelectionSnapshot is { } snapshot)
            {
                latestOperationTask = LoadCommitSelectionDiffAsync(snapshot, selectedCommitFile);
            }
            else if (selectedCommit is { } commit)
            {
                latestOperationTask = LoadCommitDiffAsync(commit, selectedCommitFile);
            }

            if (latestOperationTask is not null)
            {
                await latestOperationTask;
            }
        }
        else
        {
            RenderCurrentTextDiff(history: false);
            RenderCurrentTextDiff(history: true);
            UpdatePartialSelectionControls();
        }
    }

    private void DiffSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (resettingTextDiffSearch)
        {
            return;
        }

        var history = ReferenceEquals(sender, HistoryDiffSearchBox);
        SetSearchQuery(history, (sender as TextBox)?.Text ?? string.Empty);
    }

    private void DiffSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var history = ReferenceEquals(sender, HistoryDiffSearchBox);
        if (e.Key == VirtualKey.Enter)
        {
            var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(CoreVirtualKeyStates.Down);
            MoveSearch(history, shiftDown ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            ResetTextDiffSearch(history);
            e.Handled = true;
        }
    }

    private void DiffSearchPreviousButton_Click(object sender, RoutedEventArgs e) =>
        MoveSearch(history: false, -1);

    private void DiffSearchNextButton_Click(object sender, RoutedEventArgs e) =>
        MoveSearch(history: false, 1);

    private void HistoryDiffSearchPreviousButton_Click(object sender, RoutedEventArgs e) =>
        MoveSearch(history: true, -1);

    private void HistoryDiffSearchNextButton_Click(object sender, RoutedEventArgs e) =>
        MoveSearch(history: true, 1);

    private void DiffSearchAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        var searchBox = currentWorkspace == "history"
            ? HistoryDiffSearchBox
            : currentWorkspace == "changes"
                ? DiffSearchBox
                : null;
        if (searchBox is null || !searchBox.IsEnabled)
        {
            return;
        }

        searchBox.Focus(FocusState.Keyboard);
        searchBox.SelectAll();
        args.Handled = true;
    }

    private void SetSearchQuery(bool history, string query)
    {
        var split = textDiffMode == "Split";
        if (history)
        {
            historySearchQuery = query;
            historySearchIndex = -1;
            if (query.Length > 0 && currentHistoryDiffIsText)
            {
                historySearchIndex = GetSearchMatches(currentHistoryDiff, query, split).Count > 0 ? 0 : -1;
            }

            RenderCurrentTextDiff(history: true);
            if (historySearchIndex >= 0)
            {
                ScrollToSearchMatch(
                    history: true,
                    GetSearchMatches(currentHistoryDiff, query, split)[historySearchIndex]);
            }
        }
        else
        {
            changesSearchQuery = query;
            changesSearchIndex = -1;
            if (query.Length > 0 && currentChangesDiffIsText)
            {
                changesSearchIndex = GetSearchMatches(currentChangesDiff, query, split).Count > 0 ? 0 : -1;
            }

            RenderCurrentTextDiff(history: false);
            if (changesSearchIndex >= 0)
            {
                ScrollToSearchMatch(
                    history: false,
                    GetSearchMatches(currentChangesDiff, query, split)[changesSearchIndex]);
            }
            UpdatePartialSelectionControls();
        }
    }

    private void MoveSearch(bool history, int direction)
    {
        var diff = history ? currentHistoryDiff : currentChangesDiff;
        var query = history ? historySearchQuery : changesSearchQuery;
        var split = textDiffMode == "Split";
        if (diff is null || !IsTextDiff(diff) || query.Length == 0)
        {
            UpdateSearchStatus(history, 0, -1);
            return;
        }

        var matches = GetSearchMatches(diff, query, split);
        if (matches.Count == 0)
        {
            if (history)
            {
                historySearchIndex = -1;
            }
            else
            {
                changesSearchIndex = -1;
            }

            UpdateSearchStatus(history, 0, -1);
            return;
        }

        var currentIndex = history ? historySearchIndex : changesSearchIndex;
        currentIndex = currentIndex < 0
            ? direction > 0 ? 0 : matches.Count - 1
            : (currentIndex + direction + matches.Count) % matches.Count;
        if (history)
        {
            historySearchIndex = currentIndex;
        }
        else
        {
            changesSearchIndex = currentIndex;
        }

        RenderCurrentTextDiff(history);
        ScrollToSearchMatch(history, matches[currentIndex]);
    }

    private void ScrollToSearchMatch(bool history, DiffSearchMatch match)
    {
        var lineIndex = match.LineIndex;
        var list = history
            ? textDiffMode == "Split" ? HistoryDiffSideBySideList : HistoryDiffList
            : textDiffMode == "Split" ? DiffSideBySideList : DiffList;
        object? item;
        if (history)
        {
            item = textDiffMode == "Split"
                ? historyDiffSideBySideRows.ElementAtOrDefault(
                    historySplitRowByLine.TryGetValue(lineIndex, out var historyRow) ? historyRow : -1)
                : historyDiffRows.ElementAtOrDefault(lineIndex);
        }
        else
        {
            item = textDiffMode == "Split"
                ? diffSideBySideRows.ElementAtOrDefault(
                    changesSplitRowByLine.TryGetValue(lineIndex, out var changesRow) ? changesRow : -1)
                : diffRows.ElementAtOrDefault(lineIndex);
        }
        if (item is null)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            list.UpdateLayout();
            list.ScrollIntoView(item);
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                list.UpdateLayout();
                list.ScrollIntoView(item);
            });
        });
    }

    private void BeginTextDiffRender(ListView list, FileDiff diff)
    {
        var history = ReferenceEquals(list, HistoryDiffList);
        if (history)
        {
            currentHistoryDiff = diff;
            currentHistoryDiffIsText = false;
            ResetTextDiffSearch(history: true);
        }
        else
        {
            currentChangesDiff = diff;
            currentChangesDiffIsText = false;
            ResetTextDiffSearch(history: false);
        }

        UpdateTextDiffControls();
    }

    private void MarkTextDiffReady(ListView list)
    {
        var history = ReferenceEquals(list, HistoryDiffList);
        if (history)
        {
            currentHistoryDiffIsText = true;
            RenderCurrentTextDiff(history: true);
        }
        else
        {
            currentChangesDiffIsText = true;
            RenderCurrentTextDiff(history: false);
        }
    }

    private void ClearTextDiffState()
    {
        currentChangesDiff = null;
        currentHistoryDiff = null;
        currentChangesDiffIsText = false;
        currentHistoryDiffIsText = false;
        diffSideBySideRows.Clear();
        historyDiffSideBySideRows.Clear();
        changesSplitRowByLine.Clear();
        historySplitRowByLine.Clear();
        ResetTextDiffSearch(history: false);
        ResetTextDiffSearch(history: true);
        UpdateTextDiffControls();
    }

    private void InvalidateTextDiffCache(bool history)
    {
        if (history)
        {
            currentHistoryDiff = null;
            currentHistoryDiffIsText = false;
            historyDiffRows.Clear();
            historyDiffSideBySideRows.Clear();
            historySplitRowByLine.Clear();
            HistoryDiffList.ItemsSource = historyDiffRows;
            HistoryDiffSideBySideList.ItemsSource = historyDiffSideBySideRows;
            ResetTextDiffSearch(history: true);
        }
        else
        {
            currentChangesDiff = null;
            currentChangesDiffIsText = false;
            diffRows.Clear();
            diffSideBySideRows.Clear();
            changesSplitRowByLine.Clear();
            DiffList.ItemsSource = diffRows;
            DiffSideBySideList.ItemsSource = diffSideBySideRows;
            ResetTextDiffSearch(history: false);
        }

        UpdateTextDiffControls();
    }

    private void ResetTextDiffSearch(bool history)
    {
        resettingTextDiffSearch = true;
        try
        {
            if (history)
            {
                historySearchQuery = string.Empty;
                historySearchIndex = -1;
                HistoryDiffSearchBox.Text = string.Empty;
            }
            else
            {
                changesSearchQuery = string.Empty;
                changesSearchIndex = -1;
                DiffSearchBox.Text = string.Empty;
            }
        }
        finally
        {
            resettingTextDiffSearch = false;
        }

        UpdateSearchStatus(history, 0, -1);
        if (history ? currentHistoryDiffIsText : currentChangesDiffIsText)
        {
            RenderCurrentTextDiff(history);
        }
        else
        {
            UpdateTextDiffListVisibility();
            if (!history)
            {
                UpdatePartialSelectionControls();
            }
        }
    }

    private void RenderCurrentTextDiff(bool history)
    {
        var diff = history ? currentHistoryDiff : currentChangesDiff;
        var isText = history ? currentHistoryDiffIsText : currentChangesDiffIsText;
        if (diff is null || !isText)
        {
            UpdateTextDiffControls();
            return;
        }

        var query = history ? historySearchQuery : changesSearchQuery;
        var selectedIndex = history ? historySearchIndex : changesSearchIndex;
        var split = textDiffMode == "Split";
        var matches = GetSearchMatches(diff, query, split);
        if (matches.Count == 0)
        {
            selectedIndex = -1;
        }
        else if (selectedIndex < 0 || selectedIndex >= matches.Count)
        {
            selectedIndex = 0;
        }

        if (history)
        {
            historySearchIndex = selectedIndex;
        }
        else
        {
            changesSearchIndex = selectedIndex;
        }

        var target = history ? historyDiffRows : diffRows;
        var splitTarget = history ? historyDiffSideBySideRows : diffSideBySideRows;
        var splitRowByLine = history ? historySplitRowByLine : changesSplitRowByLine;
        target.Clear();
        splitTarget.Clear();
        splitRowByLine.Clear();
        var selectedMatch = selectedIndex >= 0 && selectedIndex < matches.Count
            ? matches[selectedIndex]
            : (DiffSearchMatch?)null;
        var unifiedRanges = BuildSearchRanges(matches, DiffSearchColumn.Unified, selectedMatch);
        var oldRanges = BuildSearchRanges(matches, DiffSearchColumn.Old, selectedMatch);
        var newRanges = BuildSearchRanges(matches, DiffSearchColumn.New, selectedMatch);
        for (var index = 0; index < diff.Lines.Count; index++)
        {
            var line = diff.Lines[index];
            target.Add(new DiffRow(
                line,
                unifiedRanges.TryGetValue(index, out var ranges) ? ranges : []));
        }

        RenderSplitRows(diff, splitTarget, splitRowByLine, oldRanges, newRanges);

        UpdateSearchStatus(history, matches.Count, selectedIndex);
        UpdateTextDiffListVisibility();
        UpdateTextDiffControls();
    }

    private static void RenderSplitRows(
        FileDiff diff,
        ObservableCollection<DiffSideBySideRow> target,
        Dictionary<int, int> rowByLine,
        IReadOnlyDictionary<int, IReadOnlyList<DiffSearchRange>> oldSearchRanges,
        IReadOnlyDictionary<int, IReadOnlyList<DiffSearchRange>> newSearchRanges)
    {
        for (var index = 0; index < diff.Lines.Count;)
        {
            var line = diff.Lines[index];
            if (line.Kind == DiffLineKind.Removed)
            {
                var removed = new List<(DiffLine Line, int Index)>();
                while (index < diff.Lines.Count && diff.Lines[index].Kind == DiffLineKind.Removed)
                {
                    removed.Add((diff.Lines[index], index));
                    index++;
                }

                var added = new List<(DiffLine Line, int Index)>();
                while (index < diff.Lines.Count && diff.Lines[index].Kind == DiffLineKind.Added)
                {
                    added.Add((diff.Lines[index], index));
                    index++;
                }

                AddPairedSplitRows(removed, added, target, rowByLine, oldSearchRanges, newSearchRanges);
                continue;
            }

            if (line.Kind == DiffLineKind.Added)
            {
                var added = new List<(DiffLine Line, int Index)>();
                while (index < diff.Lines.Count && diff.Lines[index].Kind == DiffLineKind.Added)
                {
                    added.Add((diff.Lines[index], index));
                    index++;
                }

                AddPairedSplitRows([], added, target, rowByLine, oldSearchRanges, newSearchRanges);
                continue;
            }

            var rowIndex = target.Count;
            target.Add(new DiffSideBySideRow(
                oldLine: null,
                newLine: null,
                sharedLine: line.Kind == DiffLineKind.Context ? line : null,
                headerLine: line,
                oldSearchMatches: oldSearchRanges.TryGetValue(index, out var oldRanges) ? oldRanges : [],
                newSearchMatches: newSearchRanges.TryGetValue(index, out var newRanges) ? newRanges : []));
            rowByLine[index] = rowIndex;
            index++;
        }
    }

    private static void AddPairedSplitRows(
        IReadOnlyList<(DiffLine Line, int Index)> removed,
        IReadOnlyList<(DiffLine Line, int Index)> added,
        ObservableCollection<DiffSideBySideRow> target,
        Dictionary<int, int> rowByLine,
        IReadOnlyDictionary<int, IReadOnlyList<DiffSearchRange>> oldSearchRanges,
        IReadOnlyDictionary<int, IReadOnlyList<DiffSearchRange>> newSearchRanges)
    {
        var rowCount = Math.Max(removed.Count, added.Count);
        for (var offset = 0; offset < rowCount; offset++)
        {
            var old = offset < removed.Count ? removed[offset] : ((DiffLine Line, int Index)?)null;
            var @new = offset < added.Count ? added[offset] : ((DiffLine Line, int Index)?)null;
            var representative = old?.Line ?? @new?.Line;
            if (representative is null)
            {
                continue;
            }

            var rowIndex = target.Count;
            target.Add(new DiffSideBySideRow(
                old?.Line,
                @new?.Line,
                sharedLine: null,
                headerLine: representative,
                oldSearchMatches: old is { } oldLine
                    && oldSearchRanges.TryGetValue(oldLine.Index, out var oldRanges)
                    ? oldRanges
                    : [],
                newSearchMatches: @new is { } newLine
                    && newSearchRanges.TryGetValue(newLine.Index, out var newRanges)
                    ? newRanges
                    : []));
            if (old is { } oldSource)
            {
                rowByLine[oldSource.Index] = rowIndex;
            }

            if (@new is { } newSource)
            {
                rowByLine[newSource.Index] = rowIndex;
            }
        }
    }

    private static Dictionary<int, IReadOnlyList<DiffSearchRange>> BuildSearchRanges(
        IReadOnlyList<DiffSearchMatch> matches,
        DiffSearchColumn column,
        DiffSearchMatch? selectedMatch)
    {
        return matches
            .Where(match => match.Column == column)
            .GroupBy(match => match.LineIndex)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<DiffSearchRange>)group
                    .Select(match => new DiffSearchRange(
                        match.Start,
                        match.Length,
                        selectedMatch is { } selected && selected == match))
                    .ToArray());
    }

    private static IReadOnlyList<DiffSearchMatch> GetSearchMatches(
        FileDiff? diff,
        string query,
        bool split)
    {
        if (diff is null || query.Length == 0)
        {
            return [];
        }

        var matches = new List<DiffSearchMatch>();
        for (var lineIndex = 0; lineIndex < diff.Lines.Count; lineIndex++)
        {
            var line = diff.Lines[lineIndex];
            if (line.Kind is not (DiffLineKind.Added or DiffLineKind.Removed or DiffLineKind.Context))
            {
                continue;
            }

            if (!split)
            {
                AddSearchMatches(matches, line.Text, lineIndex, DiffSearchColumn.Unified, query);
                continue;
            }

            if (line.Kind is DiffLineKind.Removed or DiffLineKind.Context)
            {
                AddSearchMatches(matches, line.Text, lineIndex, DiffSearchColumn.Old, query);
            }

            if (line.Kind is DiffLineKind.Added or DiffLineKind.Context)
            {
                AddSearchMatches(matches, line.Text, lineIndex, DiffSearchColumn.New, query);
            }
        }

        return matches;
    }

    private static void AddSearchMatches(
        List<DiffSearchMatch> matches,
        string text,
        int lineIndex,
        DiffSearchColumn column,
        string query)
    {
        for (var start = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
             start >= 0;
             start = text.IndexOf(query, start + query.Length, StringComparison.OrdinalIgnoreCase))
        {
            matches.Add(new DiffSearchMatch(lineIndex, start, query.Length, column));
        }
    }

    private void DiffTextBlock_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock textBlock)
        {
            RenderDiffTextBlock(textBlock);
        }
    }

    private void DiffTextBlock_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (sender is TextBlock textBlock)
        {
            RenderDiffTextBlock(textBlock);
        }
    }

    private static void RenderDiffTextBlock(TextBlock textBlock)
    {
        var (text, ranges) = textBlock.DataContext switch
        {
            DiffRow row => (row.Text, row.SearchMatches),
            DiffSideBySideRow row when string.Equals(textBlock.Tag as string, "Old", StringComparison.Ordinal)
                => (row.OldText, row.OldSearchMatches),
            DiffSideBySideRow row when string.Equals(textBlock.Tag as string, "New", StringComparison.Ordinal)
                => (row.NewText, row.NewSearchMatches),
            _ => (string.Empty, (IReadOnlyList<DiffSearchRange>)[]),
        };

        textBlock.Text = string.Empty;
        textBlock.Inlines.Clear();
        if (text.Length == 0)
        {
            return;
        }

        var cursor = 0;
        foreach (var range in ranges.OrderBy(range => range.Start))
        {
            var start = Math.Clamp(range.Start, 0, text.Length);
            var end = Math.Clamp(range.Start + range.Length, start, text.Length);
            if (start > cursor)
            {
                textBlock.Inlines.Add(new Run { Text = text[cursor..start] });
            }

            if (end > start)
            {
                textBlock.Inlines.Add(new Run
                {
                    Text = text[start..end],
                    FontWeight = range.IsCurrent ? FontWeights.Bold : FontWeights.SemiBold,
                    TextDecorations = TextDecorations.Underline,
                });
            }

            cursor = Math.Max(cursor, end);
        }

        if (cursor < text.Length)
        {
            textBlock.Inlines.Add(new Run { Text = text[cursor..] });
        }
    }

    private void UpdateSearchStatus(bool history, int count, int selectedIndex)
    {
        var query = history ? historySearchQuery : changesSearchQuery;
        var text = query.Length == 0
            ? string.Empty
            : count == 0
                ? "No matches"
                : $"{selectedIndex + 1} of {count}";
        if (history)
        {
            HistoryDiffSearchStatusText.Text = text;
        }
        else
        {
            DiffSearchStatusText.Text = text;
        }
    }

    private void UpdateTextDiffListVisibility()
    {
        if (currentWorkspace == "changes")
        {
            if (!currentChangesDiffIsText)
            {
                DiffList.Visibility = Visibility.Collapsed;
                DiffSideBySideList.Visibility = Visibility.Collapsed;
                return;
            }

            var showPartial = selectedPartialDiff?.IsSupported == true
                && !TextDiffBlocksPartialSelection();
            PartialDiffList.Visibility = showPartial ? Visibility.Visible : Visibility.Collapsed;
            DiffList.Visibility = !showPartial && textDiffMode == "Unified"
                ? Visibility.Visible
                : Visibility.Collapsed;
            DiffSideBySideList.Visibility = !showPartial && textDiffMode == "Split"
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (currentWorkspace == "history")
        {
            HistoryDiffList.Visibility = currentHistoryDiffIsText && textDiffMode == "Unified"
                ? Visibility.Visible
                : Visibility.Collapsed;
            HistoryDiffSideBySideList.Visibility = currentHistoryDiffIsText && textDiffMode == "Split"
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private bool TextDiffBlocksPartialSelection() =>
        hideWhitespaceChanges
        || textDiffMode == "Split"
        || changesSearchQuery.Length > 0;

    private void UpdateTextDiffControls()
    {
        if (DiffModeComboBox is null)
        {
            return;
        }

        var canConfigure = repositoryRoot is not null
            && !mutationInProgress
            && !BusyRing.IsActive;
        DiffModeComboBox.IsEnabled = canConfigure;
        HistoryDiffModeComboBox.IsEnabled = canConfigure;
        HideWhitespaceChangesCheckBox.IsEnabled = canConfigure;
        HistoryHideWhitespaceChangesCheckBox.IsEnabled = canConfigure;
        var changesSearchEnabled = currentChangesDiffIsText && currentWorkspace == "changes";
        var historySearchEnabled = currentHistoryDiffIsText && currentWorkspace == "history";
        DiffSearchBox.IsEnabled = changesSearchEnabled;
        HistoryDiffSearchBox.IsEnabled = historySearchEnabled;
        DiffSearchPreviousButton.IsEnabled = changesSearchEnabled;
        DiffSearchNextButton.IsEnabled = changesSearchEnabled;
        HistoryDiffSearchPreviousButton.IsEnabled = historySearchEnabled;
        HistoryDiffSearchNextButton.IsEnabled = historySearchEnabled;
        UpdateTextDiffListVisibility();
    }

    private static bool IsTextDiff(FileDiff diff) =>
        !diff.IsBinary && diff.ImageComparison is null && diff.SubmoduleComparison is null;
}
