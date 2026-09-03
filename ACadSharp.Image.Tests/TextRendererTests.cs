using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Objects;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class TextRendererTests
{
    private static (RecordingDrawingSurface Surface, ImageRenderContext Context, EntityRenderDispatcher Dispatcher) Setup(double scale = 1d)
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Layout layout = new("t") { PaperWidth = 100, PaperHeight = 100 };
        ImageRenderContext context = new(surface, configuration, layout, 100, 100, 0, 0, scale, 0, 0, singlePrecision: false, lineTypeScale: scale);
        return (surface, context, new EntityRenderDispatcher(configuration));
    }

    [Fact]
    public void FitTextIsCentredBetweenInsertAndAlignmentPointsWithAFixedLength()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup(scale: 2d);
        TextEntity text = new() { Value = "FIT", InsertPoint = new XYZ(10, 20, 0), AlignmentPoint = new XYZ(40, 20, 0), HorizontalAlignment = TextHorizontalAlignment.Fit, Height = 5 };

        dispatcher.Draw(context, text);

        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal(SurfaceTextAnchor.Middle, run.Anchor);
        Assert.Equal(SurfaceTextBaseline.Alphabetic, run.Baseline);
        Assert.Equal(60d, run.FixedLength, 9);           // 30 drawing units x scale 2
        Assert.Equal(80d, run.Origin.X, 9);              // origin is the alignment point for anything but Left/Baseline
        Assert.Equal(100d - 40d, run.Origin.Y, 9);
        Assert.Equal(10d, run.Height, 9);
    }

    [Fact]
    public void AlignedTextWithCoincidentPointsHasNoFixedLength()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();
        TextEntity text = new() { Value = "A", InsertPoint = new XYZ(1, 1, 0), AlignmentPoint = new XYZ(1, 1, 0), HorizontalAlignment = TextHorizontalAlignment.Aligned, Height = 2 };

        dispatcher.Draw(context, text);

        Assert.Equal(-1d, Assert.Single(surface.Texts).FixedLength);
    }

    [Theory]
    [InlineData(TextHorizontalAlignment.Left, TextVerticalAlignmentType.Baseline, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, 1d)]
    [InlineData(TextHorizontalAlignment.Center, TextVerticalAlignmentType.Baseline, SurfaceTextAnchor.Middle, SurfaceTextBaseline.Alphabetic, 9d)]
    [InlineData(TextHorizontalAlignment.Right, TextVerticalAlignmentType.Top, SurfaceTextAnchor.End, SurfaceTextBaseline.Hanging, 9d)]
    [InlineData(TextHorizontalAlignment.Middle, TextVerticalAlignmentType.Middle, SurfaceTextAnchor.Middle, SurfaceTextBaseline.Central, 9d)]
    [InlineData(TextHorizontalAlignment.Left, TextVerticalAlignmentType.Bottom, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, 9d)]
    // SurfaceTextAnchor and SurfaceTextBaseline are internal, and a public [Theory] method cannot
    // declare a parameter less accessible than itself (CS0051), so they travel through InlineData
    // boxed as object and are cast back inside the method body.
    public void TextAlignmentMapsToAnchorBaselineAndOrigin(TextHorizontalAlignment horizontal, TextVerticalAlignmentType vertical, object anchor, object baseline, double expectedOriginX)
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();
        TextEntity text = new() { Value = "T", InsertPoint = new XYZ(1, 0, 0), AlignmentPoint = new XYZ(9, 0, 0), HorizontalAlignment = horizontal, VerticalAlignment = vertical, Height = 2 };

        dispatcher.Draw(context, text);

        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal((SurfaceTextAnchor)anchor, run.Anchor);
        Assert.Equal((SurfaceTextBaseline)baseline, run.Baseline);
        Assert.Equal(expectedOriginX, run.Origin.X, 9);
    }

    [Theory]
    [InlineData(AttachmentPointType.TopLeft, SurfaceTextAnchor.Start, SurfaceTextBaseline.Hanging)]
    [InlineData(AttachmentPointType.TopCenter, SurfaceTextAnchor.Middle, SurfaceTextBaseline.Hanging)]
    [InlineData(AttachmentPointType.MiddleRight, SurfaceTextAnchor.End, SurfaceTextBaseline.Central)]
    [InlineData(AttachmentPointType.BottomCenter, SurfaceTextAnchor.Middle, SurfaceTextBaseline.Alphabetic)]
    [InlineData(AttachmentPointType.BottomLeft, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic)]
    // See the comment on TextAlignmentMapsToAnchorBaselineAndOrigin: the internal enum parameters
    // travel through InlineData as object to satisfy CS0051 on this public [Theory] method.
    public void MTextAttachmentMapsToAnchorAndBaseline(AttachmentPointType attachment, object anchor, object baseline)
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();
        MText text = new() { Value = "M", InsertPoint = new XYZ(5, 5, 0), Height = 3, AttachmentPoint = attachment, RectangleWidth = 40, LineSpacing = 1.5 };

        dispatcher.Draw(context, text);

        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal((SurfaceTextAnchor)anchor, run.Anchor);
        Assert.Equal((SurfaceTextBaseline)baseline, run.Baseline);
        Assert.Equal(40d, run.WrappingWidth, 9);
        Assert.Equal(1.5d, run.LineSpacingFactor, 9);
        Assert.Equal(-1d, run.FixedLength);
    }

    [Fact]
    public void MTextWithoutRectangleWidthDoesNotWrap()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();

        dispatcher.Draw(context, new MText { Value = "M", InsertPoint = new XYZ(5, 5, 0), Height = 3, RectangleWidth = 0 });

        Assert.Equal(-1d, Assert.Single(surface.Texts).WrappingWidth);
    }

    [Fact]
    public void ControlCodesAreExpandedAndParagraphsBecomeLines()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();

        dispatcher.Draw(context, new TextEntity { Value = "%%c20 %%d %%p1", InsertPoint = new XYZ(0, 0, 0), Height = 2 });
        dispatcher.Draw(context, new MText { Value = "First\\PSecond", InsertPoint = new XYZ(0, 0, 0), Height = 2 });

        Assert.Equal(2, surface.Texts.Count);
        Assert.Equal("Ø20 ° ±1", surface.Texts[0].Text);
        Assert.Equal("First\nSecond", surface.Texts[1].Text);
    }

    [Fact]
    public void BlankTextDrawsNothing()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();

        dispatcher.Draw(context, new TextEntity { Value = "   ", InsertPoint = new XYZ(0, 0, 0), Height = 2 });
        dispatcher.Draw(context, new MText { Value = string.Empty, InsertPoint = new XYZ(0, 0, 0), Height = 2 });

        Assert.Empty(surface.Texts);
        Assert.Equal(4, surface.Calls.Count); // two Begin/End pairs, no DrawText
    }
}
