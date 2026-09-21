using System.Globalization;
using System.Text.RegularExpressions;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    // Documented Git LFS progress shape (git-lfs-config man page, mirrored
    // from Electron's GitLFSProgressParser):
    // `<direction> <current>/<total files> <downloaded>/<total> <name>`
    private static readonly Regex LfsProgressLinePattern = new(
        @"^(.+?)\s{1}(\d+)\/(\d+)\s{1}(\d+)\/(\d+)\s{1}(.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Fetches Git LFS objects for the current checkout from a remote without
    /// touching the worktree. Reports the progress updates Git LFS observed;
    /// hosted transfers populate these while fast local transfers may report
    /// none. LFS failures throw an error naming the fetch operation.
    /// </summary>
    public async Task<LfsFetchResult> FetchLfsObjectsAsync(
        string root,
        string? remote,
        bool includeAll,
        CancellationToken cancellationToken)
    {
        if (remote is not null)
        {
            ValidateLfsRemoteName(remote);
        }

        var repositoryRoot = await ResolveRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(
            repositoryRoot,
            cancellationToken,
            async path =>
            {
                var progressPath = CreateOwnedLfsProgressFile();
                try
                {
                    var arguments = new List<string> { "lfs", "fetch" };
                    if (includeAll)
                    {
                        arguments.Add("--all");
                    }

                    if (remote is not null)
                    {
                        arguments.Add(remote);
                    }

                    try
                    {
                        await processRunner.RunAsync(
                            path,
                            arguments,
                            cancellationToken,
                            environmentOverrides: new Dictionary<string, string?>(StringComparer.Ordinal)
                            {
                                ["GIT_LFS_PROGRESS"] = progressPath,
                            }).ConfigureAwait(false);
                    }
                    catch (GitCommandException exception)
                    {
                        throw IdentifyLfsFailure(exception, "fetch LFS objects");
                    }

                    return new LfsFetchResult(ReadLfsProgressUpdates(progressPath));
                }
                finally
                {
                    DeleteOwnedLfsProgressFile(progressPath);
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Explains a failed Git command as an LFS transfer failure when its
    /// output carries a known LFS signature; otherwise rethrows the original
    /// failure with the operation named.
    /// </summary>
    public static Exception IdentifyLfsFailure(GitCommandException exception, string operation)
    {
        var output = string.Concat(exception.StandardError, "\n", exception.Message);
        if (output.Contains("smudge filter lfs failed", StringComparison.OrdinalIgnoreCase)
            || output.Contains("error downloading object", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                $"Git LFS could not download content while trying to {operation}; the remote may be unreachable or the objects may be missing.",
                exception);
        }

        if (output.Contains("batch response", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Authentication required", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                $"Git LFS authentication failed while trying to {operation}; sign in and try again without changing the repository.",
                exception);
        }

        if (output.Contains("git-lfs", StringComparison.OrdinalIgnoreCase)
            && output.Contains("not found on your path", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                $"Git LFS is not available while trying to {operation}; install Git LFS before retrying.",
                exception);
        }

        return new InvalidOperationException(
            $"Git failed while trying to {operation}: {FirstGitErrorLine(output)}",
            exception);
    }

    /// <summary>
    /// Parses Git LFS progress lines into per-file updates with running
    /// totals, mirroring Electron's GitLFSProgressParser. Lines that do not
    /// match the documented shape are skipped.
    /// </summary>
    public static IReadOnlyList<LfsTransferProgress> ParseLfsProgressLines(IEnumerable<string> lines)
    {
        var parser = new LfsProgressParser();
        var updates = new List<LfsTransferProgress>();
        foreach (var line in lines)
        {
            var update = parser.ParseLine(line);
            if (update is not null)
            {
                updates.Add(update);
            }
        }

        return updates;
    }

    private static string CreateOwnedLfsProgressFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinGit.Native");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "lfs-progress-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static void DeleteOwnedLfsProgressFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IReadOnlyList<LfsTransferProgress> ReadLfsProgressUpdates(string progressPath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(progressPath);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return ParseLfsProgressLines(lines);
    }

    private static void ValidateLfsRemoteName(string remote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        if (remote[0] == '-'
            || remote.IndexOf('\0') >= 0
            || remote.Contains('\r')
            || remote.Contains('\n'))
        {
            throw new ArgumentException("The LFS remote name contains an invalid character.", nameof(remote));
        }
    }

    private static string FirstGitErrorLine(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length != 0)
            {
                return trimmed;
            }
        }

        return "Git reported an error.";
    }

    private sealed class LfsProgressParser
    {
        private readonly Dictionary<string, LfsFileProgress> files = new(StringComparer.Ordinal);

        public LfsTransferProgress? ParseLine(string line)
        {
            var match = LfsProgressLinePattern.Match(line);
            if (!match.Success || match.Groups.Count != 7)
            {
                return null;
            }

            if (!int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var estimatedFileCount)
                || !long.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var transferredBytes)
                || !long.TryParse(match.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var totalBytes))
            {
                return null;
            }

            var fileName = match.Groups[6].Value;
            files[fileName] = new LfsFileProgress(transferredBytes, totalBytes, transferredBytes == totalBytes);

            long totalTransferred = 0;
            long totalEstimated = 0;
            var finishedFiles = 0;
            // Download estimates are unreliable, so use whichever is larger:
            // the estimate or the number of files actually observed.
            var fileCount = Math.Max(estimatedFileCount, files.Count);
            foreach (var file in files.Values)
            {
                totalTransferred += file.Transferred;
                totalEstimated += file.Size;
                finishedFiles += file.Done ? 1 : 0;
            }

            var direction = ParseDirection(match.Groups[1].Value);
            var verb = direction switch
            {
                LfsTransferDirection.Upload => "Uploading",
                LfsTransferDirection.Checkout => "Checking out",
                _ => "Downloading",
            };
            return new LfsTransferProgress(
                direction,
                fileName,
                totalTransferred,
                totalEstimated,
                finishedFiles,
                fileCount,
                $"{verb} {fileName} ({finishedFiles} out of an estimated {fileCount} completed, {FormatBytes(totalTransferred)} / {FormatBytes(totalEstimated)})");
        }

        private static LfsTransferDirection ParseDirection(string direction) => direction switch
        {
            "upload" => LfsTransferDirection.Upload,
            "checkout" => LfsTransferDirection.Checkout,
            _ => LfsTransferDirection.Download,
        };

        private static string FormatBytes(long bytes)
        {
            const long Kilobyte = 1024;
            const long Megabyte = 1024 * Kilobyte;
            if (bytes >= Megabyte)
            {
                return $"{bytes / (double)Megabyte:0.#} MB";
            }

            if (bytes >= Kilobyte)
            {
                return $"{bytes / (double)Kilobyte:0.#} KB";
            }

            return $"{bytes} B";
        }

        private sealed record LfsFileProgress(long Transferred, long Size, bool Done);
    }
}
