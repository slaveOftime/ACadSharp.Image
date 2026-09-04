using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ACadSharp.Image.Rendering;
using ACadSharp.Image.Rendering.Svg;
using CSMath;
using SixLabors.Fonts;
using SixLabors.ImageSharp.PixelFormats;

namespace ACadSharp.Image.Tests;

/// <summary>
/// Renders <see cref="SyntheticSamples.FidelityBlock"/> — a multi-line attribute, a hatch on a tilted plane inside a
/// block, a leader with a custom arrowhead block, an inverted wipeout over a line, and an MLINE with a cut in both
/// elements — through both real backends and compares the results with their baselines. Mirrors
/// <see cref="EntityGoldenTests"/>.
/// </summary>
public sealed class FidelityGoldenTests
{
    private const string FontFamily = "DejaVu Sans";
    private static readonly XNamespace Ns = SvgDrawingSurface.Ns;
    private static readonly Regex PathCommand = new(@"[ML](-?[0-9]*\.?[0-9]+) (-?[0-9]*\.?[0-9]+)", RegexOptions.Compiled);

    private static ImageExporter FidelityExporter()
    {
        ImageExporter exporter = new();
        exporter.Configuration.Width = 800;
        exporter.Configuration.Height = 500;
        exporter.Configuration.SetPadding(10);
        exporter.Configuration.FontFamilyName = FontFamily;
        exporter.Add(SyntheticSamples.FidelityBlock());
        return exporter;
    }

    [Fact]
    public void FidelityPngMatchesBaseline()
    {
        Assert.True(SystemFonts.TryGet(FontFamily, out _), $"Font '{FontFamily}' must be installed for parity tests.");
        ImageExporter exporter = FidelityExporter();

        using RenderedImagePage page = Assert.IsType<RenderedImagePage>(Assert.Single(exporter.Render()));

        GoldenAssert.Png("fidelity.model.01", page.Canvas);

        // The inverted wipeout masks the whole wipeout frame (world x in [60,110], y in [10,30] — derived from its
        // own InsertPoint/UVector/VVector/Size) EXCEPT its boundary (world x in [75,95], y in [10,30] — derived the
        // same way from ClipBoundaryVertices), so the "Under" line at y = 20 survives only inside the boundary and
        // is masked outside it (but still inside the frame). This is the assertion the SVG cannot make: SVG groups
        // by layer, so the line and the mask are not in draw order there.
        ImageRenderContext context = ImageRenderContext.CreatePageContext(new RecordingDrawingSurface(), exporter.Pages[0], exporter.Configuration);
        SurfacePoint inside = context.ToSurfacePoint(new XY(85, 20));
        SurfacePoint outside = context.ToSurfacePoint(new XY(65, 20));
        Rgba32 white = new(255, 255, 255, 255);
        Assert.NotEqual(white, GoldenAssert.DarkestPixelNear(page.Canvas, inside));
        Assert.Equal(white, GoldenAssert.DarkestPixelNear(page.Canvas, outside));
    }

    [Fact]
    public void FidelitySvgMatchesGoldenAndContainsEveryFeature()
    {
        ImageExporter exporter = FidelityExporter();

        RenderedSvgPage page = Assert.IsType<RenderedSvgPage>(Assert.Single(exporter.Render(ImageExportFormat.Svg)));

        GoldenAssert.Svg("fidelity.model.01", page.Content);

        XDocument document = XDocument.Parse(page.Content);
        XElement InLayer(string layer) => document.Descendants(Ns + "g").Single(g => (string?)g.Attribute("data-layer") == layer);

        // Multi-line attribute: two lines from the embedded MText, and the single-line value nowhere in the file.
        // The ATTDEF template in the "LABEL" block is not constant, so the explode loop skips it outright; only the
        // insert's own multi-line ATTRIB (drawn from its MText, never from AttributeEntity.Value) reaches the SVG.
        XElement text = Assert.Single(InLayer("Rooms").Descendants(Ns + "text"));
        Assert.Equal("ATTRIB", (string?)text.Attribute("data-type"));
        Assert.Equal(["Room 1", "Level 2"], text.Descendants(Ns + "tspan").Select(s => s.Value).ToArray());
        Assert.DoesNotContain("FLAT", page.Content, StringComparison.Ordinal);

        // Tilted hatch inside a block: normal (0,0,-1) mirrors the OCS X axis to world (-1,0,0) (Y is unaffected),
        // so the local square x in [0,20] becomes world x in [-20,0]; the insert at (80,70,0) then shifts it to
        // world x in [60,80]. The page frame then translates every coordinate by -minX before it reaches the SVG
        // (ImagePage.ComputeFrame's Translation = -limits.Min, and CreateSvgPageContext keeps drawing units 1:1),
        // and the block's own leftmost content — the MLINE's and LEADER's vertices at world x = 10, and the
        // multi-line attribute at world x = 10 — puts minX at 10, so the emitted path spans x in [50,70].
        XElement hatch = Assert.Single(InLayer("Tilted").Descendants(Ns + "path"));
        double[] xs = PointsOf(hatch).Select(p => p.X).ToArray();
        Assert.Equal(50d, xs.Min(), 3);
        Assert.Equal(70d, xs.Max(), 3);

        // Custom arrowhead: the block's own filled solid, not the built-in triangle.
        Assert.Contains(InLayer("Leader").Descendants(Ns + "polygon"), p => (string?)p.Attribute("data-type") == "SOLID");

        // Inverted wipeout: one even-odd path with two rings, filled with the page background.
        XElement mask = Assert.Single(InLayer("Cover").Descendants(Ns + "path"));
        Assert.Equal("#ffffff", (string?)mask.Attribute("fill"));
        Assert.Equal("evenodd", (string?)mask.Attribute("fill-rule"));
        Assert.Equal(2, RingCountOf(mask));

        // Cut MLINE: two elements, each broken into two runs (a cut between 20 and 30 along a 50-unit element), so
        // four separate lines total; the style has no fill and no square caps, so nothing else adds to the count.
        Assert.Equal(4, InLayer("Wall").Descendants().Count(e => e.Name == Ns + "line" || e.Name == Ns + "polyline"));
    }

    /// <summary>
    /// Parses an SVG <c>path</c>'s <c>d</c> attribute (its <c>M</c>/<c>L</c> commands) or a <c>polygon</c>'s/
    /// <c>polyline</c>'s <c>points</c> attribute into surface points.
    /// </summary>
    private static IReadOnlyList<SurfacePoint> PointsOf(XElement element)
    {
        if (element.Name == Ns + "path")
        {
            return PathCommand.Matches((string?)element.Attribute("d") ?? string.Empty)
                .Select(m => new SurfacePoint(
                    double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
                .ToArray();
        }

        string[] tokens = ((string?)element.Attribute("points") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<SurfacePoint> points = new(tokens.Length / 2);
        for (int i = 0; i + 1 < tokens.Length; i += 2)
        {
            points.Add(new SurfacePoint(
                double.Parse(tokens[i], CultureInfo.InvariantCulture),
                double.Parse(tokens[i + 1], CultureInfo.InvariantCulture)));
        }

        return points;
    }

    /// <summary>The number of subpaths (<c>M</c> commands) in a <c>path</c> element's <c>d</c> attribute.</summary>
    private static int RingCountOf(XElement path) => ((string?)path.Attribute("d") ?? string.Empty).Count(c => c == 'M');
}
