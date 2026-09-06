using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WinGit.Core.GitHub;

/// <summary>
/// Configuration for one explicit GitHub OAuth device-flow client.
/// </summary>
public sealed class GitHubDeviceAuthorizationClientOptions
{
    public GitHubDeviceAuthorizationClientOptions(
        string clientId,
        Uri host,
        IReadOnlyList<string>? scopes = null,
        TimeSpan? requestTimeout = null,
        int maximumResponseBytes = DefaultMaximumResponseBytes)
    {
        ClientId = ValidateClientId(clientId);
        Host = NormalizeHost(host);
        Scopes = CopyScopes(scopes);

        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (RequestTimeout <= TimeSpan.Zero ||
            RequestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The GitHub request timeout must be between one tick and ten minutes.");
        }

        if (maximumResponseBytes < 1 ||
            maximumResponseBytes > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResponseBytes),
                "The GitHub response limit must be between 1 and 1048576 bytes.");
        }

        MaximumResponseBytes = maximumResponseBytes;
    }

    public const int DefaultMaximumResponseBytes = 256 * 1024;

    public string ClientId { get; }

    /// <summary>An HTTPS origin such as https://github.com or an Enterprise host.</summary>
    public Uri Host { get; }

    /// <summary>
    /// OAuth scopes requested by the original desktop flow unless the caller
    /// supplies a narrower list: repo, user, and workflow.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; }

    public TimeSpan RequestTimeout { get; }

    public int MaximumResponseBytes { get; }

    public override string ToString() =>
        "GitHub device authorization client options.";

    private static string ValidateClientId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            ContainsControlCharacter(value))
        {
            throw new ArgumentException(
                "A non-empty public GitHub OAuth client ID is required.",
                nameof(value));
        }

        return value;
    }

    private static Uri NormalizeHost(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsAbsoluteUri ||
            !string.Equals(
                value.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            value.UserInfo.Length > 0 ||
            !string.IsNullOrEmpty(value.Query) ||
            !string.IsNullOrEmpty(value.Fragment) ||
            value.AbsolutePath is not ("" or "/"))
        {
            throw new ArgumentException(
                "The GitHub host must be an HTTPS origin without credentials or a path.",
                nameof(value));
        }

        var builder = new UriBuilder(value)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static IReadOnlyList<string> CopyScopes(
        IReadOnlyList<string>? scopes)
    {
        scopes ??= ["repo", "user", "workflow"];
        var copied = new string[scopes.Count];
        for (var index = 0; index < scopes.Count; index++)
        {
            var scope = scopes[index];
            if (string.IsNullOrWhiteSpace(scope) ||
                scope.Length > 200 ||
                ContainsControlCharacter(scope))
            {
                throw new ArgumentException(
                    "GitHub OAuth scopes must be bounded text values.",
                    nameof(scopes));
            }

            copied[index] = scope;
        }

        return Array.AsReadOnly(copied);
    }

    private static bool ContainsControlCharacter(string value) =>
        value.Any(character =>
            character <= '\u001F' || character == '\u007F');
}

/// <summary>
/// User-facing device-flow material. The device code is retained privately so
/// the caller receives only the code and URL it needs to show the user.
/// </summary>
public sealed class GitHubDeviceAuthorization
{
    internal GitHubDeviceAuthorization(
        object owner,
        string deviceCode,
        string userCode,
        Uri verificationUri,
        TimeSpan expiresIn,
        TimeSpan pollingInterval,
        DateTimeOffset expiresAt)
    {
        Owner = owner;
        DeviceCode = deviceCode;
        UserCode = userCode;
        VerificationUri = verificationUri;
        ExpiresIn = expiresIn;
        PollingInterval = pollingInterval;
        ExpiresAt = expiresAt;
    }

    public string UserCode { get; }

    public Uri VerificationUri { get; }

    public TimeSpan ExpiresIn { get; }

    public TimeSpan PollingInterval { get; }

    public DateTimeOffset ExpiresAt { get; }

    internal object Owner { get; }

    internal string DeviceCode { get; }

    public override string ToString() =>
        "GitHub device authorization.";
}

