using System.Xml.Linq;
using ACadSharp.Image.Rendering.Svg;
using ACadSharp.IO;
using SixLabors.Fonts;

namespace ACadSharp.Image.Tests;

/// <summary>Renders a block containing every primitive the sample goldens lack and compares it with its baseline and golden.</summary>
public sealed class FeatureGoldenTests
{
    private const string FontFamily = "DejaVu Sans";
    private static readonly XNamespace Ns = SvgDrawingSurface.Ns;

    private static ImageExporter FeatureExporter()
    {
        ImageExporter exporter = new();
        exporter.Configuration.Width = 800;
        exporter.Configuration.Height = 500;
        exporter.Configuration.SetPadding(10);
        exporter.Configuration.FontFamilyName = FontFamily;
        exporter.Add(SyntheticSamples.FeatureBlock());
        return exporter;
    }

    [Fact]
    public void FeaturePngMatchesBaseline()
    {
        Assert.True(SystemFonts.TryGet(FontFamily, out _), $"Font '{FontFamily}' must be installed for parity tests.");
        ImageExporter exporter = FeatureExporter();

        using RenderedImagePage page = Assert.IsType<RenderedImagePage>(Assert.Single(exporter.Render()));

        GoldenAssert.Png("features.model.01", page.Canvas);
    }

    [Fact]
    public void FeatureSvgMatchesGoldenAndContainsEveryPrimitive()
    {
        ImageExporter exporter = FeatureExporter();
        List<NotificationEventArgs> notifications = new();
        exporter.Configuration.OnNotification += (_, e) => notifications.Add(e);

        RenderedSvgPage page = Assert.IsType<RenderedSvgPage>(Assert.Single(exporter.Render(ImageExportFormat.Svg)));
        GoldenAssert.Svg("features.model.01", page.Content);

        Assert.DoesNotContain(notifications, n => n.NotificationType is NotificationType.Warning or NotificationType.NotImplemented);
        XDocument document = XDocument.Parse(page.Content);
        List<XElement> paths = document.Descendants(Ns + "path").ToList();

        // Solid hatch: even-odd filled path. Pattern hatch: several plain lines on layer Hatch.
        Assert.Single(paths, p => (string?)p.Attribute("fill-rule") == "evenodd");
        XElement hatchGroup = Assert.Single(document.Descendants(Ns + "g"), g => (string?)g.Attribute("data-layer") == "Hatch");
        Assert.True(hatchGroup.Elements(Ns + "line").Count() >= 5);

        // Full ellipse and elliptical arc (an A command with rx 10 ry 5).
        Assert.Single(document.Descendants(Ns + "ellipse"));
        Assert.Contains(paths, p => ((string?)p.Attribute("d") ?? string.Empty).Contains("A10 5", StringComparison.Ordinal));

        // Translucent line.
        XElement translucent = Assert.Single(document.Descendants(Ns + "line"), l => (string?)l.Parent!.Attribute("data-layer") == "Trans");
        Assert.Equal("0.5", (string?)translucent.Attribute("opacity"));

        // Bulged closed polyline: a path with an arc command that closes.
        Assert.Contains(paths, p => ((string?)p.Attribute("d") ?? string.Empty) is string d && d.Contains('A') && d.EndsWith('Z') && !d.Contains("A10 5", StringComparison.Ordinal));

        // Insert: two nested lines tagged with the block, in the Doors group, the ByBlock one in the insert's colour (5 = blue).
        List<XElement> doorLines = document.Descendants(Ns + "line").Where(l => (string?)l.Attribute("data-block") == "DOOR").ToList();
        Assert.Equal(2, doorLines.Count);
        Assert.All(doorLines, l => Assert.Equal("Doors", (string?)l.Parent!.Attribute("data-layer")));
        Assert.Contains(doorLines, l => (string?)l.Attribute("stroke") == "#0000ff");
        Assert.All(doorLines, l => Assert.Null(l.Attribute("data-handle")));

        // Text: MText as two tspans, Fit text with textLength.
        List<XElement> texts = document.Descendants(Ns + "text").ToList();
        Assert.Equal(2, texts.Count);
        Assert.Contains(texts, t => t.Elements(Ns + "tspan").Select(s => s.Value).SequenceEqual(["Line1", "Line2"]));
        XElement fit = Assert.Single(texts, t => t.Value == "FIT");
        Assert.Equal("40", (string?)fit.Attribute("textLength"));
        Assert.Equal("middle", (string?)fit.Attribute("text-anchor"));
    }
}
