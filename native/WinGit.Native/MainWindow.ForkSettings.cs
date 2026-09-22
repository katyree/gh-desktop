using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private bool suppressForkContributionSelection;

    /// <summary>
    /// Finds the conventional upstream remote that marks a fork. GitHub API
    /// fork detection arrives with the hosting workflows; until then the
    /// universal upstream-remote convention is the local signal.
    /// </summary>
    private string? FindUpstreamRemoteName() =>
        remoteRows.FirstOrDefault(row =>
                string.Equals(row.Name, "upstream", StringComparison.Ordinal))
            ?.Name;

    private void UpdateForkContributionSection()
    {
        if (ForkContributionPanel is null
            || ForkContributeParentRadio is null
            || ForkContributeSelfRadio is null
            || ForkContributionDescriptionText is null)
        {
            return;
        }

        var root = repositoryRoot;
        var upstream = FindUpstreamRemoteName();
        if (root is null || upstream is null)
        {
            ForkContributionPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ForkContributionPanel.Visibility = Visibility.Visible;
        var target = NativeSettingsStore.GetForkContributionTarget(settings, root);
        suppressForkContributionSelection = true;
        try
        {
            ForkContributeParentRadio.IsChecked = target == ForkContributionTarget.Parent;
            ForkContributeSelfRadio.IsChecked = target == ForkContributionTarget.Self;
        }
        finally
        {
            suppressForkContributionSelection = false;
        }

        ForkContributionDescriptionText.Text = target == ForkContributionTarget.Parent
            ? $"Pull requests target '{upstream}'. Pushing goes to the selected remote as usual."
            : "For your own purposes. Pull requests target the repository you push to.";
    }

    private void ForkContributionTargetRadio_Checked(object sender, RoutedEventArgs e)
    {
        var root = repositoryRoot;
        if (suppressForkContributionSelection
            || root is null
            || FindUpstreamRemoteName() is null)
        {
            return;
        }

        var target = ReferenceEquals(sender, ForkContributeSelfRadio)
            ? ForkContributionTarget.Self
            : ForkContributionTarget.Parent;
        NativeSettingsStore.SetForkContributionTarget(settings, root, target);
        UpdateForkContributionSection();
        _ = SaveSettingsAsync();
        _ = RestartGitHubNotificationMonitorAsync();
    }

    /// <summary>
    /// Names the fork contribution target for a push confirmation, or null
    /// when the repository has no upstream remote.
    /// </summary>
    private string? FormatForkContributionPushNote(string remoteName)
    {
        var root = repositoryRoot;
        var upstream = FindUpstreamRemoteName();
        if (root is null || upstream is null)
        {
            return null;
        }

        var target = NativeSettingsStore.GetForkContributionTarget(settings, root);
        return target == ForkContributionTarget.Parent
            ? $"Fork contribution: pull requests target '{upstream}'."
            : $"Fork contribution: pull requests target '{remoteName}' (this fork).";
    }
}
