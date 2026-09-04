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

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Single(surface.Texts, t => t.Text == "ACME");
    }

    [Fact]
    public void ConstantAttributeDefinitionIsDrawnOnceWhenTheInsertCarriesNoAttrib()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("CONST2");
        block.Entities.Add(new AttributeDefinition { Tag = "MAKER", Value = "ACME2", InsertPoint = new XYZ(1, 1, 0), Height = 2, Flags = AttributeFlags.Constant });
        // Insert has no parameterless constructor and Block has no public setter in ACadSharp 3.7.1 (verified by
        // probe), so an insert with no ATTRIB is built via Insert(BlockRecord) and then Attributes.Clear(),
        // reproducing a file where a constant attribute was never persisted as an ATTRIB.
        Insert insert = new(block) { InsertPoint = new XYZ(10, 0, 0) };
        insert.Attributes.Clear();
        Assert.Empty(insert.Attributes);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Single(surface.Texts, t => t.Text == "ACME2");
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
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.NotImplemented && n.Message.Contains("_DOT", StringComparison.Ordinal));
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

    [Fact]
    public void MLineCutParametersAreIgnoredWithAWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        MLine mline = new() { Style = TwoElementStyle(0.5), Vertices = { VertexAt(0, 10, [0.5, 0, 4, 6], [-0.5, 0]), VertexAt(20, 10, [0.5, 0], [-0.5, 0]) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.Equal(2, surface.Polylines.Count);
        Assert.Single(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("cut", StringComparison.OrdinalIgnoreCase));
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

        Assert.Equal(new XYZ(10, 15, 0), EntityRenderDispatcher.WipeoutPixelToWorld(wipeout, new XY(-0.5, -0.5)));
        Assert.Equal(new XYZ(15, 10, 0), EntityRenderDispatcher.WipeoutPixelToWorld(wipeout, new XY(0.5, 0.5)));
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
    public void InvertedAndHiddenWipeoutsDrawNothing()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        Wipeout inverted = UnitWipeout();
        inverted.ClipMode = ClipMode.Inside;
        Wipeout hidden = UnitWipeout();
        hidden.Flags = ImageDisplayFlags.None;
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(CreateContext(surface, configuration), inverted);
        dispatcher.Draw(CreateContext(surface, configuration), hidden);

        Assert.Empty(surface.Polygons);
        Assert.Single(notifications, n => n.NotificationType == NotificationType.NotImplemented);
    }

    [Fact]
    public void MLineWithANonFiniteStyleOffsetStrokesWithoutFilling()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLineStyle style = new("BROKEN") { Flags = MLineStyleFlags.FillOn, FillColor = new ACadSharp.Color(3) };
        style.AddElement(new MLineStyle.Element { Offset = 0.5, Color = ACadSharp.Color.ByLayer });
        style.AddElement(new MLineStyle.Element { Offset = double.NaN, Color = ACadSharp.Color.ByLayer });
        // The vertices carry no parameters, so the style offsets (and the NaN with them) reach the geometry.
        MLine mline = new() { Style = style, Vertices = { VertexAt(0, 10), VertexAt(20, 10) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        // Enumerable.Min returns NaN where Max skips it, so the inner element is never found and the ring is dropped:
        // both elements are still stroked, and no fill is attempted.
        Assert.Equal(2, surface.Polylines.Count);
        Assert.Empty(surface.Polygons);
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
        // InvalidOperationException: unguarded, one malformed multiline takes the whole page down.
        Assert.Contains("non-finite", Assert.Single(RenderWithNonFiniteEntity(mline)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WipeoutWithANonFiniteVectorIsSkippedWithoutKillingTheExport()
    {
        Wipeout wipeout = UnitWipeout();
        wipeout.UVector = new XYZ(double.NaN, 0, 0);

        Assert.Contains("non-finite", Assert.Single(RenderWithNonFiniteEntity(wipeout)).Message, StringComparison.Ordinal);
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
}
