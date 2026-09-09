using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace WinGit.Core.GitHub;

internal enum GitHubApiTransportErrorKind
{
    SessionMismatch,
    Network,
    Timeout,
    Cancelled,
    InvalidResponse,
}

internal sealed class GitHubApiTransportException : InvalidOperationException
{
    public GitHubApiTransportException(GitHubApiTransportErrorKind kind)
        : base(GetMessage(kind))
    {
        Kind = kind;
    }

    public GitHubApiTransportErrorKind Kind { get; }

    private static string GetMessage(GitHubApiTransportErrorKind kind) =>
        kind switch
        {
            GitHubApiTransportErrorKind.SessionMismatch =>
                "The GitHub account session belongs to a different host.",
            GitHubApiTransportErrorKind.Network =>
                "The GitHub request could not be reached.",
            GitHubApiTransportErrorKind.Timeout =>
                "The GitHub request timed out.",
            GitHubApiTransportErrorKind.Cancelled =>
                "The GitHub request was cancelled.",
            GitHubApiTransportErrorKind.InvalidResponse =>
                "GitHub returned an invalid response.",
            _ => "The GitHub request could not be completed.",
        };
}

internal sealed class GitHubApiTransportResponse
{
    public GitHubApiTransportResponse(
        HttpStatusCode statusCode,
        bool hasSamlHeader,
        bool isRateLimited,
        string content)
    {
        StatusCode = statusCode;
        HasSamlHeader = hasSamlHeader;
        IsRateLimited = isRateLimited;
        Content = content;
    }

    public HttpStatusCode StatusCode { get; }

    public bool HasSamlHeader { get; }

    public bool IsRateLimited { get; }

    public string Content { get; }

    public override string ToString() => "GitHub API transport response.";
}

