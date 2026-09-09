using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace WinGit.Core.GitHub;

/// <summary>Options for one explicit, read-only pull request client.</summary>
public sealed class GitHubPullRequestClientOptions
{
    public const int DefaultMaximumResponseBytes = 1_048_576;
    public const int DefaultMaximumPages = 100;

    private GitHubPullRequestClientOptions(
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

    /// <summary>Uses https://api.github.com/repos/{owner}/{repo}/pulls.</summary>
    public static GitHubPullRequestClientOptions ForGitHubCom(
        TimeSpan? requestTimeout = null,
        int maximumResponseBytes = DefaultMaximumResponseBytes,
        int maximumPages = DefaultMaximumPages) =>
        Create(
            GitHubApiEndpoint.GitHubCom,
            requestTimeout,
            maximumResponseBytes,
            maximumPages);

    /// <summary>Uses an Enterprise host's /api/v3 pull request endpoint.</summary>
    public static GitHubPullRequestClientOptions ForEnterprise(
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

    private static GitHubPullRequestClientOptions Create(
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
                "The GitHub pull request page limit must be between 1 and 1000 pages.");
        }

        return new GitHubPullRequestClientOptions(
            apiOrigin,
            timeout,
            maximumResponseBytes,
            maximumPages);
    }
}

public enum GitHubPullRequestErrorKind
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
    ChangedFilesLimitReached,
}

/// <summary>Sanitized failure from the authenticated pull request boundary.</summary>
public sealed class GitHubPullRequestException : InvalidOperationException
{
    public GitHubPullRequestException(GitHubPullRequestErrorKind kind)
        : base(GetMessage(kind))
    {
        Kind = kind;
    }

    public GitHubPullRequestErrorKind Kind { get; }

    private static string GetMessage(GitHubPullRequestErrorKind kind) =>
        kind switch
        {
            GitHubPullRequestErrorKind.InvalidConfiguration =>
                "GitHub pull request access is not configured.",
            GitHubPullRequestErrorKind.SessionMismatch =>
                "The GitHub account session does not match this repository host.",
            GitHubPullRequestErrorKind.Network =>
                "GitHub pull requests could not be reached.",
            GitHubPullRequestErrorKind.Timeout =>
                "The GitHub pull request request timed out.",
            GitHubPullRequestErrorKind.Unauthorized =>
                "GitHub rejected the pull request credentials.",
            GitHubPullRequestErrorKind.Forbidden =>
                "GitHub refused access to pull requests.",
            GitHubPullRequestErrorKind.RateLimited =>
                "GitHub rate limited the pull request request.",
            GitHubPullRequestErrorKind.SamlRequired =>
                "GitHub requires organization sign-in before pull request access.",
            GitHubPullRequestErrorKind.NotFound =>
                "The GitHub repository or pull request was not found.",
            GitHubPullRequestErrorKind.InvalidResponse =>
                "GitHub returned an invalid pull request response.",
            GitHubPullRequestErrorKind.PageLimitReached =>
                "The GitHub pull request page limit was reached.",
            GitHubPullRequestErrorKind.ChangedFilesLimitReached =>
                "The GitHub pull request changed-file limit was reached.",
            _ => "GitHub pull request access failed.",
        };
}

public sealed class GitHubPullRequestCancelledException : OperationCanceledException
{
    public GitHubPullRequestCancelledException()
        : base("GitHub pull request loading was cancelled.")
    {
    }
}

/// <summary>A partial or complete read-only pull request list result.</summary>
public sealed class GitHubPullRequestListResult
{
    internal GitHubPullRequestListResult(
        IReadOnlyList<GitHubPullRequest> pullRequests,
        int pagesFetched,
        bool isComplete,
        GitHubPullRequestErrorKind? errorKind)
    {
        PullRequests = new List<GitHubPullRequest>(pullRequests).AsReadOnly();
        PagesFetched = pagesFetched;
        IsComplete = isComplete;
        ErrorKind = errorKind;
    }

    public IReadOnlyList<GitHubPullRequest> PullRequests { get; }

    public int PagesFetched { get; }

    public bool IsComplete { get; }

    /// <summary>Null for a complete result; otherwise the sanitized stop reason.</summary>
    public GitHubPullRequestErrorKind? ErrorKind { get; }

    public override string ToString() => "GitHub pull request list result.";
}

/// <summary>A partial or complete read-only changed-file list result.</summary>
public sealed class GitHubPullRequestChangedFilesResult
{
    internal GitHubPullRequestChangedFilesResult(
        IReadOnlyList<GitHubPullRequestChangedFile> files,
        int pagesFetched,
        bool isComplete,
        GitHubPullRequestErrorKind? errorKind)
    {
        Files = new List<GitHubPullRequestChangedFile>(files).AsReadOnly();
        PagesFetched = pagesFetched;
        IsComplete = isComplete;
        ErrorKind = errorKind;
    }

    public IReadOnlyList<GitHubPullRequestChangedFile> Files { get; }

    public int PagesFetched { get; }

