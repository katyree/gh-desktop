using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private const int IdealSummaryLength = 50;

    private bool applyingPromptSettings;
    private bool promptControlsInitialized;

    internal event EventHandler? RepositoryIndicatorsEnabledChanged;

    internal event EventHandler? UseWindowsOpenSSHChanged;

    internal event EventHandler? UseExternalCredentialHelperChanged;

    internal void InitializePromptControls()
    {
        applyingPromptSettings = true;
        try
        {
            ConfirmRepositoryRemovalCheckBox.IsChecked = settings.ConfirmRepositoryRemoval;
            ConfirmDiscardChangesCheckBox.IsChecked = settings.ConfirmDiscardChanges;
            ConfirmDiscardChangesPermanentlyCheckBox.IsChecked = settings.ConfirmDiscardChangesPermanently;
            ConfirmDiscardStashCheckBox.IsChecked = settings.ConfirmDiscardStash;
            ConfirmForcePushCheckBox.IsChecked = settings.ConfirmForcePush;
            ConfirmUndoCommitCheckBox.IsChecked = settings.ConfirmUndoCommit;
            ConfirmCommitMessageOverrideCheckBox.IsChecked = settings.ConfirmCommitMessageOverride;
            ConfirmWorktreeRemovalCheckBox.IsChecked = settings.ConfirmWorktreeRemoval;
            ConfirmCommitFilteredChangesCheckBox.IsChecked = settings.ConfirmCommitFilteredChanges;
            ShowCommitLengthWarningCheckBox.IsChecked = settings.ShowCommitLengthWarning;
            RepositoryIndicatorsEnabledCheckBox.IsChecked = settings.RepositoryIndicatorsEnabled;
            var canUseWindowsOpenSSH = NativeSettings.IsWindowsOpenSSHAvailable();
            UseWindowsOpenSSHCheckBox.Visibility = canUseWindowsOpenSSH
                ? Visibility.Visible
                : Visibility.Collapsed;
            UseWindowsOpenSSHCheckBox.IsEnabled = canUseWindowsOpenSSH;
            UseWindowsOpenSSHCheckBox.IsChecked = canUseWindowsOpenSSH
                && settings.UseWindowsOpenSSH;
            UseExternalCredentialHelperCheckBox.IsChecked = settings.UseExternalCredentialHelper;
            UncommittedChangesStrategyComboBox.SelectedValue =
                settings.UncommittedChangesStrategy.ToString();
            promptControlsInitialized = true;
        }
        finally
        {
            applyingPromptSettings = false;
        }

        UpdateCommitLengthWarning();
    }

    internal void UpdateCommitLengthWarning()
    {
        if (CommitLengthWarningText is null)
        {
            return;
        }

        var showWarning = settings.ShowCommitLengthWarning
            && (CommitSummaryBox.Text?.Length ?? 0) > IdealSummaryLength;
        CommitLengthWarningText.Visibility = showWarning
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ConfirmRepositoryRemovalCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingPromptSettings || !promptControlsInitialized || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.ConfirmRepositoryRemoval = checkBox.IsChecked == true;
        _ = SaveSettingsAsync();
    }

    private void ConfirmDiscardChangesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingPromptSettings || !promptControlsInitialized || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.ConfirmDiscardChanges = checkBox.IsChecked == true;
        _ = SaveSettingsAsync();
    }

    private void ConfirmDiscardChangesPermanentlyCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingPromptSettings || !promptControlsInitialized || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.ConfirmDiscardChangesPermanently = checkBox.IsChecked == true;
        _ = SaveSettingsAsync();
    }

    private void ConfirmDiscardStashCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingPromptSettings || !promptControlsInitialized || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.ConfirmDiscardStash = checkBox.IsChecked == true;
        _ = SaveSettingsAsync();
    }

    private void ConfirmForcePushCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingPromptSettings || !promptControlsInitialized || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.ConfirmForcePush = checkBox.IsChecked == true;
        _ = SaveSettingsAsync();
    }

    private void ConfirmUndoCommitCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingPromptSettings || !promptControlsInitialized || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.ConfirmUndoCommit = checkBox.IsChecked == true;
        _ = SaveSettingsAsync();
    }

    private void ConfirmCommitMessageOverrideCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingPromptSettings || !promptControlsInitialized || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.ConfirmCommitMessageOverride = checkBox.IsChecked == true;
        _ = SaveSettingsAsync();
    }

    private void ConfirmWorktreeRemovalCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingPromptSettings || !promptControlsInitialized || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.ConfirmWorktreeRemoval = checkBox.IsChecked == true;
        _ = SaveSettingsAsync();
    }

    private void ConfirmCommitFilteredChangesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingPromptSettings || !promptControlsInitialized || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.ConfirmCommitFilteredChanges = checkBox.IsChecked == true;
        _ = SaveSettingsAsync();
    }

    private void ShowCommitLengthWarningCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingPromptSettings || !promptControlsInitialized || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.ShowCommitLengthWarning = checkBox.IsChecked == true;
        UpdateCommitLengthWarning();
        _ = SaveSettingsAsync();
    }

    private void RepositoryIndicatorsEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingPromptSettings || !promptControlsInitialized || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.RepositoryIndicatorsEnabled = checkBox.IsChecked == true;
        if (!settings.RepositoryIndicatorsEnabled)
        {
            ClearRepositoryIndicatorSnapshots();
        }

        _ = SetRepositoryIndicatorsEnabledAsync(settings.RepositoryIndicatorsEnabled);
        _ = SaveSettingsAsync();
        RepositoryIndicatorsEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UseWindowsOpenSSHCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingPromptSettings
            || !promptControlsInitialized
            || sender is not CheckBox checkBox
            || !NativeSettings.IsWindowsOpenSSHAvailable())
        {
            return;
        }

        settings.UseWindowsOpenSSH = checkBox.IsChecked == true;
        ApplyGitProcessOptions();
        _ = SaveSettingsAsync();
        UseWindowsOpenSSHChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UseExternalCredentialHelperCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (applyingPromptSettings || !promptControlsInitialized || sender is not CheckBox checkBox)
        {
            return;
        }

        settings.UseExternalCredentialHelper = checkBox.IsChecked == true;
        ApplyGitProcessOptions();
        _ = SaveSettingsAsync();
        UseExternalCredentialHelperChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UncommittedChangesStrategyComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (applyingPromptSettings
            || !promptControlsInitialized
            || sender is not ComboBox comboBox
            || comboBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<NativeUncommittedChangesStrategy>(
                tag,
                ignoreCase: true,
                out var strategy))
        {
            return;
        }

        settings.UncommittedChangesStrategy =
            NativeSettingsStore.NormalizeUncommittedChangesStrategy(strategy);
        _ = SaveSettingsAsync();
    }
}
