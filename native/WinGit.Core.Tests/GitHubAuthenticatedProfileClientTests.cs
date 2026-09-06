using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WinGit.Core.GitHub;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitHubAuthenticatedProfileClientTests
{
    [Fact]
    public async Task ReadUserParsesProfileAndRoutesDotComAndEnterpriseRequests()
    {
        var dotComHandler = new SequenceHandler(
            (request, _, _) =>
            {
                Assert.Equal(
                    "https://api.github.com/user",
                    request.RequestUri!.AbsoluteUri);
                Assert.Equal(
                    "Bearer synthetic-dotcom-token",
                    request.Headers.Authorization?.ToString());
                Assert.Equal(
                    "application/vnd.github+json",
                    request.Headers.Accept.Single().MediaType);
                Assert.Equal(
                    GitHubAuthenticatedProfileClientOptions.RestApiVersion,
                    request.Headers.GetValues("X-GitHub-Api-Version").Single());
                Assert.DoesNotContain(
                    "synthetic-dotcom-token",
                    request.Headers.UserAgent.ToString(),
                    StringComparison.Ordinal);
                return Task.FromResult(
                    JsonResponse(
                        """
                        {"id":12345,"login":"test-user","name":"Test User","email":"test-user@example.invalid","avatar_url":"https://avatars.githubusercontent.com/u/12345?v=4","html_url":"https://github.com/test-user","type":"User"}
                        """));
            });
        using var dotComHttpClient = new HttpClient(dotComHandler);
        using var dotComClient = new GitHubAuthenticatedProfileClient(
            GitHubAuthenticatedProfileClientOptions.ForGitHubCom(),
            dotComHttpClient);
        var dotComSession = GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                "synthetic-dotcom-token",
                "bearer",
                "repo user workflow",
                new Uri("https://github.com")));

        var dotComProfile = await dotComClient.ReadUserAsync(dotComSession);

        Assert.Equal(12345, dotComProfile.Id);
        Assert.Equal("test-user", dotComProfile.Login);
        Assert.Equal("Test User", dotComProfile.DisplayName);
        Assert.Equal("test-user@example.invalid", dotComProfile.Email);
        Assert.Equal(
            new Uri("https://avatars.githubusercontent.com/u/12345?v=4"),
            dotComProfile.AvatarUrl);
        Assert.Equal(
            new Uri("https://github.com/test-user"),
            dotComProfile.ProfileUrl);
        Assert.Equal(1, dotComHandler.CallCount);

        var enterpriseHandler = new SequenceHandler(
            (request, _, _) =>
            {
                Assert.Equal(
                    "https://api.company.test/api/v3/user",
                    request.RequestUri!.AbsoluteUri);
                Assert.Equal(
                    "Bearer synthetic-enterprise-token",
                    request.Headers.Authorization?.ToString());
                return Task.FromResult(
                    JsonResponse(
                        """
                        {"id":67890,"login":"enterprise-user","name":null,"email":null,"avatar_url":"https://cdn.untrusted.test/avatars/67890","html_url":"https://api.company.test/enterprise-user"}
                        """));
            });
        using var enterpriseHttpClient = new HttpClient(enterpriseHandler);
        using var enterpriseClient = new GitHubAuthenticatedProfileClient(
            GitHubAuthenticatedProfileClientOptions.ForEnterprise(
                new Uri("https://api.company.test")),
            enterpriseHttpClient);
        var enterpriseSession = GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                "synthetic-enterprise-token",
                "bearer",
                "repo user workflow",
                new Uri("https://api.company.test")));

        var enterpriseProfile = await enterpriseClient.ReadUserAsync(
            enterpriseSession);

        Assert.Equal(67890, enterpriseProfile.Id);
        Assert.Equal("enterprise-user", enterpriseProfile.Login);
        Assert.Null(enterpriseProfile.DisplayName);
        Assert.Null(enterpriseProfile.Email);
        Assert.Null(enterpriseProfile.AvatarUrl);
        Assert.Equal(
            new Uri("https://api.company.test/enterprise-user"),
            enterpriseProfile.ProfileUrl);
        Assert.Equal(1, enterpriseHandler.CallCount);

        var cloudHandler = new SequenceHandler(
            (request, _, _) =>
            {
                Assert.Equal(
                    "https://api.company.ghe.com/user",
                    request.RequestUri!.AbsoluteUri);
                return Task.FromResult(
                    JsonResponse(
                        """
                        {"id":24680,"login":"cloud-user","name":"Cloud User","email":null,"avatar_url":"https://avatars.githubusercontent.com/u/24680","html_url":"https://company.ghe.com/cloud-user"}
                        """));
            });
        using var cloudHttpClient = new HttpClient(cloudHandler);
        using var cloudClient = new GitHubAuthenticatedProfileClient(
            GitHubAuthenticatedProfileClientOptions.ForEnterprise(
                new Uri("https://company.ghe.com")),
            cloudHttpClient);
        var cloudSession = GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                "synthetic-cloud-token",
                "bearer",
                "repo user workflow",
                new Uri("https://company.ghe.com")));

        var cloudProfile = await cloudClient.ReadUserAsync(cloudSession);

        Assert.Equal(24680, cloudProfile.Id);
        Assert.Equal("cloud-user", cloudProfile.Login);
        Assert.Equal("Cloud User", cloudProfile.DisplayName);
        Assert.Null(cloudProfile.Email);
        Assert.Equal(
            new Uri("https://avatars.githubusercontent.com/u/24680"),
            cloudProfile.AvatarUrl);
        Assert.Equal(
            new Uri("https://company.ghe.com/cloud-user"),
            cloudProfile.ProfileUrl);
        Assert.Equal(1, cloudHandler.CallCount);
    }

    [Fact]
    public async Task ReadUserSanitizesAuthAndRateErrorsAndRefusesCancellationOrForeignSessions()
    {
        var errorHandler = new SequenceHandler(
            (_, call, _) =>
            {
                return Task.FromResult(
                    call switch
                    {
                        1 => new HttpResponseMessage(HttpStatusCode.Unauthorized)
                        {
                            Content = new StringContent(
                                "token=synthetic-secret and private details",
                                Encoding.UTF8,
                                "application/json"),
                        },
                        2 => new HttpResponseMessage(HttpStatusCode.Forbidden)
                        {
                            Headers =
                            {
                                { "X-GitHub-SSO", "required; url=https://sso.invalid/private" },
                            },
                            Content = new StringContent(
                                "SAML private response",
                                Encoding.UTF8,
                                "application/json"),
                        },
                        3 => new HttpResponseMessage(HttpStatusCode.Forbidden)
                        {
                            Content = new StringContent(
                                "ordinary private response",
                                Encoding.UTF8,
                                "application/json"),
                        },
                        4 => new HttpResponseMessage(HttpStatusCode.Forbidden)
                        {
                            Headers =
                            {
                                { "X-RateLimit-Remaining", "0" },
                            },
                            Content = new StringContent(
                                "rate limit private response",
                                Encoding.UTF8,
                                "application/json"),
                        },
                        _ => throw new InvalidOperationException(
                            "Unexpected synthetic profile request."),
                    });
            });
        using var errorHttpClient = new HttpClient(errorHandler);
        using var errorClient = new GitHubAuthenticatedProfileClient(
            GitHubAuthenticatedProfileClientOptions.ForGitHubCom(),
            errorHttpClient);
        var session = GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                "synthetic-secret",
                "bearer",
                "repo user workflow",
                new Uri("https://github.com")));

        var unauthorized = await Assert.ThrowsAsync<
            GitHubAuthenticatedProfileException>(
            () => errorClient.ReadUserAsync(session));
        Assert.Equal(
            GitHubAuthenticatedProfileErrorKind.Unauthorized,
            unauthorized.Kind);
        Assert.DoesNotContain("synthetic-secret", unauthorized.ToString());
        Assert.DoesNotContain("private details", unauthorized.ToString());

        var samlRequired = await Assert.ThrowsAsync<
            GitHubAuthenticatedProfileException>(
            () => errorClient.ReadUserAsync(session));
        Assert.Equal(
            GitHubAuthenticatedProfileErrorKind.SamlRequired,
            samlRequired.Kind);
        Assert.DoesNotContain("sso.invalid", samlRequired.ToString());

        var forbidden = await Assert.ThrowsAsync<GitHubAuthenticatedProfileException>(
            () => errorClient.ReadUserAsync(session));
        Assert.Equal(
            GitHubAuthenticatedProfileErrorKind.Forbidden,
            forbidden.Kind);

        var rateLimited = await Assert.ThrowsAsync<
            GitHubAuthenticatedProfileException>(
            () => errorClient.ReadUserAsync(session));
        Assert.Equal(
            GitHubAuthenticatedProfileErrorKind.RateLimited,
            rateLimited.Kind);
        Assert.DoesNotContain("rate limit private response", rateLimited.ToString());

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var callCountBeforeCancellation = errorHandler.CallCount;
        var cancelled = await Assert.ThrowsAsync<
            GitHubAuthenticatedProfileCancelledException>(
            () => errorClient.ReadUserAsync(session, cancellation.Token));
        Assert.IsType<GitHubAuthenticatedProfileCancelledException>(cancelled);
        Assert.Equal(callCountBeforeCancellation, errorHandler.CallCount);

        var foreignSession = GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                "synthetic-foreign-token",
                "bearer",
                "repo user workflow",
                new Uri("https://github.enterprise.test")));
        var mismatch = await Assert.ThrowsAsync<
            GitHubAuthenticatedProfileException>(
            () => errorClient.ReadUserAsync(foreignSession));
        Assert.Equal(
            GitHubAuthenticatedProfileErrorKind.SessionMismatch,
            mismatch.Kind);
        Assert.Equal(callCountBeforeCancellation, errorHandler.CallCount);
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
