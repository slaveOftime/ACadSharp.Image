using System.Reflection;
using System.Xml.Linq;
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Image.Rendering;
using ACadSharp.Image.Rendering.Svg;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class EntityRenderDispatcherTests
{
    internal static ImageRenderContext CreateContext(IDrawingSurface surface, ImageConfiguration configuration)
    {
        Layout layout = new("test") { PaperWidth = 100, PaperHeight = 100 };
        return new ImageRenderContext(surface, configuration, layout, 100, 100, 0, 0, 1d, 0, 0, singlePrecision: false, lineTypeScale: 1d);
    }

    // ACadSharp.CadObject.Handle has an internal setter in ACadSharp 3.7.1, so tests
    // that need a deterministic handle assign it via reflection instead.
    private static T WithHandle<T>(T entity, ulong handle)
        where T : CadObject
        => SyntheticSamples.WithHandle(entity, handle);

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

        // The layer-0 line is ByLayer, so it is drawn with the effective (insert) layer's colour, not layer 0's.
        Assert.Equal(SixLabors.ImageSharp.Color.FromRgb(255, 0, 0), surface.Styles[0].StrokeColor);
    }

    [Fact]
    public void NestedEntitiesResolveByLayerAgainstTheEffectiveLayerAndByBlockAgainstTheInsert()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        CadDocument document = new();
        document.Header.LineTypeScale = 2d;
        LineType dashed = new("DASHED");
        dashed.AddSegment(new LineType.Segment { Length = 1 });
        dashed.AddSegment(new LineType.Segment { Length = -1 });
        document.LineTypes.Add(dashed);
        Layer doors = new("Doors") { Color = new ACadSharp.Color(1), LineWeight = LineWeightType.W50, LineType = dashed };
        document.Layers.Add(doors);
        Layer hardware = new("Hardware") { Color = new ACadSharp.Color(5), LineWeight = LineWeightType.W100 };
        document.Layers.Add(hardware);

        BlockRecord block = new("DOOR");
        document.BlockRecords.Add(block);
        // (a) layer 0, everything ByLayer: takes the insert's layer, including its dashed linetype and weight.
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0)) { Layer = document.Layers[Layer.DefaultName] });
        // (b) everything ByBlock: takes the insert's own resolved attributes.
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(0, 10, 0)) { Color = ACadSharp.Color.ByBlock, LineWeight = LineWeightType.ByBlock, LineType = document.LineTypes.ByBlock });
        // (c) an explicit layer keeps its own attributes.
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)) { Layer = hardware });

        Insert insert = new(block) { Layer = doors, Color = new ACadSharp.Color(3), LineWeight = LineWeightType.W200, LineType = document.LineTypes.Continuous, LineTypeScale = 1.5 };
        document.Entities.Add(insert);

        dispatcher.Draw(CreateContext(surface, configuration), insert);

        // Styles are recorded per drawing call: (a), (b), (c); the insert itself draws nothing.
        Assert.Equal(3, surface.Styles.Count);
        ImageStyle layerZero = surface.Styles[0];
        Assert.Equal(SixLabors.ImageSharp.Color.FromRgb(255, 0, 0), layerZero.StrokeColor);
        Assert.Equal(configuration.GetLineWeightPixels(LineWeightType.W50), layerZero.StrokeWidth);
        // LTSCALE 2 (header, reached through the insert since the clone has no document) x CELTSCALE 1.5 (insert) x 1.
        Assert.NotNull(layerZero.DashPattern);
        Assert.Equal([3f, 3f], layerZero.DashPattern);

        ImageStyle byBlock = surface.Styles[1];
        Assert.Equal(SixLabors.ImageSharp.Color.FromRgb(0, 255, 0), byBlock.StrokeColor);
        Assert.Equal(configuration.GetLineWeightPixels(LineWeightType.W200), byBlock.StrokeWidth);
        Assert.Null(byBlock.DashPattern);

        ImageStyle own = surface.Styles[2];
        Assert.Equal(SixLabors.ImageSharp.Color.FromRgb(0, 0, 255), own.StrokeColor);
        Assert.Equal(configuration.GetLineWeightPixels(LineWeightType.W100), own.StrokeWidth);
    }

    [Fact]
    public void TopLevelByBlockDrawsAsColourSeven()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { BackgroundColor = SixLabors.ImageSharp.Color.Black };
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(CreateContext(surface, configuration), new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)) { Color = ACadSharp.Color.ByBlock, LineWeight = LineWeightType.ByBlock });

        ImageStyle style = Assert.Single(surface.Styles);
        Assert.Equal(SixLabors.ImageSharp.Color.White, style.StrokeColor);
        Assert.Equal(configuration.GetLineWeightPixels(LineWeightType.Default), style.StrokeWidth);
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
    public void LayerColourIndexSevenFollowsTheBackground()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { BackgroundColor = SixLabors.ImageSharp.Color.FromRgb(20, 20, 40) };
        EntityRenderDispatcher dispatcher = new(configuration);
        Line line = new(new XYZ(0, 0, 0), new XYZ(10, 0, 0)) { Layer = new Layer("Ink") { Color = new ACadSharp.Color(7) } };

        dispatcher.Draw(CreateContext(surface, configuration), line);

        // Colour index 7 is "ByBackground": on a dark sheet the layer group is white, not black.
        Assert.Equal(SixLabors.ImageSharp.Color.White, Assert.Single(surface.Layers).Color);
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

    private static Hatch SquareHatch(bool solid)
    {
        Hatch hatch = new();
        Hatch.BoundaryPath path = new();
        Hatch.BoundaryPath.Polyline polyline = new() { IsClosed = true };
        polyline.Vertices.AddRange([new XYZ(0, 0, 0), new XYZ(10, 0, 0), new XYZ(10, 10, 0), new XYZ(0, 10, 0)]);
        path.Edges.Add(polyline);
        hatch.Paths.Add(path);
        if (solid)
        {
            hatch.IsSolid = true;
            hatch.PatternType = HatchPatternType.SolidFill;
            hatch.Pattern = HatchPattern.Solid;
        }
        else
        {
            hatch.IsSolid = false;
            hatch.PatternType = HatchPatternType.PatternFill;
            hatch.Pattern = new HatchPattern("ANSI31");
            hatch.Pattern.Lines.Add(new HatchPattern.Line { Angle = Math.PI / 4, BasePoint = XY.Zero, Offset = new XY(0, 3.175) });
            hatch.PatternScale = 1;
        }

        return hatch;
    }

    private static Hatch.BoundaryPath SquarePath(double x0, double y0, double x1, double y1)
    {
        Hatch.BoundaryPath path = new();
        path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(x0, y0), End = new XY(x1, y0) });
        path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(x1, y0), End = new XY(x1, y1) });
        path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(x1, y1), End = new XY(x0, y1) });
        path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(x0, y1), End = new XY(x0, y0) });
        return path;
    }

    [Fact]
    public void SolidHatchFillsBoundaryRings()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(CreateContext(surface, configuration), SquareHatch(solid: true));

        Assert.Contains("FillPath rings=1", surface.Calls);
        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("DrawLine", StringComparison.Ordinal));
    }

    [Fact]
    public void PatternHatchDrawsClippedLines()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(CreateContext(surface, configuration), SquareHatch(solid: false));

        int lines = surface.Calls.Count(c => c.StartsWith("DrawLine", StringComparison.Ordinal));
        Assert.InRange(lines, 5, 9); // 45-degree lines 3.175 apart across a 10x10 square
        Assert.All(surface.Styles, s => Assert.Null(s.DashPattern));
    }

    [Fact]
    public void PatternHatchIsCappedWithWarning()
    {
        // A dashed pattern emits several segments per scan line, so the segment cap trips even though the scan-line
        // preflight (about a dozen scan lines for this square) lets the expansion run.
        Hatch hatch = SquareHatch(solid: false);
        hatch.Pattern!.Lines[0].DashLengths.AddRange([1d, -1d]);
        int cap = (int)Math.Ceiling(EntityRenderDispatcher.EstimateScanLines(hatch));

        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { MaxHatchLines = cap };
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(CreateContext(surface, configuration), hatch);

        Assert.Equal(cap, surface.Calls.Count(c => c.StartsWith("DrawLine", StringComparison.Ordinal)));
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("remaining lines were skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void ArbitraryAxisFrameMapsOcsPointsIntoWorld()
    {
        // The classic case: a (0,0,-1) extrusion mirrors X.
        OcsTransform flipped = OcsTransform.For(new XYZ(0, 0, -1));
        XY mirrored = flipped.ToWorldXY(1, 2, 0);
        Assert.Equal(-1d, mirrored.X, 9);
        Assert.Equal(2d, mirrored.Y, 9);

        // A vertical plane: OCS Y becomes world Z, so the elevation lands in world Y.
        OcsTransform vertical = OcsTransform.For(new XYZ(0, 1, 0));
        XY lifted = vertical.ToWorldXY(1, 2, 3);
        Assert.Equal(-1d, lifted.X, 9);
        Assert.Equal(3d, lifted.Y, 9);

        // Whatever the tilt, the frame is orthonormal and its Z axis is the unit normal.
        OcsTransform tilted = OcsTransform.For(new XYZ(2, 2, 2));
        double unit = 1d / Math.Sqrt(3);
        XYZ mappedZ = tilted.ToWorld(0, 0, 1);
        Assert.Equal(unit, mappedZ.X, 9);
        Assert.Equal(unit, mappedZ.Y, 9);
        Assert.Equal(unit, mappedZ.Z, 9);
        Assert.Equal(1d, Length(tilted.AxisX), 9);
        Assert.Equal(1d, Length(tilted.AxisY), 9);
        Assert.Equal(0d, Dot(tilted.AxisX, tilted.AxisY), 9);
        Assert.Equal(0d, Dot(tilted.AxisX, tilted.Normal), 9);
        Assert.Equal(0d, Dot(tilted.AxisY, tilted.Normal), 9);

        Assert.True(OcsTransform.IsWorldPlane(XYZ.AxisZ));
        Assert.False(OcsTransform.IsWorldPlane(tilted.Normal));

        static double Length(XYZ v) => Math.Sqrt(Dot(v, v));
        static double Dot(XYZ a, XYZ b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
    }

    [Fact]
    public void TextInsideAMirroredInsertStaysWithItsGeometry()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        BlockRecord block = new("LABEL");
        block.Entities.Add(new Line(new XYZ(1, 2, 0), new XYZ(3, 2, 0)));
        block.Entities.Add(new TextEntity { Value = "T", InsertPoint = new XYZ(1, 2, 0), Height = 1 });
        Insert insert = new(block) { InsertPoint = new XYZ(10, 0, 0), XScale = -1 };

        dispatcher.Draw(CreateContext(surface, configuration), insert);

        // The line starts at world x = 9; the text shares that point, and its glyphs read the mirrored extent from its end.
        (SurfacePoint start, _) = Assert.Single(surface.Lines);
        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal(9d, start.X, 6);
        Assert.Equal(start.X, run.Origin.X, 6);
        Assert.Equal(start.Y, run.Origin.Y, 6);
        Assert.Equal(SurfaceTextAnchor.End, run.Anchor);
        Assert.Equal(1d, Math.Cos(run.Rotation), 6);
    }

    [Fact]
    public void AlignedTextInsideAnInsertIsTranslatedWithIt()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        BlockRecord block = new("TAG");
        block.Entities.Add(new TextEntity { Value = "R", InsertPoint = new XYZ(1, 2, 0), AlignmentPoint = new XYZ(4, 2, 0), HorizontalAlignment = TextHorizontalAlignment.Right, Height = 1 });
        Insert insert = new(block) { InsertPoint = new XYZ(10, 0, 0) };

        dispatcher.Draw(CreateContext(surface, configuration), insert);

        // ACadSharp leaves the clone's alignment point at (4,2); the renderer places the original through the insert.
        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal(14d, run.Origin.X, 6);
        Assert.Equal(100d - 2d, run.Origin.Y, 6);
        Assert.Equal(SurfaceTextAnchor.End, run.Anchor);
    }

    [Fact]
    public void TextInsideANestedInsertIsPlacedThroughBothTransforms()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        BlockRecord inner = new("INNER");
        inner.Entities.Add(new TextEntity { Value = "N", InsertPoint = new XYZ(1, 2, 0), AlignmentPoint = new XYZ(3, 2, 0), HorizontalAlignment = TextHorizontalAlignment.Right, Height = 1 });
        BlockRecord outer = new("OUTER");
        outer.Entities.Add(new Insert(inner) { InsertPoint = new XYZ(5, 0, 0) });
        Insert insert = new(outer) { InsertPoint = new XYZ(10, 0, 0) };

        dispatcher.Draw(CreateContext(surface, configuration), insert);

        // (3,2) + (5,0) + (10,0): the alignment point travels through both inserts.
        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal(18d, run.Origin.X, 6);
        Assert.Equal(100d - 2d, run.Origin.Y, 6);
        Assert.Equal(SurfaceTextAnchor.End, run.Anchor);
    }

    [Fact]
    public void TextAndMTextInsideARotatedScaledInsertFollowIt()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        BlockRecord block = new("ROT");
        block.Entities.Add(new TextEntity { Value = "T", InsertPoint = new XYZ(1, 2, 0), Height = 1 });
        block.Entities.Add(new MText { Value = "M", InsertPoint = new XYZ(1, 5, 0), Height = 1, AlignmentPoint = new XYZ(1, 0, 0) });
        Insert insert = new(block) { InsertPoint = new XYZ(10, 0, 0), Rotation = Math.PI / 2, XScale = 2, YScale = 2 };

        dispatcher.Draw(CreateContext(surface, configuration), insert);

        Assert.Equal(2, surface.Texts.Count);
        SurfaceText text = surface.Texts[0];
        // (1,2) scaled by 2 and rotated a quarter turn about the origin, then moved to (10,0): (10 - 4, 2) = (6, 2).
        Assert.Equal(6d, text.Origin.X, 6);
        Assert.Equal(100d - 2d, text.Origin.Y, 6);
        Assert.Equal(0d, Math.Cos(text.Rotation), 6);
        Assert.Equal(1d, Math.Sin(text.Rotation), 6);
        Assert.Equal(2d, text.Height, 6);
        Assert.Equal(SurfaceTextAnchor.Start, text.Anchor);
        // A uniform insert scale (XScale == YScale) must leave WidthScale at 1: the reading-axis and up-axis
        // lengths the insert transform produces are equal, so p.WidthScale / p.Scale reduces to 1.
        Assert.Equal(1d, text.WidthScale, 9);

        SurfaceText mtext = surface.Texts[1];
        Assert.Equal(0d, Math.Cos(mtext.Rotation), 6);
        Assert.Equal(1d, Math.Sin(mtext.Rotation), 6);
        Assert.Equal(2d, mtext.Height, 6);
        Assert.Equal(1d, mtext.WidthScale, 9);
    }

    [Fact]
    public void HatchInsideAMirroredInsertStaysWithItsGeometry()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        BlockRecord block = new("HATCHED");
        Hatch hatch = SquareHatch(solid: true);
        block.Entities.Add(hatch);
        Insert insert = new(block) { InsertPoint = new XYZ(10, 0, 0), XScale = -1 };

        dispatcher.Draw(CreateContext(surface, configuration), insert);

        // This hatch's normal is the world Z axis, so it is drawn from the original's own boundary points (already
        // world) mapped straight through the insert's placement: the 0..10 square mirrored about x = 10 spans 0..10
        // again. The expectation is invariant across both the exploded-clone path and the current original-entity
        // path, which is why this test alone would not have caught the pattern-angle mirroring bug the clone path
        // had — see APatternHatchInsideAMirroredInsertMirrorsItsPatternAngle below for that.
        IReadOnlyList<SurfacePoint> ring = Assert.Single(Assert.Single(surface.FillPaths));
        Assert.Equal(0d, ring.Min(p => p.X), 6);
        Assert.Equal(10d, ring.Max(p => p.X), 6);
    }

    [Fact]
    public void APatternHatchInsideAMirroredInsertMirrorsItsPatternAngle()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("PATTERN");
        Hatch hatch = SquareHatch(solid: false);
        block.Entities.Add(hatch);
        Insert insert = new(block) { XScale = -1 };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        // SquareHatch's pattern line runs at Angle = Math.PI/4 (45 degrees) in the hatch's own space, direction
        // (cos45, sin45) = (a, a). Drawing from the original means ExplodePattern() sees that unmirrored angle and
        // the mirror (XScale = -1) is applied afterwards, through the placement, negating only X: (-a, a), a
        // world-space slope of a / -a = -1 (the old exploded-clone path left Pattern.Angle unmirrored at 0.7854 and
        // so drew the unmirrored +1 slope instead — the bug this task fixes for pattern hatches). The renderer's Y
        // flip (ImageRenderContext.ToSurfacePoint: surfaceY = SurfaceHeight - worldY) negates the Y delta again
        // without touching X, so on the surface — what surface.Lines records — every drawn segment has slope +1.
        Assert.NotEmpty(surface.Lines);
        Assert.All(surface.Lines, l =>
        {
            double dx = l.End.X - l.Start.X;
            double dy = l.End.Y - l.Start.Y;
            Assert.True(Math.Abs(dx) > 1e-6, "pattern line unexpectedly vertical in surface space");
            Assert.Equal(1d, dy / dx, 6);
        });
    }

    [Fact]
    public void NonWorldPolylineIsTessellatedAndBroughtIntoWorld()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = true };
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        LwPolyline polyline = new();
        polyline.Vertices.Add(new LwPolyline.Vertex(new XY(1, 0)) { Bulge = 1 });
        polyline.Vertices.Add(new LwPolyline.Vertex(new XY(3, 0)));
        polyline.Normal = new XYZ(0, 0, -1);

        dispatcher.Draw(CreateContext(surface, configuration), polyline);

        // Bulges only describe circular arcs on the world plane; a mirrored polyline is tessellated, then mirrored.
        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("DrawBulgePolyline", StringComparison.Ordinal));
        IReadOnlyList<SurfacePoint> points = Assert.Single(surface.Polylines);
        Assert.True(points.Count > 2);
        Assert.Equal(-1d, points[0].X, 3);
        Assert.Equal(-3d, points[^1].X, 3);
        Assert.All(points, p => Assert.InRange(p.X, -3.01, -0.99));

        // On the world plane nothing changes: the bulge reaches a curve-capable surface intact.
        surface.Calls.Clear();
        polyline.Normal = XYZ.AxisZ;
        dispatcher.Draw(CreateContext(surface, configuration), polyline);
        Assert.Contains(surface.Calls, c => c.StartsWith("DrawBulgePolyline", StringComparison.Ordinal));
    }

    [Fact]
    public void NonWorldHatchIsBroughtIntoWorld()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        Hatch solid = SquareHatch(solid: true);
        solid.Normal = new XYZ(0, 0, -1);
        dispatcher.Draw(CreateContext(surface, configuration), solid);

        IReadOnlyList<SurfacePoint> ring = Assert.Single(Assert.Single(surface.FillPaths));
        Assert.Equal(-10d, ring.Min(p => p.X), 6);
        Assert.Equal(0d, ring.Max(p => p.X), 6);

        Hatch pattern = SquareHatch(solid: false);
        pattern.Normal = new XYZ(0, 0, -1);
        dispatcher.Draw(CreateContext(surface, configuration), pattern);

        Assert.NotEmpty(surface.Lines);
        Assert.All(surface.Lines, l =>
        {
            Assert.InRange(l.Start.X, -10.001, 0.001);
            Assert.InRange(l.End.X, -10.001, 0.001);
        });
    }

    [Fact]
    public void ATiltedHatchInsideAnInsertIsMappedThroughItsOwnOcsThenTheInsertTransform()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("TILT");
        Hatch hatch = new() { IsSolid = true, Normal = new XYZ(0, 0, -1), Elevation = 0d };
        hatch.Paths.Add(SquarePath(0, 0, 10, 10));
        block.Entities.Add(hatch);
        Insert insert = new(block) { InsertPoint = new XYZ(20, 0, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        // Normal (0,0,-1) mirrors X going OCS to world, so the square spans x in [-10,0]; the insert then adds 20.
        IReadOnlyList<SurfacePoint> ring = Assert.Single(Assert.Single(surface.FillPaths));
        Assert.Equal(10d, ring.Min(p => p.X), 6);
        Assert.Equal(20d, ring.Max(p => p.X), 6);
    }

    [Fact]
    public void ATiltedHatchAtTopLevelIsUnchanged()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Hatch hatch = new() { IsSolid = true, Normal = new XYZ(0, 0, -1), Elevation = 0d };
        hatch.Paths.Add(SquarePath(0, 0, 10, 10));

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), hatch);

        IReadOnlyList<SurfacePoint> ring = Assert.Single(Assert.Single(surface.FillPaths));
        Assert.Equal(-10d, ring.Min(p => p.X), 6);
        Assert.Equal(0d, ring.Max(p => p.X), 6);
    }

    [Fact]
    public void AHatchInsideAMirroredInsertKeepsItsExtent()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("MIRROR");
        Hatch hatch = new() { IsSolid = true, Normal = XYZ.AxisZ, Elevation = 0d };
        hatch.Paths.Add(SquarePath(0, 0, 10, 10));
        block.Entities.Add(hatch);
        Insert insert = new(block) { InsertPoint = new XYZ(50, 0, 0), XScale = -1 };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        IReadOnlyList<SurfacePoint> ring = Assert.Single(Assert.Single(surface.FillPaths));
        Assert.Equal(40d, ring.Min(p => p.X), 6);
        Assert.Equal(50d, ring.Max(p => p.X), 6);
    }

    [Fact]
    public void ATiltedHatchWithAnElevationIsPlacedAlongItsOwnNormal()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Hatch hatch = new() { IsSolid = true, Normal = new XYZ(0, 1, 0), Elevation = 5d };
        hatch.Paths.Add(SquarePath(0, 0, 10, 10));

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), hatch);

        // Normal (0,1,0): the OCS X axis is world -X and the OCS Y axis is world +Z, so the square's Y collapses to a
        // constant world Y = +5 (the elevation along the normal); after the surface Y flip (SurfaceHeight - worldY)
        // that lands at 95, so the elevation reaches the output rather than being dropped as it was for a clone.
        IReadOnlyList<SurfacePoint> ring = Assert.Single(Assert.Single(surface.FillPaths));
        Assert.All(ring, p => Assert.Equal(95d, p.Y, 6));
    }

    [Fact]
    public void DensePatternHatchIsSkippedBeforeExpansion()
    {
        Hatch hatch = SquareHatch(solid: false);
        double scanLines = EntityRenderDispatcher.EstimateScanLines(hatch);
        Assert.InRange(scanLines, 6, 40);

        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { MaxHatchLines = 5 };
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(CreateContext(surface, configuration), hatch);

        Assert.Empty(surface.Lines);
        NotificationEventArgs notification = Assert.Single(notifications);
        Assert.Equal(NotificationType.Warning, notification.NotificationType);
        Assert.Contains("hatch skipped", notification.Message, StringComparison.Ordinal);

        // Above the estimate the pattern is expanded and drawn as before.
        configuration.MaxHatchLines = 50;
        dispatcher.Draw(CreateContext(surface, configuration), hatch);
        Assert.NotEmpty(surface.Lines);
        Assert.Single(notifications);
    }

    [Fact]
    public void NonWorldSolidIsBroughtIntoWorld()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        Solid solid = new()
        {
            FirstCorner = new XYZ(1, 0, 0),
            SecondCorner = new XYZ(3, 0, 0),
            ThirdCorner = new XYZ(1, 2, 0),
            FourthCorner = new XYZ(3, 2, 0),
            Normal = new XYZ(0, 0, -1),
        };

        dispatcher.Draw(CreateContext(surface, configuration), solid);

        // A (0,0,-1) extrusion mirrors X: the solid must land on x in [-3, -1], not [1, 3].
        IReadOnlyList<SurfacePoint> points = Assert.Single(surface.Polygons);
        Assert.Equal(4, points.Count);
        Assert.Equal(-1d, points.Max(p => p.X), 9);
        Assert.Equal(-3d, points.Min(p => p.X), 9);

        // The default normal leaves the corners untouched.
        solid.Normal = XYZ.AxisZ;
        dispatcher.Draw(CreateContext(surface, configuration), solid);
        Assert.Equal(1d, surface.Polygons[1].Min(p => p.X), 9);
        Assert.Equal(3d, surface.Polygons[1].Max(p => p.X), 9);
    }

    [Fact]
    public void SolidCornersAreFilledInDxfOrder()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        // Corners in the DXF Z pattern: bottom-left, bottom-right, top-left, top-right.
        Solid solid = new()
        {
            FirstCorner = new XYZ(0, 0, 0),
            SecondCorner = new XYZ(10, 0, 0),
            ThirdCorner = new XYZ(0, 5, 0),
            FourthCorner = new XYZ(10, 5, 0),
        };

        dispatcher.Draw(CreateContext(surface, configuration), solid);

        // Filled as 1-2-4-3, the outline is a rectangle; as 1-2-3-4 it would be a bow-tie.
        IReadOnlyList<SurfacePoint> points = Assert.Single(surface.Polygons);
        Assert.Equal([0d, 10d, 10d, 0d], points.Select(p => p.X).ToArray());
        Assert.Equal([100d, 100d, 95d, 95d], points.Select(p => p.Y).ToArray());
    }

    [Fact]
    public void OcsSolidInsideAnInsertAppliesTheNormalBeforeTheInsertTransform()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("PLATE");
        block.Entities.Add(new Solid { FirstCorner = new XYZ(0, 0, 0), SecondCorner = new XYZ(10, 0, 0), ThirdCorner = new XYZ(0, 5, 0), FourthCorner = new XYZ(10, 5, 0), Normal = new XYZ(0, 0, -1) });
        Insert insert = new(block) { InsertPoint = new XYZ(20, 0, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        // Normal (0,0,-1) mirrors X in OCS→world: corners x in [-10,0]; then the insert moves them by +20: x in [10,20].
        IReadOnlyList<SurfacePoint> polygon = Assert.Single(surface.Polygons);
        Assert.Equal(new HashSet<SurfacePoint> { new(20, 100), new(10, 100), new(10, 95), new(20, 95) }, polygon.ToHashSet());
    }

    [Fact]
    public void MalformedPolylineIsSkippedWithWarningAndSubsequentEntitiesStillDraw()
    {
        // Two coincident vertices joined by a bulge make ACadSharp's tessellating GetPoints throw.
        RecordingDrawingSurface surface = new() { SupportsCurves = false };
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        EntityRenderDispatcher dispatcher = new(configuration);

        LwPolyline malformed = new();
        malformed.Vertices.Add(new LwPolyline.Vertex(new XY(5, 5)) { Bulge = 1 });
        malformed.Vertices.Add(new LwPolyline.Vertex(new XY(5, 5)));
        malformed.Vertices.Add(new LwPolyline.Vertex(new XY(8, 5)));

        dispatcher.Draw(CreateContext(surface, configuration), WithHandle(malformed, 0x2A));

        Assert.Equal(0, surface.Depth);
        NotificationEventArgs notification = Assert.Single(notifications);
        Assert.Equal(NotificationType.Warning, notification.NotificationType);
        Assert.Contains("entity skipped", notification.Message, StringComparison.Ordinal);

        Line line = WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0)), 0x2B);
        dispatcher.Draw(CreateContext(surface, configuration), line);

        Assert.Contains(surface.Calls, c => c.StartsWith("DrawLine", StringComparison.Ordinal));
    }

    [Fact]
    public void Face3DWithAllEdgesVisibleIsOneClosedPolyline()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Face3D face = new()
        {
            FirstCorner = new XYZ(0, 0, 0),
            SecondCorner = new XYZ(10, 0, 0),
            ThirdCorner = new XYZ(10, 10, 0),
            FourthCorner = new XYZ(0, 10, 0),
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), face);

        Assert.Equal(["DrawPolyline n=4 closed=True"], surface.Calls.Where(c => c.StartsWith("Draw", StringComparison.Ordinal)));
        Assert.Equal(new SurfacePoint(0, 100), surface.Polylines[0][0]);
        Assert.Equal(new SurfacePoint(0, 90), surface.Polylines[0][3]);
        Assert.Empty(surface.Polygons);
    }

    [Fact]
    public void Face3DSkipsInvisibleEdgesAndKeepsTheVisibleRunsJoined()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        // Edges 2 (10,0)->(10,10) and 4 (0,10)->(0,0) hidden: two separate open edges remain.
        Face3D face = new()
        {
            FirstCorner = new XYZ(0, 0, 0),
            SecondCorner = new XYZ(10, 0, 0),
            ThirdCorner = new XYZ(10, 10, 0),
            FourthCorner = new XYZ(0, 10, 0),
            Flags = InvisibleEdgeFlags.Second | InvisibleEdgeFlags.Fourth,
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), face);

        Assert.Equal(2, surface.Polylines.Count);
        Assert.All(surface.Calls.Where(c => c.StartsWith("DrawPolyline", StringComparison.Ordinal)), c => Assert.EndsWith("closed=False", c));
        Assert.Contains(surface.Polylines, p => p.SequenceEqual([new SurfacePoint(10, 90), new SurfacePoint(0, 90)]));
        Assert.Contains(surface.Polylines, p => p.SequenceEqual([new SurfacePoint(0, 100), new SurfacePoint(10, 100)]));
    }

    [Fact]
    public void Face3DWithOneHiddenEdgeIsOneOpenRunOfThreeEdges()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Face3D face = new()
        {
            FirstCorner = new XYZ(0, 0, 0),
            SecondCorner = new XYZ(10, 0, 0),
            ThirdCorner = new XYZ(10, 10, 0),
            FourthCorner = new XYZ(0, 10, 0),
            Flags = InvisibleEdgeFlags.Third,
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), face);

        IReadOnlyList<SurfacePoint> run = Assert.Single(surface.Polylines);
        // Starts after the hidden edge: 4 -> 1 -> 2 -> 3.
        Assert.Equal([new SurfacePoint(0, 90), new SurfacePoint(0, 100), new SurfacePoint(10, 100), new SurfacePoint(10, 90)], run);
    }

    [Fact]
    public void TriangularFace3DDropsTheDegenerateEdge()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Face3D face = new()
        {
            FirstCorner = new XYZ(0, 0, 0),
            SecondCorner = new XYZ(10, 0, 0),
            ThirdCorner = new XYZ(10, 10, 0),
            FourthCorner = new XYZ(10, 10, 0),
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), face);

        Assert.Equal(["DrawPolyline n=3 closed=True"], surface.Calls.Where(c => c.StartsWith("Draw", StringComparison.Ordinal)));
    }

    private static Insert InsertWithAttribute(string value, AttributeFlags flags, out BlockRecord block)
    {
        block = new BlockRecord("TAGGED");
        block.Entities.Add(new AttributeDefinition { Tag = "ROOM", Value = "DEFAULT", InsertPoint = new XYZ(1, 1, 0), Height = 2, Flags = flags });
        // Insert(BlockRecord) creates one AttributeEntity per ATTDEF at the identity transform; place it explicitly.
        Insert insert = new(block) { InsertPoint = new XYZ(10, 0, 0) };
        AttributeEntity attribute = Assert.Single(insert.Attributes);
        attribute.Value = value;
        attribute.InsertPoint = new XYZ(15, 5, 0);
        attribute.Flags = flags;
        return insert;
    }

    [Fact]
    public void InsertDrawsItsAttributesAndNotTheDefinitionDefaults()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Insert insert = WithHandle(InsertWithAttribute("A-101", AttributeFlags.None, out _), 0xAB);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        SurfaceText text = Assert.Single(surface.Texts);
        Assert.Equal("A-101", text.Text);
        Assert.Equal(new SurfacePoint(15, 95), text.Origin);
        Assert.DoesNotContain(surface.Texts, t => t.Text == "DEFAULT");
        EntityRenderInfo info = surface.Entities.Single(e => e.EntityType == insert.Attributes.First().ObjectName);
        Assert.Equal(0xABUL, info.ParentHandle);
        Assert.Equal("TAGGED", info.BlockName);
    }

    [Fact]
    public void ConstantAttributeDefinitionsAreStillDrawn()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("CONST");
        block.Entities.Add(new AttributeDefinition { Tag = "MAKER", Value = "ACME", InsertPoint = new XYZ(1, 1, 0), Height = 2, Flags = AttributeFlags.Constant });
        Insert insert = new(block) { InsertPoint = new XYZ(10, 0, 0) };
        // Insert has no parameterless constructor and Block has no public setter in ACadSharp 3.7.1 (verified by
        // probe), so an insert with no ATTRIB is built via Insert(BlockRecord) and then Attributes.Clear(), reproducing
        // a file where a constant attribute was never persisted as an ATTRIB. Without the Clear(), Insert(BlockRecord)
        // would already have created an AttributeEntity for the constant ATTDEF, and the assertion below would pass
        // even if the explode-time fallback that reads the value from the ATTDEF itself were broken.
        insert.Attributes.Clear();
        Assert.Empty(insert.Attributes);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Single(surface.Texts, t => t.Text == "ACME");
    }

    [Theory]
    [InlineData(LayerVisibilityMode.All, AttributeVisibilityMode.Normal, true)]
    [InlineData(LayerVisibilityMode.Screen, AttributeVisibilityMode.Normal, false)]
    [InlineData(LayerVisibilityMode.Screen, AttributeVisibilityMode.All, true)]
    [InlineData(LayerVisibilityMode.Screen, AttributeVisibilityMode.None, false)]
    public void HiddenAttributesFollowAttmodeUnlessEverythingIsShown(LayerVisibilityMode layerMode, AttributeVisibilityMode attmode, bool drawn)
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { LayerVisibility = layerMode };
        Insert insert = InsertWithAttribute("SECRET", AttributeFlags.Hidden, out BlockRecord block);
        CadDocument document = new();
        document.Header.AttributeVisibility = attmode;
        document.BlockRecords.Add(block);
        document.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Equal(drawn, surface.Texts.Any(t => t.Text == "SECRET"));
    }

    [Fact]
    public void VisibleAttributeIsDrawnUnderNormalAttmode()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { LayerVisibility = LayerVisibilityMode.Screen };
        Insert insert = InsertWithAttribute("SHOWN", AttributeFlags.None, out BlockRecord block);
        CadDocument document = new();
        document.BlockRecords.Add(block);
        document.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Contains(surface.Texts, t => t.Text == "SHOWN");
    }

    [Fact]
    public void NestedInsertAttributesFollowTheOuterInsertsDocumentAttmode()
    {
        // A nested Insert exploded out of an outer block's contents carries no Document of its own; its ATTMODE
        // must still come from the outer insert's document, not fall back to Normal.
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { LayerVisibility = LayerVisibilityMode.Screen };

        BlockRecord innerBlock = new("INNER_BLK");
        innerBlock.Entities.Add(new AttributeDefinition { Tag = "X", Value = "DEFAULT_INNER", InsertPoint = new XYZ(0, 0, 0), Height = 2, Flags = AttributeFlags.None });
        Insert innerInsert = new(innerBlock) { InsertPoint = new XYZ(0, 0, 0) };
        AttributeEntity innerAttribute = Assert.Single(innerInsert.Attributes);
        innerAttribute.Value = "INNER";
        innerAttribute.InsertPoint = new XYZ(0, 0, 0);
        innerAttribute.Flags = AttributeFlags.None;

        BlockRecord outerBlock = new("OUTER_BLK");
        outerBlock.Entities.Add(innerInsert);
        Insert outerInsert = new(outerBlock) { InsertPoint = new XYZ(10, 0, 0) };

        CadDocument document = new();
        document.Header.AttributeVisibility = AttributeVisibilityMode.None;
        document.BlockRecords.Add(innerBlock);
        document.BlockRecords.Add(outerBlock);
        document.Entities.Add(outerInsert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), outerInsert);

        Assert.DoesNotContain(surface.Texts, t => t.Text == "INNER");
    }

    [Fact]
    public void ConstantAttributeDefinitionFollowsAttmodeWhenDrawnFromTheExplodePath()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { LayerVisibility = LayerVisibilityMode.Screen };
        BlockRecord block = new("CONST3");
        block.Entities.Add(new AttributeDefinition { Tag = "MAKER", Value = "ACME3", InsertPoint = new XYZ(1, 1, 0), Height = 2, Flags = AttributeFlags.Constant });
        Insert insert = new(block) { InsertPoint = new XYZ(10, 0, 0) };
        insert.Attributes.Clear();

        CadDocument document = new();
        document.Header.AttributeVisibility = AttributeVisibilityMode.None;
        document.BlockRecords.Add(block);
        document.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.DoesNotContain(surface.Texts, t => t.Text == "ACME3");
    }

    [Fact]
    public void HiddenConstantAttributeDefinitionIsNotDrawnUnderNormalAttmode()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { LayerVisibility = LayerVisibilityMode.Screen };
        BlockRecord block = new("CONST4");
        block.Entities.Add(new AttributeDefinition { Tag = "MAKER", Value = "ACME4", InsertPoint = new XYZ(1, 1, 0), Height = 2, Flags = AttributeFlags.Constant | AttributeFlags.Hidden });
        Insert insert = new(block) { InsertPoint = new XYZ(10, 0, 0) };
        insert.Attributes.Clear();

        CadDocument document = new();
        document.Header.AttributeVisibility = AttributeVisibilityMode.Normal;
        document.BlockRecords.Add(block);
        document.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.DoesNotContain(surface.Texts, t => t.Text == "ACME4");
    }

    [Fact]
    public void ConstantAttributeTagMatchIsCaseInsensitive()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("CONST5");
        block.Entities.Add(new AttributeDefinition { Tag = "Maker", Value = "ACME5", InsertPoint = new XYZ(1, 1, 0), Height = 2, Flags = AttributeFlags.Constant });
        Insert insert = new(block) { InsertPoint = new XYZ(10, 0, 0) };
        AttributeEntity attribute = Assert.Single(insert.Attributes);
        // DXF attribute tags are case-insensitive identifiers; the ATTDEF's own tag casing differs from the ATTRIB's.
        attribute.Tag = "MAKER";

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Single(surface.Texts, t => t.Text == "ACME5");
    }

    [Fact]
    public void ConstantMultiLineAttributeDefinitionIsPlacedThroughTheInsertTransform()
    {
        // Constant multi-line ATTDEFs reach EntityRenderDispatcher.Draw through the block-explode path in
        // DrawBlockContents, not through DrawAttributes: UsesOriginalGeometry already treats AttributeDefinition
        // as a TextEntity subclass and hands it the insert's transform as placement, the same as a block TEXT,
        // MTEXT or Leader. This pins that the new multi-line arm honours that placement instead of only working
        // at top level (where DrawAttributes always passes null).
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("CONSTML");
        block.Entities.Add(new AttributeDefinition
        {
            Tag = "ROOM",
            Value = "WRONG",
            AttributeType = AttributeType.ConstantMultiLine,
            Flags = AttributeFlags.Constant,
            InsertPoint = new XYZ(1, 1, 0),
            Height = 2,
            MText = new MText { Value = "Line1\\PLine2", InsertPoint = new XYZ(2, 3, 0), Height = 4 },
        });
        Insert insert = new(block) { InsertPoint = new XYZ(20, 10, 0), XScale = 2, YScale = 2, ZScale = 2 };
        // Insert(BlockRecord) auto-creates a matching ATTRIB for the constant ATTDEF, which would suppress the
        // definition in the explode loop (see ConstantAttributeDefinitionsAreStillDrawn); clearing it reproduces
        // a file where the constant attribute was never persisted as its own ATTRIB.
        insert.Attributes.Clear();

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Contains("Line1", run.Text);
        Assert.Contains("Line2", run.Text);
        Assert.DoesNotContain("WRONG", run.Text);
        // The embedded MText's own insertion point (2,3) is scaled by the insert's uniform XScale/YScale (2) and
        // translated by its InsertPoint (20,10): world = (2*2+20, 3*2+10) = (24, 16); CreateContext's 100-unit
        // paper flips Y, so the surface origin is (24, 100-16) = (24, 84).
        Assert.Equal(24d, run.Origin.X, 9);
        Assert.Equal(84d, run.Origin.Y, 9);
        // The MText's own up axis (0,1,0) is likewise scaled by 2, so its height in surface units doubles: 4*2=8.
        Assert.Equal(8d, run.Height, 9);
    }

    [Fact]
    public void StraightLeaderIsOneOpenPolylineWithoutArrow()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Leader leader = new() { Vertices = { new XYZ(0, 0, 0), new XYZ(10, 10, 0), new XYZ(20, 10, 0) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.Equal(["DrawPolyline n=3 closed=False"], surface.Calls.Where(c => c.StartsWith("Draw", StringComparison.Ordinal) || c.StartsWith("Fill", StringComparison.Ordinal)));
        Assert.Equal([new SurfacePoint(0, 100), new SurfacePoint(10, 90), new SurfacePoint(20, 90)], surface.Polylines[0]);
    }

    [Fact]
    public void LeaderArrowheadIsAFilledTriangleAtTheFirstVertex()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        // A fresh style: DimensionStyle.Default may be shared, and tests run in parallel.
        Leader leader = new() { ArrowHeadEnabled = true, Vertices = { new XYZ(0, 0, 0), new XYZ(30, 0, 0) }, Style = new DimensionStyle("ARROW") { ArrowSize = 6, ScaleFactor = 2 } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        IReadOnlyList<SurfacePoint> arrow = Assert.Single(surface.Polygons);
        Assert.Equal(3, arrow.Count);
        Assert.Equal(new SurfacePoint(0, 100), arrow[0]);
        // Length 6 * 2 = 12 along +X (away from the second vertex), half-width 12 / 6 = 2.
        Assert.Contains(arrow, p => Math.Abs(p.X - 12) < 1e-9 && Math.Abs(p.Y - 98) < 1e-9);
        Assert.Contains(arrow, p => Math.Abs(p.X - 12) < 1e-9 && Math.Abs(p.Y - 102) < 1e-9);
    }

    [Fact]
    public void SplinedLeaderIsACubicBezierChainThroughItsVertices()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Leader leader = new() { PathType = LeaderPathType.Spline, Vertices = { new XYZ(0, 0, 0), new XYZ(10, 10, 0), new XYZ(20, 0, 0), new XYZ(30, 10, 0) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.Equal(["DrawCubicBezier n=10 closed=False"], surface.Calls.Where(c => c.StartsWith("Draw", StringComparison.Ordinal)));
    }

    [Fact]
    public void CatmullRomControlPointsInterpolateTheInputPoints()
    {
        SurfacePoint[] points = [new(0, 0), new(10, 10), new(20, 0)];

        SurfacePoint[] controls = EntityRenderDispatcher.CatmullRomToBezier(points);

        Assert.Equal(7, controls.Length);
        Assert.Equal(points[0], controls[0]);
        Assert.Equal(points[1], controls[3]);
        Assert.Equal(points[2], controls[6]);
        // Interior tangent at (10,10) is (P2 - P0) / 6 = (20, 0) / 6.
        Assert.Equal(new SurfacePoint(10 - 20d / 6d, 10), controls[2]);
        Assert.Equal(new SurfacePoint(10 + 20d / 6d, 10), controls[4]);
    }

    [Fact]
    public void LeaderWithCustomArrowBlockFallsBackToTheDefaultArrowWithANotification()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        Leader leader = new() { ArrowHeadEnabled = true, Vertices = { new XYZ(0, 0, 0), new XYZ(30, 0, 0) }, Style = new DimensionStyle("DOTTED") { LeaderArrow = new BlockRecord("_DOT") } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.Single(surface.Polygons);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("_DOT", StringComparison.Ordinal));
    }

    [Fact]
    public void LeaderWithANonFiniteArrowSizeDrawsNoArrowheadAndSaysNothingAboutIt()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        Leader leader = new()
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(0, 0, 0), new XYZ(30, 0, 0) },
            Style = new DimensionStyle("NAN") { ArrowSize = double.NaN, LeaderArrow = new BlockRecord("_DOT") },
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        // Every comparison with NaN is false, so an unguarded size <= 0 test lets a triangle with NaN corners through.
        // The leader line itself is still drawn, and the fallback notification must not claim an arrow nobody drew.
        Assert.Single(surface.Polylines);
        Assert.Empty(surface.Polygons);
        Assert.Empty(notifications);
    }

    [Fact]
    public void LeaderArrowInsideAScaledInsertScalesWithIt()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("NOTE");
        Leader leader = new() { ArrowHeadEnabled = true, Vertices = { new XYZ(0, 0, 0), new XYZ(10, 0, 0) }, Style = new DimensionStyle("ARROW") { ArrowSize = 3, ScaleFactor = 1 } };
        block.Entities.Add(leader);
        Insert insert = new(block) { InsertPoint = new XYZ(5, 5, 0), XScale = 2, YScale = 2 };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        IReadOnlyList<SurfacePoint> arrow = Assert.Single(surface.Polygons);
        Assert.Equal(new SurfacePoint(5, 95), arrow[0]);
        // Source-space base at x=3 with half-width 0.5, scaled by 2 and moved by (5,5): x=11, y=5±1.
        Assert.Contains(arrow, p => Math.Abs(p.X - 11) < 1e-9 && Math.Abs(p.Y - 94) < 1e-9);
        Assert.Contains(arrow, p => Math.Abs(p.X - 11) < 1e-9 && Math.Abs(p.Y - 96) < 1e-9);
        Assert.Equal([new SurfacePoint(5, 95), new SurfacePoint(25, 95)], Assert.Single(surface.Polylines));

        // Insert.Explode()'s clone shares the leader's vertex list (a Leader.Clone() quirk like MLine.Clone()'s), so
        // ApplyTransform would otherwise leave the block's own LEADER holding world coordinates after this call.
        Assert.Equal([new XYZ(0, 0, 0), new XYZ(10, 0, 0)], leader.Vertices);
    }

    [Fact]
    public void LeaderArrowTipKeepsVertexZUnderANonWorldInsertNormal()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("ARROWZ");
        Leader leader = new() { ArrowHeadEnabled = true, Vertices = { new XYZ(0, 0, 7), new XYZ(10, 0, 7) }, Style = new DimensionStyle("ARROWZ") { ArrowSize = 3, ScaleFactor = 1 } };
        block.Entities.Add(leader);
        // Normal (0,1,0) couples Z into X/Y through the insert's transform; an arrow anchored with Z forced to 0
        // would land at a different point than the path's own first vertex, detaching the arrow from the line.
        Insert insert = new(block) { Normal = new XYZ(0, 1, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        IReadOnlyList<SurfacePoint> arrow = Assert.Single(surface.Polygons);
        IReadOnlyList<SurfacePoint> path = Assert.Single(surface.Polylines);
        Assert.Equal(path[0], arrow[0]);
    }

    [Fact]
    public void OcsSolidInsideAMirroredInsertComposesBothTransforms()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("PLATEM");
        block.Entities.Add(new Solid { FirstCorner = new XYZ(0, 0, 0), SecondCorner = new XYZ(10, 0, 0), ThirdCorner = new XYZ(0, 5, 0), FourthCorner = new XYZ(10, 5, 0), Normal = new XYZ(0, 0, -1) });
        // The OCS normal mirrors X (world x in [-10,0]), and the insert's own XScale mirrors X again: the two
        // mirrors compose to identity in X, offset by InsertPoint, not a double mirror away from it.
        Insert insert = new(block) { InsertPoint = new XYZ(20, 0, 0), XScale = -1 };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        IReadOnlyList<SurfacePoint> polygon = Assert.Single(surface.Polygons);
        Assert.Equal(new HashSet<SurfacePoint> { new(20, 100), new(30, 100), new(30, 95), new(20, 95) }, polygon.ToHashSet());
    }

    [Fact]
    public void SplinedLeaderInsideAScaledInsertMapsBezierEndpointsThroughThePlacement()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("SPLINENOTE");
        block.Entities.Add(new Leader { PathType = LeaderPathType.Spline, Vertices = { new XYZ(0, 0, 0), new XYZ(5, 5, 0), new XYZ(10, 0, 0) } });
        Insert insert = new(block) { InsertPoint = new XYZ(5, 5, 0), XScale = 2, YScale = 2 };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        // Catmull-Rom control points always start and end exactly on the input points, mapped or not, so the chain's
        // first and last control point must equal the first and last vertex mapped through the insert's transform.
        IReadOnlyList<SurfacePoint> controls = Assert.Single(surface.Beziers);
        Assert.Equal(new SurfacePoint(5, 95), controls[0]);
        Assert.Equal(new SurfacePoint(25, 95), controls[^1]);
    }

    [Fact]
    public void LeaderNestedTwoBlocksDeepIsDrawnThroughTheComposedInsertsAndKeepsItsVertices()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Leader leader = new() { Vertices = { new XYZ(0, 0, 0), new XYZ(10, 0, 0) } };
        BlockRecord inner = new("INNERL");
        inner.Entities.Add(leader);
        Insert nestedInsert = new(inner) { InsertPoint = new XYZ(2, 3, 0) };
        BlockRecord outer = new("OUTERL");
        outer.Entities.Add(nestedInsert);
        Insert outerInsert = new(outer) { InsertPoint = new XYZ(5, 20, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), outerInsert);

        // Neither Insert.Clone() (which deep-clones INNERL, including a Leader that shares LEADER's own vertex
        // list) nor either level's Explode() call is allowed to leave the deep original mutated.
        Assert.Equal([new XYZ(0, 0, 0), new XYZ(10, 0, 0)], leader.Vertices);
        // Composed translation (5,20) + (2,3) = (7,23), both inserts translation-only.
        Assert.Equal([new SurfacePoint(7, 77), new SurfacePoint(17, 77)], Assert.Single(surface.Polylines));
    }

    private static MLineStyle TwoElementStyle(double outer, MLineStyleFlags flags = MLineStyleFlags.None)
    {
        MLineStyle style = new("PLAN") { Flags = flags, FillColor = new ACadSharp.Color(3) };
        style.AddElement(new MLineStyle.Element { Offset = outer, Color = new ACadSharp.Color(1) });
        style.AddElement(new MLineStyle.Element { Offset = -outer, Color = ACadSharp.Color.ByLayer });
        return style;
    }

    private static MLine.Vertex VertexAt(double x, double y, params double[][] parameters)
    {
        MLine.Vertex vertex = new() { Position = new XYZ(x, y, 0), Direction = new XYZ(1, 0, 0), Miter = new XYZ(0, 1, 0) };
        foreach (double[] segment in parameters)
        {
            MLine.Vertex.Segment element = new();
            element.Parameters.AddRange(segment);
            vertex.Segments.Add(element);
        }

        return vertex;
    }

    [Fact]
    public void MLineDrawsOnePolylinePerStyleElementAtTheStoredOffsets()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLine mline = new() { Style = TwoElementStyle(0.5), Vertices = { VertexAt(0, 10, [0.5, 0], [-0.5, 0]), VertexAt(20, 10, [0.5, 0], [-0.5, 0]) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.Equal(2, surface.Polylines.Count);
        Assert.Equal([new SurfacePoint(0, 89.5), new SurfacePoint(20, 89.5)], surface.Polylines[0]);
        Assert.Equal([new SurfacePoint(0, 90.5), new SurfacePoint(20, 90.5)], surface.Polylines[1]);
        Assert.All(surface.Calls.Where(c => c.StartsWith("DrawPolyline", StringComparison.Ordinal)), c => Assert.EndsWith("closed=False", c));
        // Element colour 1 (red) is used for the first element; ByLayer falls back to the entity's resolved colour.
        Assert.Equal(SixLabors.ImageSharp.Color.Red.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgba32>(), surface.Styles[0].StrokeColor.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgba32>());
    }

    [Fact]
    public void MLineWithoutVertexParametersFallsBackToStyleOffsetsAndJustification()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        MLine mline = new() { Style = TwoElementStyle(0.5), ScaleFactor = 2, Justification = MLineJustification.Top, Vertices = { VertexAt(0, 10), VertexAt(20, 10) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        // Top justification puts the +0.5 element on the vertex line; the other lies 2 * 1.0 below it.
        Assert.Equal([new SurfacePoint(0, 90), new SurfacePoint(20, 90)], surface.Polylines[0]);
        Assert.Equal([new SurfacePoint(0, 92), new SurfacePoint(20, 92)], surface.Polylines[1]);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning);
    }

    [Fact]
    public void ClosedMLineClosesEveryElementAndFillsBetweenTheOuterOnes()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        // The third vertex's miter is not the shared (0,1,0) of the other two: a degenerate miter there would make
        // the wall closing the ring back to the first vertex zero-width, hiding whether the fill actually covers it.
        MLine.Vertex third = VertexAt(20, 20, [1, 0], [-1, 0]);
        third.Miter = new XYZ(-1, 0, 0);
        MLine mline = new()
        {
            Style = TwoElementStyle(1, MLineStyleFlags.FillOn),
            Flags = MLineFlags.Closed,
            Vertices = { VertexAt(0, 0, [1, 0], [-1, 0]), VertexAt(20, 0, [1, 0], [-1, 0]), third },
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.All(surface.Calls.Where(c => c.StartsWith("DrawPolyline", StringComparison.Ordinal)), c => Assert.EndsWith("closed=True", c));
        IReadOnlyList<SurfacePoint> fill = Assert.Single(surface.Polygons);
        // Keyhole fill: outer ring (3), a bridge back to the outer and inner starts (2), inner ring reversed (3).
        // The third vertex's (-1,0,0) miter puts its outer point at x 19 and its inner point at x 21.
        Assert.Equal(
            [
                new SurfacePoint(0, 99), new SurfacePoint(20, 99), new SurfacePoint(19, 80),
                new SurfacePoint(0, 99), new SurfacePoint(0, 101),
                new SurfacePoint(21, 80), new SurfacePoint(20, 101), new SurfacePoint(0, 101),
            ],
            fill);
        Assert.Equal("FillPolygon n=8", surface.Calls.First(c => c.StartsWith("Fill", StringComparison.Ordinal) || c.StartsWith("DrawPolyline", StringComparison.Ordinal)));
    }

    [Theory]
    // No cut values: one run covering the whole element.
    [InlineData(new double[] { 0.5, 0 }, 10d, new double[] { 0, 10 })]
    // A single break at the end is not a cut.
    [InlineData(new double[] { 0.5, 0, 10 }, 10d, new double[] { 0, 10 })]
    // Break at 4, resume at 6.
    [InlineData(new double[] { 0.5, 0, 4, 6 }, 10d, new double[] { 0, 4, 6, 10 })]
    // Odd count: the element ends hidden after the last value.
    [InlineData(new double[] { 0.5, 0, 4 }, 10d, new double[] { 0, 4 })]
    // Two cuts.
    [InlineData(new double[] { 0.5, 0, 2, 3, 7, 8 }, 10d, new double[] { 0, 2, 3, 7, 8, 10 })]
    // Values past the end are clamped.
    [InlineData(new double[] { 0.5, 0, 4, 99 }, 10d, new double[] { 0, 4 })]
    public void VisibleRunsFollowTheAbsoluteCutPositions(double[] parameters, double length, double[] expected)
    {
        IReadOnlyList<(double Start, double End)> runs = EntityRenderDispatcher.VisibleRuns(parameters, length);

        Assert.Equal(expected.Length / 2, runs.Count);
        for (int i = 0; i < runs.Count; i++)
        {
            Assert.Equal(expected[2 * i], runs[i].Start, 9);
            Assert.Equal(expected[(2 * i) + 1], runs[i].End, 9);
        }
    }

    [Fact]
    public void VisibleRunsStopAtANonFiniteOrDecreasingValue()
    {
        Assert.Equal([(0d, 4d)], EntityRenderDispatcher.VisibleRuns([0.5, 0, 4, double.NaN, 8], 10d));
        Assert.Equal([(0d, 4d)], EntityRenderDispatcher.VisibleRuns([0.5, 0, 4, 3], 10d));
    }

    [Fact]
    public void AnMLineWithACutDrawsTwoRunsForThatElement()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLineStyle style = new("CUT");
        style.AddElement(new MLineStyle.Element { Offset = 0 });
        MLine mline = new()
        {
            Style = style,
            Vertices =
            {
                VertexAt(0, 10, [0, 0, 4, 6]),
                VertexAt(20, 10, [0, 0, 4, 6]),
            },
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        // A cut element leaves the single-polyline path entirely: its runs are drawn as separate lines.
        Assert.Empty(surface.Polylines);
        Assert.Equal(2, surface.Lines.Count);
        Assert.Equal((new SurfacePoint(0, 90), new SurfacePoint(4, 90)), surface.Lines[0]);
        Assert.Equal((new SurfacePoint(6, 90), new SurfacePoint(20, 90)), surface.Lines[1]);
    }

    [Fact]
    public void AnMLineWithoutCutsStillDrawsOnePolylinePerElement()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLine mline = new()
        {
            Style = TwoElementStyle(0.5),
            Vertices = { VertexAt(0, 10), VertexAt(20, 10) },
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.Equal(2, surface.Polylines.Count);
    }

    [Fact]
    public void ACutMLineInsideAScaledInsertScalesItsRuns()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLineStyle style = new("CUT");
        style.AddElement(new MLineStyle.Element { Offset = 0 });
        BlockRecord block = new("WALL");
        block.Entities.Add(new MLine
        {
            Style = style,
            Vertices = { VertexAt(0, 0, [0, 0, 4, 6]), VertexAt(20, 0, [0, 0, 4, 6]) },
        });
        Insert insert = new(block) { InsertPoint = new XYZ(0, 10, 0), XScale = 2, YScale = 2, ZScale = 2 };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        // The cut positions are stored in the multiline's own units, so a 2x insert puts the 4..6 gap at 8..12.
        Assert.Empty(surface.Polylines);
        Assert.Equal(2, surface.Lines.Count);
        Assert.Equal((new SurfacePoint(0, 90), new SurfacePoint(8, 90)), surface.Lines[0]);
        Assert.Equal((new SurfacePoint(12, 90), new SurfacePoint(40, 90)), surface.Lines[1]);
    }

    [Fact]
    public void AnMLineWithAreaFillCutsNotifiesThatFillCutsAreNotDrawn()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        MLine mline = new() { Style = TwoElementStyle(0.5), Vertices = { VertexAt(0, 10, [0.5, 0], [-0.5, 0]), VertexAt(20, 10, [0.5, 0], [-0.5, 0]) } };
        mline.Vertices[0].Segments[0].AreaFillParameters.Add(2);
        mline.Vertices[0].Segments[0].AreaFillParameters.Add(5);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.Contains(notifications, n => n.NotificationType == NotificationType.NotImplemented && n.Message.Contains("fill cuts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MLineInsideABlockIsDrawnThroughTheInsertAndKeepsItsVertices()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLine mline = new() { Style = TwoElementStyle(0.5), Vertices = { VertexAt(0, 0, [0.5, 0], [-0.5, 0]), VertexAt(10, 0, [0.5, 0], [-0.5, 0]) } };
        BlockRecord block = new("WALL");
        block.Entities.Add(mline);
        Insert insert = new(block) { InsertPoint = new XYZ(5, 20, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Equal(2, mline.Vertices.Count);
        Assert.Equal(2, surface.Polylines.Count);
        Assert.Equal([new SurfacePoint(5, 79.5), new SurfacePoint(15, 79.5)], surface.Polylines[0]);
    }

    [Fact]
    public void MLineNestedTwoBlocksDeepIsDrawnThroughTheComposedInsertsAndKeepsItsVertices()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLine mline = new() { Style = TwoElementStyle(0.5), Vertices = { VertexAt(0, 0, [0.5, 0], [-0.5, 0]), VertexAt(10, 0, [0.5, 0], [-0.5, 0]) } };
        BlockRecord inner = new("INNER");
        inner.Entities.Add(mline);
        Insert nestedInsert = new(inner) { InsertPoint = new XYZ(2, 3, 0) };
        BlockRecord outer = new("OUTER");
        outer.Entities.Add(nestedInsert);
        Insert outerInsert = new(outer) { InsertPoint = new XYZ(5, 20, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), outerInsert);

        // ACadSharp 3.7.1's Insert.Clone() deep-clones its block, so exploding outerInsert clones (and empties the
        // shared vertex list of) an MLINE that is not even a direct child of OUTER's block; without healing every
        // MLINE reachable through the block tree, the original loses its vertices here too.
        Assert.Equal(2, mline.Vertices.Count);
        Assert.Equal(2, surface.Polylines.Count);
        // Composed translation (5,20) + (2,3) = (7,23), both inserts translation-only. Element 0 (offset +0.5):
        // world y 0.5 -> composed 23.5 -> surface 100-23.5 = 76.5. Element 1 (offset -0.5): world y -0.5 -> composed
        // 22.5 -> surface 77.5. X shifts by 7 for both vertices (0 and 10).
        Assert.Equal([new SurfacePoint(7, 76.5), new SurfacePoint(17, 76.5)], surface.Polylines[0]);
        Assert.Equal([new SurfacePoint(7, 77.5), new SurfacePoint(17, 77.5)], surface.Polylines[1]);
    }

    private static Wipeout UnitWipeout()
    {
        // Insert (10,10), one-pixel image whose pixel spans 5 x 5 drawing units.
        return new Wipeout
        {
            InsertPoint = new XYZ(10, 10, 0),
            UVector = new XYZ(5, 0, 0),
            VVector = new XYZ(0, 5, 0),
            Size = new XY(1, 1),
            Flags = ImageDisplayFlags.ShowImage | ImageDisplayFlags.UseClippingBoundary,
            ClippingState = true,
        };
    }

    [Fact]
    public void WipeoutPixelMappingFlipsYAndCentresPixels()
    {
        Wipeout wipeout = UnitWipeout();

        Assert.Equal(new XYZ(10, 15, 0), EntityRenderDispatcher.WipeoutPixelToWorld(wipeout, new XY(-0.5, -0.5), null));
        Assert.Equal(new XYZ(15, 10, 0), EntityRenderDispatcher.WipeoutPixelToWorld(wipeout, new XY(0.5, 0.5), null));
    }

    [Fact]
    public void RectangularWipeoutFillsTheBackgroundColourOpaquely()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { BackgroundColor = SixLabors.ImageSharp.Color.White };
        Wipeout wipeout = UnitWipeout();
        wipeout.ClipType = ClipType.Rectangular;
        wipeout.ClipBoundaryVertices.AddRange([new XY(-0.5, -0.5), new XY(0.5, 0.5)]);
        wipeout.Transparency = new Transparency(50);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), wipeout);

        IReadOnlyList<SurfacePoint> polygon = Assert.Single(surface.Polygons);
        Assert.Equal(4, polygon.Count);
        Assert.Equal(new HashSet<SurfacePoint> { new(10, 90), new(15, 90), new(15, 85), new(10, 85) }, polygon.ToHashSet());
        ImageStyle style = Assert.Single(surface.Styles);
        Assert.Equal(SixLabors.ImageSharp.Color.White.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgba32>(), style.StrokeColor.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgba32>());
        Assert.Equal(1f, style.Opacity);
        Assert.Empty(surface.Polylines);
    }

    [Fact]
    public void PolygonalWipeoutUsesItsVertices()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Wipeout wipeout = UnitWipeout();
        wipeout.ClipType = ClipType.Polygonal;
        wipeout.ClipBoundaryVertices.AddRange([new XY(-0.5, -0.5), new XY(0.5, -0.5), new XY(0, 0.5)]);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), wipeout);

        Assert.Equal([new SurfacePoint(10, 85), new SurfacePoint(15, 85), new SurfacePoint(12.5, 90)], Assert.Single(surface.Polygons));
    }

    [Fact]
    public void WipeoutWithoutClippingFillsTheWholeImageFrame()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Wipeout wipeout = UnitWipeout();
        wipeout.ClippingState = false;

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), wipeout);

        Assert.Equal(new HashSet<SurfacePoint> { new(10, 90), new(15, 90), new(15, 85), new(10, 85) }, Assert.Single(surface.Polygons).ToHashSet());
    }

    [Fact]
    public void WipeoutOnTransparentBackgroundIsSkippedWithAWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { BackgroundColor = SixLabors.ImageSharp.Color.Transparent };
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), UnitWipeout());

        Assert.Empty(surface.Polygons);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning);
    }

    [Fact]
    public void AnInvertedWipeoutMasksTheFrameMinusItsBoundary()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Wipeout wipeout = UnitWipeout();
        // A boundary is what makes "inside" vs "outside" meaningful; UnitWipeout() alone carries none.
        wipeout.ClipBoundaryVertices.AddRange([new XY(-0.5, -0.5), new XY(0.5, 0.5)]);
        wipeout.ClipMode = ClipMode.Inside;

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), wipeout);

        IReadOnlyList<IReadOnlyList<SurfacePoint>> rings = Assert.Single(surface.FillPaths);
        Assert.Equal(2, rings.Count);
        Assert.Empty(surface.Polygons);
    }

    [Fact]
    public void AWipeoutWithClippingOffFillsTheWholeFrameEvenWhenItsModeIsInverted()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Wipeout wipeout = UnitWipeout();
        wipeout.ClipMode = ClipMode.Inside;
        wipeout.ClippingState = false;

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), wipeout);

        Assert.Single(surface.Polygons);
        Assert.Empty(surface.FillPaths);
    }

    [Fact]
    public void AnOrdinaryWipeoutStillFillsOnePolygon()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), UnitWipeout());

        Assert.Single(surface.Polygons);
        Assert.Empty(surface.FillPaths);
    }

    [Fact]
    public void AWipeoutInsideAnInsertIsMappedFromTheOriginalSoItsUAndVStayDirections()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("MASK");
        block.Entities.Add(UnitWipeout());
        Insert insert = new(block) { InsertPoint = new XYZ(50, 0, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        IReadOnlyList<SurfacePoint> polygon = Assert.Single(surface.Polygons);
        // UnitWipeout covers x in [10,15]; the insert translates the insertion point by (50,0,0) to [60,65]. A U
        // vector contaminated by that translation (as ACadSharp 3.7.1's Wipeout.ApplyTransform would produce) would
        // stretch it to [60,115] instead.
        Assert.Equal(60d, polygon.Min(p => p.X), 6);
        Assert.Equal(65d, polygon.Max(p => p.X), 6);
    }

    [Fact]
    public void AHiddenWipeoutDrawsNothing()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Wipeout wipeout = UnitWipeout();
        wipeout.Flags = 0;

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), wipeout);

        Assert.Empty(surface.Polygons);
        Assert.Empty(surface.FillPaths);
    }

    [Fact]
    public void MLineWithANonFiniteStyleOffsetIsSkippedWithAWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        MLineStyle style = new("BROKEN") { Flags = MLineStyleFlags.FillOn, FillColor = new ACadSharp.Color(3) };
        style.AddElement(new MLineStyle.Element { Offset = 0.5, Color = ACadSharp.Color.ByLayer });
        style.AddElement(new MLineStyle.Element { Offset = double.NaN, Color = ACadSharp.Color.ByLayer });
        // The vertices carry no parameters, so the style offsets (and the NaN with them) reach the geometry.
        MLine mline = new() { Style = style, Vertices = { VertexAt(0, 10), VertexAt(20, 10) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        // A NaN scaled offset used to slip past Enumerable.Min (which, unlike Max, does not skip NaN), silently
        // dropping the fill ring while still stroking both elements; it is now caught before any drawing and the
        // whole entity is skipped with a warning instead.
        Assert.Empty(surface.Polylines);
        Assert.Empty(surface.Polygons);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("non-finite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MLineFallbackUnderANegativeScaleAnchorsTheGeometricTopElement()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLine mline = new() { Style = TwoElementStyle(0.5), ScaleFactor = -2, Justification = MLineJustification.Top, Vertices = { VertexAt(0, 10), VertexAt(20, 10) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        // Scaled offsets are -1 (element 0) and +1 (element 1); Top puts the +1 element on the vertex line and element 0 two units below it.
        Assert.Equal([new SurfacePoint(0, 92), new SurfacePoint(20, 92)], surface.Polylines[0]);
        Assert.Equal([new SurfacePoint(0, 90), new SurfacePoint(20, 90)], surface.Polylines[1]);
    }

    [Fact]
    public void MLineElementWithByLayerLinetypeInheritsTheEntityDashes()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLineStyle style = new("DASHED");
        style.AddElement(new MLineStyle.Element { Offset = 0.5, LineType = new LineType(LineType.ByLayerName) });
        style.AddElement(new MLineStyle.Element { Offset = -0.5 });
        LineType dashed = new("DASHED2");
        dashed.AddSegment(new LineType.Segment { Length = 2 });
        dashed.AddSegment(new LineType.Segment { Length = -1 });
        // A third element carries its own genuinely named linetype, distinct from both the ByLayer placeholder and
        // the entity's own "DASHED2": it must still resolve through LineTypeDashResolver instead of falling into
        // the ByLayer/ByBlock inheritance path, so a regression that made every element inherit the entity's dashes
        // would leave this element's pattern indistinguishable from the other two and be caught here.
        // Lengths chosen well above ImageConfiguration.MinimumDashPixels' default (2), so the pattern resolves to
        // an actual dash array rather than collapsing to solid (null) for being too short to render.
        LineType dotted = new("DOTTED2");
        dotted.AddSegment(new LineType.Segment { Length = 3 });
        dotted.AddSegment(new LineType.Segment { Length = -2 });
        style.AddElement(new MLineStyle.Element { Offset = 0, LineType = dotted });
        MLine mline = new()
        {
            Style = style,
            LineType = dashed,
            Vertices =
            {
                VertexAt(0, 10, [0.5, 0], [-0.5, 0], [0, 0]),
                VertexAt(20, 10, [0.5, 0], [-0.5, 0], [0, 0]),
            },
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.NotNull(surface.Styles[0].DashPattern);
        Assert.Equal(surface.Styles[1].DashPattern, surface.Styles[0].DashPattern);
        Assert.NotNull(surface.Styles[2].DashPattern);
        Assert.NotEqual(surface.Styles[0].DashPattern, surface.Styles[2].DashPattern);
    }

    [Fact]
    public void MLineWithANonFiniteScaleIsSkippedWithAWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        MLine mline = new() { Style = TwoElementStyle(0.5), ScaleFactor = double.NaN, Vertices = { VertexAt(0, 10), VertexAt(20, 10) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.Empty(surface.Polylines);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("non-finite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MLineWithOneVertexInsideABlockWarnsThatItHasNoVertices()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        MLine mline = new() { Style = TwoElementStyle(0.5), Vertices = { VertexAt(0, 0, [0.5, 0], [-0.5, 0]) } };
        BlockRecord block = new("STUB");
        block.Entities.Add(mline);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), new Insert(block));

        Assert.Empty(surface.Polylines);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("no vertices", StringComparison.Ordinal));
    }

    [Fact]
    public void WipeoutOnATranslucentBackgroundIsSkippedWithAWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { BackgroundColor = SixLabors.ImageSharp.Color.FromRgba(255, 255, 255, 128) };
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), UnitWipeout());

        // A partly transparent fill blends on the raster backend and masks fully in SVG (Hex drops alpha), so a
        // wipeout that cannot mask is skipped rather than drawn differently by the two backends.
        Assert.Empty(surface.Polygons);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("an opaque background", StringComparison.Ordinal));
    }

    /// <summary>
    /// Renders <paramref name="entity"/> to PNG through the public exporter alongside one ordinary line, and returns
    /// the warnings raised. Non-finite geometry must cost its own entity and nothing more.
    /// </summary>
    /// <param name="entity">The entity whose geometry carries NaN.</param>
    /// <returns>The warnings raised during the export.</returns>
    private static List<NotificationEventArgs> RenderWithNonFiniteEntity(Entity entity)
    {
        BlockRecord block = new("non-finite");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(100, 50, 0)));
        block.Entities.Add(entity);
        ImageExporter exporter = new();
        List<NotificationEventArgs> notifications = new();
        exporter.Configuration.OnNotification += (_, e) => notifications.Add(e);
        exporter.Add(block);

        using RenderedImagePage page = Assert.IsType<RenderedImagePage>(Assert.Single(exporter.Render()));

        Assert.NotNull(page.Canvas);
        return notifications.Where(n => n.NotificationType == NotificationType.Warning).ToList();
    }

    [Fact]
    public void FilledMLineWithANonFiniteVertexIsSkippedWithoutKillingTheExport()
    {
        MLine mline = new()
        {
            Style = TwoElementStyle(0.5, MLineStyleFlags.FillOn),
            Vertices = { VertexAt(0, 0, [0.5, 0], [-0.5, 0]), VertexAt(20, 0, [0.5, 0], [-0.5, 0]) },
        };
        mline.Vertices[1].Position = new XYZ(double.NaN, 0, 0);

        // ImageSharp's fill throws ArithmeticException on a NaN vertex, which is neither an ArgumentException nor an
        // InvalidOperationException: unguarded, one malformed multiline takes the whole page down. A non-finite
        // vertex position fails HasFiniteGeometry before drawing is attempted at all, so the message is the
        // dispatcher's own, not the raster backend's ArithmeticException backstop (which must never be reached: it
        // would mean the dispatcher's own check let a NaN vertex through to ImageSharp's fill).
        List<NotificationEventArgs> warnings = RenderWithNonFiniteEntity(mline);
        Assert.Contains("geometry contains non-finite values; entity skipped", Assert.Single(warnings).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(warnings, w => w.Message.Contains("Raster:", StringComparison.Ordinal));
    }

    [Fact]
    public void WipeoutWithANonFiniteVectorIsSkippedWithoutKillingTheExport()
    {
        Wipeout wipeout = UnitWipeout();
        wipeout.UVector = new XYZ(double.NaN, 0, 0);

        // As above: a non-finite UVector fails HasFiniteGeometry before drawing is attempted, so the message is the
        // dispatcher's own and the raster backend's non-finite backstop must never fire.
        List<NotificationEventArgs> warnings = RenderWithNonFiniteEntity(wipeout);
        Assert.Contains("geometry contains non-finite values; entity skipped", Assert.Single(warnings).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(warnings, w => w.Message.Contains("Raster:", StringComparison.Ordinal));
    }

    [Fact]
    public void InsertWithoutABlockIsSkippedWithAWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        Insert insert = new(new BlockRecord("GONE"));
        typeof(Insert).GetProperty(nameof(Insert.Block))!.SetValue(insert, null);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("Draw", StringComparison.Ordinal));
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("no block", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ABlockThatReferencesItselfIsSkippedWithAWarningInsteadOfOverflowing()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        CadDocument document = new();
        BlockRecord outer = new("OUTER");
        BlockRecord inner = new("INNER");
        document.BlockRecords.Add(outer);
        document.BlockRecords.Add(inner);
        outer.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)));
        // Constructing an Insert of a block overflows the stack once that block's graph already contains a cycle
        // (ACadSharp 3.7.1's Insert(BlockRecord) constructor itself recurses through the block). So the insert under
        // test is built, and added to the document, while outer's graph is still acyclic; the second half of the
        // cycle (inner's own Insert(outer)) is wired up afterwards, closing the cycle only in the two blocks'
        // Entities collections, never inside another Insert constructor call.
        Insert insert = new(outer);
        document.Entities.Add(insert);
        outer.Entities.Add(new Insert(inner));
        inner.Entities.Add(new Insert(outer));

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("references itself", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(surface.Lines);
    }

    [Fact]
    public void ACircularBlockGraphDoesNotKillTheExporterWhileFramingThePage()
    {
        // Framing runs before rendering and recurses through the same graph, so this is the call that dies first if
        // only the draw path is guarded. A stack overflow cannot be caught, so a regression here takes the whole
        // test process down rather than failing this test: run it on its own when it is new.
        CadDocument document = new();
        BlockRecord outer = new("OUTER");
        BlockRecord inner = new("INNER");
        document.BlockRecords.Add(outer);
        document.BlockRecords.Add(inner);
        outer.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)));
        // See the comment above: the insert is built and added to the document before the cycle is closed, because
        // constructing an Insert of an already-cyclic block overflows the stack inside ACadSharp's own constructor.
        Insert insert = new(outer);
        document.Entities.Add(insert);
        outer.Entities.Add(new Insert(inner));
        inner.Entities.Add(new Insert(outer));
        ImageExporter exporter = new();

        exporter.Add(document.ModelSpace);

        Assert.NotNull(exporter.Pages);
    }

    [Fact]
    public void AnOrdinaryNestedBlockStillDraws()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord outer = new("OUTER");
        BlockRecord inner = new("INNER");
        inner.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0)));
        outer.Entities.Add(new Insert(inner) { InsertPoint = new XYZ(0, 5, 0) });
        Insert insert = new(outer) { InsertPoint = new XYZ(2, 3, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Single(surface.Lines);
    }

    [Fact]
    public void ABlockCycleHiddenBehindAnMLineIsStillDetected()
    {
        // ScanBlockSubtree (used for the MLINE/LEADER heal scan) stops at the first MLINE or LEADER it finds and
        // never looks past it. BlockGraphIsCircular must not share that shortcut: the cycle-closing Insert here sits
        // behind an MLine as the block's first entity, so a regression that delegated cycle detection back to
        // ScanBlockSubtree-style logic would never reach it and would pass every other cycle test in this file.
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        CadDocument document = new();
        BlockRecord outer = new("OUTER");
        BlockRecord inner = new("INNER");
        document.BlockRecords.Add(outer);
        document.BlockRecords.Add(inner);
        outer.Entities.Add(new MLine());
        // See the construction-order comments on the other cycle tests above: the insert under test is built while
        // outer's graph is still acyclic (only the MLine is there), and the cycle is closed afterward.
        Insert insert = new(outer);
        document.Entities.Add(insert);
        outer.Entities.Add(new Insert(inner));
        inner.Entities.Add(new Insert(outer));

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("references itself", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("Draw", StringComparison.Ordinal));
    }

    [Fact]
    public void ADirectSelfReferencingBlockIsDetected()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        BlockRecord self = new("SELF");
        // Constructed while `self` is still empty, so ACadSharp's own Insert(BlockRecord) constructor - which
        // itself recurses through the block, the same recursion Explode() performs - does not yet see a cycle. The
        // self-reference is closed afterward purely through List<Entity>.Add, and this very same Insert instance
        // (not a freshly constructed one) is what gets drawn, so no later Insert(self) call ever runs against an
        // already-cyclic block.
        Insert insert = new(self);
        self.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("references itself", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("Draw", StringComparison.Ordinal));
    }

    [Fact]
    public void ADiamondSharedBlockIsNotMistakenForACycle()
    {
        // Two different paths (A -> B -> D and A -> C -> D) reach the same block D. Path-scoped cycle tracking must
        // tell this apart from a real cycle: only a globally shared "visited" set would wrongly flag D the second
        // time it is reached, which would silently refuse to draw any drawing that reuses a block from two places.
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        BlockRecord a = new("A");
        BlockRecord b = new("B");
        BlockRecord c = new("C");
        BlockRecord d = new("D");
        d.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)));
        b.Entities.Add(new Insert(d));
        c.Entities.Add(new Insert(d));
        a.Entities.Add(new Insert(b));
        a.Entities.Add(new Insert(c));
        Insert insert = new(a);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.DoesNotContain(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("references itself", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, surface.Lines.Count);
    }

    /// <summary>Whether two surface points agree to within a rounding tolerance.</summary>
    private static bool Close(SurfacePoint actual, SurfacePoint expected)
        => Math.Abs(actual.X - expected.X) < 1e-9 && Math.Abs(actual.Y - expected.Y) < 1e-9;

    private static BlockRecord ArrowBlock(string name = "ARROW", double baseX = 0, double baseY = 0)
    {
        BlockRecord block = new(name);
        block.BlockEntity.BasePoint = new XYZ(baseX, baseY, 0);
        // A unit arrow: the tip sits at the base point and the body runs back along local -X.
        block.Entities.Add(new Line(new XYZ(baseX - 1, baseY, 0), new XYZ(baseX, baseY, 0)));
        block.Entities.Add(new Solid
        {
            FirstCorner = new XYZ(baseX - 1, baseY - 0.25, 0),
            SecondCorner = new XYZ(baseX, baseY, 0),
            ThirdCorner = new XYZ(baseX - 1, baseY + 0.25, 0),
            FourthCorner = new XYZ(baseX, baseY, 0),
        });
        return block;
    }

    [Fact]
    public void ALeaderWithACustomArrowBlockDrawsTheBlockAndNotifiesNothing()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        document.BlockRecords.Add(arrow);
        Leader leader = WithHandle(new Leader
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(10, 10, 0), new XYZ(30, 10, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        }, 0x5A);
        document.Entities.Add(leader);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.DoesNotContain(notifications, n => n.NotificationType == NotificationType.NotImplemented);
        // The block's own line: its local +X points outward, away from the leader running off to (30,10), so its
        // body runs from the tip at (10,10) back towards the leader, twice as long as the block's own unit.
        Assert.Contains(surface.Lines, l => l.Start == new SurfacePoint(12, 90) && l.End == new SurfacePoint(10, 90));
        // The block's solid, not the built-in triangle.
        Assert.Single(surface.Polygons);
        // The arrow's parts belong to the leader, not to the transient insert that placed them.
        Assert.Equal(new ulong?[] { null, 0x5AUL, 0x5AUL }, surface.Entities.Select(e => e.ParentHandle).ToArray());
    }

    [Fact]
    public void ACustomArrowRotatesToTheOutwardLeaderDirection()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        document.BlockRecords.Add(arrow);
        // The leader runs downward from the tip, so the arrow's local +X must point up.
        Leader leader = new()
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(10, 50, 0), new XYZ(10, 20, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        };
        document.Entities.Add(leader);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.Contains(surface.Lines, l => l.Start == new SurfacePoint(10, 52) && l.End == new SurfacePoint(10, 50));
    }

    [Fact]
    public void ACustomArrowHonoursANonZeroBlockBasePoint()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock("ARROWB", baseX: 5, baseY: 7);
        document.BlockRecords.Add(arrow);
        Leader leader = new()
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(10, 10, 0), new XYZ(30, 10, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        };
        document.Entities.Add(leader);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        // The base point is the arrow's tip, so it must land on the leader's first vertex exactly as in the
        // zero-base-point case: the block's line still runs from (12,10) to (10,10) in world. The compensation for
        // the base point goes through the insert's rotation, so the coordinates carry a few ulps of rounding.
        Assert.Contains(surface.Lines, l => Close(l.Start, new SurfacePoint(12, 90)) && Close(l.End, new SurfacePoint(10, 90)));
    }

    [Fact]
    public void ACustomArrowInsideAScaledInsertScalesWithIt()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        document.BlockRecords.Add(arrow);
        BlockRecord note = new("NOTE");
        document.BlockRecords.Add(note);
        note.Entities.Add(new Leader
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(0, 0, 0), new XYZ(10, 0, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        });
        Insert insert = new(note) { InsertPoint = new XYZ(10, 10, 0), XScale = 3, YScale = 3, ZScale = 3 };
        document.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        // Arrow size 2 scaled by 3 is 6: the block's line runs from (16,10) to (10,10) in world.
        Assert.Contains(surface.Lines, l => l.Start == new SurfacePoint(16, 90) && l.End == new SurfacePoint(10, 90));
    }

    [Fact]
    public void ACustomArrowUnderANonUniformInsertFallsBackToTheDefaultTriangleWithAWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        document.BlockRecords.Add(arrow);
        BlockRecord note = new("NOTE");
        document.BlockRecords.Add(note);
        note.Entities.Add(new Leader
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(0, 0, 0), new XYZ(10, 0, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        });
        Insert insert = new(note) { InsertPoint = new XYZ(10, 10, 0), XScale = 3, YScale = 1, ZScale = 1, Rotation = Math.PI / 4 };
        document.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Contains(notifications, n => n.Message.Contains("cannot be placed", StringComparison.OrdinalIgnoreCase));
        Assert.Single(surface.Polygons);
    }

    [Fact]
    public void ACustomArrowUnderARotatedNonUniformInsertFallsBackOnAxisLength()
    {
        // A rotated non-uniform insert, unlike the unrotated sibling above: an Insert's transform is a rotation
        // times per-axis scales, so it maps the unit axes to lengths 3 and 1 at right angles however far it is
        // turned, and this is rejected on length like the sibling rather than on orthogonality. No placement the
        // renderer builds reaches the orthogonality branch of the gate: a tilted insert does not either, because
        // ACadSharp's arbitrary-axis X always lies in the world XY plane and leaves the two projected axes at right
        // angles with unequal lengths, and a nested insert is re-expressed by Explode() as an Insert, which cannot
        // carry a shear in the first place. InsertPlacementTests.AShearedPlacementWithEqualLengthAxesIsNotASimilarity
        // drives that branch directly with a hand-built shear instead.
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        document.BlockRecords.Add(arrow);
        BlockRecord note = new("NOTE");
        document.BlockRecords.Add(note);
        note.Entities.Add(new Leader
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(0, 0, 0), new XYZ(10, 10, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        });
        Insert insert = new(note) { InsertPoint = new XYZ(10, 10, 0), XScale = 3, YScale = 1, ZScale = 1, Rotation = Math.PI / 4 };
        document.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Contains(notifications, n => n.Message.Contains("cannot be placed", StringComparison.OrdinalIgnoreCase));
        Assert.Single(surface.Polygons);
    }

    [Fact]
    public void ARecursiveArrowBlockFallsBackToTheDefaultTriangle()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        document.BlockRecords.Add(arrow);
        arrow.Entities.Add(new Insert(arrow));
        Leader leader = new()
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(10, 10, 0), new XYZ(30, 10, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        };
        document.Entities.Add(leader);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.Contains(notifications, n => n.Message.Contains("references itself", StringComparison.OrdinalIgnoreCase));
        Assert.Single(surface.Polygons);
    }

    [Fact]
    public void AnEmptyArrowBlockDrawsNothingExtraAndWarnsOnce()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        CadDocument document = new();
        BlockRecord arrow = new("EMPTY");
        document.BlockRecords.Add(arrow);
        Leader leader = new()
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(10, 10, 0), new XYZ(30, 10, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        };
        document.Entities.Add(leader);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.Single(notifications);
        Assert.Contains(notifications, n => n.Message.Contains("is empty", StringComparison.OrdinalIgnoreCase));
        Assert.Single(surface.Polygons);
    }

    [Fact]
    public void AnArrowBlockWhoseOwnLeaderUsesItAgainFallsBackToTheDefaultTriangle()
    {
        // Leader.Clone() deep-clones its dimension style and with it that style's arrowhead block, so an arrow
        // block holding a leader that points back at it exhausts the stack inside Explode(), uncatchably; the
        // cycle walk follows the leader-arrow edge for exactly that reason.
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        document.BlockRecords.Add(arrow);
        arrow.Entities.Add(new Leader
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(-0.5, 0, 0), new XYZ(-1, 0, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 0.2, ScaleFactor = 1, LeaderArrow = arrow },
        });
        Leader leader = new()
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(10, 10, 0), new XYZ(30, 10, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        };
        document.Entities.Add(leader);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.Contains(notifications, n => n.Message.Contains("references itself", StringComparison.OrdinalIgnoreCase));
        Assert.Single(surface.Polygons);
    }

    [Fact]
    public void ACustomArrowInsideAMirroredInsertIsReflectedWithIt()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        document.BlockRecords.Add(arrow);
        BlockRecord note = new("NOTE");
        document.BlockRecords.Add(note);
        note.Entities.Add(new Leader
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(0, 0, 0), new XYZ(10, 0, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        });
        Insert insert = new(note) { InsertPoint = new XYZ(10, 10, 0), XScale = -1, YScale = 1, ZScale = 1 };
        document.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        // The mirror turns the leader round to run towards world (0,10), so the arrow body still runs from the tip
        // at (10,10) towards the leader, now along -X. A mirrored placement is expressed as a negative X scale on
        // the transient insert, so an inverted reflection branch would put the body at (12,10) instead.
        Assert.Contains(surface.Lines, l => Close(l.Start, new SurfacePoint(8, 90)) && Close(l.End, new SurfacePoint(10, 90)));
    }

    [Fact]
    public void DrawingAnArrowBlockLeavesAnMLineInsideItIntact()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        MLine mline = new()
        {
            Style = TwoElementStyle(0.5),
            Vertices = { VertexAt(-1, 0, [0.5, 0], [-0.5, 0]), VertexAt(0, 0, [0.5, 0], [-0.5, 0]) },
        };
        arrow.Entities.Add(mline);
        document.BlockRecords.Add(arrow);
        BlockRecord note = new("NOTE");
        // ACadSharp 3.7.1's Insert(BlockRecord) constructor clones a document-owned block's entities, and cloning a
        // LEADER clones its dimension style and with it that style's arrowhead block, so building the insert after
        // the leader would empty this MLINE before the renderer ever saw the drawing. The insert is therefore built
        // while NOTE is still empty, the same construction-order workaround the cycle tests use.
        Insert insert = new(note) { InsertPoint = new XYZ(10, 10, 0) };
        note.Entities.Add(new Leader
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(0, 0, 0), new XYZ(10, 0, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        });
        document.BlockRecords.Add(note);
        document.Entities.Add(insert);
        Assert.Equal(2, mline.Vertices.Count);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        // The caller's own MLINE must survive the render, and it must have been drawn from its two vertices: the
        // leader's path is one polyline and the MLINE's two style elements are the other two.
        Assert.Equal(2, mline.Vertices.Count);
        Assert.Equal(3, surface.Polylines.Count);
    }

    [Fact]
    public void DrawingATopLevelLeadersArrowBlockLeavesAnMLineInsideItIntact()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        MLine mline = new()
        {
            Style = TwoElementStyle(0.5),
            Vertices = { VertexAt(-1, 0, [0.5, 0], [-0.5, 0]), VertexAt(0, 0, [0.5, 0], [-0.5, 0]) },
        };
        arrow.Entities.Add(mline);
        document.BlockRecords.Add(arrow);
        Leader leader = new()
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(10, 10, 0), new XYZ(30, 10, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        };
        document.Entities.Add(leader);
        Assert.Equal(2, mline.Vertices.Count);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        // Nothing exploded this leader, so the only thing standing between ACadSharp's Insert(BlockRecord)
        // constructor and the caller's MLINE is the snapshot DrawArrowBlock takes before building its transient
        // insert.
        Assert.Equal(2, mline.Vertices.Count);
        Assert.Equal(3, surface.Polylines.Count);
    }
}
