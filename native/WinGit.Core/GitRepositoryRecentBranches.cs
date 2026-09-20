using System.Globalization;
using System.Text.RegularExpressions;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private const int MaximumRecentBranchLimit = 100;
    private const int RecentBranchReflogLineLimit = 2_500;

    // Mirrors Electron's getRecentBranches message parsing: checkout moves and
    // renames, with rename intermediates excluded from later additions.
    private static readonly Regex RecentBranchMovePattern = new(
        @".*? (renamed|checkout)(?:: moving from|\s*) (?:refs/heads/|\s*)(.*?) to (?:refs/heads/|\s*)(.*?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Reads the most recently checked-out local branch names from the HEAD
    /// reflog, most recent first. Rename intermediates are excluded. The
    /// current branch is not filtered here; callers displaying the names
    /// exclude it like Electron's refreshRecentBranches does.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRecentBranchNamesAsync(
        string root,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaximumRecentBranchLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"The recent branch limit must be between 1 and {MaximumRecentBranchLimit}.");
        }

        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
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
                "--no-abbrev-commit",
                "--pretty=oneline",
                "HEAD",
                "-n",
                RecentBranchReflogLineLimit.ToString(CultureInfo.InvariantCulture),
                "--",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "recent branches");

        return ParseRecentBranchNames(result.StandardOutput, limit);
    }

    private static IReadOnlyList<string> ParseRecentBranchNames(byte[] output, int limit)
    {
        var text = DecodeUtf8(output, "recent branches");
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (names.Count >= limit)
            {
                break;
            }

            var match = RecentBranchMovePattern.Match(line);
            if (!match.Success || match.Groups.Count != 4)
            {
                continue;
            }

            if (string.Equals(match.Groups[1].Value, "renamed", StringComparison.OrdinalIgnoreCase))
            {
                excluded.Add(match.Groups[2].Value);
            }

            var branchName = match.Groups[3].Value;
            if (branchName.Length != 0 && !excluded.Contains(branchName) && seen.Add(branchName))
            {
                names.Add(branchName);
            }
        }

        return names;
    }
}
