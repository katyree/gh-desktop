using System.Net;
using System.Text;
using WinGit.Core.GitHub;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitHubDeviceAuthorizationClientTests
{
    [Fact]
    public async Task DeviceFlowHonorsPendingAndSlowDownIntervalsBeforeReturningToken()
    {
        var delays = new List<TimeSpan>();
        var handler = new SequenceHandler(
            async (request, call, cancellationToken) =>
            {
                var body = await request.Content!
                    .ReadAsStringAsync(cancellationToken);
                Assert.Equal(
                    call == 1
                        ? "https://github.example.test/login/device/code"
                        : "https://github.example.test/login/oauth/access_token",
                    request.RequestUri!.AbsoluteUri);
                Assert.Contains("client_id=test-public-client", body);
                Assert.DoesNotContain("client_secret", body, StringComparison.Ordinal);

                return call switch
                {
                    1 => JsonResponse(
                        """
                        {"device_code":"test-device-code","user_code":"ABCD-1234","verification_uri":"https://github.example.test/login/device","expires_in":900,"interval":5}
                        """),
                    2 => JsonResponse("{\"error\":\"authorization_pending\"}"),
                    3 => JsonResponse("{\"error\":\"slow_down\"}"),
                    4 => JsonResponse(
                        "{\"access_token\":\"test-access-token\",\"token_type\":\"bearer\",\"scope\":\"repo user workflow\"}"),
                    _ => throw new InvalidOperationException("Unexpected synthetic request."),
                };
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubDeviceAuthorizationClient(
            new GitHubDeviceAuthorizationClientOptions(
                "test-public-client",
                new Uri("https://github.example.test")),
            httpClient,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var authorization = await client.StartDeviceAuthorizationAsync();
        var token = await client.PollAsync(authorization);

        Assert.Equal("ABCD-1234", authorization.UserCode);
        Assert.Equal(
            new Uri("https://github.example.test/login/device"),
            authorization.VerificationUri);
        Assert.Equal(TimeSpan.FromSeconds(900), authorization.ExpiresIn);
        Assert.Equal(TimeSpan.FromSeconds(5), authorization.PollingInterval);
        Assert.Equal("test-access-token", token.AccessToken);
        Assert.Equal("bearer", token.TokenType);
        Assert.Equal("repo user workflow", token.Scope);
        Assert.Equal(
            new Uri("https://github.example.test/"),
            token.IssuerHost);
        Assert.DoesNotContain("test-access-token", token.ToString());
        Assert.Equal(
            [
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
            ],
            delays);
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task DeviceFlowMapsDenialRejectsCrossClientSessionsAndHonorsCancellation()
    {
        var denialHandler = new SequenceHandler(
            (_, call, _) =>
                Task.FromResult(
                    call == 1
                        ? JsonResponse(
                            """
                            {"device_code":"denied-device","user_code":"DENY-1234","verification_uri":"https://github.example.test/login/device","expires_in":900,"interval":1}
                            """ )
                        : JsonResponse("{\"error\":\"access_denied\"}")));
        using var denialHttpClient = new HttpClient(denialHandler);
        using var denialClient = new GitHubDeviceAuthorizationClient(
            new GitHubDeviceAuthorizationClientOptions(
                "test-public-client",
                new Uri("https://github.example.test")),
            denialHttpClient,
            (_, _) => Task.CompletedTask);
        var deniedAuthorization =
            await denialClient.StartDeviceAuthorizationAsync();
        var denied = await Assert.ThrowsAsync<
            GitHubDeviceAuthorizationException>(
            () => denialClient.PollAsync(deniedAuthorization));
        Assert.Equal(GitHubDeviceFlowErrorKind.AccessDenied, denied.Kind);

        var ownerHandler = new SequenceHandler(
            (_, _, _) =>
                Task.FromResult(
                    JsonResponse(
                        """
                        {"device_code":"owner-device","user_code":"OWNR-1234","verification_uri":"https://github.example.test/login/device","expires_in":900,"interval":1}
                        """)));
        using var ownerHttpClient = new HttpClient(ownerHandler);
        using var ownerClient = new GitHubDeviceAuthorizationClient(
            new GitHubDeviceAuthorizationClientOptions(
                "test-public-client",
                new Uri("https://github.example.test")),
            ownerHttpClient,
            (_, _) => Task.CompletedTask);
        var ownerAuthorization = await ownerClient.StartDeviceAuthorizationAsync();

        var otherHandler = new SequenceHandler(
            (_, _, _) =>
                Task.FromResult(JsonResponse("{}")));
        using var otherHttpClient = new HttpClient(otherHandler);
        using var otherClient = new GitHubDeviceAuthorizationClient(
            new GitHubDeviceAuthorizationClientOptions(
                "other-public-client",
                new Uri("https://other.github.example.test")),
            otherHttpClient,
            (_, _) => Task.CompletedTask);
        var crossClient = await Assert.ThrowsAsync<
            GitHubDeviceAuthorizationException>(
            () => otherClient.PollAsync(ownerAuthorization));
        Assert.Equal(
            GitHubDeviceFlowErrorKind.InvalidConfiguration,
            crossClient.Kind);
        Assert.Equal(0, otherHandler.CallCount);

        using var cancellation = new CancellationTokenSource();
        var cancellationHandler = new SequenceHandler(
            (_, call, _) =>
                Task.FromResult(
                    call == 1
                        ? JsonResponse(
                            """
                            {"device_code":"cancel-device","user_code":"CANC-1234","verification_uri":"https://github.example.test/login/device","expires_in":900,"interval":1}
                            """ )
                        : throw new InvalidOperationException("Polling should be cancelled before a second request.")));
        using var cancellationHttpClient = new HttpClient(cancellationHandler);
        using var cancellationClient = new GitHubDeviceAuthorizationClient(
            new GitHubDeviceAuthorizationClientOptions(
                "test-public-client",
                new Uri("https://github.example.test")),
            cancellationHttpClient,
            (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            });
        var cancellationAuthorization =
            await cancellationClient.StartDeviceAuthorizationAsync();
        var cancelled = await Assert.ThrowsAsync<
            GitHubDeviceAuthorizationCancelledException>(
            () => cancellationClient.PollAsync(
                cancellationAuthorization,
                cancellation.Token));
        Assert.IsType<GitHubDeviceAuthorizationCancelledException>(cancelled);
        Assert.Equal(1, cancellationHandler.CallCount);

        var timeoutHandler = new SequenceHandler(
            (_, call, _) =>
                Task.FromResult(
                    call == 1
                        ? new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new BlockingResponseContent(),
                        }
                        : throw new InvalidOperationException(
                            "The stalled response should not be requested twice.")));
        using var timeoutHttpClient = new HttpClient(timeoutHandler);
        using var timeoutClient = new GitHubDeviceAuthorizationClient(
            new GitHubDeviceAuthorizationClientOptions(
                "test-public-client",
                new Uri("https://github.example.test"),
                requestTimeout: TimeSpan.FromMilliseconds(50)),
            timeoutHttpClient,
            (_, _) => Task.CompletedTask);
        var timedOut = await Assert.ThrowsAsync<
            GitHubDeviceAuthorizationException>(
            () => timeoutClient.StartDeviceAuthorizationAsync());
        Assert.Equal(GitHubDeviceFlowErrorKind.Timeout, timedOut.Kind);
        Assert.Equal(1, timeoutHandler.CallCount);
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

    private sealed class BlockingResponseContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new CancellationAwareReadStream());
    }

    private sealed class CancellationAwareReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(WaitForCancellationAsync(cancellationToken));

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WaitForCancellationAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private static async Task<int> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
