using CadColor = ACadSharp.Color;
using ImageColor = SixLabors.ImageSharp.Color;

namespace ACadSharp.Image.Extensions;

/// <summary>
/// Provides extension methods for converting AutoCAD colors to ImageSharp colors.
/// </summary>
internal static class ColorExtensions
{
    /// <summary>
    /// AutoCAD color index 7 represents "ByBackground" (white on dark backgrounds, black on light backgrounds).
    /// We default to black for rendering purposes.
    /// </summary>
    private const short ByBackgroundIndex = 7;

    /// <summary>
    /// Converts an AutoCAD <see cref="CadColor"/> to an ImageSharp <see cref="ImageColor"/>.
    /// </summary>
    /// <param name="color">The AutoCAD color to convert.</param>
    /// <returns>The corresponding ImageSharp color.</returns>
    public static ImageColor ToImageColor(this CadColor color)
    {
        if (color.Index == ByBackgroundIndex)
        {
            return ImageColor.Black;
        }

        return ImageColor.FromRgb(color.R, color.G, color.B);
    }
}
