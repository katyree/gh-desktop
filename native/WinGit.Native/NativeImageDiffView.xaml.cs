using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinGit.Core;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace WinGit.Native;

public sealed partial class NativeImageDiffView : UserControl
{
    private const int MaximumDecodePixelDimension = ImageDifferencePixels.MaximumPixelDimension;

    private CancellationTokenSource? renderCancellation;
    private CancellationTokenSource? modeRenderCancellation;
    private long renderGeneration;
    private long modeGeneration;
    private string preferredMode = NativeSettings.DefaultImageDiffMode;
    private bool suppressModeEvents;
    private DecodedImage? beforeDecoded;
    private DecodedImage? afterDecoded;
    private ImageComparison? currentComparison;
    private WriteableBitmap? differenceBitmap;
    private string comparisonStatus = "Side-by-side comparison.";

    public NativeImageDiffView()
    {
        InitializeComponent();
        SetPreferredModeInternal(NativeSettings.DefaultImageDiffMode, notifyParent: false);
        ClearVisuals();
    }

    public event EventHandler? ModeChanged;

    public string PreferredMode => preferredMode;

    public void SetPreferredMode(string mode)
    {
        SetPreferredModeInternal(mode, notifyParent: false);
    }

    public void Clear()
    {
        renderCancellation?.Cancel();
        renderCancellation = null;
        modeRenderCancellation?.Cancel();
        modeRenderCancellation = null;
        renderGeneration++;
        modeGeneration++;
        beforeDecoded = null;
        afterDecoded = null;
        currentComparison = null;
        differenceBitmap = null;
        comparisonStatus = "Side-by-side comparison.";
        ClearVisuals();
        UpdateModeControls();
    }

    public async Task SetComparisonAsync(
        ImageComparison comparison,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        renderCancellation?.Cancel();
        var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var localToken = localCancellation.Token;
        renderCancellation = localCancellation;
        var generation = ++renderGeneration;
        modeRenderCancellation?.Cancel();
        modeRenderCancellation = null;
        modeGeneration++;
        beforeDecoded = null;
        afterDecoded = null;
        differenceBitmap = null;
        currentComparison = comparison;
        comparisonStatus = BuildComparisonStatus(comparison);
        ClearVisuals();
        ImageDiffStatusText.Text = comparisonStatus;

        try
        {
            var decoded = await Task.WhenAll(
                DecodeAsync(comparison.Before, localToken),
                DecodeAsync(comparison.After, localToken));
            if (!IsCurrent(generation, localToken))
            {
                return;
            }

            beforeDecoded = decoded[0];
            afterDecoded = decoded[1];
            ApplySide(
                isBefore: true,
                comparison.Before,
                beforeDecoded,
                comparison);
            ApplySide(
                isBefore: false,
                comparison.After,
                afterDecoded,
                comparison);
            await RenderPreferredModeAsync(localToken);
        }
        catch (OperationCanceledException) when (localToken.IsCancellationRequested)
        {
            // Selecting another path or clearing the repository owns cancellation.
        }
        catch (Exception exception)
        {
            if (IsCurrent(generation, localToken))
            {
                comparisonStatus = $"Side-by-side comparison unavailable: {exception.Message}";
                ShowTwoUp();
            }
        }
        finally
        {
            if (ReferenceEquals(renderCancellation, localCancellation))
            {
                renderCancellation = null;
            }

            localCancellation.Dispose();
        }
    }

