using ACadSharp.Image.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ImageColor = SixLabors.ImageSharp.Color;

namespace ACadSharp.Image.Tests;

public sealed class RasterDrawingSurfaceTests
{
    private static readonly Rgba32 White = ImageColor.White.ToPixel<Rgba32>();

    private static readonly Rgba32 Black = ImageColor.Black.ToPixel<Rgba32>();

    [Fact]
    public void DrawLinePaintsPixelsAlongTheLine()
    {
        using Image<Rgba32> canvas = new(20, 20, ImageColor.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);

        surface.DrawLine(new ImageStyle(ImageColor.Black, 2f), new SurfacePoint(2, 10), new SurfacePoint(18, 10));

        Assert.Equal(Black, canvas[10, 10]);
        Assert.Equal(White, canvas[10, 2]);
    }

    [Fact]
    public void DrawPolylineClosedConnectsLastPointToFirst()
    {
        using Image<Rgba32> canvas = new(20, 20, ImageColor.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);
        SurfacePoint[] points = [new(2, 2), new(18, 2), new(18, 18)];

        surface.DrawPolyline(new ImageStyle(ImageColor.Black, 2f), points, closed: true);

        // Closing edge runs from (18,18) back to (2,2): the midpoint (10,10) must be painted.
        Assert.Equal(Black, canvas[10, 10]);
    }

    [Fact]
    public void FillPathUsesEvenOddRuleForHoles()
    {
        using Image<Rgba32> canvas = new(40, 40, ImageColor.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);
        SurfacePoint[] outer = [new(2, 2), new(38, 2), new(38, 38), new(2, 38)];
        SurfacePoint[] hole = [new(15, 15), new(25, 15), new(25, 25), new(15, 25)];

        surface.FillPath(new ImageStyle(ImageColor.Black, 1f), [outer, hole]);

        Assert.Equal(Black, canvas[5, 5]);
        Assert.Equal(White, canvas[20, 20]);
    }

    [Fact]
    public void OpacityBlendsWithBackground()
    {
        using Image<Rgba32> canvas = new(10, 10, ImageColor.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);

        surface.FillPolygon(new ImageStyle(ImageColor.Black, 1f, null, 0.5f), [new(0, 0), new(10, 0), new(10, 10), new(0, 10)]);

        Rgba32 pixel = canvas[5, 5];
        Assert.InRange(pixel.R, 120, 135);
        Assert.Equal(pixel.R, pixel.G);
        Assert.Equal(255, pixel.A);
    }

    [Fact]
    public void DashPatternLeavesGaps()
    {
        using Image<Rgba32> canvas = new(60, 10, ImageColor.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);

        // 10 px dash, 10 px gap.
        surface.DrawLine(new ImageStyle(ImageColor.Black, 2f, [10f, 10f], 1f), new SurfacePoint(0, 5), new SurfacePoint(60, 5));

        Assert.Equal(Black, canvas[5, 5]);
        Assert.Equal(White, canvas[15, 5]);
        Assert.Equal(Black, canvas[25, 5]);
    }

    [Fact]
    public void ViewportDrawsIntoChildAndCompositesAtBounds()
    {
        using Image<Rgba32> canvas = new(40, 40, ImageColor.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);

        ViewportSurface viewport = surface.BeginViewport(new SurfaceRect(20, 20, 10, 10));
        Assert.Equal(0d, viewport.OffsetX);
        Assert.Equal(10d, viewport.BottomY);

        // Fill the whole child; only the 10x10 region at (20,20) may change on the page.
        viewport.Surface.FillPolygon(new ImageStyle(ImageColor.Black, 1f), [new(0, 0), new(10, 0), new(10, 10), new(0, 10)]);
        surface.EndViewport(viewport);

        Assert.Equal(Black, canvas[25, 25]);
        Assert.Equal(White, canvas[15, 15]);
        Assert.Equal(White, canvas[35, 35]);
    }

