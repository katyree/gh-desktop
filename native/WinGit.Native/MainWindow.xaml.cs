using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using WinGit.Core;
using WinRT.Interop;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace WinGit.Native;

public sealed partial class MainWindow : Window
{
    private readonly GitRepositoryService repositoryService;
    private readonly ObservableCollection<ChangeRow> changeRows = [];
    private readonly ObservableCollection<ChangeRow> stagedChangeRows = [];
    private readonly ObservableCollection<ChangeRow> unstagedChangeRows = [];
    private readonly ObservableCollection<CommitRow> commitRows = [];
    private readonly ObservableCollection<CommitFileRow> commitFileRows = [];
    private readonly DiffRowCollection<DiffRow> diffRows = [];
    private readonly DiffRowCollection<DiffRow> historyDiffRows = [];
    private readonly ObservableCollection<BranchRow> branchRows = [];
    private readonly ObservableCollection<WorktreeRow> worktreeRows = [];
    private readonly ObservableCollection<StashRow> stashRows = [];
    private readonly ObservableCollection<SubmoduleRow> submoduleRows = [];
    private readonly ObservableCollection<RemoteRow> remoteRows = [];
    private readonly ObservableCollection<TagRow> tagRows = [];

    private NativeSettings settings = new();
    private RepositoryStatus? currentStatus;
    private string? repositoryRoot;
    private ChangeRow? selectedChange;
    private string? selectedChangePath;
    private bool selectedChangeIsStaged;
    private bool applyingChangeFilter;
    private bool initialChangePreviewPending;
    private CommitRow? selectedCommit;
    private CommitFileRow? selectedCommitFile;
    private BranchRow? selectedBranch;
    private WorktreeRow? selectedWorktree;
    private StashRow? selectedStash;
    private SubmoduleRow? selectedSubmodule;
    private RemoteRow? selectedRemote;
    private TagRow? selectedTag;
    private string currentWorkspace = "changes";
    private bool branchesLoaded;
    private bool worktreesLoaded;
    private bool stashesLoaded;
    private bool submodulesLoaded;
    private bool remotesLoaded;
    private bool tagsLoaded;
    private CancellationTokenSource? operationCancellation;
    private Task? latestOperationTask;
    private long operationGeneration;
    private bool loadingSettings;
    private bool showingSettings;
    private bool mutationInProgress;
    private Task? repositoryPickerTask;
    private bool diagnosticCaptureMode;
    private AppWindow? appWindow;
    private bool allowAppWindowClose;
    private bool appWindowCloseCleanupStarted;
    private string? historyCacheRoot;
    private string? historyCacheHeadId;

