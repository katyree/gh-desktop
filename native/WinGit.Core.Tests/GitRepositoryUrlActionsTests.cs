using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryUrlActionsTests
{
    [Fact]
    public void ParsesHttpsOpenRepositoryAction()
    {
        var parsed = RepositoryUrlParser.TryParse(
            "wingit://openRepo/https://github.com/desktop/desktop?branch=cancel-2fa-flow",
            out var action);

        Assert.True(parsed);
        Assert.NotNull(action);
        Assert.Equal(RepositoryUrlActionKind.OpenRepository, action.Kind);
        Assert.Equal("https://github.com/desktop/desktop", action.RemoteUrl);
        Assert.Equal("cancel-2fa-flow", action.Branch);
        Assert.Null(action.PullRequestNumber);
        Assert.Null(action.FilePath);
    }

    [Fact]
    public void ParsesSshPullRequestAndFilePathUsingElectronEncoding()
    {
        var parsed = RepositoryUrlParser.TryParse(
            "wingit://openRepo/git@github.com/desktop/desktop?branch=pr%2F1569&pr=1569&filepath=src%2FProgram.cs",
            out var action);

        Assert.True(parsed);
        Assert.NotNull(action);
        Assert.Equal("git@github.com/desktop/desktop", action.RemoteUrl);
        Assert.Equal("pr/1569", action.Branch);
        Assert.Equal(1569, action.PullRequestNumber);
        Assert.Equal("src/Program.cs", action.FilePath);
    }

    [Fact]
    public void DecodesFilePathOnceAndPreservesSpaces()
    {
        var parsed = RepositoryUrlParser.TryParse(
            "wingit://openRepo/https://github.com/desktop/desktop?filepath=review+%2520note.txt",
            out var action);

        Assert.True(parsed);
        Assert.NotNull(action);
        Assert.Equal("review %20note.txt", action.FilePath);
    }

    [Theory]
    [InlineData("git://github.com/desktop/desktop")]
    [InlineData("git@github.com:desktop/desktop")]
    public void AcceptsGitAndScpSshRemotes(string remoteUrl)
    {
        var parsed = RepositoryUrlParser.TryParse(
            $"wingit://openRepo/{remoteUrl}",
            out var action);

        Assert.True(parsed);
        Assert.NotNull(action);
        Assert.Equal(remoteUrl, action.RemoteUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x-wingit-client://openRepo/https://github.com/desktop/desktop")]
    [InlineData("x-github-client://openRepo/https://github.com/desktop/desktop")]
    [InlineData("wingit://oauth?code=one&state=two")]
    [InlineData("wingit://openRepo/")]
    [InlineData("wingit://openRepo/powershell:Write-Host")]
    [InlineData("wingit://openRepo/https://github.com/desktop/desktop?branch=%3C%3E")]
    [InlineData("wingit://openRepo/https://github.com/desktop/desktop?pr=2147483648")]
    public void RejectsUnsupportedOrUntrustedInput(string value)
    {
        Assert.False(RepositoryUrlParser.TryParse(value, out var action));
        Assert.Null(action);
    }
}
