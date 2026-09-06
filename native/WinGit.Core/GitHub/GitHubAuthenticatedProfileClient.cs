using System.Net;
using System.Text.Json;

namespace WinGit.Core.GitHub;

/// <summary>
/// Canonical GitHub API origins used by the native account boundary.
/// </summary>
public static class GitHubApiEndpoint
{
    private const string EnterpriseApiPath = "/api/v3/";

    /// <summary>The public GitHub.com REST API origin.</summary>
    public static Uri GitHubCom { get; } = new("https://api.github.com/");

    /// <summary>
    /// Creates the REST API origin for one GitHub Enterprise host. The input
    /// may be the host root or its conventional /api/v3 endpoint.
    /// </summary>
    public static Uri ForEnterprise(Uri enterpriseHost)
    {
        ArgumentNullException.ThrowIfNull(enterpriseHost);
        ValidateHttpsUri(enterpriseHost, nameof(enterpriseHost));
        var path = enterpriseHost.AbsolutePath.TrimEnd('/');
        if (path.Length > 0 &&
            !string.Equals(path, "/api/v3", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A GitHub Enterprise host may only use the /api/v3 API path.",
                nameof(enterpriseHost));
        }

        if (IsGheCloudHost(enterpriseHost))
        {
            var apiHost = enterpriseHost.Host.StartsWith(
                "api.",
                StringComparison.OrdinalIgnoreCase)
                ? enterpriseHost.Host
                : $"api.{enterpriseHost.Host}";
            return WithHostAndPath(enterpriseHost, apiHost, "/");
        }

        return WithPath(enterpriseHost, EnterpriseApiPath);
    }

    internal static Uri FromOAuthHost(Uri oauthHost)
    {
        ArgumentNullException.ThrowIfNull(oauthHost);
        ValidateHttpsUri(oauthHost, nameof(oauthHost));
        if (IsHost(oauthHost, "github.com") ||
            IsHost(oauthHost, "api.github.com"))
        {
            return GitHubCom;
        }

        if (IsGheCloudHost(oauthHost))
        {
            var apiHost = oauthHost.Host.StartsWith(
                "api.",
                StringComparison.OrdinalIgnoreCase)
                ? oauthHost.Host
                : $"api.{oauthHost.Host}";
            return WithHostAndPath(oauthHost, apiHost, "/");
        }

        return WithPath(oauthHost, EnterpriseApiPath);
    }

    internal static Uri WebOriginForApi(Uri apiOrigin)
    {
        if (IsHost(apiOrigin, "api.github.com"))
        {
            return WithHostAndPath(apiOrigin, "github.com", "/");
        }

        var webHost = IsGheCloudHost(apiOrigin) &&
            apiOrigin.Host.StartsWith(
                "api.",
                StringComparison.OrdinalIgnoreCase)
            ? apiOrigin.Host[4..]
            : apiOrigin.Host;
        return WithHostAndPath(apiOrigin, webHost, "/");
    }

