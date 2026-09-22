using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinGit.Core.GitHub;

public enum GitHubNotificationEventKind
{
    PullRequestComment,
    PullRequestReview,
    ChecksFailed,
}

public sealed record GitHubNotificationEvent(
    GitHubNotificationEventKind Kind,
    string Owner,
    string Repository,
    int PullRequestNumber,
    string EventId,
    string? CommitSha,
    string? ReviewState,
    string? CommentKind,
    long Timestamp)
{
    public override string ToString() => "GitHub notification event.";
}

internal readonly record struct GitHubAliveSocketReceiveResult(
    int Count,
    WebSocketMessageType MessageType,
    bool EndOfMessage,
    WebSocketCloseStatus? CloseStatus);

internal interface IGitHubAliveSocket : IAsyncDisposable
{
    ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken);

    ValueTask<GitHubAliveSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);
}

public sealed class GitHubAliveNotificationClient : IDisposable
{
    private const int MaximumHttpResponseBytes = 64 * 1024;
    private const int MaximumWebSocketMessageBytes = 64 * 1024;
    private const int MaximumSignedChannelLength = 8 * 1024;
    private const int MaximumChannelNameLength = 512;
    private const int MaximumWebSocketUrlLength = 8 * 1024;
    private const int MaximumEventStringLength = 512;
    private const int MaximumOffsetLength = 8 * 1024;
    private const int WebSocketReadBufferBytes = 8 * 1024;
    private const int MaximumBackoffSeconds = 512;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WebSocketConnectTimeout =
        TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WebSocketReadTimeout =
        TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WebSocketSendTimeout =
        TimeSpan.FromSeconds(4);
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Uri WebSocketOrigin =
        new("https://desktop.github.com/");

    private readonly GitHubApiTransport transport;
    private readonly Func<
        Uri,
        CancellationToken,
        Task<IGitHubAliveSocket>> connectSocketAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private bool disposed;

