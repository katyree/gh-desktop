using System.Text;
using System.Text.RegularExpressions;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private static readonly Regex RemoteNamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._/-]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HttpUserInfoPattern = new(
        @"(?i)^https?://[^\s/?#@]+@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SchemePasswordPattern = new(
        @"(?i)^[A-Za-z][A-Za-z0-9+.-]*://[^\s/?#@]+:[^\s/?#@]*@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ScpPasswordPattern = new(
        @"(?i)^[^\s/:@]+:[^\s/@]+@[^\s:]+:",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CredentialQueryPattern = new(
        @"(?i)([?&#](?:access_token|api[_-]?key|auth|client_secret|password|passwd|secret|token)=)[^&#\s]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Lists configured remotes with credential-bearing URL parts redacted.</summary>
    public async Task<IReadOnlyList<RemoteSummary>> GetRemotesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var result = await RunRemoteCommandAsync(
            repositoryRoot,
            ["remote", "-v"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "remote list");
        return ParseRemotes(result.StandardOutput);
    }

    /// <summary>Adds a remote after rejecting embedded URL credentials.</summary>
    public async Task<RemoteSummary> AddRemoteAsync(
        string root,
        string name,
        string url,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var remoteName = await ValidateRemoteNameAsync(
            repositoryRoot,
            name,
            cancellationToken).ConfigureAwait(false);
        var remoteUrl = ValidateRemoteUrl(url);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                await RunRemoteCommandAsync(
                    path,
                    ["remote", "add", remoteName, remoteUrl],
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return new RemoteSummary(name, SanitizeRemoteUrl(remoteUrl));
    }

    /// <summary>Changes a remote's fetch URL after rejecting embedded URL credentials.</summary>
    public async Task<RemoteSummary> SetRemoteUrlAsync(
        string root,
        string name,
        string url,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        ValidateRemoteNameSyntax(name);
        var remoteUrl = ValidateRemoteUrl(url);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var remoteName = await EnsureRemoteExistsAsync(path, name, cancellationToken).ConfigureAwait(false);
                await RunRemoteCommandAsync(
                    path,
                    ["remote", "set-url", remoteName, remoteUrl],
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return new RemoteSummary(name, SanitizeRemoteUrl(remoteUrl));
    }

    /// <summary>Removes a configured remote by its explicit name.</summary>
    public async Task RemoveRemoteAsync(
        string root,
        string name,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        ValidateRemoteNameSyntax(name);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var remoteName = await EnsureRemoteExistsAsync(path, name, cancellationToken).ConfigureAwait(false);
                await RunRemoteCommandAsync(
                    path,
                    ["remote", "remove", remoteName],
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Fetches an explicitly selected remote and prunes stale tracking refs.</summary>
    public async Task FetchAsync(
        string root,
        string remoteName,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        ValidateRemoteNameSyntax(remoteName);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var normalizedRemote = await EnsureRemoteExistsAsync(path, remoteName, cancellationToken).ConfigureAwait(false);
                await RunRemoteCommandAsync(
                    path,
                    ["fetch", "--prune", "--recurse-submodules=on-demand", normalizedRemote],
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Pulls an explicit remote branch into the current local branch with fast-forward-only semantics.</summary>
    public async Task PullFastForwardOnlyAsync(
        string root,
        string remoteName,
        string localBranch,
        string remoteBranch,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        ValidateRemoteNameSyntax(remoteName);
        var normalizedLocalBranch = await ValidateBranchNameAsync(
            repositoryRoot,
            localBranch,
            cancellationToken).ConfigureAwait(false);
        var normalizedRemoteBranch = await ValidateBranchNameAsync(
            repositoryRoot,
            remoteBranch,
            cancellationToken).ConfigureAwait(false);

        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var normalizedRemote = await EnsureRemoteExistsAsync(path, remoteName, cancellationToken).ConfigureAwait(false);
                var currentBranch = await GetCurrentBranchAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(currentBranch, normalizedLocalBranch, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Pull requires the selected local branch to be checked out.");
                }

                await RunRemoteCommandAsync(
                    path,
                    ["pull", "--ff-only", "--no-rebase", "--no-autostash", normalizedRemote, normalizedRemoteBranch],
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Pushes one explicit local branch to one explicit remote branch without force.</summary>
    public async Task PushAsync(
        string root,
        string remoteName,
        string localBranch,
        string remoteBranch,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        ValidateRemoteNameSyntax(remoteName);
        var normalizedLocalBranch = await ValidateBranchNameAsync(
            repositoryRoot,
            localBranch,
            cancellationToken).ConfigureAwait(false);
        var normalizedRemoteBranch = await ValidateBranchNameAsync(
            repositoryRoot,
            remoteBranch,
            cancellationToken).ConfigureAwait(false);

        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var normalizedRemote = await EnsureRemoteExistsAsync(path, remoteName, cancellationToken).ConfigureAwait(false);
                await EnsureLocalBranchExistsAsync(path, normalizedLocalBranch, cancellationToken).ConfigureAwait(false);
                await RunRemoteCommandAsync(
                    path,
                    [
                        "push",
                        normalizedRemote,
                        $"refs/heads/{normalizedLocalBranch}:refs/heads/{normalizedRemoteBranch}",
                    ],
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async Task<string> ValidateRemoteNameAsync(
        string repositoryRoot,
        string name,
        CancellationToken cancellationToken)
    {
        ValidateRemoteNameSyntax(name);
        var formatResult = await RunRemoteCommandAsync(
            repositoryRoot,
            ["check-ref-format", "--branch", name],
            cancellationToken,
            expectedExitCodes: [1]).ConfigureAwait(false);
        EnsureComplete(formatResult, "remote name validation");
        var checkedName = DecodeUtf8(formatResult.StandardOutput, "remote name validation").Trim();
        if (formatResult.ExitCode != 0 || !string.Equals(checkedName, name, StringComparison.Ordinal))
        {
            throw new ArgumentException("The remote name is not a valid Git name.", nameof(name));
        }

        return name;
    }

    private async Task<string> EnsureRemoteExistsAsync(
        string repositoryRoot,
        string name,
        CancellationToken cancellationToken)
    {
        var remoteResult = await RunRemoteCommandAsync(
            repositoryRoot,
            ["remote", "get-url", name],
            cancellationToken,
            expectedExitCodes: [2, 128]).ConfigureAwait(false);
        EnsureComplete(remoteResult, "remote lookup");
        var remoteUrl = DecodeUtf8(remoteResult.StandardOutput, "remote lookup").Trim();
        if (remoteResult.ExitCode != 0 || remoteUrl.Length == 0)
        {
            throw new ArgumentException("The selected remote is not configured in this repository.", nameof(name));
        }

        return name;
    }

    private async Task<string> GetCurrentBranchAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var result = await RunRemoteCommandAsync(
            repositoryRoot,
            ["symbolic-ref", "--quiet", "--short", "HEAD"],
            cancellationToken,
            expectedExitCodes: [1]).ConfigureAwait(false);
        EnsureComplete(result, "current branch lookup");
        var branch = DecodeUtf8(result.StandardOutput, "current branch lookup").Trim();
        if (result.ExitCode != 0 || branch.Length == 0)
        {
            throw new InvalidOperationException("The repository is detached; pull requires a checked-out branch.");
        }

        return branch;
    }

    private async Task EnsureLocalBranchExistsAsync(
        string repositoryRoot,
        string branchName,
        CancellationToken cancellationToken)
    {
        var result = await RunRemoteCommandAsync(
            repositoryRoot,
            ["show-ref", "--verify", "--quiet", "--end-of-options", "refs/heads/" + branchName],
            cancellationToken,
            expectedExitCodes: [1]).ConfigureAwait(false);
        EnsureComplete(result, "local branch lookup");
        if (result.ExitCode != 0)
        {
            throw new ArgumentException("The selected local branch does not exist.", nameof(branchName));
        }
    }

    private async Task<GitProcessResult> RunRemoteCommandAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? expectedExitCodes = null)
    {
        try
        {
            return await processRunner.RunAsync(
                repositoryRoot,
                arguments,
                cancellationToken,
                expectedExitCodes).ConfigureAwait(false);
        }
        catch (GitCommandException exception)
        {
            var error = SanitizeRemoteText(exception.StandardError);
            var suffix = error.Length == 0 ? string.Empty : $": {error}";
            throw new GitCommandException(
                $"Git command failed with exit code {exception.ExitCode}{suffix}",
                exception.ExitCode,
                error);
        }
    }

    private static IReadOnlyList<RemoteSummary> ParseRemotes(byte[] output)
    {
        var remotes = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = DecodeUtf8(output, "remote list");
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            const string fetchSuffix = " (fetch)";
            var tab = line.IndexOf('\t');
            if (tab <= 0 || !line.EndsWith(fetchSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = line[..tab];
            var url = line[(tab + 1)..^fetchSuffix.Length];
            if (name.Length == 0 || url.Length == 0)
            {
                continue;
            }

            remotes[name] = SanitizeRemoteUrl(url);
        }

        return remotes
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new RemoteSummary(pair.Key, pair.Value))
            .ToArray();
    }

    private static string ValidateRemoteUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url, nameof(url));
        if (url[0] == '-'
            || url.IndexOf('\0') >= 0
            || url.Contains('\r')
            || url.Contains('\n'))
        {
            throw new ArgumentException("The remote URL contains an invalid character.", nameof(url));
        }

        if (ContainsEmbeddedRemoteCredential(url))
        {
            throw new ArgumentException(
                "Embedded URL credentials are not accepted. Use Git Credential Manager or an SSH URL such as git@host:path.",
                nameof(url));
        }

        return url;
    }

    private static void ValidateRemoteNameSyntax(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        if (name[0] == '-'
            || name.IndexOf('\0') >= 0
            || name.Contains('\r')
            || name.Contains('\n')
            || !RemoteNamePattern.IsMatch(name))
        {
            throw new ArgumentException("The remote name contains an invalid character.", nameof(name));
        }
    }

    private static bool ContainsEmbeddedRemoteCredential(string url) =>
        HttpUserInfoPattern.IsMatch(url)
        || SchemePasswordPattern.IsMatch(url)
        || ScpPasswordPattern.IsMatch(url)
        || CredentialQueryPattern.IsMatch(url);

    private static string SanitizeRemoteUrl(string url)
    {
        var sanitized = Regex.Replace(
            url,
            @"(?i)(https?://)[^\s/@]+@",
            "$1<redacted>@");
        sanitized = Regex.Replace(
            sanitized,
            @"(?i)([A-Za-z][A-Za-z0-9+.-]*://)[^\s/@]+:[^\s/@]*@",
            "$1<redacted>@");
        sanitized = Regex.Replace(
            sanitized,
            @"(?i)^([^\s/:@]+):[^\s/@]+@",
            "$1:<redacted>@");
        return CredentialQueryPattern.Replace(sanitized, "$1<redacted>");
    }

    private static string SanitizeRemoteText(string text)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var lines = text.Split(['\r', '\n'], StringSplitOptions.None);
        var sanitized = string.Join('\n', lines.Select(SanitizeRemoteUrl));
        return sanitized.Length <= 4096 ? sanitized : sanitized[..4096] + "…";
    }
}
