using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private enum NativeAppCommand
    {
        CreateRepository,
        OpenRepository,
        CloneRepository,
        Settings,
        Changes,
        History,
        ChooseRepository,
        Branches,
        Worktrees,
    }

    private void InitializeApplicationCommandAccelerators()
    {
        SettingsMenuItem.KeyboardAcceleratorTextOverride = "Ctrl+,";
        SettingsMenuItem.KeyboardAccelerators.Add(
            new KeyboardAccelerator
            {
                Key = (VirtualKey)0xBC,
                Modifiers = VirtualKeyModifiers.Control,
            });
    }

    private async void AppCommandMenuItem_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is not MenuFlyoutItem { Tag: string commandName }
            || !Enum.TryParse<NativeAppCommand>(
                commandName,
                ignoreCase: false,
                out var command))
        {
            return;
        }

        await ExecuteAppCommandAsync(command);
    }

    private async Task ExecuteAppCommandAsync(NativeAppCommand command)
    {
        try
        {
            switch (command)
            {
                case NativeAppCommand.CreateRepository:
                    if (!CanStartRepositorySetup())
                    {
                        return;
                    }

                    await ShowCreateRepositoryDialogAsync();
                    break;
                case NativeAppCommand.OpenRepository:
                    if (!OpenRepositoryButton.IsEnabled)
                    {
                        return;
                    }

                    await OpenRepositoryPickerAsync();
                    break;
                case NativeAppCommand.CloneRepository:
                    if (!CanStartRepositorySetup())
                    {
                        return;
                    }

                    await ShowCloneRepositoryDialogAsync();
                    break;
                case NativeAppCommand.Settings:
                    if (!MainNavigation.IsEnabled)
                    {
                        return;
                    }

                    MainNavigation.SelectedItem = MainNavigation.SettingsItem;
                    break;
                case NativeAppCommand.Changes:
                    if (!MainNavigation.IsEnabled)
                    {
                        return;
                    }

                    SelectWorkspaceFromAppCommand("changes");
                    break;
                case NativeAppCommand.History:
                    if (!MainNavigation.IsEnabled)
                    {
                        return;
                    }

                    SelectWorkspaceFromAppCommand("history");
                    break;
                case NativeAppCommand.ChooseRepository:
                    if (!RepositoryChooserButton.IsEnabled || repositoryPickerTask is not null)
                    {
                        return;
                    }

                    RepositoryChooserButton.Flyout?.ShowAt(RepositoryChooserButton);
                    break;
                case NativeAppCommand.Branches:
                    if (!MainNavigation.IsEnabled)
                    {
                        return;
                    }

                    SelectWorkspaceFromAppCommand("branches");
                    break;
                case NativeAppCommand.Worktrees:
                    if (!MainNavigation.IsEnabled)
                    {
                        return;
                    }

                    SelectWorkspaceFromAppCommand("worktrees");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, null);
            }
        }
        catch (Exception exception)
        {
            ShowError("Unable to run application command", exception);
        }
    }

    private void SelectWorkspaceFromAppCommand(string tag)
    {
        var item = MainNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Tag as string,
                    tag,
                    StringComparison.Ordinal));
        if (item is not null)
        {
            MainNavigation.SelectedItem = item;
        }
    }
}
