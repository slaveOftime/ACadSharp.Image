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

        SurfaceText mtext = surface.Texts[1];
        Assert.Equal(0d, Math.Cos(mtext.Rotation), 6);
        Assert.Equal(1d, Math.Sin(mtext.Rotation), 6);
        Assert.Equal(2d, mtext.Height, 6);
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

        // The clone's points are already world: the 0..10 square mirrored about x = 10 spans 0..10 again.
        IReadOnlyList<SurfacePoint> ring = Assert.Single(Assert.Single(surface.FillPaths));
        Assert.Equal(0d, ring.Min(p => p.X), 6);
        Assert.Equal(10d, ring.Max(p => p.X), 6);
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
}