    public bool IsComplete { get; }

    /// <summary>Null for a complete result; otherwise the sanitized stop reason.</summary>
    public GitHubPullRequestErrorKind? ErrorKind { get; }

    public override string ToString() => "GitHub pull request changed-file result.";
}

/// <summary>A partial or complete read-only review list result.</summary>
public sealed class GitHubPullRequestReviewsResult
{
    internal GitHubPullRequestReviewsResult(
        IReadOnlyList<GitHubPullRequestReview> reviews,
        int pagesFetched,
        bool isComplete,
        GitHubPullRequestErrorKind? errorKind)
    {
        Reviews = new List<GitHubPullRequestReview>(reviews).AsReadOnly();
        PagesFetched = pagesFetched;
        IsComplete = isComplete;
        ErrorKind = errorKind;
    }

    public IReadOnlyList<GitHubPullRequestReview> Reviews { get; }

    public int PagesFetched { get; }

    public bool IsComplete { get; }

    /// <summary>Null for a complete result; otherwise the sanitized stop reason.</summary>
    public GitHubPullRequestErrorKind? ErrorKind { get; }

    public override string ToString() => "GitHub pull request review result.";
}

/// <summary>
/// One file changed by a pull request. GitHub omits <see cref="Patch"/> for
/// binary and some large files; the native client also leaves it null instead
/// of truncating a patch that exceeds its per-file safety limit.
/// </summary>
public sealed class GitHubPullRequestChangedFile
{
    internal GitHubPullRequestChangedFile(
        Uri apiOrigin,
        string filename,
        string? previousFilename,
        string status,
        int additions,
        int deletions,
        string? patch,
        bool patchWasOmittedByLimit)
    {
        ApiOrigin = apiOrigin;
        Filename = filename;
        PreviousFilename = previousFilename;
        Status = status;
        Additions = additions;
        Deletions = deletions;
        Patch = patch;
        PatchWasOmittedByLimit = patchWasOmittedByLimit;
    }

    public Uri ApiOrigin { get; }

    public string Filename { get; }

    public string? PreviousFilename { get; }

    public string Status { get; }

    public int Additions { get; }

    public int Deletions { get; }

    /// <summary>Null when GitHub omitted the patch or the client safety limit was reached.</summary>
    public string? Patch { get; }

    /// <summary>True only when this client omitted an oversized patch without truncating it.</summary>
    public bool PatchWasOmittedByLimit { get; }

    public override string ToString() => "GitHub pull request changed file.";
}

/// <summary>
/// Read-only review metadata. Review body text is returned as data and is not
/// interpreted by the native client.
/// </summary>
public sealed class GitHubPullRequestReview
{
    internal GitHubPullRequestReview(
        Uri apiOrigin,
        long id,
        string? authorLogin,
        string state,
        DateTimeOffset? submittedAt,
        string? commitId,
        string? body,
        Uri htmlUrl)
    {
        ApiOrigin = apiOrigin;
        Id = id;
        AuthorLogin = authorLogin;
        State = state;
        SubmittedAt = submittedAt;
        CommitId = commitId;
        Body = body;
        HtmlUrl = htmlUrl;
    }

    public Uri ApiOrigin { get; }

    public long Id { get; }

    /// <summary>Null when GitHub reports a deleted or unavailable review user.</summary>
    public string? AuthorLogin { get; }

    public string State { get; }

    /// <summary>Null for pending reviews that have not been submitted.</summary>
    public DateTimeOffset? SubmittedAt { get; }

    public string? CommitId { get; }

    public string? Body { get; }

    public Uri HtmlUrl { get; }

    public override string ToString() => "GitHub pull request review.";
}

public enum GitHubPullRequestState
{
    Open,
    Closed,
}

/// <summary>A pull request ref and the repository containing that ref.</summary>
public sealed class GitHubPullRequestRef
{
    internal GitHubPullRequestRef(
        string @ref,
        string sha,
        GitHubRepository? repository)
    {
        Ref = @ref;
        Sha = sha;
        Repository = repository;
    }

    public string Ref { get; }

    public string Sha { get; }

    /// <summary>Null when GitHub reports that the head repository was deleted.</summary>
    public GitHubRepository? Repository { get; }

    public override string ToString() => "GitHub pull request ref.";
}

/// <summary>
/// Read-only pull request metadata returned by the GitHub REST API. Merge
/// values are nullable because GitHub may omit them on list responses or while
/// calculating mergeability.
/// </summary>
public sealed class GitHubPullRequest
{
    internal GitHubPullRequest(
        Uri apiOrigin,
        int number,
        string title,
        Uri htmlUrl,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string authorLogin,
        string? body,
        GitHubPullRequestState state,
        bool draft,
        GitHubPullRequestRef head,
        GitHubPullRequestRef @base,
        bool? merged,
        DateTimeOffset? mergedAt,
        string? mergeCommitSha,
        string? squashMergeCommitSha,
        bool? mergeable,
        bool? rebaseable,
        string? mergeableState)
    {
        ApiOrigin = apiOrigin;
        Number = number;
        Title = title;
        HtmlUrl = htmlUrl;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        AuthorLogin = authorLogin;
        Body = body;
        State = state;
        Draft = draft;
        Head = head;
        Base = @base;
        Merged = merged;
        MergedAt = mergedAt;
        MergeCommitSha = mergeCommitSha;
        SquashMergeCommitSha = squashMergeCommitSha;
        Mergeable = mergeable;
        Rebaseable = rebaseable;
        MergeableState = mergeableState;
    }

