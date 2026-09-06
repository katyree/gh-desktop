using System.Globalization;
using System.Net;
using System.Text.Json;

namespace WinGit.Core.GitHub;

/// <summary>Options for one explicit, read-only pull-request checks read.</summary>
public sealed class GitHubPullRequestChecksClientOptions
{
    public const int DefaultMaximumResponseBytes = 1_048_576;
    public const int DefaultMaximumPages = 100;

    private GitHubPullRequestChecksClientOptions(
        Uri apiOrigin,
        TimeSpan requestTimeout,
        int maximumResponseBytes,
        int maximumPages)
    {
        ApiOrigin = apiOrigin;
        RequestTimeout = requestTimeout;
        MaximumResponseBytes = maximumResponseBytes;
        MaximumPages = maximumPages;
    }

    /// <summary>Uses https://api.github.com/repos/{owner}/{repo}/...</summary>
    public static GitHubPullRequestChecksClientOptions ForGitHubCom(
        TimeSpan? requestTimeout = null,
        int maximumResponseBytes = DefaultMaximumResponseBytes,
        int maximumPages = DefaultMaximumPages) =>
        Create(
            GitHubApiEndpoint.GitHubCom,
            requestTimeout,
            maximumResponseBytes,
            maximumPages);

    /// <summary>Uses an Enterprise host's /api/v3 checks endpoints.</summary>
    public static GitHubPullRequestChecksClientOptions ForEnterprise(
        Uri enterpriseHost,
        TimeSpan? requestTimeout = null,
        int maximumResponseBytes = DefaultMaximumResponseBytes,
        int maximumPages = DefaultMaximumPages) =>
        Create(
            GitHubApiEndpoint.ForEnterprise(enterpriseHost),
            requestTimeout,
            maximumResponseBytes,
            maximumPages);

    public Uri ApiOrigin { get; }

    public TimeSpan RequestTimeout { get; }

    public int MaximumResponseBytes { get; }

    public int MaximumPages { get; }

    private static GitHubPullRequestChecksClientOptions Create(
        Uri apiOrigin,
        TimeSpan? requestTimeout,
        int maximumResponseBytes,
        int maximumPages)
    {
        var timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The GitHub request timeout must be between one tick and ten minutes.");
        }

        if (maximumResponseBytes < 1 || maximumResponseBytes > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResponseBytes),
                "The GitHub response limit must be between 1 and 4194304 bytes.");
        }

        if (maximumPages < 1 || maximumPages > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPages),
                "The GitHub checks page limit must be between 1 and 1000 pages.");
        }

        return new GitHubPullRequestChecksClientOptions(
            apiOrigin,
            timeout,
            maximumResponseBytes,
            maximumPages);
    }
}

public enum GitHubPullRequestChecksErrorKind
{
    InvalidConfiguration,
    SessionMismatch,
    Network,
    Timeout,
    Unauthorized,
    Forbidden,
    RateLimited,
    SamlRequired,
    NotFound,
    InvalidResponse,
    PageLimitReached,
}

/// <summary>Sanitized failure from the authenticated pull-request checks boundary.</summary>
public sealed class GitHubPullRequestChecksException : InvalidOperationException
{
    public GitHubPullRequestChecksException(GitHubPullRequestChecksErrorKind kind)
        : base(GetMessage(kind))
    {
        Kind = kind;
    }

    public GitHubPullRequestChecksErrorKind Kind { get; }

    private static string GetMessage(GitHubPullRequestChecksErrorKind kind) =>
        kind switch
        {
            GitHubPullRequestChecksErrorKind.InvalidConfiguration =>
                "GitHub pull request checks are not configured.",
            GitHubPullRequestChecksErrorKind.SessionMismatch =>
                "The GitHub account session does not match this repository host.",
            GitHubPullRequestChecksErrorKind.Network =>
                "GitHub pull request checks could not be reached.",
            GitHubPullRequestChecksErrorKind.Timeout =>
                "The GitHub pull request checks request timed out.",
            GitHubPullRequestChecksErrorKind.Unauthorized =>
                "GitHub rejected the pull request checks credentials.",
            GitHubPullRequestChecksErrorKind.Forbidden =>
                "GitHub refused access to pull request checks.",
            GitHubPullRequestChecksErrorKind.RateLimited =>
                "GitHub rate limited the pull request checks request.",
            GitHubPullRequestChecksErrorKind.SamlRequired =>
                "GitHub requires organization sign-in before pull request checks access.",
            GitHubPullRequestChecksErrorKind.NotFound =>
                "The GitHub repository or pull request head was not found.",
            GitHubPullRequestChecksErrorKind.InvalidResponse =>
                "GitHub returned an invalid pull request checks response.",
            GitHubPullRequestChecksErrorKind.PageLimitReached =>
                "The GitHub pull request checks page limit was reached.",
            _ => "GitHub pull request checks access failed.",
        };
}

public sealed class GitHubPullRequestChecksCancelledException
    : OperationCanceledException
{
    public GitHubPullRequestChecksCancelledException()
        : base("GitHub pull request checks loading was cancelled.")
    {
    }
}

public enum GitHubCommitStatusState
{
    Error,
    Failure,
    Pending,
    Success,
}

/// <summary>
/// One legacy commit-status context for the immutable pull-request head SHA.
/// Target URLs are data only; this client never opens them.
/// </summary>
public sealed class GitHubCommitStatusContext
{
    internal GitHubCommitStatusContext(
        Uri apiOrigin,
        long id,
        GitHubCommitStatusState state,
        string context,
        string? description,
        Uri? targetUrl,
        DateTimeOffset? createdAt,
        DateTimeOffset? updatedAt)
    {
        ApiOrigin = apiOrigin;
        Id = id;
        State = state;
        Context = context;
        Description = description;
        TargetUrl = targetUrl;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Uri ApiOrigin { get; }

    public long Id { get; }

    public GitHubCommitStatusState State { get; }

    public string Context { get; }

    public string? Description { get; }

    /// <summary>Optional third-party build detail URL returned by GitHub.</summary>
    public Uri? TargetUrl { get; }

    public DateTimeOffset? CreatedAt { get; }

    public DateTimeOffset? UpdatedAt { get; }

    public override string ToString() => "GitHub commit status context.";
}

public enum GitHubCheckRunStatusKind
{
    Queued,
    InProgress,
    Completed,
    Unknown,
}

public enum GitHubCheckRunConclusionKind
{
    ActionRequired,
    Cancelled,
    TimedOut,
    Failure,
    Neutral,
    Success,
    Skipped,
    Stale,
    Unknown,
}

/// <summary>
/// One check run for the immutable pull-request head SHA. A null conclusion
/// remains distinct from an unknown or known completed conclusion.
/// </summary>
public sealed class GitHubCheckRun
{
    internal GitHubCheckRun(
        Uri apiOrigin,
        long id,
        string headSha,
        string name,
        GitHubCheckRunStatusKind status,
        string? unknownStatus,
        GitHubCheckRunConclusionKind? conclusion,
        string? unknownConclusion,
        string? description,
        string? appName,
        long? checkSuiteId,
        Uri detailsUrl,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt)
    {
        ApiOrigin = apiOrigin;
        Id = id;
        HeadSha = headSha;
        Name = name;
        Status = status;
        UnknownStatus = unknownStatus;
        Conclusion = conclusion;
        UnknownConclusion = unknownConclusion;
        Description = description;
        AppName = appName;
        CheckSuiteId = checkSuiteId;
        DetailsUrl = detailsUrl;
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }

