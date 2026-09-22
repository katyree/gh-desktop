using System.Text.RegularExpressions;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private static readonly Regex NotificationFullCommitIdPattern = new(
        "^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<string?> ReadNotificationCommitAuthorEmailAsync(
        string root,
        string fullCommitId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fullCommitId) ||
            !NotificationFullCommitIdPattern.IsMatch(fullCommitId))
        {
            throw new ArgumentException(
                "A notification commit ID must contain 40 to 64 hexadecimal characters.",
                nameof(fullCommitId));
        }

        var repositoryRoot = ValidateDirectory(root, nameof(root));
        var result = await processRunner.RunAsync(
                repositoryRoot,
                [
                    "show",
                    "--quiet",
                    "--no-color",
                    "--format=%ae",
                    "--end-of-options",
                    fullCommitId,
                    "--",
                ],
                cancellationToken,
                expectedExitCodes: [128])
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return null;
        }

        EnsureComplete(result, "notification commit author");
        var email = DecodeUtf8(result.StandardOutput, "notification commit author")
            .Trim();
        return email.Length == 0 ||
            email.Any(character => character <= '\u001F' || character == '\u007F')
            ? null
            : email;
    }
}
