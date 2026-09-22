using Microsoft.UI.Dispatching;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private const int MaximumRepositoryIndicatorCandidates = 64;

    private NativeRepositoryIndicatorScheduler? repositoryIndicatorScheduler;
    private DispatcherQueue? repositoryIndicatorDispatcherQueue;
    private long repositoryIndicatorDeliveryGeneration;
    private int repositoryIndicatorDeliveryEnabled;
    private readonly SemaphoreSlim repositoryIndicatorTransitionGate = new(1, 1);

    /// <summary>
    /// Raised on the window dispatcher when a background indicator snapshot
    /// changes. Chooser-row wiring can keep its own path-keyed snapshot map.
    /// </summary>
    internal event EventHandler<NativeRepositoryIndicatorUpdatedEventArgs>?
        RepositoryIndicatorUpdated;

    internal async Task StartRepositoryIndicatorsAsync()
    {
        await repositoryIndicatorTransitionGate.WaitAsync();
        try
        {
            if (diagnosticCaptureMode || appWindowCloseCleanupStarted)
            {
                DisableRepositoryIndicatorDelivery();
                return;
            }

            repositoryIndicatorDispatcherQueue ??= RootGrid.DispatcherQueue;
            var scheduler = EnsureRepositoryIndicatorScheduler();
            Interlocked.Increment(ref repositoryIndicatorDeliveryGeneration);
            Volatile.Write(ref repositoryIndicatorDeliveryEnabled, 1);
            await scheduler.StartAsync();
        }
        finally
        {
            repositoryIndicatorTransitionGate.Release();
        }
    }

    internal Task SetRepositoryIndicatorsEnabledAsync(bool enabled)
    {
        return enabled
            ? StartRepositoryIndicatorsAsync()
            : StopRepositoryIndicatorsAsync();
    }

    internal async Task StopRepositoryIndicatorsAsync()
    {
        DisableRepositoryIndicatorDelivery();
        await repositoryIndicatorTransitionGate.WaitAsync();
        try
        {
            if (repositoryIndicatorScheduler is not null)
            {
                await repositoryIndicatorScheduler.StopAsync();
            }
        }
        finally
        {
            repositoryIndicatorTransitionGate.Release();
        }
    }

    internal void PauseRepositoryIndicators() => repositoryIndicatorScheduler?.Pause();

    internal void ResumeRepositoryIndicators() => repositoryIndicatorScheduler?.Resume();

    private NativeRepositoryIndicatorScheduler EnsureRepositoryIndicatorScheduler()
    {
        if (repositoryIndicatorScheduler is not null)
        {
            return repositoryIndicatorScheduler;
        }

        repositoryIndicatorScheduler = new NativeRepositoryIndicatorScheduler(
            repositoryService,
            GetRepositoryIndicatorInputAsync);
        repositoryIndicatorScheduler.Updated += RepositoryIndicatorScheduler_Updated;
        return repositoryIndicatorScheduler;
    }

    private Task<NativeRepositoryIndicatorSchedulerInput> GetRepositoryIndicatorInputAsync()
    {
        var dispatcherQueue = repositoryIndicatorDispatcherQueue;
        if (dispatcherQueue is null)
        {
            return Task.FromResult(NativeRepositoryIndicatorSchedulerInput.Stopped);
        }

        if (dispatcherQueue.HasThreadAccess)
        {
            return Task.FromResult(CreateRepositoryIndicatorInput());
        }

        var completion = new TaskCompletionSource<NativeRepositoryIndicatorSchedulerInput>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    completion.TrySetResult(CreateRepositoryIndicatorInput());
                }
                catch (Exception)
                {
                    completion.TrySetResult(NativeRepositoryIndicatorSchedulerInput.Stopped);
                }
            }))
        {
            completion.TrySetResult(NativeRepositoryIndicatorSchedulerInput.Stopped);
        }

        return completion.Task;
    }

    private NativeRepositoryIndicatorSchedulerInput CreateRepositoryIndicatorInput() =>
        new(
            GetRepositoryIndicatorCandidates().ToArray(),
            diagnosticCaptureMode || appWindowCloseCleanupStarted);

    private IReadOnlyList<string> GetRepositoryIndicatorCandidates()
    {
        var selectedPath = repositoryRoot;
        var candidates = new List<string>(MaximumRepositoryIndicatorCandidates);
        foreach (var configuredPath in settings.RecentRepositories ?? [])
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                continue;
            }

            string normalizedPath;
            try
            {
                normalizedPath = NativeSettingsStore.NormalizeRepositoryPath(configuredPath);
            }
            catch (Exception)
            {
                continue;
            }

            if (selectedPath is not null
                && string.Equals(
                    normalizedPath,
                    selectedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (candidates.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            candidates.Add(normalizedPath);
            if (candidates.Count == MaximumRepositoryIndicatorCandidates)
            {
                break;
            }
        }

        return candidates;
    }

    private void RepositoryIndicatorScheduler_Updated(
        object? sender,
        NativeRepositoryIndicatorUpdatedEventArgs args)
    {
        if (Volatile.Read(ref repositoryIndicatorDeliveryEnabled) == 0)
        {
            return;
        }

        var generation = Volatile.Read(ref repositoryIndicatorDeliveryGeneration);
        var dispatcherQueue = repositoryIndicatorDispatcherQueue;
        if (dispatcherQueue is null)
        {
            return;
        }

        void Deliver()
        {
            if (Volatile.Read(ref repositoryIndicatorDeliveryEnabled) != 0
                && Volatile.Read(ref repositoryIndicatorDeliveryGeneration) == generation)
            {
                RepositoryIndicatorUpdated?.Invoke(this, args);
            }
        }

        if (dispatcherQueue.HasThreadAccess)
        {
            Deliver();
            return;
        }

        dispatcherQueue.TryEnqueue(Deliver);
    }

    private void DisableRepositoryIndicatorDelivery()
    {
        Volatile.Write(ref repositoryIndicatorDeliveryEnabled, 0);
        Interlocked.Increment(ref repositoryIndicatorDeliveryGeneration);
    }
}

