using WinGit.Core.GitHub;

namespace WinGit.Native;

/// <summary>Plain-text list data for the native pull request browser.</summary>
internal sealed class NativeGitHubPullRequestRow
{
    public NativeGitHubPullRequestRow(GitHubPullRequest pullRequest)
    {
        PullRequest = pullRequest ?? throw new ArgumentNullException(nameof(pullRequest));
    }

    public GitHubPullRequest PullRequest { get; }

    public string DisplayName => $"#{PullRequest.Number}  {PullRequest.Title}";

    public string Details
    {
        get
        {
            var state = PullRequest.Draft
                ? $"{PullRequest.State} · Draft"
                : PullRequest.State.ToString();
            return $"{state} · @{PullRequest.AuthorLogin}";
        }
    }

    public override string ToString() => DisplayName;
}

/// <summary>Plain-text list data for one changed pull request file.</summary>
internal sealed class NativeGitHubPullRequestFileRow
{
    public NativeGitHubPullRequestFileRow(GitHubPullRequestChangedFile file)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
    }

    public GitHubPullRequestChangedFile File { get; }

    public string DisplayName =>
        $"{File.Status}  {File.Filename}  +{File.Additions} -{File.Deletions}";

    public string Details => File.Patch is not null
        ? "Patch available"
        : File.PatchWasOmittedByLimit
            ? "Patch omitted at the native size limit"
            : "Patch unavailable from GitHub (binary or large file)";

    public override string ToString() => DisplayName;
}

/// <summary>Plain-text list data for one pull request review.</summary>
internal sealed class NativeGitHubPullRequestReviewRow
{
    public NativeGitHubPullRequestReviewRow(GitHubPullRequestReview review)
    {
        Review = review ?? throw new ArgumentNullException(nameof(review));
    }

    public GitHubPullRequestReview Review { get; }

    public string DisplayName =>
        $"{Review.State}  @{Review.AuthorLogin ?? "deleted user"}";

    public string Details => Review.SubmittedAt is { } submittedAt
        ? $"Submitted {submittedAt.ToLocalTime():MMM d, yyyy h:mm tt}"
        : "Not submitted";

    public override string ToString() => DisplayName;
}
