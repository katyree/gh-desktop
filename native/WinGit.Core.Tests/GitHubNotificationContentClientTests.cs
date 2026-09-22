using System.Net;
using System.Text;
using System.Text.Json;
using WinGit.Core.GitHub;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitHubNotificationContentClientTests
{
    [Fact]
    public async Task ReadsIssueCommentWithFixedPullRequestTargetAndSubtypeDeduplication()
    {
        var context = CreateContext();
        var handler = new RoutingHandler(
            (request, _, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(
                        "/user/emails",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        JsonResponse(
                            """
                            [
                              { "email": "test-user@example.invalid", "verified": true },
                              { "email": "TEST-USER@example.invalid", "verified": true },
                              { "email": "other@example.invalid", "verified": false }
                            ]
                            """));
                }

                Assert.Equal(
                    "https://api.github.com/repos/octo/repo/issues/comments/42",
                    request.RequestUri!.AbsoluteUri);
                return Task.FromResult(
                    JsonResponse(
                        $$"""
                        {
                          "id": 42,
                          "user": { "login": "reviewer" },
                          "body": "First line of the comment, followed by enough text to truncate safely.",
                          "issue_url": "https://api.github.com/repos/octo/repo/issues/7",
                          "html_url": "https://evil.invalid/redirect"
                        }
                        """));
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubNotificationContentClient(
            GitHubNotificationContentClientOptions.ForGitHubCom(),
            httpClient);
        var content = await client.ReadAsync(
            CreateSession(),
            context.Repository,
            context.PullRequest,
            new GitHubNotificationEvent(
                GitHubNotificationEventKind.PullRequestComment,
                "octo",
                "repo",
                7,
                "42",
                null,
                null,
                "issue-comment",
                100));

        Assert.NotNull(content);
        Assert.Equal(
            "@reviewer commented on your pull request",
            content!.Title);
        Assert.Equal(
            "Fix notification safety #7\n" +
            "First line of the comment, followed by enough text…",
            content.Body);
        Assert.Equal(
            new Uri("https://github.com/octo/repo/pull/7"),
            content.Target);
        Assert.Equal("comment:issue-comment:octo/repo/7/42", content.DeduplicationKey);
        Assert.Empty(content.CheckRunIds);
        Assert.Empty(content.AllCheckRunIds);
        Assert.Equal(
            ["test-user@example.invalid", "other@example.invalid"],
            await client.ReadAccountEmailsAsync(CreateSession()));
    }

    [Fact]
    public async Task ReadsApprovedReviewAndRejectsUnsupportedStateOrMismatchedTarget()
    {
        var context = CreateContext();
        var handler = new RoutingHandler(
            (request, call, _) =>
            {
                Assert.Equal(
                    "https://api.github.com/repos/octo/repo/pulls/7/reviews/43",
                    request.RequestUri!.AbsoluteUri);
                return Task.FromResult(
                    JsonResponse(
                        call switch
                        {
                            1 => """
                        {
                          "id": 43,
                          "user": { "login": "reviewer" },
                          "body": "Looks good.",
                          "state": "APPROVED",
                          "pull_request_url": "https://api.github.com/repos/octo/repo/pulls/7"
                        }
                        """,
                            2 => """
                        {
                          "id": 43,
                          "user": { "login": "reviewer" },
                          "body": "Looks good.",
                          "state": "APPROVED",
                          "pull_request_url": "https://api.github.com/repos/octo/repo/pulls/8"
                        }
                        """,
                            3 => """
                        {
                          "id": 43,
                          "user": { "login": "reviewer" },
                          "body": "Looks good.",
                          "state": "DISMISSED",
                          "pull_request_url": "https://api.github.com/repos/octo/repo/pulls/7"
                        }
                        """,
                            _ => """
                        {
                          "id": 43,
                          "user": { "login": "reviewer" },
                          "body": "",
                          "state": "COMMENTED",
                          "pull_request_url": "https://api.github.com/repos/octo/repo/pulls/7"
                        }
                        """,
                        }));
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubNotificationContentClient(
            GitHubNotificationContentClientOptions.ForGitHubCom(),
            httpClient);

        var content = await client.ReadAsync(
            CreateSession(),
            context.Repository,
            context.PullRequest,
            new GitHubNotificationEvent(
                GitHubNotificationEventKind.PullRequestReview,
                "octo",
                "repo",
                7,
                "43",
                null,
                "APPROVED",
                null,
                101));

        Assert.NotNull(content);
        Assert.Equal("@reviewer approved your pull request", content!.Title);
        Assert.Equal("review:octo/repo/7/43", content.DeduplicationKey);

        var mismatchedTarget = await client.ReadAsync(
            CreateSession(),
            context.Repository,
            context.PullRequest,
            new GitHubNotificationEvent(
                GitHubNotificationEventKind.PullRequestReview,
                "octo",
                "repo",
                7,
                "43",
                null,
                "APPROVED",
                null,
                102));
        Assert.Null(mismatchedTarget);

        var unsupportedState = await client.ReadAsync(
            CreateSession(),
            context.Repository,
            context.PullRequest,
            new GitHubNotificationEvent(
                GitHubNotificationEventKind.PullRequestReview,
                "octo",
                "repo",
                7,
                "43",
                null,
                "DISMISSED",
                null,
                103));
        Assert.Null(unsupportedState);

        var emptyComment = await client.ReadAsync(
            CreateSession(),
            context.Repository,
            context.PullRequest,
            new GitHubNotificationEvent(
                GitHubNotificationEventKind.PullRequestReview,
                "octo",
                "repo",
                7,
                "43",
                null,
                "COMMENTED",
                null,
                104));
        Assert.NotNull(emptyComment);
        Assert.Equal("@reviewer reviewed your pull request", emptyComment!.Title);
        Assert.Equal("Fix notification safety #7\n", emptyComment.Body);
    }

    [Fact]
    public async Task ReadsFailedChecksForMatchingSuiteAndExposesAllRunIds()
    {
        var context = CreateContext();
        var handler = new RoutingHandler(
            (request, _, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(
                        "/user/emails",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        JsonResponse(
                            """
                            [
                              { "email": "test-user@example.invalid", "verified": true },
                              { "email": "TEST-USER@example.invalid", "verified": true },
                              { "email": "other@example.invalid", "verified": false }
                            ]
                            """));
                }

                if (request.RequestUri!.AbsolutePath.EndsWith(
                        "/status",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        JsonResponse(
                            $$"""
                            {
                              "state": "failure",
                              "total_count": 1,
                              "statuses": [
                                {
                                  "id": 13,
                                  "state": "failure",
                                  "context": "legacy-ci",
                                  "description": "Legacy CI failed",
                                  "target_url": null
                                }
                              ],
                              "sha": "{{context.HeadSha}}"
                            }
                            """));
                }

                Assert.EndsWith(
                    "/check-runs",
                    request.RequestUri.AbsolutePath,
                    StringComparison.Ordinal);
                return Task.FromResult(
                    JsonResponse(BuildCheckRunsJson(context.HeadSha)));
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubNotificationContentClient(
            GitHubNotificationContentClientOptions.ForGitHubCom(),
            httpClient);

        var content = await client.ReadAsync(
            CreateSession(),
            context.Repository,
            context.PullRequest,
            new GitHubNotificationEvent(
                GitHubNotificationEventKind.ChecksFailed,
                "octo",
                "repo",
                7,
                "42",
                context.HeadSha,
                null,
                null,
                102));

        Assert.NotNull(content);
        Assert.Equal(
            "Fix notification safety #7 (aaaaaaa)\n3 checks were not successful.",
            content!.Body);
        Assert.Equal([10L, 11L], content.CheckRunIds);
        Assert.Equal([10L, 11L, 12L, 13L], content.AllCheckRunIds);
        Assert.Contains("/42/10,11/10,11,12,13", content.DeduplicationKey, StringComparison.Ordinal);
        Assert.Equal(
            ["test-user@example.invalid", "other@example.invalid"],
            await client.ReadAccountEmailsAsync(CreateSession()));

        var stale = await client.ReadAsync(
            CreateSession(),
            context.Repository,
            context.PullRequest,
            new GitHubNotificationEvent(
                GitHubNotificationEventKind.ChecksFailed,
                "octo",
                "repo",
                7,
                "42",
                new string('b', 40),
                null,
                null,
                103));
        Assert.Null(stale);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task ReadsNotificationCommitAuthorWithoutChangingRepository()
    {
        var repositoryRoot = Path.Combine(
            Path.GetTempPath(),
            "WinGit.Core.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        try
        {
            RunGit(repositoryRoot, "init", "--initial-branch", "main");
            RunGit(repositoryRoot, "config", "user.name", "Test User");
            RunGit(repositoryRoot, "config", "user.email", "test-user@example.invalid");
            File.WriteAllText(Path.Combine(repositoryRoot, "file.txt"), "content\n");
            RunGit(repositoryRoot, "add", "--all");
            RunGit(repositoryRoot, "commit", "--message", "initial");
            var head = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
            var statusBefore = RunGit(repositoryRoot, "status", "--porcelain=v2", "-z");

            var service = new WinGit.Core.GitRepositoryService();
            var author = await service.ReadNotificationCommitAuthorEmailAsync(
                repositoryRoot,
                head,
                CancellationToken.None);

            Assert.Equal("test-user@example.invalid", author);
            Assert.Equal(statusBefore, RunGit(repositoryRoot, "status", "--porcelain=v2", "-z"));
            Assert.Null(
                await service.ReadNotificationCommitAuthorEmailAsync(
                    repositoryRoot,
                    new string('a', 40),
                    CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(
                () => service.ReadNotificationCommitAuthorEmailAsync(
                    repositoryRoot,
                    "not-a-full-sha",
                    CancellationToken.None));
        }
        finally
        {
            DeleteDirectory(repositoryRoot);
        }
    }

    private static TestContext CreateContext()
    {
        var origin = GitHubApiEndpoint.GitHubCom;
        var repository = new GitHubRepository(
            origin,
            1,
            "repo",
            "octo",
            isPrivate: false,
            isFork: false,
            isArchived: false,
            defaultBranch: "main",
            pushedAt: null,
            htmlUrl: new Uri("https://github.com/octo/repo"),
            httpsCloneUrl: new Uri("https://github.com/octo/repo.git"),
            sshCloneUrl: "git@github.com:octo/repo.git");
        var headSha = new string('a', 40);
        var head = new GitHubPullRequestRef("main", headSha, repository);
        var @base = new GitHubPullRequestRef("main", headSha, repository);
        var pullRequest = new GitHubPullRequest(
            origin,
            7,
            "Fix notification safety",
            new Uri("https://github.com/octo/repo/pull/7"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "author",
            null,
            GitHubPullRequestState.Open,
            draft: false,
            head,
            @base,
            merged: false,
            mergedAt: null,
            mergeCommitSha: null,
            squashMergeCommitSha: null,
            mergeable: true,
            rebaseable: true,
            mergeableState: "clean");
        return new TestContext(repository, pullRequest, headSha);
    }

    private static GitHubAccountSession CreateSession() =>
        GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                "synthetic-notification-token",
                "bearer",
                "repo user workflow",
                new Uri("https://github.com")));

    private static string BuildCheckRunsJson(string headSha) =>
        JsonSerializer.Serialize(
            new
            {
                total_count = 3,
                check_runs = new object[]
                {
                    new
                    {
                        id = 10L,
                        head_sha = headSha,
                        name = "build",
                        status = "completed",
                        conclusion = "failure",
                        html_url = "https://github.com/octo/repo/runs/10",
                        check_suite = new { id = 42L },
                    },
                    new
                    {
                        id = 11L,
                        head_sha = headSha,
                        name = "lint",
                        status = "completed",
                        conclusion = "success",
                        html_url = "https://github.com/octo/repo/runs/11",
                        check_suite = new { id = 42L },
                    },
                    new
                    {
                        id = 12L,
                        head_sha = headSha,
                        name = "test",
                        status = "completed",
                        conclusion = "failure",
                        html_url = "https://github.com/octo/repo/runs/12",
                        check_suite = new { id = 99L },
                    },
                },
            });

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                content,
                Encoding.UTF8,
                "application/json"),
        };

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Git command failed: {error}");
        return output;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed record TestContext(
        GitHubRepository Repository,
        GitHubPullRequest PullRequest,
        string HeadSha);

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
