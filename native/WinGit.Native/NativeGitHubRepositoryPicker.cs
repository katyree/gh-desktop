using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core.GitHub;

namespace WinGit.Native;

/// <summary>
/// The native controls embedded in the clone dialog for browsing a connected
/// GitHub account. It has no network or credential responsibilities; the
/// owning window supplies account choices and catalog rows.
/// </summary>
internal sealed class NativeGitHubRepositoryPicker
{
    private readonly ObservableCollection<NativeGitHubAccountChoice> accountChoices = [];
    private readonly ObservableCollection<NativeGitHubRepositoryRow> visibleRepositories = [];
    private NativeGitHubRepositoryRow[] allRepositories = [];
    private bool suppressSelectionEvents;
    private bool isLoading;

    public NativeGitHubRepositoryPicker()
    {
        AccountBox = new ComboBox
        {
            Header = "Connected GitHub account",
            DisplayMemberPath = nameof(NativeGitHubAccountChoice.DisplayLabel),
            ItemsSource = accountChoices,
            MinWidth = 340,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(AccountBox, "GitHub account for clone");
        AccountBox.SelectionChanged += AccountBox_SelectionChanged;

        FilterBox = new TextBox
        {
            Header = "Filter repositories",
            PlaceholderText = "Search by owner or repository name",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(FilterBox, "Filter GitHub repositories");
        FilterBox.TextChanged += FilterBox_TextChanged;

        RepositoryList = new ListView
        {
            ItemsSource = visibleRepositories,
            DisplayMemberPath = nameof(NativeGitHubRepositoryRow.DisplayName),
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 190,
            MinHeight = 56,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false,
        };
        AutomationProperties.SetName(RepositoryList, "GitHub repositories");
        RepositoryList.SelectionChanged += RepositoryList_SelectionChanged;

        EmptyText = new TextBlock
        {
            Text = "No repositories loaded.",
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(EmptyText, "GitHub repository list status");

        ProtocolBox = new ComboBox
        {
            Header = "Clone URL",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(ProtocolBox, "GitHub clone URL protocol");
        ProtocolBox.Items.Add(new ComboBoxItem
        {
            Content = "HTTPS · configured Git authentication",
            Tag = NativeGitHubCloneProtocol.Https,
        });
        ProtocolBox.Items.Add(new ComboBoxItem
        {
            Content = "SSH · configured SSH key",
            Tag = NativeGitHubCloneProtocol.Ssh,
        });
        ProtocolBox.SelectedIndex = 0;
        ProtocolBox.SelectionChanged += ProtocolBox_SelectionChanged;

        SelectedDetailsText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(SelectedDetailsText, "Selected GitHub repository details");

        StatusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(StatusText, "GitHub repository loading status");

        var pickerContent = new StackPanel
        {
            Spacing = 8,
        };
        pickerContent.Children.Add(new TextBlock
        {
            Text = "GitHub repositories",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        pickerContent.Children.Add(new TextBlock
        {
            Text = "Choose a connected account, then select a repository. You can still paste a direct URL below.",
            TextWrapping = TextWrapping.Wrap,
        });
        pickerContent.Children.Add(AccountBox);
        pickerContent.Children.Add(FilterBox);
        pickerContent.Children.Add(RepositoryList);
        pickerContent.Children.Add(EmptyText);
        pickerContent.Children.Add(ProtocolBox);
        pickerContent.Children.Add(SelectedDetailsText);
        pickerContent.Children.Add(StatusText);
        View = new ScrollViewer
        {
            Content = pickerContent,
            MaxHeight = 390,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    public ScrollViewer View { get; }

    public ComboBox AccountBox { get; }

    public TextBox FilterBox { get; }

    public ListView RepositoryList { get; }

    public ComboBox ProtocolBox { get; }

    public TextBlock EmptyText { get; }

    public TextBlock SelectedDetailsText { get; }

    public TextBlock StatusText { get; }

    public NativeGitHubAccountChoice? SelectedAccount =>
        AccountBox.SelectedItem as NativeGitHubAccountChoice;

    public NativeGitHubRepositoryRow? SelectedRepository =>
        RepositoryList.SelectedItem as NativeGitHubRepositoryRow;

    public NativeGitHubCloneProtocol SelectedProtocol =>
        (ProtocolBox.SelectedItem as ComboBoxItem)?.Tag is NativeGitHubCloneProtocol protocol
            ? protocol
            : NativeGitHubCloneProtocol.Https;

    public event EventHandler? AccountSelectionChanged;

    public event EventHandler? RepositorySelectionChanged;

    public event EventHandler? ProtocolSelectionChanged;

    public void SetAccounts(
        IReadOnlyList<NativeGitHubAccountChoice> choices,
        NativeGitHubAccountChoice? preferred = null)
    {
        ArgumentNullException.ThrowIfNull(choices);
        suppressSelectionEvents = true;
        try
        {
            accountChoices.Clear();
            foreach (var choice in choices)
            {
                accountChoices.Add(choice);
            }

            AccountBox.SelectedItem = preferred is not null &&
                accountChoices.Contains(preferred)
                ? preferred
                : accountChoices.FirstOrDefault();
        }
        finally
        {
            suppressSelectionEvents = false;
        }

        AccountBox.IsEnabled = accountChoices.Count > 0;
        if (accountChoices.Count == 0)
        {
            SetRepositories([]);
        }
    }

    public void SetRepositories(IReadOnlyList<GitHubRepository> repositories)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        allRepositories = repositories
            .Select(repository => new NativeGitHubRepositoryRow(repository))
            .ToArray();
        suppressSelectionEvents = true;
        try
        {
            RepositoryList.SelectedIndex = -1;
            visibleRepositories.Clear();
            foreach (var repository in allRepositories)
            {
                if (MatchesFilter(repository))
                {
                    visibleRepositories.Add(repository);
                }
            }
        }
        finally
        {
            suppressSelectionEvents = false;
        }

        UpdateEmptyText();
        UpdateSelectionPresentation();
    }

    public void SetLoading(bool loading, string? accountName = null)
    {
        isLoading = loading;
        RepositoryList.IsEnabled = !loading && allRepositories.Length > 0;
        ProtocolBox.IsEnabled = !loading && SelectedRepository is not null;
        FilterBox.IsEnabled = !loading || allRepositories.Length > 0;
        if (loading)
        {
            SetStatus(string.IsNullOrWhiteSpace(accountName)
                ? "Loading GitHub repositories…"
                : $"Loading repositories for {accountName}…");
        }
    }

    public void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public void ClearRepositorySelection()
    {
        suppressSelectionEvents = true;
        try
        {
            RepositoryList.SelectedIndex = -1;
        }
        finally
        {
            suppressSelectionEvents = false;
        }

        UpdateSelectionPresentation();
    }

    private void AccountBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (!suppressSelectionEvents)
        {
            AccountSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RepositoryList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (suppressSelectionEvents)
        {
            return;
        }

        UpdateSelectionPresentation();
        RepositorySelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ProtocolBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (!suppressSelectionEvents)
        {
            ProtocolSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        suppressSelectionEvents = true;
        try
        {
            var selected = SelectedRepository;
            visibleRepositories.Clear();
            foreach (var repository in allRepositories)
            {
                if (MatchesFilter(repository))
                {
                    visibleRepositories.Add(repository);
                }
            }

            if (selected is not null && visibleRepositories.Contains(selected))
            {
                RepositoryList.SelectedItem = selected;
            }
            else
            {
                RepositoryList.SelectedIndex = -1;
            }
        }
        finally
        {
            suppressSelectionEvents = false;
        }

        UpdateEmptyText();
        UpdateSelectionPresentation();
    }

    private bool MatchesFilter(NativeGitHubRepositoryRow repository)
    {
        var filter = FilterBox.Text.Trim();
        return filter.Length == 0 ||
            repository.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateEmptyText()
    {
        EmptyText.Text = allRepositories.Length == 0
            ? "No repositories are available for this account."
            : "No repositories match this filter.";
        EmptyText.Visibility = visibleRepositories.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateSelectionPresentation()
    {
        var selected = SelectedRepository;
        SelectedDetailsText.Text = selected is null
            ? string.Empty
            : selected.Details;
        SelectedDetailsText.Visibility = selected is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        ProtocolBox.IsEnabled = !isLoading && selected is not null;
    }
}
