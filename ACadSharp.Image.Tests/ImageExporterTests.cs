using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class ImageExporterTests
{
    [Fact]
    public void CloseWithoutConfiguredOutputPathThrows()
    {
        ImageExporter exporter = new();

        Assert.Throws<InvalidOperationException>(() => exporter.Close());
    }

    [Fact]
    public void ConfigurationUsesDefaultCanvasSize()
    {
        ImageConfiguration configuration = new();

        Assert.Equal(ImageConfiguration.DefaultWidth, configuration.Width);
        Assert.Equal(ImageConfiguration.DefaultHeight, configuration.Height);
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
}
