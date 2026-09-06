namespace WinGit.Core;

/// <summary>Creates a bounded, centered BGRA8 difference image for two decoded previews.</summary>
public static class ImageDifferencePixels
{
    public const int MaximumPixelDimension = 2048;

    public static ImageDifferenceResult Build(
        ImageDifferenceSource before,
        ImageDifferenceSource after,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(before, nameof(before));
        ValidateSource(after, nameof(after));

        // Keep both revisions on one scale and center the smaller revision so
        // a dimension change remains visible in the result.
        var pairScale = Math.Min(
            1d,
            MaximumPixelDimension / (double)Math.Max(
                Math.Max(before.OriginalWidth, before.OriginalHeight),
                Math.Max(after.OriginalWidth, after.OriginalHeight)));
        var beforeWidth = ScaleDimension(before.OriginalWidth, pairScale);
        var beforeHeight = ScaleDimension(before.OriginalHeight, pairScale);
        var afterWidth = ScaleDimension(after.OriginalWidth, pairScale);
        var afterHeight = ScaleDimension(after.OriginalHeight, pairScale);
        var outputWidth = Math.Max(beforeWidth, afterWidth);
        var outputHeight = Math.Max(beforeHeight, afterHeight);
        var output = new byte[checked(outputWidth * outputHeight * 4)];
        var beforeOffsetX = (outputWidth - beforeWidth) / 2;
        var beforeOffsetY = (outputHeight - beforeHeight) / 2;
        var afterOffsetX = (outputWidth - afterWidth) / 2;
        var afterOffsetY = (outputHeight - afterHeight) / 2;

        for (var y = 0; y < outputHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < outputWidth; x++)
            {
                var beforePixel = ReadCenteredPixel(
                    before,
                    beforeWidth,
                    beforeHeight,
                    x - beforeOffsetX,
                    y - beforeOffsetY);
                var afterPixel = ReadCenteredPixel(
                    after,
                    afterWidth,
                    afterHeight,
                    x - afterOffsetX,
                    y - afterOffsetY);
                var difference = CalculateDifferencePixel(beforePixel, afterPixel);
                var outputOffset = (y * outputWidth + x) * 4;
                output[outputOffset] = difference.B;
                output[outputOffset + 1] = difference.G;
                output[outputOffset + 2] = difference.R;
                output[outputOffset + 3] = difference.A;
            }
        }

        return new ImageDifferenceResult(output, outputWidth, outputHeight);
    }

    private static void ValidateSource(ImageDifferenceSource source, string parameterName)
    {
        if (source.DecodedWidth <= 0
            || source.DecodedHeight <= 0
            || source.OriginalWidth <= 0
            || source.OriginalHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Image dimensions must be positive.");
        }

        var requiredBytes = checked(source.DecodedWidth * source.DecodedHeight * 4);
        if (source.Pixels.Length < requiredBytes)
        {
            throw new ArgumentException("The decoded image buffer is smaller than its dimensions.", parameterName);
        }
    }

    private static int ScaleDimension(int dimension, double scale) =>
        Math.Max(1, (int)Math.Round(dimension * scale, MidpointRounding.AwayFromZero));

    private static ImageDifferencePixel ReadCenteredPixel(
        ImageDifferenceSource source,
        int targetWidth,
        int targetHeight,
        int x,
        int y)
    {
        if (x < 0 || x >= targetWidth || y < 0 || y >= targetHeight)
        {
            return default;
        }

        var sourceX = Math.Min(
            source.DecodedWidth - 1,
            (int)((long)x * source.DecodedWidth / targetWidth));
        var sourceY = Math.Min(
            source.DecodedHeight - 1,
            (int)((long)y * source.DecodedHeight / targetHeight));
        var offset = checked((sourceY * source.DecodedWidth + sourceX) * 4);
        var alpha = source.Pixels.Span[offset + 3];
        // Ignore hidden RGB values from fully transparent pixels.
        return alpha == 0
            ? default
            : new ImageDifferencePixel(
                source.Pixels.Span[offset],
                source.Pixels.Span[offset + 1],
                source.Pixels.Span[offset + 2],
                alpha);
    }

    private static ImageDifferencePixel CalculateDifferencePixel(
        ImageDifferencePixel before,
        ImageDifferencePixel after) =>
        // Preserve coverage so opaque color differences remain visible.
        new(
            Difference(before.B, after.B),
            Difference(before.G, after.G),
            Difference(before.R, after.R),
            (byte)Math.Max(before.A, after.A));

    private static byte Difference(byte before, byte after) =>
        (byte)Math.Abs(before - after);
}

public readonly record struct ImageDifferenceSource(
    ReadOnlyMemory<byte> Pixels,
    int DecodedWidth,
    int DecodedHeight,
    int OriginalWidth,
    int OriginalHeight);

public sealed record ImageDifferenceResult(
    byte[] Pixels,
    int Width,
    int Height);

internal readonly record struct ImageDifferencePixel(byte B, byte G, byte R, byte A);