    public MainWindow(string gitExecutablePath)
    {
        repositoryService = new GitRepositoryService(gitExecutablePath);
        InitializeComponent();
        InitializeApplicationCommandAccelerators();
        InitializeThemeSynchronization();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        RootGrid.Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;

        StagedChangesList.ItemsSource = stagedChangeRows;
        UnstagedChangesList.ItemsSource = unstagedChangeRows;
        DiffList.ItemsSource = diffRows;
        PartialDiffList.ItemsSource = partialDiffRows;
        HistoryList.ItemsSource = commitRows;
        HistoryFilesList.ItemsSource = commitFileRows;
        HistoryDiffList.ItemsSource = historyDiffRows;
        InitializeHistoryComparison();
        InitializeImageDiffControls();
        InitializeTextDiffControls();
        InitializeSubmoduleDiffControls();
        BranchesList.ItemsSource = branchRows;
        WorktreesList.ItemsSource = worktreeRows;
        StashesList.ItemsSource = stashRows;
        SubmodulesList.ItemsSource = submoduleRows;
        RemoteList.ItemsSource = remoteRows;
        TagsList.ItemsSource = tagRows;
        GitHubAccountsList.ItemsSource = githubAccountRows;
        CodexModelComboBox.ItemsSource = codexModelRows;
        CodexReasoningComboBox.ItemsSource = codexReasoningRows;
        InitializeRepositoryChooserControls();
        InitializeDiscardControls();
        InitializeSelectedChangesReviewControls();
        MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
        UpdateSubmoduleControls();
        UpdateIntegrationCommandStates();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ConfigureWindow();
        if (!NativeCaptureOptions.TryParse(App.CommandLineArguments, out var captureOptions, out var captureError))
        {
            await FailCaptureAsync(captureError ?? "Invalid capture options.", App.CommandLineArguments);
            return;
        }

        if (captureOptions is { View: "repository-open-check" or "repository-picker-check" or "history-selection-check" }
            && TryGetHandlerCheckSettingsError() is { } settingsError)
        {
            await FailCaptureAsync(settingsError, App.CommandLineArguments);
            return;
        }

        settings = await NativeSettingsStore.LoadAsync();
        NormalizeSettings();
        ApplyImageDiffModeToControls();
        ApplyTextDiffSettingsToControls();
        if (captureOptions?.Theme is string captureTheme)
        {
            settings.Theme = captureTheme is "light" ? "Light" : "Dark";
        }

        ApplyTheme();
        RefreshRecentRepositories();
        InitializeIntegrationControls();
        InitializeGitConfigControls();

        if (captureOptions is not null)
        {
            diagnosticCaptureMode = true;
            await RunCaptureAsync(captureOptions);
            return;
        }

        var arguments = App.CommandLineArguments;
        var commandLineRepository = arguments.Length > 1
            && !arguments[1].StartsWith("--", StringComparison.Ordinal)
            ? arguments[1].Trim('"')
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(commandLineRepository))
        {
            await OpenRepositoryAsync(commandLineRepository);
        }
    }

    private void ConfigureWindow()
    {
        try
        {
            var windowHandle = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Closing += AppWindow_Closing;
            appWindow.Resize(new SizeInt32(1300, 850));
            appWindow.Title = "WinGit";
            SystemBackdrop = new MicaBackdrop();
        }
        catch (Exception)
        {
            // Window chrome is a visual enhancement. Keep the repository view
            // usable when a platform build does not expose AppWindow metadata.
        }
    }

    private async void AppWindow_Closing(
        AppWindow sender,
        AppWindowClosingEventArgs args)
    {
        if (allowAppWindowClose)
        {
            return;
        }

        args.Cancel = true;
        if (appWindowCloseCleanupStarted)
        {
            return;
        }

        appWindowCloseCleanupStarted = true;
        var reviewCleanup = CancelSelectedChangesReviewAndWaitAsync();
        CancelCommitMessageGeneration();
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = null;
        var codexCleanup = DisposeCodexClientAsync();
        var githubCleanup = DisposeGitHubAccountsAsync();
        try
        {
            await Task.WhenAll(reviewCleanup, codexCleanup, githubCleanup);
        }
        catch (Exception)
        {
            // Closing must not leave the window open if a child cleanup
            // failure occurs. The client owns its bounded shutdown timeout.
        }
        finally
        {
            allowAppWindowClose = true;
            Close();
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (appWindow is { } window)
        {
            window.Closing -= AppWindow_Closing;
        }

        DisposeThemeSynchronization();

        CancelCommitMessageGeneration();
        var reviewCleanup = CancelSelectedChangesReviewAndWaitAsync();
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = null;
        var codexCleanup = DisposeCodexClientAsync();
        var githubCleanup = DisposeGitHubAccountsAsync();
        try
        {
            await Task.WhenAll(reviewCleanup, codexCleanup, githubCleanup);
        }
        catch (Exception)
        {
            // Both cleanup tasks have been awaited; shutdown is already in
            // progress and must not surface a late child-process exception.
        }
    }

    private async void OpenRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenRepositoryPickerAsync();
    }

    private async Task OpenRepositoryPickerAsync()
    {
        if (!OpenRepositoryButton.IsEnabled || repositoryPickerTask is not null)
        {
            return;
        }

        var pickerTask = OpenRepositoryPickerCoreAsync();
        repositoryPickerTask = pickerTask;
        try
        {
            await pickerTask;
        }
        finally
        {
            if (ReferenceEquals(repositoryPickerTask, pickerTask))
            {
                repositoryPickerTask = null;
            }
        }
    }

    private async Task OpenRepositoryPickerCoreAsync()
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
                await OpenRepositoryAsync(folder.Path);
            }
        }
        catch (Exception exception)
        {
            ShowError("Unable to choose a repository", exception);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        ClearStaleConflictDraft();
        await RefreshRepositoryAsync();
    }

    private async void MainNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        showingSettings = args.IsSettingsSelected;
        if (showingSettings)
        {
            ShowWorkspace("settings");
            latestOperationTask = Task.WhenAll(
                EnsureCodexSettingsLoadedAsync(),
                EnsureGitHubAccountsLoadedAsync(),
                EnsureGitConfigLoadedAsync());
            await latestOperationTask;
            return;
        }

        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        ShowWorkspace(tag ?? "changes");

        if (tag == "history" && repositoryRoot is not null)
        {
            if (!historyComparisonBranchesLoaded)
            {
                latestOperationTask = LoadHistoryComparisonBranchesAsync();
                await latestOperationTask;
            }

            if (!historyComparisonActive && (commitRows.Count == 0 || !IsHistoryCacheCurrent()))
            {
                latestOperationTask = LoadHistoryAsync();
                await latestOperationTask;
            }
        }
        else if (tag == "branches" && repositoryRoot is not null && !branchesLoaded)
        {
            latestOperationTask = LoadBranchesAsync();
            await latestOperationTask;
        }
        else if (tag == "worktrees" && repositoryRoot is not null
            && (!worktreesLoaded || !stashesLoaded))
        {
            latestOperationTask = LoadWorktreesAndStashesAsync();
            await latestOperationTask;
        }
        else if (tag == "submodules" && repositoryRoot is not null && !submodulesLoaded)
        {
            latestOperationTask = LoadSubmodulesAsync();
            await latestOperationTask;
        }
        else if (tag == "remotes" && repositoryRoot is not null && !remotesLoaded)
        {
            latestOperationTask = LoadRemotesAsync();
            await latestOperationTask;
        }
        else if (tag == "tags" && repositoryRoot is not null && !tagsLoaded)
        {
            latestOperationTask = LoadTagsAsync();
            await latestOperationTask;
        }

        if (tag == "changes"
            && repositoryRoot is not null
            && selectedChange is not null
            && currentChangesDiff is null)
        {
            latestOperationTask = LoadWorkingDiffAsync(selectedChange);
            await latestOperationTask;
        }
        else if (tag == "history"
            && repositoryRoot is not null
            && selectedCommitFile is not null
            && currentHistoryDiff is null)
        {
            Task? reloadTask = null;
            if (historyCommitSelectionSnapshot is { } snapshot)
            {
                latestOperationTask = LoadCommitSelectionDiffAsync(snapshot, selectedCommitFile);
                reloadTask = latestOperationTask;
            }
            else if (selectedCommit is { } commit)
            {
                latestOperationTask = LoadCommitDiffAsync(commit, selectedCommitFile);
                reloadTask = latestOperationTask;
            }

            if (reloadTask is not null)
            {
                await reloadTask;
            }
        }
    }

    private void ShowWorkspace(string workspace)
    {
        if (currentWorkspace == "settings" && workspace != "settings")
        {
            CaptureCurrentGitConfigDraft();
        }

        if (workspace != "changes")
        {
            ClearConflictEditorState(discardPendingDraft: true);
        }

        if (workspace != "history" && currentWorkspace == "history")
        {
            ResetHistoryComparisonState();
        }

        var hasRepository = !string.IsNullOrWhiteSpace(repositoryRoot);
        var showingChanges = hasRepository && workspace == "changes";
        RepositoryHeader.Visibility = hasRepository ? Visibility.Visible : Visibility.Collapsed;
        WelcomePanel.Visibility = !hasRepository && workspace != "settings"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ChangesWorkspace.Visibility = showingChanges
            ? Visibility.Visible
            : Visibility.Collapsed;
        StageButton.Visibility = showingChanges ? Visibility.Visible : Visibility.Collapsed;
        UnstageButton.Visibility = showingChanges ? Visibility.Visible : Visibility.Collapsed;
        HistoryWorkspace.Visibility = hasRepository && workspace == "history"
            ? Visibility.Visible
            : Visibility.Collapsed;
        BranchesWorkspace.Visibility = hasRepository && workspace == "branches"
            ? Visibility.Visible
            : Visibility.Collapsed;
        WorktreesWorkspace.Visibility = hasRepository && workspace == "worktrees"
            ? Visibility.Visible
            : Visibility.Collapsed;
        SubmodulesWorkspace.Visibility = hasRepository && workspace == "submodules"
            ? Visibility.Visible
            : Visibility.Collapsed;
        RemotesWorkspace.Visibility = hasRepository && workspace == "remotes"
            ? Visibility.Visible
            : Visibility.Collapsed;
        TagsWorkspace.Visibility = hasRepository && workspace == "tags"
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsWorkspace.Visibility = workspace == "settings"
            ? Visibility.Visible
            : Visibility.Collapsed;

        currentWorkspace = workspace;

        if (showingChanges && selectedChange is null && changeRows.Count > 0)
        {
            var selectInitialPreview = initialChangePreviewPending;
            initialChangePreviewPending = false;
            ApplyChangeFilter(selectInitialPreview);
        }

        if (!hasRepository && workspace != "settings")
        {
            DiffMessageTitle.Text = "Open a repository to begin";
            DiffMessageText.Text = "Choose a local folder with Git history to inspect its changes and commits.";
        }

        UpdateMutationButtons();
        UpdateRepositoryCommandStates();
        UpdateSubmoduleDiffInteraction();
        UpdateSubmoduleControls();
        UpdateIntegrationCommandStates();
        UpdateTextDiffControls();
    }

    private async Task OpenRepositoryAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowError("Repository path is empty", new ArgumentException("Choose a local repository folder."));
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception)
        {
            ShowError("Repository path is invalid", exception);
            return;
        }

        if (!Directory.Exists(fullPath))
        {
            ShowError("Repository folder was not found", new DirectoryNotFoundException(fullPath));
            return;
        }

        CaptureCurrentGitConfigDraft();
        InvalidateSelectedChangesReviewState();
        CancelCommitMessageGeneration();
        var operation = BeginOperation("Opening repository…");
        try
        {
            var status = await repositoryService.OpenAsync(fullPath, operation.Token);
            var operationState = await ReadGitOperationStateAsync(status.RootPath, operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token))
            {
                return;
            }

            repositoryRoot = status.RootPath;
            currentStatus = status;
            // ApplyStatus selects the first changed file when the changes
            // workspace is active. Hold that selection until the operation
            // marker has been read so its diff request cannot cancel this
            // repository-open operation.
            currentWorkspace = "repository-loading";
            ClearRepositoryScopedState();
            if (!diagnosticCaptureMode)
            {
                NativeSettingsStore.AddRecentRepository(settings, status.RootPath);
                _ = SaveSettingsAsync();
            }
            ApplyStatus(status);
            ApplyGitOperationState(operationState);
            MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
            ShowWorkspace("changes");
            ErrorBar.IsOpen = false;
            StatusText.Text = "Repository loaded";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // A newer repository operation owns the UI now.
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("Unable to open repository", exception);
                // A failed switch must not discard the repository that was
                // already open. The initial empty view still gets reset so
                // its controls reflect the failed open attempt.
                if (repositoryRoot is null)
                {
                    ResetRepositoryView();
                }
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }

    private async Task RefreshRepositoryAsync()
    {
        if (repositoryRoot is null)
        {
            return;
        }

        CaptureCurrentGitConfigDraft();
        InvalidateSelectedChangesReviewState();
        var workspaceBeforeRefresh = currentWorkspace;
        var comparisonSnapshotBeforeRefresh = workspaceBeforeRefresh == "history"
            && historyComparisonActive
            ? historyComparisonSnapshot
            : null;
        var previousRoot = repositoryRoot;
        var previousHeadId = currentStatus?.HeadId;
        var shouldReloadVisibleHistory = false;
        var shouldReloadSubmodules = workspaceBeforeRefresh == "submodules";
        BranchComparisonSnapshotStaleException? comparisonStaleException = null;
        currentWorkspace = "repository-refreshing";
        var operation = BeginOperation("Refreshing repository…");
        try
        {
            var status = await repositoryService.GetStatusAsync(repositoryRoot, operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token))
            {
                return;
            }

            if (HasRepositoryHistoryChanged(
                    previousRoot,
                    previousHeadId,
                    status.RootPath,
                    status.HeadId))
            {
                InvalidateHistoryCache();
                shouldReloadVisibleHistory = workspaceBeforeRefresh == "history";
            }
            else if (comparisonSnapshotBeforeRefresh is not null
                && string.Equals(repositoryRoot, previousRoot, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await repositoryService.RevalidateBranchComparisonSnapshotAsync(
                        status.RootPath,
                        comparisonSnapshotBeforeRefresh,
                        operation.Token);
                }
                catch (BranchComparisonSnapshotStaleException exception)
                {
                    comparisonStaleException = exception;
                }
            }

            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, previousRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.Equals(previousRoot, status.RootPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previousHeadId, status.HeadId, StringComparison.OrdinalIgnoreCase))
            {
                ResetAmendMessageState(restoreOriginalDraft: true);
                AmendCheckBox.IsChecked = false;
                CommitIdentityText.Text = string.Empty;
            }

            currentStatus = status;
            repositoryRoot = status.RootPath;
            ApplyStatus(status);
            await LoadGitOperationStateAsync(status.RootPath, operation.Generation, operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, status.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ShowWorkspace(workspaceBeforeRefresh);
            if (comparisonStaleException is not null
                && historyComparisonActive
                && ReferenceEquals(historyComparisonSnapshot, comparisonSnapshotBeforeRefresh))
            {
                ShowError("Comparison changed", comparisonStaleException);
                ClearStaleHistoryComparison(comparisonStaleException.Message);
            }
            else
            {
                StatusText.Text = "Repository refreshed";
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // A newer request superseded this refresh.
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("Unable to refresh repository", exception);
            }
        }
        finally
        {
            if (currentWorkspace == "repository-refreshing")
            {
                currentWorkspace = workspaceBeforeRefresh;
            }

            EndOperation(operation.Generation);
        }

        if (shouldReloadVisibleHistory
            && IsCurrent(operation.Generation, operation.Token)
            && currentWorkspace == "history"
            && repositoryRoot is not null
            && !historyComparisonActive)
        {
            await LoadHistoryAsync();
        }

        if (shouldReloadSubmodules
            && IsCurrent(operation.Generation, operation.Token)
            && currentWorkspace == "submodules"
            && repositoryRoot is not null)
        {
            submodulesLoaded = false;
            await LoadSubmodulesAsync();
        }
    }

    private bool IsHistoryCacheCurrent()
    {
        return repositoryRoot is not null
            && currentStatus is not null
            && string.Equals(historyCacheRoot, repositoryRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(historyCacheHeadId, currentStatus.HeadId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRepositoryHistoryChanged(
        string? previousRoot,
        string? previousHeadId,
        string newRoot,
        string newHeadId)
    {
        return !string.Equals(previousRoot, newRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previousHeadId, newHeadId, StringComparison.OrdinalIgnoreCase);
    }

    private void InvalidateHistoryCache()
    {
        historyCacheRoot = null;
        historyCacheHeadId = null;
        if (historyComparisonActive)
        {
            ClearHistoryComparisonState(clearHistoryRows: true, clearBranchSelection: true);
        }
        else
        {
            suppressHistorySelection = true;
            try
            {
                HistoryList.SelectedItems.Clear();
                HistoryList.SelectedIndex = -1;
                HistoryFilesList.SelectedIndex = -1;
            }
            finally
            {
                suppressHistorySelection = false;
            }

            commitRows.Clear();
            commitFileRows.Clear();
            historyDiffRows.Clear();
            HistoryList.ItemsSource = commitRows;
            HistoryFilesList.ItemsSource = commitFileRows;
            HistoryDiffList.ItemsSource = historyDiffRows;
            HistoryCommitText.Text = "Select a commit";
            HistoryCommitSummaryText.Text = "Choose a commit to inspect its files.";
            HistorySelectionInfoText.Text = string.Empty;
            ShowHistoryOmittedCommitsButton.Visibility = Visibility.Collapsed;
            ShowHistoryDiffMessage("Select a file", "Choose a commit and file to inspect its diff.");
            UpdateHistoryDetailVisibility();
        }

        selectedCommit = null;
        selectedCommitFile = null;
        historyCommitSelectionSnapshot = null;
        UpdateRepositoryCommandStates();
    }

    private void ApplyStatus(RepositoryStatus status)
    {
        ClearConflictEditorState();
        ClearPartialDiffState();
        DiffSubmoduleView.Clear();
        DiffSubmoduleView.Visibility = Visibility.Collapsed;
        HistorySubmoduleView.Clear();
        HistorySubmoduleView.Visibility = Visibility.Collapsed;
        selectedChange = null;
        selectedChangePath = null;
        selectedChangeIsStaged = false;
        initialChangePreviewPending = true;
        StagedChangesList.SelectedItems.Clear();
        UnstagedChangesList.SelectedItems.Clear();
        StagedChangesList.SelectedIndex = -1;
        UnstagedChangesList.SelectedIndex = -1;
        RepositoryPathText.Text = status.RootPath;
        var branch = status.IsUnborn
            ? "No commits yet"
            : status.IsDetached
                ? "Detached HEAD"
                : string.IsNullOrWhiteSpace(status.Branch) ? "Repository" : status.Branch;
        if (!string.IsNullOrWhiteSpace(status.Upstream))
        {
            branch += $"  ·  {status.Upstream}";
        }

        BranchText.Text = branch;
        ChangeCountText.Text = status.Changes.Count.ToString();
        SyncText.Text = FormatSync(status);
        UpdateUndoHeadPresentation();

        changeRows.Clear();
        foreach (var change in status.Changes)
        {
            changeRows.Add(new ChangeRow(change));
        }

        ApplyChangeFilter();
        RefreshRepositoryChooserRows();
        if (changeRows.Count == 0)
        {
            selectedChange = null;
            DiffList.ItemsSource = Array.Empty<DiffRow>();
            ShowDiffMessage("Working tree is clean", "There are no local changes to display.");
        }
    }

    private static string FormatSync(RepositoryStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.Upstream))
        {
            return "Local";
        }

        if (status.Ahead == 0 && status.Behind == 0)
        {
            return "In sync";
        }

        var parts = new List<string>(2);
        if (status.Ahead > 0)
        {
            parts.Add($"↑{status.Ahead}");
        }

        if (status.Behind > 0)
        {
            parts.Add($"↓{status.Behind}");
        }

        return string.Join("  ", parts);
    }

    private void ChangesFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyChangeFilter();
    }

    private void ApplyChangeFilter(bool selectInitialPreview = false)
    {
        ClearPartialDiffState();
        var selectedStagedPaths = StagedChangesList.SelectedItems
            .OfType<ChangeRow>()
            .Select(row => row.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedUnstagedPaths = UnstagedChangesList.SelectedItems
            .OfType<ChangeRow>()
            .Select(row => row.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preferStagedPreview = selectedChangeIsStaged;
        var query = ChangesFilterBox?.Text?.Trim() ?? string.Empty;
        var visibleRows = string.IsNullOrWhiteSpace(query)
            ? changeRows.ToArray()
            : changeRows.Where(row => row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

        applyingChangeFilter = true;
        try
        {
            stagedChangeRows.Clear();
            unstagedChangeRows.Clear();
            foreach (var row in visibleRows)
            {
                var isConflict = IsConflictChange(row.Change);
                if (!isConflict && row.HasStagedChanges)
                {
                    stagedChangeRows.Add(new ChangeRow(row.Change, isStaged: true));
                }

                if (isConflict || row.HasUnstagedChanges)
                {
                    unstagedChangeRows.Add(new ChangeRow(row.Change));
                }
            }

            StagedChangesList.ItemsSource = stagedChangeRows;
            UnstagedChangesList.ItemsSource = unstagedChangeRows;
            StagedChangesList.SelectedItems.Clear();
            UnstagedChangesList.SelectedItems.Clear();
            foreach (var row in stagedChangeRows)
            {
                if (selectedStagedPaths.Contains(row.Path))
                {
                    StagedChangesList.SelectedItems.Add(row);
                }
            }

            foreach (var row in unstagedChangeRows)
            {
                if (selectedUnstagedPaths.Contains(row.Path))
                {
                    UnstagedChangesList.SelectedItems.Add(row);
                }
            }
        }
        finally
        {
            applyingChangeFilter = false;
        }

        StagedCountText.Text = stagedChangeRows.Count.ToString();
        UnstagedCountText.Text = unstagedChangeRows.Count.ToString();
        FilteredChangeCountText.Text = string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : $"{visibleRows.Length} of {changeRows.Count}";

        var previewWasHidden = false;
        if (visibleRows.Length == 0)
        {
            ClearConflictEditorState(discardPendingDraft: true);
            selectedChange = null;
            selectedChangePath = null;
            selectedChangeIsStaged = false;
            DiffFileText.Text = "Select a changed file";
            DiffSummaryText.Text = string.Empty;
            diffRows.Clear();
            DiffList.ItemsSource = diffRows;
            ShowDiffMessage("No matching files", "Try a different path or clear the filter.");
        }
        else if (selectedChange is null
            || selectedChangePath is null
            || !visibleRows.Any(row =>
                string.Equals(row.Path, selectedChangePath, StringComparison.OrdinalIgnoreCase)
                && (selectedChangeIsStaged
                    ? !IsConflictChange(row.Change) && row.HasStagedChanges
                    : IsConflictChange(row.Change) || row.HasUnstagedChanges)))
        {
            ClearConflictEditorState(discardPendingDraft: true);
            selectedChange = null;
            selectedChangePath = null;
            selectedChangeIsStaged = false;
            DiffFileText.Text = "Select a changed file";
            DiffSummaryText.Text = string.Empty;
            diffRows.Clear();
            DiffList.ItemsSource = diffRows;
            ShowDiffMessage("Select a changed file", "Choose a path from the working changes list.");
            previewWasHidden = true;
        }

        if (previewWasHidden && (selectedStagedPaths.Count > 0 || selectedUnstagedPaths.Count > 0))
        {
            var selectedStagedRow = StagedChangesList.SelectedItems.OfType<ChangeRow>().FirstOrDefault();
            var selectedUnstagedRow = UnstagedChangesList.SelectedItems.OfType<ChangeRow>().FirstOrDefault();
            var previewRow = preferStagedPreview
                ? selectedStagedRow ?? selectedUnstagedRow
                : selectedUnstagedRow ?? selectedStagedRow;
            if (previewRow is not null)
            {
                latestOperationTask = LoadSelectedChangeAsync(previewRow);
            }
        }

        if (selectInitialPreview
            && selectedChange is null
            && selectedStagedPaths.Count == 0
            && selectedUnstagedPaths.Count == 0
            && currentWorkspace == "changes")
        {
            if (unstagedChangeRows.Count > 0)
            {
                UnstagedChangesList.SelectedIndex = 0;
            }
            else if (stagedChangeRows.Count > 0)
            {
                StagedChangesList.SelectedIndex = 0;
            }
        }

        UpdateMutationButtons();
        UpdateRepositoryCommandStates();
        UpdateIntegrationCommandStates();
    }

    private async void StagedChangesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (applyingChangeFilter)
        {
            return;
        }

        if (StagedChangesList.SelectedItem is not ChangeRow row)
        {
            UpdateMutationButtons();
            UpdateIntegrationCommandStates();
            return;
        }

        selectedChange = row;
        selectedChangePath = row.Path;
        selectedChangeIsStaged = true;
        DiscardConflictDraftIfPathChanged(row.Path);
        ClearConflictEditorState();
        ClearPartialDiffState();
        if (UnstagedChangesList.SelectedItems.Count > 0)
        {
            UnstagedChangesList.SelectedItems.Clear();
        }
        UpdateMutationButtons();
        UpdateIntegrationCommandStates();
        latestOperationTask = LoadWorkingDiffAsync(row);
        await latestOperationTask;
    }

    private async void UnstagedChangesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (applyingChangeFilter)
        {
            return;
        }

        if (UnstagedChangesList.SelectedItem is not ChangeRow row)
        {
            UpdateMutationButtons();
            UpdateIntegrationCommandStates();
            return;
        }

        selectedChange = row;
        selectedChangePath = row.Path;
        selectedChangeIsStaged = false;
        DiscardConflictDraftIfPathChanged(row.Path);
        ClearConflictEditorState();
        ClearPartialDiffState();
        if (StagedChangesList.SelectedItems.Count > 0)
        {
            StagedChangesList.SelectedItems.Clear();
        }
        UpdateMutationButtons();
        UpdateIntegrationCommandStates();
        latestOperationTask = LoadWorkingDiffAsync(row);
        await latestOperationTask;
    }

    private async Task LoadSelectedChangeAsync(ChangeRow row)
    {
        selectedChange = row;
        selectedChangePath = row.Path;
        selectedChangeIsStaged = row.IsStaged;
        DiscardConflictDraftIfPathChanged(row.Path);
        ClearConflictEditorState();
        if (selectedChange is not null)
        {
            latestOperationTask = LoadWorkingDiffAsync(selectedChange);
            await latestOperationTask;
        }
    }

    private async Task LoadWorkingDiffAsync(ChangeRow row)
    {
        if (repositoryRoot is null)
        {
            return;
        }

        DiffFileText.Text = $"{(row.IsStaged ? "Staged" : "Unstaged")} · {row.Path}";
        DiffSummaryText.Text = "Loading diff…";
        InvalidateTextDiffCache(history: false);
        ClearPartialDiffState();
        diffRows.Clear();
        DiffList.ItemsSource = diffRows;
        ShowDiffMessage("Loading diff…", "Reading the selected path from Git.");
        var operation = BeginOperation($"Loading {(row.IsStaged ? "staged" : "unstaged")} {row.Path}…");
        try
        {
            var diff = row.IsStaged
                ? await repositoryService.GetIndexDiffAsync(
                    repositoryRoot,
                    row.Change,
                    operation.Token,
                    hideWhitespaceChanges)
                : await repositoryService.GetUnstagedDiffAsync(
                    repositoryRoot,
                    row.Change,
                    operation.Token,
                    hideWhitespaceChanges);
            if (!IsCurrent(operation.Generation, operation.Token) || !ReferenceEquals(selectedChange, row))
            {
                return;
            }

            await ApplyDiffAsync(
                diffRows,
                diff,
                DiffList,
                DiffSideBySideList,
                DiffImageView,
                DiffMessagePanel,
                DiffMessageTitle,
                DiffMessageText,
                operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token) || !ReferenceEquals(selectedChange, row))
            {
                return;
            }

            DiffSummaryText.Text = FormatDiffSummary(diff);
            DiffFileText.Text = $"{(row.IsStaged ? "Staged" : "Unstaged")} · {row.Path}";
            StatusText.Text = $"Showing {(row.IsStaged ? "staged" : "unstaged")} {row.Path}";
            if (diff.SubmoduleComparison is null && !diff.IsTruncated)
            {
                await LoadPartialDiffAsync(row, operation.Generation, operation.Token);
            }
            else
            {
                ClearPartialDiffState();
            }
            if (IsConflictChange(row.Change))
            {
                await LoadConflictEditorAsync(row, operation.Generation, operation.Token);
            }
            else
            {
                ClearConflictEditorState(discardPendingDraft: true);
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // Selecting another file cancels the previous diff.
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("Unable to load file diff", exception);
                ShowDiffMessage("Diff unavailable", exception.Message);
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }

    private async Task ApplyDiffAsync(
        ObservableCollection<DiffRow> target,
        FileDiff diff,
        ListView list,
        ListView splitList,
        NativeImageDiffView imageView,
        FrameworkElement messagePanel,
        TextBlock messageTitle,
        TextBlock messageText,
        CancellationToken cancellationToken)
    {
        BeginTextDiffRender(list, diff);
        target.Clear();
        splitList.ItemsSource = ReferenceEquals(list, DiffList)
            ? diffSideBySideRows
            : historyDiffSideBySideRows;
        splitList.Visibility = Visibility.Collapsed;
        imageView.Clear();
        imageView.Visibility = Visibility.Collapsed;
        var submoduleView = ReferenceEquals(list, DiffList)
            ? DiffSubmoduleView
            : ReferenceEquals(list, HistoryDiffList)
                ? HistorySubmoduleView
                : null;
        submoduleView?.Clear();
        if (submoduleView is not null)
        {
            submoduleView.Visibility = Visibility.Collapsed;
        }

        if (diff.SubmoduleComparison is { } submoduleComparison)
        {
            var submodulePath = ReferenceEquals(list, DiffList)
                ? selectedChangePath
                : selectedCommitFile?.Path;
            if (string.IsNullOrWhiteSpace(submodulePath))
            {
                list.ItemsSource = target;
                splitList.Visibility = Visibility.Collapsed;
                messageTitle.Text = "Submodule path unavailable";
                messageText.Text = "Git returned a submodule diff without a repository-relative path.";
                messagePanel.Visibility = Visibility.Visible;
                return;
            }

            if (submoduleView is null)
            {
                list.ItemsSource = target;
                splitList.Visibility = Visibility.Collapsed;
                messageTitle.Text = "Submodule diff unavailable";
                messageText.Text = "This view has no submodule renderer.";
                messagePanel.Visibility = Visibility.Visible;
                return;
            }

            var expectedRoot = repositoryRoot;
            var expectedChange = selectedChange;
            var expectedCommitFile = selectedCommitFile;
            bool IsCurrentSubmoduleDiff() =>
                IsSubmoduleDiffSelectionCurrent(
                    list,
                    submodulePath,
                    expectedRoot,
                    expectedChange,
                    expectedCommitFile,
                    cancellationToken);

            if (!IsCurrentSubmoduleDiff())
            {
                return;
            }

            await ConfigureSubmoduleDiffAsync(
                submoduleView,
                submoduleComparison,
                submodulePath,
                readOnly: ReferenceEquals(list, HistoryDiffList),
                cancellationToken: cancellationToken,
                isCurrent: IsCurrentSubmoduleDiff);
            if (!IsCurrentSubmoduleDiff())
            {
                return;
            }

            list.ItemsSource = target;
            splitList.Visibility = Visibility.Collapsed;
            list.Visibility = Visibility.Collapsed;
            messagePanel.Visibility = Visibility.Collapsed;
            submoduleView.Visibility = Visibility.Visible;
            return;
        }

        list.Visibility = Visibility.Visible;

        if (diff.ImageComparison is { } imageComparison)
        {
            list.ItemsSource = target;
            list.Visibility = Visibility.Collapsed;
            splitList.Visibility = Visibility.Collapsed;
            messagePanel.Visibility = Visibility.Collapsed;
            imageView.Visibility = Visibility.Visible;
            await imageView.SetComparisonAsync(imageComparison, cancellationToken);
            return;
        }

        if (diff.IsBinary)
        {
            list.ItemsSource = target;
            splitList.Visibility = Visibility.Collapsed;
            messageTitle.Text = "Binary file";
            messageText.Text = diff.Message ?? "Git reported binary content for this path.";
            messagePanel.Visibility = Visibility.Visible;
            return;
        }

        list.ItemsSource = target;
        splitList.ItemsSource = ReferenceEquals(list, DiffList)
            ? diffSideBySideRows
            : historyDiffSideBySideRows;
        MarkTextDiffReady(list);
        list.ItemsSource = target;
        if (target.Count == 0)
        {
            messageTitle.Text = "No textual changes";
            messageText.Text = diff.Message ?? "Git returned an empty text diff for this path.";
            messagePanel.Visibility = Visibility.Visible;
        }
        else
        {
            messagePanel.Visibility = Visibility.Collapsed;
        }

        if (diff.IsTruncated)
        {
            messageTitle.Text = "Diff truncated";
            var recoveryAction = ReferenceEquals(list, DiffList)
                ? "Use File > Open selected file in editor to inspect the full file. Configure an editor in Settings first if needed."
                : "Use File > Open repository shell to inspect the full diff with Git.";
            messageText.Text = $"{diff.Message ?? "This diff is larger than the native viewer limit."} {recoveryAction}";
            messagePanel.Visibility = Visibility.Visible;
        }
    }

    private static string FormatDiffSummary(FileDiff diff)
    {
        if (diff.ImageComparison is not null)
        {
            return "Image comparison";
        }

        if (diff.SubmoduleComparison is not null)
        {
            return "Submodule change";
        }

        if (diff.IsBinary)
        {
            return "Binary content";
        }

        var added = diff.Lines.Count(line => line.Kind == DiffLineKind.Added);
        var removed = diff.Lines.Count(line => line.Kind == DiffLineKind.Removed);
        var suffix = diff.IsTruncated ? "  ·  truncated" : string.Empty;
        return $"{added} additions  ·  {removed} deletions{suffix}";
    }

    private void ShowDiffMessage(string title, string message)
    {
        InvalidateTextDiffCache(history: false);
        DiffImageView.Clear();
        DiffImageView.Visibility = Visibility.Collapsed;
        DiffSubmoduleView.Clear();
        DiffSubmoduleView.Visibility = Visibility.Collapsed;
        DiffMessageTitle.Text = title;
        DiffMessageText.Text = message;
        DiffMessagePanel.Visibility = Visibility.Visible;
    }

    private void HideDiffMessage()
    {
        DiffMessagePanel.Visibility = Visibility.Collapsed;
    }

    private void ShowHistoryDiffMessage(string title, string message)
    {
        InvalidateTextDiffCache(history: true);
        HistoryDiffImageView.Clear();
        HistoryDiffImageView.Visibility = Visibility.Collapsed;
        HistorySubmoduleView.Clear();
        HistorySubmoduleView.Visibility = Visibility.Collapsed;
        HistoryDiffMessageTitle.Text = title;
        HistoryDiffMessageText.Text = message;
        HistoryDiffMessagePanel.Visibility = Visibility.Visible;
    }

    private void ResetRepositoryView()
    {
        CaptureCurrentGitConfigDraft();
        repositoryRoot = null;
        currentStatus = null;
        ClearRepositoryScopedState();
        ApplyChangeFilter();
        RefreshRepositoryChooserRows();
        ShowWorkspace(showingSettings ? "settings" : "changes");
    }

    private void ClearRepositoryScopedState()
    {
        ClearConflictEditorState(discardPendingDraft: true);
        ResetAmendMessageState();
        selectedChange = null;
        selectedChangePath = null;
        selectedChangeIsStaged = false;
        initialChangePreviewPending = false;
        selectedCommit = null;
        selectedCommitFile = null;
        selectedBranch = null;
        selectedWorktree = null;
        selectedStash = null;
        selectedSubmodule = null;
        selectedTag = null;
        ClearGitOperationState();
        changeRows.Clear();
        stagedChangeRows.Clear();
        unstagedChangeRows.Clear();
        commitRows.Clear();
        commitFileRows.Clear();
        diffRows.Clear();
        historyDiffRows.Clear();
        ClearTextDiffState();
        DiffImageView.Clear();
        DiffImageView.Visibility = Visibility.Collapsed;
        DiffSubmoduleView.Clear();
        DiffSubmoduleView.Visibility = Visibility.Collapsed;
        HistoryDiffImageView.Clear();
        HistoryDiffImageView.Visibility = Visibility.Collapsed;
        HistorySubmoduleView.Clear();
        HistorySubmoduleView.Visibility = Visibility.Collapsed;
        ResetHistoryComparisonState();
        branchRows.Clear();
        worktreeRows.Clear();
        stashRows.Clear();
        submoduleRows.Clear();
        remoteRows.Clear();
        tagRows.Clear();
        historyCacheRoot = null;
        historyCacheHeadId = null;
        ClearPartialDiffState();
        StashesEmptyText.Visibility = Visibility.Collapsed;
        TagsEmptyText.Visibility = Visibility.Collapsed;
        branchesLoaded = false;
        worktreesLoaded = false;
        stashesLoaded = false;
        submodulesLoaded = false;
        remotesLoaded = false;
        tagsLoaded = false;
        StagedChangesList.SelectedIndex = -1;
        UnstagedChangesList.SelectedIndex = -1;
        HistoryList.SelectedIndex = -1;
        HistoryFilesList.SelectedIndex = -1;
        BranchesList.SelectedIndex = -1;
        WorktreesList.SelectedIndex = -1;
        StashesList.SelectedIndex = -1;
        SubmodulesList.SelectedIndex = -1;
        RemoteList.SelectedIndex = -1;
        TagsList.SelectedIndex = -1;
        RemoteEmptyText.Visibility = Visibility.Collapsed;
        SubmodulesEmptyText.Visibility = Visibility.Collapsed;
        SubmodulesStatusText.Text = string.Empty;
        UpdateSubmoduleDetails();
        UpdateTagDetails();
        UpdateUndoHeadPresentation();
        StagedCountText.Text = "0";
        UnstagedCountText.Text = "0";
        CommitSummaryBox.Text = string.Empty;
        CommitDescriptionBox.Text = string.Empty;
        AmendCheckBox.IsChecked = false;
        CommitIdentityText.Text = string.Empty;
        CommitMessageGenerationStatusText.Text = string.Empty;
        DiffFileText.Text = "Select a changed file";
        DiffSummaryText.Text = string.Empty;
        DiffList.ItemsSource = diffRows;
        PartialDiffList.ItemsSource = partialDiffRows;
        HistoryDiffList.ItemsSource = historyDiffRows;
        ShowDiffMessage("Select a changed file", "Choose a path from the working changes list.");
        ShowHistoryDiffMessage("Select a file", "Choose a commit and file to inspect its diff.");
        UpdateMutationButtons();
        UpdateRepositoryCommandStates();
        UpdateSubmoduleControls();
        UpdateIntegrationCommandStates();
    }

    private void ErrorBar_Closing(InfoBar sender, InfoBarClosingEventArgs args)
    {
        ErrorBar.Message = string.Empty;
    }

    private void ShowError(string title, Exception exception)
    {
        var detail = exception is GitCommandException gitException
            ? gitException.Message
            : exception.Message;
        ErrorBar.Title = title;
        ErrorBar.Message = string.IsNullOrWhiteSpace(detail) ? "The operation failed." : detail;
        ErrorBar.IsOpen = true;
        StatusText.Text = title;
    }

    private void NormalizeSettings()
    {
        settings.Theme = settings.Theme is "System" or "Light" or "Dark" ? settings.Theme : "System";
        settings.ImageDiffMode = NativeSettingsStore.NormalizeImageDiffMode(settings.ImageDiffMode);
        settings.TextDiffMode = NativeSettingsStore.NormalizeTextDiffMode(settings.TextDiffMode);
        settings.RecentRepositories ??= [];
        settings.RecentRepositories = settings.RecentRepositories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try
                {
                    return NativeSettingsStore.NormalizeRepositoryPath(path);
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        loadingSettings = true;
        ThemeComboBox.SelectedIndex = settings.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
        loadingSettings = false;
        ApplyTitleBarTheme();
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingSettings || ThemeComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string theme)
        {
            return;
        }

        settings.Theme = theme;
        ApplyTheme();
        _ = SaveSettingsAsync();
    }

    private void RefreshRecentRepositories()
    {
        RecentRepositoriesList.ItemsSource = settings.RecentRepositories
            .Select(path => new RepositoryChooserRow(
                path,
                repositoryRoot,
                NativeSettingsStore.GetRepositoryAlias(settings, path)))
            .ToArray();
        RefreshRepositoryChooserRows();
    }

    private async void RecentRepositoriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentRepositoriesList.SelectedItem is RepositoryChooserRow row)
        {
            await OpenRepositoryAsync(row.Path);
            RecentRepositoriesList.SelectedItem = null;
        }
    }

    private async Task SaveSettingsAsync()
    {
        if (diagnosticCaptureMode)
        {
            return;
        }

        try
        {
            await NativeSettingsStore.SaveAsync(settings);
            RefreshRecentRepositories();
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowError("Unable to save native settings", exception);
        }
        catch (IOException exception)
        {
            ShowError("Unable to save native settings", exception);
        }
    }

    private (long Generation, CancellationToken Token) BeginOperation(string status, bool allowHistorySelection = false)
    {
        // Mutation entry points mark the shared state before asking for an
        // operation token. Keep review capture eligible: it starts while
        // mutationInProgress is false and must not cancel itself here.
        if (mutationInProgress)
        {
            InvalidateSelectedChangesReviewState();
        }

        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        operationGeneration++;
        SetBusy(true, status, allowHistorySelection: allowHistorySelection);
        return (operationGeneration, operationCancellation.Token);
    }

    private bool IsCurrent(long generation, CancellationToken token)
    {
        return generation == operationGeneration && !token.IsCancellationRequested;
    }

    private void EndOperation(long generation)
    {
        if (generation == operationGeneration)
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private void SetBusy(bool busy, string status, bool allowHistorySelection = false)
    {
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
        OpenRepositoryButton.IsEnabled = !busy && !mutationInProgress;
        RefreshButton.IsEnabled = !busy && !mutationInProgress && repositoryRoot is not null;
        ChangesFilterBox.IsEnabled = !busy && !mutationInProgress;
        StagedChangesList.IsEnabled = !busy && !mutationInProgress;
        UnstagedChangesList.IsEnabled = !busy && !mutationInProgress;
        PartialDiffList.IsEnabled = !busy && !mutationInProgress;
        HistoryBranchFilterBox.IsEnabled = !busy && !mutationInProgress;
        HistoryComparisonBranchList.IsEnabled = !busy && !mutationInProgress;
        HistoryList.IsEnabled = !mutationInProgress && (!busy || allowHistorySelection);
        HistoryFilesList.IsEnabled = !busy && !mutationInProgress;
        BranchesList.IsEnabled = !busy && !mutationInProgress;
        WorktreesList.IsEnabled = !busy && !mutationInProgress;
        StashesList.IsEnabled = !busy && !mutationInProgress;
        SubmodulesList.IsEnabled = !busy && !mutationInProgress;
        RemoteList.IsEnabled = !busy && !mutationInProgress;
        TagsList.IsEnabled = !busy && !mutationInProgress;
        CommitSummaryBox.IsEnabled = !busy && !mutationInProgress;
        CommitDescriptionBox.IsEnabled = !busy && !mutationInProgress;
        AmendCheckBox.IsEnabled = !busy && !mutationInProgress;
        CreateRepositoryButton.IsEnabled = !busy && !mutationInProgress;
        CloneRepositoryButton.IsEnabled = !busy && !mutationInProgress;
        CancelOperationButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.IsEnabled = busy;
        MainNavigation.IsEnabled = !mutationInProgress;
        UpdateMutationButtons();
        UpdateRepositoryCommandStates();
        UpdateSubmoduleDiffInteraction();
        UpdateSubmoduleControls();
        UpdateCodexControls();
        UpdateGitHubAccountControls();
        UpdateIntegrationCommandStates();
        UpdateGitConfigControls();
        UpdateTextDiffControls();
    }
}
