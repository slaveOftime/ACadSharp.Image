using ACadSharp.Header;
using ACadSharp.Image.Rendering;

namespace ACadSharp.Image.Tests;

public sealed class ImagePageRendererTests
{
    [Fact]
    public void ViewportLineTypeScaleDefaultsToPaperSpaceWithoutHeader()
    {
        Assert.Equal(8d, ImagePageRenderer.ResolveViewportLineTypeScale(null, 8d, 0.5d));
    }

    [Fact]
    public void ViewportLineTypeScaleKeepsPageScaleWhenPsltscaleIsOne()
    {
        // Raw $PSLTSCALE 1: dashes are scaled to paper space, so the page value is used unchanged.
        CadHeader header = new() { PaperSpaceLineTypeScaling = (SpaceLineTypeScaling)1 };

        Assert.Equal(8d, ImagePageRenderer.ResolveViewportLineTypeScale(header, 8d, 0.5d));
    }

    [Fact]
    public void ViewportLineTypeScaleFollowsViewportWhenPsltscaleIsZero()
    {
        // Raw $PSLTSCALE 0: dashes keep their model-space length and shrink with the viewport.
        CadHeader header = new() { PaperSpaceLineTypeScaling = (SpaceLineTypeScaling)0 };

        Assert.Equal(4d, ImagePageRenderer.ResolveViewportLineTypeScale(header, 8d, 0.5d));
    }
}