    internal static bool AreSame(Uri left, Uri right) =>
        string.Equals(
            left.AbsoluteUri,
            right.AbsoluteUri,
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsHost(Uri value, string host) =>
        string.Equals(value.Host, host, StringComparison.OrdinalIgnoreCase);

    internal static bool IsGheCloudHost(Uri value) =>
        value.Host.EndsWith(
            ".ghe.com",
            StringComparison.OrdinalIgnoreCase);

    private static void ValidateHttpsUri(Uri value, string parameterName)
    {
        if (!value.IsAbsoluteUri ||
            !string.Equals(
                value.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            value.UserInfo.Length > 0 ||
            !string.IsNullOrEmpty(value.Query) ||
            !string.IsNullOrEmpty(value.Fragment))
        {
            throw new ArgumentException(
                "The GitHub endpoint must be an HTTPS URL without credentials, query, or fragment.",
                parameterName);
        }
    }

    private static Uri WithPath(Uri value, string path) =>
        WithHostAndPath(value, value.Host, path);

    private static Uri WithHostAndPath(Uri value, string host, string path)
    {
        var builder = new UriBuilder(value)
        {
            Host = host,
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }
}

/// <summary>Options for one read-only authenticated GitHub profile client.</summary>
public sealed class GitHubAuthenticatedProfileClientOptions
{
    public const string RestApiVersion = "2022-11-28";
    public const int DefaultMaximumResponseBytes = 256 * 1024;

    private GitHubAuthenticatedProfileClientOptions(
        Uri apiOrigin,
        TimeSpan requestTimeout,
        int maximumResponseBytes)
    {
        ApiOrigin = apiOrigin;
        RequestTimeout = requestTimeout;
        MaximumResponseBytes = maximumResponseBytes;
    }

    /// <summary>Uses https://api.github.com/user.</summary>
    public static GitHubAuthenticatedProfileClientOptions ForGitHubCom(
        TimeSpan? requestTimeout = null,
        int maximumResponseBytes = DefaultMaximumResponseBytes) =>
        Create(
            GitHubApiEndpoint.GitHubCom,
            requestTimeout,
            maximumResponseBytes);

    /// <summary>Uses https://enterprise-host/api/v3/user.</summary>
    public static GitHubAuthenticatedProfileClientOptions ForEnterprise(
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

    private static GitHubAuthenticatedProfileClientOptions Create(
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

        if (maximumResponseBytes < 1 || maximumResponseBytes > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResponseBytes),
                "The GitHub response limit must be between 1 and 1048576 bytes.");
        }

        return new GitHubAuthenticatedProfileClientOptions(
            apiOrigin,
            timeout,
            maximumResponseBytes);
    }
}

/// <summary>
/// An in-memory GitHub credential bound to the API origin that issued its
/// device token. No persistence is performed by this type.
/// </summary>
public sealed class GitHubAccountSession
{
    private GitHubAccountSession(Uri apiOrigin, string accessToken)
    {
        ApiOrigin = apiOrigin;
        AccessToken = accessToken;
    }

    public Uri ApiOrigin { get; }

    internal string AccessToken { get; }

    /// <summary>
    /// Binds an OAuth device token to the corresponding GitHub API origin.
    /// </summary>
    public static GitHubAccountSession FromDeviceToken(
        GitHubDeviceAccessToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var accessToken = token.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken) ||
            accessToken.Length > 8_192 ||
            accessToken.Any(character =>
                character <= '\u001F' || character == '\u007F'))
        {
            throw new ArgumentException(
                "The GitHub access token is invalid.",
                nameof(token));
        }

        return new GitHubAccountSession(
            GitHubApiEndpoint.FromOAuthHost(token.IssuerHost),
            accessToken);
    }

    internal static GitHubAccountSession FromPersistedToken(
        Uri apiOrigin,
        string accessToken)
    {
        ArgumentNullException.ThrowIfNull(apiOrigin);
        if (string.IsNullOrWhiteSpace(accessToken) ||
            accessToken.Length > 8_192 ||
            accessToken.Any(character =>
                character <= '\u001F' || character == '\u007F'))
        {
            throw new ArgumentException(
                "The GitHub access token is invalid.",
                nameof(accessToken));
        }

        return new GitHubAccountSession(apiOrigin, accessToken);
    }

    public override string ToString() => "GitHub account session.";
}

/// <summary>
/// Profile fields returned by the authenticated GET /user endpoint.
/// </summary>
public sealed class GitHubAuthenticatedProfile
{
    internal GitHubAuthenticatedProfile(
        Uri apiOrigin,
        long id,
        string login,
        string? displayName,
        string? email,
        Uri? avatarUrl,
        Uri profileUrl)
    {
        ApiOrigin = apiOrigin;
        Id = id;
        Login = login;
        DisplayName = displayName;
        Email = email;
        AvatarUrl = avatarUrl;
        ProfileUrl = profileUrl;
    }

    public Uri ApiOrigin { get; }

    public long Id { get; }

    public string Login { get; }

    public string? DisplayName { get; }

    public string? Email { get; }

    public Uri? AvatarUrl { get; }

    public Uri ProfileUrl { get; }

    public override string ToString() => "GitHub authenticated profile.";
}

public enum GitHubAuthenticatedProfileErrorKind
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
}

/// <summary>
/// A sanitized failure from the authenticated profile boundary. Response
/// bodies, headers, URLs, and access tokens are intentionally omitted.
/// </summary>
public sealed class GitHubAuthenticatedProfileException : InvalidOperationException
{
    public GitHubAuthenticatedProfileException(
        GitHubAuthenticatedProfileErrorKind kind)
        : base(GetMessage(kind))
    {
        Kind = kind;
    }

    public GitHubAuthenticatedProfileErrorKind Kind { get; }

