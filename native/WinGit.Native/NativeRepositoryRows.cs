using WinGit.Core;

namespace WinGit.Native;

internal sealed class BranchRow
{
    public BranchRow(BranchSummary branch)
    {
        Branch = branch;
    }

    public BranchSummary Branch { get; }

    public string Name => Branch.Name;

    public string Marker => Branch.IsCurrent ? "●" : "○";

    public string StatusLabel => Branch.IsCurrent
        ? "Current branch"
        : string.IsNullOrWhiteSpace(Branch.WorktreePath)
            ? "Local branch"
            : "Checked out in a worktree";

    public string TrackingLabel
    {
        get
        {
            var tracking = Branch.Upstream;
            if (!string.IsNullOrWhiteSpace(Branch.Upstream)
                && !string.IsNullOrWhiteSpace(Branch.UpstreamRemote)
                && !Branch.Upstream.StartsWith(Branch.UpstreamRemote + "/", StringComparison.Ordinal))
            {
                tracking = $"{Branch.UpstreamRemote}/{Branch.Upstream}";
            }
            var counts = new List<string>(2);
            if (Branch.Ahead > 0)
            {
                counts.Add($"↑{Branch.Ahead}");
            }

            if (Branch.Behind > 0)
            {
                counts.Add($"↓{Branch.Behind}");
            }

            var progress = string.Join("  ", counts);
            return string.IsNullOrWhiteSpace(tracking)
                ? progress
                : string.IsNullOrWhiteSpace(progress) ? tracking : $"{tracking}  ·  {progress}";
        }
    }

    public string WorktreeLabel => string.IsNullOrWhiteSpace(Branch.WorktreePath)
        ? string.Empty
        : Branch.WorktreePath;

    public string SearchText => $"{Name} {StatusLabel} {TrackingLabel} {WorktreeLabel}";
}

internal sealed class WorktreeRow
{
    public WorktreeRow(WorktreeSummary worktree, bool isCurrent)
    {
        Worktree = worktree;
        IsCurrent = isCurrent;
    }

    public WorktreeSummary Worktree { get; }

    public bool IsCurrent { get; }

    public string Path => Worktree.Path;

    public string Branch => string.IsNullOrWhiteSpace(Worktree.Branch)
        ? "Detached HEAD"
        : Worktree.Branch;

    public string Head => string.IsNullOrWhiteSpace(Worktree.HeadId)
        ? "No commit"
        : Worktree.HeadId.Length > 7 ? Worktree.HeadId[..7] : Worktree.HeadId;

    public string StatusLabel => IsCurrent
        ? "Current worktree"
        : Worktree.IsLocked
            ? "Locked"
            : Worktree.IsPrunable ? "Prunable" : "Secondary worktree";

    public string SearchText => $"{Path} {Branch} {Head} {StatusLabel}";
}

internal sealed class StashRow
{
    public StashRow(StashSummary stash)
    {
        Stash = stash;
    }

    public StashSummary Stash { get; }

    public string Reference => Stash.Reference;

    public string CommitId => Stash.CommitId;

    public string Summary => Stash.Summary;

    public string Date => Stash.Date.ToLocalTime().ToString("MMM d, yyyy  h:mm tt");

    public string SearchText => $"{Reference} {Summary} {CommitId} {Date}";
}

internal sealed class RemoteRow
{
    public RemoteRow(RemoteSummary remote)
    {
        Remote = remote;
    }

    public RemoteSummary Remote { get; }

    public string Name => Remote.Name;

    /// <summary>Core supplies a URL with credentials removed.</summary>
    public string Url => string.IsNullOrWhiteSpace(Remote.Url) ? "No URL reported" : Remote.Url;

    public string SearchText => $"{Name} {Url}";
}
