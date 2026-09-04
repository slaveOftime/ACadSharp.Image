using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Image.Rendering;
using ACadSharp.IO;
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

    /// <summary>Renders the exporter's first page onto the surface, the way <see cref="ImageExporter.Render()"/> does.</summary>
    private static void RenderThrough(ImageExporter exporter, RecordingDrawingSurface surface)
    {
        new ImagePageRenderer(exporter.Configuration).RenderTo(surface, exporter.Pages[0]);
    }

    [Fact]
    public void PaperEntitiesAddedBeforeAViewportAreDrawnBeforeIt()
    {
        // A page built by ImageExporter from a layout whose title line sorts before the viewport must draw the line first.
        CadDocument document = new();
        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0)));
        Layout layout = new("Sheet") { PaperWidth = 200, PaperHeight = 100 };
        document.Layouts.Add(layout);
        layout.AssociatedBlock.Entities.Add(new Line(new XYZ(5, 5, 0), new XYZ(50, 5, 0)));
        layout.AssociatedBlock.Entities.Add(new Viewport { Center = new XYZ(100, 50, 0), Width = 50, Height = 50, ViewCenter = new XY(5, 0), ViewHeight = 20 });

        RecordingDrawingSurface surface = new();
        ImageExporter exporter = new();
        exporter.Add(layout);
        RenderThrough(exporter, surface);

        int line = surface.Calls.FindIndex(c => c.StartsWith("DrawLine", StringComparison.Ordinal));
        int viewport = surface.Calls.FindIndex(c => c.StartsWith("BeginViewport", StringComparison.Ordinal));
        Assert.True(line >= 0 && viewport >= 0 && line < viewport, $"expected the title line before the viewport, got line at {line}, viewport at {viewport}.");
    }

    [Fact]
    public void MalformedModelEntityDoesNotAbortViewportRendering()
    {
        CadDocument document = new();
        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0)));
        LwPolyline malformed = new();
        malformed.Vertices.Add(new LwPolyline.Vertex(new XY(5, 5)) { Bulge = 1 });
        malformed.Vertices.Add(new LwPolyline.Vertex(new XY(5, 5)));
        malformed.Vertices.Add(new LwPolyline.Vertex(new XY(9, 5)));
        document.Entities.Add(malformed);
        Layout layout = new("Sheet") { PaperWidth = 200, PaperHeight = 100 };
        document.Layouts.Add(layout);
        layout.AssociatedBlock.Entities.Add(new Viewport { Center = new XYZ(100, 50, 0), Width = 50, Height = 50, ViewCenter = new XY(5, 2), ViewHeight = 20 });

        RecordingDrawingSurface surface = new();
        ImageExporter exporter = new();
        List<NotificationEventArgs> notifications = new();
        exporter.Configuration.OnNotification += (_, e) => notifications.Add(e);
        exporter.Add(layout);

        RenderThrough(exporter, surface);

        Assert.Contains(surface.Calls, c => c.StartsWith("DrawLine", StringComparison.Ordinal));
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("bounds", StringComparison.OrdinalIgnoreCase));
    }
}