    [Fact]
    public void ArcPointsStartAndEndOnTheArc()
    {
        IReadOnlyList<SurfacePoint> points = CurveTessellation.ArcPoints(new SurfacePoint(0, 0), 10, 10, 0, 0, Math.PI / 2, 8);

        Assert.Equal(9, points.Count);
        Assert.Equal(10, points[0].X, 6);
        Assert.Equal(0, points[0].Y, 6);
        Assert.Equal(0, points[^1].X, 6);
        Assert.Equal(10, points[^1].Y, 6);
    }

    [Fact]
    public void BulgeArcOfOneIsASemicircle()
    {
        CurveTessellation.BulgeArc(new SurfacePoint(0, 0), new SurfacePoint(10, 0), 1d, out SurfacePoint center, out double radius, out double startAngle, out double sweep);

        Assert.Equal(5, center.X, 6);
        Assert.Equal(0, center.Y, 6);
        Assert.Equal(5, radius, 6);
        Assert.Equal(-Math.PI, sweep, 6);
        Assert.Equal(Math.PI, Math.Abs(startAngle), 6);
    }

    [Fact]
    public void PositiveBulgeBendsTowardPositiveYInSurfaceSpace()
    {
        // Drawing-space CCW arc from (0,0) to (10,0) passes below the chord; below is +Y on a Y-down surface.
        CurveTessellation.BulgeArc(new SurfacePoint(0, 0), new SurfacePoint(10, 0), 0.5d, out SurfacePoint center, out double radius, out double startAngle, out double sweep);

        Assert.Equal(5, center.X, 6);
        Assert.Equal(-3.75, center.Y, 6);
        Assert.Equal(6.25, radius, 6);
        Assert.True(sweep < 0);

        IReadOnlyList<SurfacePoint> points = CurveTessellation.ArcPoints(center, radius, radius, 0, startAngle, sweep, 2);
        Assert.Equal(5, points[1].X, 6);
        Assert.Equal(2.5, points[1].Y, 6);
    }

    [Fact]
    public void ViewportFlipOriginIsTheExactHeightNotTheRoundedImageHeight()
    {
        using Image<Rgba32> canvas = new(20, 20, ImageColor.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);

        // A viewport 9.3 px tall gets a 10-row image; its content must still be placed against 9.3, not 10.
        ViewportSurface viewport = surface.BeginViewport(new SurfaceRect(0, 0, 10, 9.3));

        Assert.Equal(9.3, viewport.BottomY, 9);
        Assert.Equal(0d, viewport.OffsetX);

        // A one-pixel line drawn on the viewport's bottom edge (surface y = BottomY) must reach the page.
        viewport.Surface.DrawLine(new ImageStyle(ImageColor.Red, 1f), new SurfacePoint(0, viewport.BottomY), new SurfacePoint(10, viewport.BottomY));
        surface.EndViewport(viewport);

        Assert.Contains(Enumerable.Range(0, 10).Select(x => canvas[x, 9]), p => p.R > p.G);
    }

    [Fact]
    public void ViewportFractionalPositionIsCarriedIntoTheChildOffsets()
    {
        using Image<Rgba32> canvas = new(20, 20, ImageColor.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);

        // Viewport at (3.6, 2.4): the image is pasted at (3, 2) and the child draws 0.6 / 0.4 px further in.
        ViewportSurface viewport = surface.BeginViewport(new SurfaceRect(3.6, 2.4, 10, 9.3));

        Assert.Equal(0.6, viewport.OffsetX, 9);
        Assert.Equal(2.4 - 2 + 9.3, viewport.BottomY, 9);

        // A vertical line on the child's own X offset must land in page column 3 (covering x 3.1..4.1), not column 4 alone.
        viewport.Surface.DrawLine(new ImageStyle(ImageColor.Red, 1f), new SurfacePoint(viewport.OffsetX, 0), new SurfacePoint(viewport.OffsetX, viewport.BottomY));
        surface.EndViewport(viewport);

        Assert.True(canvas[3, 6].R > canvas[3, 6].G, $"column 3 should carry most of the line, got {canvas[3, 6]}");
        Assert.Equal(new Rgba32(255, 255, 255, 255), canvas[2, 6]);
        Assert.Equal(new Rgba32(255, 255, 255, 255), canvas[6, 6]);
    }

