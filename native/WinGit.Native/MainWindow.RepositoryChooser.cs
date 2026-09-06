using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<RepositoryChooserRow>
        repositoryChooserRows = [];
    private AutoSuggestBox? repositoryChooserSearchBox;
    private ListView? repositoryChooserList;
    private TextBlock? repositoryChooserEmptyText;
    private Flyout? repositoryChooserFlyout;
    private bool repositoryChooserControlsInitialized;

    private void InitializeRepositoryChooserControls()
    {
        if (repositoryChooserControlsInitialized)
        {
            return;
        }

        repositoryChooserControlsInitialized = true;
        repositoryChooserSearchBox = new AutoSuggestBox
        {
            PlaceholderText = "Filter recent repositories",
            QueryIcon = new SymbolIcon(Symbol.Find),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            repositoryChooserSearchBox,
            "Filter recent repositories");
        repositoryChooserSearchBox.TextChanged += RepositoryChooserSearchBox_TextChanged;
        repositoryChooserSearchBox.QuerySubmitted += RepositoryChooserSearchBox_QuerySubmitted;

        repositoryChooserList = new ListView
        {
            ItemTemplate = (DataTemplate)RootGrid.Resources["RepositoryChooserRowTemplate"],
            ItemContainerStyle = (Style)RootGrid.Resources["ListItemStyle"],
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            MaxHeight = 360,
            ItemsSource = repositoryChooserRows,
        };
        AutomationProperties.SetName(repositoryChooserList, "Recent repositories");
        repositoryChooserList.ItemClick += RepositoryChooserList_ItemClick;
        repositoryChooserList.KeyDown += RepositoryChooserList_KeyDown;

        repositoryChooserEmptyText = new TextBlock
        {
            Text = "No matching recent repositories.",
            Style = (Style)RootGrid.Resources["SecondaryTextStyle"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var browseButton = new Button
        {
            Content = "Browse folder…",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(
            browseButton,
            "Browse for a repository folder");

        var panel = new StackPanel
        {
            MaxWidth = 440,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 8,
        };
        panel.Children.Add(new TextBlock
        {
            Text = "Switch repository",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Choose a recent repository or browse for another local folder.",
            Style = (Style)RootGrid.Resources["SecondaryTextStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(repositoryChooserSearchBox);
        panel.Children.Add(repositoryChooserList);
        panel.Children.Add(repositoryChooserEmptyText);
        panel.Children.Add(browseButton);

        repositoryChooserFlyout = new Flyout
        {
            Content = panel,
        };
        ApplyRepositoryChooserTheme();
        repositoryChooserFlyout.Opening += RepositoryChooserFlyout_Opening;
        repositoryChooserFlyout.Closed += RepositoryChooserFlyout_Closed;
        RepositoryChooserButton.Flyout = repositoryChooserFlyout;
        browseButton.Click += (_, args) =>
        {
            repositoryChooserFlyout?.Hide();
            OpenRepositoryButton_Click(browseButton, args);
        };

        RefreshRepositoryChooserRows();
    }

    private void ApplyRepositoryChooserTheme()
    {
        if (repositoryChooserFlyout?.Content is FrameworkElement content)
        {
            content.RequestedTheme = RootGrid.ActualTheme;
        }

        if (repositoryChooserFlyout is null)
        {
            return;
        }

        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(
            new Setter(FrameworkElement.RequestedThemeProperty, RootGrid.ActualTheme));
        repositoryChooserFlyout.FlyoutPresenterStyle = presenterStyle;
    }

    private void RepositoryChooserFlyout_Opening(object? sender, object args)
    {
        if (repositoryChooserSearchBox is null)
        {
            return;
        }

        repositoryChooserSearchBox.Text = string.Empty;
        RefreshRepositoryChooserRows();
        repositoryChooserSearchBox.Focus(FocusState.Programmatic);
    }

    private void RepositoryChooserFlyout_Closed(object? sender, object args)
    {
        if (repositoryChooserList is not null)
        {
            repositoryChooserList.SelectedItem = null;
        }
    }

    private void RepositoryChooserSearchBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args) =>
        RefreshRepositoryChooserRows();

    private async void RepositoryChooserSearchBox_QuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (repositoryChooserRows.Count == 1)
        {
            await OpenRepositoryChooserRowAsync(repositoryChooserRows[0]);
        }
        else if (repositoryChooserRows.Count > 0 && repositoryChooserList is not null)
        {
            repositoryChooserList.SelectedIndex = 0;
            await OpenRepositoryChooserRowAsync(repositoryChooserRows[0]);
        }
    }

    private async void RepositoryChooserList_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (args.ClickedItem is RepositoryChooserRow row)
        {
            await OpenRepositoryChooserRowAsync(row);
        }
    }

    private async void RepositoryChooserList_KeyDown(
        object sender,
        Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter
            || repositoryChooserList?.SelectedItem is not RepositoryChooserRow row)
        {
            return;
        }

        args.Handled = true;
        await OpenRepositoryChooserRowAsync(row);
    }

    private async Task OpenRepositoryChooserRowAsync(RepositoryChooserRow row)
    {
        if (row.IsMissing)
        {
            ShowError(
                "Repository folder was not found",
                new DirectoryNotFoundException(row.Path));
            return;
        }

        repositoryChooserFlyout?.Hide();
        await OpenRepositoryAsync(row.Path);
    }

    private void RepositoryChooserRemoveButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: RepositoryChooserRow row }
            || !row.IsMissing)
        {
            return;
        }

        NativeSettingsStore.RemoveRecentRepository(settings, row.Path);
        RefreshRecentRepositories();
        _ = SaveSettingsAsync();
    }

    private void RefreshRepositoryChooserRows()
    {
        if (!repositoryChooserControlsInitialized
            || repositoryChooserList is null
            || repositoryChooserSearchBox is null
            || repositoryChooserEmptyText is null)
        {
            return;
        }

        var query = repositoryChooserSearchBox.Text.Trim();
        var rows = settings.RecentRepositories
            .Select(path => new RepositoryChooserRow(
                path,
                repositoryRoot,
                NativeSettingsStore.GetRepositoryAlias(settings, path)))
            .Where(row => string.IsNullOrWhiteSpace(query)
                || row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        repositoryChooserRows.Clear();
        foreach (var row in rows)
        {
            repositoryChooserRows.Add(row);
        }

        repositoryChooserEmptyText.Visibility = rows.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        var currentPath = repositoryRoot;
        var currentLabel = currentPath is null
            ? "Choose repository"
            : new RepositoryChooserRow(
                currentPath,
                currentPath,
                NativeSettingsStore.GetRepositoryAlias(settings, currentPath)).DisplayName;
        RepositoryChooserLabel.Text = currentLabel;
        ToolTipService.SetToolTip(
            RepositoryChooserButton,
            currentPath ?? "Choose a repository");
        AutomationProperties.SetName(
            RepositoryChooserButton,
            currentPath is null
                ? "Choose repository"
                : $"Switch repository; current {currentLabel}");
    }

}