    public Uri ApiOrigin { get; }

    public long Id { get; }

    public string HeadSha { get; }

    public string Name { get; }

    public GitHubCheckRunStatusKind Status { get; }

    /// <summary>Raw status value when GitHub adds a status this client does not know.</summary>
    public string? UnknownStatus { get; }

    /// <summary>Null while no conclusion is available, including pending runs.</summary>
    public GitHubCheckRunConclusionKind? Conclusion { get; }

    /// <summary>Raw conclusion value when <see cref="Conclusion"/> is Unknown.</summary>
    public string? UnknownConclusion { get; }

    /// <summary>The optional summary supplied in the check output.</summary>
    public string? Description { get; }

    public string? AppName { get; }

    public long? CheckSuiteId { get; }

    /// <summary>Validated GitHub HTML detail URL; the client never opens it.</summary>
    public Uri DetailsUrl { get; }

    public DateTimeOffset? StartedAt { get; }

    public DateTimeOffset? CompletedAt { get; }

    public override string ToString() => "GitHub check run.";
}

/// <summary>
/// One GitHub Actions job step belonging to a modern check run. Status and
/// conclusion values retain unknown service values so the native UI does not
/// silently reinterpret a newer API value.
/// </summary>
public sealed class GitHubCheckRunJobStep
{
    internal GitHubCheckRunJobStep(
        string name,
        int number,
        GitHubCheckRunStatusKind status,
        string? unknownStatus,
        GitHubCheckRunConclusionKind? conclusion,
        string? unknownConclusion,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt)
    {
        Name = name;
        Number = number;
        Status = status;
        UnknownStatus = unknownStatus;
        Conclusion = conclusion;
        UnknownConclusion = unknownConclusion;
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }

    public string Name { get; }

    public int Number { get; }

    public GitHubCheckRunStatusKind Status { get; }

    public string? UnknownStatus { get; }

    public GitHubCheckRunConclusionKind? Conclusion { get; }

    public string? UnknownConclusion { get; }

    public DateTimeOffset? StartedAt { get; }

    public DateTimeOffset? CompletedAt { get; }

    public override string ToString() => "GitHub check-run job step.";
}

/// <summary>
/// The optional GitHub Actions job-step read for one immutable check run.
/// Providers that expose check runs but not Actions jobs return a complete
/// result with <see cref="IsSupported"/> set to false.
/// </summary>
public sealed class GitHubCheckRunJobStepsResult
{
    internal GitHubCheckRunJobStepsResult(
        Uri apiOrigin,
        string owner,
        string repositoryName,
        string headSha,
        long checkRunId,
        long? actionsJobId,
        IReadOnlyList<GitHubCheckRunJobStep> steps,
        bool isSupported,
        bool isComplete,
        GitHubPullRequestChecksErrorKind? errorKind)
    {
        ApiOrigin = apiOrigin;
        Owner = owner;
        RepositoryName = repositoryName;
        HeadSha = headSha;
        CheckRunId = checkRunId;
        ActionsJobId = actionsJobId;
        Steps = new List<GitHubCheckRunJobStep>(steps).AsReadOnly();
        IsSupported = isSupported;
        IsComplete = isComplete;
        ErrorKind = errorKind;
    }

    public Uri ApiOrigin { get; }

    public string Owner { get; }

    public string RepositoryName { get; }

    public string HeadSha { get; }

    public long CheckRunId { get; }

    /// <summary>The Actions job id when this check run maps to a job.</summary>
    public long? ActionsJobId { get; }

    public IReadOnlyList<GitHubCheckRunJobStep> Steps { get; }

    public bool IsSupported { get; }

    public bool IsComplete { get; }

    public GitHubPullRequestChecksErrorKind? ErrorKind { get; }

    public override string ToString() => "GitHub check-run job steps result.";
}

public enum GitHubPullRequestChecksRerunTargetKind
{
    Unsupported,
    CheckSuite,
    ActionsJob,
}

/// <summary>Result of one explicit rerun request for a selected check.</summary>
public sealed class GitHubPullRequestChecksRerunResult
{
    internal GitHubPullRequestChecksRerunResult(
        GitHubPullRequestChecksRerunTargetKind target,
        bool succeeded,
        GitHubPullRequestChecksErrorKind? errorKind)
    {
        Target = target;
        Succeeded = succeeded;
        ErrorKind = errorKind;
    }

    public GitHubPullRequestChecksRerunTargetKind Target { get; }

    public bool Succeeded { get; }

    public GitHubPullRequestChecksErrorKind? ErrorKind { get; }

    public bool IsSupported => Target !=
        GitHubPullRequestChecksRerunTargetKind.Unsupported;

    public override string ToString() => "GitHub pull request checks rerun result.";
}

/// <summary>
/// A partial or complete commit-status read for one pull-request head SHA.
/// <see cref="Statuses"/> comes from GitHub's combined-status endpoint, so it
/// contains the latest entry for each context rather than historical status
/// rows from the separate statuses endpoint.
/// </summary>
public sealed class GitHubPullRequestStatusResult
{
    internal GitHubPullRequestStatusResult(
        Uri apiOrigin,
        string owner,
        string repositoryName,
        string headSha,
        GitHubCommitStatusState? legacyCombinedState,
        int totalCount,
        IReadOnlyList<GitHubCommitStatusContext> statuses,
        int pagesFetched,
        bool isComplete,
        GitHubPullRequestChecksErrorKind? errorKind)
    {
        ApiOrigin = apiOrigin;
        Owner = owner;
        RepositoryName = repositoryName;
        HeadSha = headSha;
        LegacyCombinedState = legacyCombinedState;
        TotalCount = totalCount;
        Statuses = new List<GitHubCommitStatusContext>(statuses).AsReadOnly();
        PagesFetched = pagesFetched;
        IsComplete = isComplete;
        ErrorKind = errorKind;
    }

    public Uri ApiOrigin { get; }

    public string Owner { get; }

    public string RepositoryName { get; }

    public string HeadSha { get; }

    /// <summary>
    /// GitHub's legacy combined-status state. It is null when the status pages
    /// are partial or when page responses disagree while the head is changing.
    /// This does not aggregate modern check runs.
    /// </summary>
    public GitHubCommitStatusState? LegacyCombinedState { get; }

    public int TotalCount { get; }

