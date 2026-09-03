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
        // - x in [300,500], y in [112,116]: the viewport's top edge is at canvas y≈113 (paper y=45 -> 10 + 45*2.2857);
        //   model y=60 maps to viewport-local y≈0.7, so an un-frozen "Hidden" line would land on rows 113-114. The
        //   window scan confirms every pixel in that band is background, i.e. that Hidden stays out of the raster
        //   (the grid line sits at y≈250 and the circle spans y≈159-342, so the window is otherwise clean too).
        // - (171, 200): a point on the left wall (model x=0, Walls layer, red), well clear of its corners.
        //   The model bottom wall (my=0) coincides with the viewport's lower edge; before the flip-origin fix the
        //   rounded flip origin shifted content down and pushed it out of the raster image, so it could not serve
        //   as a probe. With BottomY now the exact height, the bottom wall renders and is probed below.
        Rgba32 white = new(255, 255, 255, 255);
        for (int y = 112; y <= 116; y++)
        {
            for (int x = 300; x <= 500; x++)
            {
                Assert.Equal(white, page.Canvas[x, y]);
            }
        }

        Assert.NotEqual(white, page.Canvas[171, 200]);

        // The bottom wall lies exactly on the view's lower edge; before the flip-origin fix it fell outside the viewport image.
        Rgba32 bottomWall = page.Canvas[400, 387];
        Assert.True(bottomWall.R > 200 && bottomWall.G < 100 && bottomWall.B < 100, $"expected a red pixel on the bottom wall, got {bottomWall}");
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
        // Naming the fit scale directly catches a model-scale regression that a byte-identical golden alone would
        // only report as an opaque diff. Mirrors ImageRenderContext.ComputeSvgFitScale: the smaller of the
        // width-constrained and height-constrained fits (800x500 canvas, 10px padding on every side, 297x210 paper) -
        // the 210-unit paper height is the binding constraint here, not the 297-unit width.
        double widthFit = (800d - (2 * 10)) / 297d;
        double heightFit = (500d - (2 * 10)) / 210d;
        double fitScale = Math.Min(widthFit, heightFit);
        Assert.Equal(5d * fitScale, double.Parse(dashes[0], System.Globalization.CultureInfo.InvariantCulture), 3);

        // The circle keeps its native form inside the viewport, and the page-level frame line and title sit outside it.
        Assert.Single(viewportGroup.Descendants(Ns + "circle"));
        Assert.Single(document.Descendants(Ns + "text"));
        Assert.DoesNotContain(viewportGroup.Descendants(Ns + "text"), _ => true);
    }
}
