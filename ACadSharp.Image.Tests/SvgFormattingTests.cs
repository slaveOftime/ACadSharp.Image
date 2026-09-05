using ACadSharp.Image.Rendering.Svg;

namespace ACadSharp.Image.Tests;

public sealed class SvgFormattingTests
{
    [Theory]
    [InlineData(3, 1.23456, "1.235")]
    [InlineData(3, 10.0, "10")]
    [InlineData(3, -0.0004, "0")]
    [InlineData(0, 2.5, "3")]
    [InlineData(2, 1234567.891, "1234567.89")]
    public void FormatRoundsAndTrimsTrailingZeros(int decimals, double value, string expected)
    {
        SvgNumberFormatter formatter = new(decimals);

        Assert.Equal(expected, formatter.Format(value));
    }

    [Theory]
    [InlineData(50000, 30000, 0)]   // 50 m site plan in mm: 1e-4 * 50000 = 5 -> whole units
    [InlineData(420, 297, 2)]       // A3 sheet in mm: 0.042 -> 2 decimals
    [InlineData(20, 10, 3)]         // 20 mm part: 0.002 -> 3 decimals
    [InlineData(0.5, 0.5, 5)]
    [InlineData(1e-9, 1e-9, 8)]
    public void AdaptiveDecimalsTargetsOneTenThousandthOfTheLargerSide(double width, double height, int expected)
    {
        Assert.Equal(expected, SvgNumberFormatter.AdaptiveDecimals(width, height));
    }

    [Theory]
    [InlineData("", "layer", "Walls", "layer-walls")]
    [InlineData("plan1-", "layer", "A-WALL Exterior (new)", "plan1-layer-a-wall-exterior-new")]
    [InlineData("", "layer", "0", "layer-0")]
    [InlineData("", "clip", "", "clip-")]
    public void SanitizeProducesSafeIds(string prefix, string kind, string name, string expected)
    {
        Assert.Equal(expected, SvgIdSanitizer.Sanitize(prefix, kind, name));
    }
}
