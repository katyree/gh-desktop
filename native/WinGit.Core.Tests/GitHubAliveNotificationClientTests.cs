using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WinGit.Core.GitHub;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitHubAliveNotificationClientTests
{
    [Fact]
    public async Task ListenMapsEventsAndResumesTheAliveOffset()
    {
        const string channelName = "desktop-notifications";
        var signedChannel = CreateSignedChannel(channelName);
        var requestedUris = new List<string>();
        var firstSocket = new FakeSocket(
            [
                TextMessage("{\"e\":\"ack\",\"off\":\"offset-1\"}"),
                TextMessage(
                    "{\"e\":\"msg\",\"ch\":\"other-channel\",\"off\":\"ignored\",\"data\":{}}"),
                TextMessage(
                    $"{{\"e\":\"msg\",\"ch\":\"{channelName}\",\"off\":\"offset-2\",\"data\":{{\"type\":\"pr-comment\",\"subtype\":\"review-comment\",\"timestamp\":100,\"owner\":\"octo\",\"repo\":\"hello\",\"pull_request_number\":7,\"comment_id\":\"comment-1\"}}}}"),
                CloseMessage(WebSocketCloseStatus.NormalClosure),
            ]);
        var secondSocket = new FakeSocket(
            [
                TextMessage(
                    $"{{\"e\":\"msg\",\"ch\":\"{channelName}\",\"off\":\"offset-3\",\"data\":{{\"type\":\"pr-review-submit\",\"state\":\"APPROVED\",\"timestamp\":200,\"owner\":\"octo\",\"repo\":\"hello\",\"pull_request_number\":7,\"review_id\":\"review-1\"}}}}"),
                TextMessage(
                    $"{{\"e\":\"msg\",\"ch\":\"{channelName}\",\"off\":\"offset-4\",\"data\":{{\"type\":\"pr-checks-failed\",\"timestamp\":300,\"owner\":\"octo\",\"repo\":\"hello\",\"pull_request_number\":7,\"check_suite_id\":42,\"commit_sha\":\"abc123\"}}}}"),
            ]);
        var sockets = new Queue<FakeSocket>([firstSocket, secondSocket]);
        var handler = new SequenceHandler(
            (request, _, _) =>
            {
                requestedUris.Add(request.RequestUri!.AbsoluteUri);
                Assert.Equal(
                    "Bearer synthetic-alive-token",
                    request.Headers.Authorization?.ToString());
                var requestPath = request.RequestUri!.AbsolutePath;
                return Task.FromResult(
                    requestPath.EndsWith(
                        "websocket-url",
                        StringComparison.Ordinal)
                        ? JsonResponse(
                            "{\"url\":\"wss://alive.github.com/u/123/ws?token=synthetic-ws&shared=stale&x=a%2Bb&p=stale&%73hared=duplicate&%70=duplicate\"}")
                        : JsonResponse(
                            $"{{\"channel_name\":\"{channelName}\",\"signed_channel\":\"{signedChannel}\"}}"));
            });
        using var httpClient = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        using var client = new GitHubAliveNotificationClient(
            httpClient,
            (uri, _) =>
            {
                Assert.Equal("wss", uri.Scheme);
                Assert.Equal("alive.github.com", uri.Host);
                var queryParts = uri.Query
                    .TrimStart('?')
                    .Split('&', StringSplitOptions.None);
                var sharedParts = queryParts
                    .Where(part => QueryParameterName(part) == "shared")
                    .ToArray();
                var presenceParts = queryParts
                    .Where(part => QueryParameterName(part) == "p")
                    .ToArray();
                Assert.Single(sharedParts);
                Assert.Equal("shared=false", sharedParts[0]);
                Assert.Single(presenceParts);
                Assert.StartsWith("p=", presenceParts[0], StringComparison.Ordinal);
                Assert.Contains(
                    "token=synthetic-ws",
                    queryParts,
                    StringComparer.Ordinal);
                Assert.Contains("x=a%2Bb", queryParts, StringComparer.Ordinal);
                Assert.DoesNotContain(
                    "synthetic-alive-token",
                    uri.AbsoluteUri,
                    StringComparison.Ordinal);
                return Task.FromResult<IGitHubAliveSocket>(sockets.Dequeue());
            },
            static (_, _) => Task.CompletedTask);
        var session = GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                "synthetic-alive-token",
                "bearer",
                "notifications",
                new Uri("https://github.com")));

        var events = new List<GitHubNotificationEvent>();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var notification in client.ListenAsync(
                               session,
                               cancellation.Token))
            {
                events.Add(notification);
                if (events.Count == 3)
                {
                    cancellation.Cancel();
                }
            }
        });

        Assert.Equal(4, requestedUris.Count);
        Assert.Equal(3, events.Count);
        Assert.Equal(
            GitHubNotificationEventKind.PullRequestComment,
            events[0].Kind);
        Assert.Equal("comment-1", events[0].EventId);
        Assert.Equal("review-comment", events[0].CommentKind);
        Assert.Equal(100, events[0].Timestamp);
        Assert.Equal(
            GitHubNotificationEventKind.PullRequestReview,
            events[1].Kind);
        Assert.Equal("review-1", events[1].EventId);
        Assert.Equal("APPROVED", events[1].ReviewState);
        Assert.Equal(
            GitHubNotificationEventKind.ChecksFailed,
            events[2].Kind);
        Assert.Equal("42", events[2].EventId);
        Assert.Equal("abc123", events[2].CommitSha);
        Assert.Equal(300, events[2].Timestamp);

        var sentPayloads = firstSocket.SentPayloads
            .Concat(secondSocket.SentPayloads)
            .ToArray();
        Assert.Equal(2, sentPayloads.Length);
        Assert.Equal(signedChannel, ReadSubscriptionKey(sentPayloads[0]));
        Assert.Equal(string.Empty, ReadSubscriptionOffset(sentPayloads[0]));
        Assert.Equal("offset-2", ReadSubscriptionOffset(sentPayloads[1]));
        Assert.DoesNotContain(
            "synthetic-alive-token",
            string.Join('\n', sentPayloads),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListenStopsForUnsupportedOrUnauthenticatedAccounts()
    {
        var enterpriseHandler = new SequenceHandler(
            (_, _, _) => throw new InvalidOperationException(
                "An unsupported enterprise account must not call Alive."));
        using var enterpriseHttpClient = new HttpClient(enterpriseHandler);
        using var enterpriseClient = new GitHubAliveNotificationClient(
            enterpriseHttpClient,
            (_, _) => throw new InvalidOperationException(),
            static (_, _) => Task.CompletedTask);
        var enterpriseSession = GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                "synthetic-enterprise-token",
                "bearer",
                "notifications",
                new Uri("https://enterprise.example.test")));

        var enterpriseEvents = new List<GitHubNotificationEvent>();
        await foreach (var notification in enterpriseClient.ListenAsync(
                           enterpriseSession))
        {
            enterpriseEvents.Add(notification);
        }

        Assert.Empty(enterpriseEvents);
        Assert.Equal(0, enterpriseHandler.CallCount);

        var unauthorizedHandler = new SequenceHandler(
            (_, _, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var unauthorizedHttpClient = new HttpClient(unauthorizedHandler);
        using var unauthorizedClient = new GitHubAliveNotificationClient(
            unauthorizedHttpClient,
            (_, _) => throw new InvalidOperationException(),
            static (_, _) => Task.CompletedTask);
        var dotComSession = GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                "synthetic-unauthorized-token",
                "bearer",
                "notifications",
                new Uri("https://github.com")));

        var unauthorizedEvents = new List<GitHubNotificationEvent>();
        await foreach (var notification in unauthorizedClient.ListenAsync(
                           dotComSession))
        {
            unauthorizedEvents.Add(notification);
        }

        Assert.Empty(unauthorizedEvents);
        Assert.Equal(1, unauthorizedHandler.CallCount);
    }

    [Fact]
    public async Task ListenRejectsAWebSocketOutsideTheKnownAliveOrigin()
    {
        var socketFactoryCalls = 0;
        var signedChannel = CreateSignedChannel("desktop-notifications");
        var handler = new SequenceHandler(
            (request, _, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith(
                    "websocket-url",
                    StringComparison.Ordinal)
                    ? JsonResponse(
                        "{\"url\":\"wss://evil.example.test/u/123/ws?token=synthetic\"}")
                    : JsonResponse(
                        $"{{\"channel_name\":\"desktop-notifications\",\"signed_channel\":\"{signedChannel}\"}}")));
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubAliveNotificationClient(
            httpClient,
            (_, _) =>
            {
                socketFactoryCalls++;
                return Task.FromResult<IGitHubAliveSocket>(
                    new FakeSocket([]));
            },
            static (_, _) => Task.CompletedTask);
        var session = GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                "synthetic-origin-token",
                "bearer",
                "notifications",
                new Uri("https://github.com")));

        var events = new List<GitHubNotificationEvent>();
        await foreach (var notification in client.ListenAsync(session))
        {
            events.Add(notification);
        }

        Assert.Empty(events);
        Assert.Equal(0, socketFactoryCalls);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ListenDisposesTheSocketWhenCancelledDuringSubscription()
    {
        const string channelName = "desktop-notifications";
        var socket = new BlockingSendSocket();
        var signedChannel = CreateSignedChannel(channelName);
        var handler = new SequenceHandler(
            (request, _, _) => Task.FromResult(
                    request.RequestUri!.AbsolutePath.EndsWith(
                        "websocket-url",
                        StringComparison.Ordinal)
                    ? JsonResponse(
                        "{\"url\":\"wss://alive.github.com/u/123/ws?token=synthetic\"}")
                    : JsonResponse(
                        $"{{\"channel_name\":\"{channelName}\",\"signed_channel\":\"{signedChannel}\"}}")));
        using var httpClient = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        using var client = new GitHubAliveNotificationClient(
            httpClient,
            (_, _) => Task.FromResult<IGitHubAliveSocket>(socket),
            static (_, _) => Task.CompletedTask);
        var session = GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                "synthetic-cancellation-token",
                "bearer",
                "notifications",
                new Uri("https://github.com")));

        await using var enumerator = client.ListenAsync(
                session,
                cancellation.Token)
            .GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();
        await socket.SendStarted;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext);
        Assert.True(socket.Disposed);
    }

    private static string CreateSignedChannel(string channelName)
    {
        var content = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(
                $"{{\"c\":\"{channelName}\",\"t\":\"synthetic-ticket\"}}"));
        return $"{content}--synthetic-signature";
    }

    private static FakeSocketMessage TextMessage(string value) =>
        new(
            Encoding.UTF8.GetBytes(value),
            WebSocketMessageType.Text,
            null);

    private static FakeSocketMessage CloseMessage(WebSocketCloseStatus status) =>
        new([], WebSocketMessageType.Close, status);

    private static string ReadSubscriptionKey(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement
            .GetProperty("subscribe")
            .EnumerateObject()
            .Single()
            .Name;
    }

    private static string ReadSubscriptionOffset(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement
            .GetProperty("subscribe")
            .EnumerateObject()
            .Single()
            .Value
            .GetString()!;
    }

    private static string QueryParameterName(string component)
    {
        var separator = component.IndexOf('=');
        var rawName = separator < 0 ? component : component[..separator];
        return Uri.UnescapeDataString(rawName.Replace('+', ' '));
    }

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                content,
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            int,
            CancellationToken,
            Task<HttpResponseMessage>> responder;
        private int callCount;

        public SequenceHandler(
            Func<
                HttpRequestMessage,
                int,
                CancellationToken,
                Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        public int CallCount => callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref callCount);
            return responder(request, call, cancellationToken);
        }
    }

    private sealed record FakeSocketMessage(
        byte[] Payload,
        WebSocketMessageType MessageType,
        WebSocketCloseStatus? CloseStatus);

    private sealed class FakeSocket : IGitHubAliveSocket
    {
        private readonly Queue<FakeSocketMessage> messages;
        private readonly List<string> sentPayloads = [];

        public FakeSocket(IEnumerable<FakeSocketMessage> messages)
        {
            this.messages = new Queue<FakeSocketMessage>(messages);
        }

        public IReadOnlyList<string> SentPayloads => sentPayloads;

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            Assert.Equal(WebSocketMessageType.Text, messageType);
            Assert.True(endOfMessage);
            sentPayloads.Add(Encoding.UTF8.GetString(buffer.Span));
            return ValueTask.CompletedTask;
        }

        public ValueTask<GitHubAliveSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (messages.Count == 0)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var message = messages.Dequeue();
            message.Payload.AsMemory().CopyTo(buffer);
            return ValueTask.FromResult(
                new GitHubAliveSocketReceiveResult(
                    message.Payload.Length,
                    message.MessageType,
                    true,
                    message.CloseStatus));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSendSocket : IGitHubAliveSocket
    {
        private readonly TaskCompletionSource<bool> sendStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendStarted => sendStarted.Task;

        public bool Disposed { get; private set; }

        public async ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            sendStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask<GitHubAliveSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The receive path must not run before subscription completes.");

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
