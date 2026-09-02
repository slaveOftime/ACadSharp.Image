using System.Reflection;
using System.Xml.Linq;
using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Image.Rendering.Svg;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class EntityRenderDispatcherTests
{
    private static ImageRenderContext CreateContext(IDrawingSurface surface, ImageConfiguration configuration)
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
        Assert.Contains(surface.Calls, c => c.StartsWith("DrawBulgePolyline n=2", StringComparison.Ordinal) && c.Contains("bulges=1,0", StringComparison.Ordinal));
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

    [Fact]
    public void EllipseRotationAndPartialSweepAreNegatedForTheSurface()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = true };
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        ImageRenderContext context = CreateContext(surface, configuration);

        // Major axis along +Y, so the drawing rotation is +PI/2 and the surface rotation is -PI/2.
        dispatcher.Draw(context, new Ellipse { Center = new XYZ(0, 0, 0), MajorAxisEndPoint = new XYZ(0, 4, 0), RadiusRatio = 0.5 });
        dispatcher.Draw(context, new Ellipse
        {
            Center = new XYZ(0, 0, 0),
            MajorAxisEndPoint = new XYZ(2, 0, 0),
            RadiusRatio = 0.5,
            StartParameter = 0,
            EndParameter = Math.PI / 2,
        });

        Assert.Contains(surface.Calls, c => c.StartsWith("DrawEllipse", StringComparison.Ordinal) && c.Contains("rot=-1.57", StringComparison.Ordinal));
        Assert.Contains(surface.Calls, c => c.StartsWith("DrawArc", StringComparison.Ordinal)
            && c.Contains("rx=2 ry=1", StringComparison.Ordinal)
            && c.Contains("sweep=-1.57", StringComparison.Ordinal));
    }

    [Fact]
    public void NonWorldNormalFallsBackToTessellation()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = true };
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        // A (0,0,-1) extrusion mirrors X in the OCS; only PolygonalVertexes applies it, so the native path must be skipped.
        dispatcher.Draw(CreateContext(surface, configuration), new Circle { Center = new XYZ(10, 0, 0), Radius = 1, Normal = new XYZ(0, 0, -1) });

        Assert.Contains(surface.Calls, c => c.StartsWith("DrawPolyline", StringComparison.Ordinal));
        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("DrawEllipse", StringComparison.Ordinal));
    }

    // Both angles are finite, so the entity passes the non-finite gate, but their difference overflows to infinity.
    // Without the sweep normalization the tessellation would step over an unbounded range and hang.
    private static Arc HugeSweepArc() => new()
    {
        Center = new XYZ(10, 10, 0),
        Radius = 5,
        StartAngle = -double.MaxValue,
        EndAngle = double.MaxValue,
    };

    private static Arc NonFiniteArc() => new()
    {
        Center = new XYZ(10, 10, 0),
        Radius = double.PositiveInfinity,
        StartAngle = double.NaN,
        EndAngle = double.NaN,
    };

    [Fact]
    public void HugeArcSweepNormalizesToAFullTurnInsteadOfHanging()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = true };
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(CreateContext(surface, configuration), HugeSweepArc());

        Assert.Contains(surface.Calls, c => c.StartsWith("DrawArc", StringComparison.Ordinal) && c.Contains("sweep=-6.28", StringComparison.Ordinal));

        // A full turn reaches the SVG surface as a closed ellipse (a circle here), never an arc path.
        using SvgDrawingSurface svg = new(configuration, new SurfaceRect(0, 0, 100, 100), null, null);
        dispatcher.Draw(CreateContext(svg, configuration), HugeSweepArc());

        XDocument document = svg.ToDocument();
        Assert.Single(document.Descendants(SvgDrawingSurface.Ns + "circle"));
        Assert.Empty(document.Descendants(SvgDrawingSurface.Ns + "path"));

        // The other branch of the same guard: a huge but finite sweep is folded into one turn with a modulo
        // instead of being handed to the surface raw, which would step a tessellation over 1e9 radians.
        surface.Calls.Clear();
        dispatcher.Draw(CreateContext(surface, configuration), new Arc { Center = new XYZ(10, 10, 0), Radius = 5, StartAngle = 0, EndAngle = 1e9 });
        string call = Assert.Single(surface.Calls, c => c.StartsWith("DrawArc", StringComparison.Ordinal));
        Assert.Contains("sweep=-0.577", call, StringComparison.Ordinal);
    }

    [Fact]
    public void NonFiniteArcIsSkippedWithWarning()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = true };
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(CreateContext(surface, configuration), WithHandle(NonFiniteArc(), 0x1FA));

        Assert.Empty(surface.Calls);
        NotificationEventArgs notification = Assert.Single(notifications);
        Assert.Equal(NotificationType.Warning, notification.NotificationType);
        Assert.Contains("non-finite", notification.Message, StringComparison.Ordinal);

        // Nothing of the sort may reach the markup: rx="Infinity" is not valid SVG.
        using SvgDrawingSurface svg = new(configuration, new SurfaceRect(0, 0, 100, 100), null, null);
        dispatcher.Draw(CreateContext(svg, configuration), WithHandle(NonFiniteArc(), 0x1FA));

        XDocument document = svg.ToDocument();
        Assert.Empty(document.Descendants(SvgDrawingSurface.Ns + "ellipse"));
        Assert.Empty(document.Descendants(SvgDrawingSurface.Ns + "circle"));
        Assert.Empty(document.Descendants(SvgDrawingSurface.Ns + "path"));
    }
}
