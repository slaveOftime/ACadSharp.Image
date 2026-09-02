using System.Reflection;
using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class EntityRenderDispatcherTests
{
    private static ImageRenderContext CreateContext(RecordingDrawingSurface surface, ImageConfiguration configuration)
    {
        Layout layout = new("test") { PaperWidth = 100, PaperHeight = 100 };
        return new ImageRenderContext(surface, configuration, layout, 100, 100, 0, 0, 1d, 0, 0, singlePrecision: false, lineTypeScale: 1d);
    }

    // ACadSharp.CadObject.Handle has an internal setter in ACadSharp 3.7.1, so tests
    // that need a deterministic handle assign it via reflection instead.
    private static T WithHandle<T>(T entity, ulong handle)
        where T : CadObject
    {
        typeof(CadObject).GetProperty(nameof(CadObject.Handle))!.SetValue(entity, handle);
        return entity;
    }

    [Fact]
    public void DrawWrapsEntityInBeginAndEnd()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        Line line = WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0)) { Layer = new Layer("Walls") }, 0x1F3);

        dispatcher.Draw(CreateContext(surface, configuration), line);

        Assert.Equal(3, surface.Calls.Count);
        Assert.StartsWith("BeginEntity LINE layer=Walls parent=- block=-", surface.Calls[0]);
        Assert.StartsWith("DrawLine", surface.Calls[1]);
        Assert.Equal("EndEntity", surface.Calls[2]);
        Assert.Equal(0, surface.Depth);
        Assert.Equal(0x1F3UL, surface.Entities[0].Handle);
    }

    [Fact]
    public void NestedEntityOnLayerZeroInheritsInsertLayer()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        BlockRecord block = new("DOOR");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)) { Layer = new Layer(Layer.DefaultName) });
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)) { Layer = new Layer("Hardware") });
        Insert insert = WithHandle(new Insert(block) { Layer = new Layer("Doors") }, 0xAB);

        dispatcher.Draw(CreateContext(surface, configuration), insert);

        // Outer insert, then two nested entities.
        Assert.Equal(3, surface.Entities.Count);
        Assert.Equal("Doors", surface.Entities[0].LayerName);
        Assert.Equal("Doors", surface.Entities[1].LayerName);
        Assert.Equal(0xABUL, surface.Entities[1].ParentHandle);
        Assert.Equal("DOOR", surface.Entities[1].BlockName);
        Assert.Equal("Hardware", surface.Entities[2].LayerName);
        Assert.Equal(0, surface.Depth);
    }

    [Fact]
    public void LayerInfoCarriesLayerColourAndWidth()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        Layer layer = new("Red") { Color = new ACadSharp.Color(1), LineWeight = LineWeightType.W50 };
        Line line = new(new XYZ(0, 0, 0), new XYZ(10, 0, 0)) { Layer = layer };

        dispatcher.Draw(CreateContext(surface, configuration), line);

        LayerRenderInfo info = Assert.Single(surface.Layers);
        Assert.Equal("Red", info.LayerName);
        Assert.Equal(SixLabors.ImageSharp.Color.FromRgb(255, 0, 0), info.Color);
        Assert.Equal(configuration.GetLineWeightPixels(LineWeightType.W50), info.StrokeWidth);
    }
}
