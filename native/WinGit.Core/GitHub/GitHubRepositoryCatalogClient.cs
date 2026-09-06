using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace WinGit.Core.GitHub;

/// <summary>Options for one explicit read-only authenticated repository catalog.</summary>
public sealed class GitHubRepositoryCatalogClientOptions
{
    public const int DefaultMaximumResponseBytes = 1_048_576;
    public const int DefaultMaximumPages = 100;

    private GitHubRepositoryCatalogClientOptions(
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

    /// <summary>Uses https://api.github.com/user/repos.</summary>
    public static GitHubRepositoryCatalogClientOptions ForGitHubCom(
        TimeSpan? requestTimeout = null,
        int maximumResponseBytes = DefaultMaximumResponseBytes,
        int maximumPages = DefaultMaximumPages) =>
        Create(
            GitHubApiEndpoint.GitHubCom,
            requestTimeout,
            maximumResponseBytes,
            maximumPages);

    /// <summary>Uses an Enterprise host's /api/v3/user/repos endpoint.</summary>
    public static GitHubRepositoryCatalogClientOptions ForEnterprise(
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

    private static GitHubRepositoryCatalogClientOptions Create(
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
                "The GitHub page limit must be between 1 and 1000 pages.");
        }

        return new GitHubRepositoryCatalogClientOptions(
            apiOrigin,
            timeout,
            maximumResponseBytes,
            maximumPages);
    }
}

public enum GitHubRepositoryCatalogErrorKind
{
    InvalidConfiguration,
    SessionMismatch,
    Network,
    Timeout,
    Unauthorized,
    Forbidden,
    RateLimited,
    SamlRequired,
    InvalidResponse,
    PageLimitReached,
}

/// <summary>Sanitized configuration or request failure for repository catalog access.</summary>
public sealed class GitHubRepositoryCatalogException : InvalidOperationException
{
    public GitHubRepositoryCatalogException(GitHubRepositoryCatalogErrorKind kind)
        : base(GetMessage(kind))
    {
        Kind = kind;
    }

    public GitHubRepositoryCatalogErrorKind Kind { get; }

    private static string GetMessage(GitHubRepositoryCatalogErrorKind kind) =>
        kind switch
        {
            GitHubRepositoryCatalogErrorKind.InvalidConfiguration =>
                "GitHub repository access is not configured.",
            GitHubRepositoryCatalogErrorKind.SessionMismatch =>
                "The GitHub account session belongs to a different host.",
            GitHubRepositoryCatalogErrorKind.Network =>
                "GitHub repositories could not be reached.",
            GitHubRepositoryCatalogErrorKind.Timeout =>
                "The GitHub repository request timed out.",
            GitHubRepositoryCatalogErrorKind.Unauthorized =>
                "GitHub rejected the repository credentials.",
            GitHubRepositoryCatalogErrorKind.Forbidden =>
                "GitHub refused access to repositories.",
            GitHubRepositoryCatalogErrorKind.RateLimited =>
                "GitHub rate limited the repository request.",
            GitHubRepositoryCatalogErrorKind.SamlRequired =>
                "GitHub requires organization sign-in before repository access.",
            GitHubRepositoryCatalogErrorKind.InvalidResponse =>
                "GitHub returned an invalid repository response.",
            GitHubRepositoryCatalogErrorKind.PageLimitReached =>
                "The GitHub repository page limit was reached.",
            _ => "GitHub repository access failed.",
        };
}

public sealed class GitHubRepositoryCatalogCancelledException
    : OperationCanceledException
{
    public GitHubRepositoryCatalogCancelledException()
        : base("GitHub repository loading was cancelled.")
    {
    }
}

/// <summary>A partial or complete read-only repository catalog result.</summary>
public sealed class GitHubRepositoryCatalogResult
{
    internal GitHubRepositoryCatalogResult(
        IReadOnlyList<GitHubRepository> repositories,
        int pagesFetched,
        bool isComplete,
        GitHubRepositoryCatalogErrorKind? errorKind)
    {
        Repositories = repositories;
        PagesFetched = pagesFetched;
        IsComplete = isComplete;
        ErrorKind = errorKind;
    }

