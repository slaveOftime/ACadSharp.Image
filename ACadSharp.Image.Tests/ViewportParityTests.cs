using System.Xml.Linq;
using ACadSharp.Image.Rendering.Svg;
using ACadSharp.Objects;
using SixLabors.Fonts;
using SixLabors.ImageSharp.PixelFormats;

namespace ACadSharp.Image.Tests;

public sealed class ViewportParityTests
{
    private const string FontFamily = "DejaVu Sans";
    private static readonly XNamespace Ns = SvgDrawingSurface.Ns;

    private static ImageExporter SheetExporter(out CadDocument document)
    {
        document = SyntheticSamples.ViewportSheet();
        ImageExporter exporter = new();
        exporter.Configuration.Width = 800;
        exporter.Configuration.Height = 500;
        exporter.Configuration.SetPadding(10);
        exporter.Configuration.FontFamilyName = FontFamily;
        // Viewport-scoped layer freezing is opt-in (LayerVisibilityMode.All is the default and draws frozen layers
        // too); Screen is the mode that also honours Viewport.FrozenLayers per EntityVisibilityFilter.IsVisible.
        exporter.Configuration.LayerVisibility = LayerVisibilityMode.Screen;
        Layout sheet = document.Layouts.First(l => l.Name == "Sheet");
        exporter.Add(sheet);
        return exporter;
    }

    [Fact]
    public void SheetRoundTripKeepsTheViewport()
    {
        ImageExporter exporter = SheetExporter(out CadDocument document);
        ImagePage page = Assert.Single(exporter.Pages);

        Assert.Equal(2, page.Entities.Count); // frame line and title; the viewport itself is not an entity of the page
        Assert.Single(page.Viewports);
        Assert.Equal(2d, page.Viewports[0].ScaleFactor, 9);
        Assert.Contains(page.Viewports[0].FrozenLayers, l => l.Name == "Hidden");
        Assert.Equal(1, (int)document.Header.PaperSpaceLineTypeScaling); // PSLTSCALE default: dashes at page scale
    }

    [Fact]
    public void SheetPngMatchesBaseline()
    {
        Assert.True(SystemFonts.TryGet(FontFamily, out _), $"Font '{FontFamily}' must be installed for parity tests.");
        ImageExporter exporter = SheetExporter(out _);

        using RenderedImagePage page = Assert.IsType<RenderedImagePage>(Assert.Single(exporter.Render()));

        GoldenAssert.Png("viewport-sheet.paper.01", page.Canvas);

        // PIXEL PROBES, measured against the generated baseline (see the task report for the derivation):
        // - (400, 122): model (50, 58), just below where the frozen "Hidden" top wall would have been drawn.
        //   Background, confirming Hidden stays out of the raster too.
        // - (171, 200): a point on the left wall (model x=0, Walls layer, red), well clear of its corners.
        //   The model bottom wall (my=0) exactly coincides with the viewport's clip-rect edge (the view spans
        //   model x 0..100, y 0..60 exactly) and is fully clipped away in the raster path, so it cannot serve as
        //   the non-background probe; the left wall sits on the same boundary but along an axis unaffected by
        //   this edge case and renders reliably.
        Rgba32 white = new(255, 255, 255, 255);
        Assert.Equal(white, page.Canvas[400, 122]);
        Assert.NotEqual(white, page.Canvas[171, 200]);
    }

    [Fact]
    public void SheetSvgMatchesGoldenAndClipsTheViewport()
    {
        ImageExporter exporter = SheetExporter(out _);

        RenderedSvgPage page = Assert.IsType<RenderedSvgPage>(Assert.Single(exporter.Render(ImageExportFormat.Svg)));
        GoldenAssert.Svg("viewport-sheet.paper.01", page.Content);

        XDocument document = XDocument.Parse(page.Content);
        XElement clip = Assert.Single(document.Descendants(Ns + "clipPath"));
        Assert.Equal("clip-1", (string?)clip.Attribute("id"));
        XElement viewportGroup = Assert.Single(document.Descendants(Ns + "g"), g => (string?)g.Attribute("class") == "cad-viewport");
        Assert.Equal("url(#clip-1)", (string?)viewportGroup.Attribute("clip-path"));

        // Layer groups inside the viewport carry the clip-scoped ids; the frozen layer is absent altogether.
        List<string> layerIds = viewportGroup.Descendants(Ns + "g").Where(g => (string?)g.Attribute("class") == "cad-layer").Select(g => (string)g.Attribute("id")!).ToList();
        Assert.Contains("clip-1-layer-walls", layerIds);
        Assert.Contains("clip-1-layer-grid", layerIds);
        Assert.DoesNotContain(layerIds, id => id.Contains("hidden", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Descendants(), e => (string?)e.Attribute("data-layer") == "Hidden");

        // The grid line is dashed, at page scale (PSLTSCALE 1): 5 and 2.5 drawing units times the fit scale, in pixels.
        XElement gridLine = Assert.Single(viewportGroup.Descendants(Ns + "line"), l => (string?)l.Parent!.Attribute("data-layer") == "Grid");
        string[] dashes = ((string)gridLine.Attribute("stroke-dasharray")!).Split(' ');
        Assert.Equal(2, dashes.Length);
        Assert.Equal(2d, double.Parse(dashes[0], System.Globalization.CultureInfo.InvariantCulture) / double.Parse(dashes[1], System.Globalization.CultureInfo.InvariantCulture), 3);

        // The circle keeps its native form inside the viewport, and the page-level frame line and title sit outside it.
        Assert.Single(viewportGroup.Descendants(Ns + "circle"));
        Assert.Single(document.Descendants(Ns + "text"));
        Assert.DoesNotContain(viewportGroup.Descendants(Ns + "text"), _ => true);
    }
}