    public Uri ApiOrigin { get; }

    public int Number { get; }

    public string Title { get; }

    public Uri HtmlUrl { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public string AuthorLogin { get; }

    public string? Body { get; }

    public GitHubPullRequestState State { get; }

    public bool Draft { get; }

    public GitHubPullRequestRef Head { get; }

    public GitHubPullRequestRef Base { get; }

    public bool? Merged { get; }

    public DateTimeOffset? MergedAt { get; }

    public string? MergeCommitSha { get; }

    public string? SquashMergeCommitSha { get; }

    public bool? Mergeable { get; }

    public bool? Rebaseable { get; }

    public string? MergeableState { get; }

    public override string ToString() => "GitHub pull request.";
}

/// <summary>
/// Lists and reads pull requests for one validated repository. Requests are
/// repository- and account-origin bound, and no pull request write operation is
/// exposed here.
/// </summary>
public sealed class GitHubPullRequestClient : IDisposable
{
    private const int PageSize = 100;
    private const int MaximumChangedFiles = 3_000;
    private const int MaximumTitleLength = 8_192;
    private const int MaximumBodyLength = 512 * 1024;
    private const int MaximumFilenameLength = 4_096;
    private const int MaximumFileStatusLength = 32;
    private const int MaximumReviewStateLength = 64;
    private const int MaximumPatchLength = 512 * 1024;
    private const int MaximumAuthorLength = 256;
    private const int MaximumRefLength = 1_024;
    private const int MaximumShaLength = 128;
    private const int MaximumMergeableStateLength = 128;
    private const int MaximumDateLength = 128;
    private const int MaximumUrlLength = 16_384;
    private const int MaximumNameLength = 256;
    private const int MaximumOwnerLoginLength = 256;
    private const int MaximumBranchLength = 1_024;
    private const int MaximumSshUsernameLength = 256;

    private readonly GitHubPullRequestClientOptions options;
    private readonly GitHubApiTransport transport;
    private bool disposed;

    public GitHubPullRequestClient(GitHubPullRequestClientOptions options)
        : this(options, CreateTransport(options))
    {
    }

    internal GitHubPullRequestClient(
        GitHubPullRequestClientOptions options,
        HttpClient httpClient)
        : this(options, CreateTransport(options, httpClient))
    {
    }

    private GitHubPullRequestClient(
        GitHubPullRequestClientOptions options,
        GitHubApiTransport transport)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>
    /// Loads open pull requests in API order. Non-cancel request failures return
    /// the pages collected so far; caller cancellation throws after preserving
    /// already-delivered page callbacks. Pagination is constructed locally and
    /// never follows a server-provided URL.
    /// </summary>
    public async Task<GitHubPullRequestListResult> ListAsync(
        GitHubAccountSession session,
        GitHubRemoteRepositoryIdentity repository,
        Action<IReadOnlyList<GitHubPullRequest>>? onPage = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(repository);
        EnsureSessionAndRepositoryMatch(session, repository);
        ThrowIfCancellationRequested(cancellationToken);

        var pullRequests = new List<GitHubPullRequest>();
        var pagesFetched = 0;
        for (var pageNumber = 1;
             pageNumber <= options.MaximumPages;
             pageNumber++)
        {
            ThrowIfCancellationRequested(cancellationToken);
            var endpoint = BuildPageEndpoint(repository, pageNumber);
            GitHubApiTransportResponse response;
            try
            {
                response = await transport.GetAsync(
                        endpoint,
                        session,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GitHubApiTransportException exception)
            {
                if (exception.Kind == GitHubApiTransportErrorKind.Cancelled)
                {
                    throw new GitHubPullRequestCancelledException();
                }

                return PartialResult(
                    pullRequests,
                    pagesFetched,
                    MapTransportError(exception.Kind));
            }

            if ((int)response.StatusCode is < 200 or >= 300)
            {
                return PartialResult(
                    pullRequests,
                    pagesFetched,
                    MapResponseError(response));
            }

            PageParseResult parsed;
            try
            {
                parsed = ParsePage(response.Content, repository);
            }
            catch (GitHubPullRequestException exception)
            {
                return PartialResult(
                    pullRequests,
                    pagesFetched,
                    exception.Kind);
            }

            pagesFetched++;
            pullRequests.AddRange(parsed.PullRequests);
            onPage?.Invoke(parsed.PullRequests);
            ThrowIfCancellationRequested(cancellationToken);
            if (parsed.SourceItemCount < PageSize)
            {
                return CompleteResult(pullRequests, pagesFetched);
            }
        }

        return PartialResult(
            pullRequests,
            pagesFetched,
            GitHubPullRequestErrorKind.PageLimitReached);
    }

    /// <summary>Reads one pull request, or returns null for a 404 response.</summary>
    public async Task<GitHubPullRequest?> ReadAsync(
        GitHubAccountSession session,
        GitHubRemoteRepositoryIdentity repository,
        int number,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(repository);
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(number),
                "A pull request number must be positive.");
        }

