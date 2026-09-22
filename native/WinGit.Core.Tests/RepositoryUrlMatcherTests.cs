using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class RepositoryUrlMatcherTests
{
    [Fact]
    public void MatchesEquivalentHttpsAndSshGitHubRemotes()
    {
        Assert.True(
            RepositoryUrlMatcher.Matches(
                "git@github.com:test-owner/test-repository.git",
                "https://github.com/test-owner/test-repository"));
        Assert.True(
            RepositoryUrlMatcher.Matches(
                "http://github.com/test-owner/test-repository",
                "https://github.com/test-owner/test-repository"));
    }

    [Fact]
    public void MatchesSanitizedHttpsUserInfo()
    {
        Assert.True(
            RepositoryUrlMatcher.Matches(
                "https://github.com/test-owner/test-repository.git",
                "https://<redacted>@github.com/test-owner/test-repository"));
    }

    [Fact]
    public void RejectsDifferentGitHubRepositoryOrHost()
    {
        const string requested = "https://github.com/test-owner/test-repository";

        Assert.False(
            RepositoryUrlMatcher.Matches(
                requested,
                "https://github.com/test-owner/other-repository"));
        Assert.False(
            RepositoryUrlMatcher.Matches(
                requested,
                "https://github.enterprise.test/test-owner/test-repository"));
        Assert.False(
            RepositoryUrlMatcher.Matches(
                "ssh://git@example.invalid:2222/test-owner/test-repository",
                "https://example.invalid/test-owner/test-repository"));
    }

    [Fact]
    public void RequiresExactMatchForNonGitHubRemotes()
    {
        const string configured = "file:///srv/repositories/project.git";

        Assert.True(RepositoryUrlMatcher.Matches(configured, configured));
        Assert.False(
            RepositoryUrlMatcher.Matches(
                "file:///srv/repositories/project",
                configured));
    }
}
