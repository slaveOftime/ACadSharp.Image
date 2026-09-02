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
        Insert insert = WithHandle(new Insert(block) { Layer = new Layer("Doors") { Color = new ACadSharp.Color(1) } }, 0xAB);

        dispatcher.Draw(CreateContext(surface, configuration), insert);

        // Outer insert, then two nested entities.
        Assert.Equal(3, surface.Entities.Count);
        Assert.Equal("Doors", surface.Entities[0].LayerName);
        Assert.Equal("Doors", surface.Entities[1].LayerName);
        Assert.Equal(0xABUL, surface.Entities[1].ParentHandle);
        Assert.Equal("DOOR", surface.Entities[1].BlockName);
        Assert.Equal(0UL, surface.Entities[1].Handle);
        Assert.Equal("Hardware", surface.Entities[2].LayerName);
        Assert.Equal(0, surface.Depth);
        Assert.Equal(SixLabors.ImageSharp.Color.FromRgb(255, 0, 0), surface.Layers[1].Color);
    }

    [Fact]
    public void EffectiveLayerReturnsParentLayerObjectForLayerZero()
    {
        Layer parent = new("Doors") { IsOn = false };
        Line onZero = new() { Layer = new Layer(Layer.DefaultName) };
        Line onOwn = new() { Layer = new Layer("Own") };

        Assert.Same(parent, EntityRenderDispatcher.GetEffectiveLayer(onZero, parent));
        Assert.Equal("Own", EntityRenderDispatcher.GetEffectiveLayer(onOwn, parent)!.Name);
        Assert.Equal(Layer.DefaultName, EntityRenderDispatcher.GetEffectiveLayer(onZero, null)!.Name);
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

    [Fact]
    public void CurveCapableSurfaceReceivesNativeArcsCirclesAndBulges()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = true };
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        ImageRenderContext context = CreateContext(surface, configuration);

        dispatcher.Draw(context, new Arc { Center = new XYZ(10, 10, 0), Radius = 5, StartAngle = 0, EndAngle = Math.PI / 2 });
        dispatcher.Draw(context, new Circle { Center = new XYZ(0, 0, 0), Radius = 2 });
        LwPolyline polyline = new();
        polyline.Vertices.Add(new LwPolyline.Vertex(new XY(0, 0)) { Bulge = 1 });
        polyline.Vertices.Add(new LwPolyline.Vertex(new XY(10, 0)));
        dispatcher.Draw(context, polyline);

        Assert.Contains(surface.Calls, c => c.StartsWith("DrawArc", StringComparison.Ordinal) && c.Contains("sweep=-1.57", StringComparison.Ordinal));
        Assert.Contains(surface.Calls, c => c.StartsWith("DrawEllipse", StringComparison.Ordinal) && c.Contains("rx=2", StringComparison.Ordinal));
        Assert.Contains(surface.Calls, c => c.StartsWith("DrawBulgePolyline n=2", StringComparison.Ordinal));
        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("DrawPolyline", StringComparison.Ordinal));
    }

    [Fact]
    public void RasterStyleSurfaceStillReceivesTessellatedPolylines()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = false };
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(CreateContext(surface, configuration), new Circle { Center = new XYZ(0, 0, 0), Radius = 2 });

        Assert.Contains(surface.Calls, c => c.StartsWith("DrawPolyline", StringComparison.Ordinal));
        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("DrawEllipse", StringComparison.Ordinal));
    }

    [Fact]
    public void CurveCapableSurfaceReceivesEllipseSemiAxes()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = true };
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        // ACadSharp reports MajorAxis/MinorAxis as full axis lengths; the surface takes semi-axes.
        dispatcher.Draw(CreateContext(surface, configuration), new Ellipse { Center = new XYZ(0, 0, 0), MajorAxisEndPoint = new XYZ(4, 0, 0), RadiusRatio = 0.5 });

        Assert.Contains(surface.Calls, c => c.StartsWith("DrawEllipse", StringComparison.Ordinal) && c.Contains("rx=4 ry=2", StringComparison.Ordinal));
    }
}
