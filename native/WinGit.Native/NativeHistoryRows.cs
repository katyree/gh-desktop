using WinGit.Core;

namespace WinGit.Native;

internal sealed class HistoryComparisonBranchRow
{
    public HistoryComparisonBranchRow(BranchSummary branch)
    {
        Branch = branch;
    }

    public BranchSummary Branch { get; }

    public string Name => Branch.Name;

    public string Reference => string.IsNullOrWhiteSpace(Branch.FullRef)
        ? Branch.Name
        : Branch.FullRef;

    public string Details => Branch.IsCurrent
        ? "Current branch"
        : string.IsNullOrWhiteSpace(Branch.Upstream)
            ? "Local branch"
            : $"Tracks {Branch.Upstream}";

    public string SearchText => $"{Name} {Details}";
}

internal sealed class ReflogRow
{
    public ReflogRow(ReflogEntry entry)
    {
        Entry = entry;
    }

    public ReflogEntry Entry { get; }

    public string CommitId => Entry.CommitId;

    public string Selector => Entry.Selector;

    public string Subject => string.IsNullOrWhiteSpace(Entry.Subject)
        ? "(no reflog message)"
        : Entry.Subject;

    public string Details => $"{Entry.Date.ToLocalTime():MMM d, yyyy  h:mm tt}  ·  {Entry.Selector}";

    public string AutomationName => $"{Entry.CommitId} {Entry.Selector} {Subject}";
}