/// <summary>An OAuth token returned by an explicit device-flow poll.</summary>
public sealed class GitHubDeviceAccessToken
{
    internal GitHubDeviceAccessToken(
        string accessToken,
        string tokenType,
        string scope,
        Uri issuerHost)
    {
        AccessToken = accessToken;
        TokenType = tokenType;
        Scope = scope;
        IssuerHost = issuerHost;
    }

    public string AccessToken { get; }

    public string TokenType { get; }

    public string Scope { get; }

    /// <summary>
    /// The HTTPS origin that issued this in-memory token. The account API uses
    /// this immutable value to bind the token to its matching API origin.
    /// </summary>
    public Uri IssuerHost { get; }

    public override string ToString() =>
        "GitHub device access token.";
}

/// <summary>Typed device-flow outcomes safe for native UI.</summary>
public enum GitHubDeviceFlowErrorKind
{
    InvalidConfiguration,
    Network,
    Timeout,
    InvalidResponse,
    DeviceFlowDisabled,
    ClientRejected,
    Expired,
    AccessDenied,
}

/// <summary>
/// A sanitized GitHub device-flow failure. Server descriptions, URLs, device
/// codes, and tokens are intentionally absent from the message.
/// </summary>
public sealed class GitHubDeviceAuthorizationException : InvalidOperationException
{
    public GitHubDeviceAuthorizationException(GitHubDeviceFlowErrorKind kind)
        : base(GetMessage(kind))
    {
        Kind = kind;
    }

    public GitHubDeviceFlowErrorKind Kind { get; }

    private static string GetMessage(GitHubDeviceFlowErrorKind kind) =>
        kind switch
        {
            GitHubDeviceFlowErrorKind.InvalidConfiguration =>
                "GitHub device authorization is not configured.",
            GitHubDeviceFlowErrorKind.Network =>
                "GitHub device authorization could not reach the configured host.",
            GitHubDeviceFlowErrorKind.Timeout =>
                "GitHub device authorization request timed out.",
            GitHubDeviceFlowErrorKind.DeviceFlowDisabled =>
                "GitHub device authorization is disabled for this app.",
            GitHubDeviceFlowErrorKind.ClientRejected =>
                "GitHub rejected the configured OAuth client.",
            GitHubDeviceFlowErrorKind.Expired =>
                "GitHub device authorization expired. Start again.",
            GitHubDeviceFlowErrorKind.AccessDenied =>
                "GitHub device authorization was denied.",
            GitHubDeviceFlowErrorKind.InvalidResponse =>
                "GitHub returned an invalid device authorization response.",
            _ => "GitHub returned an invalid device authorization response.",
        };
}

/// <summary>Cancellation of one explicit GitHub device-flow operation.</summary>
public sealed class GitHubDeviceAuthorizationCancelledException
    : OperationCanceledException
{
    public GitHubDeviceAuthorizationCancelledException()
        : base("GitHub device authorization was cancelled.")
    {
    }
}

/// <summary>
/// Runs GitHub's OAuth 2.0 device authorization grant. It never launches a
/// browser, persists credentials, or starts a flow without an explicit call.
/// </summary>
public sealed class GitHubDeviceAuthorizationClient : IDisposable
{
    private const int MaximumDeviceCodeLength = 512;
    private const int MaximumUserCodeLength = 128;
    private const int MaximumLifetimeSeconds = 24 * 60 * 60;
    private const int MaximumPollingIntervalSeconds = 60 * 60;
    private const int PollingSlowDownSeconds = 5;
    private const int MaximumTokenLength = 8_192;
    private const int MaximumTokenTypeLength = 64;
    private const int MaximumScopeLength = 4_096;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpClient httpClient;
    private readonly GitHubDeviceAuthorizationClientOptions options;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly object owner = new();
    private readonly bool ownsHttpClient;
    private bool disposed;

