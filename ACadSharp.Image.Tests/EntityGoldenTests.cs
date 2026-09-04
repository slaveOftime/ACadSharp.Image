using System.Xml.Linq;
using ACadSharp.Image.Rendering;
using ACadSharp.Image.Rendering.Svg;
using ACadSharp.IO;
using CSMath;
using SixLabors.Fonts;
using SixLabors.ImageSharp.PixelFormats;

namespace ACadSharp.Image.Tests;

/// <summary>
/// Renders <see cref="SyntheticSamples.EntityBlock"/> (the entities added after the feature goldens were written:
/// 3DFACE, LEADER, MLINE, WIPEOUT and a block ATTRIB) through both real backends and compares the results with
/// their baselines. Mirrors <see cref="FeatureGoldenTests"/>.
/// </summary>
public sealed class EntityGoldenTests
{
    private const string FontFamily = "DejaVu Sans";
    private static readonly XNamespace Ns = SvgDrawingSurface.Ns;

    private static ImageExporter EntityExporter()
    {
        ImageExporter exporter = new();
        exporter.Configuration.Width = 800;
        exporter.Configuration.Height = 500;
        exporter.Configuration.SetPadding(10);
        exporter.Configuration.FontFamilyName = FontFamily;
        exporter.Add(SyntheticSamples.EntityBlock());
        return exporter;
    }

    [Fact]
    public void EntityPngMatchesBaseline()
    {
        Assert.True(SystemFonts.TryGet(FontFamily, out _), $"Font '{FontFamily}' must be installed for parity tests.");
        ImageExporter exporter = EntityExporter();

        using RenderedImagePage page = Assert.IsType<RenderedImagePage>(Assert.Single(exporter.Render()));

        GoldenAssert.Png("entities.model.01", page.Canvas);

        // Raster occlusion: the WIPEOUT (world x in [70,90], y in [25,35]) masks the "Under" line (y=30, x in
        // [60,100]) where the two overlap, and leaves it visible outside that range. This is the one assertion the
        // SVG cannot make: SVG groups content by layer, so the Line/Wipeout paint order there is the layer-group
        // order, not the entities' own draw order, but the raster canvas paints them in the page's true draw order.
        // CreatePageContext(surface, ImagePage, …) resolves to PageFrame.Of(page), which is also what
        // ImagePageRenderer.ResolveFrame returns as long as HasActiveFilters() is false; EntityExporter() sets no
        // included/hidden layers and leaves LayerVisibility at its default, so the two fits coincide here. A filter
        // added to the exporter later would desync this reconstructed fit from the one the render actually used.
        ImageRenderContext context = ImageRenderContext.CreatePageContext(new RecordingDrawingSurface(), exporter.Pages[0], exporter.Configuration);
        SurfacePoint covered = context.ToSurfacePoint(new XY(80, 30));
        SurfacePoint exposed = context.ToSurfacePoint(new XY(65, 30));

        Rgba32 white = SixLabors.ImageSharp.Color.White.ToPixel<Rgba32>();
        Assert.Equal(white, DarkestPixelNear(page.Canvas, covered));
        Assert.NotEqual(white, DarkestPixelNear(page.Canvas, exposed));
    }

    /// <summary>
    /// The darkest (lowest R+G+B) pixel in a small window around <paramref name="point"/>, so the assertion survives
    /// anti-aliasing and rounding of the fitted coordinates without depending on one exact pixel.
    /// </summary>
    private static Rgba32 DarkestPixelNear(SixLabors.ImageSharp.Image<Rgba32> canvas, SurfacePoint point, int radius = 2)
    {
        int centerX = (int)Math.Round(point.X);
        int centerY = (int)Math.Round(point.Y);
        Rgba32 darkest = SixLabors.ImageSharp.Color.White.ToPixel<Rgba32>();
        int darkestLuma = int.MaxValue;
        for (int y = Math.Max(0, centerY - radius); y <= Math.Min(canvas.Height - 1, centerY + radius); y++)
        {
            for (int x = Math.Max(0, centerX - radius); x <= Math.Min(canvas.Width - 1, centerX + radius); x++)
            {
                Rgba32 pixel = canvas[x, y];
                int luma = pixel.R + pixel.G + pixel.B;
                if (luma < darkestLuma)
                {
                    darkestLuma = luma;
                    darkest = pixel;
                }
            }
        }

        return darkest;
    }

