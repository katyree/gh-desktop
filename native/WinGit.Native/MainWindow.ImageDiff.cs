namespace WinGit.Native;

public sealed partial class MainWindow
{
    private bool applyingImageDiffMode;
    private string imageDiffMode = NativeSettings.DefaultImageDiffMode;

    private void InitializeImageDiffControls()
    {
        DiffImageView.ModeChanged += ImageDiffView_ModeChanged;
        HistoryDiffImageView.ModeChanged += ImageDiffView_ModeChanged;
        ApplyImageDiffModeToControls();
    }

    private void ApplyImageDiffModeToControls()
    {
        imageDiffMode = NativeSettingsStore.NormalizeImageDiffMode(settings.ImageDiffMode);
        settings.ImageDiffMode = imageDiffMode;
        applyingImageDiffMode = true;
        try
        {
            DiffImageView.SetPreferredMode(imageDiffMode);
            HistoryDiffImageView.SetPreferredMode(imageDiffMode);
        }
        finally
        {
            applyingImageDiffMode = false;
        }
    }

    private void ImageDiffView_ModeChanged(object? sender, EventArgs e)
    {
        if (applyingImageDiffMode || sender is not NativeImageDiffView view)
        {
            return;
        }

        imageDiffMode = NativeSettingsStore.NormalizeImageDiffMode(view.PreferredMode);
        settings.ImageDiffMode = imageDiffMode;
        applyingImageDiffMode = true;
        try
        {
            DiffImageView.SetPreferredMode(imageDiffMode);
            HistoryDiffImageView.SetPreferredMode(imageDiffMode);
        }
        finally
        {
            applyingImageDiffMode = false;
        }

        if (!diagnosticCaptureMode)
        {
            _ = SaveSettingsAsync();
        }
    }
}