    public IReadOnlyList<GitHubRepository> Repositories { get; }

    public int PagesFetched { get; }

    public bool IsComplete { get; }

    /// <summary>Null for a complete result; otherwise the sanitized stopping reason.</summary>
    public GitHubRepositoryCatalogErrorKind? ErrorKind { get; }

    public override string ToString() => "GitHub repository catalog result.";
}

/// <summary>
/// Metadata and validated clone destinations for one GitHub repository. The
/// catalog never performs a clone or sends API credentials to clone URLs.
/// </summary>
public sealed class GitHubRepository
{
    internal GitHubRepository(
        Uri apiOrigin,
        long id,
        string name,
        string ownerLogin,
        bool isPrivate,
        bool isFork,
        bool isArchived,
        string? defaultBranch,
        DateTimeOffset? pushedAt,
        Uri htmlUrl,
        Uri httpsCloneUrl,
        string sshCloneUrl)
    {
        ApiOrigin = apiOrigin;
        Id = id;
        Name = name;
        OwnerLogin = ownerLogin;
        IsPrivate = isPrivate;
        IsFork = isFork;
        IsArchived = isArchived;
        DefaultBranch = defaultBranch;
        PushedAt = pushedAt;
        HtmlUrl = htmlUrl;
        HttpsCloneUrl = httpsCloneUrl;
        SshCloneUrl = sshCloneUrl;
    }

    public Uri ApiOrigin { get; }

    public long Id { get; }

    public string Name { get; }

    public string OwnerLogin { get; }

    public bool IsPrivate { get; }

    public bool IsFork { get; }

    public bool IsArchived { get; }

    public string? DefaultBranch { get; }

    public DateTimeOffset? PushedAt { get; }

    public Uri HtmlUrl { get; }

    public Uri HttpsCloneUrl { get; }

    public string SshCloneUrl { get; }

    public override string ToString() => "GitHub repository.";
}

/// <summary>
/// Streams GET /user/repos pages using caller-owned session credentials. The
/// caller receives already-fetched pages and a partial result for non-cancel
/// request failures; pagination is constructed locally and never follows a
/// server-provided URL.
/// </summary>
public sealed class GitHubRepositoryCatalogClient : IDisposable
{
    private const int PageSize = 100;
    private const int MaximumNameLength = 256;
    private const int MaximumOwnerLoginLength = 256;
    private const int MaximumBranchLength = 1_024;
    private const int MaximumDateLength = 128;
    private const int MaximumUrlLength = 16_384;
    private const int MaximumSshUsernameLength = 256;

    private readonly GitHubRepositoryCatalogClientOptions options;
    private readonly GitHubApiTransport transport;
    private bool disposed;

    public GitHubRepositoryCatalogClient(
        GitHubRepositoryCatalogClientOptions options)
        : this(options, CreateTransport(options))
    {
    }

    internal GitHubRepositoryCatalogClient(
        GitHubRepositoryCatalogClientOptions options,
        HttpClient httpClient)
        : this(options, CreateTransport(options, httpClient))
    {
    }

