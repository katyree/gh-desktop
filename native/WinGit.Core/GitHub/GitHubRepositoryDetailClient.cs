using System.Globalization;
using System.Net;
using System.Text.Json;

namespace WinGit.Core.GitHub;

public enum GitHubRemoteProtocol
{
    Https,
    Ssh,
}

/// <summary>
/// A validated owner/name identity extracted from a GitHub HTTPS or SSH
/// remote. The original remote text and any user information are discarded.
/// </summary>
public sealed class GitHubRemoteRepositoryIdentity
{
    private const int MaximumRemoteLength = 16_384;
    private const int MaximumComponentLength = 256;

    private GitHubRemoteRepositoryIdentity(
        Uri apiOrigin,
        string hostname,
        string owner,
        string name,
        GitHubRemoteProtocol protocol)
    {
        ApiOrigin = apiOrigin;
        Hostname = hostname;
        Owner = owner;
        Name = name;
        Protocol = protocol;
    }

    public Uri ApiOrigin { get; }

    public string Hostname { get; }

    public string Owner { get; }

    public string Name { get; }

    public GitHubRemoteProtocol Protocol { get; }

    /// <summary>Parses a supported HTTPS or SSH owner/name remote.</summary>
    public static bool TryParse(
        string remoteUrl,
        out GitHubRemoteRepositoryIdentity? identity)
    {
        identity = null;
        if (string.IsNullOrWhiteSpace(remoteUrl) ||
            remoteUrl.Length > MaximumRemoteLength ||
            ContainsUnsafeRemoteCharacter(remoteUrl))
        {
            return false;
        }

        if (TryParseHttps(remoteUrl, out identity) ||
            TryParseSshUri(remoteUrl, out identity) ||
            TryParseScpRemote(remoteUrl, out identity) ||
            TryParseGitRemote(remoteUrl, out identity))
        {
            return true;
        }

        identity = null;
        return false;
    }

    public override string ToString() => "GitHub remote repository identity.";

    private static bool TryParseHttps(
        string remoteUrl,
        out GitHubRemoteRepositoryIdentity? identity)
    {
        identity = null;
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            (uri.UserInfo.Length > 0 && !IsSafeUserInfo(uri.UserInfo)) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !IsSafeHost(uri.Host))
        {
            return false;
        }

        if (!TryParseRepositoryPath(uri.AbsolutePath, out var owner, out var name) ||
            !TryCreateApiOrigin(uri.Host, uri.Port, out var apiOrigin))
        {
            return false;
        }

