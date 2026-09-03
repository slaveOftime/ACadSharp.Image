using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Image.Rendering;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

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

    [Fact]
    public void LayoutPagesKeepTheirPaperSize()
    {
        // A layout carries its own paper size, so the frame must survive rendering even with a hidden
        // entity far outside the sheet.
        Layout layout = new("sheet") { PaperWidth = 210d, PaperHeight = 297d };
        layout.AssociatedBlock.Entities.Add(new Line(new XYZ(5000, 5000, 0), new XYZ(6000, 6000, 0)) { Layer = new Layer("Far") });

        ImageExporter exporter = new();
        exporter.Configuration.HideLayer("Far");
        exporter.Add(layout);
        ImagePage page = Assert.Single(exporter.Pages);

        new ImagePageRenderer(exporter.Configuration).RenderTo(new RecordingDrawingSurface(), page);

        Assert.Equal(210d, page.Layout!.PaperWidth);
        Assert.Equal(297d, page.Layout.PaperHeight);
    }

    [Fact]
    public void UnfilteredBlockPagesAreNotReframed()
    {
        // Nothing can be hidden, so the page must be handed to the surface exactly as it was built.
        BlockRecord block = new("PLAN");
        block.Entities.Add(new Line(new XYZ(100, 50, 0), new XYZ(200, 150, 0)));

        ImageExporter exporter = new();
        exporter.Add(block);
        ImagePage page = Assert.Single(exporter.Pages);
        XY translation = page.Translation;
        double paperWidth = page.Layout!.PaperWidth;

        new ImagePageRenderer(exporter.Configuration).RenderTo(new RecordingDrawingSurface(), page);

        Assert.Equal(translation, page.Translation);
        Assert.Equal(paperWidth, page.Layout.PaperWidth);
    }
}
