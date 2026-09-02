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
    /// </summary>
    private const short ByBackgroundIndex = 7;

    /// <summary>
    /// Converts an AutoCAD <see cref="CadColor"/> to an ImageSharp <see cref="ImageColor"/>, resolving color
    /// index 7 ("ByBackground") to the given <paramref name="foreground"/> color.
    /// </summary>
    /// <param name="color">The AutoCAD color to convert.</param>
    /// <param name="foreground">The color to use for index 7 ("ByBackground").</param>
    /// <returns>The corresponding ImageSharp color.</returns>
    public static ImageColor ToImageColor(this CadColor color, ImageColor foreground)
    {
        if (color.Index == ByBackgroundIndex)
        {
            return foreground;
        }

        return ImageColor.FromRgb(color.R, color.G, color.B);
    }

    /// <summary>
    /// Converts an AutoCAD <see cref="CadColor"/> to an ImageSharp <see cref="ImageColor"/>, resolving color
    /// index 7 ("ByBackground") to black.
    /// </summary>
    /// <param name="color">The AutoCAD color to convert.</param>
    /// <returns>The corresponding ImageSharp color.</returns>
    public static ImageColor ToImageColor(this CadColor color) => color.ToImageColor(ImageColor.Black);
}
