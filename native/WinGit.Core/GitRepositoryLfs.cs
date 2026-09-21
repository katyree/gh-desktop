using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private static readonly Regex LfsVersionPattern = new(
        @"^git-lfs/(\S+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LfsFilterAttributePattern = new(
        @": filter: lfs(\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string?> NoInstallHooksEnvironment =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_LFS_TRACK_NO_INSTALL_HOOKS"] = "1",
        };

    /// <summary>
    /// Reports the installed Git LFS version, or null when Git LFS is not
    /// available. A missing filter command means LFS-dependent workflows are
    /// unavailable; it is never treated as a repository error.
    /// </summary>
    public async Task<string?> GetLfsVersionAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        string output;
        try
        {
            var result = await processRunner.RunAsync(
                repositoryRoot,
                ["lfs", "version"],
                cancellationToken).ConfigureAwait(false);
            EnsureComplete(result, "LFS version");
            output = DecodeUtf8(result.StandardOutput, "LFS version");
        }
        catch (GitCommandException)
        {
            return null;
        }

        var match = LfsVersionPattern.Match(output.TrimStart());
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Reports whether the repository tracks any paths with Git LFS. Reads
    /// the configured patterns without installing hooks and returns false
    /// when LFS is unavailable or the output cannot be understood.
    /// </summary>
    public async Task<bool> IsUsingLfsAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        byte[] output;
        try
        {
            var result = await processRunner.RunAsync(
                repositoryRoot,
                ["lfs", "track", "--json"],
                cancellationToken,
                environmentOverrides: NoInstallHooksEnvironment).ConfigureAwait(false);
            EnsureComplete(result, "LFS tracked patterns");
            output = result.StandardOutput;
        }
        catch (GitCommandException)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("patterns", out var patterns)
                || patterns.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var pattern in patterns.EnumerateArray())
            {
                if (pattern.ValueKind == JsonValueKind.Object
                    && pattern.TryGetProperty("tracked", out var tracked)
                    && tracked.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Reports whether a repository-relative path is covered by the Git LFS
    /// filter through the configured attributes.
    /// </summary>
    public async Task<bool> IsTrackedByLfsAsync(
        string root,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var normalizedPath = ValidateGitPath(repositoryRoot, relativePath, nameof(relativePath));
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["check-attr", "filter", "--", normalizedPath],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "LFS attribute check");
        return LfsFilterAttributePattern.IsMatch(DecodeUtf8(result.StandardOutput, "LFS attribute check"));
    }

    /// <summary>
    /// Installs the Git LFS hooks in the repository so LFS content transfers
    /// on fetch, push, and checkout. Mirrors Electron's repository hook
    /// install; global filter installation stays out of scope.
    /// </summary>
    public async Task InstallLfsHooksAsync(
        string root,
        bool force,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var arguments = new List<string> { "lfs", "install" };
        if (force)
        {
            arguments.Add("--force");
        }

        await processRunner.RunAsync(
            repositoryRoot,
            arguments,
            cancellationToken).ConfigureAwait(false);
    }
}
