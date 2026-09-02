using System.Xml.Linq;
using ACadSharp.Entities;
using ACadSharp.Image.Rendering.Svg;
using ACadSharp.Tables;
using CSMath;
using SixLabors.ImageSharp.PixelFormats;

namespace ACadSharp.Image.Tests;

public sealed class SvgExportTests
{
    private static readonly XNamespace Ns = SvgDrawingSurface.Ns;

    private static BlockRecord SimpleBlock()
    {
        BlockRecord block = new("svg-block");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(100, 0, 0)) { Layer = new Layer("Walls") });
        block.Entities.Add(new Circle { Center = new XYZ(50, 25, 0), Radius = 10, Layer = new Layer("Doors") });
        block.Entities.Add(new TextEntity { Value = "Room", InsertPoint = new XYZ(10, 40, 0), Height = 5, Layer = new Layer("Anno") });
        return block;
    }

    [Fact]
    public void RenderSvgProducesSvgPage()
    {
        ImageExporter exporter = new();
        exporter.Add(SimpleBlock());

        using RenderedPage page = Assert.Single(exporter.Render(ImageExportFormat.Svg));
        RenderedSvgPage svg = Assert.IsType<RenderedSvgPage>(page);
        XDocument document = XDocument.Parse(svg.Content);
        XElement root = document.Root!;

        Assert.Equal(Ns + "svg", root.Name);
        Assert.Equal(ImageExportFormat.Svg, svg.Format);
        // Extents 100 x 50 (the text bounding box may enlarge the height slightly).
        string[] viewBox = ((string)root.Attribute("viewBox")!).Split(' ');
        Assert.Equal("0", viewBox[0]);
        Assert.Equal("0", viewBox[1]);
        Assert.Equal("100", viewBox[2]);
        Assert.Null(root.Attribute("width"));
        Assert.Equal(3, document.Descendants(Ns + "g").Count(g => g.Attribute("data-layer") != null));
        Assert.Single(document.Descendants(Ns + "circle"));
        Assert.Equal("Room", Assert.Single(document.Descendants(Ns + "text")).Value);
    }

    [Fact]
    public void YAxisIsFlipped()
    {
        BlockRecord block = new("flip");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)));
        ImageExporter exporter = new();
        exporter.Add(block);

        using RenderedPage page = Assert.Single(exporter.Render(ImageExportFormat.Svg));
        XDocument document = XDocument.Parse(((RenderedSvgPage)page).Content);
        XElement line = Assert.Single(document.Descendants(Ns + "line"));

        // Drawing (0,0) is the bottom-left, so it lands at SVG y = 10; drawing (10,10) lands at y = 0.
        Assert.Equal("0", (string?)line.Attribute("x1"));
        Assert.Equal("10", (string?)line.Attribute("y1"));
        Assert.Equal("10", (string?)line.Attribute("x2"));
        Assert.Equal("0", (string?)line.Attribute("y2"));
    }

    [Fact]
    public void PaddingExpandsViewBoxAndSizeIsOptional()
    {
        BlockRecord block = new("padded");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(100, 50, 0)));
        ImageExporter exporter = new();
        exporter.Configuration.Width = 1000;
        exporter.Configuration.Height = 600;
        exporter.Configuration.SetPadding(100, 50, 100, 50);
        exporter.Configuration.Svg.EmitSize = true;
        exporter.Add(block);

        using RenderedPage page = Assert.Single(exporter.Render(ImageExportFormat.Svg));
        XElement root = XDocument.Parse(((RenderedSvgPage)page).Content).Root!;

        // Drawable 800x500 for a 100x50 page -> fit 8 px/unit -> padding 12.5 units horizontally, 6.25 vertically.
        Assert.Equal("-12.5 -6.25 125 62.5", (string?)root.Attribute("viewBox"));
        Assert.Equal("1000", (string?)root.Attribute("width"));
        Assert.Equal("600", (string?)root.Attribute("height"));
    }

    [Fact]
    public void StrokeWidthsArePixelsByDefaultAndDrawingUnitsWhenScaling()
    {
        BlockRecord block = new("weights");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(100, 0, 0)) { LineWeight = ACadSharp.LineWeightType.W50 });

        ImageExporter pixels = new();
        pixels.Add(block);
        using RenderedPage pixelPage = Assert.Single(pixels.Render(ImageExportFormat.Svg));
        XElement pixelLine = Assert.Single(XDocument.Parse(((RenderedSvgPage)pixelPage).Content).Descendants(Ns + "line"));
        Assert.Equal("non-scaling-stroke", (string?)pixelLine.Attribute("vector-effect"));
        // 0.50 mm at 96 dpi = 1.8897 px, written by the fixed 3-decimal style formatter.
        Assert.Equal("1.89", (string?)pixelLine.Attribute("stroke-width"));

        ImageExporter units = new();
        units.Configuration.Svg.NonScalingStroke = false;
        units.Add(block);
        using RenderedPage unitPage = Assert.Single(units.Render(ImageExportFormat.Svg));
        XElement unitLine = Assert.Single(XDocument.Parse(((RenderedSvgPage)unitPage).Content).Descendants(Ns + "line"));
        Assert.Null(unitLine.Attribute("vector-effect"));
        Assert.Equal("0.5", (string?)unitLine.Attribute("stroke-width")); // 0.50 mm, unitless drawing treated as millimetres
    }

    [Fact]
    public void SaveWritesSvgFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"acadsharp-{Guid.NewGuid():N}.svg");
        try
        {
            ImageExporter exporter = new();
            exporter.Add(SimpleBlock());
            exporter.Save(path, ImageExportFormat.Svg);

            string content = File.ReadAllText(path);
            Assert.Contains("<svg", content, StringComparison.Ordinal);
            Assert.Contains("data-layer=\"Walls\"", content, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RenderedImagePageRejectsTheSvgFormat()
    {
        using RenderedImagePage page = new("x", new SixLabors.ImageSharp.Image<Rgba32>(1, 1), ImageExportFormat.Svg);

        Assert.Throws<NotSupportedException>(() => page.Save(new MemoryStream()));
    }
}
