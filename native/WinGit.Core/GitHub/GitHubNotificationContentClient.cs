using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinGit.Core.GitHub;

public sealed class GitHubNotificationContentClientOptions
{
    public const int DefaultMaximumResponseBytes = 1_048_576;

    private GitHubNotificationContentClientOptions(
        Uri apiOrigin,
        TimeSpan requestTimeout,
        int maximumResponseBytes)
    {
        ApiOrigin = apiOrigin;
        RequestTimeout = requestTimeout;
        MaximumResponseBytes = maximumResponseBytes;
    }

    public static GitHubNotificationContentClientOptions ForGitHubCom(
        TimeSpan? requestTimeout = null,
        int maximumResponseBytes = DefaultMaximumResponseBytes) =>
        Create(
            GitHubApiEndpoint.GitHubCom,
            requestTimeout,
            maximumResponseBytes);

    public static GitHubNotificationContentClientOptions ForEnterprise(
        Uri enterpriseHost,
        TimeSpan? requestTimeout = null,
        int maximumResponseBytes = DefaultMaximumResponseBytes) =>
        Create(
            GitHubApiEndpoint.ForEnterprise(enterpriseHost),
            requestTimeout,
            maximumResponseBytes);

    public Uri ApiOrigin { get; }

    public TimeSpan RequestTimeout { get; }

    public int MaximumResponseBytes { get; }

    private static GitHubNotificationContentClientOptions Create(
        Uri apiOrigin,
        TimeSpan? requestTimeout,
        int maximumResponseBytes)
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

        return new GitHubNotificationContentClientOptions(
            apiOrigin,
            timeout,
            maximumResponseBytes);
    }
}

public sealed record GitHubNotificationContent(
    string Title,
    string Body,
    Uri Target,
    string DeduplicationKey)
{
    public IReadOnlyList<long> CheckRunIds { get; init; } = [];

    public IReadOnlyList<long> AllCheckRunIds { get; init; } = [];
}

public sealed class GitHubNotificationContentClient : IDisposable
{
    private const int MaximumActorLength = 256;
    private const int MaximumBodyLength = 512 * 1024;
    private const int MaximumEventIdLength = 32;
    private const int MaximumEmailLength = 512;
    private const int MaximumAccountEmails = 1_000;
    private const int MaximumReviewStateLength = 64;
    private const int NotificationExcerptLength = 50;
    private static readonly Regex FullShaPattern = new(
        "^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly GitHubNotificationContentClientOptions options;
    private readonly GitHubApiTransport transport;
    private readonly GitHubPullRequestChecksClient checksClient;
    private readonly HttpClient? ownedHttpClient;
    private bool disposed;

    public GitHubNotificationContentClient(
        GitHubNotificationContentClientOptions options)
        : this(options, CreateHttpClient(), ownsHttpClient: true)
    {
    }

    internal GitHubNotificationContentClient(
        GitHubNotificationContentClientOptions options,
        HttpClient httpClient)
        : this(options, httpClient, ownsHttpClient: false)
    {
    }

    private GitHubNotificationContentClient(
        GitHubNotificationContentClientOptions options,
        HttpClient httpClient,
        bool ownsHttpClient)
        : this(
            options,
            new GitHubApiTransport(
                options.ApiOrigin,
                options.RequestTimeout,
                options.MaximumResponseBytes,
                httpClient),
            new GitHubPullRequestChecksClient(
                CreateChecksClientOptions(options),
                httpClient),
            ownsHttpClient ? httpClient : null)
    {
    }

    private GitHubNotificationContentClient(
        GitHubNotificationContentClientOptions options,
        GitHubApiTransport transport,
        GitHubPullRequestChecksClient checksClient,
        HttpClient? ownedHttpClient)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.checksClient = checksClient ?? throw new ArgumentNullException(nameof(checksClient));
        this.ownedHttpClient = ownedHttpClient;
    }

