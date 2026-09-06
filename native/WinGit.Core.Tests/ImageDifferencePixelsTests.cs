using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class ImageDifferencePixelsTests
{
    [Fact]
    public void BuildKeepsOpaqueDifferencesVisibleAndIgnoresHiddenTransparentColor()
    {
        var opaqueBefore = new ImageDifferenceSource(
            new byte[] { 0, 0, 255, 255 },
            DecodedWidth: 1,
            DecodedHeight: 1,
            OriginalWidth: 1,
            OriginalHeight: 1);
        var opaqueAfter = new ImageDifferenceSource(
            new byte[] { 0, 255, 255, 255 },
            DecodedWidth: 1,
            DecodedHeight: 1,
            OriginalWidth: 1,
            OriginalHeight: 1);

        var opaqueDifference = ImageDifferencePixels.Build(opaqueBefore, opaqueAfter);

        Assert.Equal(new byte[] { 0, 255, 0, 255 }, opaqueDifference.Pixels);

        var transparentBefore = new ImageDifferenceSource(
            new byte[] { 10, 20, 30, 0 },
            DecodedWidth: 1,
            DecodedHeight: 1,
            OriginalWidth: 1,
            OriginalHeight: 1);
        var transparentAfter = new ImageDifferenceSource(
            new byte[] { 200, 150, 100, 0 },
            DecodedWidth: 1,
            DecodedHeight: 1,
            OriginalWidth: 1,
            OriginalHeight: 1);

        var transparentDifference = ImageDifferencePixels.Build(transparentBefore, transparentAfter);

        Assert.Equal(new byte[] { 0, 0, 0, 0 }, transparentDifference.Pixels);
    }
}