    private GitHubRepositoryCatalogClient(
        GitHubRepositoryCatalogClientOptions options,
        GitHubApiTransport transport)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>
    /// Loads repository pages in API order. A callback is invoked once for
    /// every parsed page, including pages with dangling null-owner records
    /// removed. HTTP, timeout, malformed-page, and page-limit failures return
    /// the pages collected so far with IsComplete=false. Caller cancellation
    /// throws GitHubRepositoryCatalogCancelledException after preserving any
    /// already-delivered callbacks.
    /// </summary>
    public async Task<GitHubRepositoryCatalogResult> ListAsync(
        GitHubAccountSession session,
        Action<IReadOnlyList<GitHubRepository>>? onPage = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        if (!GitHubApiEndpoint.AreSame(session.ApiOrigin, options.ApiOrigin))
        {
            throw new GitHubRepositoryCatalogException(
                GitHubRepositoryCatalogErrorKind.SessionMismatch);
        }

        ThrowIfCancellationRequested(cancellationToken);
        var repositories = new List<GitHubRepository>();
        var pagesFetched = 0;
        for (var pageNumber = 1;
             pageNumber <= options.MaximumPages;
             pageNumber++)
        {
            ThrowIfCancellationRequested(cancellationToken);
            var endpoint = BuildPageEndpoint(options.ApiOrigin, pageNumber);
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
                    throw new GitHubRepositoryCatalogCancelledException();
                }

                return PartialResult(
                    repositories,
                    pagesFetched,
                    MapTransportError(exception.Kind));
            }

            if ((int)response.StatusCode is < 200 or >= 300)
            {
                return PartialResult(
                    repositories,
                    pagesFetched,
                    MapResponseError(response));
            }

            PageParseResult parsed;
            try
            {
                parsed = ParsePage(response.Content);
            }
            catch (GitHubRepositoryCatalogException exception)
            {
                return PartialResult(
                    repositories,
                    pagesFetched,
                    exception.Kind);
            }

