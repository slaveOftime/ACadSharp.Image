using ACadSharp.Image.Extensions;
using SixLabors.ImageSharp;

namespace ACadSharp.Image.Tests;

using Color = SixLabors.ImageSharp.Color;

public sealed class ColorResolutionTests
{
    [Fact]
    public void Index7IsBlackOnLightAndWhiteOnDarkBackgrounds()
    {
        ImageConfiguration light = new();
        ImageConfiguration dark = new() { BackgroundColor = Color.FromRgb(20, 20, 40) };
        ImageConfiguration transparent = new() { BackgroundColor = Color.Transparent };

        Assert.Equal(Color.Black, light.ResolveForegroundColor());
        Assert.Equal(Color.White, dark.ResolveForegroundColor());
        Assert.Equal(Color.Black, transparent.ResolveForegroundColor());
    }

    [Fact]
    public void ExplicitForegroundWins()
    {
        ImageConfiguration configuration = new() { BackgroundColor = Color.Black, ForegroundColor = Color.Yellow };

        Assert.Equal(Color.Yellow, configuration.ResolveForegroundColor());
        Assert.Equal(Color.Yellow, new ACadSharp.Color(7).ToImageColor(configuration.ResolveForegroundColor()));
        Assert.Equal(Color.FromRgb(255, 0, 0), new ACadSharp.Color(1).ToImageColor(configuration.ResolveForegroundColor()));
    }
}
