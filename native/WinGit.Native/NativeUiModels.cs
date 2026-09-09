using Microsoft.UI.Xaml;
using WinGit.Core;

namespace WinGit.Native;

/// <summary>
/// A presentation row for the native changes list. The core model stays
/// attached to the row so a selection can be passed back without reparsing a
/// path in the window.
/// </summary>
internal sealed class ChangeRow
{
    public ChangeRow(FileChange change, bool isStaged = false)
    {
        Change = change;
        IsStaged = isStaged;
    }

    public FileChange Change { get; }

    public bool IsStaged { get; }

    public bool HasStagedChanges => !string.IsNullOrWhiteSpace(Change.IndexStatus)
        && !string.Equals(Change.IndexStatus, "?", StringComparison.Ordinal)
        && !string.Equals(Change.IndexStatus, "!", StringComparison.Ordinal);

    public bool HasUnstagedChanges => !string.IsNullOrWhiteSpace(Change.WorkTreeStatus)
        && !string.Equals(Change.WorkTreeStatus, "!", StringComparison.Ordinal);

    public string Path => Change.Path;

    public string KindLabel => Change.Kind switch
    {
        ChangeKind.Added => "Added",
        ChangeKind.Modified => "Modified",
        ChangeKind.Deleted => "Deleted",
        ChangeKind.Renamed => "Renamed",
        ChangeKind.Copied => "Copied",
        ChangeKind.TypeChanged => "Type changed",
        ChangeKind.Untracked => "Untracked",
        ChangeKind.Conflicted => "Conflict",
        _ => "Changed",
    };

    public string KindGlyph => Change.Kind switch
    {
        ChangeKind.Added => "+",
        ChangeKind.Deleted => "−",
        ChangeKind.Renamed => "↪",
        ChangeKind.Conflicted => "!",
        ChangeKind.Untracked => "•",
        _ => "~",
    };

    public string StatusLabel
    {
        get
        {
            var index = string.IsNullOrWhiteSpace(Change.IndexStatus) ? " " : Change.IndexStatus;
            var workTree = string.IsNullOrWhiteSpace(Change.WorkTreeStatus) ? " " : Change.WorkTreeStatus;
            return $"{KindLabel}  {index}{workTree}";
        }
    }

    public string SearchText => $"{Path} {KindLabel} {StatusLabel}";

}

internal enum DiffSearchColumn
{
    Unified,
    Old,
    New,
}

internal readonly record struct DiffSearchMatch(
    int LineIndex,
    int Start,
    int Length,
    DiffSearchColumn Column);

internal readonly record struct DiffSearchRange(
    int Start,
    int Length,
    bool IsCurrent);

internal sealed class DiffRow
{
    public DiffRow(
        DiffLine line,
        IReadOnlyList<DiffSearchRange>? searchMatches = null)
    {
        Line = line;
        SearchMatches = searchMatches ?? [];
    }

    public DiffLine Line { get; }

    public string OldLineNumber => Line.OldLineNumber?.ToString() ?? string.Empty;

    public string NewLineNumber => Line.NewLineNumber?.ToString() ?? string.Empty;

    public string Marker => Line.Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "−",
        DiffLineKind.HunkHeader => "@@",
        DiffLineKind.FileHeader => "▸",
        DiffLineKind.NoNewline => "\\",
        DiffLineKind.Binary => "•",
        _ => " ",
    };

    public string KindLabel => Line.Kind switch
    {
        DiffLineKind.Added => "Added line",
        DiffLineKind.Removed => "Removed line",
        DiffLineKind.HunkHeader => "Hunk header",
        DiffLineKind.FileHeader => "File header",
        DiffLineKind.NoNewline => "No trailing newline",
        DiffLineKind.Binary => "Binary content",
        _ => "Context line",
    };

    public string Text => Line.Text;

    public IReadOnlyList<DiffSearchRange> SearchMatches { get; }

    public bool IsSearchMatch => SearchMatches.Count > 0;

    public bool IsCurrentSearchMatch => SearchMatches.Any(match => match.IsCurrent);

    public Visibility SearchMatchVisibility => IsSearchMatch && !IsCurrentSearchMatch
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CurrentSearchMatchVisibility => IsCurrentSearchMatch
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AddedVisibility => Line.Kind == DiffLineKind.Added
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RemovedVisibility => Line.Kind == DiffLineKind.Removed
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SectionVisibility => Line.Kind is DiffLineKind.HunkHeader or DiffLineKind.FileHeader
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility NeutralMarkerVisibility => Line.Kind is DiffLineKind.Added or DiffLineKind.Removed
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility AddedMarkerVisibility => Line.Kind == DiffLineKind.Added
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RemovedMarkerVisibility => Line.Kind == DiffLineKind.Removed
        ? Visibility.Visible
        : Visibility.Collapsed;

}

