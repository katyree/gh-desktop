using WinGit.Core.GitHub;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private readonly List<CancellationTokenSource> githubCloneDialogCancellations = [];
    private readonly List<Task> githubCloneDialogLoadTasks = [];

    private void RegisterGitHubCloneDialog(
        CancellationTokenSource cancellation)
    {
        githubCloneDialogCancellations.Add(cancellation);
        if (githubDisposed)
        {
            cancellation.Cancel();
        }
    }

    private void UnregisterGitHubCloneDialog(
        CancellationTokenSource cancellation,
        IReadOnlyList<Task> loadTasks)
    {
        githubCloneDialogCancellations.Remove(cancellation);
        foreach (var task in loadTasks)
        {
            githubCloneDialogLoadTasks.Remove(task);
        }
    }

    private Task[] CancelAndSnapshotGitHubCloneLoads()
    {
        foreach (var cancellation in githubCloneDialogCancellations.ToArray())
        {
            cancellation.Cancel();
        }

        return githubCloneDialogLoadTasks.ToArray();
    }

    private void RegisterGitHubCloneLoad(Task task)
    {
        githubCloneDialogLoadTasks.Add(task);
    }

    private NativeGitHubAccountChoice[] GetGitHubCloneAccountChoices()
    {
        return githubAccountRows
            .Select(row => new NativeGitHubAccountChoice(row.Summary))
            .ToArray();
    }

    private NativeGitHubAccountChoice? GetPreferredGitHubCloneAccount(
        IReadOnlyList<NativeGitHubAccountChoice> choices)
    {
        var selectedKey = selectedGitHubAccountRow?.Key;
        return selectedKey is null
            ? choices.FirstOrDefault()
            : choices.FirstOrDefault(choice => choice.Key == selectedKey)
                ?? choices.FirstOrDefault();
    }

    private async Task<GitHubRepositoryCatalogResult> LoadGitHubCloneRepositoriesAsync(
        NativeGitHubAccountChoice choice,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choice);
        cancellationToken.ThrowIfCancellationRequested();

        var account = selectedGitHubAccount is not null &&
            IsSameGitHubAccount(selectedGitHubAccount.Summary, choice.Summary)
            ? selectedGitHubAccount
            : await LoadGitHubCloneAccountAsync(choice, cancellationToken)
                .ConfigureAwait(true);
        if (account is null)
        {
            throw new InvalidOperationException(
                "The selected GitHub account is no longer available. Refresh Settings and try again.");
        }

        var options = CreateGitHubRepositoryCatalogOptions(choice.Summary.ApiOrigin);
        using var catalog = new GitHubRepositoryCatalogClient(options);
        return await catalog.ListAsync(account.Session, cancellationToken: cancellationToken)
            .ConfigureAwait(true);
    }

    private async Task<GitHubStoredAccount?> LoadGitHubCloneAccountAsync(
        NativeGitHubAccountChoice choice,
        CancellationToken cancellationToken)
    {
        var store = GetGitHubAccountStore();
        if (store is null)
        {
            throw new InvalidOperationException(
                "Connected GitHub accounts are unavailable in this native profile.");
        }

        return await store.LoadAsync(
                choice.Summary.ApiOrigin,
                choice.Summary.Id,
                cancellationToken)
            .ConfigureAwait(true);
    }

    private static GitHubRepositoryCatalogClientOptions CreateGitHubRepositoryCatalogOptions(
        Uri apiOrigin)
    {
        ArgumentNullException.ThrowIfNull(apiOrigin);
        if (string.Equals(
                apiOrigin.Host,
                "api.github.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return GitHubRepositoryCatalogClientOptions.ForGitHubCom();
        }

        var webHost = apiOrigin.Host;
        if (webHost.StartsWith("api.", StringComparison.OrdinalIgnoreCase) &&
            webHost.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase))
        {
            webHost = webHost[4..];
        }

        var webOrigin = new UriBuilder(
            Uri.UriSchemeHttps,
            webHost,
            apiOrigin.Port)
        {
            Path = "/",
        }.Uri;
        return GitHubRepositoryCatalogClientOptions.ForEnterprise(webOrigin);
    }

    private static string GetGitHubRepositoryCatalogStatus(
        GitHubRepositoryCatalogResult result)
    {
        if (result.IsComplete)
        {
            return result.Repositories.Count == 0
                ? "This account has no repositories available to clone."
                : $"Loaded {result.Repositories.Count} repositories.";
        }

        var reason = result.ErrorKind switch
        {
            GitHubRepositoryCatalogErrorKind.Unauthorized =>
                "GitHub rejected this account. Refresh Settings and sign in again.",
            GitHubRepositoryCatalogErrorKind.Forbidden =>
                "GitHub refused repository access for this account.",
            GitHubRepositoryCatalogErrorKind.SamlRequired =>
                "GitHub requires organization sign-in before showing these repositories.",
            GitHubRepositoryCatalogErrorKind.RateLimited =>
                "GitHub rate limited repository loading.",
            GitHubRepositoryCatalogErrorKind.Timeout =>
                "GitHub repository loading timed out.",
            GitHubRepositoryCatalogErrorKind.PageLimitReached =>
                "Repository loading reached its page limit.",
            _ => "Some repositories could not be loaded.",
        };

        return result.Repositories.Count == 0
            ? reason
            : $"Loaded {result.Repositories.Count} repositories. {reason}";
    }
}