    [Fact]
    public void EntitySvgMatchesGoldenAndContainsEveryEntity()
    {
        ImageExporter exporter = EntityExporter();
        List<NotificationEventArgs> notifications = new();
        exporter.Configuration.OnNotification += (_, e) => notifications.Add(e);

        RenderedSvgPage page = Assert.IsType<RenderedSvgPage>(Assert.Single(exporter.Render(ImageExportFormat.Svg)));
        GoldenAssert.Svg("entities.model.01", page.Content);

        Assert.DoesNotContain(notifications, n => n.NotificationType is NotificationType.Warning or NotificationType.NotImplemented);
        XDocument document = XDocument.Parse(page.Content);

        // 3DFACE: one hidden edge (Third) leaves one open run of the other three edges, so it draws as a single
        // 4-point (open) polyline. Scoped to its own layer so the leaders' and mline's own polylines cannot count.
        XElement faceGroup = Assert.Single(document.Descendants(Ns + "g"), g => (string?)g.Attribute("data-layer") == "Face");
        XElement facePolyline = Assert.Single(faceGroup.Elements(Ns + "polyline"));
        // Points are serialised as "x y x y ...", two tokens per point: 4 points is exactly 8 tokens.
        Assert.Equal(8, ((string?)facePolyline.Attribute("points"))!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        // LEADER: both leaders have ArrowHeadEnabled, so each draws one arrowhead polygon; the splined leader's own
        // path is a cubic Bezier chain (a "C" command), the straight leader's a polyline.
        List<XElement> leaderPolygons = document.Descendants(Ns + "polygon").Where(p => (string?)p.Attribute("data-type") == "LEADER").ToList();
        Assert.Equal(2, leaderPolygons.Count);
        Assert.Contains(document.Descendants(Ns + "polyline"), l => (string?)l.Attribute("data-type") == "LEADER");
        List<XElement> leaderPaths = document.Descendants(Ns + "path").Where(p => (string?)p.Attribute("data-type") == "LEADER").ToList();
        Assert.Single(leaderPaths, p => ((string?)p.Attribute("d"))!.Contains('C'));

        // MLINE: a fill-on style rings the band between its two outer elements (colour 3 = green) and draws each
        // element as its own polyline (colours 1 = red, 5 = blue), scoped to the mline's own layer.
        XElement wallGroup = Assert.Single(document.Descendants(Ns + "g"), g => (string?)g.Attribute("data-layer") == "Wall");
        XElement mlineFill = Assert.Single(wallGroup.Elements(Ns + "polygon"));
        Assert.Equal("#00ff00", (string?)mlineFill.Attribute("fill"));
        List<XElement> mlineLines = wallGroup.Elements(Ns + "polyline").ToList();
        Assert.Equal(2, mlineLines.Count);
        Assert.Contains(mlineLines, l => (string?)l.Attribute("stroke") == "#ff0000");
        Assert.Contains(mlineLines, l => (string?)l.Attribute("stroke") == "#0000ff");

        // WIPEOUT: an opaque fill of the (white) background colour, on the wipeout's own layer.
        XElement coverGroup = Assert.Single(document.Descendants(Ns + "g"), g => (string?)g.Attribute("data-layer") == "Cover");
        XElement wipeoutFill = Assert.Single(coverGroup.Elements(Ns + "polygon"));
        Assert.Equal("#ffffff", (string?)wipeoutFill.Attribute("fill"));

        // ATTRIB: the constant-attribute path is exercised by EntityRenderDispatcherTests; here the value carried by
        // the INSERT's own ATTRIB, tagged with the insert's handle as its parent.
        XElement attributeText = Assert.Single(document.Descendants(Ns + "text"), t => t.Value == "A-101");
        Assert.NotNull(attributeText.Attribute("data-parent"));
    }
}