internal sealed record NativeRepositoryIndicatorSchedulerInput(
    IReadOnlyList<string> RepositoryPaths,
    bool StopRequested)
{
    internal static NativeRepositoryIndicatorSchedulerInput Stopped { get; } =
        new([], StopRequested: true);
}

internal sealed record NativeRepositoryIndicatorSnapshot(
    string RepositoryPath,
    int ChangedFileCount,
    int Ahead,
    int Behind,
    string Branch,
    string Upstream,
    bool IsDetached,
    bool IsUnborn,
    DateTimeOffset ObservedAt);

internal sealed class NativeRepositoryIndicatorUpdatedEventArgs : EventArgs
{
    internal NativeRepositoryIndicatorUpdatedEventArgs(
        string repositoryPath,
        NativeRepositoryIndicatorSnapshot? snapshot,
        bool cleared,
        bool missing,
        bool fetchAttempted,
        bool fetchFailed,
        string? error)
    {
        RepositoryPath = repositoryPath;
        Snapshot = snapshot;
        Cleared = cleared;
        Missing = missing;
        FetchAttempted = fetchAttempted;
        FetchFailed = fetchFailed;
        Error = error;
    }

    public string RepositoryPath { get; }

    public NativeRepositoryIndicatorSnapshot? Snapshot { get; }

    public bool Cleared { get; }

    public bool Missing { get; }

    public bool FetchAttempted { get; }

    public bool FetchFailed { get; }

    /// <summary>Contains only a generic, credential-safe presentation message.</summary>
    public string? Error { get; }
}

