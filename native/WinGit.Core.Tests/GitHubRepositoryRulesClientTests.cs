using System.Net;
using System.Text;
using System.Text.Json;
using WinGit.Core.GitHub;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitHubRepositoryRulesClientTests
{
    [Fact]
    public async Task BranchRulesEscapeSlashAndResolvePatternsWithPartialMetadata()
    {
        var requestedUris = new List<string>();
        var firstPage = BuildFullBranchPage();
        var secondPage = JsonSerializer.Serialize(
            new object[]
            {
                new
                {
                    type = "commit_message_pattern",
                    ruleset_id = 99,
                    parameters = new
                    {
                        @operator = "contains",
                        pattern = "signed",
                        negate = false,
                    },
                },
            });
        var handler = new SequenceHandler(
            (request, call, _) =>
            {
                requestedUris.Add(request.RequestUri!.AbsoluteUri);
                Assert.Equal(
                    "Bearer synthetic-rules-token",
                    request.Headers.Authorization?.ToString());
                if (call <= 2)
                {
                    Assert.Contains(
                        "per_page=100",
                        request.RequestUri!.Query,
                        StringComparison.Ordinal);
                    Assert.Contains(
                        $"page={call}",
                        request.RequestUri.Query,
                        StringComparison.Ordinal);
                }
                return call switch
                {
                    1 => Task.FromResult(JsonResponse(firstPage)),
                    2 => Task.FromResult(JsonResponse(secondPage)),
                    3 => Task.FromResult(JsonResponse(
                        "{\"id\":42,\"current_user_can_bypass\":\"always\"}")),
                    4 => Task.FromResult(JsonResponse(
                        "{\"id\":73,\"current_user_can_bypass\":\"never\"}")),
                    5 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(
                            "private ruleset details",
                            Encoding.UTF8,
                            "application/json"),
                    }),
                    _ => throw new InvalidOperationException(
                        "Unexpected synthetic rules request."),
                };
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubRepositoryRulesClient(
            GitHubRepositoryRulesClientOptions.ForGitHubCom(),
            httpClient);
        var session = CreateSession(
            "synthetic-rules-token",
            new Uri("https://github.com"));

        var result = await client.GetForBranchAsync(
            session,
            "octo-org",
            "sample-repo",
            "feature/review");

        Assert.Equal(
            GitHubRepositoryRulesAvailability.Partial,
            result.Availability);
        Assert.Equal(
            GitHubRepositoryRulesErrorKind.NotFound,
            result.ErrorKind);
        Assert.Equal(new Uri("https://api.github.com/"), result.ApiOrigin);
        Assert.Equal("octo-org", result.Owner);
        Assert.Equal("sample-repo", result.RepositoryName);
        Assert.Equal("feature/review", result.Branch);
        Assert.Equal(2, result.Rules.Count);

        var prefix = Assert.Single(
            result.Rules,
            rule => rule.RulesetId == 42);
        Assert.Equal(
            GitHubCommitMessageRuleOperator.StartsWith,
            prefix.Operator);
        Assert.False(prefix.Negate);
        Assert.Equal("ABC ", prefix.Pattern);
        Assert.Equal(
            GitHubCommitMessageRuleBypassMode.Always,
            prefix.BypassMode);
        Assert.True(prefix.CanBypass);
        Assert.Equal("must start with \"ABC \"", prefix.Description);

        var negativeRegex = Assert.Single(
            result.Rules,
            rule => rule.RulesetId == 73);
        Assert.Equal(GitHubCommitMessageRuleOperator.Regex, negativeRegex.Operator);
        Assert.True(negativeRegex.Negate);
        Assert.False(negativeRegex.CanBypass);
        Assert.Equal(
            "must not match the regular expression \"^JIRA-[0-9]+$\"",
            negativeRegex.Description);

        Assert.Equal(
            [
                "https://api.github.com/repos/octo-org/sample-repo/rules/branches/feature%2Freview?per_page=100&page=1",
                "https://api.github.com/repos/octo-org/sample-repo/rules/branches/feature%2Freview?per_page=100&page=2",
                "https://api.github.com/repos/octo-org/sample-repo/rulesets/42",
                "https://api.github.com/repos/octo-org/sample-repo/rulesets/73",
                "https://api.github.com/repos/octo-org/sample-repo/rulesets/99",
            ],
            requestedUris);
    }

    [Fact]
    public async Task BranchRulesReturnSanitizedFailureAndHonorCancellation()
    {
        var failureHandler = new SequenceHandler(
            (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    "sensitive branch rules response",
                    Encoding.UTF8,
                    "application/json"),
            }));
        failureHandler.ResponseHeaders = response =>
            response.Headers.TryAddWithoutValidation(
                "X-GitHub-SSO",
                "required-org:sensitive-token");
        using var failureHttpClient = new HttpClient(failureHandler);
        using var failureClient = new GitHubRepositoryRulesClient(
            GitHubRepositoryRulesClientOptions.ForGitHubCom(),
            failureHttpClient);
        var session = CreateSession(
            "synthetic-rules-token",
            new Uri("https://github.com"));

        var unavailable = await failureClient.GetForBranchAsync(
            session,
            "octo-org",
            "sample-repo",
            "main");

        Assert.Equal(
            GitHubRepositoryRulesAvailability.Unavailable,
            unavailable.Availability);
        Assert.Equal(
            GitHubRepositoryRulesErrorKind.SamlRequired,
            unavailable.ErrorKind);
        Assert.DoesNotContain(
            unavailable.ToString(),
            "sensitive branch rules response",
            StringComparison.Ordinal);

        var cancellationHandler = new SequenceHandler(
            (_, _, _) => throw new InvalidOperationException(
                "A cancelled rules request must not be sent."));
        using var cancellationHttpClient = new HttpClient(cancellationHandler);
        using var cancellationClient = new GitHubRepositoryRulesClient(
            GitHubRepositoryRulesClientOptions.ForGitHubCom(),
            cancellationHttpClient);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<GitHubRepositoryRulesCancelledException>(
            () => cancellationClient.GetForBranchAsync(
                session,
                "octo-org",
                "sample-repo",
                "main",
                cancellation.Token));
        Assert.Equal(0, cancellationHandler.CallCount);
    }

    private static string BuildFullBranchPage()
    {
        var rules = new List<object>
        {
            new
            {
                type = "commit_message_pattern",
                ruleset_id = 42,
                parameters = new
                {
                    @operator = "starts_with",
                    pattern = "ABC ",
                },
            },
            new
            {
                type = "commit_message_pattern",
                ruleset_id = 73,
                parameters = new
                {
                    @operator = "regex",
                    pattern = "^JIRA-[0-9]+$",
                    negate = true,
                },
            },
        };
        for (var index = rules.Count; index < 100; index++)
        {
            rules.Add(new
            {
                type = "required_status_checks",
                ruleset_id = 1_000 + index,
            });
        }

        return JsonSerializer.Serialize(rules);
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

        public Action<HttpResponseMessage>? ResponseHeaders { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref callCount);
            var response = await responder(request, call, cancellationToken);
            ResponseHeaders?.Invoke(response);
            return response;
        }
    }
}
