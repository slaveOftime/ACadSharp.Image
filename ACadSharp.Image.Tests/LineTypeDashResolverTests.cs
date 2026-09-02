using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Objects;
using ACadSharp.Tables;

namespace ACadSharp.Image.Tests;

public sealed class LineTypeDashResolverTests
{
    private static LineType Dashed(params double[] lengths)
    {
        LineType lineType = new("DASHED");
        foreach (double length in lengths)
        {
            lineType.AddSegment(new LineType.Segment { Length = length });
        }

        return lineType;
    }

    private static ImageRenderContext Context(double scale, double? unitsPerMillimeter = null, float minimumDash = 2f)
    {
        ImageConfiguration configuration = new() { MinimumDashPixels = minimumDash };
        Layout layout = new("t") { PaperWidth = 10, PaperHeight = 10 };
        return new ImageRenderContext(new RecordingDrawingSurface(), configuration, layout, 10, 10, 0, 0, scale, 0, 0, singlePrecision: false, lineTypeScale: scale, strokeUnitsPerMillimeter: unitsPerMillimeter);
    }

    [Fact]
    public void ContinuousIsSolid()
    {
        Assert.Null(LineTypeDashResolver.BuildPattern(LineType.Continuous, 1d, 1f));
    }

    [Fact]
    public void AllDashPatternIsSolid()
    {
        Assert.Null(LineTypeDashResolver.BuildPattern(Dashed(1, 2), 1d, 1f));
    }

    [Fact]
    public void DashGapPatternScales()
    {
        float[]? pattern = LineTypeDashResolver.BuildPattern(Dashed(0.5, -0.25), 4d, 1f);

        Assert.NotNull(pattern);
        Assert.Equal([2f, 1f], pattern);
    }

    [Fact]
    public void DotsBecomeStrokeWidthDashesAndGapsMerge()
    {
        LineType lineType = Dashed(0.5, -0.25, 0, -0.25);

        float[]? pattern = LineTypeDashResolver.BuildPattern(lineType, 2d, 1.5f);

        // dash 1, gap 0.5, dot -> 1.5, gap 0.5
        Assert.NotNull(pattern);
        Assert.Equal([1f, 0.5f, 1.5f, 0.5f], pattern);
    }

    [Fact]
    public void ShapeSegmentsAreGaps()
    {
        LineType lineType = new("GAS");
        lineType.AddSegment(new LineType.Segment { Length = 0.5 });
        lineType.AddSegment(new LineType.Segment { Length = -0.2 });
        lineType.AddSegment(new LineType.Segment { Length = 0.3, IsText = true, Text = "GAS" });
        lineType.AddSegment(new LineType.Segment { Length = -0.2 });

        float[]? pattern = LineTypeDashResolver.BuildPattern(lineType, 10d, 1f);

        Assert.NotNull(pattern);
        Assert.Equal([5f, 7f], pattern); // gaps 2 + 3 + 2 merged
    }

    [Fact]
    public void PatternStartingWithGapGetsZeroDash()
    {
        float[]? pattern = LineTypeDashResolver.BuildPattern(Dashed(-0.5, 0.5), 1d, 1f);

        Assert.NotNull(pattern);
        Assert.Equal([0f, 0.5f, 0.5f, 0f], pattern);
    }

    [Fact]
    public void TinyPatternsAreSolidInPixelMode()
    {
        Line line = new() { LineType = Dashed(0.1, -0.1) };

        Assert.Null(LineTypeDashResolver.Resolve(line, Context(1d), 1f));
        Assert.NotNull(LineTypeDashResolver.Resolve(line, Context(20d), 1f));
        Assert.NotNull(LineTypeDashResolver.Resolve(line, Context(1d, unitsPerMillimeter: 1d), 1f));
    }

    [Fact]
    public void EntityLineTypeScaleMultiplies()
    {
        Line line = new() { LineType = Dashed(1, -1), LineTypeScale = 3 };

        float[]? pattern = LineTypeDashResolver.Resolve(line, Context(1d), 1f);

        Assert.NotNull(pattern);
        Assert.Equal([3f, 3f], pattern);
    }
}
