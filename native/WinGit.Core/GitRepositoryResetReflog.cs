using System.Globalization;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private const int MaximumReflogLimit = 2_500;

    /// <summary>
    /// Moves HEAD to a validated commit with the explicitly selected reset mode.
    /// The caller owns any confirmation required for a hard reset.
    /// </summary>
    public async Task<ResetOperationResult> ResetAsync(
        string root,
        GitResetMode mode,
        string targetCommitId,
        string expectedHeadId,
        CancellationToken cancellationToken)
    {
        var modeArgument = mode switch
        {
            GitResetMode.Soft => "--soft",
            GitResetMode.Mixed => "--mixed",
            GitResetMode.Hard => "--hard",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown Git reset mode."),
        };
        ValidateCommitId(targetCommitId);
        ValidateExpectedHeadId(expectedHeadId);
        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);

        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var state = await ReadResetOperationStateAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureResetCanRun(state, expectedHeadId);
                var resolvedTargetCommitId = await ResolveCommitIdAsync(
                    path,
                    targetCommitId,
                    cancellationToken).ConfigureAwait(false);
                await processRunner.RunAsync(
                    path,
                    ["reset", modeArgument, resolvedTargetCommitId],
                    cancellationToken).ConfigureAwait(false);

                var after = await GetStatusAsync(path, cancellationToken).ConfigureAwait(false);
                return new ResetOperationResult(
                    mode,
                    state.CurrentHeadId,
                    after.HeadId,
                    after.Branch,
                    after.IsDetached);
            }).ConfigureAwait(false);
    }

    /// <summary>Reads a bounded, date-bearing HEAD reflog for recovery selection.</summary>
    public async Task<IReadOnlyList<ReflogEntry>> GetHeadReflogAsync(
        string root,
        int limit = 250,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumReflogLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"The reflog limit must be between 1 and {MaximumReflogLimit}.");
        }

        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        // Git exits 128 for an unborn HEAD.  Confirm that state through the
        // normal status parser before running the reflog command so a broken
        // repository or another log failure is not reported as an empty
        // recovery history.
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (status.IsUnborn || status.HeadId.Length == 0)
        {
            return [];
        }

        var result = await processRunner.RunAsync(
            repositoryRoot,
            [
                "log",
                "-g",
                "--no-color",
                "--no-show-signature",
                "--no-abbrev-commit",
                "--date=iso-strict",
                "--format=%H%x00%gd%x00%gs",
                "-n",
                limit.ToString(CultureInfo.InvariantCulture),
                "HEAD",
                "--",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "HEAD reflog");
        return ParseHeadReflog(result.StandardOutput);
    }

    private async Task<ResetOperationState> ReadResetOperationStateAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var mergeState = await ReadMergeStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        return new ResetOperationState(
            mergeState.OperationKind,
            status.HeadId,
            status.Branch,
            status.IsDetached,
            status.IsUnborn,
            mergeState.UnmergedPaths);
    }

    private static void EnsureResetCanRun(
        ResetOperationState state,
        string expectedHeadId)
    {
        if (state.IsUnborn || state.CurrentHeadId.Length == 0)
        {
            throw new ResetOperationBlockedException(
                ResetOperationFailureReason.UnbornRepository,
                state,
                "Reset is unavailable because the repository has no current commit.");
        }

        if (!string.Equals(state.CurrentHeadId, expectedHeadId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ResetOperationBlockedException(
                ResetOperationFailureReason.StaleHead,
                state,
                "Reset was not applied because the current commit changed; refresh the repository first.");
        }

        if (state.OperationKind != GitOperationKind.None)
        {
            throw new ResetOperationBlockedException(
                ResetOperationFailureReason.OperationInProgress,
                state,
                "Reset is unavailable while another Git operation is in progress.");
        }

        if (state.HasUnmergedChanges)
        {
            throw new ResetOperationBlockedException(
                ResetOperationFailureReason.UnmergedChanges,
                state,
                "Reset is unavailable while the index contains unresolved conflicts.");
        }
    }

    private static IReadOnlyList<ReflogEntry> ParseHeadReflog(byte[] output)
    {
        var text = DecodeUtf8(output, "HEAD reflog");
        var entries = new List<ReflogEntry>();
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = rawLine.Split('\0');
            if (fields.Length != 3)
            {
                throw new InvalidOperationException("Git returned a malformed HEAD reflog record.");
            }

            var commitId = fields[0].Trim();
            ValidateObjectId(commitId, "reflog commit ID");
            var selector = fields[1];
            var date = ParseReflogDate(selector);
            entries.Add(new ReflogEntry(commitId, selector, date, fields[2]));
        }

        return entries;
    }

    private static DateTimeOffset ParseReflogDate(string selector)
    {
        var openBrace = selector.LastIndexOf('{');
        if (openBrace < 0 || !selector.EndsWith('}'))
        {
            throw new InvalidOperationException("Git returned a reflog selector without a date.");
        }

        var dateText = selector[(openBrace + 1)..^1];
        if (!DateTimeOffset.TryParse(
                dateText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var date))
        {
            throw new InvalidOperationException("Git returned an invalid reflog date.");
        }

        return date;
    }
}
