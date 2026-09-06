using System.Net;
using System.Text;
using System.Text.Json;
using WinGit.Core.GitHub;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitHubPullRequestClientTests
{
    [Fact]
    public async Task ListsPagesAndReadsRepoBoundDetail()
    {
        var requestedUris = new List<string>();
        var callbackCounts = new List<int>();
        var firstPage = BuildPageJson(
            host: "octocorp.ghe.com",
            firstNumber: 1,
            count: 100);
        var secondPage = BuildPageJson(
            host: "octocorp.ghe.com",
            firstNumber: 101,
            count: 1);
        var detail = BuildPullRequestJson(
            host: "octocorp.ghe.com",
            number: 101,
            state: "closed",
            headOwner: "contrib",
            headName: "repo-fork");

        var handler = new SequenceHandler(
            (request, call, _) =>
            {
                requestedUris.Add(request.RequestUri!.AbsoluteUri);
                Assert.Equal(
                    "Bearer synthetic-pr-token",
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
                    3 => JsonResponse(detail),
                    _ => throw new InvalidOperationException(
                        "Unexpected synthetic pull request request."),
                };
                if (call == 1)
                {
                    response.Headers.TryAddWithoutValidation(
                        "Link",
                        "<https://evil.invalid/repos/org/repo/pulls?page=2>; rel=\"next\"");
                }

                return Task.FromResult(response);
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubPullRequestClient(
            GitHubPullRequestClientOptions.ForEnterprise(
                new Uri("https://octocorp.ghe.com")),
            httpClient);
        var session = CreateSession(
            "synthetic-pr-token",
            new Uri("https://octocorp.ghe.com"));
        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "octocorp@octocorp.ghe.com:org/repo.git",
                out var repository));
        Assert.NotNull(repository);

        var list = await client.ListAsync(
            session,
            repository!,
            page => callbackCounts.Add(page.Count));

        Assert.True(list.IsComplete);
        Assert.Null(list.ErrorKind);
        Assert.Equal(2, list.PagesFetched);
        Assert.Equal(101, list.PullRequests.Count);
        Assert.Equal([100, 1], callbackCounts);
        Assert.Equal(
            [
                "https://api.octocorp.ghe.com/repos/org/repo/pulls?state=open&per_page=100&page=1",
                "https://api.octocorp.ghe.com/repos/org/repo/pulls?state=open&per_page=100&page=2",
            ],
            requestedUris);

        var first = Assert.Single(
            list.PullRequests,
            pullRequest => pullRequest.Number == 1);
        Assert.Equal("Pull request 1", first.Title);
        Assert.Equal(GitHubPullRequestState.Open, first.State);
        Assert.True(first.Draft);
        Assert.Equal("feature/fix", first.Head.Ref);
        Assert.Equal(new string('b', 40), first.Head.Sha);
        Assert.NotNull(first.Head.Repository);
        Assert.Equal("contrib", first.Head.Repository!.OwnerLogin);
        Assert.Equal("repo-fork", first.Head.Repository.Name);
        Assert.Equal("main", first.Base.Ref);
        Assert.Equal(new string('a', 40), first.Base.Sha);
        Assert.Equal("org", first.Base.Repository!.OwnerLogin);
        Assert.Equal("repo", first.Base.Repository.Name);

        var read = await client.ReadAsync(session, repository!, 101);

        Assert.NotNull(read);
        Assert.Equal(101, read!.Number);
        Assert.Equal("Pull request 101", read.Title);
        Assert.Equal(
            new Uri("https://octocorp.ghe.com/org/repo/pull/101"),
            read.HtmlUrl);
        Assert.Equal("author", read.AuthorLogin);
        Assert.Equal("Line one\nLine two", read.Body);
        Assert.Equal(GitHubPullRequestState.Closed, read.State);
        Assert.True(read.Draft);
        Assert.Equal("feature/fix", read.Head.Ref);
        Assert.Equal(new string('b', 40), read.Head.Sha);
        Assert.Equal("contrib", read.Head.Repository!.OwnerLogin);
        Assert.Equal("repo-fork", read.Head.Repository.Name);
        Assert.Equal("main", read.Base.Ref);
        Assert.Equal(new string('a', 40), read.Base.Sha);
        Assert.Equal("org", read.Base.Repository!.OwnerLogin);
        Assert.Equal("repo", read.Base.Repository.Name);
        Assert.False(read.Merged);
        Assert.Null(read.MergedAt);
        Assert.Equal(new string('c', 40), read.MergeCommitSha);
        Assert.Equal(new string('d', 40), read.SquashMergeCommitSha);
        Assert.True(read.Mergeable);
        Assert.True(read.Rebaseable);
        Assert.Equal("clean", read.MergeableState);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task PreservesDeletedHeadAndReportsSafeFailures()
    {
        var deletedHeadHandler = new SequenceHandler(
            (_, call, _) => call switch
            {
                1 => Task.FromResult(
                    JsonResponse(
                        "[" + BuildPullRequestJson(
                            "github.com",
                            7,
                            headRepositoryMissing: true) + "]")),
                2 => Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.NotFound)),
                _ => throw new InvalidOperationException(
                    "Unexpected deleted-head request."),
            });
        using var deletedHeadHttpClient = new HttpClient(deletedHeadHandler);
        using var deletedHeadClient = new GitHubPullRequestClient(
            GitHubPullRequestClientOptions.ForGitHubCom(),
            deletedHeadHttpClient);
        var session = CreateSession(
            "synthetic-pr-token",
            new Uri("https://github.com"));
        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "https://github.com/org/repo.git",
                out var repository));
        Assert.NotNull(repository);

        var list = await deletedHeadClient.ListAsync(session, repository!);
        Assert.True(list.IsComplete);
        var deletedHeadPullRequest = Assert.Single(list.PullRequests);
        Assert.Null(deletedHeadPullRequest.Head.Repository);
        Assert.Null(
            await deletedHeadClient.ReadAsync(session, repository!, 7));

        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "git@github.enterprise.test:org/repo.git",
                out var foreignRepository));
        Assert.NotNull(foreignRepository);
        var mismatch = await Assert.ThrowsAsync<GitHubPullRequestException>(
            () => deletedHeadClient.ListAsync(session, foreignRepository!));
        Assert.Equal(GitHubPullRequestErrorKind.SessionMismatch, mismatch.Kind);
        Assert.Equal(2, deletedHeadHandler.CallCount);

        var errorHandler = new SequenceHandler(
            (_, _, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        "synthetic private response synthetic-pr-token",
                        Encoding.UTF8,
                        "application/json"),
                }));
        using var errorHttpClient = new HttpClient(errorHandler);
        using var errorClient = new GitHubPullRequestClient(
            GitHubPullRequestClientOptions.ForGitHubCom(),
            errorHttpClient);

        var error = await errorClient.ListAsync(session, repository!);
        Assert.False(error.IsComplete);
        Assert.Equal(
            GitHubPullRequestErrorKind.Unauthorized,
            error.ErrorKind);
        Assert.DoesNotContain(
            "synthetic private response",
            error.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "synthetic-pr-token",
            error.ToString(),
            StringComparison.Ordinal);

        var cancellationHandler = new SequenceHandler(
            (_, _, _) => throw new InvalidOperationException(
                "A cancelled pull request load must not send a request."));
        using var cancellationHttpClient = new HttpClient(cancellationHandler);
        using var cancellationClient = new GitHubPullRequestClient(
            GitHubPullRequestClientOptions.ForGitHubCom(),
            cancellationHttpClient);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<GitHubPullRequestCancelledException>(
            () => cancellationClient.ListAsync(
                session,
                repository!,
                cancellationToken: cancellation.Token));
        Assert.Equal(0, cancellationHandler.CallCount);
    }

    [Fact]
    public async Task ListsChangedFilesAndReviewsWithLocalPagination()
    {
        var requestedUris = new List<string>();
        var changedFilesPage = BuildChangedFilesPageJson(100);
        var changedFilesSecondPage = JsonSerializer.Serialize(
            new[]
            {
                BuildChangedFileObject(
                    "src/renamed.cs",
                    "renamed",
                    "src/old.cs",
                    additions: 3,
                    deletions: 1,
                    patch: new string('x', 512 * 1024 + 1),
                    includePatch: true),
            });
        var reviewsPage = BuildReviewsPageJson(100, firstId: 10);
        var reviewsSecondPage = JsonSerializer.Serialize(
            new[]
            {
                BuildReviewObject(
                    id: 110,
                    state: "PENDING",
                    authorLogin: null,
                    submittedAt: null,
                    commitId: null,
                    body: null),
            });

        var handler = new SequenceHandler(
            (request, call, _) =>
            {
                requestedUris.Add(request.RequestUri!.AbsoluteUri);
                var response = call switch
                {
                    1 => JsonResponse(changedFilesPage),
                    2 => JsonResponse(changedFilesSecondPage),
                    3 => JsonResponse(reviewsPage),
                    4 => JsonResponse(reviewsSecondPage),
                    _ => throw new InvalidOperationException(
                        "Unexpected synthetic changed-file or review request."),
                };
                if (call is 1 or 3)
                {
                    response.Headers.TryAddWithoutValidation(
                        "Link",
                        "<https://evil.invalid/repos/org/repo/pulls/11/files?page=2>; rel=\"next\"");
                }

                return Task.FromResult(response);
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubPullRequestClient(
            GitHubPullRequestClientOptions.ForGitHubCom(),
            httpClient);
        var session = CreateSession(
            "synthetic-pr-token",
            new Uri("https://github.com"));
        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "https://github.com/org/repo.git",
                out var repository));
        Assert.NotNull(repository);

        var files = await client.ListChangedFilesAsync(
            session,
            repository!,
            11);
        var reviews = await client.ListReviewsAsync(
            session,
            repository!,
            11);

        Assert.True(files.IsComplete);
        Assert.Null(files.ErrorKind);
        Assert.Equal(2, files.PagesFetched);
        Assert.Equal(101, files.Files.Count);
        var patch = Assert.Single(
            files.Files,
            file => file.Filename == "src/file-0.cs");
        Assert.Equal("@@ -1 +1 @@\n-old\n+new", patch.Patch);
        Assert.False(patch.PatchWasOmittedByLimit);
        var binary = Assert.Single(
            files.Files,
            file => file.Filename == "bin/image.dat");
        Assert.Null(binary.Patch);
        Assert.False(binary.PatchWasOmittedByLimit);
        var renamed = Assert.Single(
            files.Files,
            file => file.Filename == "src/renamed.cs");
        Assert.Equal("src/old.cs", renamed.PreviousFilename);
        Assert.Null(renamed.Patch);
        Assert.True(renamed.PatchWasOmittedByLimit);

        Assert.True(reviews.IsComplete);
        Assert.Null(reviews.ErrorKind);
        Assert.Equal(2, reviews.PagesFetched);
        Assert.Equal(101, reviews.Reviews.Count);
        var approved = Assert.Single(
            reviews.Reviews,
            review => review.Id == 10);
        Assert.Equal("reviewer", approved.AuthorLogin);
        Assert.Equal("APPROVED", approved.State);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 2, 12, 30, 0, TimeSpan.Zero),
            approved.SubmittedAt);
        Assert.Equal(new string('a', 40), approved.CommitId);
        Assert.Equal("Looks good.", approved.Body);
        Assert.Equal(
            new Uri("https://github.com/org/repo/pull/11#pullrequestreview-10"),
            approved.HtmlUrl);
        var pending = Assert.Single(
            reviews.Reviews,
            review => review.Id == 110);
        Assert.Null(pending.AuthorLogin);
        Assert.Null(pending.SubmittedAt);
        Assert.Null(pending.CommitId);
        Assert.Null(pending.Body);

        Assert.Equal(
            [
                "https://api.github.com/repos/org/repo/pulls/11/files?per_page=100&page=1",
                "https://api.github.com/repos/org/repo/pulls/11/files?per_page=100&page=2",
                "https://api.github.com/repos/org/repo/pulls/11/reviews?per_page=100&page=1",
                "https://api.github.com/repos/org/repo/pulls/11/reviews?per_page=100&page=2",
            ],
            requestedUris);
    }

    [Fact]
    public async Task ReturnsPartialChangedFilesAndHonorsCancellation()
    {
        var errorHandler = new SequenceHandler(
            (_, call, _) => call switch
            {
                1 => Task.FromResult(JsonResponse(BuildChangedFilesPageJson(100))),
                2 => Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent(
                            "synthetic private response synthetic-pr-token",
                            Encoding.UTF8,
                            "application/json"),
                    }),
                _ => throw new InvalidOperationException(
                    "Unexpected synthetic changed-file request."),
            });
        using var errorHttpClient = new HttpClient(errorHandler);
        using var errorClient = new GitHubPullRequestClient(
            GitHubPullRequestClientOptions.ForGitHubCom(),
            errorHttpClient);
        var session = CreateSession(
            "synthetic-pr-token",
            new Uri("https://github.com"));
        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "https://github.com/org/repo.git",
                out var repository));
        Assert.NotNull(repository);

        var partial = await errorClient.ListChangedFilesAsync(
            session,
            repository!,
            11);

        Assert.False(partial.IsComplete);
        Assert.Equal(1, partial.PagesFetched);
        Assert.Equal(100, partial.Files.Count);
        Assert.Equal(
            GitHubPullRequestErrorKind.Unauthorized,
            partial.ErrorKind);
        Assert.DoesNotContain(
            "synthetic private response",
            partial.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "synthetic-pr-token",
            partial.ToString(),
            StringComparison.Ordinal);

        var cancellationHandler = new SequenceHandler(
            (_, _, _) => throw new InvalidOperationException(
                "A cancelled changed-file load must not send a request."));
        using var cancellationHttpClient = new HttpClient(cancellationHandler);
        using var cancellationClient = new GitHubPullRequestClient(
            GitHubPullRequestClientOptions.ForGitHubCom(),
            cancellationHttpClient);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<GitHubPullRequestCancelledException>(
            () => cancellationClient.ListReviewsAsync(
                session,
                repository!,
                11,
                cancellationToken: cancellation.Token));
        Assert.Equal(0, cancellationHandler.CallCount);
    }

    [Fact]
    public async Task StopsChangedFilesAtServiceLimitWithoutClaimingCompletion()
    {
        var fullPage = BuildChangedFilesPageJson(100);
        var handler = new SequenceHandler(
            (request, call, _) =>
            {
                Assert.Contains(
                    $"page={call}",
                    request.RequestUri!.Query,
                    StringComparison.Ordinal);
                if (call > 30)
                {
                    throw new InvalidOperationException(
                        "The changed-file service cap must prevent page 31.");
                }

                return Task.FromResult(JsonResponse(fullPage));
            });
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubPullRequestClient(
            GitHubPullRequestClientOptions.ForGitHubCom(),
            httpClient);
        var session = CreateSession(
            "synthetic-pr-token",
            new Uri("https://github.com"));
        Assert.True(
            GitHubRemoteRepositoryIdentity.TryParse(
                "https://github.com/org/repo.git",
                out var repository));
        Assert.NotNull(repository);

        var result = await client.ListChangedFilesAsync(
            session,
            repository!,
            11);

        Assert.False(result.IsComplete);
        Assert.Equal(30, result.PagesFetched);
        Assert.Equal(3_000, result.Files.Count);
        Assert.Equal(
            GitHubPullRequestErrorKind.ChangedFilesLimitReached,
            result.ErrorKind);
        Assert.Equal(30, handler.CallCount);
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
        string host,
        int firstNumber,
        int count) =>
        JsonSerializer.Serialize(
            Enumerable.Range(firstNumber, count)
                .Select(number => BuildPullRequestObject(host, number)));

    private static string BuildChangedFilesPageJson(int count)
    {
        var files = Enumerable.Range(0, count)
            .Select(index => BuildChangedFileObject(
                index == 1 ? "bin/image.dat" : $"src/file-{index}.cs",
                "modified",
                previousFilename: null,
                additions: index + 1,
                deletions: index,
                patch: index == 0
                    ? "@@ -1 +1 @@\n-old\n+new"
                    : null,
                includePatch: index == 0));
        return JsonSerializer.Serialize(files);
    }

    private static Dictionary<string, object?> BuildChangedFileObject(
        string filename,
        string status,
        string? previousFilename,
        int additions,
        int deletions,
        string? patch,
        bool includePatch)
    {
        var file = new Dictionary<string, object?>
        {
            ["filename"] = filename,
            ["previous_filename"] = previousFilename,
            ["status"] = status,
            ["additions"] = additions,
            ["deletions"] = deletions,
            ["changes"] = additions + deletions,
        };
        if (includePatch)
        {
            file["patch"] = patch;
        }

        return file;
    }

    private static string BuildReviewsPageJson(int count, long firstId) =>
        JsonSerializer.Serialize(
            Enumerable.Range(0, count)
                .Select(index => BuildReviewObject(
                    firstId + index,
                    state: "APPROVED",
                    authorLogin: "reviewer",
                    submittedAt: "2026-09-02T12:30:00Z",
                    commitId: new string('a', 40),
                    body: "Looks good.")));

    private static Dictionary<string, object?> BuildReviewObject(
        long id,
        string state,
        string? authorLogin,
        string? submittedAt,
        string? commitId,
        string? body) =>
        new()
        {
            ["id"] = id,
            ["user"] = authorLogin is null ? null : new { login = authorLogin },
            ["body"] = body,
            ["html_url"] = $"https://github.com/org/repo/pull/11#pullrequestreview-{id}",
            ["submitted_at"] = submittedAt,
            ["state"] = state,
            ["commit_id"] = commitId,
        };

    private static string BuildPullRequestJson(
        string host,
        int number,
        string state = "open",
        string headOwner = "contrib",
        string headName = "repo-fork",
        bool headRepositoryMissing = false)
    {
        return JsonSerializer.Serialize(
            BuildPullRequestObject(
                host,
                number,
                state,
                headOwner,
                headName,
                headRepositoryMissing));
    }

    private static Dictionary<string, object?> BuildPullRequestObject(
        string host,
        int number,
        string state = "open",
        string headOwner = "contrib",
        string headName = "repo-fork",
        bool headRepositoryMissing = false)
    {
        var head = new Dictionary<string, object?>
        {
            ["ref"] = "feature/fix",
            ["sha"] = new string('b', 40),
            ["repo"] = headRepositoryMissing
                ? null
                : BuildRepositoryJson(host, headOwner, headName, isFork: true),
        };
        var @base = new Dictionary<string, object?>
        {
            ["ref"] = "main",
            ["sha"] = new string('a', 40),
            ["repo"] = BuildRepositoryJson(host, "org", "repo", isFork: false),
        };
        var pullRequest = new Dictionary<string, object?>
        {
            ["number"] = number,
            ["title"] = $"Pull request {number}",
            ["html_url"] = $"https://{host}/org/repo/pull/{number}",
            ["created_at"] = "2026-09-01T12:30:00Z",
            ["updated_at"] = "2026-09-02T12:30:00Z",
            ["user"] = new { login = "author" },
            ["body"] = "Line one\nLine two",
            ["state"] = state,
            ["draft"] = number % 2 == 1,
            ["head"] = head,
            ["base"] = @base,
            ["merged"] = false,
            ["merged_at"] = null,
            ["merge_commit_sha"] = new string('c', 40),
            ["squash_merge_commit_sha"] = new string('d', 40),
            ["mergeable"] = true,
            ["rebaseable"] = true,
            ["mergeable_state"] = "clean",
        };
        return pullRequest;
    }

    private static Dictionary<string, object?> BuildRepositoryJson(
        string host,
        string owner,
        string name,
        bool isFork) =>
        new()
        {
            ["id"] = Math.Abs(HashCode.Combine(host, owner, name)),
            ["name"] = name,
            ["owner"] = new { login = owner },
            ["private"] = false,
            ["fork"] = isFork,
            ["archived"] = false,
            ["default_branch"] = "main",
            ["pushed_at"] = "2026-09-01T12:30:00Z",
            ["html_url"] = $"https://{host}/{owner}/{name}",
            ["clone_url"] = $"https://{host}/{owner}/{name}.git",
            ["ssh_url"] = $"octocorp@{host}:{owner}/{name}.git",
        };

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
