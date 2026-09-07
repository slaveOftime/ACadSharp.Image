using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Tables;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Turns a CAD linetype into an alternating dash/gap array in surface units.
/// </summary>
internal static class LineTypeDashResolver
{
    /// <summary>
    /// Resolves the dash pattern of a resolved linetype.
    /// </summary>
    /// <param name="lineType">The linetype after ByLayer/ByBlock substitution, or null for a solid stroke.</param>
    /// <param name="header">Header supplying LTSCALE, or null when the entity belongs to no document (scale 1).</param>
    /// <param name="lineTypeScale">The entity's effective CELTSCALE, including every enclosing insert's.</param>
    /// <param name="context">The context that maps linetype units onto the surface.</param>
    /// <param name="strokeWidth">The stroke width in surface units; dots are drawn as a dash this long.</param>
    /// <returns>
    /// Alternating dash and gap lengths in surface units, or <see langword="null"/> for a solid stroke.
    /// </returns>
    public static float[]? Resolve(LineType? lineType, CadHeader? header, double lineTypeScale, ImageRenderContext context, float strokeWidth)
    {
        if (lineType == null)
        {
            return null;
        }

        double ltscale = header != null && header.LineTypeScale > 0d ? header.LineTypeScale : 1d;
        double celtscale = lineTypeScale > 0d ? lineTypeScale : 1d;
        float[]? pattern = BuildPattern(lineType, ltscale * celtscale * context.LineTypeScale, strokeWidth);
        if (pattern == null)
        {
            return null;
        }

        // A huge LTSCALE or CELTSCALE overflows the pattern to infinity, which no surface can dash with.
        if (pattern.Any(v => !float.IsFinite(v)))
        {
            return null;
        }

        if (EnforcesMinimumDash(context) && pattern.Sum() < context.Configuration.MinimumDashPixels)
        {
            return null;
        }

        return pattern;
    }

    /// <summary>
    /// Indicates whether <see cref="ImageConfiguration.MinimumDashPixels"/> applies in a context.
    /// </summary>
    /// <param name="context">The context the entity is drawn in.</param>
    /// <returns>True when the surface expresses stroke sizes in pixels, so the minimum is meaningful.</returns>
    public static bool EnforcesMinimumDash(ImageRenderContext context)
    {
        return context.StrokeUnitsPerMillimeter == null;
    }

    /// <summary>
    /// Builds the dash pattern of a linetype, scaled into surface units.
    /// </summary>
    /// <param name="lineType">The linetype to convert.</param>
    /// <param name="scale">Surface units per linetype unit, including LTSCALE and CELTSCALE.</param>
    /// <param name="strokeWidth">The stroke width in surface units; dots are drawn as a dash this long.</param>
    /// <returns>
    /// Alternating dash and gap lengths in surface units, or <see langword="null"/> when the linetype has no gaps
    /// and is therefore solid.
    /// </returns>
    public static float[]? BuildPattern(LineType lineType, double scale, float strokeWidth)
    {
        List<(bool On, float Length)> entries = new();
        foreach (LineType.Segment segment in lineType.Segments)
        {
            double length = segment.Length * scale;
            bool on;
            float value;
            if (segment.IsShape || segment.IsText)
            {
                on = false;
                value = (float)Math.Abs(length);
            }
            else if (length > 0d)
            {
                on = true;
                value = (float)length;
            }
            else if (length < 0d)
            {
                on = false;
                value = (float)-length;
            }
            else
            {
                on = true;
                value = strokeWidth;
            }

            if (entries.Count > 0 && entries[^1].On == on)
            {
                entries[^1] = (on, entries[^1].Length + value);
            }
            else
            {
                entries.Add((on, value));
            }
        }

        if (entries.Count == 0 || !entries.Any(e => !e.On))
        {
            return null;
        }

        if (!entries[0].On)
        {
            entries.Insert(0, (true, 0f));
        }

        if (entries.Count % 2 == 1)
        {
            entries.Add((false, 0f));
        }

        return entries.Select(e => e.Length).ToArray();
    }
}
