using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;
using Windows.ApplicationModel.DataTransfer;

namespace WinGit.Native;

public sealed partial class NativeSubmoduleDiffView : UserControl
{
    private const string DefaultOpenUnavailableReason =
        "Opening this submodule is unavailable until the host confirms an initialized local target.";

    private SubmoduleComparison? currentComparison;
    private string currentPath = string.Empty;
    private bool currentReadOnly = true;
    private bool canOpenSubmodule;
    private bool openSubmoduleInteractionEnabled = true;
    private string openUnavailableReason = DefaultOpenUnavailableReason;

    public NativeSubmoduleDiffView()
    {
        InitializeComponent();
        Clear();
    }

    public event EventHandler? OpenSubmoduleRequested;

    public bool IsReadOnly => currentReadOnly;

    public string? CurrentPath => currentComparison is null ? null : currentPath;

    public void Clear()
    {
        currentComparison = null;
        currentPath = string.Empty;
        currentReadOnly = true;
        canOpenSubmodule = false;
        openSubmoduleInteractionEnabled = true;
        openUnavailableReason = DefaultOpenUnavailableReason;

        SubmodulePathText.Text = string.Empty;
        ChangeExplanationText.Text = "Select a submodule change to inspect its recorded revisions.";
        ContextText.Text = string.Empty;
        OldRevisionTextBox.Text = string.Empty;
        NewRevisionTextBox.Text = string.Empty;
        DirtyChangesText.Text = string.Empty;
        OpenSubmoduleReasonText.Text = string.Empty;
        CopyStatusText.Text = string.Empty;

        RevisionPanel.Visibility = Visibility.Collapsed;
        OldRevisionRow.Visibility = Visibility.Collapsed;
        NewRevisionRow.Visibility = Visibility.Collapsed;
        CopyOldRevisionButton.IsEnabled = false;
        CopyNewRevisionButton.IsEnabled = false;
        CopyStatusText.Visibility = Visibility.Collapsed;
        DirtyChangesPanel.Visibility = Visibility.Collapsed;
        OpenSubmodulePanel.Visibility = Visibility.Collapsed;
        OpenSubmoduleButton.IsEnabled = false;
    }

