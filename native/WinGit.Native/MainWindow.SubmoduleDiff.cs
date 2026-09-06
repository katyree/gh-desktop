using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private sealed record SubmoduleOpenTarget(
        string RootPath,
        string RelativePath,
        string FullPath);

    private void InitializeSubmoduleDiffControls()
    {
        DiffSubmoduleView.OpenSubmoduleRequested += DiffSubmoduleView_OpenSubmoduleRequested;
        HistorySubmoduleView.OpenSubmoduleRequested += HistorySubmoduleView_OpenSubmoduleRequested;
        HistoryList.SelectionChanged += HistoryList_SubmoduleDiffReset;
        HistoryFilesList.SelectionChanged += HistoryFilesList_SubmoduleDiffReset;
    }

    private void HistoryList_SubmoduleDiffReset(
        object sender,
        Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        HistorySubmoduleView.Clear();
        HistorySubmoduleView.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private void HistoryFilesList_SubmoduleDiffReset(
        object sender,
        Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        HistorySubmoduleView.Clear();
        HistorySubmoduleView.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private async void DiffSubmoduleView_OpenSubmoduleRequested(
        object? sender,
        EventArgs e)
    {
        if (sender is NativeSubmoduleDiffView view)
        {
            await OpenSubmoduleFromDiffAsync(view);
        }
    }

    private async void HistorySubmoduleView_OpenSubmoduleRequested(
        object? sender,
        EventArgs e)
    {
        if (sender is NativeSubmoduleDiffView view)
        {
            await OpenSubmoduleFromDiffAsync(view);
        }
    }

    private async Task ConfigureSubmoduleDiffAsync(
        NativeSubmoduleDiffView view,
        SubmoduleComparison comparison,
        string path,
        bool readOnly,
        CancellationToken cancellationToken,
        Func<bool> isCurrent)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isCurrent())
        {
            return;
        }

        var unavailableReason = readOnly
            ? "This historical diff can open only the current initialized submodule checkout, not the historical revision."
            : "Opening is unavailable until Git confirms an initialized local submodule target.";
        var canOpen = false;

        if (repositoryRoot is { } root)
        {
            try
            {
                canOpen = await FindInitializedSubmoduleAsync(
                    root,
                    path,
                    cancellationToken) is not null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                unavailableReason = "Opening is unavailable because the current submodule target could not be revalidated. Refresh the repository and try again.";
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!isCurrent())
        {
            return;
        }

        view.SetComparison(
            comparison,
            path,
            readOnly,
            canOpen,
            unavailableReason);
        UpdateSubmoduleDiffInteraction();
    }

    private async Task OpenSubmoduleFromDiffAsync(NativeSubmoduleDiffView view)
    {
        var root = repositoryRoot;
        var relativePath = view.CurrentPath;
        var expectedWorkspace = ReferenceEquals(view, HistorySubmoduleView)
            ? "history"
            : ReferenceEquals(view, DiffSubmoduleView)
                ? "changes"
                : null;
        if (root is null
            || string.IsNullOrWhiteSpace(relativePath)
            || expectedWorkspace is null
            || currentWorkspace != expectedWorkspace)
        {
            view.SetOpenSubmoduleAvailability(
                false,
                "The repository changed before this submodule could be opened.");
            return;
        }

        if (mutationInProgress || BusyRing.IsActive)
        {
            view.SetOpenSubmoduleInteractionEnabled(false);
            return;
        }

        try
        {
            var target = await FindInitializedSubmoduleAsync(
                root,
                relativePath,
                CancellationToken.None);
            if (target is null
                || repositoryRoot is not { } currentRoot
                || !IsSamePath(currentRoot, root)
                || !string.Equals(
                    view.CurrentPath,
                    relativePath,
                    StringComparison.OrdinalIgnoreCase)
                || currentWorkspace != expectedWorkspace
                || view.Visibility != Microsoft.UI.Xaml.Visibility.Visible)
            {
                view.SetOpenSubmoduleAvailability(
                    false,
                    "The submodule target changed or is no longer initialized. Refresh the diff and try again.");
                return;
            }

            if (mutationInProgress || BusyRing.IsActive)
            {
                view.SetOpenSubmoduleInteractionEnabled(false);
                return;
            }

            await OpenRepositoryAsync(target.FullPath);
        }
        catch (OperationCanceledException)
        {
            // Repository switching owns cancellation.
        }
        catch (Exception exception)
        {
            view.SetOpenSubmoduleAvailability(
                false,
                "The submodule could not be opened until its current target is revalidated.");
            ShowError("Unable to open submodule", exception);
        }
    }

    private async Task<SubmoduleOpenTarget?> FindInitializedSubmoduleAsync(
        string root,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var snapshots = await repositoryService.GetSubmodulesAsync(
            root,
            cancellationToken);
        var normalizedPath = NormalizeSubmodulePath(relativePath);
        var snapshot = snapshots.FirstOrDefault(candidate =>
            string.Equals(
                NormalizeSubmodulePath(candidate.Path),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase));
        if (snapshot is null
            || !snapshot.IsInitialized
            || !IsSamePath(snapshot.RootPath, root))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(
            Path.Combine(
                root,
                snapshot.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathWithinRepository(root, fullPath)
            || !Directory.Exists(fullPath))
        {
            return null;
        }

        return new SubmoduleOpenTarget(
            snapshot.RootPath,
            snapshot.Path,
            fullPath);
    }

    private static string NormalizeSubmodulePath(string path) =>
        path.Replace('\\', '/').Trim('/');

    private static bool IsPathWithinRepository(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(fullRoot, fullPath, comparison)
            || fullPath.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                comparison);
    }

    private void UpdateSubmoduleDiffInteraction()
    {
        var enabled = !mutationInProgress && !BusyRing.IsActive;
        DiffSubmoduleView.SetOpenSubmoduleInteractionEnabled(enabled);
        HistorySubmoduleView.SetOpenSubmoduleInteractionEnabled(enabled);
    }

    private bool IsSubmoduleDiffSelectionCurrent(
        Microsoft.UI.Xaml.Controls.ListView list,
        string path,
        string? expectedRoot,
        ChangeRow? expectedChange,
        CommitFileRow? expectedCommitFile,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || expectedRoot is null
            || repositoryRoot is null
            || !IsSamePath(repositoryRoot, expectedRoot))
        {
            return false;
        }

        if (ReferenceEquals(list, DiffList))
        {
            return currentWorkspace == "changes"
                && ReferenceEquals(selectedChange, expectedChange)
                && string.Equals(selectedChangePath, path, StringComparison.OrdinalIgnoreCase);
        }

        if (ReferenceEquals(list, HistoryDiffList))
        {
            return currentWorkspace == "history"
                && ReferenceEquals(selectedCommitFile, expectedCommitFile)
                && string.Equals(selectedCommitFile?.Path, path, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
