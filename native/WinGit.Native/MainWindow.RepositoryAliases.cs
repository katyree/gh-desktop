using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private async void RecentRepositoryRenameButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is not Button { Tag: RepositoryChooserRow row })
        {
            return;
        }

        var aliasBox = new TextBox
        {
            Header = "Repository alias",
            Text = row.Alias ?? string.Empty,
            PlaceholderText = row.RepositoryName,
            MaxLength = NativeSettings.MaximumRepositoryAliasLength,
        };
        AutomationProperties.SetName(aliasBox, "Repository alias");
        var dialog = CreateDialog(
            "Rename repository alias",
            "Save",
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Choose the label WinGit shows for \"{row.Path}\". Leave it blank to use the folder name.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    aliasBox,
                },
            });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            NativeSettingsStore.SetRepositoryAlias(settings, row.Path, aliasBox.Text);
        }
        catch (ArgumentException exception)
        {
            ShowError("Unable to rename repository alias", exception);
            return;
        }

        RefreshRecentRepositories();
        _ = SaveSettingsAsync();
    }

    private void RecentRepositoryRemoveButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is not Button { Tag: RepositoryChooserRow row })
        {
            return;
        }

        NativeSettingsStore.RemoveRecentRepository(settings, row.Path);
        RefreshRecentRepositories();
        _ = SaveSettingsAsync();
    }
}
