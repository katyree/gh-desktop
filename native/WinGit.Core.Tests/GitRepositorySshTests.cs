using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositorySshTests
{
    [Fact]
    public void SshHostPromptParsingExtractsTrustMaterial()
    {
        var prompt = GitRepositoryService.ParseSshHostPrompt(
            "The authenticity of host 'github.com (140.82.121.4)' can't be established.\n"
            + "ED25519 key fingerprint is SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCO.\n"
            + "Are you sure you want to continue connecting (yes/no/[fingerprint])?");

        Assert.NotNull(prompt);
        Assert.Equal("github.com", prompt!.Host);
        Assert.Equal("140.82.121.4", prompt.Ip);
        Assert.Equal("ED25519", prompt.KeyType);
        Assert.Equal("SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCO", prompt.Fingerprint);
    }

    [Fact]
    public void SshHostPromptParsingRejectsNonPrompts()
    {
        Assert.Null(GitRepositoryService.ParseSshHostPrompt(null));
        Assert.Null(GitRepositoryService.ParseSshHostPrompt(string.Empty));
        Assert.Null(GitRepositoryService.ParseSshHostPrompt("Permission denied (publickey)."));
        Assert.Null(GitRepositoryService.ParseSshHostPrompt(
            "The authenticity of host 'example.test' can't be established without an address."));
    }

    [Fact]
    public void SshFailureIdentificationNamesHostAndNextAction()
    {
        var hostKey = GitRepositoryService.IdentifySshFailure(
            "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\n"
            + "@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @\n"
            + "Host key verification failed.",
            "github.com");
        Assert.NotNull(hostKey);
        Assert.Contains("github.com", hostKey!, StringComparison.Ordinal);
        Assert.Contains("fingerprint", hostKey!, StringComparison.OrdinalIgnoreCase);

        var pubkey = GitRepositoryService.IdentifySshFailure(
            "git@github.com: Permission denied (publickey).",
            "github.com");
        Assert.NotNull(pubkey);
        Assert.Contains("github.com", pubkey!, StringComparison.Ordinal);
        Assert.Contains("passphrase", pubkey!, StringComparison.OrdinalIgnoreCase);

        var unreachable = GitRepositoryService.IdentifySshFailure(
            "ssh: Could not resolve hostname badhost.test: Name or service not known",
            "badhost.test");
        Assert.NotNull(unreachable);
        Assert.Contains("badhost.test", unreachable!, StringComparison.Ordinal);

        Assert.Null(GitRepositoryService.IdentifySshFailure(
            "error: failed to push some refs: rejected",
            "github.com"));
        Assert.Null(GitRepositoryService.IdentifySshFailure(string.Empty, "github.com"));

        var noHost = GitRepositoryService.IdentifySshFailure(
            "Host key verification failed.",
            null);
        Assert.NotNull(noHost);
        Assert.Contains("the remote", noHost!, StringComparison.Ordinal);
    }
}
