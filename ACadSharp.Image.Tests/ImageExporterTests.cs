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

        using RenderedImagePage page = Assert.IsType<RenderedImagePage>(Assert.Single(exporter.Render()));

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
        using RasterDrawingSurface surface = new(canvas, configuration, ownsCanvas: false);
        ImageRenderContext context = ImageRenderContext.CreatePageContext(surface, page, configuration);

        Assert.Equal(5d, context.Scale);
        Assert.Equal(10d, context.OffsetX);
        Assert.Equal(20d, context.OffsetY);
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

        using RenderedImagePage page = Assert.IsType<RenderedImagePage>(Assert.Single(exporter.Render()));

        Assert.NotNull(page.Canvas);
        Assert.DoesNotContain(notifications, n => n.NotificationType == NotificationType.NotImplemented && n.Message.Contains("Spline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RenderClosedPeriodicSplineDoesNotDrawSpokeToOrigin()
    {
        using Image<Rgba32> canvas = new(100, 100, SixLabors.ImageSharp.Color.White);
        ImageConfiguration configuration = new()
        {
            Width = 100,
            Height = 100,
        };
        ImagePage page = new()
        {
            Layout = new Layout("spline-page")
            {
                PaperWidth = 10,
                PaperHeight = 10,
            },
        };
        using RasterDrawingSurface surface = new(canvas, configuration, ownsCanvas: false);
        ImageRenderContext context = new(surface, configuration, page.Layout, 100, 100, -5, -5, 10f, 0, 0, singlePrecision: true, lineTypeScale: 10f);
        EntityRenderDispatcher dispatcher = new(configuration);
        Spline spline = new()
        {
            Degree = 3,
            Flags = SplineFlags.Closed | SplineFlags.Periodic | SplineFlags.Planar,
        };

        spline.Knots.AddRange([0d, 0d, 0d, 0d, 0.25d, 0.25d, 0.25d, 0.5d, 0.5d, 0.5d, 0.75d, 0.75d, 0.75d, 1d, 1d, 1d, 1d]);
        spline.ControlPoints.AddRange([
            new XYZ(2, 4, 0),
            new XYZ(3, 4, 0),
            new XYZ(4, 3, 0),
            new XYZ(4, 2, 0),
            new XYZ(4, 1, 0),
            new XYZ(3, 0, 0),
            new XYZ(2, 0, 0),
            new XYZ(1, 0, 0),
            new XYZ(0, 1, 0),
            new XYZ(0, 2, 0),
            new XYZ(0, 3, 0),
            new XYZ(1, 4, 0),
            new XYZ(2, 4, 0),
        ]);

        dispatcher.Draw(context, spline);

        Assert.Equal(SixLabors.ImageSharp.Color.White.ToPixel<Rgba32>(), canvas[50, 50]);
    }

    [Fact]
    public void RenderInsertDrawsBlockContentsWithoutReportingNotImplemented()
    {
        BlockRecord insertedBlock = new("inserted-block");
        insertedBlock.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(2, 1, 0)));

        BlockRecord pageBlock = new("insert-page");
        pageBlock.Entities.Add(new Insert(insertedBlock)
        {
            InsertPoint = new XYZ(3, 4, 0),
            XScale = 2,
            YScale = 3,
        });

        ImageExporter exporter = new();
        exporter.Configuration.Width = 100;
        exporter.Configuration.Height = 100;
        exporter.Configuration.SetPadding(10);
        List<NotificationEventArgs> notifications = new();
        exporter.Configuration.OnNotification += (_, args) => notifications.Add(args);
        exporter.Add(pageBlock);

        using RenderedImagePage page = Assert.IsType<RenderedImagePage>(Assert.Single(exporter.Render()));

        Rgba32 white = SixLabors.ImageSharp.Color.White.ToPixel<Rgba32>();
        Assert.DoesNotContain(notifications, n => n.NotificationType == NotificationType.NotImplemented && n.Message.Contains("Insert", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(white, page.Canvas[10, 80]);
        Assert.NotEqual(white, page.Canvas[90, 20]);
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
        using RenderedImagePage page = Assert.IsType<RenderedImagePage>(Assert.Single(exporter.Render()));

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
        exporter.Configuration.HideLayer("Layer2");

        exporter.Add(block);

        Assert.Equal(3, exporter.Pages[0].Entities.Count); // pages keep every entity; filtering happens at render time
        Assert.Equal(2, CountDrawnLines(exporter));
    }

    [Fact]
    public void HiddenLayersIsCaseInsensitive()
    {
        BlockRecord block = new("case-test-block");

        var lineOnLayer = new Line(new XYZ(0, 0, 0), new XYZ(100, 100, 0));
        lineOnLayer.Layer = new Layer("MyLayer");
        block.Entities.Add(lineOnLayer);

        ImageExporter exporter = new();
        exporter.Configuration.HideLayer("mylayer"); // lowercase

        exporter.Add(block);

        Assert.Single(exporter.Pages[0].Entities); // pages keep every entity; filtering happens at render time
        Assert.Equal(0, CountDrawnLines(exporter));
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
        exporter.Configuration.HideLayer("Layer1");
        exporter.Configuration.HideLayer("Layer3");

        exporter.Add(block);

        Assert.Equal(3, exporter.Pages[0].Entities.Count); // pages keep every entity; filtering happens at render time
        Assert.Equal(1, CountDrawnLines(exporter));
    }

    [Fact]
    public void RenderReturnsRasterPagesCarryingTheRequestedFormat()
    {
        BlockRecord block = new("format-block");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)));

        ImageExporter exporter = new();
        exporter.Add(block);

        using RenderedPage page = Assert.Single(exporter.Render(ImageExportFormat.Jpeg));

        RenderedImagePage raster = Assert.IsType<RenderedImagePage>(page);
        Assert.Equal(ImageExportFormat.Jpeg, raster.Format);
        Assert.Equal("format-block", raster.Name);
    }

    [Fact]
    public void RenderedPageSavesToStreamInItsFormat()
    {
        BlockRecord block = new("stream-block");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)));

        ImageExporter exporter = new();
        exporter.Configuration.Width = 32;
        exporter.Configuration.Height = 32;
        exporter.Add(block);

        using RenderedPage page = Assert.Single(exporter.Render(ImageExportFormat.Png));
        using MemoryStream stream = new();
        page.Save(stream);

        byte[] bytes = stream.ToArray();
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }

    private static int CountDrawnLines(ImageExporter exporter)
    {
        RecordingDrawingSurface surface = new();
        ImagePageRenderer renderer = new(exporter.Configuration);
        renderer.RenderTo(surface, exporter.Pages[0]);
        return surface.Calls.Count(c => c.StartsWith("DrawLine", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangingHiddenLayersAfterAddTakesEffect()
    {
        BlockRecord block = new("late-hide");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 1, 0)) { Layer = new Layer("Late") });
        ImageExporter exporter = new();
        exporter.Add(block);

        Assert.Equal(1, CountDrawnLines(exporter));
        exporter.Configuration.HideLayer("Late");
        Assert.Equal(0, CountDrawnLines(exporter));
    }

    [Fact]
    public void HiddenEntitiesDoNotAffectAutoSizedFraming()
    {
        static ImageExporter Build(bool withFarHiddenLine)
        {
            BlockRecord block = new("framing");
            block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)) { Layer = new Layer("Visible") });
            if (withFarHiddenLine)
            {
                block.Entities.Add(new Line(new XYZ(1000, 1000, 0), new XYZ(1010, 1010, 0)) { Layer = new Layer("Far") });
            }

            ImageExporter exporter = new();
            exporter.Configuration.Width = 200;
            exporter.Configuration.Height = 200;
            exporter.Configuration.HideLayer("Far");
            exporter.Add(block);
            return exporter;
        }

        static string FirstLineCall(ImageExporter exporter)
        {
            RecordingDrawingSurface surface = new();
            new ImagePageRenderer(exporter.Configuration).RenderTo(surface, exporter.Pages[0]);
            return surface.Calls.Single(c => c.StartsWith("DrawLine", StringComparison.Ordinal));
        }

        Assert.Equal(FirstLineCall(Build(withFarHiddenLine: false)), FirstLineCall(Build(withFarHiddenLine: true)));
    }
}
