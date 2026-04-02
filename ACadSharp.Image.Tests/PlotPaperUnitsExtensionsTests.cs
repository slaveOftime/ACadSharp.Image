using ACadSharp.Image.Extensions;
using ACadSharp.Objects;

namespace ACadSharp.Image.Tests;

public sealed class PlotPaperUnitsExtensionsTests
{
    [Fact]
    public void MillimetersToPixelsUsesDpi()
    {
        float pixels = 10d.ToPixels(PlotPaperUnits.Millimeters, 254f);

        Assert.Equal(100f, pixels, 3);
    }

    [Fact]
    public void PixelsRemainUnchanged()
    {
        float pixels = 42d.ToPixels(PlotPaperUnits.Pixels, 96f);

        Assert.Equal(42f, pixels);
    }
}
