namespace ACadSharp.Image.Rendering;

/// <summary>
/// Text sizing shared by the raster and SVG backends, chosen to match the CAD intent.
/// </summary>
internal static class TextMetrics
{
    /// <summary>Em size per unit of cap height.</summary>
    public const double CapHeightToEm = 4d / 3d;

    /// <summary>Font size (em) for a CAD text height.</summary>
    public static double EmSize(double capHeight) => capHeight * CapHeightToEm;
}
