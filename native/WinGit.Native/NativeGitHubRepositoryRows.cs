using WinGit.Core.GitHub;

namespace WinGit.Native;

/// <summary>
/// A non-secret account choice used by the clone picker. The picker keeps
/// the stored account session in the window and never exposes its token.
/// </summary>
internal sealed class NativeGitHubAccountChoice
{
    public NativeGitHubAccountChoice(GitHubAccountSummary summary)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    public GitHubAccountSummary Summary { get; }

    public string Key =>
        $"{Summary.ApiOrigin.AbsoluteUri}|{Summary.Id}";

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Summary.DisplayName)
            ? $"@{Summary.Login}"
            : $"{Summary.DisplayName} (@{Summary.Login})";

    public string DisplayLabel => $"{DisplayName} · {Summary.ApiOrigin.Host}";

    public string Details =>
        string.IsNullOrWhiteSpace(Summary.Email)
            ? Summary.ApiOrigin.Host
            : $"{Summary.Email} · {Summary.ApiOrigin.Host}";

    public string SearchText => $"{DisplayName} {Details}";

    public override string ToString() => DisplayName;
}

internal enum NativeGitHubCloneProtocol
{
    Https,
    Ssh,
}

/// <summary>
/// A catalog row that contains only the validated metadata and clone
/// destinations returned by the Core GitHub client.
/// </summary>
internal sealed class NativeGitHubRepositoryRow
{
    public NativeGitHubRepositoryRow(GitHubRepository repository)
    {
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public GitHubRepository Repository { get; }

    public string DisplayName =>
        $"{Repository.OwnerLogin}/{Repository.Name}";

    public string Details
    {
        get
        {
            var labels = new List<string>();
            if (Repository.IsPrivate)
            {
                labels.Add("Private");
            }

            if (Repository.IsFork)
            {
                labels.Add("Fork");
            }

            if (Repository.IsArchived)
            {
                labels.Add("Archived");
            }

            if (!string.IsNullOrWhiteSpace(Repository.DefaultBranch))
            {
                labels.Add($"Default {Repository.DefaultBranch}");
            }

            return labels.Count == 0
                ? "GitHub repository"
                : string.Join(" · ", labels);
        }
    }

    public string SearchText =>
        $"{DisplayName} {Details} {Repository.HtmlUrl.AbsoluteUri}";

    public string CloneUrl(NativeGitHubCloneProtocol protocol) =>
        protocol == NativeGitHubCloneProtocol.Ssh
            ? Repository.SshCloneUrl
            : Repository.HttpsCloneUrl.AbsoluteUri;

    public override string ToString() => DisplayName;
}
