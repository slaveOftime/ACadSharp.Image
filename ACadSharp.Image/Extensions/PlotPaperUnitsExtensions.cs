using System.ComponentModel;
using ACadSharp.Objects;

namespace ACadSharp.Image.Extensions;

public static class PlotPaperUnitsExtensions
{
    public static float GetPixelsPerUnit(this PlotPaperUnits unit, float dpi)
    {
        return unit switch
        {
            PlotPaperUnits.Inches => dpi,
            PlotPaperUnits.Millimeters => dpi / 25.4f,
            PlotPaperUnits.Pixels => 1f,
            _ => throw new InvalidEnumArgumentException(nameof(unit), (int)unit, typeof(PlotPaperUnits)),
        };
    }

    public static float ToPixels(this double value, PlotPaperUnits unit, float dpi)
    {
        return (float)value * unit.GetPixelsPerUnit(dpi);
    }
}
