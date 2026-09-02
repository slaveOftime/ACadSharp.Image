using System.Xml.Linq;
using ACadSharp.Image.Rendering;
using ACadSharp.Image.Rendering.Svg;
using SixLabors.ImageSharp;

namespace ACadSharp.Image.Tests;

using Color = SixLabors.ImageSharp.Color;

public sealed class SvgDrawingSurfaceTests
{
    private static readonly XNamespace Ns = SvgDrawingSurface.Ns;

    private static SvgDrawingSurface CreateSurface(Action<ImageConfiguration>? configure = null)
    {
        ImageConfiguration configuration = new();
        configure?.Invoke(configuration);
        return new SvgDrawingSurface(configuration, new SurfaceRect(0, 0, 100, 50), null, null);
    }

    private static EntityRenderInfo Entity(string layer, string type = "LINE", ulong handle = 0x10, ulong? parent = null, string? block = null)
        => new(layer, type, handle, parent, block);

    private static LayerRenderInfo Layer(string name) => new(name, Color.FromRgb(255, 0, 0), 1f);

    [Fact]
    public void RootHasViewBoxAndNoSizeByDefault()
    {
        using SvgDrawingSurface surface = CreateSurface();

        XElement root = surface.ToDocument().Root!;

        Assert.Equal(Ns + "svg", root.Name);
        Assert.Equal("0 0 100 50", (string?)root.Attribute("viewBox"));
        Assert.Null(root.Attribute("width"));
        Assert.Null(root.Attribute("height"));
        XElement cadRoot = Assert.Single(root.Elements(Ns + "g"));
        Assert.Equal("cad-root", (string?)cadRoot.Attribute("class"));
        Assert.Empty(cadRoot.Attributes().Where(a => a.Name != "class"));
        XElement defaults = Assert.Single(cadRoot.Elements(Ns + "g"));
        Assert.Equal("none", (string?)defaults.Attribute("fill"));
        Assert.Contains("Arial", (string?)defaults.Attribute("font-family"));
    }

    [Fact]
    public void SizeIsEmittedWhenRequested()
    {
        ImageConfiguration configuration = new();
        using SvgDrawingSurface surface = new(configuration, new SurfaceRect(0, 0, 100, 50), 800, 400);

        XElement root = surface.ToDocument().Root!;

        Assert.Equal("800", (string?)root.Attribute("width"));
        Assert.Equal("400", (string?)root.Attribute("height"));
    }

    [Fact]
    public void BackgroundRectOnlyWhenOpaque()
    {
        using SvgDrawingSurface opaque = CreateSurface();
        using SvgDrawingSurface transparent = CreateSurface(c => c.BackgroundColor = Color.Transparent);

        Assert.Single(opaque.ToDocument().Descendants(Ns + "rect").Where(r => (string?)r.Attribute("class") == "cad-background"));
        Assert.Empty(transparent.ToDocument().Descendants(Ns + "rect"));
    }

    [Fact]
    public void EntitiesAreGroupedByEffectiveLayerInOrderOfFirstAppearance()
    {
        using SvgDrawingSurface surface = CreateSurface(c => c.Svg.IdPrefix = "p-");
        ImageStyle style = new(Color.FromRgb(255, 0, 0), 1f);

        surface.BeginEntity(Entity("Walls", handle: 0x1F3), Layer("Walls"));
        surface.DrawLine(style, new SurfacePoint(0, 0), new SurfacePoint(10, 0));
        surface.EndEntity();
        surface.BeginEntity(Entity("Doors", handle: 0x1F4), Layer("Doors"));
        surface.DrawLine(style, new SurfacePoint(0, 0), new SurfacePoint(0, 10));
        surface.EndEntity();
        surface.BeginEntity(Entity("Walls", handle: 0x1F5), Layer("Walls"));
        surface.DrawLine(style, new SurfacePoint(5, 5), new SurfacePoint(6, 6));
        surface.EndEntity();

        XElement defaults = surface.ToDocument().Root!.Element(Ns + "g")!.Element(Ns + "g")!;
        List<XElement> layers = defaults.Elements(Ns + "g").ToList();

        Assert.Equal(2, layers.Count);
        Assert.Equal("Walls", (string?)layers[0].Attribute("data-layer"));
        Assert.Equal("p-layer-walls", (string?)layers[0].Attribute("id"));
        Assert.Equal("cad-layer", (string?)layers[0].Attribute("class"));
        Assert.Equal("#ff0000", (string?)layers[0].Attribute("stroke"));
        Assert.Equal(2, layers[0].Elements(Ns + "line").Count());
        Assert.Equal("Doors", (string?)layers[1].Attribute("data-layer"));

        XElement first = layers[0].Elements(Ns + "line").First();
        Assert.Equal("1F3", (string?)first.Attribute("data-handle"));
        Assert.Equal("LINE", (string?)first.Attribute("data-type"));
        Assert.Null(first.Attribute("stroke")); // same as the layer group
        Assert.Equal("non-scaling-stroke", (string?)first.Attribute("vector-effect"));
    }

