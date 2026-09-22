using WinGit.Core.GitHub;

namespace WinGit.Core;

public static class RepositoryUrlMatcher
{
    public static bool Matches(string requestedUrl, string configuredRemoteUrl)
    {
        if (string.IsNullOrWhiteSpace(requestedUrl) ||
            string.IsNullOrWhiteSpace(configuredRemoteUrl))
        {
            return false;
        }

        if (HasNonDefaultSshTransportPort(requestedUrl) ||
            HasNonDefaultSshTransportPort(configuredRemoteUrl))
        {
            return string.Equals(
                requestedUrl,
                configuredRemoteUrl,
                StringComparison.Ordinal);
        }

        if (TryParseGitHubIdentity(requestedUrl, out var requestedIdentity) &&
            requestedIdentity is not null &&
            TryParseGitHubIdentity(configuredRemoteUrl, out var configuredIdentity) &&
            configuredIdentity is not null)
        {
            return string.Equals(
                       requestedIdentity.Hostname,
                       configuredIdentity.Hostname,
                       StringComparison.Ordinal) &&
                   requestedIdentity.ApiOrigin.Port == configuredIdentity.ApiOrigin.Port &&
                   string.Equals(
                       requestedIdentity.Owner,
                       configuredIdentity.Owner,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       requestedIdentity.Name,
                       configuredIdentity.Name,
                       StringComparison.Ordinal);
        }

        return string.Equals(
            requestedUrl,
            configuredRemoteUrl,
            StringComparison.Ordinal);
    }

    private static bool TryParseGitHubIdentity(
        string url,
        out GitHubRemoteRepositoryIdentity? identity)
    {
        if (GitHubRemoteRepositoryIdentity.TryParse(url, out identity) &&
            identity is not null)
        {
            return true;
        }

        const string httpPrefix = "http://";
        var normalizedUrl = url.StartsWith(httpPrefix, StringComparison.OrdinalIgnoreCase)
            ? "https://" + url[httpPrefix.Length..]
            : url;
        const string sanitizedHttpsUserInfo = "https://<redacted>@";
        if (normalizedUrl.StartsWith(sanitizedHttpsUserInfo, StringComparison.OrdinalIgnoreCase))
        {
            normalizedUrl = "https://" + normalizedUrl[sanitizedHttpsUserInfo.Length..];
        }

        return !string.Equals(normalizedUrl, url, StringComparison.Ordinal) &&
            GitHubRemoteRepositoryIdentity.TryParse(normalizedUrl, out identity) &&
            identity is not null;
    }

    private static bool HasNonDefaultSshTransportPort(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase) &&
        uri.Port is not -1 and not 22;
}
