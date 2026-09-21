using System.Text.RegularExpressions;

namespace WinGit.Core;

/// <summary>A parsed SSH unknown-host prompt: host, address, key type, and fingerprint.</summary>
public sealed record SshHostPrompt(
    string Host,
    string Ip,
    string KeyType,
    string Fingerprint);

public sealed partial class GitRepositoryService
{
    // Mirrors Electron's parseAddSSHHostPrompt for the OpenSSH unknown-host
    // prompt. Fingerprints identify the host key and are safe to display;
    // private key material never appears in these prompts.
    private static readonly Regex SshHostPromptPattern = new(
        @"^The authenticity of host '([^ ]+) \(([^\)]+)\)' can't be established[^.]*\.\n([^ ]+) key fingerprint is ([^.]+)\.",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Parses an SSH unknown-host prompt into its host, address, key type,
    /// and fingerprint, or null when the text is not such a prompt.
    /// </summary>
    public static SshHostPrompt? ParseSshHostPrompt(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            return null;
        }

        var match = SshHostPromptPattern.Match(prompt);
        if (!match.Success || match.Groups.Count != 5)
        {
            return null;
        }

        return new SshHostPrompt(
            match.Groups[1].Value,
            match.Groups[2].Value,
            match.Groups[3].Value,
            match.Groups[4].Value);
    }

    /// <summary>
    /// Explains an SSH failure with the affected host and next action while
    /// keeping key material and passwords out of the message. Returns null
    /// when the output is not SSH-related. Constructed messages never echo
    /// process output.
    /// </summary>
    public static string? IdentifySshFailure(string standardError, string? remoteHost)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return null;
        }

        var host = string.IsNullOrWhiteSpace(remoteHost) ? "the remote" : $"'{remoteHost.Trim()}'";
        if (Regex.IsMatch(
                standardError,
                @"host key verification failed|remote host identification has changed|offending .*key in ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return $"SSH host key verification failed for {host}. Verify the host fingerprint before trusting it; remove the stale entry only when the change is expected.";
        }

        if (Regex.IsMatch(
                standardError,
                @"permission denied \(publickey|no more authentication methods|all authentication methods failed",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return $"SSH public-key authentication failed for {host}. Check the configured key and its passphrase; private key material is never requested here.";
        }

        if (Regex.IsMatch(
                standardError,
                @"could not resolve hostname|connection timed out|connection refused|network is unreachable|no route to host",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return $"Could not reach {host} over SSH. Check the network connection and the host name without changing the repository.";
        }

        return null;
    }
}
