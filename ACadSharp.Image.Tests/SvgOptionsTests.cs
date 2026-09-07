namespace ACadSharp.Image.Tests;

public sealed class SvgOptionsTests
{
    [Fact]
    public void DefaultsMatchSpec()
    {
        SvgOptions options = new ImageConfiguration().Svg;

        Assert.True(options.NonScalingStroke);
        Assert.True(options.EmitEntityAttributes);
        Assert.False(options.EmitSize);
        Assert.Equal(string.Empty, options.IdPrefix);
        Assert.Null(options.Precision);
    }

    [Fact]
    public void PrecisionIsValidated()
    {
        SvgOptions options = new();

        options.Precision = 3;
        Assert.Equal(3, options.Precision);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Precision = 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Precision = -1);
    }

    [Fact]
    public void LineWeightMillimetersUsesOverridesThenDefaults()
    {
        ImageConfiguration configuration = new();

        Assert.Equal(0.25d, configuration.GetLineWeightMillimeters(ACadSharp.LineWeightType.W25));
        configuration.SetLineWeight(ACadSharp.LineWeightType.W25, 0.4d);
        Assert.Equal(0.4d, configuration.GetLineWeightMillimeters(ACadSharp.LineWeightType.W25));
        Assert.Equal(0d, configuration.GetLineWeightMillimeters(ACadSharp.LineWeightType.ByLayer));
    }
}
