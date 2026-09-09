using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using WinGit.Core;

namespace WinGit.Native;

/// <summary>A display row for a partial diff hunk or one of its lines.</summary>
internal sealed class PartialDiffRow : INotifyPropertyChanged
{
    private readonly PartialDiffHunk? hunk;
    private readonly PartialDiffLine? line;
    private bool? isChecked;

    private PartialDiffRow(PartialDiffHunk hunk)
    {
        this.hunk = hunk;
        HunkId = hunk.Id;
        IsHunk = true;
        isChecked = false;
    }

    private PartialDiffRow(PartialDiffLine line)
    {
        this.line = line;
        HunkId = line.HunkId;
        LineIndex = line.LineIndex;
        IsHunk = false;
        isChecked = line.IsSelectable ? false : null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static IEnumerable<PartialDiffRow> CreateRows(PartialFileDiff diff)
    {
        foreach (var hunk in diff.Hunks)
        {
            yield return new PartialDiffRow(hunk);
            foreach (var line in hunk.Lines)
            {
                yield return new PartialDiffRow(line);
            }
        }
    }

    public bool IsHunk { get; }

    public bool IsLine => !IsHunk;

    public int HunkId { get; }

    public int LineIndex { get; }

    public bool CanSelect => IsHunk || line?.IsSelectable == true;

    public bool IsSelectableLine => !IsHunk && line?.IsSelectable == true;

    public string Header => hunk?.Header ?? string.Empty;

    public string OldLineNumber => line?.OldLineNumber?.ToString() ?? string.Empty;

    public string NewLineNumber => line?.NewLineNumber?.ToString() ?? string.Empty;

    public string Marker => line?.Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "−",
        DiffLineKind.Context => " ",
        DiffLineKind.NoNewline => "\\",
        _ => string.Empty,
    };

    public string Text => line?.Text ?? string.Empty;

    public string KindLabel => IsHunk
        ? $"Hunk {HunkId + 1}"
        : line?.Kind switch
        {
            DiffLineKind.Added => "Added line",
            DiffLineKind.Removed => "Removed line",
            DiffLineKind.Context => "Context line",
            DiffLineKind.NoNewline => "No newline marker",
            _ => "Diff line",
        };

    public string AutomationName => IsHunk
        ? $"Hunk {HunkId + 1}: {Header}"
        : $"{KindLabel} {LineNumberForAccessibility}: {Text}";

    public string SelectionAutomationName => IsHunk
        ? $"Select entire hunk {HunkId + 1}"
        : CanSelect
            ? $"Select {KindLabel.ToLowerInvariant()} {LineNumberForAccessibility}"
            : $"{KindLabel} {LineNumberForAccessibility}";

    public string LineNumberForAccessibility => line?.NewLineNumber?.ToString()
        ?? line?.OldLineNumber?.ToString()
        ?? "without a line number";

    public Visibility HunkVisibility => IsHunk ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LineVisibility => IsHunk ? Visibility.Collapsed : Visibility.Visible;

    public Visibility AddedVisibility => line?.Kind == DiffLineKind.Added
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RemovedVisibility => line?.Kind == DiffLineKind.Removed
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool? IsChecked
    {
        get => isChecked;
        set
        {
            if (isChecked == value)
            {
                return;
            }

            isChecked = value;
            OnPropertyChanged();
        }
    }

    public PartialDiffSelection Selection => IsHunk
        ? PartialDiffSelection.ForHunk(HunkId)
        : PartialDiffSelection.ForLine(HunkId, LineIndex);

    public PartialDiffLine? Line => line;

    public void SetCheckedFromSelection(bool? value) => IsChecked = value;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