    public GitHubDeviceAuthorizationClient(
        GitHubDeviceAuthorizationClientOptions options)
        : this(
            options,
            CreateHttpClient(),
            ownsHttpClient: true,
            static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken))
    {
    }

    internal GitHubDeviceAuthorizationClient(
        GitHubDeviceAuthorizationClientOptions options,
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
        : this(
            options,
            httpClient,
            ownsHttpClient: false,
            delayAsync)
    {
    }

    private GitHubDeviceAuthorizationClient(
        GitHubDeviceAuthorizationClientOptions options,
        HttpClient httpClient,
        bool ownsHttpClient,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        this.ownsHttpClient = ownsHttpClient;
    }

    /// <summary>
    /// Requests a new device code from the configured GitHub origin. The
    /// caller must deliberately pass the returned session to PollAsync.
    /// </summary>
    public async Task<GitHubDeviceAuthorization>
        StartDeviceAuthorizationAsync(
            CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfCancellationRequested(cancellationToken);
        var fields = new List<KeyValuePair<string, string>>
        {
            new("client_id", options.ClientId),
        };
        if (options.Scopes.Count > 0)
        {
            fields.Add(
                new("scope", string.Join(' ', options.Scopes)));
        }

        var response = await PostFormAsync(
                new Uri(options.Host, "login/device/code"),
                fields,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapStartError(response.Content);
        }

        var authorization = ParseDeviceAuthorization(
            response.Content,
            DateTimeOffset.UtcNow);
        ThrowIfCancellationRequested(cancellationToken);
        return authorization;
    }

    /// <summary>
    /// Polls an explicit device authorization session until it succeeds or a
    /// typed terminal GitHub outcome is returned. No token is persisted here.
    /// </summary>
    public async Task<GitHubDeviceAccessToken> PollAsync(
        GitHubDeviceAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ThrowIfDisposed();
        if (!ReferenceEquals(authorization.Owner, owner))
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.InvalidConfiguration);
        }

        ThrowIfCancellationRequested(cancellationToken);

        var interval = authorization.PollingInterval;
        while (true)
        {
            ThrowIfCancellationRequested(cancellationToken);
            var remaining = authorization.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new GitHubDeviceAuthorizationException(
                    GitHubDeviceFlowErrorKind.Expired);
            }

            var wait = interval <= remaining ? interval : remaining;
            try
            {
                await delayAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw new GitHubDeviceAuthorizationCancelledException();
            }
            catch
            {
                throw new GitHubDeviceAuthorizationException(
                    GitHubDeviceFlowErrorKind.Network);
            }

            ThrowIfCancellationRequested(cancellationToken);
            if (DateTimeOffset.UtcNow >= authorization.ExpiresAt)
            {
                throw new GitHubDeviceAuthorizationException(
                    GitHubDeviceFlowErrorKind.Expired);
            }

            var response = await PostFormAsync(
                    new Uri(options.Host, "login/oauth/access_token"),
                    [
                        new KeyValuePair<string, string>(
                            "client_id",
                            options.ClientId),
                        new KeyValuePair<string, string>(
                            "device_code",
                            authorization.DeviceCode),
                        new KeyValuePair<string, string>(
                            "grant_type",
                            "urn:ietf:params:oauth:grant-type:device_code"),
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
            var pollResult = ParsePollResponse(
                response.Content,
                response.StatusCode);
            if (pollResult.AccessToken is not null)
            {
                ThrowIfCancellationRequested(cancellationToken);
                return pollResult.AccessToken;
            }

            switch (pollResult.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(PollingSlowDownSeconds);
                    if (interval > TimeSpan.FromSeconds(MaximumPollingIntervalSeconds))
                    {
                        throw new GitHubDeviceAuthorizationException(
                            GitHubDeviceFlowErrorKind.Expired);
                    }

                    continue;
                case "expired_token":
                case "token_expired":
                    throw new GitHubDeviceAuthorizationException(
                        GitHubDeviceFlowErrorKind.Expired);
                case "access_denied":
                    throw new GitHubDeviceAuthorizationException(
                        GitHubDeviceFlowErrorKind.AccessDenied);
                case "device_flow_disabled":
                    throw new GitHubDeviceAuthorizationException(
                        GitHubDeviceFlowErrorKind.DeviceFlowDisabled);
                case "incorrect_client_credentials":
                case "invalid_client":
                    throw new GitHubDeviceAuthorizationException(
                        GitHubDeviceFlowErrorKind.ClientRejected);
                default:
                    throw new GitHubDeviceAuthorizationException(
                        GitHubDeviceFlowErrorKind.InvalidResponse);
            }
        }
    }

    private async Task<HttpPostResponse> PostFormAsync(
        Uri endpoint,
        IReadOnlyList<KeyValuePair<string, string>> fields,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("WinGit.Native/0.1");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCts.CancelAfter(options.RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new GitHubDeviceAuthorizationCancelledException();
        }
        catch (OperationCanceledException)
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.Timeout);
        }
        catch (HttpRequestException)
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.Network);
        }
        catch
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.Network);
        }

        using (response)
        {
            if (IsRedirect(response.StatusCode) ||
                response.RequestMessage?.RequestUri is { } actualUri &&
                !UrisMatch(actualUri, endpoint))
            {
                throw new GitHubDeviceAuthorizationException(
                    GitHubDeviceFlowErrorKind.InvalidResponse);
            }

            var content = await ReadBoundedResponseAsync(
                    response,
                    timeoutCts.Token,
                    cancellationToken)
                .ConfigureAwait(false);
            return new HttpPostResponse(response.StatusCode, content);
        }
    }

    private async Task<string> ReadBoundedResponseAsync(
        HttpResponseMessage response,
        CancellationToken operationCancellationToken,
        CancellationToken userCancellationToken)
    {
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > options.MaximumResponseBytes)
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.InvalidResponse);
        }

        try
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(operationCancellationToken)
                .ConfigureAwait(false);
            await using var buffer = new MemoryStream();
            var chunk = new byte[8 * 1024];
            while (true)
            {
                var count = await stream.ReadAsync(
                        chunk,
                        operationCancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                if (buffer.Length + count > options.MaximumResponseBytes)
                {
                    throw new GitHubDeviceAuthorizationException(
                        GitHubDeviceFlowErrorKind.InvalidResponse);
                }

                await buffer.WriteAsync(
                        chunk.AsMemory(0, count),
                        operationCancellationToken)
                    .ConfigureAwait(false);
            }

            return StrictUtf8.GetString(buffer.ToArray());
        }
        catch (GitHubDeviceAuthorizationException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (userCancellationToken.IsCancellationRequested)
        {
            throw new GitHubDeviceAuthorizationCancelledException();
        }
        catch (OperationCanceledException)
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.Timeout);
        }
        catch (DecoderFallbackException)
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.InvalidResponse);
        }
        catch
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.Network);
        }
    }

    private GitHubDeviceAuthorization ParseDeviceAuthorization(
        string content,
        DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 20 });
            var root = RequireObject(document.RootElement);
            var deviceCode = RequiredText(
                root,
                "device_code",
                MaximumDeviceCodeLength);
            var userCode = RequiredText(
                root,
                "user_code",
                MaximumUserCodeLength);
            var verificationUri = ParseVerificationUri(
                RequiredText(root, "verification_uri", 16_384));
            var expiresInSeconds = RequiredInteger(
                root,
                "expires_in",
                1,
                MaximumLifetimeSeconds);
            var intervalSeconds = RequiredInteger(
                root,
                "interval",
                1,
                MaximumPollingIntervalSeconds);
            var expiresIn = TimeSpan.FromSeconds(expiresInSeconds);
            return new GitHubDeviceAuthorization(
                owner,
                deviceCode,
                userCode,
                verificationUri,
                expiresIn,
                TimeSpan.FromSeconds(intervalSeconds),
                now.Add(expiresIn));
        }
        catch (GitHubDeviceAuthorizationException)
        {
            throw;
        }
        catch
        {
            throw MapStartError(content);
        }
    }

    private PollResponse ParsePollResponse(
        string content,
        HttpStatusCode statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 20 });
            var root = RequireObject(document.RootElement);
            if (root.TryGetProperty("access_token", out var accessTokenValue))
            {
                if ((int)statusCode is < 200 or >= 300 ||
                    root.TryGetProperty("error", out _))
                {
                    throw new GitHubDeviceAuthorizationException(
                        GitHubDeviceFlowErrorKind.InvalidResponse);
                }

                var accessToken = RequiredText(
                    accessTokenValue,
                    MaximumTokenLength);
                var tokenType = root.TryGetProperty(
                        "token_type",
                        out var tokenTypeValue)
                    ? OptionalText(tokenTypeValue, MaximumTokenTypeLength)
                    : "bearer";
                var scope = root.TryGetProperty("scope", out var scopeValue)
                    ? OptionalText(scopeValue, MaximumScopeLength)
                    : string.Empty;
                return new PollResponse(
                    new GitHubDeviceAccessToken(
                        accessToken,
                        tokenType,
                        scope,
                        options.Host),
                    Error: null);
            }

            var error = RequiredText(root, "error", 128);
            return new PollResponse(AccessToken: null, error);
        }
        catch (GitHubDeviceAuthorizationException)
        {
            throw;
        }
        catch
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.InvalidResponse);
        }
    }

    private static GitHubDeviceAuthorizationException MapStartError(
        string content)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 20 });
            var root = RequireObject(document.RootElement);
            var error = root.TryGetProperty("error", out var value)
                ? OptionalText(value, 128)
                : null;
            return error switch
            {
                "device_flow_disabled" => new GitHubDeviceAuthorizationException(
                    GitHubDeviceFlowErrorKind.DeviceFlowDisabled),
                "incorrect_client_credentials" or "invalid_client" =>
                    new GitHubDeviceAuthorizationException(
                        GitHubDeviceFlowErrorKind.ClientRejected),
                _ => new GitHubDeviceAuthorizationException(
                    GitHubDeviceFlowErrorKind.InvalidResponse),
            };
        }
        catch
        {
            return new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.InvalidResponse);
        }
    }

    private Uri ParseVerificationUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var verificationUri) ||
            !UrisMatchOrigin(verificationUri, options.Host) ||
            verificationUri.UserInfo.Length > 0 ||
            !string.IsNullOrEmpty(verificationUri.Query) ||
            !string.IsNullOrEmpty(verificationUri.Fragment) ||
            !string.Equals(
                verificationUri.AbsolutePath.TrimEnd('/'),
                "/login/device",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.InvalidResponse);
        }

        return verificationUri;
    }

    private static JsonElement RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.InvalidResponse);
        }

        return value;
    }

    private static string RequiredText(
        JsonElement root,
        string propertyName,
        int maximumLength)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.InvalidResponse);
        }

        return RequiredText(value, maximumLength);
    }

    private static string RequiredText(JsonElement value, int maximumLength)
    {
        var result = OptionalText(value, maximumLength);
        if (result.Length == 0)
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.InvalidResponse);
        }

        return result;
    }

    private static string OptionalText(JsonElement value, int maximumLength)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.InvalidResponse);
        }

        var result = value.GetString();
        if (result is null ||
            result.Length > maximumLength ||
            result.Any(character =>
                character <= '\u001F' || character == '\u007F'))
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.InvalidResponse);
        }

        return result.Trim();
    }

    private static int RequiredInteger(
        JsonElement root,
        string propertyName,
        int minimum,
        int maximum)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result) ||
            result < minimum ||
            result > maximum)
        {
            throw new GitHubDeviceAuthorizationException(
                GitHubDeviceFlowErrorKind.InvalidResponse);
        }

        return result;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is >= 300 and <= 399;

    private static bool UrisMatch(Uri left, Uri right) =>
        string.Equals(
            left.AbsoluteUri,
            right.AbsoluteUri,
            StringComparison.OrdinalIgnoreCase);

    private static bool UrisMatchOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static void ThrowIfCancellationRequested(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new GitHubDeviceAuthorizationCancelledException();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(GitHubDeviceAuthorizationClient));
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

    private sealed record PollResponse(
        GitHubDeviceAccessToken? AccessToken,
        string? Error);

    private sealed record HttpPostResponse(
        HttpStatusCode StatusCode,
        string Content)
    {
        public bool IsSuccessStatusCode =>
            (int)StatusCode is >= 200 and < 300;
    }
}
