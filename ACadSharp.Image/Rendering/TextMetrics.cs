namespace ACadSharp.Image.Rendering;

/// <summary>
/// Text sizing shared by the raster and SVG backends, chosen to match the CAD intent.
/// </summary>
/// <remarks>
/// <c>SurfaceText.Height</c> is the CAD text height, which is the cap height, and common sans faces have a cap height
/// of about 0.72 em; 4/3 of the cap height is therefore the em that draws the glyphs at the height the drawing asks
/// for. Both backends lay that em out at 72 dpi (one point is one pixel on the raster canvas, one user unit in SVG),
/// so text scales with the page like the geometry does and the two outputs agree. Line spacing follows AutoCAD: 5/3 of
/// the text height per line at spacing factor 1.
/// </remarks>
internal static class TextMetrics
{
    /// <summary>Em size per unit of cap height.</summary>
    public const double CapHeightToEm = 4d / 3d;

    /// <summary>Font size (em) for a CAD text height.</summary>
    public static double EmSize(double capHeight) => capHeight * CapHeightToEm;
}
