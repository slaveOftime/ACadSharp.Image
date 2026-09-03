using SixLabors.Fonts;

namespace ACadSharp.Image.Rendering.Svg;

/// <summary>
/// Text metrics for the SVG backend, chosen to match the raster backend and the CAD intent.
/// </summary>
/// <remarks>
/// <c>SurfaceText.Height</c> is the CAD text height, which is the cap height. The raster backend creates a font of that
/// size in points and renders at 96 dpi, so its em size is 4/3 of the cap height, and common sans faces have a cap
/// height of about 0.72 em; the SVG uses the same factor so both outputs agree. Line spacing follows AutoCAD: 5/3 of
/// the text height per line at spacing factor 1.
/// </remarks>
internal static class SvgTextLayout
{
    /// <summary>Em size per unit of cap height.</summary>
    public const double CapHeightToEm = 4d / 3d;

    /// <summary>Font size (em) for a CAD text height.</summary>
    public static double EmSize(double capHeight) => capHeight * CapHeightToEm;

    /// <summary>Distance between consecutive baselines.</summary>
    public static double LineHeight(double capHeight, double lineSpacingFactor) =>
        capHeight * (lineSpacingFactor <= 0 ? 1d : lineSpacingFactor) * 5d / 3d;

    /// <summary>
    /// Offset of the first line's baseline from the anchor, in surface units (negative is up), so the whole block hangs
    /// from, is centred on, or stands on the anchor the way the CAD attachment point says.
    /// </summary>
    public static double BlockOffset(int lineCount, double lineHeight, SurfaceTextBaseline baseline) => baseline switch
    {
        SurfaceTextBaseline.Central => -(lineCount - 1) * lineHeight / 2d,
        SurfaceTextBaseline.Alphabetic => -(lineCount - 1) * lineHeight,
        _ => 0d,
    };

    /// <summary>
    /// Em size the wrapping measurements use. SixLabors applies no hinting, so advances are linear in the size: a
    /// fixed reference size with a proportionally scaled width wraps sub-unit CAD text as accurately as large text,
    /// and sidesteps the one-point clamp in <see cref="FontResolver.Create"/>.
    /// </summary>
    private const float ReferenceSize = 100f;

    /// <summary>
    /// Splits text into lines: explicit line breaks always break; when <paramref name="wrappingWidth"/> is positive,
    /// the lines are the ones ImageSharp itself lays out for that width, so both backends break the same labels at the
    /// same places. A word wider than the width stays alone on its line.
    /// </summary>
    /// <param name="text">Text with <c>\n</c> for explicit breaks.</param>
    /// <param name="wrappingWidth">Available width in surface units, or a non-positive value for no wrapping.</param>
    /// <param name="emSize">Font size in surface units.</param>
    /// <param name="fontFamily">Configured family, resolved through <see cref="FontResolver"/> for measuring.</param>
    /// <returns>The lines, never empty; the paragraphs unwrapped when no font is installed to measure with.</returns>
    public static IReadOnlyList<string> Wrap(string text, double wrappingWidth, double emSize, string? fontFamily)
    {
        string[] paragraphs = text.Replace("\r\n", "\n").Split('\n');
        if (wrappingWidth <= 0d || emSize <= 0d || !FontResolver.TryResolve(fontFamily, out FontFamily family))
        {
            return paragraphs;
        }

        // Points at 72 dpi are surface units, so the scaled wrapping length is directly comparable with the advances.
        TextOptions options = new(family.CreateFont(ReferenceSize))
        {
            Dpi = 72f,
            WrappingLength = (float)(wrappingWidth * ReferenceSize / emSize),
        };

        List<string> lines = new();
        foreach (string paragraph in paragraphs)
        {
            AppendLines(paragraph, options, lines);
        }

        return lines;
    }

    /// <summary>
    /// Appends the lines ImageSharp lays one paragraph out on, sliced out of the paragraph by the string index of the
    /// glyph that starts each line. ImageSharp drops the whitespace run it broke at, which the slice still carries, so
    /// every line but the last is trimmed at its end.
    /// </summary>
    /// <param name="paragraph">One paragraph, without line breaks.</param>
    /// <param name="options">Measuring options carrying the font and the scaled wrapping length.</param>
    /// <param name="lines">Receives the laid-out lines.</param>
    private static void AppendLines(string paragraph, TextOptions options, List<string> lines)
    {
        if (paragraph.Length == 0
            || !TextMeasurer.TryMeasureCharacterBounds(paragraph, options, out ReadOnlySpan<GlyphBounds> glyphs)
            || glyphs.Length == 0)
        {
            lines.Add(paragraph);
            return;
        }

        List<int> starts = [0];
        for (int i = 1; i < glyphs.Length; i++)
        {
            if (StartsLine(paragraph, glyphs[i - 1], glyphs[i]))
            {
                starts.Add(glyphs[i].StringIndex);
            }
        }

        for (int i = 0; i < starts.Count; i++)
        {
            bool last = i + 1 == starts.Count;
            string line = paragraph[starts[i]..(last ? paragraph.Length : starts[i + 1])];
            lines.Add(last ? line : line.TrimEnd());
        }
    }

    /// <summary>
    /// Whether a glyph opens a new line. Every break moves the glyph down by a line, so a top that did not descend
    /// rules one out; on top of that the glyph either falls back towards the left margin, or ImageSharp swallowed the
    /// whitespace run it broke at, leaving a gap in the string indices. Requiring the descent as well keeps a
    /// combining mark drawn back over its base, or a surrogate pair's index step, from reading as a break.
    /// </summary>
    /// <param name="paragraph">The paragraph being laid out.</param>
    /// <param name="previous">The preceding glyph.</param>
    /// <param name="current">The glyph to classify.</param>
    /// <returns><c>true</c> when <paramref name="current"/> is the first glyph of a new line.</returns>
    private static bool StartsLine(string paragraph, GlyphBounds previous, GlyphBounds current)
    {
        if (current.Bounds.Y <= previous.Bounds.Y)
        {
            return false;
        }

        if (current.Bounds.X < previous.Bounds.X)
        {
            return true;
        }

        for (int i = previous.StringIndex + 1; i < current.StringIndex; i++)
        {
            if (!char.IsWhiteSpace(paragraph[i]))
            {
                return false;
            }

            if (i + 1 == current.StringIndex)
            {
                return true;
            }
        }

        return false;
    }
}