    [Fact]
    public void TextLinesAreSpacedAtFiveThirdsOfTheTextHeight()
    {
        // Capitals only, so each line inks exactly one band and the band starts track the baselines.
        using Image<Rgba32> singleLine = DrawnText("H", SurfaceTextBaseline.Hanging, 1d, 0d);
        using Image<Rgba32> twoLines = DrawnText("H\nH", SurfaceTextBaseline.Hanging, 1d, 0d);
        int[] one = InkBandStarts(singleLine);
        int[] two = InkBandStarts(twoLines);

        int first = Assert.Single(one);
        Assert.Equal(2, two.Length);

        // The added leading must go between the lines, not above the first one: line 1 stays exactly where a
        // single-line run puts it.
        Assert.Equal(first, two[0]);

        double distance = two[1] - two[0];
        Assert.True(Math.Abs(distance - 50d / 3d) <= 1d, $"expected the lines about {50d / 3d:F1} px apart (5/3 of the text height), got {distance}.");
    }

    [Fact]
    public void AlphabeticTextAnchorsItsLastLine()
    {
        // Alphabetic stands the block on the anchor, so the closing line must not move when a line is added above it.
        using Image<Rgba32> singleLine = DrawnText("H", SurfaceTextBaseline.Alphabetic, 1d, 0d);
        using Image<Rgba32> twoLines = DrawnText("H\nH", SurfaceTextBaseline.Alphabetic, 1d, 0d);

        int[] one = InkBandStarts(singleLine);
        int[] two = InkBandStarts(twoLines);

        Assert.Single(one);
        Assert.Equal(2, two.Length);
        Assert.Equal(one[0], two[^1]);

        double distance = two[1] - two[0];
        Assert.True(Math.Abs(distance - 50d / 3d) <= 1d, $"expected the lines about {50d / 3d:F1} px apart, got {distance}.");
    }

    [Fact]
    public void RotatedTextAnchorsItsFirstLineAlongItsOwnUpAxis()
    {
        // A quarter turn puts the text's up axis along the page's x axis: the spacing correction must travel with it,
        // so the first line of a rotated block still starts where a rotated single line does.
        using Image<Rgba32> singleLine = DrawnText("H", SurfaceTextBaseline.Hanging, 1d, Math.PI / 2d);
        using Image<Rgba32> twoLines = DrawnText("H\nH", SurfaceTextBaseline.Hanging, 1d, Math.PI / 2d);

        int[] one = InkColumnStarts(singleLine);
        int[] two = InkColumnStarts(twoLines);

        Assert.Single(one);
        Assert.Equal(2, two.Length);

        // The transform rotates by -90 degrees, which sends the text's downward line advance towards +x, so the block
        // grows rightwards and its first line is the leftmost band.
        Assert.Equal(one[0], two[0]);
        double distance = two[1] - two[0];
        Assert.True(Math.Abs(distance - 50d / 3d) <= 1d, $"expected the rotated lines about {50d / 3d:F1} px apart, got {distance}.");
    }

    /// <summary>Draws one text run of height 10 at the canvas centre and returns the canvas.</summary>
    private static Image<Rgba32> DrawnText(string value, SurfaceTextBaseline baseline, double lineSpacingFactor, double rotation, float dpi = 96f)
    {
        Image<Rgba32> canvas = new(200, 200, ImageColor.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration { Dpi = dpi }, ownsCanvas: false);
        surface.DrawText(
            new ImageStyle(ImageColor.Black, 1f),
            new SurfaceText(value, new SurfacePoint(100, 100), 10, rotation, SurfaceTextAnchor.Start, baseline, 0, lineSpacingFactor, 0));
        return canvas;
    }

