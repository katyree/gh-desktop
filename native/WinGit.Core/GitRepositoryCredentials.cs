using System.Text.RegularExpressions;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    /// <summary>
    /// Fills a credential request through an explicit helper without ever
    /// prompting. Inherited helpers are cleared first so no UI can appear.
    /// Throws when Git reports no stored credential.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> FillCredentialAsync(
        string root,
        IReadOnlyDictionary<string, string> request,
        string credentialHelper,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var validatedRequest = ValidateCredentialEntries(request, nameof(request));
        var helper = ValidateCredentialHelper(credentialHelper);
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["-c", "credential.helper=", "-c", $"credential.helper={helper}", "credential", "fill"],
            cancellationToken,
            standardInput: FormatCredential(validatedRequest)).ConfigureAwait(false);
        EnsureComplete(result, "credential fill");
        return ParseCredential(DecodeUtf8(result.StandardOutput, "credential fill"));
    }

    /// <summary>Approves (stores) a credential through an explicit helper.</summary>
    public async Task ApproveCredentialAsync(
        string root,
        IReadOnlyDictionary<string, string> credential,
        string credentialHelper,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var validatedCredential = ValidateCredentialEntries(credential, nameof(credential));
        var helper = ValidateCredentialHelper(credentialHelper);
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["-c", "credential.helper=", "-c", $"credential.helper={helper}", "credential", "approve"],
            cancellationToken,
            standardInput: FormatCredential(validatedCredential)).ConfigureAwait(false);
        EnsureComplete(result, "credential approve");
    }

    /// <summary>Rejects (erases) a credential through an explicit helper.</summary>
    public async Task RejectCredentialAsync(
        string root,
        IReadOnlyDictionary<string, string> credential,
        string credentialHelper,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var validatedCredential = ValidateCredentialEntries(credential, nameof(credential));
        var helper = ValidateCredentialHelper(credentialHelper);
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["-c", "credential.helper=", "-c", $"credential.helper={helper}", "credential", "reject"],
            cancellationToken,
            standardInput: FormatCredential(validatedCredential)).ConfigureAwait(false);
        EnsureComplete(result, "credential reject");
    }

    /// <summary>
    /// Explains a failed Git command as an HTTPS authentication failure when
    /// its output carries a known signature, naming the remote host. Returns
    /// null when the failure is not authentication-related. Credential values
    /// never appear in the returned message.
    /// </summary>
    public static string? IdentifyAuthenticationFailure(string standardError, string? remoteHost)
    {
        var host = string.IsNullOrWhiteSpace(remoteHost) ? "the remote" : $"'{remoteHost.Trim()}'";
        if (Regex.IsMatch(
                standardError,
                @"authentication failed|invalid username|invalid credentials|could not read (username|password)|logon failed",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return $"Git authentication failed for {host}. Check the stored credential or sign in again without changing the repository.";
        }

        if (Regex.IsMatch(
                standardError,
                @"repository not found|repository unavailable|not found\b.*\brepositor",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return $"Git could not find the repository on {host}, or access was denied. Check the remote URL and account access without changing the repository.";
        }

        return null;
    }

    /// <summary>
    /// Parses the credential-helper key=value protocol, expanding repeated
    /// key[] entries into indexed keys like Electron's parseCredential.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseCredential(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var credential = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator];
            var entryValue = line[(separator + 1)..];
            if (key.EndsWith("[]", StringComparison.Ordinal))
            {
                var prefix = key[..^2];
                var index = 0;
                while (credential.ContainsKey($"{prefix}[{index}]"))
                {
                    index++;
                }

                credential[$"{prefix}[{index}]"] = entryValue;
            }
            else
            {
                credential[key] = entryValue;
            }
        }

        return credential;
    }

    /// <summary>
    /// Formats credential entries for the helper protocol, rejecting values
    /// that could smuggle additional entries.
    /// </summary>
    public static string FormatCredential(IReadOnlyDictionary<string, string> credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var builder = new System.Text.StringBuilder();
        foreach (var (key, value) in credential)
        {
            if (value.Contains('\n') || value.Contains('\0'))
            {
                throw new ArgumentException($"Forbidden characters in credential value: {key}.", nameof(credential));
            }

            builder.Append(Regex.Replace(key, @"\[\d+\]$", "[]"));
            builder.Append('=');
            builder.Append(value);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static IReadOnlyDictionary<string, string> ValidateCredentialEntries(
        IReadOnlyDictionary<string, string> entries,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(entries, parameterName);
        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                throw new ArgumentException("Credential entries need names and values.", parameterName);
            }
        }

        return entries;
    }

    private static string ValidateCredentialHelper(string credentialHelper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialHelper);
        if (credentialHelper.IndexOf('\0') >= 0
            || credentialHelper.Contains('\r')
            || credentialHelper.Contains('\n'))
        {
            throw new ArgumentException("The credential helper contains an invalid character.", nameof(credentialHelper));
        }

        return credentialHelper;
    }
}
