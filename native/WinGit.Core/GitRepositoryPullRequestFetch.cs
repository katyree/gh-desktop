namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    public async Task<string> FetchPullRequestHeadAsync(
        string root,
        string remoteName,
        int pullRequestNumber,
        CancellationToken cancellationToken)
    {
        if (pullRequestNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pullRequestNumber),
                pullRequestNumber,
                "The pull request number must be positive.");
        }

        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        ValidateRemoteNameSyntax(remoteName);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            path => FetchPullRequestHeadInMutationAsync(
                path,
                remoteName,
                pullRequestNumber,
                cancellationToken)).ConfigureAwait(false);
    }

    private async Task<string> FetchPullRequestHeadInMutationAsync(
        string repositoryRoot,
        string remoteName,
        int pullRequestNumber,
        CancellationToken cancellationToken)
    {
        var normalizedRemote = await EnsureRemoteExistsAsync(
            repositoryRoot,
            remoteName,
            cancellationToken).ConfigureAwait(false);
        var remoteUrl = await ReadRemoteUrlAsync(
            repositoryRoot,
            normalizedRemote,
            cancellationToken).ConfigureAwait(false);
        var temporaryRef = $"refs/wingit/pull-request-fetch/{Guid.NewGuid():N}";
        if (await ReadRefIdAsync(repositoryRoot, temporaryRef, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException("Git generated a temporary pull request ref that already exists.");
        }

        Exception? operationFailure = null;
        try
        {
            try
            {
                await RunRemoteCommandAsync(
                    repositoryRoot,
                    [
                        "fetch",
                        "--no-prune",
                        "--no-tags",
                        "--no-write-fetch-head",
                        normalizedRemote,
                        $"refs/pull/{pullRequestNumber}/head:{temporaryRef}",
                    ],
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GitCommandException exception)
            {
                throw MapTransportFailure(
                    "fetching pull request head from",
                    normalizedRemote,
                    remoteUrl,
                    exception);
            }

            return await ResolveCommitIdAsync(
                repositoryRoot,
                temporaryRef,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                await RunRemoteCommandAsync(
                    repositoryRoot,
                    ["update-ref", "-d", temporaryRef],
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch when (operationFailure is not null)
            {
            }
        }
    }
}
