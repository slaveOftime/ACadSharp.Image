using System.Globalization;

namespace ACadSharp.Image.Rendering.Svg;

/// <summary>
/// Formats numbers for SVG attributes: invariant culture, fixed decimals, trailing zeros trimmed, no negative zero.
/// </summary>
internal sealed class SvgNumberFormatter
{
    private readonly int _decimals;

    public SvgNumberFormatter(int decimals)
    {
        this._decimals = Math.Clamp(decimals, 0, 8);
    }

    public int Decimals => this._decimals;

    public string Format(double value)
    {
        double rounded = Math.Round(value, this._decimals, MidpointRounding.AwayFromZero);
        if (rounded == 0d)
        {
            return "0";
        }

        string text = rounded.ToString("F" + this._decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (this._decimals > 0)
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }

        return text;
    }

    /// <summary>
    /// Decimals such that the resolution is one ten-thousandth of the larger viewBox side, clamped to 0..8.
    /// </summary>
    public static int AdaptiveDecimals(double width, double height)
    {
        double size = Math.Max(Math.Abs(width), Math.Abs(height));
        if (size <= 0d || double.IsNaN(size) || double.IsInfinity(size))
        {
            return 3;
        }

        int decimals = 4 - (int)Math.Floor(Math.Log10(size));
        return Math.Clamp(decimals, 0, 8);
    }
}