    [Fact]
    public void NestedEntityCarriesParentAndBlock()
    {
        using SvgDrawingSurface surface = CreateSurface();
        ImageStyle style = new(Color.FromRgb(255, 0, 0), 1f);

        surface.BeginEntity(Entity("Doors", "INSERT", 0xA0), Layer("Doors"));
        surface.BeginEntity(Entity("Doors", "LINE", 0xA1, 0xA0, "DOOR"), Layer("Doors"));
        surface.DrawLine(style, new SurfacePoint(0, 0), new SurfacePoint(1, 1));
        surface.EndEntity();
        surface.EndEntity();

        XElement line = Assert.Single(surface.ToDocument().Descendants(Ns + "line"));
        Assert.Equal("A0", (string?)line.Attribute("data-parent"));
        Assert.Equal("DOOR", (string?)line.Attribute("data-block"));
    }

    [Fact]
    public void ZeroHandleOmitsDataHandle()
    {
        using SvgDrawingSurface surface = CreateSurface();

        // Exploded block contents are clones with handle 0 (ACadSharp 3.7.1); no data-handle is written for them.
        surface.BeginEntity(Entity("Doors", "LINE", 0, 0xA0, "DOOR"), Layer("Doors"));
        surface.DrawLine(new ImageStyle(Color.FromRgb(255, 0, 0), 1f), new SurfacePoint(0, 0), new SurfacePoint(1, 1));
        surface.EndEntity();

        XElement line = Assert.Single(surface.ToDocument().Descendants(Ns + "line"));
        Assert.Null(line.Attribute("data-handle"));
        Assert.Equal("LINE", (string?)line.Attribute("data-type"));
        Assert.Equal("A0", (string?)line.Attribute("data-parent"));
    }

    [Fact]
    public void StyleOverridesAreWrittenOnlyWhenTheyDiffer()
    {
        using SvgDrawingSurface surface = CreateSurface(c => c.Svg.EmitEntityAttributes = false);

        surface.BeginEntity(Entity("L"), Layer("L"));
        surface.DrawLine(new ImageStyle(Color.FromRgb(0, 0, 255), 2.5f, [4f, 2f], 0.5f), new SurfacePoint(0, 0), new SurfacePoint(1, 1));
        surface.EndEntity();

        XElement line = Assert.Single(surface.ToDocument().Descendants(Ns + "line"));
        Assert.Equal("#0000ff", (string?)line.Attribute("stroke"));
        Assert.Equal("2.5", (string?)line.Attribute("stroke-width"));
        Assert.Equal("4 2", (string?)line.Attribute("stroke-dasharray"));
        Assert.Equal("0.5", (string?)line.Attribute("opacity"));
        Assert.Null(line.Attribute("data-handle"));
    }

    [Fact]
    public void PolylineAndPolygonAndFills()
    {
        using SvgDrawingSurface surface = CreateSurface();
        ImageStyle style = new(Color.Black, 1f);
        SurfacePoint[] tri = [new(0, 0), new(10, 0), new(10, 10)];

        surface.BeginEntity(Entity("L"), Layer("L"));
        surface.DrawPolyline(style, tri, closed: false);
        surface.DrawPolyline(style, tri, closed: true);
        surface.FillPolygon(style, tri);
        surface.FillPath(style, [tri, [new(2, 2), new(4, 2), new(4, 4)]]);
        surface.FillCircle(style, new SurfacePoint(5, 5), 2);
        surface.EndEntity();

        XDocument document = surface.ToDocument();
        Assert.Equal("0 0 10 0 10 10", (string?)Assert.Single(document.Descendants(Ns + "polyline")).Attribute("points"));
        List<XElement> polygons = document.Descendants(Ns + "polygon").ToList();
        Assert.Equal(2, polygons.Count);
        Assert.Null(polygons[0].Attribute("fill"));
        Assert.Equal("#000000", (string?)polygons[1].Attribute("fill"));
        Assert.Equal("none", (string?)polygons[1].Attribute("stroke"));
        XElement path = Assert.Single(document.Descendants(Ns + "path"));
        Assert.Equal("evenodd", (string?)path.Attribute("fill-rule"));
        Assert.Equal("M0 0L10 0L10 10ZM2 2L4 2L4 4Z", (string?)path.Attribute("d"));
        XElement circle = Assert.Single(document.Descendants(Ns + "circle"));
        Assert.Equal("2", (string?)circle.Attribute("r"));
        Assert.Equal("#000000", (string?)circle.Attribute("fill"));
    }

    [Fact]
    public void DrawingUnitStrokesOmitVectorEffect()
    {
        using SvgDrawingSurface surface = CreateSurface(c => c.Svg.NonScalingStroke = false);

        surface.BeginEntity(Entity("L"), Layer("L"));
        surface.DrawLine(new ImageStyle(Color.Black, 0.25f), new SurfacePoint(0, 0), new SurfacePoint(1, 1));
        surface.EndEntity();

        XElement line = Assert.Single(surface.ToDocument().Descendants(Ns + "line"));
        Assert.Null(line.Attribute("vector-effect"));
    }
}