        EnsureSessionAndRepositoryMatch(session, repository);
        ThrowIfCancellationRequested(cancellationToken);

        GitHubApiTransportResponse response;
        try
        {
            response = await transport.GetAsync(
                    BuildDetailEndpoint(repository, number),
                    session,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitHubApiTransportException exception)
        {
            if (exception.Kind == GitHubApiTransportErrorKind.Cancelled)
            {
                throw new GitHubPullRequestCancelledException();
            }

            throw new GitHubPullRequestException(
                MapTransportError(exception.Kind));
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if ((int)response.StatusCode is < 200 or >= 300)
        {
            throw new GitHubPullRequestException(MapResponseError(response));
        }

        GitHubPullRequest pullRequest;
        try
        {
            pullRequest = ParsePullRequest(response.Content, repository);
        }
        catch (GitHubPullRequestException)
        {
            throw;
        }
        catch
        {
            throw InvalidResponse();
        }

        ThrowIfCancellationRequested(cancellationToken);
        return pullRequest;
    }

    /// <summary>
    /// Loads the files changed by one pull request. Non-cancel request
    /// failures return the pages collected so far; pagination is constructed
    /// locally and never follows a server-provided URL.
    /// </summary>
    public async Task<GitHubPullRequestChangedFilesResult> ListChangedFilesAsync(
        GitHubAccountSession session,
        GitHubRemoteRepositoryIdentity repository,
        int pullRequestNumber,
        Action<IReadOnlyList<GitHubPullRequestChangedFile>>? onPage = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(repository);
        ValidatePullRequestNumber(pullRequestNumber);
        EnsureSessionAndRepositoryMatch(session, repository);
        ThrowIfCancellationRequested(cancellationToken);

        var files = new List<GitHubPullRequestChangedFile>();
        var pagesFetched = 0;
        for (var pageNumber = 1;
             pageNumber <= options.MaximumPages;
             pageNumber++)
        {
            ThrowIfCancellationRequested(cancellationToken);
            GitHubApiTransportResponse response;
            try
            {
                response = await transport.GetAsync(
                        BuildChangedFilesPageEndpoint(
                            repository,
                            pullRequestNumber,
                            pageNumber),
                        session,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GitHubApiTransportException exception)
            {
                if (exception.Kind == GitHubApiTransportErrorKind.Cancelled)
                {
                    throw new GitHubPullRequestCancelledException();
                }

                return PartialChangedFilesResult(
                    files,
                    pagesFetched,
                    MapTransportError(exception.Kind));
            }

            if ((int)response.StatusCode is < 200 or >= 300)
            {
                return PartialChangedFilesResult(
                    files,
                    pagesFetched,
                    MapResponseError(response));
            }

            ChangedFilesPageParseResult parsed;
            try
            {
                parsed = ParseChangedFilesPage(response.Content);
            }
            catch (GitHubPullRequestException exception)
            {
                return PartialChangedFilesResult(
                    files,
                    pagesFetched,
                    exception.Kind);
            }

            pagesFetched++;
            files.AddRange(parsed.Files);
            onPage?.Invoke(parsed.Files);
            ThrowIfCancellationRequested(cancellationToken);
            if (files.Count >= MaximumChangedFiles)
            {
                return PartialChangedFilesResult(
                    files,
                    pagesFetched,
                    GitHubPullRequestErrorKind.ChangedFilesLimitReached);
            }

            if (parsed.SourceItemCount < PageSize)
            {
                return CompleteChangedFilesResult(files, pagesFetched);
            }
        }

        return PartialChangedFilesResult(
            files,
            pagesFetched,
            GitHubPullRequestErrorKind.PageLimitReached);
    }

    /// <summary>
    /// Loads reviews for one pull request. Review bodies are returned as data
    /// and are never interpreted by this client. Non-cancel request failures
    /// return the pages collected so far; pagination is constructed locally.
    /// </summary>
    public async Task<GitHubPullRequestReviewsResult> ListReviewsAsync(
        GitHubAccountSession session,
        GitHubRemoteRepositoryIdentity repository,
        int pullRequestNumber,
        Action<IReadOnlyList<GitHubPullRequestReview>>? onPage = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(repository);
        ValidatePullRequestNumber(pullRequestNumber);
        EnsureSessionAndRepositoryMatch(session, repository);
        ThrowIfCancellationRequested(cancellationToken);

        var reviews = new List<GitHubPullRequestReview>();
        var pagesFetched = 0;
        for (var pageNumber = 1;
             pageNumber <= options.MaximumPages;
             pageNumber++)
        {
            ThrowIfCancellationRequested(cancellationToken);
            GitHubApiTransportResponse response;
            try
            {
                response = await transport.GetAsync(
                        BuildReviewsPageEndpoint(
                            repository,
                            pullRequestNumber,
                            pageNumber),
                        session,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GitHubApiTransportException exception)
            {
                if (exception.Kind == GitHubApiTransportErrorKind.Cancelled)
                {
                    throw new GitHubPullRequestCancelledException();
                }

                return PartialReviewsResult(
                    reviews,
                    pagesFetched,
                    MapTransportError(exception.Kind));
            }

            if ((int)response.StatusCode is < 200 or >= 300)
            {
                return PartialReviewsResult(
                    reviews,
                    pagesFetched,
                    MapResponseError(response));
            }

            ReviewsPageParseResult parsed;
            try
            {
                parsed = ParseReviewsPage(response.Content);
            }
            catch (GitHubPullRequestException exception)
            {
                return PartialReviewsResult(
                    reviews,
                    pagesFetched,
                    exception.Kind);
            }

            pagesFetched++;
            reviews.AddRange(parsed.Reviews);
            onPage?.Invoke(parsed.Reviews);
            ThrowIfCancellationRequested(cancellationToken);
            if (parsed.SourceItemCount < PageSize)
            {
                return CompleteReviewsResult(reviews, pagesFetched);
            }
        }

        return PartialReviewsResult(
            reviews,
            pagesFetched,
            GitHubPullRequestErrorKind.PageLimitReached);
    }

    private ChangedFilesPageParseResult ParseChangedFilesPage(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 24 });
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > PageSize)
            {
                throw InvalidResponse();
            }

            var files = new List<GitHubPullRequestChangedFile>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                files.Add(ParseChangedFile(element));
            }