    public IReadOnlyList<GitHubCommitStatusContext> Statuses { get; }

    public int PagesFetched { get; }

    public bool IsComplete { get; }

    public GitHubPullRequestChecksErrorKind? ErrorKind { get; }

    public override string ToString() => "GitHub pull request status result.";
}

/// <summary>
/// A partial or complete check-run read for one pull-request head SHA.
/// <see cref="IsComplete"/> describes the locally paginated response from the
/// reference endpoint. GitHub can limit that endpoint to check runs from the
/// 1000 most recent check suites, so completion does not claim exhaustive
/// historical coverage.
/// </summary>
public sealed class GitHubPullRequestCheckRunsResult
{
    internal GitHubPullRequestCheckRunsResult(
        Uri apiOrigin,
        string owner,
        string repositoryName,
        string headSha,
        int totalCount,
        IReadOnlyList<GitHubCheckRun> checkRuns,
        int pagesFetched,
        bool isComplete,
        GitHubPullRequestChecksErrorKind? errorKind)
    {
        ApiOrigin = apiOrigin;
        Owner = owner;
        RepositoryName = repositoryName;
        HeadSha = headSha;
        TotalCount = totalCount;
        CheckRuns = new List<GitHubCheckRun>(checkRuns).AsReadOnly();
        PagesFetched = pagesFetched;
        IsComplete = isComplete;
        ErrorKind = errorKind;
    }

    public Uri ApiOrigin { get; }

    public string Owner { get; }

    public string RepositoryName { get; }

    public string HeadSha { get; }

    public int TotalCount { get; }

    public IReadOnlyList<GitHubCheckRun> CheckRuns { get; }

    public int PagesFetched { get; }

    public bool IsComplete { get; }

    public GitHubPullRequestChecksErrorKind? ErrorKind { get; }

    public override string ToString() => "GitHub pull request check-run result.";
}

/// <summary>
/// The two read-only GitHub checks views for one immutable pull-request head
/// SHA. No aggregate state is exposed unless both source reads are complete.
/// </summary>
public sealed class GitHubPullRequestChecksResult
{
    internal GitHubPullRequestChecksResult(
        GitHubPullRequestStatusResult status,
        GitHubPullRequestCheckRunsResult checkRuns)
    {
        Status = status;
        CheckRuns = checkRuns;
    }

    public GitHubPullRequestStatusResult Status { get; }

    public GitHubPullRequestCheckRunsResult CheckRuns { get; }

    public bool IsComplete => Status.IsComplete && CheckRuns.IsComplete;

    /// <summary>
    /// The API-provided combined legacy status state. A partial read never
    /// reports a successful aggregate.
    /// </summary>
    public GitHubCommitStatusState? LegacyCombinedState =>
        IsComplete ? Status.LegacyCombinedState : null;

    public GitHubPullRequestChecksErrorKind? ErrorKind =>
        Status.ErrorKind ?? CheckRuns.ErrorKind;

    public override string ToString() => "GitHub pull request checks result.";
}

/// <summary>
/// Loads legacy commit statuses and modern check runs for one immutable
/// pull-request head SHA. Requests are origin- and session-bound, and all
/// pagination is constructed locally.
/// </summary>
public sealed class GitHubPullRequestChecksClient : IDisposable
{
    private const int PageSize = 100;
    private const int MinimumFullShaLength = 40;
    private const int MaximumFullShaLength = 64;
    private const int MaximumOwnerOrRepositoryLength = 256;
    private const int MaximumContextLength = 256;
    private const int MaximumDescriptionLength = 128 * 1024;
    private const int MaximumNameLength = 256;
    private const int MaximumAppNameLength = 256;
    private const int MaximumUnknownValueLength = 64;
    private const int MaximumDateLength = 128;
    private const int MaximumUrlLength = 16_384;
    private const int MaximumJobSteps = 1_000;

    private readonly GitHubPullRequestChecksClientOptions options;
    private readonly GitHubApiTransport transport;
    private bool disposed;

    public GitHubPullRequestChecksClient(
        GitHubPullRequestChecksClientOptions options)
        : this(options, CreateTransport(options))
    {
    }

    internal GitHubPullRequestChecksClient(
        GitHubPullRequestChecksClientOptions options,
        HttpClient httpClient)
        : this(options, CreateTransport(options, httpClient))
    {
    }

    private GitHubPullRequestChecksClient(
        GitHubPullRequestChecksClientOptions options,
        GitHubApiTransport transport)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<GitHubPullRequestChecksResult> LoadAsync(
        GitHubAccountSession session,
        GitHubRemoteRepositoryIdentity repository,
        string headSha,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(repository);
        headSha = ValidateHeadSha(headSha);
        EnsureSessionAndRepositoryMatch(session, repository);
        ThrowIfCancellationRequested(cancellationToken);

        var statusTask = LoadStatusAsync(
            session,
            repository,
            headSha,
            cancellationToken);
        var checkRunsTask = LoadCheckRunsAsync(
            session,
            repository,
            headSha,
            cancellationToken);
        await Task.WhenAll(statusTask, checkRunsTask).ConfigureAwait(false);
        return new GitHubPullRequestChecksResult(
            statusTask.Result,
            checkRunsTask.Result);
    }

