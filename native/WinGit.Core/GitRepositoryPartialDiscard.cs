namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    /// <summary>Discards only the selected unstaged textual changes from the work tree.</summary>
    public async Task DiscardSelectedChangesAsync(
        string root,
        PartialFileDiff diff,
        IReadOnlyCollection<PartialDiffSelection> selection,
        CancellationToken cancellationToken)
    {
        await ApplySelectedChangesAsync(
            root,
            diff,
            selection,
            staged: false,
            reverse: true,
            applyToIndex: false,
            cancellationToken).ConfigureAwait(false);
    }
}
