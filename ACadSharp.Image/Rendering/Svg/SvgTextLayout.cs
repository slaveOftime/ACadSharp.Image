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
    public const double CapHeightToEm = TextMetrics.CapHeightToEm;

    /// <summary>Font size (em) for a CAD text height.</summary>
    public static double EmSize(double capHeight) => TextMetrics.EmSize(capHeight);

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
    /// the words are fitted greedily, measured with SixLabors.Fonts, at the raster's break opportunities, so both
    /// backends break the same labels at the same break opportunities. A token wider than the width stays alone on its
    /// line.
    /// </summary>
    /// <param name="text">Text with <c>\n</c> for explicit breaks.</param>
    /// <param name="wrappingWidth">Available width in surface units, or a non-positive value for no wrapping.</param>
    /// <param name="emSize">Font size in surface units.</param>
    /// <param name="fontFamily">Configured family, resolved through <see cref="FontResolver"/> for measuring.</param>
    /// <returns>The lines, never empty; the paragraphs unwrapped when there is no font, or no way, to measure with.</returns>
    public static IReadOnlyList<string> Wrap(string text, double wrappingWidth, double emSize, string? fontFamily)
    {
        string[] paragraphs = text.Replace("\r\n", "\n").Split('\n');
        if (wrappingWidth <= 0d || emSize <= 0d || !FontResolver.TryResolve(fontFamily, out FontFamily family))
        {
            return paragraphs;
        }

        // Points at 72 dpi are surface units, so the scaled limit is directly comparable with the measured advances.
        TextOptions options = new(family.CreateFont(ReferenceSize)) { Dpi = 72f };
        double limit = wrappingWidth * ReferenceSize / emSize;

        try
        {
            List<string> lines = new();
            foreach (string paragraph in paragraphs)
            {
                AppendLines(paragraph, options, limit, lines);
            }

            return lines;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Shaping can reject text this font cannot lay out. Unwrapped text beats no text and beats a throw on a
            // drawing that merely contains an awkward label.
            return paragraphs;
        }
    }

    /// <summary>
    /// Appends the lines of one paragraph, fitting tokens greedily: a token joins the current line while the line's
    /// advance still fits, and otherwise opens the next one. Trailing whitespace is trimmed off the candidate before
    /// measuring, the way the raster ignores the spaces that fall at a break, and off every line that a break closes;
    /// whitespace inside a line survives as typed.
    /// </summary>
    /// <param name="paragraph">One paragraph, without line breaks.</param>
    /// <param name="options">Measuring options carrying the reference-size font.</param>
    /// <param name="limit">Available width, scaled to the reference size.</param>
    /// <param name="lines">Receives the fitted lines.</param>
    private static void AppendLines(string paragraph, TextOptions options, double limit, List<string> lines)
    {
        string current = string.Empty;
        foreach (string token in Tokenize(paragraph))
        {
            if (current.Length == 0)
            {
                current = token;
                continue;
            }

            string candidate = current + token;
            if (TextMeasurer.MeasureAdvance(candidate.TrimEnd(' ', '\t'), options).Width <= limit)
            {
                current = candidate;
            }
            else
            {
                lines.Add(current.TrimEnd(' ', '\t'));
                current = token;
            }
        }

        lines.Add(current);
    }

    /// <summary>
    /// Cuts a paragraph at the raster's break opportunities: after a run of breaking whitespace, which stays attached
    /// to the token it follows, and after a hyphen-minus or a slash that is not itself followed by whitespace. Working
    /// on the string in logical order keeps the tokens meaningful for right-to-left text and for combining marks,
    /// which a pass over laid-out glyphs cannot promise.
    /// </summary>
    /// <param name="paragraph">One paragraph, without line breaks.</param>
    /// <returns>The tokens, in logical order; concatenated they are the paragraph.</returns>
    private static IEnumerable<string> Tokenize(string paragraph)
    {
        int start = 0;
        int index = 0;
        while (index < paragraph.Length)
        {
            char current = paragraph[index];
            if (IsBreakingSpace(current))
            {
                while (index < paragraph.Length && IsBreakingSpace(paragraph[index]))
                {
                    index++;
                }
            }
            else if ((current == '-' || current == '/') && index + 1 < paragraph.Length && !IsBreakingSpace(paragraph[index + 1]))
            {
                index++;
            }
            else
            {
                index++;
                continue;
            }

            yield return paragraph[start..index];
            start = index;
        }

        if (start < paragraph.Length)
        {
            yield return paragraph[start..];
        }
    }

    /// <summary>Whether the character ends a run that a line may break after.</summary>
    /// <param name="value">The character to classify.</param>
    /// <returns><c>true</c> for a space or a tab; a no-break space (U+00A0) never breaks, as in UAX #14.</returns>
    private static bool IsBreakingSpace(char value) => value is ' ' or '\t';
}
