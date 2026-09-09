namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    /// <summary>Reads the immutable tip of a validated local branch.</summary>
    public async Task<string> GetLocalBranchTipAsync(
        string root,
        string branchName,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var normalizedBranchName = await ValidateBranchNameAsync(
            repositoryRoot,
            branchName,
            cancellationToken).ConfigureAwait(false);
        var branchTip = await ReadRefIdAsync(
            repositoryRoot,
            $"refs/heads/{normalizedBranchName}",
            cancellationToken).ConfigureAwait(false);
        if (branchTip is null)
        {
            throw new InvalidOperationException("The selected local branch no longer exists; refresh branches and try again.");
        }

        return branchTip;
    }
}
