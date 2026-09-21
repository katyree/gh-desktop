using System.Text.RegularExpressions;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    // Mirrors Electron's samlReauthErrorMessageRe for the remote message Git
    // reports when an organization enforces SAML SSO. Singleline lets the
    // organization clause span the line break before "you must re-authorize".
    private static readonly Regex SamlEnforcementPattern = new(
        @"`([^']+)' organization has enabled or enforced SAML SSO.*?you must re-authorize",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Extracts the organization name from a SAML SSO enforcement message,
    /// or null when the output carries no such enforcement. The caller owns
    /// the re-authorization dialog and the retry of the blocked operation.
    /// </summary>
    public static string? TryParseSamlEnforcement(string standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return null;
        }

        var match = SamlEnforcementPattern.Match(standardError);
        return match.Success && match.Groups.Count == 2 && match.Groups[1].Value.Length != 0
            ? match.Groups[1].Value
            : null;
    }

    /// <summary>
    /// Explains expired or rejected credentials with the sign-in recovery
    /// path. Returns null when the output does not indicate an expired or
    /// invalid credential. Credential values never appear in the message.
    /// </summary>
    public static string? IdentifyExpiredCredential(string standardError, string? remoteHost)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return null;
        }

        var host = string.IsNullOrWhiteSpace(remoteHost) ? "the remote" : $"'{remoteHost.Trim()}'";
        if (Regex.IsMatch(
                standardError,
                @"bad credentials|invalid credentials|invalid token|token (has )?expired|oauth.*expired|HTTP 401|returned error: 401\b|401 unauthorized",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return $"The stored credential for {host} expired or was revoked. Sign in to GitHub again, then retry without changing the repository.";
        }

        return null;
    }
}
