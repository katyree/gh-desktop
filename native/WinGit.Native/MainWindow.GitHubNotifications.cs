using WinGit.Core;
using WinGit.Core.GitHub;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private const int MaximumNotificationPullRequests = 256;
    private const int MaximumDeliveredNotifications = 512;
    private const int MaximumDeliveredCheckRuns = 1_024;

    private readonly object githubNotificationCacheGate = new();
    private readonly Dictionary<string, GitHubPullRequest> githubNotificationPullRequests =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> githubNotificationPullRequestOrder = [];
    private readonly HashSet<string> deliveredGitHubNotificationKeys =
        new(StringComparer.Ordinal);
    private readonly Queue<string> deliveredGitHubNotificationOrder = [];
    private readonly HashSet<long> deliveredGitHubCheckRunIds = [];
    private readonly Queue<long> deliveredGitHubCheckRunOrder = [];
    private readonly SemaphoreSlim githubNotificationLifecycleGate = new(1, 1);
    private CancellationTokenSource? githubNotificationMonitorCancellation;
    private CancellationTokenSource? githubNotificationStartCancellation;
    private Task? githubNotificationMonitorTask;
    private GitHubAliveNotificationClient? githubAliveNotificationClient;
    private GitHubNotificationMonitorDescriptor? githubNotificationMonitorDescriptor;
    private GitHubStoredAccount? githubNotificationAccount;
    private long githubNotificationGeneration;
    private long githubNotificationRequestGeneration;

    internal Task RestartGitHubNotificationMonitorAsync() =>
        RestartGitHubNotificationMonitorCoreAsync();

    internal Task StopGitHubNotificationMonitorAsync() =>
        StopGitHubNotificationMonitorCoreAsync();

    internal void CacheNotificationPullRequests(
        GitHubRemoteRepositoryIdentity repository,
        IReadOnlyList<GitHubPullRequest> pullRequests)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(pullRequests);

        lock (githubNotificationCacheGate)
        {
            foreach (var pullRequest in pullRequests)
            {
                if (pullRequest.Number <= 0 ||
                    !AreSameGitHubOrigin(
                        pullRequest.ApiOrigin,
                        repository.ApiOrigin))
                {
                    continue;
                }

                var key = GetNotificationPullRequestKey(
                    repository,
                    pullRequest.Number);
                if (!githubNotificationPullRequests.ContainsKey(key))
                {
                    githubNotificationPullRequestOrder.Enqueue(key);
                }

                githubNotificationPullRequests[key] = pullRequest;
            }

            while (githubNotificationPullRequestOrder.Count >
                   MaximumNotificationPullRequests)
            {
                var oldest = githubNotificationPullRequestOrder.Dequeue();
                githubNotificationPullRequests.Remove(oldest);
            }
        }
    }

    internal void CacheNotificationPullRequest(
        GitHubRemoteRepositoryIdentity repository,
        GitHubPullRequest pullRequest)
    {
        CacheNotificationPullRequests(repository, [pullRequest]);
    }

    internal void ClearNotificationPullRequestCache()
    {
        lock (githubNotificationCacheGate)
        {
            githubNotificationPullRequests.Clear();
            githubNotificationPullRequestOrder.Clear();
        }
    }

    private void NotificationsEnabledChangedForMonitor(
        object? sender,
        EventArgs args)
    {
        _ = RestartGitHubNotificationMonitorAsync();
    }

    private async Task RestartGitHubNotificationMonitorCoreAsync()
    {
        var requestGeneration = ++githubNotificationRequestGeneration;
        InvalidateGitHubNotificationRun();
        var lifecycleGateHeld = false;
        try
        {
            await githubNotificationLifecycleGate.WaitAsync();
            lifecycleGateHeld = true;
            if (requestGeneration != githubNotificationRequestGeneration)
            {
                return;
            }

            var previousDescriptor = githubNotificationMonitorDescriptor;
            await StopGitHubNotificationMonitorLockedAsync();
            if (requestGeneration != githubNotificationRequestGeneration)
            {
                return;
            }

            if (ShouldLoadGitHubNotificationAccount())
            {
                var startCancellation = new CancellationTokenSource();
                githubNotificationStartCancellation = startCancellation;
                try
                {
                    await EnsureGitHubNotificationAccountAsync(
                        startCancellation.Token);
                }
                finally
                {
                    if (ReferenceEquals(
                            githubNotificationStartCancellation,
                            startCancellation))
                    {
                        githubNotificationStartCancellation = null;
                    }

                    startCancellation.Dispose();
                }
            }

            if (requestGeneration != githubNotificationRequestGeneration)
            {
                return;
            }

            var descriptor = CaptureGitHubNotificationMonitorDescriptor();
            var contextChanged = !AreSameGitHubNotificationDescriptors(
                previousDescriptor,
                descriptor);
            if (contextChanged)
            {
                ClearDeliveredGitHubNotificationCache();
            }

            if (descriptor is null)
            {
                SetNotificationMonitorStatus(GetGitHubNotificationWaitingStatus());
                return;
            }

            var cancellation = new CancellationTokenSource();
            var generation = ++githubNotificationGeneration;
            var aliveClient = new GitHubAliveNotificationClient();
            githubNotificationMonitorCancellation = cancellation;
            githubAliveNotificationClient = aliveClient;
            githubNotificationMonitorDescriptor = descriptor;
            githubNotificationMonitorTask = RunGitHubNotificationMonitorAsync(
                descriptor,
                generation,
                cancellation,
                aliveClient);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            SetNotificationMonitorStatus("GitHub monitoring is unavailable.");
        }
        finally
        {
            if (lifecycleGateHeld)
            {
                githubNotificationLifecycleGate.Release();
            }
        }
    }

    private async Task StopGitHubNotificationMonitorCoreAsync()
    {
        ++githubNotificationRequestGeneration;
        InvalidateGitHubNotificationRun();
        var lifecycleGateHeld = false;
        try
        {
            await githubNotificationLifecycleGate.WaitAsync();
            lifecycleGateHeld = true;
            await StopGitHubNotificationMonitorLockedAsync();
        }
        finally
        {
            if (lifecycleGateHeld)
            {
                githubNotificationLifecycleGate.Release();
            }
        }
    }

    private async Task StopGitHubNotificationMonitorLockedAsync()
    {
        ++githubNotificationGeneration;
        var cancellation = githubNotificationMonitorCancellation;
        var monitorTask = githubNotificationMonitorTask;
        var aliveClient = githubAliveNotificationClient;
        githubNotificationMonitorCancellation = null;
        githubNotificationMonitorTask = null;
        githubAliveNotificationClient = null;
        githubNotificationMonitorDescriptor = null;
        cancellation?.Cancel();

        if (monitorTask is not null)
        {
            try
            {
                await monitorTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
            }
        }

        aliveClient?.Dispose();
        cancellation?.Dispose();
    }

    private async Task RunGitHubNotificationMonitorAsync(
        GitHubNotificationMonitorDescriptor descriptor,
        long generation,
        CancellationTokenSource cancellation,
        GitHubAliveNotificationClient aliveClient)
    {
        try
        {
            var resolved = await ResolveGitHubNotificationRepositoryAsync(
                    descriptor,
                    cancellation.Token);
            if (resolved.Repository is null || resolved.Identity is null)
            {
                if (IsCurrentGitHubNotificationRun(
                        descriptor,
                        generation,
                        cancellation.Token))
                {
                    SetNotificationMonitorStatus(resolved.Status);
                }

                return;
            }

            if (!IsCurrentGitHubNotificationRun(
                    descriptor,
                    generation,
                    cancellation.Token))
            {
                return;
            }

            using var contentClient = new GitHubNotificationContentClient(
                GitHubNotificationContentClientOptions.ForGitHubCom());
            var accountEmails = await contentClient.ReadAccountEmailsAsync(
                    descriptor.Account.Session,
                    cancellation.Token);
            if (!IsCurrentGitHubNotificationRun(
                    descriptor,
                    generation,
                    cancellation.Token))
            {
                return;
            }

            try
            {
                using var pullRequestClient = new GitHubPullRequestClient(
                    CreateGitHubPullRequestOptions(resolved.Identity.ApiOrigin));
                var pullRequests = await pullRequestClient.ListAsync(
                    descriptor.Account.Session,
                    resolved.Identity,
                    cancellationToken: cancellation.Token);
                if (pullRequests.IsComplete)
                {
                    if (!IsCurrentGitHubNotificationRun(
                            descriptor,
                            generation,
                            cancellation.Token))
                    {
                        return;
                    }

                    CacheNotificationPullRequests(
                        resolved.Identity,
                        pullRequests.PullRequests);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
            }

            if (!IsCurrentGitHubNotificationRun(
                    descriptor,
                    generation,
                    cancellation.Token))
            {
                return;
            }

            SetNotificationMonitorStatus("GitHub event monitoring is enabled.");
            await foreach (var notification in aliveClient.ListenAsync(
                               descriptor.Account.Session,
                               cancellation.Token))
            {
                if (!IsCurrentGitHubNotificationRun(
                        descriptor,
                        generation,
                        cancellation.Token))
                {
                    return;
                }

                await HandleGitHubNotificationAsync(
                        descriptor,
                        resolved.Repository,
                        resolved.Identity,
                        accountEmails,
                        notification,
                        generation,
                        cancellation.Token,
                        contentClient);
            }

            if (IsCurrentGitHubNotificationRun(
                    descriptor,
                    generation,
                    cancellation.Token))
            {
                SetNotificationMonitorStatus("GitHub monitoring is unavailable.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (IsCurrentGitHubNotificationRun(
                    descriptor,
                    generation,
                    cancellation.Token))
            {
                SetNotificationMonitorStatus("GitHub monitoring is unavailable.");
            }
        }
    }

    private async Task EnsureGitHubNotificationAccountAsync(
        CancellationToken cancellationToken)
    {
        if (githubDisposed ||
            diagnosticCaptureMode ||
            notificationDeliverySuppressed ||
            !settings.NotificationsEnabled ||
            repositoryRoot is null ||
            selectedGitHubAccountRow is not null ||
            GetGitHubNotificationAccount() is not null)
        {
            return;
        }

        await EnsureGitHubAccountsLoadedAsync()
            .WaitAsync(cancellationToken);
        if (githubDisposed ||
            diagnosticCaptureMode ||
            selectedGitHubAccountRow is not null ||
            GetGitHubNotificationAccount() is not null)
        {
            return;
        }

        var summary = githubAccountRows
            .Select(row => row.Summary)
            .FirstOrDefault(account => AreSameGitHubOrigin(
                account.ApiOrigin,
                GitHubApiEndpoint.GitHubCom));
        if (summary is null)
        {
            return;
        }

        var store = GetGitHubAccountStore();
        if (store is null)
        {
            return;
        }

        var account = await store.LoadAsync(
            summary.ApiOrigin,
            summary.Id,
            cancellationToken);
        if (!githubDisposed &&
            !diagnosticCaptureMode &&
            selectedGitHubAccountRow is null &&
            account is not null)
        {
            githubNotificationAccount = account;
        }
    }

    private bool ShouldLoadGitHubNotificationAccount() =>
        !githubDisposed &&
        !diagnosticCaptureMode &&
        !notificationDeliverySuppressed &&
        settings.NotificationsEnabled &&
        repositoryRoot is not null &&
        selectedGitHubAccountRow is null &&
        GetGitHubNotificationAccount() is null;

    private void InvalidateGitHubNotificationRun()
    {
        ++githubNotificationGeneration;
        githubNotificationMonitorCancellation?.Cancel();
        githubNotificationStartCancellation?.Cancel();
    }

    private async Task HandleGitHubNotificationAsync(
        GitHubNotificationMonitorDescriptor descriptor,
        GitHubRepository repository,
        GitHubRemoteRepositoryIdentity identity,
        IReadOnlyList<string> accountEmails,
        GitHubNotificationEvent notification,
        long generation,
        CancellationToken cancellationToken,
        GitHubNotificationContentClient contentClient)
    {
        if (!string.Equals(
                notification.Owner,
                identity.Owner,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                notification.Repository,
                identity.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var pullRequest = FindCachedNotificationPullRequest(
            identity,
            notification.PullRequestNumber);
        if (pullRequest is null)
        {
            return;
        }

        try
        {
            using var pullRequestClient = new GitHubPullRequestClient(
                CreateGitHubPullRequestOptions(identity.ApiOrigin));
            var refreshedPullRequest = await pullRequestClient.ReadAsync(
                descriptor.Account.Session,
                identity,
                pullRequest.Number,
                cancellationToken);
            if (refreshedPullRequest is null)
            {
                return;
            }

            if (!IsCurrentGitHubNotificationRun(
                    descriptor,
                    generation,
                    cancellationToken))
            {
                return;
            }

            pullRequest = refreshedPullRequest;
            CacheNotificationPullRequest(identity, refreshedPullRequest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return;
        }

        if (notification.Kind == GitHubNotificationEventKind.ChecksFailed)
        {
            if (accountEmails.Count == 0 ||
                string.IsNullOrWhiteSpace(notification.CommitSha))
            {
                return;
            }

            string? authorEmail;
            try
            {
                authorEmail = await repositoryService
                    .ReadNotificationCommitAuthorEmailAsync(
                        descriptor.RepositoryRoot,
                        notification.CommitSha,
                        cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(authorEmail) ||
                !accountEmails.Any(email => string.Equals(
                    email,
                    authorEmail,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
        }

        if (!IsCurrentGitHubNotificationRun(
                descriptor,
                generation,
                cancellationToken))
        {
            return;
        }

        var content = await contentClient.ReadAsync(
                descriptor.Account.Session,
                repository,
                pullRequest,
                notification,
                cancellationToken);
        if (content is null ||
            !IsCurrentGitHubNotificationRun(
                descriptor,
                generation,
                cancellationToken) ||
            HasDeliveredGitHubNotification(content) ||
            AllCheckRunsDelivered(content))
        {
            return;
        }

        if (!TryShowNotification(content.Title, content.Body, content.Target))
        {
            return;
        }

        RecordDeliveredGitHubNotification(content);
    }

    private async Task<GitHubNotificationRepositoryResolution>
        ResolveGitHubNotificationRepositoryAsync(
            GitHubNotificationMonitorDescriptor descriptor,
            CancellationToken cancellationToken)
    {
        var remotes = await repositoryService.GetRemotesAsync(
                descriptor.RepositoryRoot,
                cancellationToken);
        var contributionTarget = NativeSettingsStore.GetForkContributionTarget(
            settings,
            descriptor.RepositoryRoot);
        var upstream = remotes.FirstOrDefault(remote => string.Equals(
            remote.Name,
            "upstream",
            StringComparison.Ordinal));
        IEnumerable<RemoteSummary> candidates;
        if (contributionTarget == ForkContributionTarget.Parent &&
            upstream is not null)
        {
            candidates = [upstream];
        }
        else
        {
            candidates = remotes.OrderBy(remote => string.Equals(
                    remote.Name,
                    "origin",
                    StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1);
        }

        foreach (var remote in candidates)
        {
            if (!GitHubRemoteRepositoryIdentity.TryParse(
                    remote.Url,
                    out var identity) ||
                identity is null)
            {
                continue;
            }

            if (!AreSameGitHubOrigin(
                    identity.ApiOrigin,
                    GitHubApiEndpoint.GitHubCom))
            {
                return GitHubNotificationRepositoryResolution.Waiting(
                    "GitHub monitoring is unavailable for this repository host.");
            }

            using var detailClient = new GitHubRepositoryDetailClient(
                GitHubRepositoryDetailClientOptions.ForGitHubCom());
            var detail = await detailClient.ReadAsync(
                    descriptor.Account.Session,
                    identity,
                    cancellationToken);
            return detail is null
                ? GitHubNotificationRepositoryResolution.Waiting(
                    "GitHub monitoring is unavailable.")
                : GitHubNotificationRepositoryResolution.Ready(
                    identity,
                    detail.Repository);
        }

        return GitHubNotificationRepositoryResolution.Waiting(
            "GitHub monitoring is waiting for a GitHub.com remote.");
    }

    private GitHubNotificationMonitorDescriptor?
        CaptureGitHubNotificationMonitorDescriptor()
    {
        if (githubDisposed ||
            diagnosticCaptureMode ||
            notificationDeliverySuppressed ||
            !settings.NotificationsEnabled ||
            repositoryRoot is null)
        {
            return null;
        }

        var account = GetGitHubNotificationAccount();
        if (account is null)
        {
            return null;
        }

        return new GitHubNotificationMonitorDescriptor(
            repositoryRoot,
            account);
    }

    private string GetGitHubNotificationWaitingStatus() =>
        githubDisposed || diagnosticCaptureMode
            ? "GitHub monitoring is paused during diagnostics."
            : notificationDeliverySuppressed
                ? "GitHub monitoring is paused while delivery is suppressed."
            : !settings.NotificationsEnabled
                ? "GitHub monitoring is disabled in WinGit settings."
                : repositoryRoot is null
                    ? "GitHub monitoring is waiting for a repository."
                    : GetGitHubNotificationAccount() is null
                        ? "GitHub monitoring is waiting for a GitHub.com account."
                        : "GitHub monitoring is unavailable.";

    private GitHubStoredAccount? GetGitHubNotificationAccount()
    {
        if (selectedGitHubAccount is { } selected &&
            AreSameGitHubOrigin(
                selected.Summary.ApiOrigin,
                GitHubApiEndpoint.GitHubCom))
        {
            return selected;
        }

        return githubNotificationAccount is { } automatic &&
            AreSameGitHubOrigin(
                automatic.Summary.ApiOrigin,
                GitHubApiEndpoint.GitHubCom)
            ? automatic
            : null;
    }

    private static bool AreSameGitHubNotificationDescriptors(
        GitHubNotificationMonitorDescriptor? left,
        GitHubNotificationMonitorDescriptor? right) =>
        left is not null &&
        right is not null &&
        string.Equals(
            left.RepositoryRoot,
            right.RepositoryRoot,
            StringComparison.OrdinalIgnoreCase) &&
        ReferenceEquals(
            left.Account.Session,
            right.Account.Session);

    private bool IsCurrentGitHubNotificationRun(
        GitHubNotificationMonitorDescriptor descriptor,
        long generation,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        !githubDisposed &&
        generation == githubNotificationGeneration &&
        ReferenceEquals(githubNotificationMonitorDescriptor, descriptor);

    private GitHubPullRequest? FindCachedNotificationPullRequest(
        GitHubRemoteRepositoryIdentity repository,
        int number)
    {
        if (number <= 0)
        {
            return null;
        }

        lock (githubNotificationCacheGate)
        {
            githubNotificationPullRequests.TryGetValue(
                GetNotificationPullRequestKey(repository, number),
                out var pullRequest);
            return pullRequest;
        }
    }

    private bool HasDeliveredGitHubNotification(
        GitHubNotificationContent content)
    {
        lock (githubNotificationCacheGate)
        {
            return deliveredGitHubNotificationKeys.Contains(
                content.DeduplicationKey);
        }
    }

    private bool AllCheckRunsDelivered(GitHubNotificationContent content)
    {
        if (content.CheckRunIds.Count == 0)
        {
            return false;
        }

        lock (githubNotificationCacheGate)
        {
            return content.CheckRunIds.All(deliveredGitHubCheckRunIds.Contains);
        }
    }

    private void RecordDeliveredGitHubNotification(
        GitHubNotificationContent content)
    {
        lock (githubNotificationCacheGate)
        {
            if (deliveredGitHubNotificationKeys.Add(content.DeduplicationKey))
            {
                deliveredGitHubNotificationOrder.Enqueue(
                    content.DeduplicationKey);
            }

            foreach (var checkRunId in content.AllCheckRunIds.Where(id => id > 0))
            {
                if (deliveredGitHubCheckRunIds.Add(checkRunId))
                {
                    deliveredGitHubCheckRunOrder.Enqueue(checkRunId);
                }
            }

            while (deliveredGitHubNotificationOrder.Count >
                   MaximumDeliveredNotifications)
            {
                deliveredGitHubNotificationKeys.Remove(
                    deliveredGitHubNotificationOrder.Dequeue());
            }

            while (deliveredGitHubCheckRunOrder.Count > MaximumDeliveredCheckRuns)
            {
                deliveredGitHubCheckRunIds.Remove(
                    deliveredGitHubCheckRunOrder.Dequeue());
            }
        }
    }

    private void ClearDeliveredGitHubNotificationCache()
    {
        lock (githubNotificationCacheGate)
        {
            deliveredGitHubNotificationKeys.Clear();
            deliveredGitHubNotificationOrder.Clear();
            deliveredGitHubCheckRunIds.Clear();
            deliveredGitHubCheckRunOrder.Clear();
        }
    }

    private static string GetNotificationPullRequestKey(
        GitHubRemoteRepositoryIdentity repository,
        int number) =>
        $"{repository.ApiOrigin.AbsoluteUri}|{repository.Owner}|{repository.Name}|{number}";

    private sealed record GitHubNotificationMonitorDescriptor(
        string RepositoryRoot,
        GitHubStoredAccount Account);

    private sealed record GitHubNotificationRepositoryResolution(
        GitHubRemoteRepositoryIdentity? Identity,
        GitHubRepository? Repository,
        string Status)
    {
        public static GitHubNotificationRepositoryResolution Ready(
            GitHubRemoteRepositoryIdentity identity,
            GitHubRepository repository) =>
            new(identity, repository, "GitHub event monitoring is enabled.");

        public static GitHubNotificationRepositoryResolution Waiting(
            string status) =>
            new(null, null, status);
    }
}
