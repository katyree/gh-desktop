using WinGit.Core.GitHub;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitHubSecretScanningTests
{
    private const string Denial =
        "To https://github.com/org/repo.git\n" +
        " ! [rejected]        main -> main (GH013: Repository rule violations found)\n" +
        "error: failed to push some refs to 'https://github.com/org/repo.git'\n" +
        "remote: error: GH013: Repository rule violations found for refs/heads/main.\n" +
        "remote: \n" +
        "remote: - GITHUB PUSH PROTECTION\n" +
        "remote:   —————————————————————————————————\n" +
        "remote:     Resolve the following violations before pushing again.\n" +
        "remote: \n" +
        "remote:     —— GitHub Access Token ———————————————————\n" +
        "remote:        locations:\n" +
        "remote:          - commit: abcdef0123456789abcdef0123456789abcdef01\n" +
        "remote:            path: config/secrets.txt:42\n" +
        "remote: \n" +
        "remote:        (?) Learn how to resolve a blocked push\n" +
        "remote:        https://github.com/org/repo/security/secret-scanning/unblock-secret/1 \n" +
        "remote: \n" +
        "remote:     —— Generic Secret ———————————————————\n" +
        "remote:        locations:\n" +
        "remote:          - commit: abcdef0123456789abcdef0123456789abcdef01\n" +
        "remote:            path: app.env:7\n" +
        "remote:          - commit: 1234567890abcdef1234567890abcdef12345678\n" +
        "remote:            path: app.env:9\n" +
        "remote: \n" +
        "remote:        (?) To bypass push protection, you must request an exemption\n" +
        "remote:        https://github.com/org/repo/security/secret-scanning/unblock-secret/2 \n";

    [Fact]
    public void PushProtectionParsingExtractsTypedMetadata()
    {
        var remoteMessage = GitHubSecretScanning.GetRemoteMessage(Denial);
        Assert.DoesNotContain("remote: ", remoteMessage, StringComparison.Ordinal);

        var secrets = GitHubSecretScanning.ParsePushProtectionSecrets(remoteMessage);

        Assert.Equal(2, secrets.Count);
        var token = Assert.Single(secrets, secret => secret.Description == "GitHub Access Token");
        Assert.Equal("1", token.Id);
        Assert.Equal(
            "https://github.com/org/repo/security/secret-scanning/unblock-secret/1",
            token.BypassUrl);
        Assert.False(token.RequiresApproval);
        var tokenLocation = Assert.Single(token.Locations);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", tokenLocation.CommitSha);
        Assert.Equal("config/secrets.txt", tokenLocation.Path);
        Assert.Equal(42, tokenLocation.LineNumber);

        var generic = Assert.Single(secrets, secret => secret.Description == "Generic Secret");
        Assert.Equal("2", generic.Id);
        Assert.True(generic.RequiresApproval);
        Assert.Equal(2, generic.Locations.Count);
        Assert.Equal("app.env", generic.Locations[0].Path);
        Assert.Equal(7, generic.Locations[0].LineNumber);
        Assert.Equal("1234567890abcdef1234567890abcdef12345678", generic.Locations[1].CommitSha);
        Assert.Equal(9, generic.Locations[1].LineNumber);
    }

    [Fact]
    public void PushProtectionParsingIgnoresNonDenials()
    {
        Assert.Empty(GitHubSecretScanning.ParsePushProtectionSecrets(string.Empty));
        Assert.Empty(GitHubSecretScanning.ParsePushProtectionSecrets(
            "To https://github.com/org/repo.git\n ! [rejected] main -> main (fetch first)\n"));
        Assert.Empty(GitHubSecretScanning.GetRemoteMessage("local output without remote lines"));
    }

    [Fact]
    public void PushProtectionSummaryNamesFindingsWithoutSecrets()
    {
        var summary = GitHubSecretScanning.TryFormatPushProtectionSummary(Denial);

        Assert.NotNull(summary);
        Assert.Contains("2 secrets", summary!, StringComparison.Ordinal);
        Assert.Contains("GitHub Access Token", summary!, StringComparison.Ordinal);
        Assert.Contains("config/secrets.txt:42", summary!, StringComparison.Ordinal);
        Assert.Contains("bypass needs approval", summary!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abcdef0123456789", summary!, StringComparison.Ordinal);

        Assert.Null(GitHubSecretScanning.TryFormatPushProtectionSummary(
            "To https://github.com/org/repo.git\n ! [rejected] main -> main (fetch first)\n"));
        Assert.Null(GitHubSecretScanning.TryFormatPushProtectionSummary(string.Empty));
    }
}
