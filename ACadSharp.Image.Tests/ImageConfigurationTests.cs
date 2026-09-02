using System.Collections.Generic;
using ACadSharp.IO;

namespace ACadSharp.Image.Tests;

public sealed class ImageConfigurationTests
{
    [Fact]
    public void HiddenLayersAreManagedThroughMethods()
    {
        ImageConfiguration configuration = new();

        configuration.HideLayer("LayerA");
        configuration.HideLayer("layerb");

        Assert.Contains("LayerA", configuration.HiddenLayers);
        Assert.Contains("LAYERB", configuration.HiddenLayers);
        Assert.True(configuration.ShowLayer("LAYERA"));
        Assert.DoesNotContain("LayerA", configuration.HiddenLayers);

        configuration.ClearHiddenLayers();

        Assert.Empty(configuration.HiddenLayers);
    }

    [Fact]
    public void LineWeightOverridesAreManagedThroughMethods()
    {
        ImageConfiguration configuration = new();

        configuration.SetLineWeight(LineWeightType.W25, 0.30d);

        Assert.Equal(0.30d, configuration.LineWeightValues[LineWeightType.W25]);
        Assert.True(configuration.RemoveLineWeight(LineWeightType.W25));
        Assert.False(configuration.LineWeightValues.ContainsKey(LineWeightType.W25));

        configuration.SetLineWeight(LineWeightType.W35, 0.35d);
        configuration.ClearLineWeights();

        Assert.Empty(configuration.LineWeightValues);
    }

    [Fact]
    public void LayerVisibilityDefaultsToAll()
    {
        Assert.Equal(LayerVisibilityMode.All, new ImageConfiguration().LayerVisibility);
    }

    [Fact]
    public void IncludedLayersAreManagedThroughMethods()
    {
        ImageConfiguration configuration = new();

        configuration.IncludeLayer("Walls");
        configuration.IncludeLayers(["doors", "Windows"]);

        Assert.Equal(3, configuration.IncludedLayers.Count);
        Assert.Contains("WALLS", configuration.IncludedLayers);
        Assert.True(configuration.ExcludeLayer("DOORS"));
        Assert.False(configuration.ExcludeLayer("nope"));
        Assert.Throws<ArgumentException>(() => configuration.IncludeLayer(" "));

        configuration.ClearIncludedLayers();

        Assert.Empty(configuration.IncludedLayers);
    }

    [Fact]
    public void NewNumericSettingsAreValidated()
    {
        ImageConfiguration configuration = new();

        Assert.Null(configuration.ForegroundColor);
        Assert.Equal(2f, configuration.MinimumDashPixels);
        Assert.Equal(20000, configuration.MaxHatchLines);
        Assert.Throws<ArgumentOutOfRangeException>(() => configuration.MinimumDashPixels = -1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => configuration.MaxHatchLines = 0);
    }
}
