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

    [Fact]
    public void HideLayersAddsSeveralNamesCaseInsensitively()
    {
        ImageConfiguration configuration = new();

        configuration.HideLayers(["A-DOOR", "a-door", "A-GLAZ"]);

        Assert.Equal(2, configuration.HiddenLayers.Count);
        Assert.Contains("A-DOOR", configuration.HiddenLayers);
        Assert.Contains("a-glaz", configuration.HiddenLayers);
        Assert.True(configuration.ShowLayer("A-Door"));
        Assert.False(configuration.ShowLayer("A-Door"));
        Assert.Throws<ArgumentException>(() => configuration.HideLayers([" "]));
    }

    [Fact]
    public void IncludedLayersBehavesAsAReadOnlySet()
    {
        ImageConfiguration configuration = new();
        configuration.IncludeLayers(["Walls", "Doors"]);
        IReadOnlySet<string> included = configuration.IncludedLayers;

        Assert.Equal(2, included.Count);
        Assert.True(included.Contains("walls"));
        Assert.True(included.IsSubsetOf(["WALLS", "DOORS", "Glazing"]));
        Assert.True(included.IsProperSubsetOf(["WALLS", "DOORS", "Glazing"]));
        Assert.True(included.IsSupersetOf(["doors"]));
        Assert.True(included.IsProperSupersetOf(["doors"]));
        Assert.True(included.Overlaps(["Doors", "Roof"]));
        Assert.True(included.SetEquals(["DOORS", "WALLS"]));
        Assert.Equal(2, included.Count());
        Assert.True(configuration.ExcludeLayer("WALLS"));
        Assert.False(included.Contains("Walls"));
    }

    [Fact]
    public void LineWeightOverridesValidateAndFallBackToDefaults()
    {
        ImageConfiguration configuration = new();
        double defaultW50 = configuration.GetLineWeightMillimeters(ACadSharp.LineWeightType.W50);

        configuration.SetLineWeight(ACadSharp.LineWeightType.W50, 1.25);
        Assert.Equal(1.25, configuration.GetLineWeightMillimeters(ACadSharp.LineWeightType.W50));
        Assert.Throws<ArgumentOutOfRangeException>(() => configuration.SetLineWeight(ACadSharp.LineWeightType.W50, -0.1));
        Assert.True(configuration.RemoveLineWeight(ACadSharp.LineWeightType.W50));
        Assert.False(configuration.RemoveLineWeight(ACadSharp.LineWeightType.W50));
        Assert.Equal(defaultW50, configuration.GetLineWeightMillimeters(ACadSharp.LineWeightType.W50));
        Assert.Equal(0d, configuration.GetLineWeightMillimeters(ACadSharp.LineWeightType.ByLayer));
        configuration.SetLineWeight(ACadSharp.LineWeightType.Default, 0d);
        Assert.Equal(Math.Max(1f, configuration.LineWeightScale), configuration.GetLineWeightPixels(ACadSharp.LineWeightType.Default));
        configuration.ClearLineWeights();
        Assert.Equal(defaultW50, configuration.GetLineWeightMillimeters(ACadSharp.LineWeightType.W50));
    }
}