        identity = new GitHubRemoteRepositoryIdentity(
            apiOrigin,
            uri.Host,
            owner,
            name,
            GitHubRemoteProtocol.Https);
        return true;
    }

    private static bool TryParseSshUri(
        string remoteUrl,
        out GitHubRemoteRepositoryIdentity? identity)
    {
        identity = null;
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length == 0 ||
            !IsSafeUserInfo(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !IsSafeHost(uri.Host) ||
            !TryParseRepositoryPath(uri.AbsolutePath, out var owner, out var name) ||
            !TryCreateApiOrigin(uri.Host, httpsPort: 443, out var apiOrigin))
        {
            return false;
        }

        identity = new GitHubRemoteRepositoryIdentity(
            apiOrigin,
            uri.Host,
            owner,
            name,
            GitHubRemoteProtocol.Ssh);
        return true;
    }

    private static bool TryParseScpRemote(
        string remoteUrl,
        out GitHubRemoteRepositoryIdentity? identity)
    {
        identity = null;
        if (remoteUrl.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var atIndex = remoteUrl.IndexOf('@');
        if (atIndex <= 0 ||
            atIndex == remoteUrl.Length - 1 ||
            !IsSafeUserInfo(remoteUrl[..atIndex]))
        {
            return false;
        }

        var hostStart = atIndex + 1;
        var colonIndex = remoteUrl[hostStart] == '['
            ? remoteUrl.IndexOf("]:", hostStart, StringComparison.Ordinal) + 1
            : remoteUrl.IndexOf(':', hostStart);
        if (colonIndex <= hostStart ||
            colonIndex >= remoteUrl.Length - 1)
        {
            return false;
        }

        var host = remoteUrl[hostStart..colonIndex];
        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            if (!host.EndsWith("]", StringComparison.Ordinal))
            {
                return false;
            }

            host = host[1..^1];
        }

        if (!IsSafeHost(host) ||
            !TryParseRepositoryPath(remoteUrl[(colonIndex + 1)..], out var owner, out var name) ||
            !TryCreateApiOrigin(host, httpsPort: 443, out var apiOrigin))
        {
            return false;
        }

        identity = new GitHubRemoteRepositoryIdentity(
            apiOrigin,
            host,
            owner,
            name,
            GitHubRemoteProtocol.Ssh);
        return true;
    }

    private static bool TryParseGitRemote(
        string remoteUrl,
        out GitHubRemoteRepositoryIdentity? identity)
    {
        identity = null;
        if (!remoteUrl.StartsWith("git:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remote = remoteUrl[4..];
        var separator = remote.IndexOf('/');
        if (separator <= 0 ||
            !IsSafeHost(remote[..separator]) ||
            !TryParseRepositoryPath(remote[(separator + 1)..], out var owner, out var name) ||
            !TryCreateApiOrigin(remote[..separator], httpsPort: 443, out var apiOrigin))
        {
            return false;
        }

        identity = new GitHubRemoteRepositoryIdentity(
            apiOrigin,
            remote[..separator],
            owner,
            name,
            GitHubRemoteProtocol.Ssh);
        return true;
    }

    private static bool TryParseRepositoryPath(
        string path,
        out string owner,
        out string name)
    {
        owner = string.Empty;
        name = string.Empty;
        var trimmedPath = path.Trim('/');
        var parts = trimmedPath.Split('/');
        if (parts.Length != 2 ||
            !IsSafeRepositoryComponent(parts[0]) ||
            !TryNormalizeRepositoryName(parts[1], out name))
        {
            return false;
        }

        owner = parts[0];
        return true;
    }

    private static bool TryNormalizeRepositoryName(
        string value,
        out string name)
    {
        name = value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
        return IsSafeRepositoryComponent(name);
    }

    private static bool TryCreateApiOrigin(
        string host,
        int httpsPort,
        out Uri apiOrigin)
    {
        apiOrigin = GitHubApiEndpoint.GitHubCom;
        if (!IsSafeHost(host) || httpsPort is < 1 or > 65_535)
        {
            return false;
        }

        if (string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (httpsPort != 443)
            {
                return false;
            }

            apiOrigin = GitHubApiEndpoint.GitHubCom;
            return true;
        }

        try
        {
            var webOrigin = new UriBuilder(Uri.UriSchemeHttps, host, httpsPort).Uri;
            apiOrigin = GitHubApiEndpoint.ForEnterprise(webOrigin);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSafeRepositoryComponent(string value) =>
        value.Length is > 0 and <= MaximumComponentLength &&
        value is not "." and not ".." &&
        value.All(character =>
            char.IsLetterOrDigit(character) ||
            character is '.' or '_' or '-');

    private static bool IsSafeUserInfo(string value) =>
        value.Length is > 0 and <= MaximumComponentLength &&
        !value.Contains(':', StringComparison.Ordinal) &&
        value.All(character =>
            char.IsLetterOrDigit(character) ||
            character is '.' or '_' or '-');

    private static bool IsSafeHost(string host)
    {
        if (host.Length == 0 ||
            host.Length > 253 ||
            host[0] == '-' ||
            host.Any(character =>
                character <= '\u001F' ||
                character == '\u007F' ||
                char.IsWhiteSpace(character) ||
                character is '@' or '/' or '\\' or ':' or '?'))
        {
            return false;
        }

        return Uri.CheckHostName(host) is
            UriHostNameType.Dns or
            UriHostNameType.IPv4 or
            UriHostNameType.IPv6;
    }

    private static bool ContainsUnsafeRemoteCharacter(string value) =>
        value.Any(character =>
            character <= '\u001F' ||
            character == '\u007F' ||
            char.IsWhiteSpace(character));
}

/// <summary>Options for one explicit, read-only repository detail request.</summary>
public sealed class GitHubRepositoryDetailClientOptions
{
    public const int DefaultMaximumResponseBytes = 256 * 1024;

    private GitHubRepositoryDetailClientOptions(
        Uri apiOrigin,
        TimeSpan requestTimeout,
        int maximumResponseBytes)
    {
        ApiOrigin = apiOrigin;
        RequestTimeout = requestTimeout;
        MaximumResponseBytes = maximumResponseBytes;
    }

    /// <summary>Uses https://api.github.com/repos/{owner}/{name}.</summary>
    public static GitHubRepositoryDetailClientOptions ForGitHubCom(
        TimeSpan? requestTimeout = null,
        int maximumResponseBytes = DefaultMaximumResponseBytes) =>
        Create(
            GitHubApiEndpoint.GitHubCom,
            requestTimeout,
            maximumResponseBytes);

    /// <summary>Uses an Enterprise host's /api/v3 repository endpoint.</summary>
    public static GitHubRepositoryDetailClientOptions ForEnterprise(
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

    private static GitHubRepositoryDetailClientOptions Create(
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

        return new GitHubRepositoryDetailClientOptions(
            apiOrigin,
            timeout,
            maximumResponseBytes);
    }
}

public enum GitHubRepositoryDetailErrorKind
{
    InvalidConfiguration,
    InvalidRemote,
    SessionMismatch,
    Network,
    Timeout,
    Unauthorized,
    Forbidden,
    RateLimited,
    SamlRequired,
    InvalidResponse,
}

/// <summary>Sanitized failure from the authenticated repository detail boundary.</summary>
public sealed class GitHubRepositoryDetailException : InvalidOperationException
{
    public GitHubRepositoryDetailException(GitHubRepositoryDetailErrorKind kind)
        : base(GetMessage(kind))
    {
        Kind = kind;
    }

    public GitHubRepositoryDetailErrorKind Kind { get; }

    private static string GetMessage(GitHubRepositoryDetailErrorKind kind) =>
        kind switch
        {
            GitHubRepositoryDetailErrorKind.InvalidConfiguration =>
                "GitHub repository access is not configured.",
            GitHubRepositoryDetailErrorKind.InvalidRemote =>
                "The Git remote is not a supported GitHub repository URL.",
            GitHubRepositoryDetailErrorKind.SessionMismatch =>
                "The GitHub account session does not match this repository host.",
            GitHubRepositoryDetailErrorKind.Network =>
                "GitHub repository information could not be reached.",
            GitHubRepositoryDetailErrorKind.Timeout =>
                "GitHub repository information request timed out.",
            GitHubRepositoryDetailErrorKind.Unauthorized =>
                "GitHub rejected the repository credentials.",
            GitHubRepositoryDetailErrorKind.Forbidden =>
                "GitHub refused access to repository information.",
            GitHubRepositoryDetailErrorKind.RateLimited =>
                "GitHub rate limited the repository request.",
            GitHubRepositoryDetailErrorKind.SamlRequired =>
                "GitHub requires organization sign-in before repository access.",
            GitHubRepositoryDetailErrorKind.InvalidResponse =>
                "GitHub returned an invalid repository response.",
            _ => "GitHub repository access failed.",
        };
}

public sealed class GitHubRepositoryDetailCancelledException
    : OperationCanceledException
{
    public GitHubRepositoryDetailCancelledException()
        : base("GitHub repository information was cancelled.")
    {
    }
}

/// <summary>High-level permissions returned for an explicitly requested repository.</summary>
public sealed class GitHubRepositoryPermissions
{
    internal GitHubRepositoryPermissions(bool admin, bool push, bool pull)
    {
        Admin = admin;
        Push = push;
        Pull = pull;
    }

    public bool Admin { get; }

    public bool Push { get; }

    public bool Pull { get; }

    public override string ToString() => "GitHub repository permissions.";
}

/// <summary>
/// Read-only repository metadata returned for one host-bound remote. Parent
/// metadata is represented by the existing immutable catalog record.
/// </summary>
public sealed class GitHubRepositoryDetail
{
    internal GitHubRepositoryDetail(
        Uri apiOrigin,
        GitHubRepository repository,
        GitHubRepository? parent,
        GitHubRepositoryPermissions? permissions)
    {
        ApiOrigin = apiOrigin;
        Repository = repository;
        Parent = parent;
        Permissions = permissions;
    }

    public Uri ApiOrigin { get; }

    public GitHubRepository Repository { get; }

    public GitHubRepository? Parent { get; }

    public GitHubRepositoryPermissions? Permissions { get; }

    public override string ToString() => "GitHub repository detail.";
}

/// <summary>
/// Reads one authenticated repository detail record after validating the
/// supplied remote and account origins. It performs no writes or cloning.
/// </summary>
public sealed class GitHubRepositoryDetailClient : IDisposable
{
    private const int MaximumNameLength = 256;
    private const int MaximumOwnerLoginLength = 256;
    private const int MaximumBranchLength = 1_024;
    private const int MaximumDateLength = 128;
    private const int MaximumUrlLength = 16_384;

    private readonly GitHubRepositoryDetailClientOptions options;
    private readonly GitHubApiTransport transport;
    private bool disposed;

    public GitHubRepositoryDetailClient(
        GitHubRepositoryDetailClientOptions options)
        : this(options, CreateTransport(options))
    {
    }

    internal GitHubRepositoryDetailClient(
        GitHubRepositoryDetailClientOptions options,
        HttpClient httpClient)
        : this(options, CreateTransport(options, httpClient))
    {
    }

    private GitHubRepositoryDetailClient(
        GitHubRepositoryDetailClientOptions options,
        GitHubApiTransport transport)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>Reads detail for a validated remote identity, or null for a 404.</summary>
    public async Task<GitHubRepositoryDetail?> ReadAsync(
        GitHubAccountSession session,
        GitHubRemoteRepositoryIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(identity);
        EnsureSessionAndRemoteMatch(session, identity);
        ThrowIfCancellationRequested(cancellationToken);

        var endpoint = new Uri(
            options.ApiOrigin,
            $"repos/{Uri.EscapeDataString(identity.Owner)}/{Uri.EscapeDataString(identity.Name)}");
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

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if ((int)response.StatusCode is < 200 or >= 300)
        {
            throw MapResponseError(response);
        }

        var detail = ParseDetail(response.Content, identity);
        ThrowIfCancellationRequested(cancellationToken);
        return detail;
    }

    /// <summary>Parses and reads one HTTPS/SSH remote, or null for a 404.</summary>
    public Task<GitHubRepositoryDetail?> ReadRemoteAsync(
        GitHubAccountSession session,
        string remoteUrl,
        CancellationToken cancellationToken = default)
    {
        if (!GitHubRemoteRepositoryIdentity.TryParse(remoteUrl, out var identity) ||
            identity is null)
        {
            throw new GitHubRepositoryDetailException(
                GitHubRepositoryDetailErrorKind.InvalidRemote);
        }

        return ReadAsync(session, identity, cancellationToken);
    }

    private void EnsureSessionAndRemoteMatch(
        GitHubAccountSession session,
        GitHubRemoteRepositoryIdentity identity)
    {
        if (!GitHubApiEndpoint.AreSame(session.ApiOrigin, options.ApiOrigin) ||
            !GitHubApiEndpoint.AreSame(identity.ApiOrigin, options.ApiOrigin))
        {
            throw new GitHubRepositoryDetailException(
                GitHubRepositoryDetailErrorKind.SessionMismatch);
        }
    }

    private GitHubRepositoryDetail ParseDetail(
        string content,
        GitHubRemoteRepositoryIdentity identity)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 20 });
            var root = RequireObject(document.RootElement);
            var repository = ParseRepository(root, identity.Owner, identity.Name);
            var parent = ParseParent(root);
            var permissions = ParsePermissions(root);
            return new GitHubRepositoryDetail(
                options.ApiOrigin,
                repository,
                parent,
                permissions);
        }
        catch (GitHubRepositoryDetailException)
        {
            throw;
        }
        catch
        {
            throw InvalidResponse();
        }
    }

    private GitHubRepository ParseRepository(
        JsonElement root,
        string expectedOwner,
        string expectedName)
    {
        var repository = ParseRepositorySummary(root);
        if (!string.Equals(repository.OwnerLogin, expectedOwner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(repository.Name, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResponse();
        }

        return repository;
    }

    private GitHubRepository ParseRepositorySummary(JsonElement root)
    {
        var owner = RequireObject(
            RequiredProperty(root, "owner"));
        var ownerLogin = RequiredText(
            owner,
            "login",
            MaximumOwnerLoginLength);
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

    private GitHubRepository? ParseParent(JsonElement root)
    {
        if (!root.TryGetProperty("parent", out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse();
        }

        return ParseRepositorySummary(value);
    }

    private static GitHubRepositoryPermissions? ParsePermissions(JsonElement root)
    {
        if (!root.TryGetProperty("permissions", out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse();
        }

        return new GitHubRepositoryPermissions(
            RequiredBoolean(value, "admin"),
            RequiredBoolean(value, "push"),
            RequiredBoolean(value, "pull"));
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

    private string RequiredSshCloneUrl(
        JsonElement root,
        string expectedOwner,
        string expectedName)
    {
        var value = RequiredText(root, "ssh_url", MaximumUrlLength);
        if (!GitHubRemoteRepositoryIdentity.TryParse(value, out var identity) ||
            identity is null ||
            identity.Protocol != GitHubRemoteProtocol.Ssh ||
            !GitHubApiEndpoint.AreSame(identity.ApiOrigin, options.ApiOrigin) ||
            !string.Equals(identity.Owner, expectedOwner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.Name, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResponse();
        }

        return value;
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
                character <= '\u001F' ||
                character == '\u007F'))
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
                character <= '\u001F' ||
                character == '\u007F'))
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

    private static Exception MapTransportError(
        GitHubApiTransportErrorKind kind) =>
        kind switch
        {
            GitHubApiTransportErrorKind.SessionMismatch =>
                new GitHubRepositoryDetailException(
                    GitHubRepositoryDetailErrorKind.SessionMismatch),
            GitHubApiTransportErrorKind.Cancelled =>
                new GitHubRepositoryDetailCancelledException(),
            GitHubApiTransportErrorKind.Timeout =>
                new GitHubRepositoryDetailException(
                    GitHubRepositoryDetailErrorKind.Timeout),
            GitHubApiTransportErrorKind.InvalidResponse =>
                new GitHubRepositoryDetailException(
                    GitHubRepositoryDetailErrorKind.InvalidResponse),
            _ => new GitHubRepositoryDetailException(
                GitHubRepositoryDetailErrorKind.Network),
        };

    private static GitHubRepositoryDetailException MapResponseError(
        GitHubApiTransportResponse response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new GitHubRepositoryDetailException(
                GitHubRepositoryDetailErrorKind.Unauthorized);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden &&
            response.HasSamlHeader)
        {
            return new GitHubRepositoryDetailException(
                GitHubRepositoryDetailErrorKind.SamlRequired);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            response.IsRateLimited)
        {
            return new GitHubRepositoryDetailException(
                GitHubRepositoryDetailErrorKind.RateLimited);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new GitHubRepositoryDetailException(
                GitHubRepositoryDetailErrorKind.Forbidden);
        }

        if ((int)response.StatusCode >= 500)
        {
            return new GitHubRepositoryDetailException(
                GitHubRepositoryDetailErrorKind.Network);
        }

        return InvalidResponse();
    }

    private static GitHubRepositoryDetailException InvalidResponse() =>
        new(GitHubRepositoryDetailErrorKind.InvalidResponse);

    private static void ThrowIfCancellationRequested(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new GitHubRepositoryDetailCancelledException();
        }
    }

    private static GitHubApiTransport CreateTransport(
        GitHubRepositoryDetailClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GitHubApiTransport(
            options.ApiOrigin,
            options.RequestTimeout,
            options.MaximumResponseBytes);
    }

    private static GitHubApiTransport CreateTransport(
        GitHubRepositoryDetailClientOptions options,
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
                nameof(GitHubRepositoryDetailClient));
        }
    }
}
