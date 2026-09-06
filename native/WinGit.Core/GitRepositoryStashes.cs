using System.Globalization;
using System.Text.RegularExpressions;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private static readonly Regex StashReferencePattern = new(
        @"^stash@\{[0-9]+\}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Reads stash reflog references and their immutable commit IDs.</summary>
    public async Task<IReadOnlyList<StashSummary>> GetStashesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await GetStashesAtRootAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a stash and returns its immutable top-entry commit ID.</summary>
    public async Task<string> CreateStashAsync(
        string root,
        string message,
        bool includeUntracked,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = ValidateDirectory(root, nameof(root));
        ValidateStashMessage(message);
        var stash = await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            path => CreateStashInMutationAsync(path, message, includeUntracked, cancellationToken)).ConfigureAwait(false);
        return stash.CommitId;
    }

    /// <summary>Applies a stash by immutable commit ID and retains the stash entry.</summary>
    public async Task ApplyStashAsync(
        string root,
        string stashSHA,
        bool restoreIndex,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = ValidateDirectory(root, nameof(root));
        ValidateCommitId(stashSHA);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            path => ApplyStashInMutationAsync(path, stashSHA, restoreIndex, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Drops a stash only after its current reflog reference resolves to the expected SHA.</summary>
    public async Task DropStashAsync(
        string root,
        string expectedStashRef,
        string expectedSHA,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = ValidateDirectory(root, nameof(root));
        ValidateStashReference(expectedStashRef);
        ValidateCommitId(expectedSHA);
        await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            path => DropStashInMutationAsync(path, expectedStashRef, expectedSHA, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<StashSummary>> GetStashesAtRootAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["stash", "list", "--no-color", "--format=%gd%x00%H%x00%gs%x00%ct%x00"],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "stash list");
        return ParseStashes(result.StandardOutput);
    }

    private async Task<StashSummary> CreateStashInMutationAsync(
        string repositoryRoot,
        string message,
        bool includeUntracked,
        CancellationToken cancellationToken)
    {
        ValidateStashMessage(message);
        var previousStashId = await ReadStashHeadAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var arguments = new List<string> { "stash", "push" };
        if (includeUntracked)
        {
            arguments.Add("--include-untracked");
        }

        arguments.Add("--message");
        arguments.Add(message);
        await processRunner.RunAsync(repositoryRoot, arguments, cancellationToken).ConfigureAwait(false);

        var currentStashId = await ReadStashHeadAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (currentStashId is null
            || string.Equals(previousStashId, currentStashId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Git did not create a stash because there were no local changes.");
        }

        var stash = (await GetStashesAtRootAsync(repositoryRoot, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(candidate.CommitId, currentStashId, StringComparison.OrdinalIgnoreCase));
        return stash ?? throw new InvalidOperationException("Git created a stash, but its entry could not be read back.");
    }

    private async Task ApplyStashInMutationAsync(
        string repositoryRoot,
        string stashSHA,
        bool restoreIndex,
        CancellationToken cancellationToken)
    {
        ValidateCommitId(stashSHA);
        var arguments = new List<string> { "stash", "apply" };
        if (restoreIndex)
        {
            arguments.Add("--index");
        }

        arguments.Add(stashSHA);
        await processRunner.RunAsync(repositoryRoot, arguments, cancellationToken).ConfigureAwait(false);
    }

    private async Task DropStashInMutationAsync(
        string repositoryRoot,
        string expectedStashRef,
        string expectedSHA,
        CancellationToken cancellationToken)
    {
        ValidateStashReference(expectedStashRef);
        ValidateCommitId(expectedSHA);
        var resolved = await processRunner.RunAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "--quiet", "--end-of-options", expectedStashRef + "^{commit}"],
            cancellationToken,
            expectedExitCodes: [1]).ConfigureAwait(false);
        EnsureComplete(resolved, "stash reference validation");
        var actualSHA = DecodeUtf8(resolved.StandardOutput, "stash reference validation").Trim();
        if (resolved.ExitCode != 0
            || !string.Equals(actualSHA, expectedSHA, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The stash reference no longer identifies the expected stash.");
        }

        await processRunner.RunAsync(
            repositoryRoot,
            ["stash", "drop", expectedStashRef],
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<StashSummary> ParseStashes(byte[] output)
    {
        var fields = DecodeUtf8(output, "stash list")
            .Split('\0', StringSplitOptions.None);
        var stashes = new List<StashSummary>();
        for (var index = 0; index + 3 < fields.Length; index += 4)
        {
            var reference = fields[index].Trim('\r', '\n');
            if (reference.Length == 0)
            {
                continue;
            }

            var commitId = fields[index + 1].Trim('\r', '\n');
            var summary = fields[index + 2].Trim('\r', '\n');
            var timestampText = fields[index + 3].Trim('\r', '\n');
            ValidateStashReference(reference);
            ValidateCommitId(commitId);
            if (!long.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            {
                throw new InvalidOperationException("Git returned an invalid stash timestamp.");
            }

            DateTimeOffset date;
            try
            {
                date = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new InvalidOperationException("Git returned an out-of-range stash timestamp.", exception);
            }

            stashes.Add(new StashSummary(reference, commitId, summary, date));
        }

        return stashes;
    }

    private async Task<string?> ReadStashHeadAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "--quiet", "--end-of-options", "refs/stash^{commit}"],
            cancellationToken,
            expectedExitCodes: [1]).ConfigureAwait(false);
        EnsureComplete(result, "stash head lookup");
        if (result.ExitCode != 0)
        {
            return null;
        }

        var commitId = DecodeUtf8(result.StandardOutput, "stash head lookup").Trim();
        ValidateCommitId(commitId);
        return commitId;
    }

    private static void ValidateStashMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.IndexOf('\0') >= 0 || message.Contains('\r') || message.Contains('\n'))
        {
            throw new ArgumentException("A stash message cannot contain NUL or line breaks.", nameof(message));
        }
    }

    private static void ValidateStashReference(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (!StashReferencePattern.IsMatch(reference))
        {
            throw new ArgumentException("Stash references must use the form stash@{N}.", nameof(reference));
        }
    }
}
