using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core.GitHub;
using Windows.Foundation;
using Windows.System;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private bool githubPullRequestDialogOpen;
    private readonly List<CancellationTokenSource> githubPullRequestDialogCancellations = [];
    private readonly List<Task> githubPullRequestDialogLoadTasks = [];

    private void RegisterGitHubPullRequestDialog(
        CancellationTokenSource cancellation)
    {
        githubPullRequestDialogCancellations.Add(cancellation);
        if (githubDisposed)
        {
            cancellation.Cancel();
        }
    }

    private void RegisterGitHubPullRequestLoad(Task task)
    {
        githubPullRequestDialogLoadTasks.Add(task);
    }

    private Task[] CancelAndSnapshotGitHubPullRequestLoads()
    {
        foreach (var cancellation in githubPullRequestDialogCancellations.ToArray())
        {
            cancellation.Cancel();
        }

        return githubPullRequestDialogLoadTasks.ToArray();
    }

    private void UnregisterGitHubPullRequestDialog(
        CancellationTokenSource cancellation,
        IReadOnlyList<Task> loadTasks)
    {
        githubPullRequestDialogCancellations.Remove(cancellation);
        foreach (var task in loadTasks)
        {
            githubPullRequestDialogLoadTasks.Remove(task);
        }
    }

    private async void BrowsePullRequestsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (githubPullRequestDialogOpen ||
            githubDisposed ||
            diagnosticCaptureMode)
        {
            return;
        }

        githubPullRequestDialogOpen = true;
        try
        {
            await ShowGitHubPullRequestBrowserAsync();
        }
        catch (Exception)
        {
            if (!githubDisposed)
            {
                ShowError(
                    "GitHub pull requests unavailable",
                    new InvalidOperationException(
                        "The pull request browser could not be opened."));
            }
        }
        finally
        {
            githubPullRequestDialogOpen = false;
        }
    }

    private async Task ShowGitHubPullRequestBrowserAsync()
    {
        var remote = selectedRemote;
        if (remote is null)
        {
            ShowError(
                "GitHub pull requests unavailable",
                new InvalidOperationException("Select a remote first."));
            return;
        }

        if (!GitHubRemoteRepositoryIdentity.TryParse(
                remote.Remote.Url,
                out var repository) ||
            repository is null)
        {
            ShowError(
                "GitHub pull requests unavailable",
                new InvalidOperationException(
                    "The selected remote is not a supported GitHub HTTPS or SSH repository."));
            return;
        }

        try
        {
            await EnsureGitHubAccountsLoadedAsync();
        }
        catch (Exception)
        {
            // Existing account rows remain useful; the dialog explains when
            // no matching account is available.
        }

        if (githubDisposed || diagnosticCaptureMode)
        {
            return;
        }

        var choices = githubAccountRows
            .Where(row => AreSameGitHubOrigin(
                row.Summary.ApiOrigin,
                repository.ApiOrigin))
            .Select(row => new NativeGitHubAccountChoice(row.Summary))
            .ToArray();

        using var dialogCancellation = new CancellationTokenSource();
        RegisterGitHubPullRequestDialog(dialogCancellation);

        var accountBox = new ComboBox
        {
            Header = "GitHub account for this remote",
            DisplayMemberPath = nameof(NativeGitHubAccountChoice.DisplayLabel),
            ItemsSource = choices,
            IsEnabled = choices.Length > 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        accountBox.SelectedIndex = -1;
        AutomationProperties.SetName(
            accountBox,
            "GitHub account for pull requests");

        var loadButton = new Button
        {
            Content = "Load pull requests",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(
            loadButton,
            "Load GitHub pull requests");

        var statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(
            statusText,
            "GitHub pull request loading status");

        var pullRequestRows = new ObservableCollection<NativeGitHubPullRequestRow>();
        var pullRequestList = new ListView
        {
            ItemsSource = pullRequestRows,
            DisplayMemberPath = nameof(NativeGitHubPullRequestRow.DisplayName),
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 190,
            MinHeight = 56,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            pullRequestList,
            "GitHub pull requests");

        var pullRequestEmptyText = new TextBlock
        {
            Text = "No open pull requests.",
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(
            pullRequestEmptyText,
            "GitHub pull request list status");

        var pullRequestDetailText = new TextBlock
        {
            Text = "Select a pull request to read its details.",
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(
            pullRequestDetailText,
            "Selected GitHub pull request details");

        var detailStatusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(
            detailStatusText,
            "Selected GitHub pull request loading status");

        var openButton = new Button
        {
            Content = "Open on GitHub",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(
            openButton,
            "Open selected pull request on GitHub");

        var changedFileRows =
            new ObservableCollection<NativeGitHubPullRequestFileRow>();
        var changedFileList = new ListView
        {
            ItemsSource = changedFileRows,
            DisplayMemberPath = nameof(NativeGitHubPullRequestFileRow.DisplayName),
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 170,
            MinHeight = 48,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            changedFileList,
            "Pull request changed files");

        var changedFilePatchText = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
        };
        var changedFilePatchScrollViewer = new ScrollViewer
        {
            Content = changedFilePatchText,
            MaxHeight = 260,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetName(
            changedFilePatchScrollViewer,
            "Selected pull request file patch");

        var reviewRows =
            new ObservableCollection<NativeGitHubPullRequestReviewRow>();
        var reviewList = new ListView
        {
            ItemsSource = reviewRows,
            DisplayMemberPath = nameof(NativeGitHubPullRequestReviewRow.DisplayName),
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 170,
            MinHeight = 48,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            reviewList,
            "Pull request reviews");

        var reviewDetailText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(
            reviewDetailText,
            "Selected pull request review body");

        var checksStatusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(
            checksStatusText,
            "Pull request checks loading status");

        var legacyStatusRows =
            new ObservableCollection<NativeGitHubCommitStatusRow>();
        var legacyStatusList = new ListView
        {
            ItemsSource = legacyStatusRows,
            DisplayMemberPath = nameof(NativeGitHubCommitStatusRow.DisplayName),
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 170,
            MinHeight = 48,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            legacyStatusList,
            "Legacy pull request commit statuses");

        var legacyStatusEmptyText = new TextBlock
        {
            Text = "No legacy commit status contexts for this commit.",
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(
            legacyStatusEmptyText,
            "Legacy commit status empty state");

        var modernCheckRows =
            new ObservableCollection<NativeGitHubCheckRunRow>();
        var modernCheckList = new ListView
        {
            ItemsSource = modernCheckRows,
            DisplayMemberPath = nameof(NativeGitHubCheckRunRow.DisplayName),
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 170,
            MinHeight = 48,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            modernCheckList,
            "Modern pull request check runs");

        var modernCheckEmptyText = new TextBlock
        {
            Text = "No modern check runs for this commit.",
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(
            modernCheckEmptyText,
            "Modern check run empty state");

        var checksDetailText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(
            checksDetailText,
            "Selected pull request check details");

        var openCheckLinkButton = new Button
        {
            Content = "Open selected check link",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(
            openCheckLinkButton,
            "Open selected pull request check link");

        var checkJobStepsStatusText = new TextBlock
        {
            Text = "Select a modern check run to load its job steps.",
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(
            checkJobStepsStatusText,
            "Selected check job steps status");

        var checkJobStepRows =
            new ObservableCollection<NativeGitHubCheckRunJobStepRow>();
        var checkJobStepList = new ListView
        {
            ItemsSource = checkJobStepRows,
            DisplayMemberPath = nameof(NativeGitHubCheckRunJobStepRow.DisplayName),
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 170,
            MinHeight = 48,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            checkJobStepList,
            "Selected modern check job steps");

        var rerunCheckButton = new Button
        {
            Content = "Re-run selected check",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        void SetRerunButtonText(string text)
        {
            rerunCheckButton.Content = text;
            AutomationProperties.SetName(rerunCheckButton, text);
        }

        SetRerunButtonText("Re-run selected check");

        var rerunStatusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(
            rerunStatusText,
            "Selected check rerun status");

        var repositoryText = new TextBlock
        {
            Text = $"Remote: {repository.Hostname} · {repository.Owner}/{repository.Name}",
            TextWrapping = TextWrapping.Wrap,
        };
        var accountHintText = new TextBlock
        {
            Text = choices.Length == 0
                ? $"No stored GitHub account matches {repository.Hostname}. Sign in from Settings, then try again."
                : "Choose the stored account for this remote. Its host and login are shown in the account list.",
            TextWrapping = TextWrapping.Wrap,
        };

        var overviewPanel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                pullRequestDetailText,
                detailStatusText,
                openButton,
            },
        };
        var filesPanel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Changed files",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                changedFileList,
                changedFilePatchScrollViewer,
            },
        };
        var reviewsPanel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Reviews",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                reviewList,
                reviewDetailText,
            },
        };
        var checksPanel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                checksStatusText,
                new TextBlock
                {
                    Text = "Legacy commit statuses",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                legacyStatusList,
                legacyStatusEmptyText,
                new TextBlock
                {
                    Text = "Modern check runs",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                modernCheckList,
                modernCheckEmptyText,
                checksDetailText,
                openCheckLinkButton,
                checkJobStepsStatusText,
                checkJobStepList,
                rerunCheckButton,
                rerunStatusText,
            },
        };
        var detailTabs = new TabView
        {
            IsAddTabButtonVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        detailTabs.TabItems.Add(
            new TabViewItem
            {
                Header = "Overview",
                Content = overviewPanel,
                IsClosable = false,
            });
        detailTabs.TabItems.Add(
            new TabViewItem
            {
                Header = "Files",
                Content = filesPanel,
                IsClosable = false,
            });
        detailTabs.TabItems.Add(
            new TabViewItem
            {
                Header = "Reviews",
                Content = reviewsPanel,
                IsClosable = false,
            });
        detailTabs.TabItems.Add(
            new TabViewItem
            {
                Header = "Checks",
                Content = checksPanel,
                IsClosable = false,
            });
        AutomationProperties.SetName(
            detailTabs,
            "Pull request overview, files, reviews, and checks");

        var contentPanel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                repositoryText,
                accountHintText,
                accountBox,
                loadButton,
                statusText,
                new TextBlock
                {
                    Text = "Open pull requests",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                pullRequestList,
                pullRequestEmptyText,
                detailTabs,
            },
        };
        var dialog = CreateDialog(
            $"Pull requests · {repository.Owner}/{repository.Name}",
            "Close",
            new ScrollViewer
            {
                Content = contentPanel,
                MaxHeight = 680,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });

        if (choices.Length == 0)
        {
            statusText.Text =
                "Pull requests need a stored GitHub account for the selected remote host.";
        }

        CancellationTokenSource? listCancellation = null;
        CancellationTokenSource? detailCancellation = null;
        var ownedTasks = new List<Task>();
        var accountGeneration = 0L;
        var detailGeneration = 0L;
        var listLoading = false;
        NativeGitHubPullRequestRow? selectedPullRequest = null;
        Uri? selectedCheckLink = null;
        GitHubRemoteRepositoryIdentity? selectedChecksRepository = null;
        GitHubAccountSession? selectedChecksSession = null;
        NativeGitHubAccountChoice? selectedChecksAccount = null;
        NativeGitHubPullRequestRow? selectedChecksPullRequest = null;
        string? selectedChecksHeadSha = null;
        long selectedChecksGeneration = 0;
        CancellationTokenSource? selectedChecksCancellation = null;
        GitHubCheckRunJobStepsResult? selectedCheckJobSteps = null;

        bool IsAccountLoadCurrent(
            NativeGitHubAccountChoice choice,
            long generation,
            CancellationTokenSource cancellation) =>
            !githubDisposed &&
            !dialogCancellation.IsCancellationRequested &&
            !cancellation.IsCancellationRequested &&
            generation == accountGeneration &&
            ReferenceEquals(accountBox.SelectedItem, choice);

        bool IsDetailLoadCurrent(
            NativeGitHubPullRequestRow row,
            long generation,
            CancellationTokenSource cancellation) =>
            !githubDisposed &&
            !dialogCancellation.IsCancellationRequested &&
            !cancellation.IsCancellationRequested &&
            generation == detailGeneration &&
            ReferenceEquals(pullRequestList.SelectedItem, row);

        bool IsChecksActionCurrent(
            NativeGitHubPullRequestRow row,
            NativeGitHubAccountChoice accountChoice,
            long generation,
            CancellationTokenSource cancellation) =>
            IsDetailLoadCurrent(row, generation, cancellation) &&
            ReferenceEquals(accountBox.SelectedItem, accountChoice) &&
            ReferenceEquals(selectedChecksPullRequest, row) &&
            selectedChecksGeneration == generation;

        void TrackLoad(Task task)
        {
            ownedTasks.Add(task);
            RegisterGitHubPullRequestLoad(task);
        }

        void ClearPullRequestDetailPresentation(string detailMessage)
        {
            selectedPullRequest = null;
            pullRequestDetailText.Text = detailMessage;
            detailStatusText.Text = string.Empty;
            openButton.IsEnabled = false;
            changedFileRows.Clear();
            changedFileList.SelectedIndex = -1;
            changedFileList.IsEnabled = false;
            changedFilePatchText.Text = string.Empty;
            reviewRows.Clear();
            reviewList.SelectedIndex = -1;
            reviewList.IsEnabled = false;
            reviewDetailText.Text = string.Empty;
            checksStatusText.Text = string.Empty;
            selectedChecksRepository = null;
            selectedChecksSession = null;
            selectedChecksAccount = null;
            selectedChecksPullRequest = null;
            selectedChecksHeadSha = null;
            selectedChecksGeneration = 0;
            selectedChecksCancellation = null;
            selectedCheckJobSteps = null;
            legacyStatusRows.Clear();
            legacyStatusList.SelectedIndex = -1;
            legacyStatusList.IsEnabled = false;
            legacyStatusEmptyText.Visibility = Visibility.Collapsed;
            modernCheckRows.Clear();
            modernCheckList.SelectedIndex = -1;
            modernCheckList.IsEnabled = false;
            modernCheckEmptyText.Visibility = Visibility.Collapsed;
            checksDetailText.Text = string.Empty;
            selectedCheckLink = null;
            openCheckLinkButton.IsEnabled = false;
            checkJobStepsStatusText.Text =
                "Select a modern check run to load its job steps.";
            checkJobStepRows.Clear();
            checkJobStepList.SelectedIndex = -1;
            checkJobStepList.IsEnabled = false;
            rerunCheckButton.IsEnabled = false;
            SetRerunButtonText("Re-run selected check");
            rerunStatusText.Text = string.Empty;
        }

        void ClearPullRequestPresentation()
        {
            pullRequestRows.Clear();
            pullRequestList.SelectedIndex = -1;
            pullRequestList.IsEnabled = false;
            pullRequestEmptyText.Visibility = Visibility.Collapsed;
            ClearPullRequestDetailPresentation(
                "Select a pull request to read its details.");
        }

        void SetChecksLoading(string headSha)
        {
            checksStatusText.Text =
                $"Loading legacy commit statuses and modern check runs for head {ShortObjectId(headSha)}…";
            legacyStatusRows.Clear();
            legacyStatusList.SelectedIndex = -1;
            legacyStatusList.IsEnabled = false;
            legacyStatusEmptyText.Visibility = Visibility.Collapsed;
            modernCheckRows.Clear();
            modernCheckList.SelectedIndex = -1;
            modernCheckList.IsEnabled = false;
            modernCheckEmptyText.Visibility = Visibility.Collapsed;
            checksDetailText.Text = string.Empty;
            selectedCheckLink = null;
            openCheckLinkButton.IsEnabled = false;
            selectedCheckJobSteps = null;
            checkJobStepsStatusText.Text =
                "Select a modern check run to load its job steps.";
            checkJobStepRows.Clear();
            checkJobStepList.SelectedIndex = -1;
            checkJobStepList.IsEnabled = false;
            rerunCheckButton.IsEnabled = false;
            SetRerunButtonText("Re-run selected check");
            rerunStatusText.Text = string.Empty;
        }

        void ApplyChecksResult(GitHubPullRequestChecksResult result)
        {
            legacyStatusRows.Clear();
            foreach (var status in result.Status.Statuses)
            {
                legacyStatusRows.Add(new NativeGitHubCommitStatusRow(status));
            }

            legacyStatusList.IsEnabled = legacyStatusRows.Count > 0;
            legacyStatusEmptyText.Visibility = result.Status.IsComplete &&
                legacyStatusRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            modernCheckRows.Clear();
            foreach (var checkRun in result.CheckRuns.CheckRuns)
            {
                modernCheckRows.Add(new NativeGitHubCheckRunRow(checkRun));
            }

            modernCheckList.IsEnabled = modernCheckRows.Count > 0;
            modernCheckEmptyText.Visibility = result.CheckRuns.IsComplete &&
                modernCheckRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            checksDetailText.Text = string.Empty;
            selectedCheckLink = null;
            openCheckLinkButton.IsEnabled = false;
            selectedCheckJobSteps = null;
            checkJobStepsStatusText.Text =
                "Select a modern check run to load its job steps.";
            checkJobStepRows.Clear();
            checkJobStepList.SelectedIndex = -1;
            checkJobStepList.IsEnabled = false;
            rerunCheckButton.IsEnabled = false;
            SetRerunButtonText("Re-run selected check");
            rerunStatusText.Text = string.Empty;
            checksStatusText.Text = FormatPullRequestChecksStatus(result);
        }

        void LegacyStatusList_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (legacyStatusList.SelectedItem is not NativeGitHubCommitStatusRow row)
            {
                if (modernCheckList.SelectedItem is null)
                {
                    checksDetailText.Text = string.Empty;
                    selectedCheckLink = null;
                    openCheckLinkButton.IsEnabled = false;
                }

                return;
            }

            modernCheckList.SelectedIndex = -1;
            selectedCheckJobSteps = null;
            checkJobStepRows.Clear();
            checkJobStepList.SelectedIndex = -1;
            checkJobStepList.IsEnabled = false;
            checkJobStepsStatusText.Text =
                "Select a modern check run to load its job steps.";
            rerunCheckButton.IsEnabled = false;
            rerunStatusText.Text = string.Empty;
            checksDetailText.Text = row.Details;
            selectedCheckLink = row.Status.TargetUrl is { } targetUrl &&
                IsSafeExternalBrowserUri(targetUrl)
                ? targetUrl
                : null;
            openCheckLinkButton.IsEnabled = selectedCheckLink is not null;
        }

        void ApplyCheckJobStepsResult(
            GitHubCheckRunJobStepsResult result)
        {
            checkJobStepRows.Clear();
            foreach (var step in result.Steps)
            {
                checkJobStepRows.Add(
                    new NativeGitHubCheckRunJobStepRow(step));
            }

            checkJobStepList.IsEnabled = checkJobStepRows.Count > 0;
            if (result.ErrorKind is { } errorKind)
            {
                checkJobStepsStatusText.Text =
                    result.Steps.Count == 0
                        ? $"Job steps could not be loaded. {FormatPullRequestChecksError(errorKind)}"
                        : $"{result.Steps.Count} job steps loaded; " +
                            FormatPullRequestChecksError(errorKind);
            }
            else if (!result.IsSupported)
            {
                checkJobStepsStatusText.Text =
                    "This check provider does not expose GitHub Actions job steps.";
            }
            else if (result.Steps.Count == 0)
            {
                checkJobStepsStatusText.Text =
                    "No job steps were returned for this check run.";
            }
            else if (!result.IsComplete)
            {
                checkJobStepsStatusText.Text =
                    $"{result.Steps.Count} job steps loaded; the result is partial.";
            }
            else
            {
                checkJobStepsStatusText.Text =
                    $"{result.Steps.Count} job steps loaded.";
            }

            if (modernCheckList.SelectedItem is NativeGitHubCheckRunRow row)
            {
                SetRerunButtonText(result.ErrorKind is not null
                    ? "Re-run unavailable"
                    : result.IsSupported
                        ? "Re-run Actions job"
                        : "Re-run check suite (all checks)");
                rerunCheckButton.IsEnabled =
                    result.IsComplete &&
                    result.ErrorKind is null &&
                    row.CheckRun.Status == GitHubCheckRunStatusKind.Completed &&
                    row.CheckRun.CheckSuiteId is not null;
            }
        }

        async Task LoadSelectedCheckJobStepsAsync(
            NativeGitHubCheckRunRow row,
            NativeGitHubPullRequestRow detailRow,
            NativeGitHubAccountChoice accountChoice,
            GitHubAccountSession session,
            GitHubRemoteRepositoryIdentity checksRepository,
            string headSha,
            long generation,
            CancellationTokenSource cancellation)
        {
            if (!IsChecksActionCurrent(
                    detailRow,
                    accountChoice,
                    generation,
                    cancellation) ||
                !ReferenceEquals(modernCheckList.SelectedItem, row))
            {
                return;
            }

            selectedCheckJobSteps = null;
            checkJobStepRows.Clear();
            checkJobStepList.SelectedIndex = -1;
            checkJobStepList.IsEnabled = false;
            if (row.CheckRun.CheckSuiteId is null)
            {
                SetRerunButtonText("Re-run unavailable");
                rerunCheckButton.IsEnabled = false;
                checkJobStepsStatusText.Text =
                    "This check provider does not expose job steps or a rerun action.";
                return;
            }

            checkJobStepsStatusText.Text = "Loading selected check job steps…";
            try
            {
                using var checksClient = new GitHubPullRequestChecksClient(
                    CreateGitHubPullRequestChecksOptions(
                        checksRepository.ApiOrigin));
                var result = await checksClient.LoadJobStepsAsync(
                        session,
                        checksRepository,
                        row.CheckRun,
                        headSha,
                        cancellation.Token)
                    .ConfigureAwait(true);
                if (!IsChecksActionCurrent(
                        detailRow,
                        accountChoice,
                        generation,
                        cancellation) ||
                    !ReferenceEquals(modernCheckList.SelectedItem, row))
                {
                    return;
                }

                selectedCheckJobSteps = result;
                ApplyCheckJobStepsResult(result);
            }
            catch (GitHubPullRequestChecksCancelledException)
            {
                if (IsChecksActionCurrent(
                        detailRow,
                        accountChoice,
                        generation,
                        cancellation))
                {
                    checkJobStepsStatusText.Text =
                        "Selected check job-step loading was cancelled.";
                }
            }
            catch (GitHubPullRequestChecksException exception)
            {
                if (IsChecksActionCurrent(
                        detailRow,
                        accountChoice,
                        generation,
                        cancellation))
                {
                    checkJobStepsStatusText.Text =
                        $"Job steps could not be loaded. {FormatPullRequestChecksError(exception.Kind)}";
                }
            }
            catch (OperationCanceledException)
            {
                if (IsChecksActionCurrent(
                        detailRow,
                        accountChoice,
                        generation,
                        cancellation))
                {
                    checkJobStepsStatusText.Text =
                        "Selected check job-step loading was cancelled.";
                }
            }
            catch (Exception)
            {
                if (IsChecksActionCurrent(
                        detailRow,
                        accountChoice,
                        generation,
                        cancellation))
                {
                    checkJobStepsStatusText.Text =
                        "Job steps could not be loaded.";
                }
            }
        }

        void ModernCheckList_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (modernCheckList.SelectedItem is not NativeGitHubCheckRunRow row)
            {
                if (legacyStatusList.SelectedItem is null)
                {
                    checksDetailText.Text = string.Empty;
                    selectedCheckLink = null;
                    openCheckLinkButton.IsEnabled = false;
                    selectedCheckJobSteps = null;
                    checkJobStepRows.Clear();
                    checkJobStepList.SelectedIndex = -1;
                    checkJobStepList.IsEnabled = false;
                    checkJobStepsStatusText.Text =
                        "Select a modern check run to load its job steps.";
                    rerunCheckButton.IsEnabled = false;
                    rerunStatusText.Text = string.Empty;
                }

                return;
            }

            legacyStatusList.SelectedIndex = -1;
            checksDetailText.Text = row.Details;
            selectedCheckLink = IsSafePullRequestBrowserUri(
                row.CheckRun.DetailsUrl,
                repository)
                ? row.CheckRun.DetailsUrl
                : null;
            openCheckLinkButton.IsEnabled = selectedCheckLink is not null;

            selectedCheckJobSteps = null;
            checkJobStepRows.Clear();
            checkJobStepList.SelectedIndex = -1;
            checkJobStepList.IsEnabled = false;
            rerunStatusText.Text = string.Empty;
            rerunCheckButton.IsEnabled = false;
            SetRerunButtonText("Re-run selected check");

            var detailRow = selectedChecksPullRequest;
            var accountChoice = selectedChecksAccount;
            var session = selectedChecksSession;
            var checksRepository = selectedChecksRepository;
            var headSha = selectedChecksHeadSha;
            var generation = selectedChecksGeneration;
            var cancellation = selectedChecksCancellation;
            if (detailRow is null ||
                accountChoice is null ||
                session is null ||
                checksRepository is null ||
                headSha is null ||
                cancellation is null)
            {
                checkJobStepsStatusText.Text =
                    "Select a pull request before loading check job steps.";
                return;
            }

            var task = LoadSelectedCheckJobStepsAsync(
                row,
                detailRow,
                accountChoice,
                session,
                checksRepository,
                headSha,
                generation,
                cancellation);
            TrackLoad(task);
        }

        async Task RerunCheckAsync()
        {
            if (modernCheckList.SelectedItem is not NativeGitHubCheckRunRow row)
            {
                rerunStatusText.Text =
                    "Select a modern check run before requesting a rerun.";
                return;
            }

            var detailRow = selectedChecksPullRequest;
            var accountChoice = selectedChecksAccount;
            var session = selectedChecksSession;
            var checksRepository = selectedChecksRepository;
            var headSha = selectedChecksHeadSha;
            var generation = selectedChecksGeneration;
            var cancellation = selectedChecksCancellation;
            if (detailRow is null ||
                accountChoice is null ||
                session is null ||
                checksRepository is null ||
                headSha is null ||
                cancellation is null ||
                row.CheckRun.Status != GitHubCheckRunStatusKind.Completed ||
                row.CheckRun.CheckSuiteId is null)
            {
                rerunStatusText.Text =
                    "This selected check cannot be rerun by its provider.";
                return;
            }

            if (!IsChecksActionCurrent(
                    detailRow,
                    accountChoice,
                    generation,
                    cancellation) ||
                !ReferenceEquals(modernCheckList.SelectedItem, row))
            {
                rerunStatusText.Text =
                    "The selected pull request changed; choose the check again.";
                return;
            }

            var jobSteps = selectedCheckJobSteps;
            if (jobSteps is null ||
                !jobSteps.IsComplete ||
                jobSteps.ErrorKind is not null)
            {
                rerunStatusText.Text =
                    "Rerun is unavailable until the selected check target finishes loading.";
                return;
            }

            rerunCheckButton.IsEnabled = false;
            rerunStatusText.Text = "Requesting a rerun for the selected check…";
            var actionsJobId = jobSteps.ActionsJobId;
            try
            {
                using var checksClient = new GitHubPullRequestChecksClient(
                    CreateGitHubPullRequestChecksOptions(
                        checksRepository.ApiOrigin));
                var result = await checksClient.RerunAsync(
                        session,
                        checksRepository,
                        row.CheckRun,
                        headSha,
                        actionsJobId,
                        cancellation.Token)
                    .ConfigureAwait(true);
                if (!IsChecksActionCurrent(
                        detailRow,
                        accountChoice,
                        generation,
                        cancellation) ||
                    !ReferenceEquals(modernCheckList.SelectedItem, row))
                {
                    return;
                }

                if (!result.IsSupported)
                {
                    rerunStatusText.Text =
                        "This check provider does not expose a rerun action.";
                    return;
                }

                if (result.Succeeded)
                {
                    rerunStatusText.Text = result.Target ==
                        GitHubPullRequestChecksRerunTargetKind.ActionsJob
                        ? "Rerun request sent for the selected Actions job."
                        : "Rerun request sent for the selected check suite.";
                    checksStatusText.Text =
                        "Rerun requested. Reload checks to see the new result.";
                }
                else if (result.ErrorKind is
                    GitHubPullRequestChecksErrorKind.Network or
                    GitHubPullRequestChecksErrorKind.Timeout)
                {
                    rerunStatusText.Text =
                        "Rerun request outcome is unknown; reload checks before retrying.";
                    rerunCheckButton.IsEnabled = false;
                }
                else
                {
                    rerunStatusText.Text =
                        $"Rerun request failed. {FormatPullRequestChecksError(result.ErrorKind)}";
                    rerunCheckButton.IsEnabled = true;
                }
            }
            catch (GitHubPullRequestChecksCancelledException)
            {
                if (IsChecksActionCurrent(
                        detailRow,
                        accountChoice,
                        generation,
                        cancellation))
                {
                    rerunStatusText.Text =
                        "Rerun request outcome is unknown; reload checks before retrying.";
                    rerunCheckButton.IsEnabled = false;
                }
            }
            catch (GitHubPullRequestChecksException exception)
            {
                if (IsChecksActionCurrent(
                        detailRow,
                        accountChoice,
                        generation,
                        cancellation))
                {
                    rerunStatusText.Text =
                        $"Rerun request failed. {FormatPullRequestChecksError(exception.Kind)}";
                    rerunCheckButton.IsEnabled = true;
                }
            }
            catch (OperationCanceledException)
            {
                if (IsChecksActionCurrent(
                        detailRow,
                        accountChoice,
                        generation,
                        cancellation))
                {
                    rerunStatusText.Text =
                        "Rerun request outcome is unknown; reload checks before retrying.";
                    rerunCheckButton.IsEnabled = false;
                }
            }
            catch (Exception)
            {
                if (IsChecksActionCurrent(
                        detailRow,
                        accountChoice,
                        generation,
                        cancellation))
                {
                    rerunStatusText.Text =
                        "Rerun request outcome is unknown; reload checks before retrying.";
                    rerunCheckButton.IsEnabled = false;
                }
            }
        }

        async void OpenCheckLinkButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            var link = selectedCheckLink;
            if (link is null || !IsSafeExternalBrowserUri(link))
            {
                checksStatusText.Text =
                    "The selected check link did not pass the HTTPS safety check.";
                return;
            }

            try
            {
                var launched = await Launcher.LaunchUriAsync(link);
                checksStatusText.Text = launched
                    ? "The selected check link opened in your browser."
                    : "Windows could not open the selected check link.";
            }
            catch (Exception)
            {
                checksStatusText.Text =
                    "Windows could not open the selected check link.";
            }
        }

        async Task LoadPullRequestsCoreAsync(
            NativeGitHubAccountChoice choice,
            long generation,
            CancellationTokenSource cancellation)
        {
            try
            {
                var account = await LoadGitHubCloneAccountAsync(
                        choice,
                        cancellation.Token)
                    .ConfigureAwait(true);
                if (account is null ||
                    !AreSameGitHubOrigin(
                        account.Summary.ApiOrigin,
                        repository.ApiOrigin))
                {
                    throw new InvalidOperationException(
                        "The selected GitHub account is unavailable for this remote host.");
                }

                var options = CreateGitHubPullRequestOptions(
                    account.Summary.ApiOrigin);
                using var client = new GitHubPullRequestClient(options);
                var result = await client.ListAsync(
                        account.Session,
                        repository,
                        cancellationToken: cancellation.Token)
                    .ConfigureAwait(true);
                if (!IsAccountLoadCurrent(choice, generation, cancellation))
                {
                    return;
                }

                foreach (var pullRequest in result.PullRequests)
                {
                    pullRequestRows.Add(
                        new NativeGitHubPullRequestRow(pullRequest));
                }

                pullRequestList.IsEnabled = pullRequestRows.Count > 0;
                pullRequestEmptyText.Visibility = result.IsComplete &&
                    pullRequestRows.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                statusText.Text = FormatPullRequestListStatus(result);
            }
            catch (GitHubPullRequestCancelledException)
            {
                if (IsAccountLoadCurrent(choice, generation, cancellation))
                {
                    statusText.Text = "Pull request loading was cancelled.";
                }
            }
            catch (GitHubPullRequestException exception)
            {
                if (IsAccountLoadCurrent(choice, generation, cancellation))
                {
                    statusText.Text = FormatPullRequestError(exception.Kind);
                }
            }
            catch (OperationCanceledException)
            {
                if (IsAccountLoadCurrent(choice, generation, cancellation))
                {
                    statusText.Text = "Pull request loading was cancelled.";
                }
            }
            catch (Exception)
            {
                if (IsAccountLoadCurrent(choice, generation, cancellation))
                {
                    statusText.Text = "GitHub pull requests could not be loaded.";
                }
            }
        }

        async Task LoadPullRequestsAsync()
        {
            if (accountBox.SelectedItem is not NativeGitHubAccountChoice choice ||
                listLoading)
            {
                statusText.Text = choices.Length == 0
                    ? "No matching stored GitHub account is available."
                    : "Choose the stored GitHub account for this remote.";
                return;
            }

            listCancellation?.Cancel();
            detailCancellation?.Cancel();
            ClearPullRequestPresentation();
            accountGeneration++;
            detailGeneration++;
            var generation = accountGeneration;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                dialogCancellation.Token);
            listCancellation = cancellation;
            listLoading = true;
            accountBox.IsEnabled = false;
            loadButton.IsEnabled = false;
            statusText.Text = $"Loading pull requests for {choice.DisplayLabel}…";
            var task = LoadPullRequestsCoreAsync(
                choice,
                generation,
                cancellation);
            TrackLoad(task);
            try
            {
                await task;
            }
            finally
            {
                listLoading = false;
                if (ReferenceEquals(listCancellation, cancellation))
                {
                    listCancellation = null;
                    cancellation.Dispose();
                    accountBox.IsEnabled = choices.Length > 0 &&
                        !dialogCancellation.IsCancellationRequested;
                    loadButton.IsEnabled = accountBox.SelectedItem is not null &&
                        !dialogCancellation.IsCancellationRequested;
                }
                else
                {
                    cancellation.Dispose();
                }
            }
        }

        async Task LoadPullRequestDetailsCoreAsync(
            NativeGitHubAccountChoice choice,
            NativeGitHubPullRequestRow row,
            long generation,
            CancellationTokenSource cancellation)
        {
            try
            {
                var account = await LoadGitHubCloneAccountAsync(
                        choice,
                        cancellation.Token)
                    .ConfigureAwait(true);
                if (account is null ||
                    !AreSameGitHubOrigin(
                        account.Summary.ApiOrigin,
                        repository.ApiOrigin))
                {
                    throw new InvalidOperationException(
                        "The selected GitHub account is unavailable for this remote host.");
                }

                var options = CreateGitHubPullRequestOptions(
                    account.Summary.ApiOrigin);
                using var client = new GitHubPullRequestClient(options);
                var pullRequest = await client.ReadAsync(
                        account.Session,
                        repository,
                        row.PullRequest.Number,
                        cancellation.Token)
                    .ConfigureAwait(true);
                if (!IsDetailLoadCurrent(row, generation, cancellation))
                {
                    return;
                }

                if (pullRequest is null)
                {
                    selectedPullRequest = null;
                    pullRequestDetailText.Text =
                        "This pull request is no longer available.";
                    detailStatusText.Text =
                        "GitHub returned not found for this pull request.";
                    openButton.IsEnabled = false;
                    return;
                }

                selectedPullRequest = row;
                pullRequestDetailText.Text = FormatPullRequestDetail(pullRequest);
                openButton.IsEnabled = true;
                detailStatusText.Text =
                    "Loading changed files and reviews…";

                var files = await client.ListChangedFilesAsync(
                        account.Session,
                        repository,
                        pullRequest.Number,
                        cancellationToken: cancellation.Token)
                    .ConfigureAwait(true);
                if (!IsDetailLoadCurrent(row, generation, cancellation))
                {
                    return;
                }

                foreach (var file in files.Files)
                {
                    changedFileRows.Add(
                        new NativeGitHubPullRequestFileRow(file));
                }

                changedFileList.IsEnabled = changedFileRows.Count > 0;
                var reviews = await client.ListReviewsAsync(
                        account.Session,
                        repository,
                        pullRequest.Number,
                        cancellationToken: cancellation.Token)
                    .ConfigureAwait(true);
                if (!IsDetailLoadCurrent(row, generation, cancellation))
                {
                    return;
                }

                foreach (var review in reviews.Reviews)
                {
                    reviewRows.Add(
                        new NativeGitHubPullRequestReviewRow(review));
                }

                reviewList.IsEnabled = reviewRows.Count > 0;
                detailStatusText.Text = FormatPullRequestDetailStatus(
                    files,
                    reviews);

                var detailHeadSha = pullRequest.Head.Sha;
                selectedChecksRepository = repository;
                selectedChecksSession = account.Session;
                selectedChecksAccount = choice;
                selectedChecksPullRequest = row;
                selectedChecksHeadSha = detailHeadSha;
                selectedChecksGeneration = generation;
                selectedChecksCancellation = cancellation;
                SetChecksLoading(detailHeadSha);
                try
                {
                    using var checksClient = new GitHubPullRequestChecksClient(
                        CreateGitHubPullRequestChecksOptions(
                            account.Summary.ApiOrigin));
                    var checks = await checksClient.LoadAsync(
                            account.Session,
                            repository,
                            detailHeadSha,
                            cancellation.Token)
                        .ConfigureAwait(true);
                    if (!IsDetailLoadCurrent(row, generation, cancellation))
                    {
                        return;
                    }

                    ApplyChecksResult(checks);
                }
                catch (GitHubPullRequestChecksCancelledException)
                {
                    if (IsDetailLoadCurrent(row, generation, cancellation))
                    {
                        checksStatusText.Text =
                            "Pull request checks loading was cancelled.";
                    }
                }
                catch (GitHubPullRequestChecksException exception)
                {
                    if (IsDetailLoadCurrent(row, generation, cancellation))
                    {
                        checksStatusText.Text = FormatPullRequestChecksError(
                            exception.Kind);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (IsDetailLoadCurrent(row, generation, cancellation))
                    {
                        checksStatusText.Text =
                            "Pull request checks loading was cancelled.";
                    }
                }
                catch (Exception)
                {
                    if (IsDetailLoadCurrent(row, generation, cancellation))
                    {
                        checksStatusText.Text =
                            "Pull request checks could not be loaded.";
                    }
                }
            }
            catch (GitHubPullRequestCancelledException)
            {
                if (IsDetailLoadCurrent(row, generation, cancellation))
                {
                    detailStatusText.Text =
                        "Pull request details loading was cancelled.";
                }
            }
            catch (GitHubPullRequestException exception)
            {
                if (IsDetailLoadCurrent(row, generation, cancellation))
                {
                    detailStatusText.Text = FormatPullRequestError(exception.Kind);
                }
            }
            catch (OperationCanceledException)
            {
                if (IsDetailLoadCurrent(row, generation, cancellation))
                {
                    detailStatusText.Text =
                        "Pull request details loading was cancelled.";
                }
            }
            catch (Exception)
            {
                if (IsDetailLoadCurrent(row, generation, cancellation))
                {
                    detailStatusText.Text =
                        "Pull request details could not be loaded.";
                }
            }
        }

        async Task LoadPullRequestDetailsAsync(
            NativeGitHubPullRequestRow row)
        {
            detailCancellation?.Cancel();
            detailGeneration++;
            var generation = detailGeneration;
            ClearPullRequestDetailPresentation(
                $"Loading #{row.PullRequest.Number}…");
            detailStatusText.Text = "Loading pull request details…";
            var choice = accountBox.SelectedItem as NativeGitHubAccountChoice;
            if (choice is null)
            {
                detailStatusText.Text =
                    "Choose a stored GitHub account before loading details.";
                return;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                dialogCancellation.Token);
            detailCancellation = cancellation;
            var task = LoadPullRequestDetailsCoreAsync(
                choice,
                row,
                generation,
                cancellation);
            TrackLoad(task);
            try
            {
                await task;
            }
            finally
            {
                if (ReferenceEquals(detailCancellation, cancellation))
                {
                    detailCancellation = null;
                }

                cancellation.Dispose();
            }
        }

        void AccountBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            listCancellation?.Cancel();
            detailCancellation?.Cancel();
            accountGeneration++;
            detailGeneration++;
            ClearPullRequestPresentation();
            var choice = accountBox.SelectedItem as NativeGitHubAccountChoice;
            loadButton.IsEnabled = choice is not null &&
                !dialogCancellation.IsCancellationRequested;
            statusText.Text = choice is null
                ? choices.Length == 0
                    ? "No matching stored GitHub account is available."
                    : "Choose a stored GitHub account for this remote."
                : $"Ready to load pull requests for {choice.DisplayLabel}.";
        }

        void PullRequestList_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (pullRequestList.SelectedItem is NativeGitHubPullRequestRow row)
            {
                var task = LoadPullRequestDetailsAsync(row);
                TrackLoad(task);
                return;
            }

            detailCancellation?.Cancel();
            detailGeneration++;
            ClearPullRequestDetailPresentation(
                "Select a pull request to read its details.");
        }

        void ChangedFileList_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            var row = changedFileList.SelectedItem as NativeGitHubPullRequestFileRow;
            if (row is null)
            {
                changedFilePatchText.Text = string.Empty;
                return;
            }

            changedFilePatchText.Text = row.File.Patch is not null
                ? $"Patch for {row.File.Filename}:\n{row.File.Patch}"
                : row.File.PatchWasOmittedByLimit
                    ? $"Patch for {row.File.Filename} was omitted because it exceeded the native safety limit."
                    : $"GitHub did not provide a patch for {row.File.Filename}. It may be binary or too large.";
        }

        void ReviewList_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            var row = reviewList.SelectedItem as NativeGitHubPullRequestReviewRow;
            if (row is null)
            {
                reviewDetailText.Text = string.Empty;
                return;
            }

            var body = string.IsNullOrEmpty(row.Review.Body)
                ? "No review body."
                : row.Review.Body;
            reviewDetailText.Text =
                $"{row.DisplayName}\n{row.Details}\n\n{body}";
        }

        async void OpenButton_Click(object sender, RoutedEventArgs args)
        {
            var pullRequest = selectedPullRequest?.PullRequest;
            if (pullRequest is null ||
                !IsSafePullRequestBrowserUri(
                    pullRequest.HtmlUrl,
                    repository))
            {
                detailStatusText.Text =
                    "The pull request link did not pass the GitHub host check.";
                return;
            }

            try
            {
                var launched = await Launcher.LaunchUriAsync(
                    pullRequest.HtmlUrl);
                detailStatusText.Text = launched
                    ? "The pull request opened in your browser."
                    : "Windows could not open the pull request link.";
            }
            catch (Exception)
            {
                detailStatusText.Text =
                    "Windows could not open the pull request link.";
            }
        }

        RoutedEventHandler loadButtonClick = async (_, _) =>
        {
            var task = LoadPullRequestsAsync();
            TrackLoad(task);
            await task;
        };
        SelectionChangedEventHandler accountSelectionChanged =
            (_, args) => AccountBox_SelectionChanged(accountBox, args);
        SelectionChangedEventHandler pullRequestSelectionChanged =
            (_, args) => PullRequestList_SelectionChanged(pullRequestList, args);
        SelectionChangedEventHandler changedFileSelectionChanged =
            (_, args) => ChangedFileList_SelectionChanged(changedFileList, args);
        SelectionChangedEventHandler reviewSelectionChanged =
            (_, args) => ReviewList_SelectionChanged(reviewList, args);
        SelectionChangedEventHandler legacyStatusSelectionChanged =
            (_, args) => LegacyStatusList_SelectionChanged(legacyStatusList, args);
        SelectionChangedEventHandler modernCheckSelectionChanged =
            (_, args) => ModernCheckList_SelectionChanged(modernCheckList, args);
        RoutedEventHandler openButtonClick = OpenButton_Click;
        RoutedEventHandler openCheckLinkButtonClick = OpenCheckLinkButton_Click;
        RoutedEventHandler rerunCheckButtonClick = (_, _) =>
        {
            var task = RerunCheckAsync();
            TrackLoad(task);
        };
        TypedEventHandler<ContentDialog, ContentDialogClosingEventArgs> dialogClosing =
            (_, _) =>
            {
                dialogCancellation.Cancel();
                listCancellation?.Cancel();
                detailCancellation?.Cancel();
            };

        accountBox.SelectionChanged += accountSelectionChanged;
        loadButton.Click += loadButtonClick;
        pullRequestList.SelectionChanged += pullRequestSelectionChanged;
        changedFileList.SelectionChanged += changedFileSelectionChanged;
        reviewList.SelectionChanged += reviewSelectionChanged;
        legacyStatusList.SelectionChanged += legacyStatusSelectionChanged;
        modernCheckList.SelectionChanged += modernCheckSelectionChanged;
        openButton.Click += openButtonClick;
        openCheckLinkButton.Click += openCheckLinkButtonClick;
        rerunCheckButton.Click += rerunCheckButtonClick;
        dialog.Closing += dialogClosing;

        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            dialogCancellation.Cancel();
            listCancellation?.Cancel();
            detailCancellation?.Cancel();
            foreach (var task in ownedTasks.ToArray())
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception)
                {
                }
            }

            dialog.Closing -= dialogClosing;
            rerunCheckButton.Click -= rerunCheckButtonClick;
            openCheckLinkButton.Click -= openCheckLinkButtonClick;
            openButton.Click -= openButtonClick;
            modernCheckList.SelectionChanged -= modernCheckSelectionChanged;
            legacyStatusList.SelectionChanged -= legacyStatusSelectionChanged;
            reviewList.SelectionChanged -= reviewSelectionChanged;
            changedFileList.SelectionChanged -= changedFileSelectionChanged;
            pullRequestList.SelectionChanged -= pullRequestSelectionChanged;
            loadButton.Click -= loadButtonClick;
            accountBox.SelectionChanged -= accountSelectionChanged;
            UnregisterGitHubPullRequestDialog(
                dialogCancellation,
                ownedTasks);
        }
    }

    private static GitHubPullRequestClientOptions CreateGitHubPullRequestOptions(
        Uri apiOrigin)
    {
        ArgumentNullException.ThrowIfNull(apiOrigin);
        return apiOrigin.Host.Equals(
                "api.github.com",
                StringComparison.OrdinalIgnoreCase)
            ? GitHubPullRequestClientOptions.ForGitHubCom()
            : GitHubPullRequestClientOptions.ForEnterprise(apiOrigin);
    }

    private static GitHubPullRequestChecksClientOptions
        CreateGitHubPullRequestChecksOptions(Uri apiOrigin)
    {
        ArgumentNullException.ThrowIfNull(apiOrigin);
        return apiOrigin.Host.Equals(
                "api.github.com",
                StringComparison.OrdinalIgnoreCase)
            ? GitHubPullRequestChecksClientOptions.ForGitHubCom()
            : GitHubPullRequestChecksClientOptions.ForEnterprise(apiOrigin);
    }

    private static string FormatPullRequestListStatus(
        GitHubPullRequestListResult result)
    {
        if (result.IsComplete)
        {
            return result.PullRequests.Count == 0
                ? "No open pull requests."
                : $"Loaded {result.PullRequests.Count} open pull requests.";
        }

        var reason = FormatPullRequestError(result.ErrorKind);
        return result.PullRequests.Count == 0
            ? reason
            : $"Loaded {result.PullRequests.Count} open pull requests. {reason}";
    }

    private static string FormatPullRequestDetail(
        GitHubPullRequest pullRequest)
    {
        var state = pullRequest.Draft
            ? $"{pullRequest.State} · Draft"
            : pullRequest.State.ToString();
        var body = string.IsNullOrEmpty(pullRequest.Body)
            ? "No description."
            : pullRequest.Body;
        return
            $"#{pullRequest.Number}  {pullRequest.Title}\n" +
            $"{state} · @{pullRequest.AuthorLogin}\n" +
            $"Base {pullRequest.Base.Ref} · Head {pullRequest.Head.Ref}\n\n" +
            body;
    }

    private static string FormatPullRequestDetailStatus(
        GitHubPullRequestChangedFilesResult files,
        GitHubPullRequestReviewsResult reviews)
    {
        var fileStatus = files.IsComplete
            ? $"{files.Files.Count} changed files."
            : $"{files.Files.Count} changed files loaded. {FormatPullRequestError(files.ErrorKind)}";
        var reviewStatus = reviews.IsComplete
            ? $"{reviews.Reviews.Count} reviews."
            : $"{reviews.Reviews.Count} reviews loaded. {FormatPullRequestError(reviews.ErrorKind)}";
        return $"{fileStatus} {reviewStatus}";
    }

    private static string FormatPullRequestChecksStatus(
        GitHubPullRequestChecksResult result)
    {
        var legacyState = result.Status.LegacyCombinedState?.ToString() ??
            "unavailable";
        var legacy = result.Status.IsComplete
            ? result.Status.Statuses.Count == 0
                ? "Legacy commit statuses: none."
                : $"Legacy combined state: {legacyState}. " +
                    $"{result.Status.Statuses.Count} latest context(s) loaded."
            : $"Legacy commit statuses: {result.Status.Statuses.Count} loaded; " +
                $"{FormatPullRequestChecksError(result.Status.ErrorKind)}";
        var modern = result.CheckRuns.IsComplete
            ? result.CheckRuns.CheckRuns.Count == 0
                ? "Modern check runs: none."
                : $"Modern check runs: {result.CheckRuns.CheckRuns.Count} loaded."
            : $"Modern check runs: {result.CheckRuns.CheckRuns.Count} loaded; " +
                $"{FormatPullRequestChecksError(result.CheckRuns.ErrorKind)}";
        return $"{legacy} {modern}";
    }

    private static string FormatPullRequestChecksError(
        GitHubPullRequestChecksErrorKind? errorKind) =>
        errorKind switch
        {
            GitHubPullRequestChecksErrorKind.InvalidConfiguration =>
                "Checks are not configured.",
            GitHubPullRequestChecksErrorKind.SessionMismatch =>
                "The account does not match this repository host.",
            GitHubPullRequestChecksErrorKind.Network =>
                "GitHub checks could not be reached.",
            GitHubPullRequestChecksErrorKind.Timeout =>
                "GitHub checks loading timed out.",
            GitHubPullRequestChecksErrorKind.Unauthorized =>
                "GitHub rejected this account.",
            GitHubPullRequestChecksErrorKind.Forbidden =>
                "GitHub refused checks access.",
            GitHubPullRequestChecksErrorKind.RateLimited =>
                "GitHub rate limited checks loading.",
            GitHubPullRequestChecksErrorKind.SamlRequired =>
                "GitHub requires organization sign-in.",
            GitHubPullRequestChecksErrorKind.NotFound =>
                "The repository or pull request head was not found.",
            GitHubPullRequestChecksErrorKind.InvalidResponse =>
                "GitHub returned an invalid checks response.",
            GitHubPullRequestChecksErrorKind.PageLimitReached =>
                "Checks loading reached its page limit.",
            _ => "GitHub checks could not be loaded.",
        };

    private static string FormatPullRequestError(
        GitHubPullRequestErrorKind? errorKind) =>
        errorKind switch
        {
            GitHubPullRequestErrorKind.Unauthorized =>
                "GitHub rejected this account.",
            GitHubPullRequestErrorKind.Forbidden =>
                "GitHub refused pull request access.",
            GitHubPullRequestErrorKind.RateLimited =>
                "GitHub rate limited pull request loading.",
            GitHubPullRequestErrorKind.SamlRequired =>
                "GitHub requires organization sign-in.",
            GitHubPullRequestErrorKind.Timeout =>
                "GitHub pull request loading timed out.",
            GitHubPullRequestErrorKind.NotFound =>
                "The repository or pull request was not found.",
            GitHubPullRequestErrorKind.PageLimitReached =>
                "Pull request loading reached its page limit.",
            GitHubPullRequestErrorKind.ChangedFilesLimitReached =>
                "Changed-file loading reached GitHub's service limit.",
            _ => "GitHub pull requests could not be loaded.",
        };

    private static bool IsSafePullRequestBrowserUri(
        Uri value,
        GitHubRemoteRepositoryIdentity repository) =>
        value.IsAbsoluteUri &&
        string.Equals(
            value.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            value.Host,
            repository.Hostname,
            StringComparison.OrdinalIgnoreCase) &&
        value.Port == repository.ApiOrigin.Port &&
        value.UserInfo.Length == 0 &&
        string.IsNullOrEmpty(value.Fragment);

    private static bool IsSafeExternalBrowserUri(Uri value) =>
        value.IsAbsoluteUri &&
        string.Equals(
            value.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase) &&
        value.UserInfo.Length == 0 &&
        string.IsNullOrEmpty(value.Fragment);

    private sealed class NativeGitHubCommitStatusRow
    {
        public NativeGitHubCommitStatusRow(
            GitHubCommitStatusContext status)
        {
            Status = status ?? throw new ArgumentNullException(nameof(status));
        }

        public GitHubCommitStatusContext Status { get; }

        public string DisplayName =>
            $"{Status.State}  {Status.Context}";

        public string Details
        {
            get
            {
                var description = string.IsNullOrWhiteSpace(Status.Description)
                    ? "No description."
                    : Status.Description;
                var updated = Status.UpdatedAt is { } updatedAt
                    ? $"Updated {updatedAt.ToLocalTime():MMM d, yyyy h:mm tt}."
                    : "Update time unavailable.";
                var link = Status.TargetUrl is null
                    ? "No external build link was provided."
                    : "An external HTTPS build link is available.";
                return
                    $"Legacy context: {Status.Context}\n" +
                    $"State: {Status.State}\n" +
                    $"{description}\n{updated} {link}";
            }
        }

        public override string ToString() => DisplayName;
    }

    private sealed class NativeGitHubCheckRunJobStepRow
    {
        public NativeGitHubCheckRunJobStepRow(GitHubCheckRunJobStep step)
        {
            Step = step ?? throw new ArgumentNullException(nameof(step));
        }

        public GitHubCheckRunJobStep Step { get; }

        public string DisplayName =>
            $"{Step.Number}. {StatusLabel}" +
            (Step.Status == GitHubCheckRunStatusKind.Completed
                ? $" · {ConclusionLabel}"
                : string.Empty) +
            $"  {Step.Name}";

        private string StatusLabel =>
            Step.Status == GitHubCheckRunStatusKind.Unknown &&
            !string.IsNullOrWhiteSpace(Step.UnknownStatus)
                ? $"Unknown ({Step.UnknownStatus})"
                : Step.Status.ToString();

        private string ConclusionLabel =>
            Step.Conclusion == GitHubCheckRunConclusionKind.Unknown &&
            !string.IsNullOrWhiteSpace(Step.UnknownConclusion)
                ? $"Unknown ({Step.UnknownConclusion})"
                : Step.Conclusion?.ToString() ??
                    (Step.Status == GitHubCheckRunStatusKind.Completed
                        ? "No conclusion"
                        : "Pending");

        public override string ToString() => DisplayName;
    }

    private sealed class NativeGitHubCheckRunRow
    {
        public NativeGitHubCheckRunRow(GitHubCheckRun checkRun)
        {
            CheckRun = checkRun ?? throw new ArgumentNullException(nameof(checkRun));
        }

        public GitHubCheckRun CheckRun { get; }

        public string DisplayName =>
            $"{StatusLabel}" +
            (CheckRun.Status == GitHubCheckRunStatusKind.Completed
                ? $" · {ConclusionLabel}"
                : string.Empty) +
            $"  {CheckRun.Name}";

        private string StatusLabel =>
            CheckRun.Status == GitHubCheckRunStatusKind.Unknown &&
            !string.IsNullOrWhiteSpace(CheckRun.UnknownStatus)
                ? $"Unknown ({CheckRun.UnknownStatus})"
                : CheckRun.Status.ToString();

        private string ConclusionLabel =>
            CheckRun.Conclusion == GitHubCheckRunConclusionKind.Unknown &&
            !string.IsNullOrWhiteSpace(CheckRun.UnknownConclusion)
                ? $"Unknown ({CheckRun.UnknownConclusion})"
                : CheckRun.Conclusion?.ToString() ??
                    (CheckRun.Status == GitHubCheckRunStatusKind.Completed
                        ? "No conclusion"
                        : "Pending");

        public string Details
        {
            get
            {
                var app = string.IsNullOrWhiteSpace(CheckRun.AppName)
                    ? "App unavailable"
                    : $"App: {CheckRun.AppName}";
                var description = string.IsNullOrWhiteSpace(CheckRun.Description)
                    ? "No check summary."
                    : CheckRun.Description;
                return
                    $"Modern check run: {CheckRun.Name}\n" +
                    $"Status: {StatusLabel} · Result: {ConclusionLabel}\n" +
                    $"{app}\n{description}\n" +
                    "A GitHub check details link is available.";
            }
        }

        public override string ToString() => DisplayName;
    }
}
