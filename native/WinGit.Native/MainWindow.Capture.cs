using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private async Task RunCaptureAsync(NativeCaptureOptions options)
    {
        try
        {
            if (options.View == "history-comparison-check")
            {
                if (string.IsNullOrWhiteSpace(options.RepositoryPath))
                {
                    await FailCaptureAsync(
                        "--view history-comparison-check requires --repository <fixture run root>.",
                        App.CommandLineArguments);
                    return;
                }

                await RunHistoryComparisonCheckAsync(options);
                return;
            }

            if (options.View is "whole-file-staging-check" or "partial-staging-check" or "commit-composer-check")
            {
                if (string.IsNullOrWhiteSpace(options.RepositoryPath))
                {
                    await FailCaptureAsync(
                        $"--view {options.View} requires --repository <fixture parent>.",
                        App.CommandLineArguments);
                    return;
                }

                if (options.View == "whole-file-staging-check")
                {
                    await RunWholeFileStagingCheckAsync(options);
                }
                else if (options.View == "partial-staging-check")
                {
                    await RunPartialStagingCheckAsync(options);
                }
                else
                {
                    await RunCommitComposerCheckAsync(options);
                }

                return;
            }

            if (options.View is "repository-open-check" or "repository-picker-check" or "history-selection-check")
            {
                if (string.IsNullOrWhiteSpace(options.RepositoryPath))
                {
                    await FailCaptureAsync(
                        $"--view {options.View} requires --repository <fixture parent>.",
                        App.CommandLineArguments);
                    return;
                }

                if (options.View == "repository-open-check")
                {
                    await RunRepositoryOpenCheckAsync(options);
                }
                else if (options.View == "repository-picker-check")
                {
                    await RunRepositoryPickerCheckAsync(options);
                }
                else
                {
                    await RunHistorySelectionCheckAsync(options);
                }

                return;
            }

            if (options.View is "changes" or "history" or "branches" or "worktrees" or "stashes" or "remotes" or "tags")
            {
                if (string.IsNullOrWhiteSpace(options.RepositoryPath))
                {
                    await FailCaptureAsync(
                        $"--view {options.View} requires --repository <path> or a positional repository path.",
                        App.CommandLineArguments);
                    return;
                }

                await OpenRepositoryAsync(options.RepositoryPath);
                if (repositoryRoot is null || ErrorBar.IsOpen)
                {
                    var openFailure = ErrorBar.IsOpen
                        ? $"{ErrorBar.Title}: {ErrorBar.Message}"
                        : "The repository could not be opened for capture.";
                    await FailCaptureAsync(openFailure, App.CommandLineArguments);
                    return;
                }

                if (options.View == "history")
                {
                    ShowWorkspace("history");
                    // Load the branch picker before History selects its first
                    // commit. Selecting that commit starts a nested file/diff
                    // read through the shared operation slot; loading branches
                    // afterwards would cancel that read and leave capture on
                    // "Loading commit files…".
                    if (!historyComparisonBranchesLoaded)
                    {
                        latestOperationTask = LoadHistoryComparisonBranchesAsync();
                        await latestOperationTask;
                    }

                    if (!string.IsNullOrWhiteSpace(options.ComparisonReference))
                    {
                        latestOperationTask = LoadHistoryBranchComparisonAsync(
                            options.ComparisonReference,
                            ComparisonMode.Behind);
                        await latestOperationTask;
                    }
                    else
                    {
                        latestOperationTask = LoadHistoryAsync();
                        await latestOperationTask;
                    }

                    MainNavigation.SelectedItem = MainNavigation.MenuItems[1];
                }
                else if (options.View == "branches")
                {
                    MainNavigation.SelectedItem = MainNavigation.MenuItems[2];
                    ShowWorkspace("branches");
                }
                else if (options.View is "worktrees" or "stashes")
                {
                    MainNavigation.SelectedItem = MainNavigation.MenuItems[3];
                    ShowWorkspace("worktrees");
                }
                else if (options.View == "remotes")
                {
                    MainNavigation.SelectedItem = MainNavigation.MenuItems[4];
                    ShowWorkspace("remotes");
                }
                else if (options.View == "tags")
                {
                    MainNavigation.SelectedItem = MainNavigation.MenuItems[5];
                    ShowWorkspace("tags");
                }
                else
                {
                    MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
                    ShowWorkspace("changes");
                }
            }
            else
            {
                MainNavigation.SelectedItem = MainNavigation.SettingsItem;
                ShowWorkspace("settings");
                latestOperationTask = Task.WhenAll(
                    EnsureCodexSettingsLoadedAsync(),
                    EnsureGitHubAccountsLoadedAsync());
            }

            await WaitForLatestOperationAsync();
            if (ErrorBar.IsOpen)
            {
                await FailCaptureAsync(ErrorBar.Message, App.CommandLineArguments);
                return;
            }

            await WaitForLayoutAsync();
            if (options.View == "changes")
            {
                // Let the virtualized list materialize its native controls
                // before selecting a line for the read-only capture.
                SelectFirstPartialLineForCapture();
                await WaitForLayoutAsync();
            }

            await CaptureRootGridAsync(options.OutputPath);
        }
        catch (Exception exception)
        {
            await FailCaptureAsync(exception.Message, App.CommandLineArguments, exception);
        }
    }

    private async Task WaitForLatestOperationAsync()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var task = latestOperationTask;
            if (task is null)
            {
                return;
            }

            await task;
            if (ReferenceEquals(task, latestOperationTask))
            {
                return;
            }
        }

        throw new InvalidOperationException("The native view did not settle before capture.");
    }

    private Task WaitForLayoutAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!RootGrid.DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
                {
                    RootGrid.UpdateLayout();
                    completion.TrySetResult(true);
                }))
        {
            completion.TrySetException(new InvalidOperationException("The native UI dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private async Task CaptureRootGridAsync(string outputPath)
    {
        RootGrid.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(RootGrid);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            throw new InvalidOperationException("The native UI rendered an empty surface.");
        }

        var pixelBuffer = await bitmap.GetPixelsAsync();
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException("The capture output directory is unavailable.");
        }

        Directory.CreateDirectory(outputDirectory);
        var outputFolder = await StorageFolder.GetFolderFromPathAsync(outputDirectory);
        var outputFile = await outputFolder.CreateFileAsync(
            Path.GetFileName(outputPath),
            CreationCollisionOption.ReplaceExisting);
        using var outputStream = await outputFile.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight,
            96,
            96,
            pixelBuffer.ToArray());
        await encoder.FlushAsync();
        await outputStream.FlushAsync();

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new IOException("The PNG encoder did not create a non-empty output file.");
        }

        StatusText.Text = $"Capture written to {outputPath}";
        var codexCleanup = DisposeCodexClientAsync();
        var githubCleanup = DisposeGitHubAccountsAsync();
        try
        {
            await Task.WhenAll(codexCleanup, githubCleanup);
        }
        catch (Exception exception)
        {
            await FailCaptureAsync(
                "The capture was written, but an owned account service could not be closed.",
                App.CommandLineArguments,
                exception);
            return;
        }

        Environment.Exit(0);
    }

    private async Task FailCaptureAsync(
        string message,
        IReadOnlyList<string> arguments,
        Exception? exception = null)
    {
        var detail = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        var outputPath = TryGetCaptureOutputPath(arguments);
        var errorPath = outputPath is null
            ? Path.Combine(AppContext.BaseDirectory, "capture-error.txt")
            : outputPath + ".error.txt";

        var codexCleanup = DisposeCodexClientAsync();
        var githubCleanup = DisposeGitHubAccountsAsync();
        try
        {
            await Task.WhenAll(codexCleanup, githubCleanup);
        }
        catch (Exception disposeException)
        {
            detail = $"{detail}{Environment.NewLine}Unable to close an owned account service: {disposeException}";
        }

        try
        {
            var directory = Path.GetDirectoryName(errorPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(errorPath, detail);
        }
        catch (Exception writeException)
        {
            detail = $"{detail}{Environment.NewLine}Unable to write capture error: {writeException.Message}";
        }

        Environment.Exit(1);
    }

    private static string? TryGetCaptureOutputPath(IReadOnlyList<string> arguments)
    {
        for (var index = 1; index + 1 < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--capture", StringComparison.OrdinalIgnoreCase))
            {
                var path = arguments[index + 1].Trim('"');
                return Path.IsPathFullyQualified(path) ? path : null;
            }
        }

        return null;
    }
}
