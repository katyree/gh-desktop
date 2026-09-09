using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;
using WinGit.Core.GitHub;
using WinRT.Interop;
using Windows.Storage.Pickers;
using Windows.Foundation;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private async Task LoadRemotesAsync()
    {
        if (repositoryRoot is null)
        {
            return;
        }

        var root = repositoryRoot;
        var operation = BeginOperation("Loading remotes…");
        try
        {
            var remotes = await repositoryService.GetRemotesAsync(root, operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            remoteRows.Clear();
            foreach (var remote in remotes)
            {
                remoteRows.Add(new RemoteRow(remote));
            }

            remotesLoaded = true;
            selectedRemote = null;
            RemoteList.SelectedIndex = -1;
            RemoteEmptyText.Visibility = remoteRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (remoteRows.Count > 0)
            {
                RemoteList.SelectedIndex = 0;
            }
            else
            {
                UpdateRemoteDetails();
            }

            StatusText.Text = remoteRows.Count == 0
                ? "No remotes configured"
                : $"Loaded {remoteRows.Count} remotes";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // A newer repository operation owns the remote panel.
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                RemoteEmptyText.Visibility = Visibility.Collapsed;
                ShowError("Unable to load remotes", exception);
                selectedRemote = null;
                UpdateRemoteDetails();
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }

    private void RemoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedRemote = RemoteList.SelectedItem as RemoteRow;
        UpdateRemoteDetails();
        UpdateRepositoryCommandStates();
    }

    private void UpdateRemoteDetails()
    {
        if (selectedRemote is null)
        {
            SelectedRemoteNameText.Text = "Select a remote";
            SelectedRemoteUrlText.Text = remoteRows.Count == 0
                ? "Add a remote to fetch, pull, or push this repository."
                : "Choose a remote to view its URL and run a transport action.";
            RemoteTransportBranchText.Text = FormatTransportBranchHint();
            return;
        }

        SelectedRemoteNameText.Text = selectedRemote.Name;
        SelectedRemoteUrlText.Text = $"URL: {selectedRemote.Url}";
        RemoteTransportBranchText.Text = FormatTransportBranchHint();
    }

    private string FormatTransportBranchHint()
    {
        if (currentStatus is null)
        {
            return "Open a repository to use transport operations.";
        }

        if (currentStatus.IsUnborn)
        {
            return "This repository has no commits yet. Fetch is available; pull and push need a checked-out branch with a commit.";
        }

        if (currentStatus.IsDetached || string.IsNullOrWhiteSpace(currentStatus.Branch))
        {
            return "Detached HEAD: fetch is available. Pull and push need a checked-out local branch.";
        }

        return $"Current local branch: {currentStatus.Branch}. Pull and push need a remote branch.";
    }

    private string GetDefaultRemoteBranch(string remoteName)
    {
        var upstream = currentStatus?.Upstream ?? string.Empty;
        var prefix = remoteName + "/";
        return upstream.StartsWith(prefix, StringComparison.Ordinal)
            ? upstream[prefix.Length..]
            : currentStatus?.Branch ?? string.Empty;
    }

    private bool CanStartRepositorySetup() => !mutationInProgress && !BusyRing.IsActive;

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e)
    {
        operationCancellation?.Cancel();
    }

    private async Task<RepositoryStatus> RunRepositorySetupAsync(
        string progress,
        Func<CancellationToken, Task<RepositoryStatus>> setup)
    {
        if (!CanStartRepositorySetup())
        {
            throw new InvalidOperationException("Another repository operation is already running.");
        }

        mutationInProgress = true;
        var operation = BeginOperation(progress);
        try
        {
            var status = await setup(operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token))
            {
                throw new OperationCanceledException(operation.Token);
            }

            return status;
        }
        finally
        {
            mutationInProgress = false;
            if (operation.Generation == operationGeneration)
            {
                SetBusy(false, StatusText.Text);
            }
            else
            {
                UpdateMutationButtons();
                UpdateRepositoryCommandStates();
            }
        }
    }

    private async Task PickFolderIntoAsync(TextBox target)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                target.Text = folder.Path;
            }
        }
        catch (Exception exception)
        {
            ShowError("Unable to choose a folder", exception);
        }
    }

    private static Grid CreatePathPickerRow(TextBox pathBox, Button chooseButton)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(pathBox);
        Grid.SetColumn(pathBox, 0);
        row.Children.Add(chooseButton);
        Grid.SetColumn(chooseButton, 1);
        return row;
    }

    private static InfoBar CreateDialogErrorBar()
    {
        return new InfoBar
        {
            IsOpen = false,
            IsClosable = false,
            Severity = InfoBarSeverity.Error,
        };
    }

    private void SetDialogError(InfoBar errorBar, string message)
    {
        errorBar.Message = string.IsNullOrWhiteSpace(message) ? "The operation failed." : message;
        errorBar.IsOpen = true;
        ErrorBar.IsOpen = false;
    }

    private void MoveErrorToDialog(InfoBar errorBar, string fallback)
    {
        var message = string.IsNullOrWhiteSpace(ErrorBar.Message) ? fallback : ErrorBar.Message;
        SetDialogError(errorBar, message);
    }

    private static string ExceptionDetail(Exception exception)
    {
        if (exception is GitCommandException gitException
            && !string.IsNullOrWhiteSpace(gitException.StandardError))
        {
            return gitException.StandardError;
        }

        return exception.Message;
    }

    private static string NormalizeSetupPath(string value, string label)
    {
        var trimmed = value.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"Enter a {label}.");
        }

        return Path.GetFullPath(trimmed);
    }

    private static ComboBox CreateTemplateComboBox(
        string header,
        IReadOnlyList<NativeTemplateChoice> choices,
        string automationName)
    {
        var comboBox = new ComboBox
        {
            Header = header,
            MinWidth = 220,
        };
        AutomationProperties.SetName(comboBox, automationName);
        foreach (var choice in choices)
        {
            comboBox.Items.Add(new ComboBoxItem
            {
                Content = choice.DisplayName,
                Tag = choice.Key,
            });
        }

        comboBox.SelectedIndex = choices.Count == 0 ? -1 : 0;
        return comboBox;
    }

    private static string SelectedTemplateKey(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "None";

    private async void CreateRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowCreateRepositoryDialogAsync();
    }

    private async Task ShowCreateRepositoryDialogAsync()
    {
        if (!CanStartRepositorySetup())
        {
            return;
        }

        var configuredDefaultBranch = await GetDefaultBranchForCreationAsync();
        if (!CanStartRepositorySetup())
        {
            return;
        }

        var destinationBox = new TextBox
        {
            Header = "Repository folder",
            PlaceholderText = "C:\\work\\my-repository",
        };
        AutomationProperties.SetName(destinationBox, "New repository folder");
        var chooseButton = new Button { Content = "Choose…" };
        AutomationProperties.SetName(chooseButton, "Choose new repository folder");
        chooseButton.Click += async (_, _) => await PickFolderIntoAsync(destinationBox);

        var branchBox = new TextBox
        {
            Header = "Initial branch",
            Text = configuredDefaultBranch,
            PlaceholderText = "main",
        };
        AutomationProperties.SetName(branchBox, "Initial branch name");
        var descriptionBox = new TextBox
        {
            Header = "Description (used in README)",
            PlaceholderText = "Optional project description",
        };
        AutomationProperties.SetName(descriptionBox, "Repository description");
        var readmeCheckBox = new CheckBox
        {
            Content = "Initialize with a README",
            IsChecked = false,
        };
        AutomationProperties.SetName(readmeCheckBox, "Initialize repository with a README");
        var gitIgnoreBox = CreateTemplateComboBox(
            "Git ignore",
            NativeRepositoryTemplates.GetGitIgnoreChoices(),
            "Git ignore template");
        var licenseBox = CreateTemplateComboBox(
            "License",
            NativeRepositoryTemplates.GetLicenseChoices(),
            "License template");
        var holderBox = new TextBox
        {
            Header = "Copyright holder (optional)",
            PlaceholderText = "Name to place in the license",
        };
        AutomationProperties.SetName(holderBox, "License copyright holder");
        var emailBox = new TextBox
        {
            Header = "Copyright email (optional)",
            PlaceholderText = "Email to place in the license",
        };
        AutomationProperties.SetName(emailBox, "License copyright email");
        var errorBar = CreateDialogErrorBar();
        var content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                errorBar,
                new TextBlock
                {
                    Text = "The destination may be new or empty. Existing Git repositories are rejected, and selected template files are never overwritten.",
                    TextWrapping = TextWrapping.Wrap,
                },
                CreatePathPickerRow(destinationBox, chooseButton),
                branchBox,
                descriptionBox,
                readmeCheckBox,
                gitIgnoreBox,
                licenseBox,
                holderBox,
                emailBox,
            },
        };
        var dialog = CreateDialog("Create repository", "Create", content);

        while (true)
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                var destination = NormalizeSetupPath(destinationBox.Text, "repository folder");
                var initialBranch = branchBox.Text.Trim();
                if (initialBranch.Length == 0)
                {
                    throw new ArgumentException("Enter an initial branch name.");
                }

                var repositoryName = new DirectoryInfo(destination).Name;
                var description = descriptionBox.Text.Trim();
                var readmeContents = readmeCheckBox.IsChecked == true
                    ? NativeRepositoryTemplates.BuildReadme(destination, description)
                    : null;
                var gitIgnoreKey = SelectedTemplateKey(gitIgnoreBox);
                var gitIgnoreContents = gitIgnoreKey == "None"
                    ? null
                    : await NativeRepositoryTemplates.ReadGitIgnoreAsync(gitIgnoreKey, CancellationToken.None);
                var licenseKey = SelectedTemplateKey(licenseBox);
                var licenseContents = licenseKey == "None"
                    ? null
                    : await NativeRepositoryTemplates.ReadLicenseAsync(
                        licenseKey,
                        repositoryName,
                        holderBox.Text.Trim(),
                        emailBox.Text.Trim(),
                        description,
                        CancellationToken.None);

                var status = await RunRepositorySetupAsync(
                    "Creating repository…",
                    token => repositoryService.InitializeAsync(
                        destination,
                        initialBranch,
                        readmeContents,
                        gitIgnoreContents,
                        licenseContents,
                        token));
                await OpenRepositoryAsync(status.RootPath);
                return;
            }
            catch (OperationCanceledException)
            {
                SetDialogError(errorBar, "Repository creation was cancelled. Your entered details are still here; choose Create to retry.");
            }
            catch (Exception exception)
            {
                SetDialogError(errorBar, ExceptionDetail(exception));
            }
        }
    }

    private async void CloneRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowCloneRepositoryDialogAsync();
    }

    private async Task ShowCloneRepositoryDialogAsync()
    {
        if (!CanStartRepositorySetup())
        {
            return;
        }

        try
        {
            await EnsureGitHubAccountsLoadedAsync();
        }
        catch (Exception)
        {
            // Direct URL cloning remains available when the optional account
            // catalog cannot be loaded.
        }

        var urlBox = new TextBox
        {
            Header = "Repository URL",
            PlaceholderText = "https://github.com/owner/repository.git",
        };
        AutomationProperties.SetName(urlBox, "Clone repository URL");
        var destinationBox = new TextBox
        {
            Header = "Destination folder",
            PlaceholderText = "C:\\work\\repository",
        };
        AutomationProperties.SetName(destinationBox, "Clone destination folder");
        var chooseButton = new Button { Content = "Choose…" };
        AutomationProperties.SetName(chooseButton, "Choose clone destination folder");
        chooseButton.Click += async (_, _) => await PickFolderIntoAsync(destinationBox);
        var branchBox = new TextBox
        {
            Header = "Branch (optional)",
            PlaceholderText = "Clone the remote default branch",
        };
        AutomationProperties.SetName(branchBox, "Clone branch");

        var githubPicker = new NativeGitHubRepositoryPicker();
        var githubChoices = GetGitHubCloneAccountChoices();
        var preferredGitHubChoice = GetPreferredGitHubCloneAccount(githubChoices);
        githubPicker.SetAccounts(githubChoices, preferredGitHubChoice);
        if (githubChoices.Length == 0)
        {
            githubPicker.SetStatus(
                string.IsNullOrWhiteSpace(githubConfigurationMessage)
                    ? "No connected GitHub account. Paste a URL to clone directly."
                    : githubConfigurationMessage!);
        }

        var errorBar = CreateDialogErrorBar();
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                errorBar,
                new TextBlock
                {
                    Text = "The destination must be new or empty. HTTPS and SSH use your configured Git authentication.",
                    TextWrapping = TextWrapping.Wrap,
                },
                urlBox,
                githubPicker.View,
                CreatePathPickerRow(destinationBox, chooseButton),
                branchBox,
            },
        };
        var dialog = CreateDialog("Clone repository", "Clone", content);

        using var githubPickerCancellation = new CancellationTokenSource();
        RegisterGitHubCloneDialog(githubPickerCancellation);
        CancellationTokenSource? githubPickerLoadCancellation = null;
        Task? githubPickerLoadTask = null;
        var githubPickerLoadTasks = new List<Task>();
        var githubPickerGeneration = 0L;
        var suppressPickerUrlChange = false;
        string? pickerAutoFilledBranch = null;

        void ApplySelectedGitHubRepository()
        {
            var selectedRepository = githubPicker.SelectedRepository;
            if (selectedRepository is null)
            {
                return;
            }

            suppressPickerUrlChange = true;
            try
            {
                urlBox.Text = selectedRepository.CloneUrl(
                    githubPicker.SelectedProtocol);
                pickerAutoFilledBranch = selectedRepository.Repository.DefaultBranch;
                branchBox.Text = pickerAutoFilledBranch ?? string.Empty;
            }
            finally
            {
                suppressPickerUrlChange = false;
            }
        }

        async Task LoadSelectedGitHubAccountAsync(
            NativeGitHubAccountChoice? choice)
        {
            githubPickerLoadCancellation?.Cancel();
            var generation = ++githubPickerGeneration;
            if (choice is null)
            {
                githubPicker.SetRepositories([]);
                githubPicker.SetLoading(false);
                githubPicker.SetStatus(
                    "No connected GitHub account. Paste a URL to clone directly.");
                return;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                githubPickerCancellation.Token);
            githubPickerLoadCancellation = cancellation;
            githubPicker.SetRepositories([]);
            githubPicker.SetLoading(true, choice.DisplayName);
            var loadTask = LoadSelectedGitHubAccountCoreAsync(
                choice,
                generation,
                cancellation);
            githubPickerLoadTask = loadTask;
            try
            {
                await loadTask;
            }
            catch (OperationCanceledException)
            {
                // A new account choice or dialog close owns the newer state.
            }
            finally
            {
                if (ReferenceEquals(githubPickerLoadCancellation, cancellation))
                {
                    githubPickerLoadCancellation = null;
                    githubPicker.SetLoading(false);
                }

                cancellation.Dispose();
                if (ReferenceEquals(githubPickerLoadTask, loadTask))
                {
                    githubPickerLoadTask = null;
                }
            }
        }

        async Task LoadSelectedGitHubAccountCoreAsync(
            NativeGitHubAccountChoice choice,
            long generation,
            CancellationTokenSource cancellation)
        {
            try
            {
                var result = await LoadGitHubCloneRepositoriesAsync(
                        choice,
                        cancellation.Token)
                    .ConfigureAwait(true);
                if (generation != githubPickerGeneration ||
                    cancellation.IsCancellationRequested ||
                    githubPickerCancellation.IsCancellationRequested)
                {
                    return;
                }

                githubPicker.SetRepositories(result.Repositories);
                githubPicker.SetStatus(GetGitHubRepositoryCatalogStatus(result));
            }
            catch (GitHubRepositoryCatalogCancelledException)
            {
                if (generation == githubPickerGeneration &&
                    !githubPickerCancellation.IsCancellationRequested)
                {
                    githubPicker.SetStatus("GitHub repository loading was cancelled.");
                }
            }
            catch (Exception exception)
            {
                if (generation == githubPickerGeneration &&
                    !githubPickerCancellation.IsCancellationRequested &&
                    !cancellation.IsCancellationRequested)
                {
                    githubPicker.SetRepositories([]);
                    githubPicker.SetStatus(
                        string.IsNullOrWhiteSpace(exception.Message)
                            ? "GitHub repositories could not be loaded."
                            : exception.Message);
                }
            }
        }

        EventHandler accountSelectionChanged = (_, _) =>
        {
            githubPickerLoadTask = TrackGitHubPickerLoad(
                githubPicker.SelectedAccount);
        };
        EventHandler repositorySelectionChanged = (_, _) =>
        {
            ApplySelectedGitHubRepository();
        };
        EventHandler protocolSelectionChanged = (_, _) =>
        {
            ApplySelectedGitHubRepository();
        };
        TypedEventHandler<object, WindowEventArgs> windowClosed = (_, _) =>
        {
            githubPickerCancellation.Cancel();
            githubPickerLoadCancellation?.Cancel();
        };
        TypedEventHandler<ContentDialog, ContentDialogClosingEventArgs> dialogClosing = (_, args) =>
        {
            if (args.Result != ContentDialogResult.Primary)
            {
                githubPickerCancellation.Cancel();
                githubPickerLoadCancellation?.Cancel();
            }
        };
        TextChangedEventHandler urlChanged = (_, _) =>
        {
            if (!suppressPickerUrlChange)
            {
                var selectedRepository = githubPicker.SelectedRepository;
                var expectedSelectedUrl = selectedRepository?.CloneUrl(
                    githubPicker.SelectedProtocol);
                if (expectedSelectedUrl is not null &&
                    string.Equals(
                        urlBox.Text.Trim(),
                        expectedSelectedUrl,
                        StringComparison.Ordinal))
                {
                    // WinUI raises TextChanged asynchronously after a Text
                    // assignment. Keep the picker selection for that
                    // deferred event by comparing the current value with the
                    // selected row's URL rather than relying only on the
                    // synchronous setter guard above.
                    return;
                }

                if (pickerAutoFilledBranch is not null &&
                    string.Equals(
                        branchBox.Text.Trim(),
                        pickerAutoFilledBranch,
                        StringComparison.Ordinal))
                {
                    branchBox.Text = string.Empty;
                }

                pickerAutoFilledBranch = null;
                githubPicker.ClearRepositorySelection();
            }
        };
        TextChangedEventHandler branchChanged = (_, _) =>
        {
            if (!suppressPickerUrlChange)
            {
                if (pickerAutoFilledBranch is not null &&
                    string.Equals(
                        branchBox.Text.Trim(),
                        pickerAutoFilledBranch,
                        StringComparison.Ordinal))
                {
                    // This can be the deferred event for the branch assigned
                    // while applying a repository selection.
                    return;
                }

                pickerAutoFilledBranch = null;
            }
        };

        githubPicker.AccountSelectionChanged += accountSelectionChanged;
        githubPicker.RepositorySelectionChanged += repositorySelectionChanged;
        githubPicker.ProtocolSelectionChanged += protocolSelectionChanged;
        urlBox.TextChanged += urlChanged;
        branchBox.TextChanged += branchChanged;
        Closed += windowClosed;
        dialog.Closing += dialogClosing;
        if (githubPicker.SelectedAccount is not null)
        {
            githubPickerLoadTask = TrackGitHubPickerLoad(
                githubPicker.SelectedAccount);
        }

        Task TrackGitHubPickerLoad(NativeGitHubAccountChoice? choice)
        {
            var task = LoadSelectedGitHubAccountAsync(choice);
            RegisterGitHubCloneLoad(task);
            githubPickerLoadTasks.Add(task);
            return task;
        }

        try
        {
            while (true)
            {
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                try
                {
                    var url = urlBox.Text.Trim();
                    if (url.Length == 0)
                    {
                        throw new ArgumentException("Enter a repository URL.");
                    }

                    if (url.Contains("<redacted>", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("Enter the clean repository URL. A redacted URL cannot be saved or cloned.");
                    }

                    var destination = NormalizeSetupPath(destinationBox.Text, "clone destination folder");
                    var branch = string.IsNullOrWhiteSpace(branchBox.Text) ? null : branchBox.Text.Trim();
                    var status = await RunRepositorySetupAsync(
                        "Cloning repository…",
                        token => repositoryService.CloneAsync(url, destination, branch, token));
                    await OpenRepositoryAsync(status.RootPath);
                    return;
                }
                catch (OperationCanceledException)
                {
                    SetDialogError(errorBar, "Repository clone was cancelled. Your entered details are still here; choose Clone to retry.");
                }
                catch (Exception exception)
                {
                    SetDialogError(errorBar, ExceptionDetail(exception));
                }
            }
        }
        finally
        {
            githubPickerCancellation.Cancel();
            githubPickerLoadCancellation?.Cancel();
            foreach (var task in githubPickerLoadTasks.ToArray())
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
            Closed -= windowClosed;
            urlBox.TextChanged -= urlChanged;
            branchBox.TextChanged -= branchChanged;
            githubPicker.AccountSelectionChanged -= accountSelectionChanged;
            githubPicker.RepositorySelectionChanged -= repositorySelectionChanged;
            githubPicker.ProtocolSelectionChanged -= protocolSelectionChanged;
            UnregisterGitHubCloneDialog(
                githubPickerCancellation,
                githubPickerLoadTasks);
        }
    }

    private async void AddRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartRepositoryWrite())
        {
            return;
        }

        var nameBox = new TextBox
        {
            Header = "Remote name",
            Text = "origin",
            PlaceholderText = "origin",
        };
        AutomationProperties.SetName(nameBox, "New remote name");
        var urlBox = new TextBox
        {
            Header = "Remote URL",
            PlaceholderText = "https://github.com/owner/repository.git",
        };
        AutomationProperties.SetName(urlBox, "New remote URL");
        var errorBar = CreateDialogErrorBar();
        var dialog = CreateDialog(
            "Add remote",
            "Add",
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    errorBar,
                    new TextBlock
                    {
                        Text = "Use Git Credential Manager or an SSH URL for authentication. URLs with embedded credentials are rejected.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    nameBox,
                    urlBox,
                },
            });

        while (true)
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var name = nameBox.Text.Trim();
            var url = urlBox.Text.Trim();
            if (name.Length == 0 || url.Length == 0)
            {
                SetDialogError(errorBar, "Enter both a remote name and URL.");
                continue;
            }

            if (url.Contains("<redacted>", StringComparison.OrdinalIgnoreCase))
            {
                SetDialogError(errorBar, "Enter the clean remote URL. A redacted URL cannot be saved.");
                continue;
            }

            var succeeded = await RunRepositoryWriteAsync(
                "Adding remote…",
                $"Remote {name} added",
                "Remote add cancelled; refreshing repository…",
                "Unable to add remote",
                (root, token) => repositoryService.AddRemoteAsync(root, name, url, token),
                refreshBranches: true,
                refreshRemotes: true);
            if (succeeded)
            {
                return;
            }

            MoveErrorToDialog(errorBar, "Remote add did not complete. Your entered details are still here; choose Add to retry.");
        }
    }

    private async void EditRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var remote = selectedRemote;
        if (!CanStartRepositoryWrite() || remote is null)
        {
            return;
        }

        var urlBox = new TextBox
        {
            Header = "Remote URL",
            Text = remote.Url,
        };
        AutomationProperties.SetName(urlBox, "Edited remote URL");
        var errorBar = CreateDialogErrorBar();
        var dialog = CreateDialog(
            $"Edit {remote.Name}",
            "Save",
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    errorBar,
                    new TextBlock
                    {
                        Text = $"Update the fetch URL for \"{remote.Name}\". Replace any <redacted> placeholder with the clean URL before saving.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    urlBox,
                },
            });

        while (true)
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var url = urlBox.Text.Trim();
            if (url.Length == 0)
            {
                SetDialogError(errorBar, "Enter a remote URL.");
                continue;
            }

            if (url.Contains("<redacted>", StringComparison.OrdinalIgnoreCase))
            {
                SetDialogError(errorBar, "Replace the redacted URL portion with the clean URL before saving.");
                continue;
            }

            var remoteName = remote.Name;
            var succeeded = await RunRepositoryWriteAsync(
                "Saving remote URL…",
                $"Remote {remoteName} updated",
                "Remote URL update cancelled; refreshing repository…",
                "Unable to update remote URL",
                (root, token) => repositoryService.SetRemoteUrlAsync(root, remoteName, url, token),
                refreshBranches: true,
                refreshRemotes: true);
            if (succeeded)
            {
                return;
            }

            MoveErrorToDialog(errorBar, "Remote URL update did not complete. Your entered URL is still here; choose Save to retry.");
        }
    }

    private async void RemoveRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var remote = selectedRemote;
        if (!CanStartRepositoryWrite() || remote is null)
        {
            return;
        }

        var remoteName = remote.Name;
        var dialog = CreateDialog(
            "Remove remote?",
            "Remove",
            new TextBlock
            {
                Text = $"Remove the remote \"{remoteName}\"? Its local configuration is deleted; remote repository data is not changed.",
                TextWrapping = TextWrapping.Wrap,
            });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunRepositoryWriteAsync(
            $"Removing {remoteName}…",
            $"Remote {remoteName} removed",
            "Remote removal cancelled; refreshing repository…",
            "Unable to remove remote",
            (root, token) => repositoryService.RemoveRemoteAsync(root, remoteName, token),
            refreshBranches: true,
            refreshRemotes: true);
    }

    private async void FetchRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var remote = selectedRemote;
        if (!CanStartRepositoryWrite() || remote is null)
        {
            return;
        }

        var remoteName = remote.Name;
        await RunRepositoryWriteAsync(
            $"Fetching {remoteName}…",
            $"Fetched {remoteName}",
            "Fetch cancelled; refreshing repository…",
            "Unable to fetch remote",
            (root, token) => repositoryService.FetchAsync(root, remoteName, token),
            refreshBranches: true,
            refreshRemotes: true);
    }

    private async void PullRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var remote = selectedRemote;
        var localBranch = currentStatus?.Branch;
        if (!CanStartRepositoryWrite()
            || remote is null
            || currentStatus is null
            || currentStatus.IsDetached
            || currentStatus.IsUnborn
            || string.IsNullOrWhiteSpace(localBranch))
        {
            return;
        }

        var remoteName = remote.Name;
        var remoteBranchBox = new TextBox
        {
            Header = "Remote branch",
            Text = GetDefaultRemoteBranch(remoteName),
            PlaceholderText = "main",
        };
        AutomationProperties.SetName(remoteBranchBox, "Remote branch to pull");
        var errorBar = CreateDialogErrorBar();
        var dialog = CreateDialog(
            "Pull remote",
            "Pull",
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    errorBar,
                    new TextBlock
                    {
                        Text = $"Pull {remoteName}/… into the checked-out local branch \"{localBranch}\". Pull only moves forward and refuses divergent history.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    remoteBranchBox,
                },
            });

        while (true)
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var remoteBranch = remoteBranchBox.Text.Trim();
            if (remoteBranch.Length == 0)
            {
                SetDialogError(errorBar, "Enter the remote branch to pull.");
                continue;
            }

            var succeeded = await RunRepositoryWriteAsync(
                $"Pulling {remoteName}/{remoteBranch}…",
                $"Pulled {remoteName}/{remoteBranch}",
                "Fast-forward pull cancelled; refreshing repository…",
                "Fast-forward pull refused",
                (root, token) => repositoryService.PullFastForwardOnlyAsync(root, remoteName, localBranch, remoteBranch, token),
                refreshBranches: true,
                refreshRemotes: true);
            if (succeeded)
            {
                return;
            }

            MoveErrorToDialog(errorBar, "Git refused the fast-forward pull. Resolve the branch divergence or local changes, then retry.");
        }
    }

    private async void PushRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var remote = selectedRemote;
        var localBranch = currentStatus?.Branch;
        if (!CanStartRepositoryWrite()
            || remote is null
            || currentStatus is null
            || currentStatus.IsDetached
            || currentStatus.IsUnborn
            || string.IsNullOrWhiteSpace(localBranch))
        {
            return;
        }

        var remoteName = remote.Name;
        var remoteBranchBox = new TextBox
        {
            Header = "Remote branch",
            Text = GetDefaultRemoteBranch(remoteName),
            PlaceholderText = localBranch,
        };
        AutomationProperties.SetName(remoteBranchBox, "Remote branch to push");
        var errorBar = CreateDialogErrorBar();
        var dialog = CreateDialog(
            "Push remote",
            "Push",
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    errorBar,
                    new TextBlock
                    {
                        Text = $"Push the checked-out local branch \"{localBranch}\" to {remoteName}/…. This operation never forces the remote branch.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    remoteBranchBox,
                },
            });

        while (true)
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var remoteBranch = remoteBranchBox.Text.Trim();
            if (remoteBranch.Length == 0)
            {
                SetDialogError(errorBar, "Enter the remote branch to push.");
                continue;
            }

            var succeeded = await RunRepositoryWriteAsync(
                $"Pushing {localBranch} to {remoteName}/{remoteBranch}…",
                $"Pushed {localBranch} to {remoteName}/{remoteBranch}",
                "Push cancelled; refreshing repository…",
                "Unable to push remote",
                (root, token) => repositoryService.PushAsync(root, remoteName, localBranch, remoteBranch, token),
                refreshBranches: true,
                refreshRemotes: true);
            if (succeeded)
            {
                return;
            }

            MoveErrorToDialog(errorBar, "Push did not complete. Check the remote branch and authentication, then retry without force.");
        }
    }
}
