namespace WinGit.Core.GitHub;

/// <summary>
/// Shared failure classification for pull-request checks, mirroring
/// Electron's isFailure: a completed check run with a failure or
/// action-required conclusion, or a failed/error legacy status.
/// </summary>
public static class GitHubCheckClassification
{
    public static bool IsFailedCheckRun(GitHubCheckRun checkRun)
    {
        ArgumentNullException.ThrowIfNull(checkRun);

        return checkRun.Status == GitHubCheckRunStatusKind.Completed &&
            checkRun.Conclusion is GitHubCheckRunConclusionKind.Failure
                or GitHubCheckRunConclusionKind.ActionRequired;
    }

    public static bool IsFailedCommitStatus(GitHubCommitStatusContext status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return status.State is GitHubCommitStatusState.Failure
            or GitHubCommitStatusState.Error;
    }
}
