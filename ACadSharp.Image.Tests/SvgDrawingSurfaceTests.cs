using System.Xml.Linq;
using ACadSharp.Image.Rendering;
using ACadSharp.Image.Rendering.Svg;

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

    [Theory]
    [InlineData(null, 3)]
    [InlineData(1d, 3)]
    [InlineData(0.001d, 6)]
    [InlineData(1e-6d, 8)]
    [InlineData(1d / 25.4d, 5)]
    public void StyleDecimalsFollowTheStrokeUnit(double? strokeUnitsPerMillimeter, int expected)
    {
        using SvgDrawingSurface surface = new(new ImageConfiguration(), new SurfaceRect(0, 0, 100, 50), null, null, strokeUnitsPerMillimeter);

        Assert.Equal(expected, surface.StyleDecimals);
    }

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
        Assert.DoesNotContain(cadRoot.Attributes(), a => a.Name != "class");
        XElement defaults = Assert.Single(cadRoot.Elements(Ns + "g"));
        Assert.Equal("none", (string?)defaults.Attribute("fill"));
        Assert.Contains("Arial", (string?)defaults.Attribute("font-family"));
        Assert.DoesNotContain("<?xml", surface.ToSvgString(), StringComparison.Ordinal);
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

        Assert.Single(opaque.ToDocument().Descendants(Ns + "rect"), r => (string?)r.Attribute("class") == "cad-background");
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

    [Fact]
    public void StyleScalarsUseFixedPrecisionRegardlessOfCoordinatePrecision()
    {
        ImageConfiguration configuration = new();
        using SvgDrawingSurface surface = new(configuration, new SurfaceRect(0, 0, 20000, 10000), null, null);
        ImageStyle style = new(Color.Black, 0.25f, [0.5f, 0.25f], 0.5f);

        surface.BeginEntity(Entity("L"), Layer("L"));
        surface.DrawLine(style, new SurfacePoint(0, 0), new SurfacePoint(12345.678, 0));
        surface.EndEntity();

        XElement line = Assert.Single(surface.ToDocument().Descendants(Ns + "line"));
        Assert.Equal("0.25", (string?)line.Attribute("stroke-width"));
        Assert.Equal("0.5 0.25", (string?)line.Attribute("stroke-dasharray"));
        Assert.Equal("0.5", (string?)line.Attribute("opacity"));
        Assert.Equal("12346", (string?)line.Attribute("x2"));
    }

    [Fact]
    public void ArcIsWrittenAsPathWithFlags()
    {
        using SvgDrawingSurface surface = CreateSurface();

        // Quarter turn clockwise on screen (positive surface sweep) from angle 0.
        surface.DrawArc(new ImageStyle(Color.Black, 1f), new SurfacePoint(50, 25), 10, 10, 0, 0, Math.PI / 2);

        XElement path = Assert.Single(surface.ToDocument().Descendants(Ns + "path"));
        Assert.Equal("M60 25A10 10 0 0 1 50 35", (string?)path.Attribute("d"));
    }

    [Fact]
    public void CounterClockwiseArcHasSweepFlagZeroAndLargeArcWhenOverHalfTurn()
    {
        using SvgDrawingSurface surface = CreateSurface();

        surface.DrawArc(new ImageStyle(Color.Black, 1f), new SurfacePoint(50, 25), 10, 10, 0, 0, -1.5 * Math.PI);

        XElement path = Assert.Single(surface.ToDocument().Descendants(Ns + "path"));
        Assert.Equal("M60 25A10 10 0 1 0 50 35", (string?)path.Attribute("d"));
    }

    [Fact]
    public void FullSweepBecomesEllipseAndCirclesUseCircle()
    {
        using SvgDrawingSurface surface = CreateSurface();

        surface.DrawArc(new ImageStyle(Color.Black, 1f), new SurfacePoint(50, 25), 10, 5, Math.PI / 4, 0, 2 * Math.PI);
        surface.DrawEllipse(new ImageStyle(Color.Black, 1f), new SurfacePoint(10, 10), 3, 3, 0);

        XDocument document = surface.ToDocument();
        XElement ellipse = Assert.Single(document.Descendants(Ns + "ellipse"));
        Assert.Equal("10", (string?)ellipse.Attribute("rx"));
        Assert.Equal("5", (string?)ellipse.Attribute("ry"));
        Assert.Equal("rotate(45 50 25)", (string?)ellipse.Attribute("transform"));
        XElement circle = Assert.Single(document.Descendants(Ns + "circle"));
        Assert.Equal("3", (string?)circle.Attribute("r"));
        Assert.Null(circle.Attribute("fill"));
    }

    [Fact]
    public void BulgePolylineWritesArcCommands()
    {
        using SvgDrawingSurface surface = CreateSurface();

        surface.DrawBulgePolyline(new ImageStyle(Color.Black, 1f), [new(0, 0), new(10, 0), new(10, 10)], [1d, 0d], closed: false);

        XElement path = Assert.Single(surface.ToDocument().Descendants(Ns + "path"));
        Assert.Equal("M0 0A5 5 0 0 0 10 0L10 10", (string?)path.Attribute("d"));
    }

    [Fact]
    public void BulgePolylineDropsTheBulgeOfADroppedVertex()
    {
        using SvgDrawingSurface surface = CreateSurface();

        // The non-finite vertex goes and its bulge with it, so the arc stays on the (10,0) vertex that owns it.
        surface.DrawBulgePolyline(
            new ImageStyle(Color.Black, 1f),
            [new(0, 0), new(double.NaN, double.NaN), new(10, 0), new(20, 0)],
            [0d, 0d, 1d, 0d],
            closed: false);

        XElement path = Assert.Single(surface.ToDocument().Descendants(Ns + "path"));
        string d = (string?)path.Attribute("d") ?? string.Empty;
        Assert.Equal(1, d.Count(c => c == 'A'));
        Assert.DoesNotContain("NaN", d, StringComparison.Ordinal);
    }

    [Fact]
    public void CubicBezierWritesCCommands()
    {
        using SvgDrawingSurface surface = CreateSurface();

        surface.DrawCubicBezier(new ImageStyle(Color.Black, 1f), [new(0, 0), new(1, 2), new(3, 2), new(4, 0)], closed: true);

        XElement path = Assert.Single(surface.ToDocument().Descendants(Ns + "path"));
        Assert.Equal("M0 0C1 2 3 2 4 0Z", (string?)path.Attribute("d"));
    }

    [Fact]
    public void TextIsWrittenAsTextElement()
    {
        using SvgDrawingSurface surface = CreateSurface();
        SurfaceText run = new("Hello", new SurfacePoint(10, 20), 2.5, Math.PI / 6, SurfaceTextAnchor.Middle, SurfaceTextBaseline.Central, -1, 1, 12);

        surface.BeginEntity(Entity("Anno", "TEXT"), Layer("Anno"));
        surface.DrawText(new ImageStyle(Color.Black, 1f), run);
        surface.EndEntity();

        XElement text = Assert.Single(surface.ToDocument().Descendants(Ns + "text"));
        Assert.Equal("Hello", text.Value);
        Assert.Equal("10", (string?)text.Attribute("x"));
        Assert.Equal("20", (string?)text.Attribute("y"));
        Assert.Equal("3.33", (string?)text.Attribute("font-size"));   // 2.5 × 4/3
        Assert.Equal("middle", (string?)text.Attribute("text-anchor"));
        Assert.Equal("central", (string?)text.Attribute("dominant-baseline"));
        Assert.Equal("rotate(-30 10 20)", (string?)text.Attribute("transform"));
        Assert.Equal("12", (string?)text.Attribute("textLength"));
        Assert.Equal("spacingAndGlyphs", (string?)text.Attribute("lengthAdjust"));
        Assert.Equal("#000000", (string?)text.Attribute("fill"));
        Assert.Equal("none", (string?)text.Attribute("stroke"));
    }

    [Fact]
    public void NonUniformWidthScaleAddsAScaleTransform()
    {
        using SvgDrawingSurface surface = CreateSurface();
        SurfaceText run = new("AB", new SurfacePoint(10, 20), 4, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, -1, 1, -1, WidthScale: 2);

        surface.DrawText(new ImageStyle(Color.Black, 1f), run);

        XElement text = Assert.Single(surface.ToDocument().Descendants(Ns + "text"));
        Assert.Equal("translate(10 20) scale(2 1) translate(-10 -20)", (string?)text.Attribute("transform"));
    }

    [Fact]
    public void NonUniformWidthScaleWithRotationAppendsScaleAfterRotate()
    {
        using SvgDrawingSurface surface = CreateSurface();
        SurfaceText run = new("AB", new SurfacePoint(10, 20), 4, Math.PI / 2, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, -1, 1, -1, WidthScale: 2);

        surface.DrawText(new ImageStyle(Color.Black, 1f), run);

        XElement text = Assert.Single(surface.ToDocument().Descendants(Ns + "text"));
        string? transform = (string?)text.Attribute("transform");
        Assert.NotNull(transform);
        Assert.StartsWith("rotate(-90 10 20) ", transform, StringComparison.Ordinal);
        Assert.EndsWith("translate(10 20) scale(2 1) translate(-10 -20)", transform, StringComparison.Ordinal);
    }

    [Fact]
    public void UnitWidthScaleAddsNoTransformAttribute()
    {
        using SvgDrawingSurface surface = CreateSurface();
        SurfaceText run = new("AB", new SurfacePoint(10, 20), 4, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, -1, 1, -1, WidthScale: 1);

        surface.DrawText(new ImageStyle(Color.Black, 1f), run);

        XElement text = Assert.Single(surface.ToDocument().Descendants(Ns + "text"));
        Assert.Null(text.Attribute("transform"));
    }

    [Fact]
    public void WidthScaleUsesStylePrecisionNotCoordinatePrecision()
    {
        // A drawing whose viewBox drives the adaptive coordinate formatter to 0 decimals (or an explicit
        // Svg.Precision of 0) must not round a fractional WidthScale away to an integer, or to 0, which would
        // erase the text: the stretch factor is a dimensionless ratio, formatted at style precision instead.
        using SvgDrawingSurface surface = CreateSurface(c => c.Svg.Precision = 0);
        SurfaceText run = new("AB", new SurfacePoint(10, 20), 4, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, -1, 1, -1, WidthScale: 1.5);

        surface.DrawText(new ImageStyle(Color.Black, 1f), run);

        XElement text = Assert.Single(surface.ToDocument().Descendants(Ns + "text"));
        string? transform = (string?)text.Attribute("transform");
        Assert.NotNull(transform);
        Assert.Contains("scale(1.5 1)", transform, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiLineTextUsesTspans()
    {
        using SvgDrawingSurface surface = CreateSurface();
        SurfaceText run = new("A\nB\nC", new SurfacePoint(0, 0), 2, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, -1, 1, -1);

        surface.DrawText(new ImageStyle(Color.Black, 1f), run);

        XElement text = Assert.Single(surface.ToDocument().Descendants(Ns + "text"));
        List<XElement> spans = text.Elements(Ns + "tspan").ToList();
        Assert.Equal(3, spans.Count);
        Assert.Null(spans[0].Attribute("dy"));
        Assert.Equal("3.33", (string?)spans[1].Attribute("dy"));
        Assert.Equal("0", (string?)spans[1].Attribute("x"));
        Assert.Null(text.Attribute("dominant-baseline"));
        Assert.Null(text.Attribute("transform"));
    }

    [Fact]
    public void LayerNamesThatSanitiseAlikeGetUniqueIds()
    {
        using SvgDrawingSurface surface = CreateSurface();
        ImageStyle style = new(Color.Black, 1f);

        surface.BeginEntity(Entity("A WALL"), Layer("A WALL"));
        surface.DrawLine(style, new SurfacePoint(0, 0), new SurfacePoint(1, 1));
        surface.EndEntity();
        surface.BeginEntity(Entity("A-WALL"), Layer("A-WALL"));
        surface.DrawLine(style, new SurfacePoint(1, 1), new SurfacePoint(2, 2));
        surface.EndEntity();

        XDocument document = surface.ToDocument();
        List<string> ids = document.Descendants().Select(e => (string?)e.Attribute("id")).OfType<string>().ToList();

        // Both names sanitise to "layer-a-wall"; duplicate ids are invalid markup, so the second one is suffixed.
        Assert.Equal(new[] { "layer-a-wall", "layer-a-wall-2" }, ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ViewportWrapsContentsInClippedGroupWithOwnLayerGroups()
    {
        using SvgDrawingSurface surface = CreateSurface(c => c.Svg.IdPrefix = "x-");
        ImageStyle style = new(Color.Black, 1f);

        surface.BeginEntity(Entity("Title"), Layer("Title"));
        surface.DrawLine(style, new SurfacePoint(0, 0), new SurfacePoint(1, 1));
        surface.EndEntity();

        ViewportSurface viewport = surface.BeginViewport(new SurfaceRect(10, 5, 40, 30));
        Assert.Same(surface, viewport.Surface);
        Assert.Equal(10d, viewport.OffsetX);
        Assert.Equal(35d, viewport.BottomY);
        surface.BeginEntity(Entity("Title"), Layer("Title"));
        surface.DrawLine(style, new SurfacePoint(12, 6), new SurfacePoint(20, 20));
        surface.EndEntity();
        surface.EndViewport(viewport);

        XDocument document = surface.ToDocument();
        XElement clipPath = Assert.Single(document.Descendants(Ns + "clipPath"));
        Assert.Equal("x-clip-1", (string?)clipPath.Attribute("id"));
        Assert.Equal("userSpaceOnUse", (string?)clipPath.Attribute("clipPathUnits"));
        XElement rect = Assert.Single(clipPath.Elements(Ns + "rect"));
        Assert.Equal("10", (string?)rect.Attribute("x"));
        Assert.Equal("30", (string?)rect.Attribute("height"));

        XElement group = Assert.Single(document.Descendants(Ns + "g"), g => (string?)g.Attribute("clip-path") == "url(#x-clip-1)");
        Assert.Equal("cad-viewport", (string?)group.Attribute("class"));
        // The viewport has its own "Title" layer group, separate from the page-level one, with a distinct id.
        List<XElement> titleGroups = document.Descendants(Ns + "g").Where(g => (string?)g.Attribute("data-layer") == "Title").ToList();
        Assert.Equal(2, titleGroups.Count);
        Assert.Equal("x-layer-title", (string?)titleGroups[0].Attribute("id"));
        Assert.Equal("x-clip-1-layer-title", (string?)titleGroups[1].Attribute("id"));
        Assert.Single(group.Descendants(Ns + "line"));
        Assert.True(document.Descendants(Ns + "defs").Single().ElementsBeforeSelf().Count() == 0);

        List<string> ids = document.Descendants().Select(e => (string?)e.Attribute("id")).Where(id => id != null).ToList()!;
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ForbiddenXmlCharactersAreStrippedFromTextLayerAndBlockNames()
    {
        string bad = "A" + (char)1 + "B";
        using SvgDrawingSurface surface = CreateSurface();
        SurfaceText run = new(bad + "\nC" + (char)0x1F + "D", new SurfacePoint(0, 0), 1, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, -1, 1, 0);

        surface.BeginEntity(Entity(bad, "TEXT", block: bad), Layer(bad));
        surface.DrawText(new ImageStyle(Color.Black, 1f), run);
        surface.EndEntity();

        // Serialising is what throws on U+0001; the whole point is that it no longer does.
        string markup = surface.ToSvgString();
        XDocument document = XDocument.Parse(markup);
        XElement group = Assert.Single(document.Descendants(Ns + "g"), g => (string?)g.Attribute("class") == "cad-layer");
        Assert.Equal("AB", (string?)group.Attribute("data-layer"));
        XElement text = Assert.Single(document.Descendants(Ns + "text"));
        Assert.Equal("AB", (string?)text.Attribute("data-block"));
        Assert.Equal(["AB", "CD"], text.Elements(Ns + "tspan").Select(t => t.Value).ToArray());
    }

    [Fact]
    public void XmlTextCleanKeepsLegalWhitespaceAndSurrogatePairs()
    {
        string legal = "tab\t nl\n cr\r emoji\U0001F600";
        Assert.Same(legal, SvgXmlText.Clean(legal));
        Assert.Equal("ab", SvgXmlText.Clean("a" + (char)0xFFFE + "b"));
        Assert.Equal("ab", SvgXmlText.Clean("a\uD83Db"));
    }

    [Fact]
    public void TranslucentBackgroundKeepsItsAlphaAsFillOpacity()
    {
        using SvgDrawingSurface surface = CreateSurface(c => c.BackgroundColor = Color.FromRgba(0, 0, 0, 128));

        XElement rect = Assert.Single(surface.ToDocument().Descendants(Ns + "rect"));
        Assert.Equal("#000000", (string?)rect.Attribute("fill"));
        Assert.Equal("0.502", (string?)rect.Attribute("fill-opacity"));

        using SvgDrawingSurface opaque = CreateSurface();
        Assert.Null(Assert.Single(opaque.ToDocument().Descendants(Ns + "rect")).Attribute("fill-opacity"));
    }

    [Fact]
    public void IdPrefixIsRestrictedToIdSafeCharacters()
    {
        using SvgDrawingSurface surface = CreateSurface(c => c.Svg.IdPrefix = "drawing one/\"2\" ");
        ViewportSurface viewport = surface.BeginViewport(new SurfaceRect(0, 0, 10, 10));
        surface.BeginEntity(Entity("Walls"), Layer("Walls"));
        surface.DrawLine(new ImageStyle(Color.Black, 1f), new SurfacePoint(0, 0), new SurfacePoint(1, 1));
        surface.EndEntity();
        surface.EndViewport(viewport);

        XDocument document = surface.ToDocument();
        XElement clip = Assert.Single(document.Descendants(Ns + "clipPath"));
        Assert.Equal("drawing-one-2-clip-1", (string?)clip.Attribute("id"));
        XElement group = Assert.Single(document.Descendants(Ns + "g"), g => (string?)g.Attribute("class") == "cad-viewport");
        Assert.Equal("url(#drawing-one-2-clip-1)", (string?)group.Attribute("clip-path"));
        Assert.Equal("drawing-one-2-clip-1-layer-walls", (string?)Assert.Single(document.Descendants(Ns + "g"), g => (string?)g.Attribute("class") == "cad-layer").Attribute("id"));

        Assert.Equal("Plan_1-", SvgIdSanitizer.SanitizePrefix("Plan_1-"));
        Assert.Equal(string.Empty, SvgIdSanitizer.SanitizePrefix(""));
    }

    [Fact]
    public void FontSizeIsTheEmForTheCadCapHeight()
    {
        using SvgDrawingSurface surface = CreateSurface();
        surface.BeginEntity(Entity("Anno", "TEXT"), Layer("Anno"));
        surface.DrawText(new ImageStyle(Color.Black, 1f), new SurfaceText("H", new SurfacePoint(0, 0), 3, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, -1, 1, 0));
        surface.EndEntity();

        XElement text = Assert.Single(surface.ToDocument().Descendants(Ns + "text"));
        Assert.Equal("4", (string?)text.Attribute("font-size"));   // 3 × 4/3
    }

    [Fact]
    public void MultiLineBlocksAreAnchoredByTheirBaseline()
    {
        using SvgDrawingSurface surface = CreateSurface();
        ImageStyle style = new(Color.Black, 1f);
        surface.BeginEntity(Entity("Anno", "MTEXT"), Layer("Anno"));
        surface.DrawText(style, new SurfaceText("a\nb\nc", new SurfacePoint(10, 50), 3, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Central, -1, 1, 0));
        surface.DrawText(style, new SurfaceText("a\nb", new SurfacePoint(10, 50), 3, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, -1, 1, 0));
        surface.DrawText(style, new SurfaceText("a\nb", new SurfacePoint(10, 50), 3, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Hanging, -1, 1, 0));
        surface.EndEntity();

        List<XElement> texts = surface.ToDocument().Descendants(Ns + "text").ToList();
        // Line height is 5/3 of the cap height: 5. Central: three lines, first line one line height above the origin.
        Assert.Equal("45", (string?)texts[0].Attribute("y"));
        // Alphabetic (bottom): two lines, first line one line height above.
        Assert.Equal("45", (string?)texts[1].Attribute("y"));
        // Hanging (top): first line at the origin.
        Assert.Equal("50", (string?)texts[2].Attribute("y"));
        Assert.Equal(["a", "b", "c"], texts[0].Elements(Ns + "tspan").Select(t => t.Value).ToArray());
        Assert.Equal("5", (string?)texts[0].Elements(Ns + "tspan").ElementAt(1).Attribute("dy"));
    }

    [Fact]
    public void TextIsWrappedAtTheWrappingWidth()
    {
        using SvgDrawingSurface surface = CreateSurface(c => c.FontFamilyName = "DejaVu Sans");
        surface.BeginEntity(Entity("Anno", "MTEXT"), Layer("Anno"));
        // Width 14 at cap height 3 (em 4) fits roughly five to six characters of DejaVu Sans per line.
        surface.DrawText(new ImageStyle(Color.Black, 1f), new SurfaceText("alpha beta gamma delta", new SurfacePoint(0, 0), 3, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Hanging, 14, 1, 0));
        surface.EndEntity();

        XElement text = Assert.Single(surface.ToDocument().Descendants(Ns + "text"));
        string[] lines = text.Elements(Ns + "tspan").Select(t => t.Value).ToArray();
        Assert.Equal(["alpha", "beta", "gamma", "delta"], lines);
    }

    [Fact]
    public void TextElementsPreserveRepeatedWhitespace()
    {
        // Asserted against the serialized string, not ToDocument()'s in-memory tree: XDocument.Save's
        // pretty-printing is what can turn xml:space="preserve" into drawn indentation (see the multi-line
        // test below), and only re-parsing the actual output can catch that.
        using SvgDrawingSurface surface = CreateSurface();
        surface.BeginEntity(Entity("Anno", "TEXT"), Layer("Anno"));
        surface.DrawText(new ImageStyle(Color.Black, 1f), new SurfaceText("A  B", new SurfacePoint(0, 0), 3, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, -1, 1, 0));
        surface.EndEntity();

        XDocument document = XDocument.Parse(surface.ToSvgString(), LoadOptions.PreserveWhitespace);
        XElement text = Assert.Single(document.Descendants(Ns + "text"));
        Assert.Equal("preserve", (string?)text.Attribute(XNamespace.Xml + "space"));
        Assert.Equal("A  B", text.Value);
    }

    [Fact]
    public void MultiLineTextCarriesNoXmlSpaceOfItsOwnAndEachTspanPreservesItsRun()
    {
        // XDocument.Save's default pretty-printing indents each <tspan> with a newline plus spaces; those
        // indentation characters end up as direct-child text nodes of <text> regardless of any attribute (an
        // XmlWriter formatting fact, verified against the serialized string below), so they cannot be asserted
        // away without disabling indentation for the whole document. What actually matters is which xml:space
        // value governs them: xml:space is inherited, so if <text> carried "preserve" those indentation nodes
        // would inherit it and be drawn (SVG 1.1 assigns the whitespace after a </tspan> to the *preceding* text
        // chunk, visibly shifting a middle-anchored line — this was the bug). <text> must therefore carry no
        // xml:space of its own here, leaving its direct-child whitespace nodes under the ordinary default
        // (collapsing) rule, while each <tspan> carries its own explicit xml:space="preserve" so the repeated
        // spaces *inside* its line survive.
        using SvgDrawingSurface surface = CreateSurface();
        ImageStyle style = new(Color.Black, 1f);
        surface.BeginEntity(Entity("Anno", "MTEXT"), Layer("Anno"));
        surface.DrawText(style, new SurfaceText("a  b\nc  d", new SurfacePoint(10, 50), 3, 0, SurfaceTextAnchor.Middle, SurfaceTextBaseline.Alphabetic, -1, 1, 0));
        surface.EndEntity();

        XDocument document = XDocument.Parse(surface.ToSvgString(), LoadOptions.PreserveWhitespace);
        XElement text = Assert.Single(document.Descendants(Ns + "text"));

        Assert.Null(text.Attribute(XNamespace.Xml + "space"));
        // Any direct text child of <text> here is pretty-print indentation, never drawable glyph content: the
        // wrapper never hands DrawText a paragraph containing only whitespace.
        Assert.All(text.Nodes().OfType<XText>(), node => Assert.True(string.IsNullOrWhiteSpace(node.Value)));

        List<XElement> tspans = text.Elements(Ns + "tspan").ToList();
        Assert.Equal(2, tspans.Count);
        foreach (XElement tspan in tspans)
        {
            Assert.Equal("preserve", (string?)tspan.Attribute(XNamespace.Xml + "space"));
            Assert.Empty(tspan.Elements());
        }

        Assert.Equal("a  b", tspans[0].Value);
        Assert.Equal("c  d", tspans[1].Value);
    }

    [Fact]
    public void WrapKeepsExplicitBreaksAndLongWords()
    {
        IReadOnlyList<string> lines = SvgTextLayout.Wrap("one two\nthree fourfivesixseven", 8, 4, "DejaVu Sans");

        Assert.Equal("one", lines[0]);
        Assert.Equal("two", lines[1]);
        Assert.Equal("three", lines[2]);
        Assert.Equal("fourfivesixseven", lines[3]);   // a single word wider than the width stays on its own line
        Assert.Equal(["x"], SvgTextLayout.Wrap("x", -1, 4, "DejaVu Sans"));
    }
}
