using System.Net;
using System.Net.Http;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitCredentialHostClassifierTests
{
    [Fact]
    public async Task FastPathsClassifyWithoutNetworkDiscovery()
    {
        using var handler = new RecordingHandler(
            (_, _) => throw new InvalidOperationException("Fast path made a request."));

        Assert.Equal(
            GitCredentialHostKind.GitHub,
            await GitCredentialHostClassifier.ClassifyAsync(
                "https://github.com/test/repo.git",
                handler));
        Assert.Equal(
            GitCredentialHostKind.GitHub,
            await GitCredentialHostClassifier.ClassifyAsync(
                "https://engineering.github.example.test/test/repo.git",
                handler));
        Assert.Equal(
            GitCredentialHostKind.Generic,
            await GitCredentialHostClassifier.ClassifyAsync(
                "https://gitlab.example.test/test/repo.git",
                handler));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task UnknownHostUsesCredentialFreeHeadAndGitHubHeader()
    {
        using var handler = new RecordingHandler(
            (request, _) =>
            {
                Assert.Equal(HttpMethod.Head, request.Method);
                Assert.Equal("enterprise.example.test", request.RequestUri!.Host);
                Assert.Equal("/api/v3/meta", request.RequestUri.AbsolutePath);
                Assert.StartsWith("?ghd=", request.RequestUri.Query, StringComparison.Ordinal);
                Assert.DoesNotContain("synthetic-secret", request.RequestUri.AbsoluteUri, StringComparison.Ordinal);
                Assert.Null(request.Headers.Authorization);
                Assert.False(request.Headers.Contains("Cookie"));

                var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                response.Headers.TryAddWithoutValidation(
                    "x-github-request-id",
                    "synthetic-request-id");
                return Task.FromResult(response);
            });

        var kind = await GitCredentialHostClassifier.ClassifyAsync(
            "https://synthetic-user:synthetic-secret@enterprise.example.test/org/repo.git",
            handler);

        Assert.Equal(GitCredentialHostKind.GitHub, kind);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task UnknownHostWithoutGitHubHeaderIsGenericAndNetworkFailureIsUnknown()
    {
        using var genericHandler = new RecordingHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var generic = await GitCredentialHostClassifier.ClassifyAsync(
            "https://forge.example.test/org/repo.git",
            genericHandler);

        using var failingHandler = new RecordingHandler(
            (_, _) => throw new HttpRequestException("synthetic network failure"));
        var unknown = await GitCredentialHostClassifier.ClassifyAsync(
            "https://unreachable.example.test/org/repo.git",
            failingHandler);

        Assert.Equal(GitCredentialHostKind.Generic, generic);
        Assert.Equal(GitCredentialHostKind.Unknown, unknown);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder;
        private int callCount;

        public RecordingHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        public int CallCount => callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            return responder(request, cancellationToken);
        }
    }
}