            return new ChangedFilesPageParseResult(
                files,
                document.RootElement.GetArrayLength());
        }
        catch (GitHubPullRequestException)
        {
            throw;
        }
        catch
        {
            throw InvalidResponse();
        }
    }

    private GitHubPullRequestChangedFile ParseChangedFile(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse();
        }

        var patch = OptionalPatch(root, out var patchWasOmittedByLimit);
        return new GitHubPullRequestChangedFile(
            options.ApiOrigin,
            RequiredText(root, "filename", MaximumFilenameLength),
            OptionalText(root, "previous_filename", MaximumFilenameLength),
            RequiredText(root, "status", MaximumFileStatusLength),
            RequiredCount(root, "additions"),
            RequiredCount(root, "deletions"),
            patch,
            patchWasOmittedByLimit);
    }

    private ReviewsPageParseResult ParseReviewsPage(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 24 });
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > PageSize)
            {
                throw InvalidResponse();
            }

            var reviews = new List<GitHubPullRequestReview>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                reviews.Add(ParseReview(element));
            }

            return new ReviewsPageParseResult(
                reviews,
                document.RootElement.GetArrayLength());
        }
        catch (GitHubPullRequestException)
        {
            throw;
        }
        catch
        {
            throw InvalidResponse();
        }
    }

    private GitHubPullRequestReview ParseReview(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse();
        }

        var userValue = RequiredProperty(root, "user");
        var authorLogin = userValue.ValueKind == JsonValueKind.Null
            ? null
            : RequiredText(
                RequireObject(userValue),
                "login",
                MaximumAuthorLength);
        return new GitHubPullRequestReview(
            options.ApiOrigin,
            RequiredInteger(root, "id"),
            authorLogin,
            RequiredText(root, "state", MaximumReviewStateLength),
            OptionalDateTimeOffset(root, "submitted_at"),
            OptionalSha(root, "commit_id"),
            OptionalBody(root, "body"),
            RequiredWebUrl(
                root,
                "html_url",
                allowQuery: true,
                allowFragment: true));
    }

    private PageParseResult ParsePage(
        string content,
        GitHubRemoteRepositoryIdentity repository)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 24 });
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > PageSize)
            {
                throw InvalidResponse();
            }

            var pullRequests = new List<GitHubPullRequest>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                pullRequests.Add(ParsePullRequest(element, repository));
            }

            return new PageParseResult(
                pullRequests,
                document.RootElement.GetArrayLength());
        }
        catch (GitHubPullRequestException)
        {
            throw;
        }
        catch
        {
            throw InvalidResponse();
        }
    }

    private GitHubPullRequest ParsePullRequest(
        string content,
        GitHubRemoteRepositoryIdentity repository)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 24 });
            return ParsePullRequest(document.RootElement, repository);
        }
        catch (GitHubPullRequestException)
        {
            throw;
        }
        catch
        {
            throw InvalidResponse();
        }
    }

    private GitHubPullRequest ParsePullRequest(
        JsonElement root,
        GitHubRemoteRepositoryIdentity repository)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse();
        }

        var number = RequiredInteger(root, "number");
        if (number > int.MaxValue)
        {
            throw InvalidResponse();
        }

        var title = RequiredText(root, "title", MaximumTitleLength);
        var htmlUrl = RequiredWebUrl(root, "html_url", allowQuery: true);
        var createdAt = RequiredDateTimeOffset(root, "created_at");
        var updatedAt = RequiredDateTimeOffset(root, "updated_at");
        var author = RequireObject(RequiredProperty(root, "user"));
        var authorLogin = RequiredText(author, "login", MaximumAuthorLength);
        var body = OptionalBody(root, "body");
        var state = ParseState(RequiredText(root, "state", 32));
        var draft = OptionalBoolean(root, "draft") ?? false;
        var head = ParseRef(
            RequireObject(RequiredProperty(root, "head")),
            allowNullRepository: true);
        var @base = ParseRef(
            RequireObject(RequiredProperty(root, "base")),
            allowNullRepository: false);
        if (@base.Repository is null ||
            !string.Equals(
                @base.Repository.OwnerLogin,
                repository.Owner,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                @base.Repository.Name,
                repository.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResponse();
        }

        return new GitHubPullRequest(
            options.ApiOrigin,
            (int)number,
            title,
            htmlUrl,
            createdAt,
            updatedAt,
            authorLogin,
            body,
            state,
            draft,
            head,
            @base,
            OptionalBoolean(root, "merged"),
            OptionalDateTimeOffset(root, "merged_at"),
            OptionalSha(root, "merge_commit_sha"),
            OptionalSha(root, "squash_merge_commit_sha"),
            OptionalBoolean(root, "mergeable"),
            OptionalBoolean(root, "rebaseable"),
            OptionalText(root, "mergeable_state", MaximumMergeableStateLength));
    }

    private GitHubPullRequestRef ParseRef(
        JsonElement root,
        bool allowNullRepository)
    {
        var @ref = RequiredText(root, "ref", MaximumRefLength);
        var sha = RequiredSha(root, "sha");
        var repositoryValue = RequiredProperty(root, "repo");
        if (repositoryValue.ValueKind == JsonValueKind.Null)
        {
            if (!allowNullRepository)
            {
                throw InvalidResponse();
            }

            return new GitHubPullRequestRef(@ref, sha, repository: null);
        }

        var repository = ParseRepository(RequireObject(repositoryValue));
        return new GitHubPullRequestRef(@ref, sha, repository);
    }

    private GitHubRepository ParseRepository(JsonElement root)
    {
        var owner = RequireObject(RequiredProperty(root, "owner"));
        var ownerLogin = RequiredText(owner, "login", MaximumOwnerLoginLength);
        var id = RequiredInteger(root, "id");
        var name = RequiredText(root, "name", MaximumNameLength);
        var isPrivate = RequiredBoolean(root, "private");
        var isFork = RequiredBoolean(root, "fork");
        var isArchived = RequiredBoolean(root, "archived");
        var defaultBranch = OptionalText(
            root,
            "default_branch",
            MaximumBranchLength);
        var pushedAt = OptionalDateTimeOffset(root, "pushed_at");
        var htmlUrl = RequiredWebUrl(root, "html_url", allowQuery: true);
        var httpsCloneUrl = RequiredWebUrl(root, "clone_url", allowQuery: false);
        var sshCloneUrl = RequiredSshCloneUrl(root, ownerLogin, name);
        return new GitHubRepository(
            options.ApiOrigin,
            id,
            name,
            ownerLogin,
            isPrivate,
            isFork,
            isArchived,
            defaultBranch,
            pushedAt,
            htmlUrl,
            httpsCloneUrl,
            sshCloneUrl);
    }

    private Uri RequiredWebUrl(
        JsonElement root,
        string propertyName,
        bool allowQuery,
        bool allowFragment = false)
    {
        var value = RequiredText(root, propertyName, MaximumUrlLength);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !IsSafeHttpsUri(uri, allowFragment) ||
            (!allowQuery && !string.IsNullOrEmpty(uri.Query)) ||
            !SameOrigin(uri, GitHubApiEndpoint.WebOriginForApi(options.ApiOrigin)))
        {
            throw InvalidResponse();
        }

        return uri;
    }

    private static string RequiredSshCloneUrl(
        JsonElement root,
        string expectedOwner,
        string expectedName)
    {
        var value = RequiredText(root, "ssh_url", MaximumUrlLength);
        if (value.Any(character =>
                character <= '\u001F' ||
                character == '\u007F' ||
                char.IsWhiteSpace(character)))
        {
            throw InvalidResponse();
        }

        var atIndex = value.IndexOf('@');
        if (atIndex <= 0 || atIndex == value.Length - 1)
        {
            throw InvalidResponse();
        }

        var hostStart = atIndex + 1;
        var colonIndex = value[hostStart] == '['
            ? value.IndexOf("]:", hostStart, StringComparison.Ordinal) + 1
            : value.IndexOf(':', hostStart);
        if (colonIndex <= hostStart || colonIndex == value.Length - 1)
        {
            throw InvalidResponse();
        }

        var username = value[..atIndex];
        var host = value[hostStart..colonIndex];
        var path = value[(colonIndex + 1)..];
        if (!IsSafeSshUsername(username) ||
            !IsSafeSshHost(host) ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            path.EndsWith("/", StringComparison.Ordinal) ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.Contains('?', StringComparison.Ordinal) ||
            path.Contains('#', StringComparison.Ordinal) ||
            path.Contains(':', StringComparison.Ordinal))
        {
            throw InvalidResponse();
        }

        var segments = path.Split('/');
        if (segments.Length != 2 ||
            !string.Equals(segments[0], expectedOwner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                NormalizeRepositoryName(segments[1]),
                expectedName,
                StringComparison.OrdinalIgnoreCase) ||
            segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.Any(character =>
                    character <= '\u001F' || character == '\u007F')))
        {
            throw InvalidResponse();
        }

        return value;
    }

    private static string NormalizeRepositoryName(string value) =>
        value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;

    private static bool IsSafeSshUsername(string username) =>
        username.Length <= MaximumSshUsernameLength &&
        username[0] != '-' &&
        username.All(character =>
            char.IsLetterOrDigit(character) ||
            character is '.' or '_' or '-');

    private static bool IsSafeSshHost(string host)
    {
        if (host.Length == 0 ||
            host.Length > 253 ||
            host[0] == '-' ||
            host.Any(character =>
                character <= '\u001F' ||
                character == '\u007F' ||
                char.IsWhiteSpace(character) ||
                character is '@' or '/' or '\\'))
        {
            return false;
        }

        if (host[0] == '[' || host[^1] == ']')
        {
            return host.Length > 2 &&
                host[0] == '[' &&
                host[^1] == ']' &&
                IPAddress.TryParse(host[1..^1], out var address) &&
                address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        return !host.Contains(':', StringComparison.Ordinal) &&
            Uri.CheckHostName(host) is UriHostNameType.Dns or
                UriHostNameType.IPv4;
    }

    private static GitHubPullRequestState ParseState(string value) =>
        value switch
        {
            "open" => GitHubPullRequestState.Open,
            "closed" => GitHubPullRequestState.Closed,
            _ => throw InvalidResponse(),
        };

    private static long RequiredInteger(JsonElement root, string propertyName)
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

    private static int RequiredCount(JsonElement root, string propertyName)
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

    private static bool RequiredBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw InvalidResponse();
        }

        return value.GetBoolean();
    }

    private static bool? OptionalBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw InvalidResponse();
        }

        return value.GetBoolean();
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

    private static string? OptionalBody(JsonElement root, string propertyName)
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
            result.Length > MaximumBodyLength ||
            result.Any(character => character is '\0' or '\u007F'))
        {
            throw InvalidResponse();
        }

        return result;
    }

    private static string? OptionalPatch(
        JsonElement root,
        out bool patchWasOmittedByLimit)
    {
        patchWasOmittedByLimit = false;
        if (!root.TryGetProperty("patch", out var value) ||
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
            result.Any(character => character is '\0' or '\u007F'))
        {
            throw InvalidResponse();
        }

        if (result.Length > MaximumPatchLength)
        {
            patchWasOmittedByLimit = true;
            return null;
        }

        return result;
    }

    private static string RequiredSha(JsonElement root, string propertyName)
    {
        var value = RequiredText(root, propertyName, MaximumShaLength);
        if (value.Length is < 4 or > MaximumShaLength ||
            value.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F'))))
        {
            throw InvalidResponse();
        }

        return value;
    }

    private static string? OptionalSha(JsonElement root, string propertyName)
    {
        var value = OptionalText(root, propertyName, MaximumShaLength);
        if (value is null)
        {
            return null;
        }

        if (value.Length is < 4 or > MaximumShaLength ||
            value.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F'))))
        {
            throw InvalidResponse();
        }

        return value;
    }

    private static DateTimeOffset RequiredDateTimeOffset(
        JsonElement root,
        string propertyName)
    {
        var value = RequiredText(root, propertyName, MaximumDateLength);
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

    private static JsonElement RequiredProperty(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw InvalidResponse();
        }

        return value;
    }

    private static bool IsSafeHttpsUri(Uri value, bool allowFragment = false) =>
        value.IsAbsoluteUri &&
        string.Equals(
            value.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase) &&
        value.UserInfo.Length == 0 &&
        (allowFragment || string.IsNullOrEmpty(value.Fragment));

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static Uri BuildPageEndpoint(
        GitHubRemoteRepositoryIdentity repository,
        int pageNumber) =>
        new(
            repository.ApiOrigin,
            $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/pulls?state=open&per_page={PageSize}&page={pageNumber}");

    private static Uri BuildDetailEndpoint(
        GitHubRemoteRepositoryIdentity repository,
        int number) =>
        new(
            repository.ApiOrigin,
            $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/pulls/{number}");

    private static Uri BuildChangedFilesPageEndpoint(
        GitHubRemoteRepositoryIdentity repository,
        int pullRequestNumber,
        int pageNumber) =>
        new(
            repository.ApiOrigin,
            $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/pulls/{pullRequestNumber}/files?per_page={PageSize}&page={pageNumber}");

    private static Uri BuildReviewsPageEndpoint(
        GitHubRemoteRepositoryIdentity repository,
        int pullRequestNumber,
        int pageNumber) =>
        new(
            repository.ApiOrigin,
            $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/pulls/{pullRequestNumber}/reviews?per_page={PageSize}&page={pageNumber}");

    private static void ValidatePullRequestNumber(int number)
    {
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(number),
                "A pull request number must be positive.");
        }
    }

    private void EnsureSessionAndRepositoryMatch(
        GitHubAccountSession session,
        GitHubRemoteRepositoryIdentity repository)
    {
        if (!GitHubApiEndpoint.AreSame(session.ApiOrigin, options.ApiOrigin) ||
            !GitHubApiEndpoint.AreSame(repository.ApiOrigin, options.ApiOrigin))
        {
            throw new GitHubPullRequestException(
                GitHubPullRequestErrorKind.SessionMismatch);
        }
    }

    private static GitHubPullRequestListResult CompleteResult(
        List<GitHubPullRequest> pullRequests,
        int pagesFetched) =>
        new(pullRequests, pagesFetched, isComplete: true, errorKind: null);

    private static GitHubPullRequestListResult PartialResult(
        List<GitHubPullRequest> pullRequests,
        int pagesFetched,
        GitHubPullRequestErrorKind errorKind) =>
        new(pullRequests, pagesFetched, isComplete: false, errorKind);

    private static GitHubPullRequestChangedFilesResult CompleteChangedFilesResult(
        List<GitHubPullRequestChangedFile> files,
        int pagesFetched) =>
        new(files, pagesFetched, isComplete: true, errorKind: null);

    private static GitHubPullRequestChangedFilesResult PartialChangedFilesResult(
        List<GitHubPullRequestChangedFile> files,
        int pagesFetched,
        GitHubPullRequestErrorKind errorKind) =>
        new(files, pagesFetched, isComplete: false, errorKind);

    private static GitHubPullRequestReviewsResult CompleteReviewsResult(
        List<GitHubPullRequestReview> reviews,
        int pagesFetched) =>
        new(reviews, pagesFetched, isComplete: true, errorKind: null);

    private static GitHubPullRequestReviewsResult PartialReviewsResult(
        List<GitHubPullRequestReview> reviews,
        int pagesFetched,
        GitHubPullRequestErrorKind errorKind) =>
        new(reviews, pagesFetched, isComplete: false, errorKind);

    private static GitHubPullRequestErrorKind MapTransportError(
        GitHubApiTransportErrorKind kind) =>
        kind switch
        {
            GitHubApiTransportErrorKind.SessionMismatch =>
                GitHubPullRequestErrorKind.SessionMismatch,
            GitHubApiTransportErrorKind.Timeout =>
                GitHubPullRequestErrorKind.Timeout,
            GitHubApiTransportErrorKind.InvalidResponse =>
                GitHubPullRequestErrorKind.InvalidResponse,
            GitHubApiTransportErrorKind.Cancelled =>
                GitHubPullRequestErrorKind.InvalidConfiguration,
            _ => GitHubPullRequestErrorKind.Network,
        };

    private static GitHubPullRequestErrorKind MapResponseError(
        GitHubApiTransportResponse response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return GitHubPullRequestErrorKind.NotFound;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return GitHubPullRequestErrorKind.Unauthorized;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden &&
            response.HasSamlHeader)
        {
            return GitHubPullRequestErrorKind.SamlRequired;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            response.IsRateLimited)
        {
            return GitHubPullRequestErrorKind.RateLimited;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return GitHubPullRequestErrorKind.Forbidden;
        }

        if ((int)response.StatusCode >= 500)
        {
            return GitHubPullRequestErrorKind.Network;
        }

        return GitHubPullRequestErrorKind.InvalidResponse;
    }

    private static GitHubPullRequestException InvalidResponse() =>
        new(GitHubPullRequestErrorKind.InvalidResponse);

    private static void ThrowIfCancellationRequested(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new GitHubPullRequestCancelledException();
        }
    }

    private static GitHubApiTransport CreateTransport(
        GitHubPullRequestClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GitHubApiTransport(
            options.ApiOrigin,
            options.RequestTimeout,
            options.MaximumResponseBytes);
    }

    private static GitHubApiTransport CreateTransport(
        GitHubPullRequestClientOptions options,
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
            throw new ObjectDisposedException(nameof(GitHubPullRequestClient));
        }
    }

    private sealed record PageParseResult(
        IReadOnlyList<GitHubPullRequest> PullRequests,
        int SourceItemCount);

    private sealed record ChangedFilesPageParseResult(
        IReadOnlyList<GitHubPullRequestChangedFile> Files,
        int SourceItemCount);

    private sealed record ReviewsPageParseResult(
        IReadOnlyList<GitHubPullRequestReview> Reviews,
        int SourceItemCount);
}