    /// <summary>
    /// Loads the Actions job steps for one selected check run. A check provider
    /// that does not expose an Actions workflow returns a complete,
    /// unsupported result without treating that provider as an API failure.
    /// </summary>
    public async Task<GitHubCheckRunJobStepsResult> LoadJobStepsAsync(
        GitHubAccountSession session,
        GitHubRemoteRepositoryIdentity repository,
        GitHubCheckRun checkRun,
        string headSha,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(checkRun);
        headSha = ValidateHeadSha(headSha);
        EnsureSessionAndRepositoryMatch(session, repository);
        EnsureCheckRunMatches(checkRun, headSha);
        ThrowIfCancellationRequested(cancellationToken);

        if (checkRun.CheckSuiteId is not { } checkSuiteId)
        {
            return UnsupportedJobStepsResult(repository, headSha, checkRun.Id);
        }

        GitHubApiTransportResponse workflowResponse;
        try
        {
            workflowResponse = await transport.GetAsync(
                    BuildWorkflowRunsByCheckSuiteEndpoint(
                        repository,
                        checkSuiteId),
                    session,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitHubApiTransportException exception)
        {
            if (exception.Kind == GitHubApiTransportErrorKind.Cancelled)
            {
                throw new GitHubPullRequestChecksCancelledException();
            }

            return FailedJobStepsResult(
                repository,
                headSha,
                checkRun.Id,
                MapTransportError(exception.Kind));
        }

        if ((int)workflowResponse.StatusCode is < 200 or >= 300)
        {
            return IsUnsupportedActionsResponse(workflowResponse.StatusCode)
                ? UnsupportedJobStepsResult(repository, headSha, checkRun.Id)
                : FailedJobStepsResult(
                    repository,
                    headSha,
                    checkRun.Id,
                    MapResponseError(workflowResponse));
        }

        WorkflowRunParseResult? workflowRun;
        try
        {
            workflowRun = ParseWorkflowRun(
                workflowResponse.Content,
                headSha,
                checkSuiteId);
        }
        catch (GitHubPullRequestChecksException exception)
        {
            return FailedJobStepsResult(
                repository,
                headSha,
                checkRun.Id,
                exception.Kind);
        }

        if (workflowRun is null)
        {
            return UnsupportedJobStepsResult(repository, headSha, checkRun.Id);
        }

        var jobsFetched = 0;
        for (var pageNumber = 1;
             pageNumber <= options.MaximumPages;
             pageNumber++)
        {
            ThrowIfCancellationRequested(cancellationToken);
            GitHubApiTransportResponse jobsResponse;
            try
            {
                jobsResponse = await transport.GetAsync(
                        BuildWorkflowRunJobsEndpoint(
                            repository,
                            workflowRun.Id,
                            pageNumber),
                        session,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GitHubApiTransportException exception)
            {
                if (exception.Kind == GitHubApiTransportErrorKind.Cancelled)
                {
                    throw new GitHubPullRequestChecksCancelledException();
                }

                return FailedJobStepsResult(
                    repository,
                    headSha,
                    checkRun.Id,
                    MapTransportError(exception.Kind));
            }

            if ((int)jobsResponse.StatusCode is < 200 or >= 300)
            {
                return IsUnsupportedActionsResponse(jobsResponse.StatusCode)
                    ? UnsupportedJobStepsResult(repository, headSha, checkRun.Id)
                    : FailedJobStepsResult(
                        repository,
                        headSha,
                        checkRun.Id,
                        MapResponseError(jobsResponse));
            }

            WorkflowJobsPageParseResult parsedJobs;
            try
            {
                parsedJobs = ParseWorkflowJobs(
                    jobsResponse.Content,
                    headSha);
            }
            catch (GitHubPullRequestChecksException exception)
            {
                return FailedJobStepsResult(
                    repository,
                    headSha,
                    checkRun.Id,
                    exception.Kind);
            }

            jobsFetched += parsedJobs.Jobs.Count;
            var matchingJob = parsedJobs.Jobs.FirstOrDefault(
                job => job.Id == checkRun.Id);
            if (matchingJob is not null)
            {
                return new GitHubCheckRunJobStepsResult(
                    repository.ApiOrigin,
                    repository.Owner,
                    repository.Name,
                    headSha,
                    checkRun.Id,
                    matchingJob.Id,
                    matchingJob.Steps,
                    isSupported: true,
                    isComplete: true,
                    errorKind: null);
            }

            ThrowIfCancellationRequested(cancellationToken);
            if (jobsFetched >= parsedJobs.TotalCount)
            {
                return UnsupportedJobStepsResult(
                    repository,
                    headSha,
                    checkRun.Id);
            }

            if (parsedJobs.SourceItemCount < PageSize)
            {
                return FailedJobStepsResult(
                    repository,
                    headSha,
                    checkRun.Id,
                    GitHubPullRequestChecksErrorKind.InvalidResponse);
            }
        }

        return FailedJobStepsResult(
            repository,
            headSha,
            checkRun.Id,
            GitHubPullRequestChecksErrorKind.PageLimitReached);
    }

    /// <summary>
    /// Explicitly requests a rerun for the selected check. GitHub Actions jobs
    /// use the Actions job endpoint when the selected run was mapped to one;
    /// other modern checks use their check suite. A legacy status has no
    /// rerun target and returns an unsupported result without a request.
    /// </summary>
    public async Task<GitHubPullRequestChecksRerunResult> RerunAsync(
        GitHubAccountSession session,
        GitHubRemoteRepositoryIdentity repository,
        GitHubCheckRun checkRun,
        string headSha,
        long? actionsJobId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(checkRun);
        headSha = ValidateHeadSha(headSha);
        EnsureSessionAndRepositoryMatch(session, repository);
        EnsureCheckRunMatches(checkRun, headSha);
        ThrowIfCancellationRequested(cancellationToken);

        GitHubPullRequestChecksRerunTargetKind target;
        Uri endpoint;
        if (actionsJobId is { } jobId)
        {
            if (jobId <= 0 || jobId != checkRun.Id)
            {
                return new GitHubPullRequestChecksRerunResult(
                    GitHubPullRequestChecksRerunTargetKind.Unsupported,
                    succeeded: false,
                    errorKind: GitHubPullRequestChecksErrorKind.InvalidResponse);
            }

            target = GitHubPullRequestChecksRerunTargetKind.ActionsJob;
            endpoint = BuildRerunActionsJobEndpoint(repository, jobId);
        }
        else if (checkRun.CheckSuiteId is { } checkSuiteId)
        {
            target = GitHubPullRequestChecksRerunTargetKind.CheckSuite;
            endpoint = BuildRerequestCheckSuiteEndpoint(
                repository,
                checkSuiteId);
        }
        else
        {
            return new GitHubPullRequestChecksRerunResult(
                GitHubPullRequestChecksRerunTargetKind.Unsupported,
                succeeded: false,
                errorKind: null);
        }

        GitHubApiTransportResponse response;
        try
        {
            response = await transport.PostAsync(
                    endpoint,
                    session,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitHubApiTransportException exception)
        {
            if (exception.Kind == GitHubApiTransportErrorKind.Cancelled)
            {
                throw new GitHubPullRequestChecksCancelledException();
            }

            return new GitHubPullRequestChecksRerunResult(
                target,
                succeeded: false,
                errorKind: MapTransportError(exception.Kind));
        }

        if ((int)response.StatusCode is >= 200 and < 300)
        {
            return new GitHubPullRequestChecksRerunResult(
                target,
                succeeded: true,
                errorKind: null);
        }

        return new GitHubPullRequestChecksRerunResult(
            target,
            succeeded: false,
            errorKind: MapResponseError(response));
    }

    private async Task<GitHubPullRequestStatusResult> LoadStatusAsync(
        GitHubAccountSession session,
        GitHubRemoteRepositoryIdentity repository,
        string headSha,
        CancellationToken cancellationToken)
    {
        var statuses = new List<GitHubCommitStatusContext>();
        var pagesFetched = 0;
        var totalCount = 0;
        GitHubCommitStatusState? legacyCombinedState = null;
        var stateIsConsistent = true;

        for (var pageNumber = 1;
             pageNumber <= options.MaximumPages;
             pageNumber++)
        {
            ThrowIfCancellationRequested(cancellationToken);
            GitHubApiTransportResponse response;
            try
            {
                response = await transport.GetAsync(
                        BuildStatusPageEndpoint(repository, headSha, pageNumber),
                        session,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GitHubApiTransportException exception)
            {
                if (exception.Kind == GitHubApiTransportErrorKind.Cancelled)
                {
                    throw new GitHubPullRequestChecksCancelledException();
                }

                return PartialStatusResult(
                    repository,
                    headSha,
                    statuses,
                    pagesFetched,
                    totalCount,
                    errorKind: MapTransportError(exception.Kind));
            }

            if ((int)response.StatusCode is < 200 or >= 300)
            {
                return PartialStatusResult(
                    repository,
                    headSha,
                    statuses,
                    pagesFetched,
                    totalCount,
                    errorKind: MapResponseError(response));
            }

            StatusPageParseResult parsed;
            try
            {
                parsed = ParseStatusPage(response.Content, headSha);
            }
            catch (GitHubPullRequestChecksException exception)
            {
                return PartialStatusResult(
                    repository,
                    headSha,
                    statuses,
                    pagesFetched,
                    totalCount,
                    errorKind: exception.Kind);
            }

            if (pagesFetched == 0)
            {
                totalCount = parsed.TotalCount;
            }
            else if (parsed.TotalCount > totalCount)
            {
                totalCount = parsed.TotalCount;
            }

            if (legacyCombinedState is null)
            {
                legacyCombinedState = parsed.CombinedState;
            }
            else if (legacyCombinedState != parsed.CombinedState)
            {
                stateIsConsistent = false;
            }

            pagesFetched++;
            statuses.AddRange(parsed.Statuses);
            ThrowIfCancellationRequested(cancellationToken);

            if (statuses.Count >= totalCount)
            {
                return CompleteStatusResult(
                    repository,
                    headSha,
                    statuses,
                    pagesFetched,
                    totalCount,
                    stateIsConsistent ? legacyCombinedState : null);
            }

            if (parsed.SourceItemCount < PageSize)
            {
                return PartialStatusResult(
                    repository,
                    headSha,
                    statuses,
                    pagesFetched,
                    totalCount,
                    errorKind: GitHubPullRequestChecksErrorKind.InvalidResponse);
            }
        }

        return PartialStatusResult(
            repository,
            headSha,
            statuses,
            pagesFetched,
            totalCount,
            errorKind: GitHubPullRequestChecksErrorKind.PageLimitReached);
    }

    private async Task<GitHubPullRequestCheckRunsResult> LoadCheckRunsAsync(
        GitHubAccountSession session,
        GitHubRemoteRepositoryIdentity repository,
        string headSha,
        CancellationToken cancellationToken)
    {
        var checkRuns = new List<GitHubCheckRun>();
        var pagesFetched = 0;
        var totalCount = 0;

        for (var pageNumber = 1;
             pageNumber <= options.MaximumPages;
             pageNumber++)
        {
            ThrowIfCancellationRequested(cancellationToken);
            GitHubApiTransportResponse response;
            try
            {
                response = await transport.GetAsync(
                        BuildCheckRunsPageEndpoint(repository, headSha, pageNumber),
                        session,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GitHubApiTransportException exception)
            {
                if (exception.Kind == GitHubApiTransportErrorKind.Cancelled)
                {
                    throw new GitHubPullRequestChecksCancelledException();
                }

                return PartialCheckRunsResult(
                    repository,
                    headSha,
                    checkRuns,
                    pagesFetched,
                    totalCount,
                    MapTransportError(exception.Kind));
            }

            if ((int)response.StatusCode is < 200 or >= 300)
            {
                return PartialCheckRunsResult(
                    repository,
                    headSha,
                    checkRuns,
                    pagesFetched,
                    totalCount,
                    MapResponseError(response));
            }

            CheckRunsPageParseResult parsed;
            try
            {
                parsed = ParseCheckRunsPage(response.Content, headSha);
            }
            catch (GitHubPullRequestChecksException exception)
            {
                return PartialCheckRunsResult(
                    repository,
                    headSha,
                    checkRuns,
                    pagesFetched,
                    totalCount,
                    exception.Kind);
            }

            if (pagesFetched == 0)
            {
                totalCount = parsed.TotalCount;
            }
            else if (parsed.TotalCount > totalCount)
            {
                totalCount = parsed.TotalCount;
            }

            pagesFetched++;
            checkRuns.AddRange(parsed.CheckRuns);
            ThrowIfCancellationRequested(cancellationToken);

            if (checkRuns.Count >= totalCount)
            {
                return CompleteCheckRunsResult(
                    repository,
                    headSha,
                    checkRuns,
                    pagesFetched,
                    totalCount);
            }

            if (parsed.SourceItemCount < PageSize)
            {
                return PartialCheckRunsResult(
                    repository,
                    headSha,
                    checkRuns,
                    pagesFetched,
                    totalCount,
                    GitHubPullRequestChecksErrorKind.InvalidResponse);
            }
        }

        return PartialCheckRunsResult(
            repository,
            headSha,
            checkRuns,
            pagesFetched,
            totalCount,
            GitHubPullRequestChecksErrorKind.PageLimitReached);
    }

    private StatusPageParseResult ParseStatusPage(
        string content,
        string expectedHeadSha)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 24 });
            var root = RequireObject(document.RootElement);
            var responseSha = RequiredFullSha(root, "sha");
            if (!string.Equals(
                    responseSha,
                    expectedHeadSha,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidResponse();
            }

            var statusesValue = RequiredProperty(root, "statuses");
            if (statusesValue.ValueKind != JsonValueKind.Array ||
                statusesValue.GetArrayLength() > PageSize)
            {
                throw InvalidResponse();
            }

            var statuses = new List<GitHubCommitStatusContext>();
            foreach (var element in statusesValue.EnumerateArray())
            {
                statuses.Add(ParseStatus(element));
            }

            return new StatusPageParseResult(
                ParseStatusState(RequiredText(root, "state", 32)),
                RequiredNonNegativeCount(root, "total_count"),
                statuses,
                statusesValue.GetArrayLength());
        }
        catch (GitHubPullRequestChecksException)
        {
            throw;
        }
        catch
        {
            throw InvalidResponse();
        }
    }

    private GitHubCommitStatusContext ParseStatus(JsonElement root)
    {
        var targetUrl = OptionalExternalUrl(root, "target_url");
        return new GitHubCommitStatusContext(
            options.ApiOrigin,
            RequiredPositiveInteger(root, "id"),
            ParseStatusState(RequiredText(root, "state", 32)),
            RequiredText(root, "context", MaximumContextLength),
            OptionalDisplayText(root, "description", MaximumDescriptionLength),
            targetUrl,
            OptionalDateTimeOffset(root, "created_at"),
            OptionalDateTimeOffset(root, "updated_at"));
    }

    private CheckRunsPageParseResult ParseCheckRunsPage(
        string content,
        string expectedHeadSha)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 24 });
            var root = RequireObject(document.RootElement);
            var checkRunsValue = RequiredProperty(root, "check_runs");
            if (checkRunsValue.ValueKind != JsonValueKind.Array ||
                checkRunsValue.GetArrayLength() > PageSize)
            {
                throw InvalidResponse();
            }