    /// <summary>The first row of every run of inked rows, a row counting as inked when it holds a pixel darker than mid grey.</summary>
    private static int[] InkBandStarts(Image<Rgba32> canvas)
    {
        List<int> starts = new();
        bool previousInked = false;
        for (int y = 0; y < canvas.Height; y++)
        {
            bool inked = false;
            for (int x = 0; x < canvas.Width && !inked; x++)
            {
                inked = canvas[x, y].R < 128;
            }

            if (inked && !previousInked)
            {
                starts.Add(y);
            }

            previousInked = inked;
        }

        return starts.ToArray();
    }

    /// <summary>The first column of every run of inked columns, the transpose of <see cref="InkBandStarts"/>.</summary>
    private static int[] InkColumnStarts(Image<Rgba32> canvas)
    {
        List<int> starts = new();
        bool previousInked = false;
        for (int x = 0; x < canvas.Width; x++)
        {
            bool inked = false;
            for (int y = 0; y < canvas.Height && !inked; y++)
            {
                inked = canvas[x, y].R < 128;
            }

            if (inked && !previousInked)
            {
                starts.Add(x);
            }

            previousInked = inked;
        }

        return starts.ToArray();
    }

    [Fact]
    public void FillsDropNonFiniteGeometryInsteadOfThrowing()
    {
        using Image<Rgba32> canvas = new(20, 20, ImageColor.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);
        ImageStyle style = new(ImageColor.Black, 1f);
        SurfacePoint[] ring = [new(2, 2), new(18, 2), new(double.PositiveInfinity, 18)];

        // ImageSharp's scan-line fill throws ArithmeticException on a non-finite vertex, which the dispatcher's
        // catch filter would not have caught: the surface drops those points the way the SVG backend does.
        surface.FillPolygon(style, [new(2, 2), new(double.NaN, 2), new(18, 18), new(2, 18)]);
        surface.FillPath(style, [ring]);
        surface.FillCircle(style, new SurfacePoint(10, double.NaN), 5);

        // The polygon keeps its three finite corners and is still filled; the two-point ring and the circle vanish.
        Assert.Equal(Black, canvas[5, 15]);
        Assert.Equal(White, canvas[10, 3]);
    }

    [Fact]
    public void TextSizeDoesNotDependOnTheConfiguredDpi()
    {
        using Image<Rgba32> rendered96 = DrawnText("Hg", SurfaceTextBaseline.Alphabetic, 1d, 0d, dpi: 96f);
        using Image<Rgba32> rendered300 = DrawnText("Hg", SurfaceTextBaseline.Alphabetic, 1d, 0d, dpi: 300f);
        int[] at96 = InkColumnBounds(rendered96);
        int[] at300 = InkColumnBounds(rendered300);

        Assert.True(Math.Abs(at96[0] - at300[0]) <= 1 && Math.Abs(at96[1] - at300[1]) <= 1, $"ink columns {at96[0]}..{at96[1]} at 96 dpi but {at300[0]}..{at300[1]} at 300 dpi.");
    }

    [Fact]
    public void HangingTextStaysOnItsAnchorForAnyLineSpacingFactor()
    {
        using Image<Rgba32> single = DrawnText("H", SurfaceTextBaseline.Hanging, 1d, 0d);
        using Image<Rgba32> spaced = DrawnText("H\nH", SurfaceTextBaseline.Hanging, 2d, 0d);

        int[] one = InkBandStarts(single);
        int[] two = InkBandStarts(spaced);

        Assert.Equal(Assert.Single(one), two[0]);
        double distance = two[1] - two[0];
        Assert.True(Math.Abs(distance - 100d / 3d) <= 1d, $"expected the lines about {100d / 3d:F1} px apart (2 x 5/3 of the text height), got {distance}.");
    }

    /// <summary>First and last canvas column holding a pixel darker than mid grey.</summary>
    private static int[] InkColumnBounds(Image<Rgba32> canvas)
    {
        int first = -1;
        int last = -1;
        for (int x = 0; x < canvas.Width; x++)
        {
            bool inked = false;
            for (int y = 0; y < canvas.Height && !inked; y++)
            {
                inked = canvas[x, y].R < 128;
            }

            if (inked)
            {
                if (first < 0)
                {
                    first = x;
                }

                last = x;
            }
        }

        return [first, last];
    }
}
