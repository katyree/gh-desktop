using System.Net;
using System.Text;
using WinGit.Core.GitHub;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitHubRepositoryDetailClientTests
{
    [Fact]
    public async Task ParsesHttpsAndSshRemotesAndReadsHostBoundRepositoryDetail()
    {
        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "https://github.com/octocat/Hello-World.git",
                out var dotComIdentity));
        Assert.NotNull(dotComIdentity);
        Assert.Equal(
            "https://api.github.com/",
            dotComIdentity!.ApiOrigin.AbsoluteUri);
        Assert.Equal(GitHubRemoteProtocol.Https, dotComIdentity.Protocol);
        Assert.Equal("octocat", dotComIdentity.Owner);
        Assert.Equal("Hello-World", dotComIdentity.Name);

        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "octocorp@octocorp.ghe.com:org/repo.git",
                out var cloudIdentity));
        Assert.NotNull(cloudIdentity);
        Assert.Equal(
            "https://api.octocorp.ghe.com/",
            cloudIdentity!.ApiOrigin.AbsoluteUri);
        Assert.Equal(GitHubRemoteProtocol.Ssh, cloudIdentity.Protocol);

        var handler = new SequenceHandler(
            (request, _, _) =>
            {
                Assert.Equal(
                    "https://github.enterprise.test/api/v3/repos/org/repo",
                    request.RequestUri!.AbsoluteUri);
                Assert.Equal(
                    "Bearer synthetic-detail-token",
                    request.Headers.Authorization?.ToString());
                Assert.Equal(
                    "application/vnd.github+json",
                    request.Headers.Accept.Single().MediaType);
                Assert.Equal(
                    GitHubAuthenticatedProfileClientOptions.RestApiVersion,
                    request.Headers.GetValues("X-GitHub-Api-Version").Single());
                return Task.FromResult(
                    JsonResponse(
                        """
                        {
                          "id": 42,
                          "name": "repo",
                          "owner": { "login": "org" },
                          "private": true,
                          "fork": true,
                          "archived": false,
                          "default_branch": "main",
                          "pushed_at": "2026-09-01T12:30:00Z",
                          "html_url": "https://github.enterprise.test/org/repo",
                          "clone_url": "https://github.enterprise.test/org/repo.git",
                          "ssh_url": "git@github.enterprise.test:org/repo.git",
                          "parent": {
                            "id": 7,
                            "name": "repo",
                            "owner": { "login": "upstream" },
                            "private": false,
                            "fork": false,
                            "archived": false,
                            "default_branch": "main",
                            "pushed_at": null,
                            "html_url": "https://github.enterprise.test/upstream/repo",
                            "clone_url": "https://github.enterprise.test/upstream/repo.git",
                            "ssh_url": "git@github.enterprise.test:upstream/repo.git"
                          },
                          "permissions": { "admin": true, "push": true, "pull": true }
                        }
                        """));
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubRepositoryDetailClient(
            GitHubRepositoryDetailClientOptions.ForEnterprise(
                new Uri("https://github.enterprise.test")),
            httpClient);
        var session = GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                "synthetic-detail-token",
                "bearer",
                "repo user workflow",
                new Uri("https://github.enterprise.test")));
        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "git@github.enterprise.test:org/repo.git",
                out var identity));
        Assert.NotNull(identity);

        var detail = await client.ReadAsync(
            session,
            identity!,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(42, detail!.Repository.Id);
        Assert.Equal("repo", detail.Repository.Name);
        Assert.Equal("org", detail.Repository.OwnerLogin);
        Assert.True(detail.Repository.IsPrivate);
        Assert.True(detail.Repository.IsFork);
        Assert.Equal("main", detail.Repository.DefaultBranch);
        Assert.NotNull(detail.Parent);
        Assert.Equal("upstream", detail.Parent!.OwnerLogin);
        Assert.Equal(7, detail.Parent.Id);
        Assert.NotNull(detail.Permissions);
        Assert.True(detail.Permissions!.Admin);
        Assert.True(detail.Permissions.Push);
        Assert.True(detail.Permissions.Pull);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RefusesForeignRemoteAndSanitizesAuthenticatedErrors()
    {
        var handler = new SequenceHandler(
            (_, _, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        "Bearer synthetic-detail-token private server body",
                        Encoding.UTF8,
                        "application/json"),
                }));
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubRepositoryDetailClient(
            GitHubRepositoryDetailClientOptions.ForGitHubCom(),
            httpClient);
        var session = GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                "synthetic-detail-token",
                "bearer",
                "repo user workflow",
                new Uri("https://github.com")));
        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "git@github.enterprise.test:org/repo.git",
                out var foreignIdentity));
        Assert.NotNull(foreignIdentity);

        var mismatch = await Assert.ThrowsAsync<GitHubRepositoryDetailException>(
            () => client.ReadAsync(
                session,
                foreignIdentity!,
                CancellationToken.None));
        Assert.Equal(GitHubRepositoryDetailErrorKind.SessionMismatch, mismatch.Kind);
        Assert.Equal(0, handler.CallCount);

        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "https://github.com/org/repo.git",
                out var dotComIdentity));
        var unauthorized = await Assert.ThrowsAsync<GitHubRepositoryDetailException>(
            () => client.ReadAsync(
                session,
                dotComIdentity!,
                CancellationToken.None));
        Assert.Equal(GitHubRepositoryDetailErrorKind.Unauthorized, unauthorized.Kind);
        Assert.DoesNotContain("synthetic-detail-token", unauthorized.ToString());
        Assert.DoesNotContain("private server body", unauthorized.ToString());
        Assert.Equal(1, handler.CallCount);
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
