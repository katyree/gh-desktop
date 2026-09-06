using System.Net;
using System.Text;
using System.Text.Json;
using WinGit.Core.GitHub;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitHubPullRequestChecksClientTests
{
    [Fact]
    public async Task LoadsStatusContextsAndCheckRunsForHeadSha()
    {
        var headSha = new string('a', 40);
        var requestedUris = new List<string>();
        var handler = new RoutingHandler(
            (request, _, _) =>
            {
                requestedUris.Add(request.RequestUri!.AbsoluteUri);
                Assert.Equal(
                    "Bearer synthetic-checks-token",
                    request.Headers.Authorization?.ToString());
                if (request.RequestUri.AbsolutePath.EndsWith(
                        "/status",
                        StringComparison.Ordinal))
                {
                    var page = IsPage(request.RequestUri, 1)
                        ? BuildStatusPageJson(
                            headSha,
                            totalCount: 101,
                            count: 100,
                            firstId: 1)
                        : BuildStatusPageJson(
                            headSha,
                            totalCount: 101,
                            count: 1,
                            firstId: 101);
                    return Task.FromResult(JsonResponse(page));
                }

                if (request.RequestUri.AbsolutePath.EndsWith(
                        "/check-runs",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        JsonResponse(
                            BuildCheckRunsPageJson(
                                headSha,
                                count: 3,
                                totalCount: 3)));
                }

                throw new InvalidOperationException(
                    "Unexpected synthetic checks request.");
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubPullRequestChecksClient(
            GitHubPullRequestChecksClientOptions.ForGitHubCom(),
            httpClient);
        var session = CreateSession(
            "synthetic-checks-token",
            new Uri("https://github.com"));
        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "https://github.com/org/repo.git",
                out var repository));
        Assert.NotNull(repository);

        var result = await client.LoadAsync(session, repository!, headSha);

        Assert.True(result.IsComplete);
        Assert.Null(result.ErrorKind);
        Assert.Equal(GitHubCommitStatusState.Pending, result.LegacyCombinedState);
        Assert.Equal(2, result.Status.PagesFetched);
        Assert.Equal(101, result.Status.TotalCount);
        Assert.Equal(101, result.Status.Statuses.Count);
        var pending = Assert.Single(
            result.Status.Statuses,
            status => status.Id == 1);
        Assert.Equal(GitHubCommitStatusState.Pending, pending.State);
        Assert.Null(pending.TargetUrl);
        Assert.Equal("Waiting for CI", pending.Description);
        Assert.Equal(1, result.CheckRuns.PagesFetched);
        Assert.Equal(3, result.CheckRuns.TotalCount);
        Assert.Equal(3, result.CheckRuns.CheckRuns.Count);
        var queued = Assert.Single(
            result.CheckRuns.CheckRuns,
            check => check.Id == 1);
        Assert.Equal(GitHubCheckRunStatusKind.Queued, queued.Status);
        Assert.Null(queued.Conclusion);
        Assert.Null(queued.UnknownConclusion);
        var success = Assert.Single(
            result.CheckRuns.CheckRuns,
            check => check.Id == 2);
        Assert.Equal(
            GitHubCheckRunConclusionKind.Success,
            success.Conclusion);
        var unknown = Assert.Single(
            result.CheckRuns.CheckRuns,
            check => check.Id == 3);
        Assert.Equal(
            GitHubCheckRunConclusionKind.Unknown,
            unknown.Conclusion);
        Assert.Equal("future_conclusion", unknown.UnknownConclusion);
        Assert.Equal(
            new Uri("https://github.com/org/repo/runs/2"),
            success.DetailsUrl);
        var expectedUris = new[]
        {
            "https://api.github.com/repos/org/repo/commits/" +
                headSha +
                "/status?per_page=100&page=1",
            "https://api.github.com/repos/org/repo/commits/" +
                headSha +
                "/check-runs?per_page=100&page=1",
            "https://api.github.com/repos/org/repo/commits/" +
                headSha +
                "/status?per_page=100&page=2",
        }.OrderBy(uri => uri).ToArray();
        Assert.Equal(expectedUris, requestedUris.OrderBy(uri => uri).ToArray());
    }

    [Fact]
    public async Task PreservesPartialCheckRunsAndRejectsMismatchAndCancellation()
    {
        var headSha = new string('b', 40);
        var handler = new RoutingHandler(
            (request, _, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(
                        "/status",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        JsonResponse(
                            BuildStatusPageJson(
                                headSha,
                                totalCount: 0,
                                count: 0,
                                firstId: 1)));
                }

                if (request.RequestUri.AbsolutePath.EndsWith(
                        "/check-runs",
                        StringComparison.Ordinal) &&
                    IsPage(request.RequestUri, 1))
                {
                    return Task.FromResult(
                        JsonResponse(
                            BuildCheckRunsPageJson(
                                headSha,
                                count: 100,
                                totalCount: 101)));
                }

                var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        "synthetic private checks response",
                        Encoding.UTF8,
                        "application/json"),
                };
                return Task.FromResult(response);
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubPullRequestChecksClient(
            GitHubPullRequestChecksClientOptions.ForGitHubCom(),
            httpClient);
        var session = CreateSession(
            "synthetic-checks-token",
            new Uri("https://github.com"));
        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "https://github.com/org/repo.git",
                out var repository));
        Assert.NotNull(repository);

        var partial = await client.LoadAsync(session, repository!, headSha);

        Assert.False(partial.IsComplete);
        Assert.Null(partial.LegacyCombinedState);
        Assert.True(partial.Status.IsComplete);
        Assert.Empty(partial.Status.Statuses);
        Assert.False(partial.CheckRuns.IsComplete);
        Assert.Equal(
            GitHubPullRequestChecksErrorKind.Forbidden,
            partial.CheckRuns.ErrorKind);
        Assert.Equal(100, partial.CheckRuns.CheckRuns.Count);

        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "https://enterprise.example/org/repo.git",
                out var foreignRepository));
        var mismatch = await Assert.ThrowsAsync<GitHubPullRequestChecksException>(
            () => client.LoadAsync(session, foreignRepository!, headSha));
        Assert.Equal(
            GitHubPullRequestChecksErrorKind.SessionMismatch,
            mismatch.Kind);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<GitHubPullRequestChecksCancelledException>(
            () => client.LoadAsync(
                session,
                repository!,
                headSha,
                cancellation.Token));
    }

    [Fact]
    public async Task LoadsSelectedActionsJobStepsAndRerunsThatJob()
    {
        var headSha = new string('c', 40);
        var requestedRequests = new List<(HttpMethod Method, string Uri)>();
        var handler = new RoutingHandler(
            (request, _, _) =>
            {
                requestedRequests.Add(
                    (request.Method, request.RequestUri!.AbsoluteUri));
                if (request.Method == HttpMethod.Post)
                {
                    Assert.EndsWith(
                        "/repos/org/repo/actions/jobs/2/rerun",
                        request.RequestUri.AbsolutePath,
                        StringComparison.Ordinal);
                    return Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.Created));
                }

                if (request.RequestUri.AbsolutePath.EndsWith(
                        "/status",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        JsonResponse(
                            BuildStatusPageJson(
                                headSha,
                                totalCount: 0,
                                count: 0,
                                firstId: 1)));
                }

                if (request.RequestUri.AbsolutePath.EndsWith(
                        "/check-runs",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        JsonResponse(
                            BuildCheckRunsPageJson(
                                headSha,
                                count: 3,
                                totalCount: 3)));
                }

                if (request.RequestUri.AbsolutePath.EndsWith(
                        "/actions/runs",
                        StringComparison.Ordinal))
                {
                    Assert.False(
                        request.RequestUri.Query.Contains(
                            "event=",
                            StringComparison.OrdinalIgnoreCase));
                    return Task.FromResult(
                        JsonResponse(
                            JsonSerializer.Serialize(
                                new
                                {
                                    total_count = 1,
                                    workflow_runs = new[]
                                    {
                                        new
                                        {
                                            id = 900L,
                                            check_suite_id = 101L,
                                            head_sha = headSha,
                                        },
                                    },
                                })));
                }

                if (request.RequestUri.AbsolutePath.EndsWith(
                        "/actions/runs/900/jobs",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        JsonResponse(
                            BuildWorkflowJobsPageJson(
                                headSha,
                                totalCount: 101,
                                count: IsPage(request.RequestUri, 1)
                                    ? 100
                                    : 1,
                                firstId: IsPage(request.RequestUri, 1)
                                    ? 1_000
                                    : 2,
                                includeSelected: !IsPage(
                                    request.RequestUri,
                                    1))));
                }

                throw new InvalidOperationException(
                    "Unexpected synthetic workflow request.");
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubPullRequestChecksClient(
            GitHubPullRequestChecksClientOptions.ForGitHubCom(),
            httpClient);
        var session = CreateSession(
            "synthetic-checks-token",
            new Uri("https://github.com"));
        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "https://github.com/org/repo.git",
                out var repository));
        Assert.NotNull(repository);

        var checks = await client.LoadAsync(session, repository!, headSha);
        var checkRun = Assert.Single(
            checks.CheckRuns.CheckRuns,
            candidate => candidate.Id == 2);

        var mismatchedHead = new string('d', 40);
        await Assert.ThrowsAsync<GitHubPullRequestChecksException>(
            () => client.LoadJobStepsAsync(
                session,
                repository!,
                checkRun,
                mismatchedHead));

        var steps = await client.LoadJobStepsAsync(
            session,
            repository!,
            checkRun,
            headSha);

        Assert.True(steps.IsComplete);
        Assert.True(steps.IsSupported);
        Assert.Equal(2, steps.ActionsJobId);
        Assert.Equal(2, steps.Steps.Count);
        Assert.Equal("Build", steps.Steps[0].Name);
        Assert.Equal(
            GitHubCheckRunConclusionKind.Failure,
            steps.Steps[0].Conclusion);
        Assert.Equal(GitHubCheckRunStatusKind.Queued, steps.Steps[1].Status);
        Assert.Null(steps.Steps[1].Conclusion);

        var rerun = await client.RerunAsync(
            session,
            repository!,
            checkRun,
            headSha,
            steps.ActionsJobId);

        Assert.True(rerun.Succeeded);
        Assert.Equal(
            GitHubPullRequestChecksRerunTargetKind.ActionsJob,
            rerun.Target);
        Assert.Contains(
            requestedRequests,
            request => request.Method == HttpMethod.Post &&
                request.Uri.EndsWith(
                    "/repos/org/repo/actions/jobs/2/rerun",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task RerunUsesCheckSuiteForNonActionsCheck()
    {
        var headSha = new string('e', 40);
        var postUris = new List<string>();
        var handler = new RoutingHandler(
            (request, _, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    postUris.Add(request.RequestUri!.AbsoluteUri);
                    return Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.Created));
                }

                if (request.RequestUri!.AbsolutePath.EndsWith(
                        "/status",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        JsonResponse(
                            BuildStatusPageJson(
                                headSha,
                                totalCount: 0,
                                count: 0,
                                firstId: 1)));
                }

                if (request.RequestUri.AbsolutePath.EndsWith(
                        "/check-runs",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        JsonResponse(
                            BuildCheckRunsPageJson(
                                headSha,
                                count: 3,
                                totalCount: 3)));
                }

                throw new InvalidOperationException(
                    "Unexpected synthetic rerun request.");
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubPullRequestChecksClient(
            GitHubPullRequestChecksClientOptions.ForGitHubCom(),
            httpClient);
        var session = CreateSession(
            "synthetic-checks-token",
            new Uri("https://github.com"));
        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "https://github.com/org/repo.git",
                out var repository));
        Assert.NotNull(repository);

        var checks = await client.LoadAsync(session, repository!, headSha);
        var checkRun = Assert.Single(
            checks.CheckRuns.CheckRuns,
            candidate => candidate.Id == 2);

        var rerun = await client.RerunAsync(
            session,
            repository!,
            checkRun,
            headSha,
            actionsJobId: null);

        Assert.True(rerun.Succeeded);
        Assert.Equal(
            GitHubPullRequestChecksRerunTargetKind.CheckSuite,
            rerun.Target);
        Assert.Contains(
            postUris,
            uri => uri.EndsWith(
                "/repos/org/repo/check-suites/101/rerequest",
                StringComparison.Ordinal));
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

    private static string BuildStatusPageJson(
        string headSha,
        int totalCount,
        int count,
        long firstId)
    {
        var statuses = Enumerable.Range(0, count)
            .Select(index => new
            {
                id = firstId + index,
                state = index == 0 && firstId == 1 ? "pending" : "success",
                context = $"ci/{firstId + index}",
                description = index == 0 && firstId == 1
                    ? "Waiting for CI"
                    : "Passed",
                target_url = index == 0 && firstId == 1
                    ? null
                    : $"https://ci.example.test/build/{firstId + index}",
                created_at = "2026-09-01T12:00:00Z",
                updated_at = "2026-09-01T12:05:00Z",
            })
            .ToArray();
        return JsonSerializer.Serialize(
            new
            {
                state = "pending",
                total_count = totalCount,
                statuses,
                sha = headSha,
            });
    }

    private static string BuildCheckRunsPageJson(
        string headSha,
        int count,
        int totalCount)
    {
        var checkRuns = Enumerable.Range(0, count)
            .Select(index => new
            {
                id = index + 1L,
                head_sha = headSha,
                name = $"check-{index + 1}",
                status = count == 3 && index == 0
                    ? "queued"
                    : "completed",
                conclusion = count == 3 && index == 0
                    ? null
                    : count == 3 && index == 1
                        ? "success"
                        : count == 3 && index == 2
                            ? "future_conclusion"
                            : "success",
                html_url = $"https://github.com/org/repo/runs/{index + 1}",
                started_at = "2026-09-01T12:00:00Z",
                completed_at = count == 3 && index == 0
                    ? null
                    : "2026-09-01T12:05:00Z",
                output = new
                {
                    summary = count == 3 && index == 1
                        ? "Build passed"
                        : (string?)null,
                },
                app = new { name = "Synthetic CI" },
                check_suite = new { id = 100L + index },
            })
            .ToArray();
        return JsonSerializer.Serialize(
            new
            {
                total_count = totalCount,
                check_runs = checkRuns,
            });
    }

    private static string BuildWorkflowJobsPageJson(
        string headSha,
        int totalCount,
        int count,
        long firstId,
        bool includeSelected)
    {
        var jobs = Enumerable.Range(0, count)
            .Select(index => (object)new
            {
                id = firstId + index,
                head_sha = headSha,
                steps = Array.Empty<object>(),
            })
            .ToList();
        if (includeSelected)
        {
            jobs[0] = new
            {
                id = 2L,
                head_sha = headSha,
                steps = new object[]
                {
                    new
                    {
                        name = "Build",
                        number = 1,
                        status = "completed",
                        conclusion = "failure",
                        started_at = "2026-09-01T12:00:00Z",
                        completed_at = "2026-09-01T12:05:00Z",
                    },
                    new
                    {
                        name = "Upload diagnostics",
                        number = 2,
                        status = "queued",
                        conclusion = (string?)null,
                        started_at = (string?)null,
                        completed_at = (string?)null,
                    },
                },
            };
        }

        return JsonSerializer.Serialize(
            new
            {
                total_count = totalCount,
                jobs,
            });
    }

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                content,
                Encoding.UTF8,
                "application/json"),
        };

    private static bool IsPage(Uri uri, int page) =>
        uri.Query.EndsWith(
            $"&page={page}",
            StringComparison.Ordinal);

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            int,
            CancellationToken,
            Task<HttpResponseMessage>> responder;
        private int callCount;

        public RoutingHandler(
            Func<
                HttpRequestMessage,
                int,
                CancellationToken,
                Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref callCount);
            return responder(request, call, cancellationToken);
        }
    }
}