    private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressModeEvents || ModeComboBox.SelectedValue is not string mode)
        {
            return;
        }

        SetPreferredModeInternal(mode, notifyParent: true);
    }

    private void ModeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ModeSliderValueText is not null)
        {
            ModeSliderValueText.Text = $"{Math.Round(ModeSlider.Value):0}%";
        }

        if (!suppressModeEvents
            && (preferredMode is "Swipe" or "OnionSkin")
            && currentComparison is not null)
        {
            _ = RenderPreferredModeAsync();
        }
    }

    private void ModeOverlayGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AdvancedModePanel is null || ModeBeforeImage is null || ModeAfterImage is null)
        {
            return;
        }

        ApplyOverlayVisuals();
    }

    private void SetPreferredModeInternal(string mode, bool notifyParent)
    {
        var normalized = NativeSettingsStore.NormalizeImageDiffMode(mode);
        var changed = !string.Equals(preferredMode, normalized, StringComparison.Ordinal);
        preferredMode = normalized;
        suppressModeEvents = true;
        try
        {
            ModeComboBox.SelectedValue = preferredMode;
            UpdateModeControls();
        }
        finally
        {
            suppressModeEvents = false;
        }

        if (changed && currentComparison is not null)
        {
            _ = RenderPreferredModeAsync();
        }

        if (notifyParent && changed)
        {
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateModeControls()
    {
        if (ModeSliderLabel is null || ModeSlider is null || ModeSliderValueText is null)
        {
            return;
        }

        var sliderVisible = preferredMode is "Swipe" or "OnionSkin";
        var visibility = sliderVisible ? Visibility.Visible : Visibility.Collapsed;
        ModeSliderLabel.Visibility = visibility;
        ModeSlider.Visibility = visibility;
        ModeSliderValueText.Visibility = visibility;
        ModeSliderLabel.Text = preferredMode switch
        {
            "Swipe" => "Swipe position",
            "OnionSkin" => "Onion opacity",
            _ => string.Empty,
        };
        ModeSliderValueText.Text = $"{Math.Round(ModeSlider.Value):0}%";
    }

    private async Task RenderPreferredModeAsync(CancellationToken selectionToken = default)
    {
        modeRenderCancellation?.Cancel();
        var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(selectionToken);
        var localToken = localCancellation.Token;
        modeRenderCancellation = localCancellation;
        var generation = ++modeGeneration;

        try
        {
            localToken.ThrowIfCancellationRequested();
            if (preferredMode == NativeSettings.DefaultImageDiffMode)
            {
                ShowTwoUp();
                return;
            }

            if (!CanUseOverlay())
            {
                ShowTwoUp($"{GetModeLabel(preferredMode)} is unavailable for one-sided or unavailable image data; showing 2-up.");
                return;
            }

            if (preferredMode == "Difference")
            {
                var before = beforeDecoded;
                var after = afterDecoded;
                if (before is null
                    || after is null
                    || before.BgraPixels is null
                    || after.BgraPixels is null
                    || before.DecodedPixelWidth <= 0
                    || before.DecodedPixelHeight <= 0
                    || after.DecodedPixelWidth <= 0
                    || after.DecodedPixelHeight <= 0)
                {
                    ShowTwoUp("Difference is unavailable for this image format; showing 2-up.");
                    return;
                }

                if (differenceBitmap is null)
                {
                    var difference = await Task.Run(
                        () => ImageDifferencePixels.Build(
                            new ImageDifferenceSource(
                                before.BgraPixels!.AsMemory(),
                                before.DecodedPixelWidth,
                                before.DecodedPixelHeight,
                                before.OriginalPixelWidth,
                                before.OriginalPixelHeight),
                            new ImageDifferenceSource(
                                after.BgraPixels!.AsMemory(),
                                after.DecodedPixelWidth,
                                after.DecodedPixelHeight,
                                after.OriginalPixelWidth,
                                after.OriginalPixelHeight),
                            localToken),
                        localToken);
                    if (!IsModeCurrent(generation, localToken))
                    {
                        return;
                    }

                    differenceBitmap = CreateDifferenceBitmap(
                        difference.Pixels,
                        difference.Width,
                        difference.Height);
                }
            }

            if (!IsModeCurrent(generation, localToken))
            {
                return;
            }

            ShowOverlayMode(preferredMode);
        }
        catch (OperationCanceledException) when (localToken.IsCancellationRequested)
        {
            // A newer mode or selection owns the view.
        }
        catch (Exception)
        {
            if (IsModeCurrent(generation, localToken))
            {
                ShowTwoUp($"{GetModeLabel(preferredMode)} preview is unavailable; showing 2-up.");
            }
        }
        finally
        {
            if (ReferenceEquals(modeRenderCancellation, localCancellation))
            {
                modeRenderCancellation = null;
            }

            localCancellation.Dispose();
        }
    }

    private bool CanUseOverlay()
    {
        var comparison = currentComparison;
        return comparison is not null
            && !comparison.IsTruncated
            && string.IsNullOrWhiteSpace(comparison.Message)
            && beforeDecoded is { Bitmap: not null, Failed: false }
            && afterDecoded is { Bitmap: not null, Failed: false };
    }

    private void ShowTwoUp(string? reason = null)
    {
        TwoUpPanel.Visibility = Visibility.Visible;
        AdvancedModePanel.Visibility = Visibility.Collapsed;
        ImageDiffStatusText.Text = reason is null
            ? comparisonStatus
            : $"{comparisonStatus} {reason}";
        ModeBeforeImage.Source = null;
        ModeAfterImage.Source = null;
        ResetOverlayImageLayout(ModeBeforeImage);
        ResetOverlayImageLayout(ModeAfterImage);
        ModeBeforeImage.Visibility = Visibility.Visible;
        ModeAfterImage.Visibility = Visibility.Visible;
        ApplyOverlayVisuals();
    }

    private void ShowOverlayMode(string mode)
    {
        var before = beforeDecoded;
        var after = afterDecoded;
        if (before?.Bitmap is null || after?.Bitmap is null)
        {
            ShowTwoUp($"{GetModeLabel(mode)} is unavailable; showing 2-up.");
            return;
        }

        TwoUpPanel.Visibility = Visibility.Collapsed;
        AdvancedModePanel.Visibility = Visibility.Visible;
        ImageDiffStatusText.Text = $"{GetModeLabel(mode)} comparison.";
        ModeBeforeImage.Visibility = Visibility.Visible;
        ModeAfterImage.Visibility = Visibility.Visible;
        ModeBeforeImage.Source = before.Bitmap;
        ModeAfterImage.Source = after.Bitmap;
        ModeBeforeBadge.Visibility = Visibility.Visible;
        ModeAfterBadge.Visibility = Visibility.Visible;
        ModeBeforeLabel.Text = "Before";
        ModeAfterLabel.Text = "After";

        if (mode == "Difference")
        {
            ModeBeforeImage.Source = differenceBitmap;
            ModeAfterImage.Source = null;
            ModeAfterImage.Visibility = Visibility.Collapsed;
            ModeBeforeBadge.Visibility = Visibility.Visible;
            ModeAfterBadge.Visibility = Visibility.Collapsed;
            ModeBeforeLabel.Text = "Difference";
            AdvancedModeDetailsText.Text = BuildModeDetails("Difference");
            AdvancedModeFooterText.Text = "Changed areas are highlighted; unchanged areas stay dark.";
        }
        else
        {
            AdvancedModeDetailsText.Text = BuildModeDetails(mode);
            AdvancedModeFooterText.Text = mode == "Swipe"
                ? "Drag the slider to reveal the before and after previews."
                : "Adjust opacity to compare the before and after previews.";
        }

        ApplyOverlayVisuals();
    }

    private string BuildModeDetails(string mode)
    {
        var before = FormatPreviewDimensions(beforeDecoded);
        var after = FormatPreviewDimensions(afterDecoded);
        return $"{GetModeLabel(mode)} · Before {before} · After {after}";
    }

    private void ApplyOverlayVisuals()
    {
        if (AdvancedModePanel.Visibility != Visibility.Visible)
        {
            ResetOverlayImageLayout(ModeBeforeImage);
            ResetOverlayImageLayout(ModeAfterImage);
            return;
        }

        var width = ModeOverlayGrid.ActualWidth;
        var height = ModeOverlayGrid.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            ModeBeforeImage.Clip = null;
            ModeAfterImage.Clip = null;
            return;
        }

        var before = beforeDecoded;
        var after = afterDecoded;
        var difference = differenceBitmap;
        if (preferredMode == "Difference" && difference is not null)
        {
            var differenceScale = Math.Min(
                1d,
                Math.Min(
                    width / (double)difference.PixelWidth,
                    height / (double)difference.PixelHeight));
            SetOverlayImageSize(
                ModeBeforeImage,
                difference.PixelWidth * differenceScale,
                difference.PixelHeight * differenceScale);
            ResetOverlayImageLayout(ModeAfterImage);
        }
        else if (before is not null && after is not null)
        {
            var pairWidth = Math.Max(
                before.OriginalPixelWidth,
                after.OriginalPixelWidth);
            var pairHeight = Math.Max(
                before.OriginalPixelHeight,
                after.OriginalPixelHeight);
            if (pairWidth <= 0 || pairHeight <= 0)
            {
                return;
            }

            var pairScale = Math.Min(
                1d,
                Math.Min(width / pairWidth, height / pairHeight));
            SetOverlayImageSize(
                ModeBeforeImage,
                before.OriginalPixelWidth * pairScale,
                before.OriginalPixelHeight * pairScale);
            SetOverlayImageSize(
                ModeAfterImage,
                after.OriginalPixelWidth * pairScale,
                after.OriginalPixelHeight * pairScale);
        }

        if (preferredMode == "Swipe" && AdvancedModePanel.Visibility == Visibility.Visible)
        {
            var split = width * Math.Clamp(ModeSlider.Value / 100d, 0d, 1d);
            ModeBeforeImage.Clip = CreateSwipeClip(ModeBeforeImage, width, split, beforeSide: true);
            ModeAfterImage.Clip = CreateSwipeClip(ModeAfterImage, width, split, beforeSide: false);
            ModeAfterImage.Opacity = 1;
        }
        else if (preferredMode == "OnionSkin" && AdvancedModePanel.Visibility == Visibility.Visible)
        {
            ModeBeforeImage.Clip = null;
            ModeAfterImage.Clip = null;
            ModeAfterImage.Opacity = Math.Clamp(ModeSlider.Value / 100d, 0d, 1d);
        }
        else
        {
            ModeBeforeImage.Clip = null;
            ModeAfterImage.Clip = null;
            ModeAfterImage.Opacity = 1;
        }
    }

    private static RectangleGeometry? CreateSwipeClip(
        Image image,
        double canvasWidth,
        double split,
        bool beforeSide)
    {
        var imageWidth = !double.IsNaN(image.Width) ? image.Width : image.ActualWidth;
        var imageHeight = !double.IsNaN(image.Height) ? image.Height : image.ActualHeight;
        if (double.IsNaN(imageWidth)
            || double.IsNaN(imageHeight)
            || imageWidth <= 0
            || imageHeight <= 0)
        {
            return null;
        }

        var imageLeft = (canvasWidth - imageWidth) / 2;
        var localSplit = Math.Clamp(split - imageLeft, 0, imageWidth);
        return new RectangleGeometry
        {
            Rect = beforeSide
                ? new Rect(0, 0, localSplit, imageHeight)
                : new Rect(localSplit, 0, imageWidth - localSplit, imageHeight),
        };
    }

    private static void SetOverlayImageSize(Image image, double width, double height)
    {
        image.Width = Math.Max(1, width);
        image.Height = Math.Max(1, height);
        image.HorizontalAlignment = HorizontalAlignment.Center;
        image.VerticalAlignment = VerticalAlignment.Center;
    }

    private static void ResetOverlayImageLayout(Image image)
    {
        image.Width = double.NaN;
        image.Height = double.NaN;
        image.HorizontalAlignment = HorizontalAlignment.Stretch;
        image.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void ApplySide(
        bool isBefore,
        ImageDiffContent? content,
        DecodedImage? decoded,
        ImageComparison comparison)
    {
        var pathText = isBefore ? BeforePathText : AfterPathText;
        var image = isBefore ? BeforeImage : AfterImage;
        var fallback = isBefore ? BeforeFallbackText : AfterFallbackText;
        var details = isBefore ? BeforeDetailsText : AfterDetailsText;
        var previewIssue = comparison.IsTruncated || !string.IsNullOrWhiteSpace(comparison.Message);
        var issueMessage = GetPreviewIssueMessage(comparison);

        image.Source = null;
        fallback.Text = string.Empty;
        pathText.Text = string.Empty;
        details.Text = string.Empty;

        if (content is null)
        {
            if (previewIssue)
            {
                pathText.Text = "Preview unavailable";
                fallback.Text = $"Image preview unavailable: {issueMessage}";
                details.Text = issueMessage;
            }
            else
            {
                var changeLabel = isBefore ? "Added" : "Deleted";
                pathText.Text = changeLabel;
                fallback.Text = isBefore
                    ? "Added in this revision."
                    : "Deleted in this revision.";
                details.Text = "This side is not present in the comparison.";
            }

            return;
        }

        pathText.Text = content.RelativePath;
        if (decoded?.Bitmap is not null && !decoded.Failed)
        {
            image.Source = decoded.Bitmap;
            details.Text = FormatPreviewDetails(content, decoded);
            return;
        }

        fallback.Text = "Image preview unavailable.";
        details.Text = FormatUnavailableDetails(content, decoded);
    }

    private async Task<DecodedImage?> DecodeAsync(
        ImageDiffContent? content,
        CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = content.Bytes.ToArray();
        try
        {
            using var metadataStream = new MemoryStream(bytes, writable: false);
            using var decoderStream = new MemoryStream(bytes, writable: false);
            var decoder = await BitmapDecoder.CreateAsync(metadataStream.AsRandomAccessStream());
            cancellationToken.ThrowIfCancellationRequested();
            var originalWidth = checked((int)decoder.PixelWidth);
            var originalHeight = checked((int)decoder.PixelHeight);
            var scale = Math.Min(
                1d,
                MaximumDecodePixelDimension / (double)Math.Max(originalWidth, originalHeight));
            var decodedWidth = Math.Max(1, (int)Math.Round(originalWidth * scale));
            var decodedHeight = Math.Max(1, (int)Math.Round(originalHeight * scale));
            var previewScaled = decodedWidth != originalWidth || decodedHeight != originalHeight;
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)decodedWidth,
                ScaledHeight = (uint)decodedHeight,
            };

            byte[]? bgraPixels = null;
            try
            {
                var provider = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                cancellationToken.ThrowIfCancellationRequested();
                bgraPixels = provider.DetachPixelData();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Keep the display preview useful when pixel extraction is not supported.
            }

            cancellationToken.ThrowIfCancellationRequested();
            BitmapImage? bitmap = null;
            try
            {
                bitmap = new BitmapImage
                {
                    DecodePixelWidth = decodedWidth,
                    DecodePixelHeight = decodedHeight,
                };
                await bitmap.SetSourceAsync(decoderStream.AsRandomAccessStream());
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // The side remains represented with an actionable fallback.
                bitmap = null;
            }

            return new DecodedImage(
                content,
                bitmap,
                originalWidth,
                originalHeight,
                decodedWidth,
                decodedHeight,
                previewScaled,
                bitmap is null,
                bgraPixels);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return DecodedImage.CreateFailed(content);
        }
    }

    private static WriteableBitmap CreateDifferenceBitmap(
        byte[] pixels,
        int width,
        int height)
    {
        var bitmap = new WriteableBitmap(width, height);
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    private static string BuildComparisonStatus(ImageComparison comparison)
    {
        if (!comparison.IsTruncated && string.IsNullOrWhiteSpace(comparison.Message))
        {
            return "Side-by-side comparison.";
        }

        return $"Side-by-side comparison. Preview unavailable: {GetPreviewIssueMessage(comparison)}";
    }

    private static string GetPreviewIssueMessage(ImageComparison comparison) =>
        string.IsNullOrWhiteSpace(comparison.Message)
            ? "The image data is not available for a safe preview."
            : comparison.Message.Trim();

    private static string GetModeLabel(string mode) =>
        mode switch
        {
            "Swipe" => "Swipe",
            "OnionSkin" => "Onion Skin",
            "Difference" => "Difference",
            _ => "2-up",
        };

    private static string FormatPreviewDetails(ImageDiffContent content, DecodedImage decoded)
    {
        var dimensions = FormatPreviewDimensions(decoded);
        return decoded.PreviewScaled
            ? $"{content.ByteCount:N0} bytes · {dimensions} · preview limited to {decoded.DecodedPixelWidth} × {decoded.DecodedPixelHeight} px"
            : $"{content.ByteCount:N0} bytes · {dimensions}";
    }

    private static string FormatUnavailableDetails(
        ImageDiffContent content,
        DecodedImage? decoded)
    {
        var dimensions = decoded is null || decoded.OriginalPixelWidth <= 0
            ? "dimensions unavailable"
            : $"{decoded.OriginalPixelWidth} × {decoded.OriginalPixelHeight} px";
        return $"{content.ByteCount:N0} bytes · {dimensions} · preview unavailable";
    }

    private static string FormatPreviewDimensions(DecodedImage? decoded)
    {
        if (decoded is null || decoded.OriginalPixelWidth <= 0 || decoded.OriginalPixelHeight <= 0)
        {
            return "dimensions unavailable";
        }

        return $"{decoded.OriginalPixelWidth} × {decoded.OriginalPixelHeight} px";
    }

    private bool IsCurrent(long generation, CancellationToken token) =>
        generation == renderGeneration && !token.IsCancellationRequested;

    private bool IsModeCurrent(long generation, CancellationToken token) =>
        generation == modeGeneration && !token.IsCancellationRequested;

    private void ClearVisuals()
    {
        TwoUpPanel.Visibility = Visibility.Visible;
        AdvancedModePanel.Visibility = Visibility.Collapsed;
        BeforeImage.Source = null;
        AfterImage.Source = null;
        ModeBeforeImage.Source = null;
        ModeAfterImage.Source = null;
        ResetOverlayImageLayout(ModeBeforeImage);
        ResetOverlayImageLayout(ModeAfterImage);
        ModeBeforeImage.Visibility = Visibility.Visible;
        ModeAfterImage.Visibility = Visibility.Visible;
        ModeBeforeBadge.Visibility = Visibility.Visible;
        ModeAfterBadge.Visibility = Visibility.Visible;
        ModeBeforeLabel.Text = "Before";
        ModeAfterLabel.Text = "After";
        ModeBeforeImage.Clip = null;
        ModeAfterImage.Clip = null;
        ModeAfterImage.Opacity = 1;
        BeforePathText.Text = string.Empty;
        AfterPathText.Text = string.Empty;
        BeforeFallbackText.Text = string.Empty;
        AfterFallbackText.Text = string.Empty;
        BeforeDetailsText.Text = string.Empty;
        AfterDetailsText.Text = string.Empty;
        AdvancedModeDetailsText.Text = string.Empty;
        AdvancedModeFooterText.Text = string.Empty;
        ImageDiffStatusText.Text = comparisonStatus;
    }

    private sealed record DecodedImage(
        ImageDiffContent Content,
        BitmapImage? Bitmap,
        int OriginalPixelWidth,
        int OriginalPixelHeight,
        int DecodedPixelWidth,
        int DecodedPixelHeight,
        bool PreviewScaled,
        bool Failed,
        byte[]? BgraPixels)
    {
        public static DecodedImage CreateFailed(ImageDiffContent content) =>
            new(content, null, 0, 0, 0, 0, false, true, null);
    }

}