            var checkRuns = new List<GitHubCheckRun>();
            foreach (var element in checkRunsValue.EnumerateArray())
            {
                checkRuns.Add(ParseCheckRun(element, expectedHeadSha));
            }

            return new CheckRunsPageParseResult(
                RequiredNonNegativeCount(root, "total_count"),
                checkRuns,
                checkRunsValue.GetArrayLength());
        }
        catch (GitHubPullRequestChecksException)
        {
            throw;
        }
        catch
        {
            throw InvalidResponse();
        }
    }

    private WorkflowRunParseResult? ParseWorkflowRun(
        string content,
        string expectedHeadSha,
        long expectedCheckSuiteId)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 24 });
            var root = RequireObject(document.RootElement);
            RequiredNonNegativeCount(root, "total_count");
            var workflowRuns = RequiredProperty(root, "workflow_runs");
            if (workflowRuns.ValueKind != JsonValueKind.Array ||
                workflowRuns.GetArrayLength() > PageSize)
            {
                throw InvalidResponse();
            }

            if (workflowRuns.GetArrayLength() == 0)
            {
                return null;
            }

            var workflowRun = RequireObject(workflowRuns[0]);
            if (RequiredPositiveInteger(workflowRun, "check_suite_id") !=
                expectedCheckSuiteId)
            {
                throw InvalidResponse();
            }

            var headSha = RequiredFullSha(workflowRun, "head_sha");
            if (!string.Equals(
                    headSha,
                    expectedHeadSha,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidResponse();
            }

            return new WorkflowRunParseResult(
                RequiredPositiveInteger(workflowRun, "id"),
                headSha);
        }
        catch (GitHubPullRequestChecksException)
        {
            throw;
        }
        catch
        {
            throw InvalidResponse();
        }
    }

    private WorkflowJobsPageParseResult ParseWorkflowJobs(
        string content,
        string expectedHeadSha)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 32 });
            var root = RequireObject(document.RootElement);
            var totalCount = RequiredNonNegativeCount(root, "total_count");
            var jobsValue = RequiredProperty(root, "jobs");
            if (jobsValue.ValueKind != JsonValueKind.Array ||
                jobsValue.GetArrayLength() > PageSize)
            {
                throw InvalidResponse();
            }

            var jobs = new List<WorkflowJobParseResult>();
            foreach (var jobElement in jobsValue.EnumerateArray())
            {
                var job = RequireObject(jobElement);
                var jobHeadSha = RequiredFullSha(job, "head_sha");
                if (!string.Equals(
                        jobHeadSha,
                        expectedHeadSha,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw InvalidResponse();
                }

                var stepsValue = RequiredProperty(job, "steps");
                if (stepsValue.ValueKind != JsonValueKind.Array ||
                    stepsValue.GetArrayLength() > MaximumJobSteps)
                {
                    throw InvalidResponse();
                }

                var steps = new List<GitHubCheckRunJobStep>();
                foreach (var stepElement in stepsValue.EnumerateArray())
                {
                    steps.Add(ParseJobStep(stepElement));
                }

                jobs.Add(
                    new WorkflowJobParseResult(
                        RequiredPositiveInteger(job, "id"),
                        steps));
            }

            return new WorkflowJobsPageParseResult(
                totalCount,
                jobs.AsReadOnly(),
                jobsValue.GetArrayLength());
        }
        catch (GitHubPullRequestChecksException)
        {
            throw;
        }
        catch
        {
            throw InvalidResponse();
        }
    }

    private GitHubCheckRunJobStep ParseJobStep(JsonElement root)
    {
        var statusText = RequiredText(root, "status", MaximumUnknownValueLength);
        var (status, unknownStatus) = ParseCheckRunStatus(statusText);
        var conclusionValue = OptionalText(
            root,
            "conclusion",
            MaximumUnknownValueLength);
        var (conclusion, unknownConclusion) = ParseConclusion(conclusionValue);
        var stepNumber = RequiredPositiveInteger(root, "number");
        if (stepNumber > int.MaxValue)
        {
            throw InvalidResponse();
        }

        return new GitHubCheckRunJobStep(
            RequiredText(root, "name", MaximumNameLength),
            (int)stepNumber,
            status,
            unknownStatus,
            conclusion,
            unknownConclusion,
            OptionalDateTimeOffset(root, "started_at"),
            OptionalDateTimeOffset(root, "completed_at"));
    }

    private GitHubCheckRun ParseCheckRun(
        JsonElement root,
        string expectedHeadSha)
    {
        var headSha = RequiredFullSha(root, "head_sha");
        if (!string.Equals(
                headSha,
                expectedHeadSha,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResponse();
        }

        var statusText = RequiredText(root, "status", MaximumUnknownValueLength);
        var (status, unknownStatus) = ParseCheckRunStatus(statusText);
        var conclusionValue = OptionalText(
            root,
            "conclusion",
            MaximumUnknownValueLength);
        var (conclusion, unknownConclusion) = ParseConclusion(conclusionValue);
        var description = ParseOutputSummary(root);
        var appName = ParseAppName(root);
        var checkSuiteId = ParseCheckSuiteId(root);
        return new GitHubCheckRun(
            options.ApiOrigin,
            RequiredPositiveInteger(root, "id"),
            headSha,
            RequiredText(root, "name", MaximumNameLength),
            status,
            unknownStatus,
            conclusion,
            unknownConclusion,
            description,
            appName,
            checkSuiteId,
            RequiredGitHubWebUrl(root, "html_url"),
            OptionalDateTimeOffset(root, "started_at"),
            OptionalDateTimeOffset(root, "completed_at"));
    }

    private string? ParseOutputSummary(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (output.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse();
        }

        return OptionalDisplayText(
            output,
            "summary",
            MaximumDescriptionLength);
    }

    private static string? ParseAppName(JsonElement root)
    {
        if (!root.TryGetProperty("app", out var app) ||
            app.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (app.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse();
        }

        return OptionalText(app, "name", MaximumAppNameLength);
    }

    private static long? ParseCheckSuiteId(JsonElement root)
    {
        if (!root.TryGetProperty("check_suite", out var suite) ||
            suite.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (suite.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse();
        }

        return RequiredPositiveInteger(suite, "id");
    }

    private static (GitHubCheckRunStatusKind Kind, string? Unknown)
        ParseCheckRunStatus(string value) =>
        value switch
        {
            "queued" => (GitHubCheckRunStatusKind.Queued, null),
            "in_progress" => (GitHubCheckRunStatusKind.InProgress, null),
            "completed" => (GitHubCheckRunStatusKind.Completed, null),
            _ => (GitHubCheckRunStatusKind.Unknown, value),
        };

    private static (
        GitHubCheckRunConclusionKind? Kind,
        string? Unknown)
        ParseConclusion(string? value)
    {
        if (value is null)
        {
            return (null, null);
        }

        return value switch
        {
            "action_required" =>
                (GitHubCheckRunConclusionKind.ActionRequired, null),
            "cancelled" =>
                (GitHubCheckRunConclusionKind.Cancelled, null),
            "timed_out" =>
                (GitHubCheckRunConclusionKind.TimedOut, null),
            "failure" =>
                (GitHubCheckRunConclusionKind.Failure, null),
            "neutral" =>
                (GitHubCheckRunConclusionKind.Neutral, null),
            "success" =>
                (GitHubCheckRunConclusionKind.Success, null),
            "skipped" =>
                (GitHubCheckRunConclusionKind.Skipped, null),
            "stale" =>
                (GitHubCheckRunConclusionKind.Stale, null),
            _ => (GitHubCheckRunConclusionKind.Unknown, value),
        };
    }

    private Uri? OptionalExternalUrl(
        JsonElement root,
        string propertyName)
    {
        var value = OptionalText(root, propertyName, MaximumUrlLength);
        if (value is null)
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !IsSafeHttpsUri(uri))
        {
            throw InvalidResponse();
        }

        return uri;
    }

    private Uri RequiredGitHubWebUrl(
        JsonElement root,
        string propertyName)
    {
        var value = RequiredText(root, propertyName, MaximumUrlLength);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !IsSafeHttpsUri(uri) ||
            !SameOrigin(
                uri,
                GitHubApiEndpoint.WebOriginForApi(options.ApiOrigin)))
        {
            throw InvalidResponse();
        }

        return uri;
    }

    private static GitHubCommitStatusState ParseStatusState(string value) =>
        value switch
        {
            "error" => GitHubCommitStatusState.Error,
            "failure" => GitHubCommitStatusState.Failure,
            "pending" => GitHubCommitStatusState.Pending,
            "success" => GitHubCommitStatusState.Success,
            _ => throw InvalidResponse(),
        };

    private static string ValidateHeadSha(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !IsFullShaLength(value.Length) ||
            value.Any(character => !IsHex(character)))
        {
            throw new ArgumentException(
                "The pull request head must be a full hexadecimal commit SHA.",
                nameof(value));
        }

        return value.ToLowerInvariant();
    }

    private static bool IsHex(char character) =>
        character is >= '0' and <= '9' or
            >= 'a' and <= 'f' or
            >= 'A' and <= 'F';

    private static string RequiredFullSha(
        JsonElement root,
        string propertyName)
    {
        var value = RequiredText(root, propertyName, MaximumFullShaLength);
        if (!IsFullShaLength(value.Length) ||
            value.Any(character => !IsHex(character)))
        {
            throw InvalidResponse();
        }

        return value.ToLowerInvariant();
    }

    private static bool IsFullShaLength(int length) =>
        length is MinimumFullShaLength or MaximumFullShaLength;

    private static long RequiredPositiveInteger(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var result) ||
            result <= 0)
        {
            throw InvalidResponse();
        }

        return result;
    }

    private static int RequiredNonNegativeCount(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var result) ||
            result < 0 ||
            result > int.MaxValue)
        {
            throw InvalidResponse();
        }

        return (int)result;
    }

    private static string RequiredText(
        JsonElement root,
        string propertyName,
        int maximumLength)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse();
        }

        var result = value.GetString();
        if (result is null ||
            result.Length > maximumLength ||
            result.Any(character =>
                character <= '\u001F' || character == '\u007F'))
        {
            throw InvalidResponse();
        }

        result = result.Trim();
        if (result.Length == 0)
        {
            throw InvalidResponse();
        }

        return result;
    }

    private static string? OptionalText(
        JsonElement root,
        string propertyName,
        int maximumLength)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse();
        }

        var result = value.GetString();
        if (result is null ||
            result.Length > maximumLength ||
            result.Any(character =>
                character <= '\u001F' || character == '\u007F'))
        {
            throw InvalidResponse();
        }

        result = result.Trim();
        return result.Length == 0 ? null : result;
    }

    private static string? OptionalDisplayText(
        JsonElement root,
        string propertyName,
        int maximumLength)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse();
        }

        var result = value.GetString();
        if (result is null ||
            result.Length > maximumLength ||
            result.Any(character => character is '\0' or '\u007F'))
        {
            throw InvalidResponse();
        }

        return result;
    }

    private static DateTimeOffset? OptionalDateTimeOffset(
        JsonElement root,
        string propertyName)
    {
        var value = OptionalText(root, propertyName, MaximumDateLength);
        if (value is null)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var result))
        {
            throw InvalidResponse();
        }

        return result;
    }

    private static JsonElement RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse();
        }

        return value;
    }

    private static JsonElement RequiredProperty(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw InvalidResponse();
        }

        return value;
    }

    private static bool IsSafeHttpsUri(Uri value) =>
        value.IsAbsoluteUri &&
        string.Equals(
            value.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase) &&
        value.UserInfo.Length == 0 &&
        string.IsNullOrEmpty(value.Fragment);

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static Uri BuildStatusPageEndpoint(
        GitHubRemoteRepositoryIdentity repository,
        string headSha,
        int pageNumber) =>
        new(
            repository.ApiOrigin,
            $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/commits/{headSha}/status?per_page={PageSize}&page={pageNumber}");

    private static Uri BuildCheckRunsPageEndpoint(
        GitHubRemoteRepositoryIdentity repository,
        string headSha,
        int pageNumber) =>
        new(
            repository.ApiOrigin,
            $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/commits/{headSha}/check-runs?per_page={PageSize}&page={pageNumber}");

    private static Uri BuildWorkflowRunsByCheckSuiteEndpoint(
        GitHubRemoteRepositoryIdentity repository,
        long checkSuiteId) =>
        new(
            repository.ApiOrigin,
            $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/actions/runs?check_suite_id={checkSuiteId}&per_page={PageSize}&page=1");

    private static Uri BuildWorkflowRunJobsEndpoint(
        GitHubRemoteRepositoryIdentity repository,
        long workflowRunId,
        int pageNumber) =>
        new(
            repository.ApiOrigin,
            $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/actions/runs/{workflowRunId}/jobs?filter=latest&per_page={PageSize}&page={pageNumber}");

    private static Uri BuildRerequestCheckSuiteEndpoint(
        GitHubRemoteRepositoryIdentity repository,
        long checkSuiteId) =>
        new(
            repository.ApiOrigin,
            $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/check-suites/{checkSuiteId}/rerequest");

    private static Uri BuildRerunActionsJobEndpoint(
        GitHubRemoteRepositoryIdentity repository,
        long jobId) =>
        new(
            repository.ApiOrigin,
            $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/actions/jobs/{jobId}/rerun");

    private void EnsureSessionAndRepositoryMatch(
        GitHubAccountSession session,
        GitHubRemoteRepositoryIdentity repository)
    {
        if (!GitHubApiEndpoint.AreSame(session.ApiOrigin, options.ApiOrigin) ||
            !GitHubApiEndpoint.AreSame(repository.ApiOrigin, options.ApiOrigin))
        {
            throw new GitHubPullRequestChecksException(
                GitHubPullRequestChecksErrorKind.SessionMismatch);
        }
    }

    private void EnsureCheckRunMatches(
        GitHubCheckRun checkRun,
        string headSha)
    {
        if (!GitHubApiEndpoint.AreSame(checkRun.ApiOrigin, options.ApiOrigin))
        {
            throw new GitHubPullRequestChecksException(
                GitHubPullRequestChecksErrorKind.SessionMismatch);
        }

        if (!string.Equals(
                checkRun.HeadSha,
                headSha,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResponse();
        }
    }

    private static GitHubCheckRunJobStepsResult UnsupportedJobStepsResult(
        GitHubRemoteRepositoryIdentity repository,
        string headSha,
        long checkRunId) =>
        new(
            repository.ApiOrigin,
            repository.Owner,
            repository.Name,
            headSha,
            checkRunId,
            actionsJobId: null,
            steps: Array.Empty<GitHubCheckRunJobStep>(),
            isSupported: false,
            isComplete: true,
            errorKind: null);

    private static GitHubCheckRunJobStepsResult FailedJobStepsResult(
        GitHubRemoteRepositoryIdentity repository,
        string headSha,
        long checkRunId,
        GitHubPullRequestChecksErrorKind errorKind) =>
        new(
            repository.ApiOrigin,
            repository.Owner,
            repository.Name,
            headSha,
            checkRunId,
            actionsJobId: null,
            steps: Array.Empty<GitHubCheckRunJobStep>(),
            isSupported: false,
            isComplete: false,
            errorKind: errorKind);

    private GitHubPullRequestStatusResult CompleteStatusResult(
        GitHubRemoteRepositoryIdentity repository,
        string headSha,
        List<GitHubCommitStatusContext> statuses,
        int pagesFetched,
        int totalCount,
        GitHubCommitStatusState? combinedState) =>
        new(
            repository.ApiOrigin,
            repository.Owner,
            repository.Name,
            headSha,
            combinedState,
            totalCount,
            statuses,
            pagesFetched,
            isComplete: true,
            errorKind: null);

    private GitHubPullRequestStatusResult PartialStatusResult(
        GitHubRemoteRepositoryIdentity repository,
        string headSha,
        List<GitHubCommitStatusContext> statuses,
        int pagesFetched,
        int totalCount,
        GitHubPullRequestChecksErrorKind errorKind) =>
        new(
            repository.ApiOrigin,
            repository.Owner,
            repository.Name,
            headSha,
            legacyCombinedState: null,
            totalCount,
            statuses,
            pagesFetched,
            isComplete: false,
            errorKind);

    private static GitHubPullRequestCheckRunsResult CompleteCheckRunsResult(
        GitHubRemoteRepositoryIdentity repository,
        string headSha,
        List<GitHubCheckRun> checkRuns,
        int pagesFetched,
        int totalCount) =>
        new(
            repository.ApiOrigin,
            repository.Owner,
            repository.Name,
            headSha,
            totalCount,
            checkRuns,
            pagesFetched,
            isComplete: true,
            errorKind: null);

    private static GitHubPullRequestCheckRunsResult PartialCheckRunsResult(
        GitHubRemoteRepositoryIdentity repository,
        string headSha,
        List<GitHubCheckRun> checkRuns,
        int pagesFetched,
        int totalCount,
        GitHubPullRequestChecksErrorKind errorKind) =>
        new(
            repository.ApiOrigin,
            repository.Owner,
            repository.Name,
            headSha,
            totalCount,
            checkRuns,
            pagesFetched,
            isComplete: false,
            errorKind);

    private static GitHubPullRequestChecksErrorKind MapTransportError(
        GitHubApiTransportErrorKind kind) =>
        kind switch
        {
            GitHubApiTransportErrorKind.SessionMismatch =>
                GitHubPullRequestChecksErrorKind.SessionMismatch,
            GitHubApiTransportErrorKind.Timeout =>
                GitHubPullRequestChecksErrorKind.Timeout,
            GitHubApiTransportErrorKind.InvalidResponse =>
                GitHubPullRequestChecksErrorKind.InvalidResponse,
            GitHubApiTransportErrorKind.Cancelled =>
                GitHubPullRequestChecksErrorKind.InvalidConfiguration,
            _ => GitHubPullRequestChecksErrorKind.Network,
        };

    private static GitHubPullRequestChecksErrorKind MapResponseError(
        GitHubApiTransportResponse response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return GitHubPullRequestChecksErrorKind.NotFound;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return GitHubPullRequestChecksErrorKind.Unauthorized;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden &&
            response.HasSamlHeader)
        {
            return GitHubPullRequestChecksErrorKind.SamlRequired;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            response.IsRateLimited)
        {
            return GitHubPullRequestChecksErrorKind.RateLimited;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return GitHubPullRequestChecksErrorKind.Forbidden;
        }

        if ((int)response.StatusCode >= 500)
        {
            return GitHubPullRequestChecksErrorKind.Network;
        }

        return GitHubPullRequestChecksErrorKind.InvalidResponse;
    }

    private static bool IsUnsupportedActionsResponse(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or
            HttpStatusCode.NotFound or
            HttpStatusCode.MethodNotAllowed or
            HttpStatusCode.UnprocessableEntity;

    private static GitHubPullRequestChecksException InvalidResponse() =>
        new(GitHubPullRequestChecksErrorKind.InvalidResponse);

    private static void ThrowIfCancellationRequested(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new GitHubPullRequestChecksCancelledException();
        }
    }

    private static GitHubApiTransport CreateTransport(
        GitHubPullRequestChecksClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GitHubApiTransport(
            options.ApiOrigin,
            options.RequestTimeout,
            options.MaximumResponseBytes);
    }

    private static GitHubApiTransport CreateTransport(
        GitHubPullRequestChecksClientOptions options,
        HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GitHubApiTransport(
            options.ApiOrigin,
            options.RequestTimeout,
            options.MaximumResponseBytes,
            httpClient);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        transport.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GitHubPullRequestChecksClient));
        }
    }

    private sealed record StatusPageParseResult(
        GitHubCommitStatusState CombinedState,
        int TotalCount,
        IReadOnlyList<GitHubCommitStatusContext> Statuses,
        int SourceItemCount);

    private sealed record CheckRunsPageParseResult(
        int TotalCount,
        IReadOnlyList<GitHubCheckRun> CheckRuns,
        int SourceItemCount);

    private sealed record WorkflowRunParseResult(
        long Id,
        string HeadSha);

    private sealed record WorkflowJobParseResult(
        long Id,
        IReadOnlyList<GitHubCheckRunJobStep> Steps);

    private sealed record WorkflowJobsPageParseResult(
        int TotalCount,
        IReadOnlyList<WorkflowJobParseResult> Jobs,
        int SourceItemCount);
}