/// <summary>
/// The shared read-only HTTP boundary for native GitHub REST clients. It owns
/// the production redirect policy, timeout, bounded body read, and common
/// request headers; endpoint-specific clients retain response parsing and
/// status mapping.
/// </summary>
internal sealed class GitHubApiTransport : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Uri apiOrigin;
    private readonly TimeSpan requestTimeout;
    private readonly int maximumResponseBytes;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private bool disposed;

    internal GitHubApiTransport(
        Uri apiOrigin,
        TimeSpan requestTimeout,
        int maximumResponseBytes)
        : this(
            apiOrigin,
            requestTimeout,
            maximumResponseBytes,
            CreateHttpClient(),
            ownsHttpClient: true)
    {
    }

    internal GitHubApiTransport(
        Uri apiOrigin,
        TimeSpan requestTimeout,
        int maximumResponseBytes,
        HttpClient httpClient)
        : this(
            apiOrigin,
            requestTimeout,
            maximumResponseBytes,
            httpClient,
            ownsHttpClient: false)
    {
    }

    private GitHubApiTransport(
        Uri apiOrigin,
        TimeSpan requestTimeout,
        int maximumResponseBytes,
        HttpClient httpClient,
        bool ownsHttpClient)
    {
        this.apiOrigin = apiOrigin ?? throw new ArgumentNullException(nameof(apiOrigin));
        this.requestTimeout = requestTimeout;
        this.maximumResponseBytes = maximumResponseBytes;
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.ownsHttpClient = ownsHttpClient;
    }

    internal Task<GitHubApiTransportResponse> GetAsync(
        Uri endpoint,
        GitHubAccountSession session,
        CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Get,
            endpoint,
            session,
            cancellationToken);

    internal Task<GitHubApiTransportResponse> PostAsync(
        Uri endpoint,
        GitHubAccountSession session,
        CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Post,
            endpoint,
            session,
            cancellationToken);

    private async Task<GitHubApiTransportResponse> SendAsync(
        HttpMethod method,
        Uri endpoint,
        GitHubAccountSession session,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);
        if (!GitHubApiEndpoint.AreSame(session.ApiOrigin, apiOrigin))
        {
            throw new GitHubApiTransportException(
                GitHubApiTransportErrorKind.SessionMismatch);
        }
        if (!IsAllowedEndpoint(endpoint))
        {
            throw new GitHubApiTransportException(
                GitHubApiTransportErrorKind.InvalidResponse);
        }

        using var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add(
            "X-GitHub-Api-Version",
            GitHubAuthenticatedProfileClientOptions.RestApiVersion);
        request.Headers.UserAgent.ParseAdd("WinGit.Native/0.1");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            session.AccessToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCts.CancelAfter(requestTimeout);

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
            throw new GitHubApiTransportException(
                GitHubApiTransportErrorKind.Cancelled);
        }
        catch (OperationCanceledException)
        {
            throw new GitHubApiTransportException(
                GitHubApiTransportErrorKind.Timeout);
        }
        catch (HttpRequestException)
        {
            throw new GitHubApiTransportException(
                GitHubApiTransportErrorKind.Network);
        }
        catch
        {
            throw new GitHubApiTransportException(
                GitHubApiTransportErrorKind.Network);
        }

        using (response)
        {
            if (IsRedirect(response.StatusCode) ||
                response.RequestMessage?.RequestUri is { } actualUri &&
                !GitHubApiEndpoint.AreSame(actualUri, endpoint))
            {
                throw new GitHubApiTransportException(
                    GitHubApiTransportErrorKind.InvalidResponse);
            }

            var content = await ReadBoundedResponseAsync(
                    response,
                    timeoutCts.Token,
                    cancellationToken)
                .ConfigureAwait(false);
            var hasSamlHeader = response.Headers.Contains("X-GitHub-SSO");
            var isRateLimited = IsRateLimited(response.Headers, content);
            return new GitHubApiTransportResponse(
                response.StatusCode,
                hasSamlHeader,
                isRateLimited,
                content);
        }
    }

    private async Task<string> ReadBoundedResponseAsync(
        HttpResponseMessage response,
        CancellationToken operationCancellationToken,
        CancellationToken userCancellationToken)
    {
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > maximumResponseBytes)
        {
            throw new GitHubApiTransportException(
                GitHubApiTransportErrorKind.InvalidResponse);
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

                if (buffer.Length + count > maximumResponseBytes)
                {
                    throw new GitHubApiTransportException(
                        GitHubApiTransportErrorKind.InvalidResponse);
                }

                await buffer.WriteAsync(
                        chunk.AsMemory(0, count),
                        operationCancellationToken)
                    .ConfigureAwait(false);
            }

            return StrictUtf8.GetString(buffer.ToArray());
        }
        catch (GitHubApiTransportException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (userCancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiTransportException(
                GitHubApiTransportErrorKind.Cancelled);
        }
        catch (OperationCanceledException)
        {
            throw new GitHubApiTransportException(
                GitHubApiTransportErrorKind.Timeout);
        }
        catch (DecoderFallbackException)
        {
            throw new GitHubApiTransportException(
                GitHubApiTransportErrorKind.InvalidResponse);
        }
        catch
        {
            throw new GitHubApiTransportException(
                GitHubApiTransportErrorKind.Network);
        }
    }

    private static bool IsRateLimited(
        HttpResponseHeaders headers,
        string content)
    {
        if (headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
            values.Any(value => string.Equals(
                value.Trim(),
                "0",
                StringComparison.Ordinal)))
        {
            return true;
        }

        return content.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is >= 300 and <= 399;

    private bool IsAllowedEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri ||
            !string.Equals(
                endpoint.Scheme,
                apiOrigin.Scheme,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                endpoint.Host,
                apiOrigin.Host,
                StringComparison.OrdinalIgnoreCase) ||
            endpoint.Port != apiOrigin.Port ||
            endpoint.UserInfo.Length > 0 ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            return false;
        }

        var basePath = apiOrigin.AbsolutePath.TrimEnd('/');
        if (basePath.Length == 0)
        {
            return true;
        }

        var endpointPath = endpoint.AbsolutePath.TrimEnd('/');
        return string.Equals(endpointPath, basePath, StringComparison.Ordinal) ||
            endpointPath.StartsWith(
                basePath + "/",
                StringComparison.Ordinal);
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
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(GitHubApiTransport));
        }
    }
}
