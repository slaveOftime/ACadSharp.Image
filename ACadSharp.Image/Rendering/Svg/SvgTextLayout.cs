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
    /// Splits text into lines: explicit line breaks always break; when <paramref name="wrappingWidth"/> is positive,
    /// words are added greedily while the measured advance fits. A single word wider than the width stays alone.
    /// </summary>
    /// <param name="text">Text with <c>\n</c> for explicit breaks.</param>
    /// <param name="wrappingWidth">Available width in surface units, or a non-positive value for no wrapping.</param>
    /// <param name="emSize">Font size in surface units.</param>
    /// <param name="fontFamily">Configured family, resolved through <see cref="FontResolver"/> for measuring.</param>
    /// <returns>The lines, never empty.</returns>
    public static IReadOnlyList<string> Wrap(string text, double wrappingWidth, double emSize, string? fontFamily)
    {
        string[] paragraphs = text.Replace("\r\n", "\n").Split('\n');
        if (wrappingWidth <= 0d || emSize <= 0d)
        {
            return paragraphs;
        }

        // Points at 72 dpi are surface units, so the measured advance is directly comparable with the width.
        TextOptions options = new(FontResolver.Create(fontFamily, (float)emSize)) { Dpi = 72f };
        List<string> lines = new();
        foreach (string paragraph in paragraphs)
        {
            string[] words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            string current = words[0];
            for (int i = 1; i < words.Length; i++)
            {
                string candidate = current + " " + words[i];
                if (TextMeasurer.MeasureAdvance(candidate, options).Width <= wrappingWidth)
                {
                    current = candidate;
                }
                else
                {
                    lines.Add(current);
                    current = words[i];
                }
            }

            lines.Add(current);
        }

        return lines;
    }
}