    public async Task<GitHubNotificationContent?> ReadAsync(
        GitHubAccountSession session,
        GitHubRepository repository,
        GitHubPullRequest knownPullRequest,
        GitHubNotificationEvent notificationEvent,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(knownPullRequest);
        ArgumentNullException.ThrowIfNull(notificationEvent);
        ThrowIfCancellationRequested(cancellationToken);

        if (!IsCurrentContext(session, repository, knownPullRequest) ||
            !IsMatchingEvent(repository, knownPullRequest, notificationEvent))
        {
            return null;
        }

        return notificationEvent.Kind switch
        {
            GitHubNotificationEventKind.PullRequestComment =>
                await ReadCommentAsync(
                        session,
                        repository,
                        knownPullRequest,
                        notificationEvent,
                        cancellationToken)
                    .ConfigureAwait(false),
            GitHubNotificationEventKind.PullRequestReview =>
                await ReadReviewAsync(
                        session,
                        repository,
                        knownPullRequest,
                        notificationEvent,
                        cancellationToken)
                    .ConfigureAwait(false),
            GitHubNotificationEventKind.ChecksFailed =>
                await ReadChecksFailedAsync(
                        session,
                        repository,
                        knownPullRequest,
                        notificationEvent,
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => null,
        };
    }

    public async Task<IReadOnlyList<string>> ReadAccountEmailsAsync(
        GitHubAccountSession session,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        ThrowIfCancellationRequested(cancellationToken);
        if (!GitHubApiEndpoint.AreSame(session.ApiOrigin, options.ApiOrigin))
        {
            return Array.Empty<string>();
        }

        GitHubApiTransportResponse response;
        try
        {
            response = await transport.GetAsync(
                    new Uri(options.ApiOrigin, "user/emails"),
                    session,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitHubApiTransportException exception)
        {
            ThrowIfTransportCancelled(exception, cancellationToken);
            return Array.Empty<string>();
        }

        if ((int)response.StatusCode is < 200 or >= 300)
        {
            return Array.Empty<string>();
        }

        try
        {
            return ParseAccountEmails(response.Content);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task<GitHubNotificationContent?> ReadCommentAsync(
        GitHubAccountSession session,
        GitHubRepository repository,
        GitHubPullRequest knownPullRequest,
        GitHubNotificationEvent notificationEvent,
        CancellationToken cancellationToken)
    {
        if (!TryParsePositiveId(notificationEvent.EventId, out var commentId) ||
            !TryGetCommentKind(notificationEvent.CommentKind, out var commentKind))
        {
            return null;
        }

        var endpoint = BuildCommentEndpoint(
            repository,
            commentKind,
            commentId);
        var response = await GetOrNullAsync(
                endpoint,
                session,
                cancellationToken)
            .ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(
                response.Content,
                new JsonDocumentOptions { MaxDepth = 20 });
            var root = RequireObject(document.RootElement);
            if (RequiredPositiveInteger(root, "id") != commentId)
            {
                return null;
            }

            var actor = ParseActor(root);
            var body = RequiredBody(root);
            var expectedTarget = BuildPullRequestApiEndpoint(
                repository,
                knownPullRequest.Number,
                commentKind == GitHubNotificationCommentKind.IssueComment);
            var targetProperty = commentKind == GitHubNotificationCommentKind.IssueComment
                ? "issue_url"
                : "pull_request_url";
            if (!MatchesExpectedApiTarget(root, targetProperty, expectedTarget))
            {
                return null;
            }

            return new GitHubNotificationContent(
                $"@{actor} commented on your pull request",
                FormatPullRequestBody(knownPullRequest, body),
                BuildPullRequestWebTarget(repository, knownPullRequest.Number),
                BuildCommentDeduplicationKey(
                    repository,
                    knownPullRequest.Number,
                    commentId,
                    commentKind));
        }
        catch
        {
            return null;
        }
    }

    private async Task<GitHubNotificationContent?> ReadReviewAsync(
        GitHubAccountSession session,
        GitHubRepository repository,
        GitHubPullRequest knownPullRequest,
        GitHubNotificationEvent notificationEvent,
        CancellationToken cancellationToken)
    {
        if (!TryParsePositiveId(notificationEvent.EventId, out var reviewId))
        {
            return null;
        }

        var response = await GetOrNullAsync(
                BuildReviewEndpoint(repository, knownPullRequest.Number, reviewId),
                session,
                cancellationToken)
            .ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(
                response.Content,
                new JsonDocumentOptions { MaxDepth = 20 });
            var root = RequireObject(document.RootElement);
            if (RequiredPositiveInteger(root, "id") != reviewId)
            {
                return null;
            }

            var actor = ParseActor(root);
            var state = RequiredText(root, "state", MaximumReviewStateLength)
                .Trim()
                .ToUpperInvariant();
            var body = OptionalBody(root);
            if (state is not ("APPROVED" or "CHANGES_REQUESTED" or "COMMENTED") ||
                !MatchesExpectedApiTarget(
                    root,
                    "pull_request_url",
                    BuildPullRequestApiEndpoint(
                        repository,
                        knownPullRequest.Number,
                        issueTarget: false)))
            {
                return null;
            }

            var reviewVerb = state switch
            {
                "APPROVED" => "approved",
                "CHANGES_REQUESTED" => "requested changes on",
                _ => "reviewed",
            };
            return new GitHubNotificationContent(
                $"@{actor} {reviewVerb} your pull request",
                FormatPullRequestBody(knownPullRequest, body),
                BuildPullRequestWebTarget(repository, knownPullRequest.Number),
                BuildReviewDeduplicationKey(repository, knownPullRequest.Number, reviewId));
        }
        catch
        {
            return null;
        }
    }

    private async Task<GitHubNotificationContent?> ReadChecksFailedAsync(
        GitHubAccountSession session,
        GitHubRepository repository,
        GitHubPullRequest knownPullRequest,
        GitHubNotificationEvent notificationEvent,
        CancellationToken cancellationToken)
    {
        if (!TryParsePositiveId(notificationEvent.EventId, out var checkSuiteId) ||
            !TryValidateFullSha(notificationEvent.CommitSha, out var eventHeadSha) ||
            !string.Equals(
                eventHeadSha,
                knownPullRequest.Head.Sha,
                StringComparison.OrdinalIgnoreCase) ||
            !TryGetRepositoryIdentity(repository, out var identity))
        {
            return null;
        }

        GitHubPullRequestChecksResult checks;
        try
        {
            checks = await checksClient.LoadAsync(
                    session,
                    identity!,
                    eventHeadSha,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitHubPullRequestChecksCancelledException)
        {
            throw;
        }
        catch (GitHubPullRequestChecksException)
        {
            return null;
        }

        var status = checks.Status;
        if (!status.IsComplete ||
            !string.Equals(
                status.HeadSha,
                knownPullRequest.Head.Sha,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                status.Owner,
                repository.OwnerLogin,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                status.RepositoryName,
                repository.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var checkRuns = checks.CheckRuns;
        if (!checkRuns.IsComplete ||
            !string.Equals(
                checkRuns.HeadSha,
                knownPullRequest.Head.Sha,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                checkRuns.Owner,
                repository.OwnerLogin,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                checkRuns.RepositoryName,
                repository.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var suiteRuns = checkRuns.CheckRuns
            .Where(check => check.CheckSuiteId == checkSuiteId)
            .ToArray();
        if (suiteRuns.Length == 0)
        {
            return null;
        }

        var allFailedRuns = checkRuns.CheckRuns
            .Where(check =>
                check.Status == GitHubCheckRunStatusKind.Completed &&
                check.Conclusion == GitHubCheckRunConclusionKind.Failure)
            .ToArray();
        var failedStatuses = status.Statuses
            .Where(commitStatus =>
                commitStatus.State is
                    GitHubCommitStatusState.Error or
                    GitHubCommitStatusState.Failure)
            .ToArray();
        var failedCheckCount = allFailedRuns.Length + failedStatuses.Length;
        if (failedCheckCount == 0)
        {
            return null;
        }

        var suiteRunIds = suiteRuns
            .Select(check => check.Id)
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var allRunIds = checkRuns.CheckRuns
            .Select(check => check.Id)
            .Where(id => id > 0)
            .Concat(
                status.Statuses
                    .Select(commitStatus => commitStatus.Id)
                    .Where(id => id > 0))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var suiteRunIdText = string.Join(
            ",",
            suiteRunIds
                .Select(id => id.ToString(CultureInfo.InvariantCulture)));
        var allRunIdText = string.Join(
            ",",
            allRunIds
                .Select(id => id.ToString(CultureInfo.InvariantCulture)));
        if (suiteRunIds.Length == 0 || allRunIds.Length == 0)
        {
            return null;
        }

        var pluralChecks = failedCheckCount == 1
            ? "check was"
            : "checks were";
        return new GitHubNotificationContent(
            "Pull Request checks failed",
            $"{knownPullRequest.Title} #{knownPullRequest.Number} ({eventHeadSha[..7]})\n" +
            $"{failedCheckCount} {pluralChecks} not successful.",
            BuildPullRequestWebTarget(repository, knownPullRequest.Number),
            BuildChecksDeduplicationKey(
                repository,
                knownPullRequest.Number,
                eventHeadSha,
                checkSuiteId,
                suiteRunIdText,
                allRunIdText))
        {
            CheckRunIds = new List<long>(suiteRunIds).AsReadOnly(),
            AllCheckRunIds = new List<long>(allRunIds).AsReadOnly(),
        };
    }

    private async Task<GitHubApiTransportResponse?> GetOrNullAsync(
        Uri endpoint,
        GitHubAccountSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await transport.GetAsync(
                    endpoint,
                    session,
                    cancellationToken)
                .ConfigureAwait(false);
            return (int)response.StatusCode is >= 200 and < 300
                ? response
                : null;
        }
        catch (GitHubApiTransportException exception)
        {
            ThrowIfTransportCancelled(exception, cancellationToken);
            return null;
        }
    }

    private IReadOnlyList<string> ParseAccountEmails(string content)
    {
        using var document = JsonDocument.Parse(
            content,
            new JsonDocumentOptions { MaxDepth = 12 });
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() > MaximumAccountEmails)
        {
            return Array.Empty<string>();
        }

        var emails = new List<string>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var email = RequiredText(
                RequireObject(element),
                "email",
                MaximumEmailLength).Trim();
            if (email.Length == 0 || email.Any(char.IsWhiteSpace))
            {
                return Array.Empty<string>();
            }

            emails.Add(email);
        }

        return emails.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private bool IsCurrentContext(
        GitHubAccountSession session,
        GitHubRepository repository,
        GitHubPullRequest knownPullRequest) =>
        GitHubApiEndpoint.AreSame(session.ApiOrigin, options.ApiOrigin) &&
        GitHubApiEndpoint.AreSame(repository.ApiOrigin, options.ApiOrigin) &&
        GitHubApiEndpoint.AreSame(knownPullRequest.ApiOrigin, options.ApiOrigin) &&
        string.Equals(
            knownPullRequest.Base.Repository?.OwnerLogin,
            repository.OwnerLogin,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            knownPullRequest.Base.Repository?.Name,
            repository.Name,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsMatchingEvent(
        GitHubRepository repository,
        GitHubPullRequest knownPullRequest,
        GitHubNotificationEvent notificationEvent) =>
        notificationEvent.PullRequestNumber == knownPullRequest.Number &&
        string.Equals(
            notificationEvent.Owner,
            repository.OwnerLogin,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            notificationEvent.Repository,
            repository.Name,
            StringComparison.OrdinalIgnoreCase);

    private static bool TryGetRepositoryIdentity(
        GitHubRepository repository,
        out GitHubRemoteRepositoryIdentity? identity)
    {
        if (!GitHubRemoteRepositoryIdentity.TryParse(
                repository.HttpsCloneUrl.AbsoluteUri,
                out identity) ||
            identity is null ||
            !GitHubApiEndpoint.AreSame(identity.ApiOrigin, repository.ApiOrigin) ||
            !string.Equals(identity.Owner, repository.OwnerLogin, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.Name, repository.Name, StringComparison.OrdinalIgnoreCase))
        {
            identity = null;
            return false;
        }

        return true;
    }

    private static Uri BuildCommentEndpoint(
        GitHubRepository repository,
        GitHubNotificationCommentKind commentKind,
        long commentId) =>
        new(
            repository.ApiOrigin,
            $"repos/{Escape(repository.OwnerLogin)}/{Escape(repository.Name)}/" +
            (commentKind == GitHubNotificationCommentKind.IssueComment
                ? $"issues/comments/{commentId.ToString(CultureInfo.InvariantCulture)}"
                : $"pulls/comments/{commentId.ToString(CultureInfo.InvariantCulture)}"));

    private static Uri BuildReviewEndpoint(
        GitHubRepository repository,
        int pullRequestNumber,
        long reviewId) =>
        new(
            repository.ApiOrigin,
            $"repos/{Escape(repository.OwnerLogin)}/{Escape(repository.Name)}/pulls/" +
            $"{pullRequestNumber.ToString(CultureInfo.InvariantCulture)}/reviews/" +
            reviewId.ToString(CultureInfo.InvariantCulture));

    private static Uri BuildPullRequestApiEndpoint(
        GitHubRepository repository,
        int pullRequestNumber,
        bool issueTarget) =>
        new(
            repository.ApiOrigin,
            $"repos/{Escape(repository.OwnerLogin)}/{Escape(repository.Name)}/" +
            (issueTarget ? "issues" : "pulls") +
            $"/{pullRequestNumber.ToString(CultureInfo.InvariantCulture)}");

    private static Uri BuildPullRequestWebTarget(
        GitHubRepository repository,
        int pullRequestNumber) =>
        new(
            GitHubApiEndpoint.WebOriginForApi(repository.ApiOrigin),
            $"{Escape(repository.OwnerLogin)}/{Escape(repository.Name)}/pull/" +
            pullRequestNumber.ToString(CultureInfo.InvariantCulture));

    private static string BuildCommentDeduplicationKey(
        GitHubRepository repository,
        int pullRequestNumber,
        long commentId,
        GitHubNotificationCommentKind commentKind) =>
        $"comment:{GetCommentKindValue(commentKind)}:{repository.OwnerLogin}/{repository.Name}/{pullRequestNumber.ToString(CultureInfo.InvariantCulture)}/{commentId.ToString(CultureInfo.InvariantCulture)}";

    private static string GetCommentKindValue(
        GitHubNotificationCommentKind commentKind) =>
        commentKind == GitHubNotificationCommentKind.IssueComment
            ? "issue-comment"
            : "review-comment";

    private static string BuildReviewDeduplicationKey(
        GitHubRepository repository,
        int pullRequestNumber,
        long reviewId) =>
        $"review:{repository.OwnerLogin}/{repository.Name}/{pullRequestNumber.ToString(CultureInfo.InvariantCulture)}/{reviewId.ToString(CultureInfo.InvariantCulture)}";

    private static string BuildChecksDeduplicationKey(
        GitHubRepository repository,
        int pullRequestNumber,
        string headSha,
        long checkSuiteId,
        string suiteRunIds,
        string allRunIds) =>
        $"checks:{repository.OwnerLogin}/{repository.Name}/{pullRequestNumber.ToString(CultureInfo.InvariantCulture)}/{headSha.ToLowerInvariant()}/{checkSuiteId.ToString(CultureInfo.InvariantCulture)}/{suiteRunIds}/{allRunIds}";

    private static string FormatPullRequestBody(
        GitHubPullRequest pullRequest,
        string body) =>
        $"{pullRequest.Title} #{pullRequest.Number}\n{Truncate(body, NotificationExcerptLength)}";

    private static string Truncate(string value, int maximumLength)
    {
        var characters = new List<string>();
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value is >= 0xFE00 and <= 0xFE0F &&
                characters.Count > 0)
            {
                characters[^1] += rune.ToString();
            }
            else
            {
                characters.Add(rune.ToString());
            }
        }

        return characters.Count <= maximumLength
            ? value
            : string.Concat(characters.Take(maximumLength)) + "…";
    }

    private static bool MatchesExpectedApiTarget(
        JsonElement root,
        string propertyName,
        Uri expectedTarget)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var actualTarget))
        {
            return false;
        }

        return GitHubApiEndpoint.AreSame(actualTarget, expectedTarget) &&
            actualTarget.UserInfo.Length == 0 &&
            string.IsNullOrEmpty(actualTarget.Query) &&
            string.IsNullOrEmpty(actualTarget.Fragment);
    }

    private static string ParseActor(JsonElement root) =>
        RequiredText(
            RequireObject(RequiredProperty(root, "user")),
            "login",
            MaximumActorLength);

    private static string RequiredBody(JsonElement root)
    {
        var value = RequiredProperty(root, "body");

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() is { } body && body.Length <= MaximumBodyLength
                ? body
                : throw new InvalidDataException("GitHub returned an invalid notification body.")
            : throw new InvalidDataException("GitHub returned an invalid notification body.");
    }

    private static string OptionalBody(JsonElement root)
    {
        if (!root.TryGetProperty("body", out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() is { } body && body.Length <= MaximumBodyLength
                ? body
                : throw new InvalidDataException("GitHub returned an invalid notification body.")
            : throw new InvalidDataException("GitHub returned an invalid notification body.");
    }

    private static bool TryGetCommentKind(
        string? value,
        out GitHubNotificationCommentKind kind)
    {
        kind = value?.Trim().ToLowerInvariant() switch
        {
            "issue-comment" => GitHubNotificationCommentKind.IssueComment,
            "review-comment" => GitHubNotificationCommentKind.ReviewComment,
            _ => default,
        };
        return value?.Trim().ToLowerInvariant() is "issue-comment" or "review-comment";
    }

    private static bool TryParsePositiveId(string value, out long id)
    {
        id = 0;
        return !string.IsNullOrWhiteSpace(value) &&
            value.Length <= MaximumEventIdLength &&
            long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out id) &&
            id > 0;
    }

    private static bool TryValidateFullSha(
        string? value,
        out string sha)
    {
        sha = value ?? string.Empty;
        return FullShaPattern.IsMatch(sha);
    }

    private static JsonElement RequireObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw new InvalidDataException("GitHub returned an invalid notification record.");

    private static JsonElement RequiredProperty(
        JsonElement root,
        string propertyName) =>
        root.TryGetProperty(propertyName, out var value)
            ? value
            : throw new InvalidDataException("GitHub omitted required notification data.");

    private static long RequiredPositiveInteger(
        JsonElement root,
        string propertyName)
    {
        var value = RequiredProperty(root, propertyName);
        return value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number) &&
            number > 0
            ? number
            : throw new InvalidDataException("GitHub returned an invalid notification identifier.");
    }

    private static string RequiredText(
        JsonElement root,
        string propertyName,
        int maximumLength)
    {
        var value = RequiredProperty(root, propertyName);
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text ||
            text.Length == 0 ||
            text.Length > maximumLength ||
            text.Any(character => character <= '\u001F' || character == '\u007F'))
        {
            throw new InvalidDataException("GitHub returned invalid notification text.");
        }

        return text;
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static void ThrowIfTransportCancelled(
        GitHubApiTransportException exception,
        CancellationToken cancellationToken)
    {
        if (exception.Kind == GitHubApiTransportErrorKind.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static void ThrowIfCancellationRequested(
        CancellationToken cancellationToken) =>
        cancellationToken.ThrowIfCancellationRequested();

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(GitHubNotificationContentClient));
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        checksClient.Dispose();
        transport.Dispose();
        ownedHttpClient?.Dispose();
    }

    private static GitHubPullRequestChecksClientOptions CreateChecksClientOptions(
        GitHubNotificationContentClientOptions options) =>
        GitHubApiEndpoint.IsHost(options.ApiOrigin, "api.github.com")
            ? GitHubPullRequestChecksClientOptions.ForGitHubCom(
                options.RequestTimeout,
                options.MaximumResponseBytes)
            : GitHubPullRequestChecksClientOptions.ForEnterprise(
                GitHubApiEndpoint.WebOriginForApi(options.ApiOrigin),
                options.RequestTimeout,
                options.MaximumResponseBytes);

    private enum GitHubNotificationCommentKind
    {
        IssueComment,
        ReviewComment,
    }
}
