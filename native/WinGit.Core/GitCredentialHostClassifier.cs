using System.Net;

namespace WinGit.Core;

/// <summary>The credential boundary that applies to one HTTPS remote host.</summary>
public enum GitCredentialHostKind
{
    Unknown,
    GitHub,
    Generic,
}

/// <summary>
/// Classifies HTTPS remote hosts without forwarding remote credentials during
/// the optional GitHub Enterprise discovery request.
/// </summary>
public static class GitCredentialHostClassifier
{
    private static readonly HashSet<string> KnownThirdPartyHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "dev.azure.com",
            "gitlab.com",
            "bitbucket.org",
            "amazonaws.com",
            "visualstudio.com",
        };

    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Classifies one HTTPS remote URL. Fast-path hosts do not make a network
    /// request; an unknown host is probed without cookies or authorization.
    /// </summary>
    public static Task<GitCredentialHostKind> ClassifyAsync(
        string remoteUrl,
        CancellationToken cancellationToken = default) =>
        ClassifyCoreAsync(remoteUrl, handler: null, cancellationToken);

    /// <summary>
    /// Test seam for the one discovery request. Production callers should use
    /// <see cref="ClassifyAsync(string, CancellationToken)"/>.
    /// </summary>
    internal static Task<GitCredentialHostKind> ClassifyAsync(
        string remoteUrl,
        HttpMessageHandler handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ClassifyCoreAsync(remoteUrl, handler, cancellationToken);
    }

    private static Task<GitCredentialHostKind> ClassifyCoreAsync(
        string remoteUrl,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetHttpsRemote(remoteUrl, out var remoteUri))
        {
            return Task.FromResult(GitCredentialHostKind.Unknown);
        }

        var host = remoteUri.DnsSafeHost.TrimEnd('.');
        if (IsGitHubFastPath(host))
        {
            return Task.FromResult(GitCredentialHostKind.GitHub);
        }

        if (IsGenericFastPath(host))
        {
            return Task.FromResult(GitCredentialHostKind.Generic);
        }

        return DiscoverHostKindAsync(remoteUri, handler, cancellationToken);
    }

    private static async Task<GitCredentialHostKind> DiscoverHostKindAsync(
        Uri remoteUri,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        using var client = handler is null
            ? new HttpClient(CreateSafeHandler(), disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;

        var metaUri = BuildMetaUri(remoteUri);
        using var request = new HttpRequestMessage(HttpMethod.Head, metaUri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(DiscoveryTimeout);

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            return HasGitHubRequestId(response)
                ? GitCredentialHostKind.GitHub
                : GitCredentialHostKind.Generic;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GitCredentialHostKind.Unknown;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Discovery is best effort. Do not log or expose the remote URL.
            return GitCredentialHostKind.Unknown;
        }
    }

    private static HttpClientHandler CreateSafeHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        Credentials = null,
        PreAuthenticate = false,
    };

    private static bool TryGetHttpsRemote(string remoteUrl, out Uri remoteUri)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)
            || !Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out var parsedUri)
            || parsedUri is null
            || !string.Equals(
                parsedUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(parsedUri.DnsSafeHost))
        {
            remoteUri = null!;
            return false;
        }

        remoteUri = parsedUri;
        return true;
    }

    private static Uri BuildMetaUri(Uri remoteUri)
    {
        var builder = new UriBuilder(
            Uri.UriSchemeHttps,
            remoteUri.DnsSafeHost,
            remoteUri.IsDefaultPort ? -1 : remoteUri.Port)
        {
            Path = "/api/v3/meta",
            Query = $"ghd={Guid.NewGuid():D}",
        };
        return builder.Uri;
    }

    private static bool IsGitHubFastPath(string host) =>
        string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "api.github.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase)
        || IsSubdomainPattern(host, "github");

    private static bool IsGenericFastPath(string host) =>
        IsKnownThirdPartyHost(host)
        || IsSubdomainPattern(host, "bitbucket")
        || IsSubdomainPattern(host, "gitlab");

    private static bool IsKnownThirdPartyHost(string host)
    {
        if (KnownThirdPartyHosts.Contains(host))
        {
            return true;
        }

        return KnownThirdPartyHosts.Any(knownHost =>
            host.EndsWith($".{knownHost}", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSubdomainPattern(string host, string label) =>
        host.StartsWith($"{label}.", StringComparison.OrdinalIgnoreCase)
        || host.Contains($".{label}.", StringComparison.OrdinalIgnoreCase);

    private static bool HasGitHubRequestId(HttpResponseMessage response) =>
        response.Headers.Contains("x-github-request-id")
        || response.Content.Headers.Contains("x-github-request-id");
}
