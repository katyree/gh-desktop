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

    /// <summary>
    /// Updates a remote's default-branch HEAD symref without failing the
    /// caller when the remote is unreachable or unknown. Returns the resolved
    /// default branch name, or null when it cannot be determined. Fetch and
    /// pull wire this in as best-effort post-step work.
    /// </summary>
    public async Task<string?> UpdateRemoteHeadAsync(
        string root,
        string name,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        ValidateRemoteNameSyntax(name);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            path => UpdateRemoteHeadInMutationAsync(path, name, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<string?> UpdateRemoteHeadInMutationAsync(
        string repositoryRoot,
        string name,
        CancellationToken cancellationToken)
    {
        var result = await RunRemoteCommandAsync(
            repositoryRoot,
            ["remote", "set-head", "-a", name],
            cancellationToken,
            expectedExitCodes: [1, 128]).ConfigureAwait(false);
        EnsureComplete(result, "remote HEAD update");
        if (result.ExitCode != 0)
        {
            return null;
        }

        return await ReadRemoteHeadTargetAsync(repositoryRoot, name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a failed fetch or pull to the most specific transport explanation
    /// available, always naming the remote. SAML, expired-credential,
    /// HTTPS-auth, SSH, certificate, and unreachable signatures are tried
    /// in order; anything else preserves the original failure.
    /// </summary>
    private static Exception MapTransportFailure(
        string operation,
        string remoteName,
        string? remoteUrl,
        GitCommandException exception)
    {
        var host = TryGetRemoteHost(remoteUrl);
        string? identified = null;
        if (TryParseSamlEnforcement(exception.StandardError) is string organization)
        {
            identified = $"The '{organization}' organization enforces SAML SSO. Sign in again and authorize the organization before {operation} '{remoteName}'.";
        }

        identified ??= IdentifyExpiredCredential(exception.StandardError, host)
            ?? IdentifyAuthenticationFailure(exception.StandardError, host)
            ?? IdentifySshFailure(exception.StandardError, host)
            ?? IdentifyCertificateFailure(exception.StandardError, host)
            ?? IdentifyUnreachableRemote(exception.StandardError, host);
        if (identified is not null)
        {
            return new InvalidOperationException(identified, exception);
        }

        return exception;
    }

    /// <summary>
    /// Maps a failed push, reporting a rejected ref with pull-first guidance
    /// before falling back to shared transport explanations.
    /// </summary>
    private static Exception MapPushFailure(
        string remoteName,
        string localBranch,
        string remoteBranch,
        string? remoteUrl,
        GitCommandException exception)
    {
        if (Regex.IsMatch(
                exception.StandardError,
                @"\[rejected\].*stale info",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return new InvalidOperationException(
                $"Force push stopped: '{remoteName}/{remoteBranch}' changed since the lease was captured. Refresh the remote state and confirm force push again instead of retrying blindly.",
                exception);
        }

        if (Regex.IsMatch(
                exception.StandardError,
                @"\[rejected\].*fetch first|updates were rejected because the remote contains work",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return new InvalidOperationException(
                $"Push rejected: '{remoteName}/{remoteBranch}' already contains work missing from '{localBranch}'. Fetch and pull the remote branch first, then push again; force push needs its own explicit confirmation.",
                exception);
        }

        return MapTransportFailure("pushing to", remoteName, remoteUrl, exception);
    }

    /// <summary>
    /// Maps a failed fast-forward pull, reporting the merge-or-rebase path
    /// for divergence and the preservation requirement for dirty worktrees
    /// before falling back to shared transport explanations.
    /// </summary>
    private static Exception MapPullFailure(
        string remoteName,
        string? remoteUrl,
        string localBranch,
        GitCommandException exception)
    {
        if (Regex.IsMatch(
                exception.StandardError,
                @"not possible to fast-forward",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return new InvalidOperationException(
                $"Pull refused: '{localBranch}' and '{remoteName}' have diverged, so a fast-forward is impossible. Merge the remote branch into '{localBranch}' or rebase '{localBranch}' onto it; WinGit never moves the branch automatically.",
                exception);
        }

        if (Regex.IsMatch(
                exception.StandardError,
                @"would be overwritten by merge|commit your changes or stash them",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return new InvalidOperationException(
                $"Pull refused to overwrite local changes in '{localBranch}'. Commit, stash, or discard them first; the worktree was left unchanged.",
                exception);
        }

        return MapTransportFailure("pulling from", remoteName, remoteUrl, exception);
    }

    private static string? IdentifyUnreachableRemote(string standardError, string? host)
    {
        var target = string.IsNullOrWhiteSpace(host) ? "the remote" : $"'{host}'";
        if (Regex.IsMatch(
                standardError,
                @"could not resolve host|failed to connect|connection timed out|network is unreachable|no route to host",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return $"Could not reach {target} over the network. Check the connection and the remote URL without changing the repository.";
        }

        return null;
    }

    /// <summary>Extracts a display-safe host from an explicit remote URL.</summary>
    private async Task<string?> ReadRemoteUrlAsync(
        string repositoryRoot,
        string name,
        CancellationToken cancellationToken)
    {
        var result = await RunRemoteCommandAsync(
            repositoryRoot,
            ["remote", "get-url", name],
            cancellationToken,
            expectedExitCodes: [2, 128]).ConfigureAwait(false);
        EnsureComplete(result, "remote URL lookup");
        if (result.ExitCode != 0)
        {
            return null;
        }

        var url = DecodeUtf8(result.StandardOutput, "remote URL lookup").Trim();
        return url.Length == 0 ? null : url;
    }

    private static string? TryGetRemoteHost(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var url = remoteUrl.Trim();
        if ((url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(url, UriKind.Absolute, out var httpUri)
            && httpUri.Host.Length != 0)
        {
            return httpUri.Host;
        }

        var scpSeparator = url.IndexOf(':');
        if (scpSeparator > 0 && !url.Contains("://", StringComparison.Ordinal))
        {
            var authority = url[..scpSeparator];
            var atSeparator = authority.LastIndexOf('@');
            var host = atSeparator >= 0 ? authority[(atSeparator + 1)..] : authority;
            return host.Length == 0 ? null : host;
        }

        return null;
    }

    /// <summary>
    /// Fetches an explicitly selected remote, prunes stale tracking refs, and
    /// refreshes the remote's default-branch HEAD symref without failing the
    /// fetch when that refresh is unavailable. Transport failures throw an
    /// error naming the remote host.
    /// </summary>
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
                var remoteUrl = await ReadRemoteUrlAsync(path, normalizedRemote, cancellationToken).ConfigureAwait(false);
                try
                {
                    await RunRemoteCommandAsync(
                        path,
                        ["fetch", "--prune", "--recurse-submodules=on-demand", normalizedRemote],
                        cancellationToken).ConfigureAwait(false);
                }
                catch (GitCommandException exception)
                {
                    throw MapTransportFailure("fetching from", normalizedRemote, remoteUrl, exception);
                }

                try
                {
                    await UpdateRemoteHeadInMutationAsync(path, normalizedRemote, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Refreshing the default-branch symref is best-effort like
                    // Electron's post-fetch update: a successful fetch stands
                    // even when the remote HEAD cannot be resolved.
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls an explicit remote branch into the current local branch with
    /// fast-forward-only semantics. Divergence and dirty-worktree refusals
    /// report the merge-or-rebase path and preservation requirement instead
    /// of raw Git output, and the remote HEAD symref refreshes on success.
    /// </summary>
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

                var remoteUrl = await ReadRemoteUrlAsync(path, normalizedRemote, cancellationToken).ConfigureAwait(false);
                try
                {
                    await RunRemoteCommandAsync(
                        path,
                        ["pull", "--ff-only", "--no-rebase", "--no-autostash", normalizedRemote, normalizedRemoteBranch],
                        cancellationToken).ConfigureAwait(false);
                }
                catch (GitCommandException exception)
                {
                    throw MapPullFailure(normalizedRemote, remoteUrl, normalizedLocalBranch, exception);
                }

                try
                {
                    await UpdateRemoteHeadInMutationAsync(path, normalizedRemote, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Refreshing the default-branch symref is best-effort: a
                    // successful pull stands even when the remote HEAD cannot
                    // be resolved.
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one remote-tracking branch tip, or null when the remote branch
    /// does not exist locally. Used to capture force-push leases.
    /// </summary>
    public async Task<string?> GetRemoteBranchTipAsync(
        string root,
        string remoteName,
        string remoteBranch,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        ValidateRemoteNameSyntax(remoteName);
        var normalizedRemoteBranch = await ValidateBranchNameAsync(
            repositoryRoot,
            remoteBranch,
            cancellationToken).ConfigureAwait(false);
        return await ReadRefIdAsync(
            repositoryRoot,
            $"refs/remotes/{remoteName}/{normalizedRemoteBranch}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Force-pushes one explicit local branch with a lease on the expected
    /// remote tip, so the push fails instead of overwriting commits pushed
    /// after the caller captured the tip. Plain --force is never used.
    /// Transport failures name the remote host.
    /// </summary>
    public async Task ForcePushWithLeaseAsync(
        string root,
        string remoteName,
        string localBranch,
        string remoteBranch,
        string expectedRemoteTip,
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
        ValidateCommitId(expectedRemoteTip);

        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var normalizedRemote = await EnsureRemoteExistsAsync(path, remoteName, cancellationToken).ConfigureAwait(false);
                await EnsureLocalBranchExistsAsync(path, normalizedLocalBranch, cancellationToken).ConfigureAwait(false);
                var remoteUrl = await ReadRemoteUrlAsync(path, normalizedRemote, cancellationToken).ConfigureAwait(false);
                try
                {
                    await RunRemoteCommandAsync(
                        path,
                        [
                            "push",
                            $"--force-with-lease=refs/heads/{normalizedRemoteBranch}:{expectedRemoteTip}",
                            normalizedRemote,
                            $"refs/heads/{normalizedLocalBranch}:refs/heads/{normalizedRemoteBranch}",
                        ],
                        cancellationToken).ConfigureAwait(false);
                }
                catch (GitCommandException exception)
                {
                    throw MapPushFailure(
                        normalizedRemote,
                        normalizedLocalBranch,
                        normalizedRemoteBranch,
                        remoteUrl,
                        exception);
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes one explicit local branch to one explicit remote branch without
    /// force. A rejected ref reports the ref with pull-first guidance instead
    /// of raw Git output; transport failures name the remote host.
    /// </summary>
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
                var remoteUrl = await ReadRemoteUrlAsync(path, normalizedRemote, cancellationToken).ConfigureAwait(false);
                try
                {
                    await RunRemoteCommandAsync(
                        path,
                        [
                            "push",
                            normalizedRemote,
                            $"refs/heads/{normalizedLocalBranch}:refs/heads/{normalizedRemoteBranch}",
                        ],
                        cancellationToken).ConfigureAwait(false);
                }
                catch (GitCommandException exception)
                {
                    throw MapPushFailure(
                        normalizedRemote,
                        normalizedLocalBranch,
                        normalizedRemoteBranch,
                        remoteUrl,
                        exception);
                }
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