    private static string GetMessage(
        GitHubAuthenticatedProfileErrorKind kind) =>
        kind switch
        {
            GitHubAuthenticatedProfileErrorKind.InvalidConfiguration =>
                "GitHub account authentication is not configured.",
            GitHubAuthenticatedProfileErrorKind.SessionMismatch =>
                "The GitHub account session belongs to a different host.",
            GitHubAuthenticatedProfileErrorKind.Network =>
                "GitHub account information could not be reached.",
            GitHubAuthenticatedProfileErrorKind.Timeout =>
                "GitHub account information request timed out.",
            GitHubAuthenticatedProfileErrorKind.Unauthorized =>
                "GitHub rejected the account credentials.",
            GitHubAuthenticatedProfileErrorKind.Forbidden =>
                "GitHub refused access to account information.",
            GitHubAuthenticatedProfileErrorKind.RateLimited =>
                "GitHub rate limited the account request.",
            GitHubAuthenticatedProfileErrorKind.SamlRequired =>
                "GitHub requires organization sign-in before account access.",
            GitHubAuthenticatedProfileErrorKind.InvalidResponse =>
                "GitHub returned an invalid account response.",
            _ => "GitHub returned an invalid account response.",
        };
}

public sealed class GitHubAuthenticatedProfileCancelledException
    : OperationCanceledException
{
    public GitHubAuthenticatedProfileCancelledException()
        : base("GitHub account information was cancelled.")
    {
    }
}

/// <summary>
/// Performs one explicit, read-only GET /user request. It does not persist a
/// credential, fetch avatars, follow redirects, or start authentication.
/// </summary>
public sealed class GitHubAuthenticatedProfileClient : IDisposable
{
    private const int MaximumLoginLength = 256;
    private const int MaximumDisplayNameLength = 512;
    private const int MaximumEmailLength = 512;
    private const int MaximumUrlLength = 16_384;

    private readonly GitHubAuthenticatedProfileClientOptions options;
    private readonly GitHubApiTransport transport;
    private bool disposed;

    public GitHubAuthenticatedProfileClient(
        GitHubAuthenticatedProfileClientOptions options)
        : this(options, CreateTransport(options))
    {
    }

    internal GitHubAuthenticatedProfileClient(
        GitHubAuthenticatedProfileClientOptions options,
        HttpClient httpClient)
        : this(options, CreateTransport(options, httpClient))
    {
    }

    private GitHubAuthenticatedProfileClient(
        GitHubAuthenticatedProfileClientOptions options,
        GitHubApiTransport transport)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>Reads the profile associated with the supplied host-bound session.</summary>
    public async Task<GitHubAuthenticatedProfile> ReadUserAsync(
        GitHubAccountSession session,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        if (!GitHubApiEndpoint.AreSame(session.ApiOrigin, options.ApiOrigin))
        {
            throw new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.SessionMismatch);
        }

