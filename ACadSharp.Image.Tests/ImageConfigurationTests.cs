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
}
