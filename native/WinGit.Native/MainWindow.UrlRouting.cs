using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private async Task RouteStartupRepositoryUrlAsync(RepositoryUrlAction action)
    {
        try
        {
            var existingRoot = await FindRecentRepositoryAsync(action.RemoteUrl);
            string? root;
            if (existingRoot is null)
            {
                root = await ShowCloneRepositoryDialogAsync(
                    action.RemoteUrl,
                    action.PullRequestNumber is null ? action.Branch : null);
                if (root is null)
                {
                    return;
                }
            }
            else
            {
                await OpenRepositoryAsync(existingRoot);
                root = existingRoot;
            }

            if (!IsCurrentStartupRepository(root))
            {
                return;
            }

            var matchingRemote = await FindMatchingRemoteAsync(root, action.RemoteUrl);
            if (matchingRemote is null)
            {
                return;
            }

            var completed = action.PullRequestNumber is int pullRequestNumber
                ? await RoutePullRequestAsync(root, matchingRemote.Name, pullRequestNumber)
                : action.Branch is string branchName
                    ? await RouteBranchAsync(root, matchingRemote.Name, branchName)
                    : true;
            if (!completed || !IsCurrentStartupRepository(root))
            {
                return;
            }

            if (action.FilePath is not null)
            {
                try
                {
                    NativeExternalLaunchers.RevealInFileManager(root, action.FilePath);
                }
                catch (Exception)
                {
                    ShowError(
                        "Unable to reveal requested file",
                        new InvalidOperationException("The requested file could not be revealed."));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ShowError(
                "Unable to open application link",
                new InvalidOperationException("The application link could not be opened."));
        }
    }

    private async Task<string?> FindRecentRepositoryAsync(string requestedUrl)
    {
        foreach (var candidate in settings.RecentRepositories)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            IReadOnlyList<RemoteSummary> remotes;
            try
            {
                remotes = await repositoryService.GetRemotesAsync(
                    candidate,
                    CancellationToken.None);
            }
            catch (Exception)
            {
                continue;
            }

            if (remotes.Any(remote => RepositoryUrlMatcher.Matches(requestedUrl, remote.Url)))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<RemoteSummary?> FindMatchingRemoteAsync(
        string root,
        string requestedUrl)
    {
        IReadOnlyList<RemoteSummary> remotes;
        try
        {
            remotes = await repositoryService.GetRemotesAsync(
                root,
                CancellationToken.None);
        }
        catch (Exception)
        {
            ShowError(
                "Unable to read repository link",
                new InvalidOperationException("The repository remotes could not be read."));
            return null;
        }

        if (!IsCurrentStartupRepository(root))
        {
            return null;
        }

        var matchingRemote = remotes
            .Where(remote => RepositoryUrlMatcher.Matches(requestedUrl, remote.Url))
            .OrderByDescending(remote => string.Equals(remote.Name, "origin", StringComparison.Ordinal))
            .ThenBy(remote => remote.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (matchingRemote is null)
        {
            ShowError(
                "Repository link mismatch",
                new InvalidOperationException("The opened repository does not contain the requested remote."));
        }

        return matchingRemote;
    }

    private async Task<bool> RouteBranchAsync(
        string root,
        string remoteName,
        string branchName)
    {
        var fetched = await RunRepositoryWriteAsync(
            $"Fetching {remoteName}…",
            $"Fetched {remoteName}",
            "Branch fetch cancelled; refreshing repository…",
            "Unable to fetch branch link",
            (mutationRoot, token) => repositoryService.FetchAsync(mutationRoot, remoteName, token),
            refreshBranches: true,
            expectedRoot: root);
        if (!fetched || !IsCurrentStartupRepository(root))
        {
            return false;
        }

        var branch = branchRows.FirstOrDefault(row =>
            string.Equals(row.Name, branchName, StringComparison.Ordinal));
        if (branch is null)
        {
            BranchCreationPlan plan;
            try
            {
                plan = await repositoryService.CaptureBranchCreationPlanAsync(
                    root,
                    branchName,
                    $"refs/remotes/{remoteName}/{branchName}",
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception)
            {
                ShowError(
                    "Unable to prepare branch link",
                    new InvalidOperationException("The requested branch could not be prepared."));
                return false;
            }

            if (!IsCurrentStartupRepository(root))
            {
                return false;
            }

            var created = await RunRepositoryWriteAsync(
                $"Creating {branchName}…",
                $"Created {branchName}",
                "Branch creation cancelled; refreshing repository…",
                "Unable to create branch link",
                (mutationRoot, token) => repositoryService.CreateBranchAsync(mutationRoot, plan, token),
                refreshBranches: true,
                refreshWorktrees: true,
                expectedRoot: root,
                selectBranchName: branchName);
            if (!created || !IsCurrentStartupRepository(root))
            {
                return false;
            }

            branch = branchRows.FirstOrDefault(row =>
                string.Equals(row.Name, branchName, StringComparison.Ordinal));
        }

        if (branch is null || !IsCurrentStartupRepository(root))
        {
            ShowError(
                "Branch link unavailable",
                new InvalidOperationException("The requested local branch could not be selected."));
            return false;
        }

        SelectBranchRow(branch.Name);
        if (!IsCurrentStartupRepository(root))
        {
            return false;
        }

        if (selectedBranch is null
            || !string.Equals(selectedBranch.Name, branch.Name, StringComparison.Ordinal))
        {
            ShowError(
                "Branch link unavailable",
                new InvalidOperationException("The requested local branch could not be selected."));
            return false;
        }

        if (branch.Branch.IsCurrent)
        {
            return true;
        }

        return await SwitchBranchAsync(branch);
    }

    private async Task<bool> RoutePullRequestAsync(
        string root,
        string remoteName,
        int pullRequestNumber)
    {
        string? headId = null;
        var fetched = await RunRepositoryWriteAsync(
            $"Fetching pull request {pullRequestNumber}…",
            $"Fetched pull request {pullRequestNumber}",
            "Pull request fetch cancelled; refreshing repository…",
            "Unable to fetch pull request link",
            async (mutationRoot, token) =>
            {
                headId = await repositoryService.FetchPullRequestHeadAsync(
                    mutationRoot,
                    remoteName,
                    pullRequestNumber,
                    token);
            },
            refreshBranches: true,
            expectedRoot: root);
        if (!fetched || string.IsNullOrWhiteSpace(headId) || !IsCurrentStartupRepository(root))
        {
            return false;
        }

        var branchName = $"pr/{pullRequestNumber}";
        var branch = branchRows.FirstOrDefault(row =>
            string.Equals(row.Name, branchName, StringComparison.Ordinal));
        if (branch is not null)
        {
            string localHead;
            try
            {
                localHead = await repositoryService.GetLocalBranchTipAsync(
                    root,
                    branchName,
                    CancellationToken.None);
            }
            catch (Exception)
            {
                ShowError(
                    "Unable to inspect pull request branch",
                    new InvalidOperationException("The local pull request branch could not be inspected."));
                return false;
            }

            if (!IsCurrentStartupRepository(root))
            {
                return false;
            }

            if (!string.Equals(localHead, headId, StringComparison.OrdinalIgnoreCase))
            {
                ShowError(
                    "Pull request branch collision",
                    new InvalidOperationException("The local pull request branch already points to a different commit."));
                return false;
            }
        }
        else
        {
            BranchCreationPlan plan;
            try
            {
                plan = await repositoryService.CaptureBranchCreationPlanAsync(
                    root,
                    branchName,
                    headId,
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception)
            {
                ShowError(
                    "Unable to prepare pull request branch",
                    new InvalidOperationException("The local pull request branch could not be prepared."));
                return false;
            }

            if (!IsCurrentStartupRepository(root))
            {
                return false;
            }

            var created = await RunRepositoryWriteAsync(
                $"Creating {branchName}…",
                $"Created {branchName}",
                "Pull request branch creation cancelled; refreshing repository…",
                "Unable to create pull request branch",
                (mutationRoot, token) => repositoryService.CreateBranchAsync(mutationRoot, plan, token),
                refreshBranches: true,
                refreshWorktrees: true,
                expectedRoot: root,
                selectBranchName: branchName);
            if (!created || !IsCurrentStartupRepository(root))
            {
                return false;
            }

            branch = branchRows.FirstOrDefault(row =>
                string.Equals(row.Name, branchName, StringComparison.Ordinal));
        }

        if (branch is null || !IsCurrentStartupRepository(root))
        {
            ShowError(
                "Pull request branch unavailable",
                new InvalidOperationException("The local pull request branch could not be selected."));
            return false;
        }

        SelectBranchRow(branch.Name);
        if (!IsCurrentStartupRepository(root)
            || selectedBranch is null
            || !string.Equals(selectedBranch.Name, branch.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (branch.Branch.IsCurrent)
        {
            return true;
        }

        return await SwitchBranchAsync(branch);
    }

    private bool IsCurrentStartupRepository(string expectedRoot) =>
        repositoryRoot is not null && IsSamePath(repositoryRoot, expectedRoot);
}
