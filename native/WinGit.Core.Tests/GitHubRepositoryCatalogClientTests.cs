using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WinGit.Core.GitHub;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitHubRepositoryCatalogClientTests
{
    [Fact]
    public async Task ListRepositoriesStreamsPagesAndMapsMetadata()
    {
        var requestedUris = new List<string>();
        var callbackCounts = new List<int>();
        var firstPage = BuildPageJson(
            count: 100,
            firstId: 1_000,
            host: "octocorp.ghe.com",
            nullOwnerIndex: 50,
            sshUser: "octocorp",
            sshHost: "octocorp.ghe.com");
        var secondPage = BuildPageJson(
            count: 1,
            firstId: 2_000,
            host: "octocorp.ghe.com",
            sshUser: "octocorp",
            sshHost: "octocorp.ghe.com");
        var handler = new SequenceHandler(
            (request, call, _) =>
            {
                requestedUris.Add(request.RequestUri!.AbsoluteUri);
                Assert.Equal(
                    "Bearer synthetic-catalog-token",
                    request.Headers.Authorization?.ToString());
                Assert.Equal(
                    "application/vnd.github+json",
                    request.Headers.Accept.Single().MediaType);
                Assert.Equal(
                    GitHubAuthenticatedProfileClientOptions.RestApiVersion,
                    request.Headers.GetValues("X-GitHub-Api-Version").Single());
                var response = call switch
                {
                    1 => JsonResponse(firstPage),
                    2 => JsonResponse(secondPage),
                    _ => throw new InvalidOperationException(
                        "Unexpected synthetic repository request."),
                };
                if (call == 1)
                {
                    response.Headers.TryAddWithoutValidation(
                        "Link",
                        "<https://evil.invalid/user/repos?page=2>; rel=\"next\"");
                }

                return Task.FromResult(response);
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubRepositoryCatalogClient(
            GitHubRepositoryCatalogClientOptions.ForEnterprise(
                new Uri("https://octocorp.ghe.com")),
            httpClient);
        var session = CreateSession(
            "synthetic-catalog-token",
            new Uri("https://octocorp.ghe.com"));

        var result = await client.ListAsync(
            session,
            page => callbackCounts.Add(page.Count));

        Assert.True(result.IsComplete);
        Assert.Null(result.ErrorKind);
        Assert.Equal(2, result.PagesFetched);
        Assert.Equal(100, result.Repositories.Count);
        Assert.Equal([99, 1], callbackCounts);
        Assert.DoesNotContain(result.Repositories, repository => repository.Id == 1_050);
        var first = Assert.Single(
            result.Repositories,
            repository => repository.Id == 1_000);
        Assert.Equal("org", first.OwnerLogin);
        Assert.True(first.IsPrivate);
        Assert.False(first.IsFork);
        Assert.False(first.IsArchived);
        Assert.Equal("main", first.DefaultBranch);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-01T12:30:00Z"),
            first.PushedAt);
        Assert.Equal(
            new Uri("https://octocorp.ghe.com/org/repo-1000"),
            first.HtmlUrl);
        Assert.Equal(
            new Uri("https://octocorp.ghe.com/org/repo-1000.git"),
            first.HttpsCloneUrl);
        Assert.Equal(
            "octocorp@octocorp.ghe.com:org/repo-1000.git",
            first.SshCloneUrl);
        Assert.Equal(
            [
                "https://api.octocorp.ghe.com/user/repos?visibility=all&affiliation=owner,collaborator,organization_member&per_page=100&page=1",
                "https://api.octocorp.ghe.com/user/repos?visibility=all&affiliation=owner,collaborator,organization_member&per_page=100&page=2",
            ],
            requestedUris);
    }

    [Fact]
    public async Task ListRepositoriesReturnsPartialErrorsAndHonorsCancellation()
    {
        var firstPage = BuildPageJson(
            count: 100,
            firstId: 3_000,
            host: "github.com");
        var errorHandler = new SequenceHandler(
            (request, call, _) =>
            {
                Assert.Contains(
                    $"page={call}",
                    request.RequestUri!.Query,
                    StringComparison.Ordinal);
                if (call == 1)
                {
                    return Task.FromResult(JsonResponse(firstPage));
                }

                var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        "synthetic private rate-limit response",
                        Encoding.UTF8,
                        "application/json"),
                };
                response.Headers.TryAddWithoutValidation(
                    "X-RateLimit-Remaining",
                    "0");
                return Task.FromResult(response);
            });
        using var errorHttpClient = new HttpClient(errorHandler);
        using var errorClient = new GitHubRepositoryCatalogClient(
            GitHubRepositoryCatalogClientOptions.ForGitHubCom(),
            errorHttpClient);
        var session = CreateSession(
            "synthetic-catalog-token",
            new Uri("https://github.com"));

        var partial = await errorClient.ListAsync(session);

        Assert.False(partial.IsComplete);
        Assert.Equal(
            GitHubRepositoryCatalogErrorKind.RateLimited,
            partial.ErrorKind);
        Assert.Equal(1, partial.PagesFetched);
        Assert.Equal(100, partial.Repositories.Count);
        Assert.DoesNotContain(
            partial.ToString(),
            "synthetic private rate-limit response",
            StringComparison.Ordinal);

        var cancellationHandler = new SequenceHandler(
            (_, _, _) => throw new InvalidOperationException(
                "A cancelled catalog must not send a request."));
        using var cancellationHttpClient = new HttpClient(cancellationHandler);
        using var cancellationClient = new GitHubRepositoryCatalogClient(
            GitHubRepositoryCatalogClientOptions.ForGitHubCom(),
            cancellationHttpClient);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<GitHubRepositoryCatalogCancelledException>(
            () => cancellationClient.ListAsync(session, cancellationToken: cancellation.Token));
        Assert.Equal(0, cancellationHandler.CallCount);
    }

    private static GitHubAccountSession CreateSession(
        string accessToken,
        Uri issuerHost) =>
        GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                accessToken,
                "bearer",
                "repo user workflow",
                issuerHost));

    private static string BuildPageJson(
        int count,
        long firstId,
        string host,
        int nullOwnerIndex = -1,
        string sshUser = "git",
        string? sshHost = null)
    {
        var repositories = Enumerable.Range(0, count)
            .Select(index =>
            {
                var id = firstId + index;
                var owner = index == nullOwnerIndex
                    ? null
                    : new { login = "org" };
                return new
                {
                    id,
                    name = $"repo-{id}",
                    owner,
                    @private = index == 0,
                    fork = false,
                    archived = false,
                    default_branch = index == 1 ? (string?)null : "main",
                    pushed_at = index == 2
                        ? (string?)null
                        : "2026-09-01T12:30:00Z",
                    html_url = $"https://{host}/org/repo-{id}",
                    clone_url = $"https://{host}/org/repo-{id}.git",
                    ssh_url = $"{sshUser}@{sshHost ?? $"ssh.{host}"}:org/repo-{id}.git",
                };
            })
            .ToArray();
        return JsonSerializer.Serialize(repositories);
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
}
