using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositorySamlReauthTests
{
    [Fact]
    public void SamlEnforcementParsingExtractsOrganization()
    {
        Assert.Equal(
            "octo-org",
            GitRepositoryService.TryParseSamlEnforcement(
                "remote: The `octo-org' organization has enabled or enforced SAML SSO. "
                + "To access this repository, you must re-authorize the OAuth App."));

        Assert.Equal(
            "octo-org",
            GitRepositoryService.TryParseSamlEnforcement(
                "remote: The `octo-org' organization has enabled or enforced SAML SSO.\n"
                + "To access this repository, you must re-authorize the OAuth App."));

        Assert.Null(GitRepositoryService.TryParseSamlEnforcement(null!));
        Assert.Null(GitRepositoryService.TryParseSamlEnforcement(string.Empty));
        Assert.Null(GitRepositoryService.TryParseSamlEnforcement(
            "fatal: Authentication failed for 'https://github.com/o/r.git'"));
        Assert.Null(GitRepositoryService.TryParseSamlEnforcement(
            "remote: The `octo-org' organization allows SAML SSO but does not enforce it."));
    }

    [Fact]
    public void ExpiredCredentialIdentificationNamesRecovery()
    {
        var expired = GitRepositoryService.IdentifyExpiredCredential(
            "{\"message\":\"Bad credentials\",\"documentation_url\":\"https://docs.github.com\"}",
            "github.com");
        Assert.NotNull(expired);
        Assert.Contains("github.com", expired!, StringComparison.Ordinal);
        Assert.Contains("Sign in", expired!, StringComparison.Ordinal);

        var token = GitRepositoryService.IdentifyExpiredCredential(
            "error: Token expired at 2026-01-01",
            "github.com");
        Assert.NotNull(token);
        Assert.Contains("expired or was revoked", token!, StringComparison.OrdinalIgnoreCase);

        var unauthorized = GitRepositoryService.IdentifyExpiredCredential(
            "fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 401",
            null);
        Assert.NotNull(unauthorized);
        Assert.Contains("the remote", unauthorized!, StringComparison.Ordinal);

        Assert.Null(GitRepositoryService.IdentifyExpiredCredential(
            "fatal: Authentication failed for 'https://github.com/o/r.git'",
            "github.com"));
        Assert.Null(GitRepositoryService.IdentifyExpiredCredential(string.Empty, "github.com"));
    }

    [Fact]
    public void CertificateFailureIdentificationNamesHostAndSafeRecovery()
    {
        var selfSigned = GitRepositoryService.IdentifyCertificateFailure(
            "fatal: unable to access 'https://ghe.example.test/o/r.git/': SSL certificate problem: self-signed certificate",
            "ghe.example.test");
        Assert.NotNull(selfSigned);
        Assert.Contains("ghe.example.test", selfSigned!, StringComparison.Ordinal);
        Assert.Contains("never disables verification", selfSigned!, StringComparison.OrdinalIgnoreCase);

        var issuer = GitRepositoryService.IdentifyCertificateFailure(
            "SSL certificate problem: unable to get local issuer certificate",
            "ghe.example.test");
        Assert.NotNull(issuer);
        Assert.Contains("administrator", issuer!, StringComparison.OrdinalIgnoreCase);

        var expired = GitRepositoryService.IdentifyCertificateFailure(
            "error: certificate has expired",
            null);
        Assert.NotNull(expired);
        Assert.Contains("the remote", expired!, StringComparison.Ordinal);

        Assert.Null(GitRepositoryService.IdentifyCertificateFailure(
            "fatal: Authentication failed for 'https://github.com/o/r.git'",
            "github.com"));
        Assert.Null(GitRepositoryService.IdentifyCertificateFailure(string.Empty, "github.com"));
    }
}
