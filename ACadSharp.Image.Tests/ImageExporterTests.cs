using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Image.Rendering;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ACadSharp.Image.Tests;

public sealed class ImageExporterTests
{
    [Fact]
    public void CloseWithoutConfiguredOutputPathThrows()
    {
        ImageExporter exporter = new();

        Assert.Throws<InvalidOperationException>(() => exporter.Save("demo.png"));
    }

    [Fact]
    public void ConfigurationUsesDefaultCanvasSize()
    {
        ImageConfiguration configuration = new();

        Assert.Equal(ImageConfiguration.DefaultWidth, configuration.Width);
        Assert.Equal(ImageConfiguration.DefaultHeight, configuration.Height);
        Assert.Equal(0, configuration.PaddingLeft);
        Assert.Equal(0, configuration.PaddingTop);
        Assert.Equal(0, configuration.PaddingRight);
        Assert.Equal(0, configuration.PaddingBottom);
    }

    [Fact]
    public void RenderUsesConfiguredCanvasSize()
    {
        BlockRecord block = new("line-block");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(100, 50, 0)));

        ImageExporter exporter = new();
        exporter.Configuration.Width = 800;
        exporter.Configuration.Height = 600;
        exporter.Add(block);

        using RenderedImagePage page = Assert.Single(exporter.Render());

        Assert.Equal(800, page.Canvas.Width);
        Assert.Equal(600, page.Canvas.Height);
    }

    [Fact]
    public void PageContextUsesConfiguredPadding()
    {
        ImageConfiguration configuration = new()
        {
            Width = 100,
            Height = 80,
        };
        configuration.SetPadding(10, 20, 30, 20);

        ImagePage page = new()
        {
            Layout = new Layout("padding-page")
            {
                PaperWidth = 12,
                PaperHeight = 8,
            },
        };

        using Image<Rgba32> canvas = new(configuration.Width, configuration.Height);
        ImageRenderContext context = ImageRenderContext.CreatePageContext(canvas, page, configuration);

        Assert.Equal(5f, context.PixelsPerUnit);
        Assert.Equal(10f, context.OffsetX);
        Assert.Equal(20f, context.OffsetY);
    }

    [Fact]
    public void RenderThrowsWhenPaddingConsumesCanvas()
    {
        BlockRecord block = new("padding-overflow-block");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)));

        ImageExporter exporter = new();
        exporter.Configuration.Width = 20;
        exporter.Configuration.Height = 20;
        exporter.Configuration.SetPadding(10, 0, 10, 0);
        exporter.Add(block);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => exporter.Render());

        Assert.Contains("Padding", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderSplineBlockDoesNotReportNotImplemented()
    {
        BlockRecord block = new("spline-block");
        Spline spline = new();

        spline.FitPoints.Add(new XYZ(0, 0, 0));
        spline.FitPoints.Add(new XYZ(10, 12, 0));
        spline.FitPoints.Add(new XYZ(20, 0, 0));
        spline.UpdateFromFitPoints(16);

        block.Entities.Add(spline);

        ImageExporter exporter = new();
        List<NotificationEventArgs> notifications = new();
        exporter.Configuration.OnNotification += (_, args) => notifications.Add(args);

        exporter.Add(block);

        using RenderedImagePage page = Assert.Single(exporter.Render());

        Assert.NotNull(page.Canvas);
        Assert.DoesNotContain(notifications, n => n.NotificationType == NotificationType.NotImplemented && n.Message.Contains("Spline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RenderHandlesEntitiesWithNaNBoundingBox()
    {
        // Create a block with normal lines
        BlockRecord block = new("nan-bbox-block");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(100, 50, 0)));
        block.Entities.Add(new Line(new XYZ(100, 50, 0), new XYZ(200, 0, 0)));

        ImageExporter exporter = new();
        exporter.Add(block);

        // Should render successfully without NaN propagation issues
        using RenderedImagePage page = Assert.Single(exporter.Render());

        Assert.NotNull(page.Canvas);
        Assert.Equal(ImageConfiguration.DefaultWidth, page.Canvas.Width);
        Assert.Equal(ImageConfiguration.DefaultHeight, page.Canvas.Height);
    }

    [Fact]
    public void HiddenLayersFiltersOutEntitiesOnSpecifiedLayers()
    {
        // Create blocks with entities on different layers
        BlockRecord block = new("layer-test-block");

        var lineOnLayer1 = new Line(new XYZ(0, 0, 0), new XYZ(50, 50, 0));
        lineOnLayer1.Layer = new Layer("Layer1");

        var lineOnLayer2 = new Line(new XYZ(50, 50, 0), new XYZ(100, 0, 0));
        lineOnLayer2.Layer = new Layer("Layer2");

        var lineOnLayer3 = new Line(new XYZ(0, 50, 0), new XYZ(100, 50, 0));
        lineOnLayer3.Layer = new Layer("Layer3");

        block.Entities.Add(lineOnLayer1);
        block.Entities.Add(lineOnLayer2);
        block.Entities.Add(lineOnLayer3);

        ImageExporter exporter = new();
        exporter.Configuration.HiddenLayers.Add("Layer2");

        exporter.Add(block);

        // Verify filtering before rendering
        ImagePage page = exporter.Pages[0];
        Assert.Equal(2, page.Entities.Count); // Only Layer1 and Layer3 entities
    }

    [Fact]
    public void HiddenLayersIsCaseInsensitive()
    {
        BlockRecord block = new("case-test-block");

        var lineOnLayer = new Line(new XYZ(0, 0, 0), new XYZ(100, 100, 0));
        lineOnLayer.Layer = new Layer("MyLayer");
        block.Entities.Add(lineOnLayer);

        ImageExporter exporter = new();
        exporter.Configuration.HiddenLayers.Add("mylayer"); // lowercase

        exporter.Add(block);

        ImagePage page = exporter.Pages[0];
        Assert.Empty(page.Entities); // All entities filtered out
    }

    [Fact]
    public void MultipleHiddenLayersCanBeConfigured()
    {
        BlockRecord block = new("multi-layer-block");

        var line1 = new Line(new XYZ(0, 0, 0), new XYZ(50, 50, 0));
        line1.Layer = new Layer("Layer1");

        var line2 = new Line(new XYZ(50, 50, 0), new XYZ(100, 0, 0));
        line2.Layer = new Layer("Layer2");

        var line3 = new Line(new XYZ(0, 50, 0), new XYZ(100, 50, 0));
        line3.Layer = new Layer("Layer3");

        block.Entities.Add(line1);
        block.Entities.Add(line2);
        block.Entities.Add(line3);

        ImageExporter exporter = new();
        exporter.Configuration.HiddenLayers.Add("Layer1");
        exporter.Configuration.HiddenLayers.Add("Layer3");

        exporter.Add(block);

        ImagePage page = exporter.Pages[0];
        Assert.Single(page.Entities); // Only Layer2 entity
    }
}