            pagesFetched++;
            repositories.AddRange(parsed.Repositories);
            onPage?.Invoke(parsed.Repositories);
            ThrowIfCancellationRequested(cancellationToken);
            if (parsed.SourceItemCount < PageSize)
            {
                return CompleteResult(repositories, pagesFetched);
            }
        }

        return PartialResult(
            repositories,
            pagesFetched,
            GitHubRepositoryCatalogErrorKind.PageLimitReached);
    }

    private PageParseResult ParsePage(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 20 });
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > PageSize)
            {
                throw InvalidResponse();
            }

            var repositories = new List<GitHubRepository>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("owner", out var owner))
                {
                    throw InvalidResponse();
                }

                if (owner.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (owner.ValueKind != JsonValueKind.Object)
                {
                    throw InvalidResponse();
                }

                var ownerLogin = RequiredText(
                    owner,
                    "login",
                    MaximumOwnerLoginLength);
                var id = RequiredInteger(element, "id");
                var name = RequiredText(element, "name", MaximumNameLength);
                var isPrivate = RequiredBoolean(element, "private");
                var isFork = RequiredBoolean(element, "fork");
                var isArchived = RequiredBoolean(element, "archived");
                var defaultBranch = OptionalText(
                    element,
                    "default_branch",
                    MaximumBranchLength);
                var pushedAt = OptionalDateTimeOffset(element, "pushed_at");
                var htmlUrl = RequiredWebUrl(element, "html_url", allowQuery: true);
                var httpsCloneUrl = RequiredWebUrl(
                    element,
                    "clone_url",
                    allowQuery: false);
                var sshCloneUrl = RequiredSshCloneUrl(element, "ssh_url");
                repositories.Add(new GitHubRepository(
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
                    sshCloneUrl));
            }

            return new PageParseResult(repositories, document.RootElement.GetArrayLength());
        }
        catch (GitHubRepositoryCatalogException)
        {
            throw;
        }
        catch
        {
            throw InvalidResponse();
        }
    }

    private Uri RequiredWebUrl(
        JsonElement root,
        string propertyName,
        bool allowQuery)
    {
        var value = RequiredText(root, propertyName, MaximumUrlLength);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !IsSafeHttpsUri(uri) ||
            (!allowQuery && !string.IsNullOrEmpty(uri.Query)) ||
            !SameOrigin(uri, GitHubApiEndpoint.WebOriginForApi(options.ApiOrigin)))
        {
            throw InvalidResponse();
        }

        return uri;
    }

    private static string RequiredSshCloneUrl(
        JsonElement root,
        string propertyName)
    {
        var value = RequiredText(root, propertyName, MaximumUrlLength);
        if (value.Any(character =>
                character <= '\u001F' ||
                character == '\u007F' ||
                char.IsWhiteSpace(character)))
        {
            throw InvalidResponse();
        }

        var atIndex = value.IndexOf('@');
        if (atIndex <= 0)
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

    private static bool IsSafeSshUsername(string username) =>
        username.Length <= MaximumSshUsernameLength &&
        username[0] != '-' &&
        username.All(character =>
            char.IsLetterOrDigit(character) ||
            character is '.' or '_' or '-');

    private static bool IsSafeSshHost(string host)
    {
        if (host.Length == 0 ||
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

        return host.Length <= 253 &&
            !host.Contains(':', StringComparison.Ordinal) &&
            Uri.CheckHostName(host) is UriHostNameType.Dns or
                UriHostNameType.IPv4;
    }

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

    private static bool RequiredBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
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

    private static Uri BuildPageEndpoint(Uri apiOrigin, int pageNumber) =>
        new(
            apiOrigin,
            $"user/repos?visibility=all&affiliation=owner,collaborator,organization_member&per_page={PageSize}&page={pageNumber}");

    private static GitHubRepositoryCatalogResult CompleteResult(
        List<GitHubRepository> repositories,
        int pagesFetched) =>
        new(repositories.ToArray(), pagesFetched, isComplete: true, errorKind: null);

    private static GitHubRepositoryCatalogResult PartialResult(
        List<GitHubRepository> repositories,
        int pagesFetched,
        GitHubRepositoryCatalogErrorKind errorKind) =>
        new(repositories.ToArray(), pagesFetched, isComplete: false, errorKind);

    private static GitHubRepositoryCatalogErrorKind MapTransportError(
        GitHubApiTransportErrorKind kind) =>
        kind switch
        {
            GitHubApiTransportErrorKind.SessionMismatch =>
                GitHubRepositoryCatalogErrorKind.SessionMismatch,
            GitHubApiTransportErrorKind.Timeout =>
                GitHubRepositoryCatalogErrorKind.Timeout,
            GitHubApiTransportErrorKind.Cancelled =>
                GitHubRepositoryCatalogErrorKind.InvalidConfiguration,
            GitHubApiTransportErrorKind.InvalidResponse =>
                GitHubRepositoryCatalogErrorKind.InvalidResponse,
            _ => GitHubRepositoryCatalogErrorKind.Network,
        };

    private static GitHubRepositoryCatalogErrorKind MapResponseError(
        GitHubApiTransportResponse response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return GitHubRepositoryCatalogErrorKind.Unauthorized;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden &&
            response.HasSamlHeader)
        {
            return GitHubRepositoryCatalogErrorKind.SamlRequired;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            response.IsRateLimited)
        {
            return GitHubRepositoryCatalogErrorKind.RateLimited;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return GitHubRepositoryCatalogErrorKind.Forbidden;
        }

        if ((int)response.StatusCode >= 500)
        {
            return GitHubRepositoryCatalogErrorKind.Network;
        }

        return GitHubRepositoryCatalogErrorKind.InvalidResponse;
    }

    private static GitHubRepositoryCatalogException InvalidResponse() =>
        new(GitHubRepositoryCatalogErrorKind.InvalidResponse);

    private static void ThrowIfCancellationRequested(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new GitHubRepositoryCatalogCancelledException();
        }
    }

    private static GitHubApiTransport CreateTransport(
        GitHubRepositoryCatalogClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GitHubApiTransport(
            options.ApiOrigin,
            options.RequestTimeout,
            options.MaximumResponseBytes);
    }

    private static GitHubApiTransport CreateTransport(
        GitHubRepositoryCatalogClientOptions options,
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
                nameof(GitHubRepositoryCatalogClient));
        }
    }

    private sealed record PageParseResult(
        IReadOnlyList<GitHubRepository> Repositories,
        int SourceItemCount);
}
