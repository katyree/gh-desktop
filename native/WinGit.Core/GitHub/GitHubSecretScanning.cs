using System.Text.RegularExpressions;

namespace WinGit.Core.GitHub;

/// <summary>One secret location: the commit, file path, and line number.</summary>
public sealed record GitHubSecretLocation(
    string CommitSha,
    string Path,
    int LineNumber);

/// <summary>
/// Secret metadata from a push-protection denial: the secret type, its
/// locations, and the bypass reference. Never carries secret values, only
/// the type name and where it was found.
/// </summary>
public sealed record GitHubSecretScanResult(
    string Id,
    string Description,
    string BypassUrl,
    bool RequiresApproval,
    IReadOnlyList<GitHubSecretLocation> Locations);

/// <summary>
/// Parses GitHub push-protection denials into secret metadata, mirroring
/// Electron's secret-scanning extraction. Only type names, locations, and
/// bypass references are read; secret values never appear in this output.
/// </summary>
public static class GitHubSecretScanning
{
    private static readonly Regex SecretsPattern = new(
        @"—— (?<description>.*?) —+[\s\S]*?locations:(?<locationsGroup>(?:\s+- commit: [a-f0-9]{40}\s+path: [\s\S]*?)+).*?(?<bypassURL>https[\s\S]*?) ",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LocationsPattern = new(
        @"- commit: (?<commitSha>[a-f0-9]{40})\s+path: (?<path>.*?):(?<lineNumber>\d+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Keeps the server-specific lines of Git stderr (the `remote: ` lines)
    /// with the prefix removed, mirroring Electron's remote-message helper.
    /// </summary>
    public static string GetRemoteMessage(string standardError)
    {
        if (string.IsNullOrEmpty(standardError))
        {
            return string.Empty;
        }

        const string needle = "remote: ";
        return string.Join(
            "\n",
            standardError
                .Split(['\r', '\n'], StringSplitOptions.None)
                .Where(line => line.StartsWith(needle, StringComparison.Ordinal))
                .Select(line => line[needle.Length..]));
    }

    /// <summary>Parses secret metadata from a stripped remote message.</summary>
    public static IReadOnlyList<GitHubSecretScanResult> ParsePushProtectionSecrets(string remoteMessage)
    {
        var secrets = new List<GitHubSecretScanResult>();
        if (string.IsNullOrEmpty(remoteMessage))
        {
            return secrets;
        }

        foreach (Match match in SecretsPattern.Matches(remoteMessage))
        {
            var description = match.Groups["description"].Value;
            var bypassUrl = match.Groups["bypassURL"].Value;
            if (description.Length == 0 || bypassUrl.Length == 0)
            {
                continue;
            }

            var locations = new List<GitHubSecretLocation>();
            foreach (Match locationMatch in LocationsPattern.Matches(match.Groups["locationsGroup"].Value))
            {
                if (!int.TryParse(
                        locationMatch.Groups["lineNumber"].Value,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var lineNumber) ||
                    lineNumber <= 0)
                {
                    continue;
                }

                locations.Add(new GitHubSecretLocation(
                    locationMatch.Groups["commitSha"].Value,
                    locationMatch.Groups["path"].Value,
                    lineNumber));
            }

            secrets.Add(new GitHubSecretScanResult(
                bypassUrl[(bypassUrl.LastIndexOf('/') + 1)..],
                description,
                bypassUrl,
                match.Value.Contains("request an exemption", StringComparison.Ordinal),
                locations));
        }

        return secrets;
    }

    /// <summary>
    /// Formats push-protection findings for a push error, or null when the
    /// output carries no push-protection denial.
    /// </summary>
    public static string? TryFormatPushProtectionSummary(string standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError) ||
            !Regex.IsMatch(
                standardError,
                @"push protection|GH013|secret scanning",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return null;
        }

        var secrets = ParsePushProtectionSecrets(GetRemoteMessage(standardError));
        if (secrets.Count == 0)
        {
            return "Push blocked: GitHub secret scanning found secrets in the pushed commits. Remove them from the commits before pushing again.";
        }

        var lines = secrets.Select(secret =>
        {
            var firstLocation = secret.Locations.Count == 0
                ? "location unavailable"
                : $"{secret.Locations[0].Path}:{secret.Locations[0].LineNumber}";
            return $"• {secret.Description} at {firstLocation}" +
                (secret.RequiresApproval ? " (bypass needs approval on GitHub)" : string.Empty);
        });
        return $"Push blocked: GitHub secret scanning found {secrets.Count} secret{(secrets.Count == 1 ? string.Empty : "s")}. Remove them from the commits before pushing again:\n" +
            string.Join("\n", lines);
    }
}
