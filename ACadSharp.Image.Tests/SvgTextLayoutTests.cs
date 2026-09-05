using ACadSharp.Image.Rendering.Svg;
using SixLabors.Fonts;

namespace ACadSharp.Image.Tests;

/// <summary>
/// Checks that the SVG line breaking is scale-invariant, breaks at the raster's opportunities and lays out the same
/// number of lines as ImageSharp does for the same text and width.
/// </summary>
public sealed class SvgTextLayoutTests
{
    private const string Family = "DejaVu Sans";

    /// <summary>Em size the parity measurements use; the layout is linear in it, so any fixed size will do.</summary>
    private const float ReferenceSize = 100f;

    [Fact]
    public void WrappingIsScaleInvariant()
    {
        IReadOnlyList<string> large = SvgTextLayout.Wrap("alpha beta gamma delta", 14, 4, Family);
        IReadOnlyList<string> small = SvgTextLayout.Wrap("alpha beta gamma delta", 1.4, 0.4, Family);

        Assert.Equal(["alpha", "beta", "gamma", "delta"], large);
        Assert.Equal(large, small);
    }

    [Fact]
    public void HyphenatedPartsBreakAfterTheHyphen()
    {
        // "left-" advances 7.92 units at em 4, the other parts more, so only one part fits per line at width 8.
        IReadOnlyList<string> lines = SvgTextLayout.Wrap("left-hand-side-rail", 8, 4, Family);

        Assert.Equal(["left-", "hand-", "side-", "rail"], lines);
    }

    [Fact]
    public void RepeatedSpacesArePreserved()
    {
        Assert.Equal(["A  B"], SvgTextLayout.Wrap("A  B", 100, 4, Family));
    }

    [Fact]
    public void SlashesBreakAfterTheSlash()
    {
        Assert.Equal(["a/", "b/", "c/", "d"], SvgTextLayout.Wrap("a/b/c/d", 4, 4, Family));
    }

    [Fact]
    public void TokenWiderThanTheWidthStaysAloneOnItsLine()
    {
        Assert.Equal(["supercalifragilistic"], SvgTextLayout.Wrap("supercalifragilistic", 2, 4, Family));
    }

    [Fact]
    public void NoBreakSpaceNeverBreaks()
    {
        Assert.Equal(["A\u00A0B"], SvgTextLayout.Wrap("A\u00A0B", 1, 4, Family));
    }

    [Fact]
    public void CombiningMarksDoNotBreakTheWrapper()
    {
        // Combining marks below the baseline (U+0332 COMBINING LOW LINE) and their precomposed equivalents.
        Assert.Equal(["\u05D5\u0332\u05D5\u0332 abc"], SvgTextLayout.Wrap("\u05D5\u0332\u05D5\u0332 abc", 100, 4, Family));
        Assert.Equal(["\u1E3B abc"], SvgTextLayout.Wrap("\u1E3B abc", 100, 4, Family));
        Assert.Equal(["l\u0332l\u0332 abc"], SvgTextLayout.Wrap("l\u0332l\u0332 abc", 100, 4, Family));
    }

    [Fact]
    public void RightToLeftTextWrapsInLogicalOrder()
    {
        // Three Hebrew words; the lines must be logical-order slices, not visual-order ones.
        IReadOnlyList<string> lines = SvgTextLayout.Wrap("\u05D0\u05D1\u05D2 \u05D3\u05D4\u05D5 \u05D6\u05D7\u05D8", 6, 4, Family);

        Assert.Equal(["\u05D0\u05D1\u05D2", "\u05D3\u05D4\u05D5", "\u05D6\u05D7\u05D8"], lines);
    }

    [Theory]
    [InlineData("alpha beta gamma delta", 6)]
    [InlineData("alpha beta gamma delta", 9)]
    [InlineData("alpha beta gamma delta", 14)]
    [InlineData("alpha beta gamma delta", 30)]
    [InlineData("left-hand-side-rail", 6)]
    [InlineData("left-hand-side-rail", 9)]
    [InlineData("left-hand-side-rail", 14)]
    [InlineData("left-hand-side-rail", 30)]
    [InlineData("A  B   C", 6)]
    [InlineData("A  B   C", 9)]
    [InlineData("A  B   C", 14)]
    [InlineData("A  B   C", 30)]
    [InlineData("A\u00A0B", 1)]
    [InlineData("keep\u00A0together x", 12)]
    public void LineCountMatchesImageSharpLayout(string text, double width)
    {
        int expected = ImageSharpLineCount(text, width, 4d);

        IReadOnlyList<string> lines = SvgTextLayout.Wrap(text, width, 4, Family);

        Assert.Equal(expected, lines.Count);
    }

    /// <summary>
    /// Counts the lines ImageSharp lays the text out on, independently of the wrapper: consecutive baselines are one
    /// em apart at line spacing 1, and no glyph's top strays a whole em from its own line's top, so the distinct
    /// <c>floor(top / em)</c> values are the lines.
    /// </summary>
    private static int ImageSharpLineCount(string text, double width, double emSize)
    {
        Assert.True(SystemFonts.TryGet(Family, out FontFamily family), $"Font '{Family}' must be installed.");
        TextOptions options = new(family.CreateFont(ReferenceSize))
        {
            Dpi = 72f,
            WrappingLength = (float)(width * ReferenceSize / emSize),
        };

        Assert.True(TextMeasurer.TryMeasureCharacterBounds(text, options, out ReadOnlySpan<GlyphBounds> glyphs));
        HashSet<int> rows = new();
        foreach (GlyphBounds glyph in glyphs)
        {
            rows.Add((int)Math.Floor(glyph.Bounds.Y / ReferenceSize));
        }

        return rows.Count;
    }
}
