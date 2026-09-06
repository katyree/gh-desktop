using System.Text.RegularExpressions;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private async Task<IReadOnlyList<string>> FindUnresolvedConflictMarkerPathsAsync(
        string repositoryRoot,
        IReadOnlyList<string> selectedPaths,
        RepositoryStatus status,
        CancellationToken cancellationToken)
    {
        var selected = new HashSet<string>(selectedPaths, StringComparer.Ordinal);
        var markerPaths = new List<string>();
        foreach (var change in status.Changes)
        {
            if (!selected.Contains(change.Path) || !IsUnmergedChange(change))
            {
                continue;
            }

            var result = await processRunner.RunAsync(
                repositoryRoot,
                [
                    "diff",
                    "--check",
                    "--no-ext-diff",
                    "--no-textconv",
                    "--no-color",
                    "--",
                    ToLiteralPathSpec(change.Path),
                ],
                cancellationToken,
                expectedExitCodes: [2]).ConfigureAwait(false);
            if (result.StandardOutputTruncated || result.StandardErrorTruncated)
            {
                throw new GitOutputLimitException("conflict marker check");
            }

            var output = DecodeUtf8(result.StandardOutput, "conflict marker check");
            if (Regex.IsMatch(
                    output,
                    @"(?m)^[^\r\n]+:\d+: leftover conflict marker\r?$",
                    RegexOptions.CultureInvariant))
            {
                markerPaths.Add(change.Path);
            }
        }

        return markerPaths;
    }
}

/// <summary>Raised before staging a selected conflict file that still has marker lines.</summary>
public sealed class UnresolvedConflictMarkersException : InvalidOperationException
{
    public UnresolvedConflictMarkersException(IReadOnlyList<string> paths)
        : base(CreateMessage(paths))
    {
        Paths = new List<string>(paths ?? throw new ArgumentNullException(nameof(paths))).AsReadOnly();
    }

    public IReadOnlyList<string> Paths { get; }

    private static string CreateMessage(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("At least one conflicted path is required.", nameof(paths));
        }

        return $"Resolve or explicitly replace conflict markers before staging: {string.Join(", ", paths)}.";
    }
}