internal sealed class DiffSideBySideRow
{
    public DiffSideBySideRow(
        DiffLine line,
        IReadOnlyList<DiffSearchRange>? searchMatches = null)
        : this(
            line.Kind == DiffLineKind.Removed ? line : null,
            line.Kind == DiffLineKind.Added ? line : null,
            line.Kind is DiffLineKind.Context ? line : null,
            line,
            line.Kind is DiffLineKind.Removed or DiffLineKind.Context ? searchMatches : null,
            line.Kind is DiffLineKind.Added or DiffLineKind.Context ? searchMatches : null)
    {
    }

    public DiffSideBySideRow(
        DiffLine? oldLine,
        DiffLine? newLine,
        DiffLine? sharedLine,
        DiffLine headerLine,
        IReadOnlyList<DiffSearchRange>? oldSearchMatches = null,
        IReadOnlyList<DiffSearchRange>? newSearchMatches = null)
    {
        OldLine = oldLine;
        NewLine = newLine;
        SharedLine = sharedLine;
        HeaderLine = headerLine;
        OldSearchMatches = oldSearchMatches ?? [];
        NewSearchMatches = newSearchMatches ?? [];
    }

    public DiffLine? OldLine { get; }

    public DiffLine? NewLine { get; }

    public DiffLine? SharedLine { get; }

    public DiffLine HeaderLine { get; }

    public DiffLine Line => SharedLine ?? OldLine ?? NewLine ?? HeaderLine;

    public string OldLineNumber => (SharedLine ?? OldLine)?.OldLineNumber?.ToString() ?? string.Empty;

    public string NewLineNumber => (SharedLine ?? NewLine)?.NewLineNumber?.ToString() ?? string.Empty;

    public string OldMarker => OldLine is not null && OldLine.Kind == DiffLineKind.Removed ? "−" : string.Empty;

    public string NewMarker => NewLine is not null && NewLine.Kind == DiffLineKind.Added ? "+" : string.Empty;

    public string OldText => (SharedLine ?? OldLine)?.Text ?? string.Empty;

    public string NewText => (SharedLine ?? NewLine)?.Text ?? string.Empty;

    public string Header => HeaderLine.Text;

    public IReadOnlyList<DiffSearchRange> OldSearchMatches { get; }

    public IReadOnlyList<DiffSearchRange> NewSearchMatches { get; }

    public bool IsSearchMatch => OldSearchMatches.Count > 0 || NewSearchMatches.Count > 0;

    public bool IsCurrentSearchMatch => OldSearchMatches.Any(match => match.IsCurrent)
        || NewSearchMatches.Any(match => match.IsCurrent);

    public Visibility SearchMatchVisibility => IsSearchMatch && !IsCurrentSearchMatch
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CurrentSearchMatchVisibility => IsCurrentSearchMatch
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility LineVisibility => SharedLine is not null || OldLine is not null || NewLine is not null
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility HeaderVisibility => LineVisibility == Visibility.Visible
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility OldRemovedVisibility => OldLine?.Kind == DiffLineKind.Removed
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility NewAddedVisibility => NewLine?.Kind == DiffLineKind.Added
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string KindLabel => Line.Kind switch
    {
        DiffLineKind.Added => "Added line",
        DiffLineKind.Removed => "Removed line",
        DiffLineKind.HunkHeader => "Hunk header",
        DiffLineKind.FileHeader => "File header",
        DiffLineKind.NoNewline => "No trailing newline",
        DiffLineKind.Binary => "Binary content",
        _ => "Context line",
    };
}

internal sealed class CommitRow
{
    public CommitRow(CommitSummary commit)
    {
        Commit = commit;
    }

    public CommitSummary Commit { get; }

    public string ShortId => Commit.ShortId.Length > 7 ? Commit.ShortId[..7] : Commit.ShortId;

    public string Summary => Commit.Summary;

    public string Author => Commit.Author;

    public string Date => Commit.Date.ToLocalTime().ToString("MMM d, yyyy  h:mm tt");
}

internal sealed class CommitFileRow
{
    public CommitFileRow(FileChange file)
    {
        File = file;
    }

    public FileChange File { get; }

    public string Path => File.Path;

    public string KindLabel => File.Kind.ToString();

    public string KindGlyph => File.Kind switch
    {
        ChangeKind.Added => "+",
        ChangeKind.Deleted => "−",
        ChangeKind.Renamed => "↪",
        _ => "~",
    };
}