internal sealed class NativeRepositoryIndicatorScheduler
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FetchMinimumInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaximumSkew = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefreshSkew = CreateSkew();

    private const int MaximumRepositoriesPerCycle = 64;
    private const int MaximumRemotesPerRepository = 32;

    private readonly object sync = new();
    private readonly GitRepositoryService repositoryService;
    private readonly Func<Task<NativeRepositoryIndicatorSchedulerInput>> getInputAsync;
    private readonly Dictionary<string, DateTimeOffset> lastFetchAt =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> knownRepositoryPaths =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? cancellation;
    private Task? runTask;
    private TaskCompletionSource<bool> resumeSignal = CreateCompletedSignal();
    private bool paused;

    internal NativeRepositoryIndicatorScheduler(
        GitRepositoryService repositoryService,
        Func<Task<NativeRepositoryIndicatorSchedulerInput>> getInputAsync)
    {
        this.repositoryService = repositoryService;
        this.getInputAsync = getInputAsync;
    }

    internal event EventHandler<NativeRepositoryIndicatorUpdatedEventArgs>? Updated;

    internal Task StartAsync()
    {
        lock (sync)
        {
            if (runTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            runTask = RunAsync(cancellation.Token);
        }

        return Task.CompletedTask;
    }

    internal async Task StopAsync()
    {
        Task? task;
        CancellationTokenSource? source;
        lock (sync)
        {
            source = cancellation;
            task = runTask;
            source?.Cancel();
            resumeSignal.TrySetResult(true);
        }

        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the normal shutdown path.
            }
        }

        lock (sync)
        {
            if (ReferenceEquals(runTask, task))
            {
                runTask = null;
                cancellation = null;
                paused = false;
                resumeSignal = CreateCompletedSignal();
            }
        }

        ClearKnownIndicators();
        source?.Dispose();
    }

    internal void Pause()
    {
        lock (sync)
        {
            if (paused)
            {
                return;
            }

            paused = true;
            resumeSignal = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal void Resume()
    {
        TaskCompletionSource<bool>? signal = null;
        lock (sync)
        {
            if (!paused)
            {
                return;
            }

            paused = false;
            signal = resumeSignal;
            resumeSignal = CreateCompletedSignal();
        }

        signal.TrySetResult(true);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                    InitialDelay + RefreshInterval + RefreshSkew,
                    cancellationToken)
                .ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var input = await getInputAsync()
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (input.StopRequested)
                {
                    return;
                }

                var startedAt = DateTimeOffset.UtcNow;
                await RefreshCycleAsync(input.RepositoryPaths, cancellationToken)
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var elapsed = DateTimeOffset.UtcNow - startedAt;
                var delay = RefreshInterval - elapsed;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                await Task.Delay(delay + RefreshSkew, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is the normal shutdown path.
        }
    }

    private async Task RefreshCycleAsync(
        IReadOnlyList<string> configuredCandidates,
        CancellationToken cancellationToken)
    {
        var candidates = configuredCandidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(TryNormalizePath)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRepositoriesPerCycle)
            .ToArray();
        ClearObsoleteIndicators(candidates);

        foreach (var repositoryPath in candidates)
        {
            await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await RefreshRepositoryAsync(repositoryPath, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RefreshRepositoryAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(repositoryPath))
        {
            PublishUnavailable(
                repositoryPath,
                missing: true,
                error: "Repository folder is unavailable.");
            return;
        }

        RepositoryStatus status;
        try
        {
            status = await repositoryService
                .GetStatusAsync(repositoryPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            PublishUnavailable(
                repositoryPath,
                missing: false,
                error: "Repository status is unavailable.");
            return;
        }

        var fetchAttempted = false;
        var fetchFailed = false;
        var fetchError = (string?)null;
        if (IsFetchDue(repositoryPath))
        {
            IReadOnlyList<RemoteSummary> remotes;
            try
            {
                remotes = await repositoryService
                    .GetRemotesAsync(repositoryPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                remotes = [];
                fetchFailed = true;
                fetchError = "Unable to inspect repository remotes.";
            }

            var fetchRemote = SelectBackgroundFetchRemote(remotes, status.Upstream);
            if (fetchRemote is not null)
            {
                MarkFetchAttempt(repositoryPath);
                fetchAttempted = true;
                await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await repositoryService
                        .FetchAsync(
                            repositoryPath,
                            fetchRemote.Name,
                            cancellationToken,
                            isBackgroundTask: true)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    fetchFailed = true;
                    fetchError = "Unable to fetch repository remotes.";
                }

                try
                {
                    status = await repositoryService
                        .GetStatusAsync(repositoryPath, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    fetchFailed = true;
                    fetchError ??= "Unable to refresh repository status.";
                }
            }
        }

        PublishSnapshot(
            status,
            fetchAttempted,
            fetchFailed,
            fetchError);
    }

    private static RemoteSummary? SelectBackgroundFetchRemote(
        IReadOnlyList<RemoteSummary> remotes,
        string upstream)
    {
        var candidates = remotes
            .Where(remote => !string.IsNullOrWhiteSpace(remote.Name))
            .Take(MaximumRemotesPerRepository)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var trackingRemote = candidates
            .Where(remote => !string.IsNullOrWhiteSpace(upstream)
                && upstream.StartsWith(remote.Name + "/", StringComparison.Ordinal))
            .OrderByDescending(remote => remote.Name.Length)
            .FirstOrDefault();
        return trackingRemote
            ?? candidates.FirstOrDefault(remote =>
                string.Equals(remote.Name, "origin", StringComparison.Ordinal))
            ?? candidates[0];
    }

    private bool IsFetchDue(string repositoryPath)
    {
        lock (sync)
        {
            return !lastFetchAt.TryGetValue(repositoryPath, out var lastFetch)
                || DateTimeOffset.UtcNow - lastFetch >= FetchMinimumInterval;
        }
    }

    private void MarkFetchAttempt(string repositoryPath)
    {
        lock (sync)
        {
            lastFetchAt[repositoryPath] = DateTimeOffset.UtcNow;
        }
    }

    private void PublishSnapshot(
        RepositoryStatus status,
        bool fetchAttempted,
        bool fetchFailed,
        string? fetchError)
    {
        TrackRepository(status.RootPath);
        Updated?.Invoke(
            this,
            new NativeRepositoryIndicatorUpdatedEventArgs(
                status.RootPath,
                new NativeRepositoryIndicatorSnapshot(
                    status.RootPath,
                    status.Changes.Count,
                    status.Ahead,
                    status.Behind,
                    status.Branch,
                    status.Upstream,
                    status.IsDetached,
                    status.IsUnborn,
                    DateTimeOffset.UtcNow),
                cleared: false,
                missing: false,
                fetchAttempted: fetchAttempted,
                fetchFailed: fetchFailed,
                error: fetchError));
    }

    private void PublishUnavailable(string repositoryPath, bool missing, string error)
    {
        TrackRepository(repositoryPath);
        Updated?.Invoke(
            this,
            new NativeRepositoryIndicatorUpdatedEventArgs(
                repositoryPath,
                snapshot: null,
                cleared: true,
                missing: missing,
                fetchAttempted: false,
                fetchFailed: false,
                error: error));
    }

    private void ClearObsoleteIndicators(IReadOnlyCollection<string> candidates)
    {
        List<string> obsolete;
        lock (sync)
        {
            obsolete = knownRepositoryPaths
                .Where(path => !candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                .ToList();
            foreach (var path in obsolete)
            {
                knownRepositoryPaths.Remove(path);
                lastFetchAt.Remove(path);
            }
        }

        foreach (var path in obsolete)
        {
            PublishCleared(path);
        }
    }

    private void ClearKnownIndicators()
    {
        List<string> known;
        lock (sync)
        {
            known = [.. knownRepositoryPaths];
            knownRepositoryPaths.Clear();
            lastFetchAt.Clear();
        }

        foreach (var path in known)
        {
            PublishCleared(path);
        }
    }

    private void PublishCleared(string repositoryPath)
    {
        Updated?.Invoke(
            this,
            new NativeRepositoryIndicatorUpdatedEventArgs(
                repositoryPath,
                snapshot: null,
                cleared: true,
                missing: false,
                fetchAttempted: false,
                fetchFailed: false,
                error: null));
    }

    private void TrackRepository(string repositoryPath)
    {
        lock (sync)
        {
            knownRepositoryPaths.Add(repositoryPath);
        }
    }

    private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task waitTask;
        lock (sync)
        {
            waitTask = paused ? resumeSignal.Task : Task.CompletedTask;
        }

        await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? TryNormalizePath(string path)
    {
        try
        {
            return NativeSettingsStore.NormalizeRepositoryPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static TaskCompletionSource<bool> CreateCompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(true);
        return signal;
    }

    private static TimeSpan CreateSkew()
    {
        var milliseconds = Random.Shared.Next(
            1,
            (int)MaximumSkew.TotalMilliseconds + 1);
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