    public GitHubAliveNotificationClient()
        : this(
            new GitHubApiTransport(
                GitHubApiEndpoint.GitHubCom,
                RequestTimeout,
                MaximumHttpResponseBytes),
            ConnectSocketAsync,
            static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken))
    {
    }

    internal GitHubAliveNotificationClient(
        HttpClient httpClient,
        Func<Uri, CancellationToken, Task<IGitHubAliveSocket>>
            connectSocketAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
        : this(
            new GitHubApiTransport(
                GitHubApiEndpoint.GitHubCom,
                RequestTimeout,
                MaximumHttpResponseBytes,
                httpClient),
            connectSocketAsync,
            delayAsync)
    {
    }

    private GitHubAliveNotificationClient(
        GitHubApiTransport transport,
        Func<Uri, CancellationToken, Task<IGitHubAliveSocket>>
            connectSocketAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.connectSocketAsync = connectSocketAsync ??
            throw new ArgumentNullException(nameof(connectSocketAsync));
        this.delayAsync = delayAsync ??
            throw new ArgumentNullException(nameof(delayAsync));
    }

    public async IAsyncEnumerable<GitHubNotificationEvent> ListenAsync(
        GitHubAccountSession session,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);

        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetimeCancellation.Token);
        var operationCancellation = linkedCancellation.Token;
        operationCancellation.ThrowIfCancellationRequested();

        if (!GitHubApiEndpoint.AreSame(
                session.ApiOrigin,
                GitHubApiEndpoint.GitHubCom))
        {
            yield break;
        }

        var presenceId = CreatePresenceId();
        var connectionCount = 0;
        var reconnectAttempt = 0;
        var offset = string.Empty;
        AliveConnection? connection = null;

        while (true)
        {
            operationCancellation.ThrowIfCancellationRequested();

            AliveFetchResult fetchResult;
            try
            {
                fetchResult = await FetchConnectionAsync(
                        session,
                        operationCancellation)
                    .ConfigureAwait(false);
            }
            catch (GitHubAliveTransientException)
            {
                await DelayBeforeReconnectAsync(
                        reconnectAttempt++,
                        operationCancellation)
                    .ConfigureAwait(false);
                continue;
            }

            if (fetchResult.Kind == AliveFetchResultKind.Stop)
            {
                yield break;
            }

            if (fetchResult.Connection is null)
            {
                await DelayBeforeReconnectAsync(
                        reconnectAttempt++,
                        operationCancellation)
                    .ConfigureAwait(false);
                continue;
            }

            if (connection is null ||
                !string.Equals(
                    connection.Value.ChannelName,
                    fetchResult.Connection.Value.ChannelName,
                    StringComparison.Ordinal))
            {
                offset = string.Empty;
            }

            var activeConnection = fetchResult.Connection.Value;
            connection = activeConnection;
            var socketState = new AliveSocketRunState();
            socketState.Offset = offset;
            await foreach (var notification in ReadSocketAsync(
                               activeConnection,
                               presenceId,
                               ++connectionCount,
                               socketState,
                               operationCancellation)
                           .ConfigureAwait(false))
            {
                yield return notification;
            }
            offset = socketState.Offset;

            operationCancellation.ThrowIfCancellationRequested();
            if (socketState.FatalClose)
            {
                yield break;
            }

            if (socketState.SawProtocolActivity)
            {
                reconnectAttempt = 0;
            }

            await DelayBeforeReconnectAsync(
                    reconnectAttempt++,
                    operationCancellation)
                .ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
        transport.Dispose();
    }

    private async Task<AliveFetchResult> FetchConnectionAsync(
        GitHubAccountSession session,
        CancellationToken cancellationToken)
    {
        var webSocketResponse = await GetAsync(
                "/alive_internal/websocket-url",
                session,
                cancellationToken)
            .ConfigureAwait(false);
        if (webSocketResponse.Kind != AliveFetchResultKind.Available)
        {
            return new AliveFetchResult(webSocketResponse.Kind, null);
        }

        if (!TryParseWebSocketResponse(
                webSocketResponse.Response!.Content,
                out var webSocketUri))
        {
            return new AliveFetchResult(AliveFetchResultKind.Stop, null);
        }

        var channelResponse = await GetAsync(
                "/desktop_internal/alive-channel",
                session,
                cancellationToken)
            .ConfigureAwait(false);
        if (channelResponse.Kind != AliveFetchResultKind.Available)
        {
            return new AliveFetchResult(channelResponse.Kind, null);
        }

        if (!TryParseChannelResponse(
                channelResponse.Response!.Content,
                out var channelName,
                out var signedChannel))
        {
            return new AliveFetchResult(AliveFetchResultKind.Stop, null);
        }

        return new AliveFetchResult(
            AliveFetchResultKind.Available,
            new AliveConnection(webSocketUri, channelName, signedChannel));
    }

    private async Task<AliveHttpResult> GetAsync(
        string path,
        GitHubAccountSession session,
        CancellationToken cancellationToken)
    {
        GitHubApiTransportResponse response;
        try
        {
            response = await transport.GetAsync(
                    new Uri(GitHubApiEndpoint.GitHubCom, path),
                    session,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitHubApiTransportException exception)
        {
            if (exception.Kind == GitHubApiTransportErrorKind.Cancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (exception.Kind is
                GitHubApiTransportErrorKind.Network or
                GitHubApiTransportErrorKind.Timeout)
            {
                throw new GitHubAliveTransientException();
            }

            return new AliveHttpResult(AliveFetchResultKind.Stop, null);
        }

        if (IsSuccess(response.StatusCode))
        {
            return new AliveHttpResult(AliveFetchResultKind.Available, response);
        }

        return new AliveHttpResult(
            IsRetryableStatus(response.StatusCode)
                ? AliveFetchResultKind.Retry
                : AliveFetchResultKind.Stop,
            null);
    }

    private static bool TryParseWebSocketResponse(
        string content,
        out Uri webSocketUri)
    {
        webSocketUri = null!;
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("url", out var url) ||
                url.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = url.GetString();
            if (!IsSafeString(value, MaximumWebSocketUrlLength) ||
                !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
                !IsAllowedWebSocketUri(parsed))
            {
                return false;
            }

            webSocketUri = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool TryParseChannelResponse(
        string content,
        out string channelName,
        out string signedChannel)
    {
        channelName = string.Empty;
        signedChannel = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(
                    "channel_name",
                    out var nameElement) ||
                !document.RootElement.TryGetProperty(
                    "signed_channel",
                    out var signedElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                signedElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var name = nameElement.GetString();
            var signed = signedElement.GetString();
            if (!IsSafeString(name, MaximumChannelNameLength) ||
                !IsSafeString(signed, MaximumSignedChannelLength) ||
                !TryValidateSignedChannel(name!, signed!))
            {
                return false;
            }

            channelName = name!;
            signedChannel = signed!;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryValidateSignedChannel(
        string channelName,
        string signedChannel)
    {
        var separator = signedChannel.IndexOf("--", StringComparison.Ordinal);
        if (separator <= 0 ||
            separator == signedChannel.Length - 2 ||
            signedChannel.IndexOf(
                "--",
                separator + 2,
                StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        try
        {
            var content = signedChannel[..separator];
            var decoded = StrictUtf8.GetString(Convert.FromBase64String(content));
            using var document = JsonDocument.Parse(
                decoded,
                new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("c", out var channel) ||
                channel.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    channel.GetString(),
                    channelName,
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("t", out var token) ||
                !IsTruthy(token))
            {
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsTruthy(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
            JsonValueKind.Number => value.TryGetDouble(out var number) &&
                number != 0,
            JsonValueKind.True => true,
            _ => false,
        };

    private static bool IsAllowedWebSocketUri(Uri value) =>
        value.IsAbsoluteUri &&
        string.Equals(
            value.Scheme,
            "wss",
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            value.Host,
            "alive.github.com",
            StringComparison.OrdinalIgnoreCase) &&
        (value.Port is -1 or 443) &&
        value.UserInfo.Length == 0 &&
        string.IsNullOrEmpty(value.Fragment);

    private static bool TryReadEvent(
        string payload,
        string channelName,
        ref string offset,
        out GitHubNotificationEvent? notification)
    {
        notification = null;
        try
        {
            using var document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("e", out var envelopeType) ||
                envelopeType.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var type = envelopeType.GetString();
            if (string.Equals(type, "ack", StringComparison.Ordinal))
            {
                TryUpdateOffset(root, ref offset);
                return false;
            }

            if (!string.Equals(type, "msg", StringComparison.Ordinal) ||
                !root.TryGetProperty("ch", out var channel) ||
                channel.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    channel.GetString(),
                    channelName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            TryUpdateOffset(root, ref offset);
            if (!root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return TryMapNotification(data, out notification);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void TryUpdateOffset(
        JsonElement envelope,
        ref string offset)
    {
        if (!envelope.TryGetProperty("off", out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var next = value.GetString();
        if (IsSafeString(next, MaximumOffsetLength))
        {
            offset = next!;
        }
    }

    private static bool TryMapNotification(
        JsonElement data,
        out GitHubNotificationEvent? notification)
    {
        notification = null;
        if (!TryGetString(data, "type", MaximumEventStringLength, out var type) ||
            !TryGetString(data, "owner", MaximumEventStringLength, out var owner) ||
            !TryGetString(data, "repo", MaximumEventStringLength, out var repository) ||
            !TryGetPositiveInt(data, "pull_request_number", out var pullRequestNumber) ||
            !TryGetTimestamp(data, out var timestamp))
        {
            return false;
        }

        if (string.Equals(type, "pr-comment", StringComparison.Ordinal))
        {
            if (!TryGetString(
                    data,
                    "subtype",
                    MaximumEventStringLength,
                    out var commentKind) ||
                !IsCommentKind(commentKind) ||
                !TryGetString(
                    data,
                    "comment_id",
                    MaximumEventStringLength,
                    out var eventId))
            {
                return false;
            }

            notification = new GitHubNotificationEvent(
                GitHubNotificationEventKind.PullRequestComment,
                owner,
                repository,
                pullRequestNumber,
                eventId,
                null,
                null,
                commentKind,
                timestamp);
            return true;
        }

        if (string.Equals(type, "pr-review-submit", StringComparison.Ordinal))
        {
            if (!TryGetString(
                    data,
                    "state",
                    MaximumEventStringLength,
                    out var reviewState) ||
                !IsReviewState(reviewState) ||
                !TryGetString(
                    data,
                    "review_id",
                    MaximumEventStringLength,
                    out var eventId))
            {
                return false;
            }

            notification = new GitHubNotificationEvent(
                GitHubNotificationEventKind.PullRequestReview,
                owner,
                repository,
                pullRequestNumber,
                eventId,
                null,
                reviewState,
                null,
                timestamp);
            return true;
        }

        if (string.Equals(type, "pr-checks-failed", StringComparison.Ordinal) &&
            TryGetPositiveLong(data, "check_suite_id", out var checkSuiteId) &&
            TryGetString(
                data,
                "commit_sha",
                MaximumEventStringLength,
                out var commitSha))
        {
            notification = new GitHubNotificationEvent(
                GitHubNotificationEventKind.ChecksFailed,
                owner,
                repository,
                pullRequestNumber,
                checkSuiteId.ToString(CultureInfo.InvariantCulture),
                commitSha,
                null,
                null,
                timestamp);
            return true;
        }

        return false;
    }

    private static bool TryGetString(
        JsonElement parent,
        string name,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (!IsSafeString(candidate, maximumLength))
        {
            return false;
        }

        value = candidate!;
        return true;
    }

    private static bool TryGetPositiveInt(
        JsonElement parent,
        string name,
        out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value) &&
            value > 0;
    }

    private static bool TryGetPositiveLong(
        JsonElement parent,
        string name,
        out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value) &&
            value > 0;
    }

    private static bool TryGetTimestamp(
        JsonElement parent,
        out long timestamp)
    {
        timestamp = 0;
        return parent.TryGetProperty("timestamp", out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out timestamp) &&
            timestamp >= 0;
    }

    private static bool IsSafeString(string? value, int maximumLength) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            character >= '\u0020' && character != '\u007f');

    private static bool IsCommentKind(string value) =>
        string.Equals(value, "review-comment", StringComparison.Ordinal) ||
        string.Equals(value, "issue-comment", StringComparison.Ordinal);

    private static bool IsReviewState(string value) =>
        string.Equals(value, "APPROVED", StringComparison.Ordinal) ||
        string.Equals(value, "CHANGES_REQUESTED", StringComparison.Ordinal) ||
        string.Equals(value, "COMMENTED", StringComparison.Ordinal);

    private static async Task SendSubscribeAsync(
        IGitHubAliveSocket socket,
        string signedChannel,
        string offset,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                subscribe = new Dictionary<string, string>
                {
                    [signedChannel] = offset,
                },
            });
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WebSocketSendTimeout);
        try
        {
            await socket.SendAsync(
                    payload,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubAliveTransientException();
        }
    }

    private async IAsyncEnumerable<GitHubNotificationEvent> ReadSocketAsync(
        AliveConnection connection,
        string presenceId,
        int connectionCount,
        AliveSocketRunState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var socketUri = AddSocketParameters(
            connection.WebSocketUri,
            presenceId,
            connectionCount);
        IGitHubAliveSocket? socket = null;
        var connected = false;
        try
        {
            socket = await connectSocketAsync(socketUri, cancellationToken)
                .ConfigureAwait(false);
            connected = true;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (GitHubAliveTransientException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        if (!connected || socket is null)
        {
            yield break;
        }

        try
        {
            var subscribed = false;
            try
            {
                await SendSubscribeAsync(
                        socket,
                        connection.SignedChannel,
                        state.Offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                subscribed = true;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (GitHubAliveTransientException)
            {
            }
            catch (WebSocketException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            if (!subscribed)
            {
                yield break;
            }

            while (true)
            {
                var received = false;
                var message = default(AliveSocketMessage);
                try
                {
                    message = await ReceiveMessageAsync(
                            socket,
                            cancellationToken)
                        .ConfigureAwait(false);
                    received = true;
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                }
                catch (GitHubAliveTransientException)
                {
                }
                catch (WebSocketException)
                {
                }
                catch (InvalidOperationException)
                {
                }

                if (!received)
                {
                    yield break;
                }

                if (message.IsClose)
                {
                    state.FatalClose = message.CloseStatus is
                        WebSocketCloseStatus.PolicyViolation;
                    yield break;
                }

                if (message.Text is null)
                {
                    continue;
                }

                state.SawProtocolActivity = true;
                var nextOffset = state.Offset;
                if (TryReadEvent(
                        message.Text,
                        connection.ChannelName,
                        ref nextOffset,
                        out var notification))
                {
                    state.Offset = nextOffset;
                    yield return notification!;
                }
                else
                {
                    state.Offset = nextOffset;
                }
            }
        }
        finally
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<AliveSocketMessage> ReceiveMessageAsync(
        IGitHubAliveSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(WebSocketReadBufferBytes);
        await using var payload = new MemoryStream();
        var messageType = WebSocketMessageType.Text;
        var firstResult = true;
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(WebSocketReadTimeout);
        try
        {
            while (true)
            {
                GitHubAliveSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(
                            buffer.AsMemory(0, WebSocketReadBufferBytes),
                            timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    throw new GitHubAliveTransientException();
                }

                if (result.Count < 0 ||
                    result.Count > WebSocketReadBufferBytes)
                {
                    throw new GitHubAliveTransientException();
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return new AliveSocketMessage(
                        null,
                        true,
                        result.CloseStatus);
                }

                if (firstResult)
                {
                    messageType = result.MessageType;
                    firstResult = false;
                }

                if (result.Count > 0)
                {
                    if (payload.Length + result.Count >
                        MaximumWebSocketMessageBytes)
                    {
                        throw new GitHubAliveTransientException();
                    }

                    await payload.WriteAsync(
                            buffer.AsMemory(0, result.Count),
                            timeout.Token)
                        .ConfigureAwait(false);
                }

                if (result.EndOfMessage)
                {
                    break;
                }
            }

            if (messageType != WebSocketMessageType.Text)
            {
                return new AliveSocketMessage(null, false, null);
            }

            try
            {
                return new AliveSocketMessage(
                    StrictUtf8.GetString(payload.ToArray()),
                    false,
                    null);
            }
            catch (DecoderFallbackException)
            {
                return new AliveSocketMessage(null, false, null);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Uri AddSocketParameters(
        Uri uri,
        string presenceId,
        int connectionCount)
    {
        var original = uri.OriginalString;
        var queryStart = original.IndexOf('?');
        var path = queryStart < 0 ? original : original[..queryStart];
        var query = queryStart < 0 ? string.Empty : original[(queryStart + 1)..];
        var components = query.Length == 0
            ? new List<string>()
            : query.Split('&', StringSplitOptions.None).ToList();

        ReplaceQueryParameter(components, "shared", "shared=false");
        ReplaceQueryParameter(
            components,
            "p",
            $"p={Uri.EscapeDataString($"{presenceId}.{connectionCount}")}");

        return new Uri(
            $"{path}?{string.Join('&', components)}",
            UriKind.Absolute);
    }

    private static void ReplaceQueryParameter(
        List<string> components,
        string parameterName,
        string replacement)
    {
        var firstMatch = -1;
        for (var index = 0; index < components.Count; index++)
        {
            if (!QueryParameterNameEquals(components[index], parameterName))
            {
                continue;
            }

            if (firstMatch < 0)
            {
                firstMatch = index;
                components[index] = replacement;
            }
            else
            {
                components.RemoveAt(index--);
            }
        }

        if (firstMatch < 0)
        {
            components.Add(replacement);
        }
    }

    private static bool QueryParameterNameEquals(
        string component,
        string parameterName)
    {
        var separator = component.IndexOf('=');
        var rawName = separator < 0 ? component : component[..separator];
        try
        {
            return string.Equals(
                Uri.UnescapeDataString(rawName.Replace('+', ' ')),
                parameterName,
                StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static string CreatePresenceId() =>
        $"{RandomNumberGenerator.GetInt32(int.MaxValue)}_" +
        $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

    private async Task DelayBeforeReconnectAsync(
        int attempt,
        CancellationToken cancellationToken)
    {
        var seconds = 1L << Math.Min(attempt, 9);
        seconds = Math.Min(seconds, MaximumBackoffSeconds);
        await delayAsync(
                TimeSpan.FromSeconds(seconds),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsSuccess(HttpStatusCode statusCode) =>
        (int)statusCode is >= 200 and < 300;

    private static bool IsRetryableStatus(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static async Task<IGitHubAliveSocket> ConnectSocketAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WebSocketConnectTimeout);
        try
        {
            return await ClientWebSocketAdapter.ConnectAsync(uri, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubAliveTransientException();
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GitHubAliveNotificationClient));
        }
    }

    private readonly record struct AliveConnection(
        Uri WebSocketUri,
        string ChannelName,
        string SignedChannel);

    private readonly record struct AliveHttpResult(
        AliveFetchResultKind Kind,
        GitHubApiTransportResponse? Response);

    private readonly record struct AliveFetchResult(
        AliveFetchResultKind Kind,
        AliveConnection? Connection);

    private readonly record struct AliveSocketMessage(
        string? Text,
        bool IsClose,
        WebSocketCloseStatus? CloseStatus);

    private sealed class AliveSocketRunState
    {
        public string Offset { get; set; } = string.Empty;

        public bool SawProtocolActivity { get; set; }

        public bool FatalClose { get; set; }
    }

    private enum AliveFetchResultKind
    {
        Available,
        Retry,
        Stop,
    }

    private sealed class GitHubAliveTransientException : Exception
    {
    }

    private sealed class ClientWebSocketAdapter : IGitHubAliveSocket
    {
        private readonly ClientWebSocket socket;
        private readonly HttpMessageInvoker invoker;

        private ClientWebSocketAdapter(
            ClientWebSocket socket,
            HttpMessageInvoker invoker)
        {
            this.socket = socket;
            this.invoker = invoker;
        }

        public static async Task<IGitHubAliveSocket> ConnectAsync(
            Uri uri,
            CancellationToken cancellationToken)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
            };
            var invoker = new HttpMessageInvoker(
                handler,
                disposeHandler: true);
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader(
                "Origin",
                WebSocketOrigin.GetLeftPart(UriPartial.Authority));
            try
            {
                await socket.ConnectAsync(uri, invoker, cancellationToken)
                    .ConfigureAwait(false);
                return new ClientWebSocketAdapter(socket, invoker);
            }
            catch
            {
                socket.Dispose();
                invoker.Dispose();
                throw;
            }
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) =>
            socket.SendAsync(
                buffer,
                messageType,
                endOfMessage,
                cancellationToken);

        public ValueTask<GitHubAliveSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken) =>
            ReceiveCoreAsync(buffer, cancellationToken);

        public ValueTask DisposeAsync()
        {
            socket.Dispose();
            invoker.Dispose();
            return ValueTask.CompletedTask;
        }

        private async ValueTask<GitHubAliveSocketReceiveResult> ReceiveCoreAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            return new GitHubAliveSocketReceiveResult(
                result.Count,
                result.MessageType,
                result.EndOfMessage,
                socket.CloseStatus);
        }
    }
}
