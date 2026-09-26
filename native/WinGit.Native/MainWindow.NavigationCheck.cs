using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private async Task RunNavigationCheckAsync(NativeCaptureOptions options)
    {
        var report = new StringBuilder();
        try
        {
            await OpenRepositoryAsync(options.RepositoryPath!);
            await WaitForLatestOperationAsync();
            Check(report, repositoryRoot is not null && !ErrorBar.IsOpen, "repository opened", ErrorBar.Message);

            foreach (var item in RepositoryNavigation.MenuItems.OfType<NavigationViewItem>())
            {
                SelectWorkspaceFromAppCommand((string)item.Tag);
                await WaitForLatestOperationAsync();
                await WaitForLayoutAsync();
                Check(report, currentWorkspace == (string)item.Tag
                    && ReferenceEquals(MainNavigation.SelectedItem, MainNavigation.MenuItems[2])
                    && RepositoryNavigation.Visibility == Visibility.Visible,
                    "repository tab routes to " + item.Tag, currentWorkspace);
            }

            AppCommandMenuItem_Click(HistoryMenuItem, new RoutedEventArgs());
            await WaitForLatestOperationAsync();
            Check(report, currentWorkspace == "history" && HistoryWorkspace.Visibility == Visibility.Visible
                && RepositoryNavigation.Visibility == Visibility.Collapsed,
                "History menu command leaves repository tools", currentWorkspace);

            AppCommandMenuItem_Click(BranchesMenuItem, new RoutedEventArgs());
            await WaitForLatestOperationAsync();
            Check(report, currentWorkspace == "branches", "Branches menu command selects its secondary tab", currentWorkspace);

            MainNavigation.SelectedItem = SettingsNavigationItem;
            await WaitForLatestOperationAsync();
            for (var index = 0; index < SettingsCategoryList.Items.Count; index++)
            {
                SettingsCategoryList.SelectedIndex = index;
                await WaitForLayoutAsync();
                Check(report, SettingsPages.Children.Count(page => page.Visibility == Visibility.Visible) == 1
                    && SettingsPages.Children[index].Visibility == Visibility.Visible,
                    "settings category " + index + " opens one page", SettingsCategoryList.SelectedIndex.ToString());
            }

            mutationInProgress = true;
            SetBusy(true, "Checking navigation lock");
            AppCommandMenuItem_Click(ChangesMenuItem, new RoutedEventArgs());
            Check(report, currentWorkspace == "settings" && !MainNavigation.IsEnabled && !RepositoryNavigation.IsEnabled,
                "navigation stays locked during a mutation", currentWorkspace);
            mutationInProgress = false;
            SetBusy(false, "Navigation checks passed");

            AppCommandMenuItem_Click(ChangesMenuItem, new RoutedEventArgs());
            await WaitForLatestOperationAsync();
            await WaitForLayoutAsync();
            Check(report, currentWorkspace == "changes" && CommitPanel.ActualWidth > 0
                && CommitScroller.ActualWidth == ChangesCommitColumn.ActualWidth,
                "Changes command restores the commit column", currentWorkspace);
            Check(report, ReferenceEquals(ToolbarRemotePicker.SelectedItem, RemoteList.SelectedItem),
                "toolbar and repository page share the selected remote", selectedRemote?.Name ?? "no remote");
            await File.WriteAllTextAsync(options.OutputPath + ".checks.txt", report.ToString());
            await CaptureRootGridAsync(options.OutputPath);
        }
        catch (Exception exception)
        {
            mutationInProgress = false;
            await File.WriteAllTextAsync(options.OutputPath + ".checks.txt", report.ToString());
            await FailCaptureAsync(exception.Message, App.CommandLineArguments, exception);
        }
    }
}