        ThrowIfCancellationRequested(cancellationToken);
        var endpoint = new Uri(options.ApiOrigin, "user");
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
            throw MapTransportError(exception.Kind);
        }

        if ((int)response.StatusCode is < 200 or >= 300)
        {
            throw MapError(response);
        }

        var profile = ParseProfile(response.Content);
        ThrowIfCancellationRequested(cancellationToken);
        return profile;
    }

    private GitHubAuthenticatedProfile ParseProfile(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 20 });
            var root = RequireObject(document.RootElement);
            var id = RequiredInteger(root, "id");
            var login = RequiredText(root, "login", MaximumLoginLength);
            var displayName = OptionalNullableText(
                root,
                "name",
                MaximumDisplayNameLength);
            var email = OptionalNullableText(root, "email", MaximumEmailLength);
            var avatarUrl = ParseAvatarUrl(root);
            var profileUrl = ParseProfileUrl(
                RequiredText(root, "html_url", MaximumUrlLength),
                isAvatar: false)!;
            return new GitHubAuthenticatedProfile(
                options.ApiOrigin,
                id,
                login,
                displayName,
                email,
                avatarUrl,
                profileUrl);
        }
        catch (GitHubAuthenticatedProfileException)
        {
            throw;
        }
        catch
        {
            throw new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.InvalidResponse);
        }
    }

    private Uri? ParseAvatarUrl(JsonElement root)
    {
        if (!root.TryGetProperty("avatar_url", out var value) ||
            value.ValueKind == JsonValueKind.Null ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) ||
            text.Length > MaximumUrlLength ||
            text.Any(character =>
                character <= '\u001F' || character == '\u007F'))
        {
            return null;
        }

        try
        {
            return ParseProfileUrl(text.Trim(), isAvatar: true);
        }
        catch (GitHubAuthenticatedProfileException)
        {
            return null;
        }
    }

    private Uri? ParseProfileUrl(string value, bool isAvatar)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(
                uri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length > 0 ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.InvalidResponse);
        }

        var webOrigin = GitHubApiEndpoint.WebOriginForApi(options.ApiOrigin);
        var sameWebOrigin = SameOrigin(uri, webOrigin);
        var allowedGitHubAvatarHost =
            (GitHubApiEndpoint.IsHost(options.ApiOrigin, "api.github.com") ||
             GitHubApiEndpoint.IsGheCloudHost(options.ApiOrigin)) &&
            uri.Host.EndsWith(
                ".githubusercontent.com",
                StringComparison.OrdinalIgnoreCase);
        if (!sameWebOrigin && (!isAvatar || !allowedGitHubAvatarHost))
        {
            throw new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.InvalidResponse);
        }

        return uri;
    }

    private static Exception MapTransportError(
        GitHubApiTransportErrorKind kind) =>
        kind switch
        {
            GitHubApiTransportErrorKind.SessionMismatch =>
                new GitHubAuthenticatedProfileException(
                    GitHubAuthenticatedProfileErrorKind.SessionMismatch),
            GitHubApiTransportErrorKind.Cancelled =>
                new GitHubAuthenticatedProfileCancelledException(),
            GitHubApiTransportErrorKind.Timeout =>
                new GitHubAuthenticatedProfileException(
                    GitHubAuthenticatedProfileErrorKind.Timeout),
            GitHubApiTransportErrorKind.InvalidResponse =>
                new GitHubAuthenticatedProfileException(
                    GitHubAuthenticatedProfileErrorKind.InvalidResponse),
            _ => new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.Network),
        };

    private static GitHubAuthenticatedProfileException MapError(
        GitHubApiTransportResponse response)
    {
        var statusCode = response.StatusCode;
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.Unauthorized);
        }

        if (statusCode == HttpStatusCode.Forbidden && response.HasSamlHeader)
        {
            return new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.SamlRequired);
        }

        if (statusCode == HttpStatusCode.TooManyRequests || response.IsRateLimited)
        {
            return new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.RateLimited);
        }

        if (statusCode == HttpStatusCode.Forbidden)
        {
            return new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.Forbidden);
        }

        if ((int)statusCode >= 500)
        {
            return new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.Network);
        }

        return new GitHubAuthenticatedProfileException(
            GitHubAuthenticatedProfileErrorKind.InvalidResponse);
    }

    private static JsonElement RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.InvalidResponse);
        }

        return value;
    }

    private static long RequiredInteger(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var result) ||
            result <= 0)
        {
            throw new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.InvalidResponse);
        }

        return result;
    }

    private static string RequiredText(
        JsonElement root,
        string propertyName,
        int maximumLength)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.InvalidResponse);
        }

        var result = value.GetString();
        if (result is null ||
            result.Length > maximumLength ||
            result.Any(character =>
                character <= '\u001F' || character == '\u007F'))
        {
            throw new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.InvalidResponse);
        }

        result = result.Trim();
        if (result.Length == 0)
        {
            throw new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.InvalidResponse);
        }

        return result;
    }

    private static string? OptionalNullableText(
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
            throw new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.InvalidResponse);
        }

        var result = value.GetString();
        if (result is null ||
            result.Length > maximumLength ||
            result.Any(character =>
                character <= '\u001F' || character == '\u007F'))
        {
            throw new GitHubAuthenticatedProfileException(
                GitHubAuthenticatedProfileErrorKind.InvalidResponse);
        }

        result = result.Trim();
        return result.Length == 0 ? null : result;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static void ThrowIfCancellationRequested(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new GitHubAuthenticatedProfileCancelledException();
        }
    }

    private static GitHubApiTransport CreateTransport(
        GitHubAuthenticatedProfileClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GitHubApiTransport(
            options.ApiOrigin,
            options.RequestTimeout,
            options.MaximumResponseBytes);
    }

    private static GitHubApiTransport CreateTransport(
        GitHubAuthenticatedProfileClientOptions options,
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
                nameof(GitHubAuthenticatedProfileClient));
        }
    }
}