    public void SetComparison(
        SubmoduleComparison comparison,
        string path,
        bool readOnly,
        bool canOpenSubmodule = false,
        string? openUnavailableReason = null)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        currentComparison = comparison;
        currentPath = path;
        currentReadOnly = readOnly;
        this.canOpenSubmodule = canOpenSubmodule;
        this.openUnavailableReason = string.IsNullOrWhiteSpace(openUnavailableReason)
            ? DefaultOpenUnavailableReason
            : openUnavailableReason.Trim();
        ApplyComparison();
    }

    public void SetOpenSubmoduleAvailability(
        bool isAvailable,
        string? unavailableReason = null)
    {
        canOpenSubmodule = isAvailable;
        openUnavailableReason = string.IsNullOrWhiteSpace(unavailableReason)
            ? DefaultOpenUnavailableReason
            : unavailableReason.Trim();
        UpdateOpenSubmoduleAction();
    }

    public void SetOpenSubmoduleInteractionEnabled(bool isEnabled)
    {
        openSubmoduleInteractionEnabled = isEnabled;
        UpdateOpenSubmoduleAction();
    }

    private void ApplyComparison()
    {
        if (currentComparison is not { } comparison)
        {
            Clear();
            return;
        }

        SubmodulePathText.Text = $"Path: {currentPath}";
        ChangeExplanationText.Text = BuildChangeExplanation(comparison);
        ContextText.Text = currentReadOnly
            ? "History view. This captured change cannot be staged from here."
            : comparison.HasDirtyChanges
                ? "Working tree view. Commit local changes inside the submodule before recording its revision in the parent repository."
                : "Working tree view. This change can be committed to the parent repository.";

        SetRevisionRow(
            OldRevisionRow,
            OldRevisionTextBox,
            CopyOldRevisionButton,
            comparison.OldCommitId);
        SetRevisionRow(
            NewRevisionRow,
            NewRevisionTextBox,
            CopyNewRevisionButton,
            comparison.NewCommitId);
        RevisionPanel.Visibility = Visibility.Visible;

        if (comparison.HasDirtyChanges)
        {
            DirtyChangesText.Text = BuildDirtyChangesExplanation(comparison);
            DirtyChangesPanel.Visibility = Visibility.Visible;
        }
        else
        {
            DirtyChangesText.Text = string.Empty;
            DirtyChangesPanel.Visibility = Visibility.Collapsed;
        }

        CopyStatusText.Text = string.Empty;
        CopyStatusText.Visibility = Visibility.Collapsed;
        OpenSubmodulePanel.Visibility = Visibility.Visible;
        UpdateOpenSubmoduleAction();
    }

    private void SetRevisionRow(
        Grid row,
        TextBox textBox,
        Button copyButton,
        string? commitId)
    {
        var hasRevision = !string.IsNullOrWhiteSpace(commitId);
        row.Visibility = hasRevision ? Visibility.Visible : Visibility.Collapsed;
        textBox.Text = commitId ?? string.Empty;
        copyButton.IsEnabled = hasRevision;
    }

    private void UpdateOpenSubmoduleAction()
    {
        if (currentComparison is null)
        {
            OpenSubmodulePanel.Visibility = Visibility.Collapsed;
            OpenSubmoduleButton.IsEnabled = false;
            OpenSubmoduleReasonText.Text = string.Empty;
            return;
        }

        OpenSubmodulePanel.Visibility = Visibility.Visible;
        OpenSubmoduleButton.IsEnabled = canOpenSubmodule && openSubmoduleInteractionEnabled;
        OpenSubmoduleReasonText.Text = canOpenSubmodule
            ? openSubmoduleInteractionEnabled
                ? currentReadOnly
                    ? "Open the current initialized submodule checkout. It may differ from this historical revision."
                    : "Open the current initialized submodule checkout as a separate repository."
                : "Opening is unavailable while Git is busy or a repository mutation is running."
            : openUnavailableReason;
    }

    private void CopyOldRevisionButton_Click(object sender, RoutedEventArgs e) =>
        CopyRevision(OldRevisionTextBox.Text, "Previous revision copied to the clipboard.");

    private void CopyNewRevisionButton_Click(object sender, RoutedEventArgs e) =>
        CopyRevision(NewRevisionTextBox.Text, "New revision copied to the clipboard.");

    private void CopyRevision(string revision, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(revision);
            Clipboard.SetContent(package);
            CopyStatusText.Text = successMessage;
        }
        catch (Exception exception)
        {
            CopyStatusText.Text = $"Unable to copy the revision: {exception.Message}";
        }

        CopyStatusText.Visibility = Visibility.Visible;
    }

    private void OpenSubmoduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (canOpenSubmodule && currentComparison is not null)
        {
            OpenSubmoduleRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private string BuildChangeExplanation(SubmoduleComparison comparison)
    {
        var verb = currentReadOnly ? "was" : "has been";
        var suffix = currentReadOnly
            ? string.Empty
            : " This change can be committed to the parent repository.";

        if (comparison.IsAdded && comparison.NewCommitId is { } addedCommitId)
        {
            return $"This submodule {verb} added, pointing at revision {ShortObjectId(addedCommitId)}.{suffix}";
        }

        if (comparison.IsRemoved && comparison.OldCommitId is { } removedCommitId)
        {
            return $"This submodule {verb} removed while pointing at revision {ShortObjectId(removedCommitId)}.{suffix}";
        }

        if (comparison.HasRevisionChange
            && comparison.OldCommitId is { } oldCommitId
            && comparison.NewCommitId is { } newCommitId)
        {
            return $"This submodule changed its revision from {ShortObjectId(oldCommitId)} to {ShortObjectId(newCommitId)}.{suffix}";
        }

        if (comparison.IsDirtyOnly)
        {
            return currentReadOnly
                ? "This history entry records local changes while the submodule revision stayed the same."
                : "This submodule has local changes while its recorded revision stayed the same.";
        }

        return "This submodule change has no recorded revision transition.";
    }

    private string BuildDirtyChangesExplanation(SubmoduleComparison comparison)
    {
        var state = comparison.OldIsDirty && comparison.NewIsDirty
            ? "local changes on both sides"
            : "local changes";
        var subject = currentReadOnly
            ? $"This historical snapshot includes {state} in the submodule."
            : $"This submodule has {state}.";
        return subject
            + " Those changes must be committed inside the submodule before they can be part of the parent repository.";
    }

    private static string ShortObjectId(string value) =>
        value.Length > 12 ? value[..12] : value;
}
