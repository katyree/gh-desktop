using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private IReadOnlyList<NativeIntegrationOption> discoveredEditors = Array.Empty<NativeIntegrationOption>();
    private IReadOnlyList<NativeIntegrationOption> discoveredShells = Array.Empty<NativeIntegrationOption>();
    private bool integrationsInitialized;

    private void InitializeIntegrationControls()
    {
        if (integrationsInitialized)
        {
            return;
        }

        integrationsInitialized = true;
        RefreshIntegrationDiscovery();
    }

    private void RefreshIntegrationDiscovery()
    {
        try
        {
            var result = NativeIntegrationDiscovery.DiscoverAll();
            discoveredEditors = result.Editors;
            discoveredShells = result.Shells;

            loadingSettings = true;
            try
            {
                EditorIntegrationComboBox.ItemsSource = discoveredEditors;
                ShellIntegrationComboBox.ItemsSource = discoveredShells;
                EditorIntegrationComboBox.SelectedItem = SelectIntegration(
                    discoveredEditors,
                    settings.EditorId);
                ShellIntegrationComboBox.SelectedItem = SelectIntegration(
                    discoveredShells,
                    settings.ShellId);
            }
            finally
            {
                loadingSettings = false;
            }

            EditorIntegrationStatusText.Text = BuildIntegrationStatus(
                "editor",
                discoveredEditors,
                settings.EditorId);
            ShellIntegrationStatusText.Text = BuildIntegrationStatus(
                "shell",
                discoveredShells,
                settings.ShellId);
            EditorIntegrationDiagnosticsText.Text = BuildUnavailableText(
                "editor.",
                settings.EditorId,
                discoveredEditors,
                result.Unavailable);
            ShellIntegrationDiagnosticsText.Text = BuildUnavailableText(
                "shell.",
                settings.ShellId,
                discoveredShells,
                result.Unavailable);
            StatusText.Text = "External tools refreshed";
            UpdateIntegrationCommandStates();
        }
        catch (Exception exception)
        {
            discoveredEditors = Array.Empty<NativeIntegrationOption>();
            discoveredShells = Array.Empty<NativeIntegrationOption>();
            EditorIntegrationComboBox.ItemsSource = discoveredEditors;
            ShellIntegrationComboBox.ItemsSource = discoveredShells;
            EditorIntegrationStatusText.Text = "No supported editor could be discovered.";
            ShellIntegrationStatusText.Text = "No supported shell could be discovered.";
            EditorIntegrationDiagnosticsText.Text = "Refresh failed. Check the error message and try again.";
            ShellIntegrationDiagnosticsText.Text = "Refresh failed. Check the error message and try again.";
            UpdateIntegrationCommandStates();
            ShowError("Unable to discover external tools", exception);
        }
    }

    private void IntegrationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (loadingSettings || sender is not ComboBox comboBox)
        {
            return;
        }

        if (ReferenceEquals(comboBox, EditorIntegrationComboBox))
        {
            settings.EditorId = (comboBox.SelectedItem as NativeIntegrationOption)?.StableId;
        }
        else if (ReferenceEquals(comboBox, ShellIntegrationComboBox))
        {
            settings.ShellId = (comboBox.SelectedItem as NativeIntegrationOption)?.StableId;
        }
        else
        {
            return;
        }

        if (!diagnosticCaptureMode)
        {
            _ = SaveSettingsAsync();
        }
        RefreshIntegrationStatusText();
        UpdateIntegrationCommandStates();
    }

    private void RefreshIntegrationsButton_Click(object sender, RoutedEventArgs args)
    {
        if (mutationInProgress || BusyRing.IsActive)
        {
            return;
        }

        RefreshIntegrationDiscovery();
    }

    private void IntegrationMenuItem_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuFlyoutItem { Tag: string command })
        {
            return;
        }

        switch (command)
        {
            case "OpenRepositoryEditor":
                OpenRepositoryInEditor();
                break;
            case "OpenSelectedFileEditor":
                OpenSelectedFileInEditor();
                break;
            case "OpenRepositoryShell":
                OpenRepositoryShell();
                break;
            case "RevealRepository":
                RevealRepositoryInFileManager();
                break;
            case "RevealSelectedFile":
                RevealSelectedFileInFileManager();
                break;
        }
    }

    private void OpenRepositoryEditorButton_Click(object sender, RoutedEventArgs args) =>
        OpenRepositoryInEditor();

    private void OpenSelectedFileEditorButton_Click(object sender, RoutedEventArgs args) =>
        OpenSelectedFileInEditor();

    private void OpenRepositoryShellButton_Click(object sender, RoutedEventArgs args) =>
        OpenRepositoryShell();

    private void RevealRepositoryButton_Click(object sender, RoutedEventArgs args) =>
        RevealRepositoryInFileManager();

    private void RevealSelectedFileButton_Click(object sender, RoutedEventArgs args) =>
        RevealSelectedFileInFileManager();

    private void OpenRepositoryInEditor()
    {
        if (!CanLaunchRepositoryIntegration() || repositoryRoot is null)
        {
            return;
        }

        if (EditorIntegrationComboBox.SelectedItem is not NativeIntegrationOption editor)
        {
            ShowError(
                "No editor selected",
                new InvalidOperationException("Choose an installed editor in Settings before opening a repository."));
            return;
        }

        try
        {
            NativeExternalLaunchers.LaunchEditor(editor, repositoryRoot);
            StatusText.Text = $"Opened repository in {editor.DisplayName}";
        }
        catch (Exception exception)
        {
            ShowError("Unable to open repository in editor", exception);
        }
    }

    private void OpenSelectedFileInEditor()
    {
        if (!CanLaunchSelectedFileIntegration()
            || repositoryRoot is null
            || string.IsNullOrWhiteSpace(selectedChangePath))
        {
            return;
        }

        if (EditorIntegrationComboBox.SelectedItem is not NativeIntegrationOption editor)
        {
            ShowError(
                "No editor selected",
                new InvalidOperationException("Choose an installed editor in Settings before opening a file."));
            return;
        }

        try
        {
            NativeExternalLaunchers.LaunchEditor(editor, repositoryRoot, selectedChangePath);
            StatusText.Text = $"Opened {selectedChangePath} in {editor.DisplayName}";
        }
        catch (Exception exception)
        {
            ShowError("Unable to open selected file in editor", exception);
        }
    }

    private void OpenRepositoryShell()
    {
        if (!CanLaunchRepositoryIntegration() || repositoryRoot is null)
        {
            return;
        }

        if (ShellIntegrationComboBox.SelectedItem is not NativeIntegrationOption shell)
        {
            ShowError(
                "No shell selected",
                new InvalidOperationException("Choose an installed shell in Settings before opening a repository shell."));
            return;
        }

        try
        {
            NativeExternalLaunchers.LaunchShell(shell, repositoryRoot);
            StatusText.Text = $"Opened repository in {shell.DisplayName}";
        }
        catch (Exception exception)
        {
            ShowError("Unable to open repository shell", exception);
        }
    }

    private void RevealRepositoryInFileManager()
    {
        if (!CanLaunchRepositoryIntegration() || repositoryRoot is null)
        {
            return;
        }

        try
        {
            NativeExternalLaunchers.RevealInFileManager(repositoryRoot);
            StatusText.Text = "Opened repository in File Explorer";
        }
        catch (Exception exception)
        {
            ShowError("Unable to reveal repository", exception);
        }
    }

    private void RevealSelectedFileInFileManager()
    {
        if (!CanLaunchSelectedFileIntegration()
            || repositoryRoot is null
            || string.IsNullOrWhiteSpace(selectedChangePath))
        {
            return;
        }

        var relativePath = selectedChangePath;
        var selectedPathExists = IsRepositoryPathPresent(repositoryRoot, relativePath);
        try
        {
            NativeExternalLaunchers.RevealInFileManager(repositoryRoot, relativePath);
            StatusText.Text = selectedPathExists
                ? $"Opened {relativePath} in File Explorer"
                : $"{relativePath} is missing; opened its containing folder in File Explorer";
        }
        catch (Exception exception)
        {
            ShowError("Unable to reveal selected file", exception);
        }
    }

    private bool CanLaunchRepositoryIntegration() =>
        repositoryRoot is not null
        && !diagnosticCaptureMode
        && !mutationInProgress
        && !BusyRing.IsActive;

    private bool CanLaunchSelectedFileIntegration() =>
        CanLaunchRepositoryIntegration()
        && currentWorkspace == "changes"
        && selectedChange is not null
        && !string.IsNullOrWhiteSpace(selectedChangePath);

    private void UpdateIntegrationCommandStates()
    {
        if (OpenRepositoryEditorMenuItem is null)
        {
            return;
        }

        var canOpenRepository = CanLaunchRepositoryIntegration();
        var canOpenSelectedFile = CanLaunchSelectedFileIntegration();
        var editorAvailable = EditorIntegrationComboBox.SelectedItem is NativeIntegrationOption;
        var shellAvailable = ShellIntegrationComboBox.SelectedItem is NativeIntegrationOption;

        OpenRepositoryEditorMenuItem.IsEnabled = canOpenRepository && editorAvailable;
        OpenSelectedFileEditorMenuItem.IsEnabled = canOpenSelectedFile && editorAvailable;
        OpenRepositoryShellMenuItem.IsEnabled = canOpenRepository && shellAvailable;
        RevealRepositoryMenuItem.IsEnabled = canOpenRepository;
        RevealSelectedFileMenuItem.IsEnabled = canOpenSelectedFile;
        OpenRepositoryEditorButton.IsEnabled = canOpenRepository && editorAvailable;
        OpenSelectedFileEditorButton.IsEnabled = canOpenSelectedFile && editorAvailable;
        OpenRepositoryShellButton.IsEnabled = canOpenRepository && shellAvailable;
        RevealRepositoryButton.IsEnabled = canOpenRepository;
        RevealSelectedFileButton.IsEnabled = canOpenSelectedFile;
        RefreshIntegrationsButton.IsEnabled = !diagnosticCaptureMode
            && !mutationInProgress
            && !BusyRing.IsActive;
    }

    private void RefreshIntegrationStatusText()
    {
        EditorIntegrationStatusText.Text = BuildIntegrationStatus(
            "editor",
            discoveredEditors,
            settings.EditorId);
        ShellIntegrationStatusText.Text = BuildIntegrationStatus(
            "shell",
            discoveredShells,
            settings.ShellId);
    }

    private static NativeIntegrationOption? SelectIntegration(
        IReadOnlyList<NativeIntegrationOption> options,
        string? savedId)
    {
        if (!string.IsNullOrWhiteSpace(savedId))
        {
            return options.FirstOrDefault(option =>
                string.Equals(option.StableId, savedId, StringComparison.Ordinal));
        }

        return options.FirstOrDefault();
    }

    private static string BuildIntegrationStatus(
        string kind,
        IReadOnlyList<NativeIntegrationOption> options,
        string? savedId)
    {
        if (options.Count == 0)
        {
            return $"No supported {kind} is installed.";
        }

        if (!string.IsNullOrWhiteSpace(savedId)
            && !options.Any(option => string.Equals(option.StableId, savedId, StringComparison.Ordinal)))
        {
            return $"Saved {kind} selection is unavailable. Choose an installed {kind}.";
        }

        var noun = options.Count == 1 ? kind : $"{kind}s";
        return $"{options.Count} supported {noun} detected.";
    }

    private static string BuildUnavailableText(
        string stableIdPrefix,
        string? savedId,
        IReadOnlyList<NativeIntegrationOption> options,
        IEnumerable<NativeIntegrationDiagnostic> diagnostics)
    {
        if (options.Count == 0)
        {
            var kind = stableIdPrefix.StartsWith("editor.", StringComparison.Ordinal)
                ? "editor"
                : "shell";
            return $"No supported {kind} was found. Install a supported {kind} and refresh.";
        }

        if (string.IsNullOrWhiteSpace(savedId)
            || options.Any(option => string.Equals(option.StableId, savedId, StringComparison.Ordinal)))
        {
            return string.Empty;
        }

        var savedDiagnostic = diagnostics.FirstOrDefault(diagnostic =>
            string.Equals(diagnostic.StableId, savedId, StringComparison.Ordinal));
        return savedDiagnostic is null
            ? "The saved selection is unavailable. Choose an installed option."
            : $"Saved selection unavailable: {savedDiagnostic.DisplayName}: {savedDiagnostic.Reason}";
    }

    private static bool IsRepositoryPathPresent(string root, string relativePath)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(root, relativePath));
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
