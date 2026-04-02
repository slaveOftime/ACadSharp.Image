using CadColor = ACadSharp.Color;
using ImageColor = SixLabors.ImageSharp.Color;

namespace ACadSharp.Image.Extensions;

internal static class ColorExtensions
{
    public static ImageColor ToImageColor(this CadColor color)
    {
        if (color.Index == 7)
        {
            return ImageColor.Black;
        }

        return ImageColor.FromRgb(color.R, color.G, color.B);
    }
}
