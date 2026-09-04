using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

/// <summary>
/// Drawings built in code for the golden tests, so the goldens cover the feature list rather than whatever the sample
/// files happen to contain. <see cref="ViewportSheet"/> is round-tripped through the DXF writer and reader so the
/// document arrives the way a file would (owners, handles and table references wired by the reader).
/// </summary>
internal static class SyntheticSamples
{
    /// <summary>
    /// Model space with lines on layers Walls, Hidden and Grid (dashed) and a circle, plus a layout "Sheet"
    /// (297 x 210) holding a viewport at scale 2 that freezes layer Hidden, a frame line and a title.
    /// </summary>
    public static CadDocument ViewportSheet()
    {
        CadDocument document = new();
        document.Header.LineTypeScale = 1d;

        LineType dashed = new("DASHED");
        dashed.AddSegment(new LineType.Segment { Length = 5 });
        dashed.AddSegment(new LineType.Segment { Length = -2.5 });
        document.LineTypes.Add(dashed);

        Layer walls = new("Walls") { Color = new Color(1) };
        Layer hidden = new("Hidden") { Color = new Color(5) };
        Layer grid = new("Grid") { Color = new Color(3), LineType = dashed };
        document.Layers.Add(walls);
        document.Layers.Add(hidden);
        document.Layers.Add(grid);

        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(100, 0, 0)) { Layer = walls });
        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(0, 60, 0)) { Layer = walls });
        document.Entities.Add(new Line(new XYZ(0, 60, 0), new XYZ(100, 60, 0)) { Layer = hidden });
        document.Entities.Add(new Line(new XYZ(0, 30, 0), new XYZ(100, 30, 0)) { Layer = grid });
        document.Entities.Add(new Circle { Center = new XYZ(50, 30, 0), Radius = 20, Layer = walls });

        // The default "Layout1" is left in the document: ACadSharp 3.7.1's LayoutCollection.Remove hard-codes
        // Layout.PaperLayoutName ("Layout1") as non-removable and throws ArgumentException for it. It is harmless
        // here because the tests add only the "Sheet" layout to the exporter, never document.Layouts wholesale.
        Layout sheet = new("Sheet") { PaperWidth = 297, PaperHeight = 210 };
        document.Layouts.Add(sheet);

        Viewport viewport = new()
        {
            Center = new XYZ(148.5, 105, 0),
            Width = 200,
            Height = 120,
            ViewCenter = new XY(50, 30),
            ViewHeight = 60,
        };
        viewport.FrozenLayers.Add(hidden);
        sheet.AssociatedBlock.Entities.Add(viewport);
        sheet.AssociatedBlock.Entities.Add(new Line(new XYZ(10, 10, 0), new XYZ(287, 10, 0)) { Layer = walls });
        sheet.AssociatedBlock.Entities.Add(new TextEntity { Value = "SHEET 1", InsertPoint = new XYZ(10, 190, 0), Height = 8, Layer = walls });

        // DxfWriter.Dispose() closes the underlying stream, so a MemoryStream cannot be read back afterwards; write
        // to a temporary file instead.
        string path = Path.Combine(Path.GetTempPath(), $"viewport-{Guid.NewGuid():N}.dxf");
        try
        {
            using (DxfWriter writer = new(path, document, binary: false))
            {
                writer.Write();
            }

            return DxfReader.Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// One block exercising every primitive the goldens from the sample files do not contain: a solid and a pattern
    /// hatch, a full ellipse and an elliptical arc, a translucent line, an insert with layer-0 and ByBlock contents,
    /// a bulged closed polyline, a two-line MText and a Fit-aligned text.
    /// </summary>
    public static BlockRecord FeatureBlock()
    {
        BlockRecord block = new("features");
        Layer hatchLayer = new("Hatch") { Color = new Color(1) };
        Layer curves = new("Curves") { Color = new Color(4) };
        Layer trans = new("Trans") { Color = new Color(6) };
        Layer doors = new("Doors") { Color = new Color(3) };
        Layer anno = new("Anno") { Color = new Color(7) };

        Hatch solid = new() { IsSolid = true, PatternType = HatchPatternType.SolidFill, Pattern = HatchPattern.Solid, Layer = hatchLayer };
        solid.Paths.Add(SquarePath(0, 0, 20));
        block.Entities.Add(solid);

        Hatch pattern = new() { IsSolid = false, PatternType = HatchPatternType.PatternFill, Pattern = new HatchPattern("ANSI31"), Layer = hatchLayer };
        pattern.Pattern.Lines.Add(new HatchPattern.Line { Angle = Math.PI / 4, BasePoint = XY.Zero, Offset = new XY(0, 3.175) });
        pattern.PatternScale = 1;
        pattern.Paths.Add(SquarePath(30, 0, 20));
        block.Entities.Add(pattern);

        block.Entities.Add(new Ellipse { Center = new XYZ(70, 10, 0), MajorAxisEndPoint = new XYZ(10, 0, 0), RadiusRatio = 0.5, Layer = curves });
        block.Entities.Add(new Ellipse { Center = new XYZ(100, 10, 0), MajorAxisEndPoint = new XYZ(10, 0, 0), RadiusRatio = 0.5, StartParameter = 0, EndParameter = Math.PI, Layer = curves });

        block.Entities.Add(new Line(new XYZ(0, 30, 0), new XYZ(120, 30, 0)) { Layer = trans, Transparency = new Transparency(50), LineWeight = LineWeightType.W100 });

        LwPolyline bulged = new() { IsClosed = true, Layer = curves };
        bulged.Vertices.Add(new LwPolyline.Vertex(new XY(0, 40)) { Bulge = 1 });
        bulged.Vertices.Add(new LwPolyline.Vertex(new XY(20, 40)));
        bulged.Vertices.Add(new LwPolyline.Vertex(new XY(20, 55)));
        block.Entities.Add(bulged);

        BlockRecord door = new("DOOR");
        door.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0)) { Layer = new Layer(Layer.DefaultName) });
        door.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(0, 10, 0)) { Color = Color.ByBlock, LineWeight = LineWeightType.ByBlock });
        block.Entities.Add(new Insert(door) { InsertPoint = new XYZ(40, 40, 0), Layer = doors, Color = new Color(5), LineWeight = LineWeightType.W70 });

        block.Entities.Add(new MText { Value = "Line1\\PLine2", InsertPoint = new XYZ(70, 48, 0), Height = 4, Layer = anno });
        block.Entities.Add(new TextEntity { Value = "FIT", InsertPoint = new XYZ(70, 55, 0), AlignmentPoint = new XYZ(110, 55, 0), HorizontalAlignment = TextHorizontalAlignment.Fit, Height = 4, Layer = anno });

        return block;
    }

    private static Hatch.BoundaryPath SquarePath(double x, double y, double size)
    {
        Hatch.BoundaryPath path = new();
        Hatch.BoundaryPath.Polyline polyline = new() { IsClosed = true };
        polyline.Vertices.AddRange([new XYZ(x, y, 0), new XYZ(x + size, y, 0), new XYZ(x + size, y + size, 0), new XYZ(x, y + size, 0)]);
        path.Edges.Add(polyline);
        return path;
    }

    /// <summary>
    /// A rectangular hatch boundary path built from four line edges (rather than a single closed polyline edge),
    /// spanning <paramref name="x0"/>..<paramref name="x1"/> and <paramref name="y0"/>..<paramref name="y1"/>.
    /// Shared by <c>EntityRenderDispatcherTests</c> and this class.
    /// </summary>
    internal static Hatch.BoundaryPath SquarePath(double x0, double y0, double x1, double y1)
    {
        Hatch.BoundaryPath path = new();
        path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(x0, y0), End = new XY(x1, y0) });
        path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(x1, y0), End = new XY(x1, y1) });
        path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(x1, y1), End = new XY(x0, y1) });
        path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(x0, y1), End = new XY(x0, y0) });
        return path;
    }

    /// <summary>
    /// One block exercising the entities added after the feature goldens were written: a 3DFACE with one hidden edge,
    /// a straight and a splined LEADER (both with arrowheads), a filled two-element MLINE turning a right-angle corner,
    /// an opaque WIPEOUT masking part of a line, and an INSERT whose ATTRIB carries a room number. Handles are
    /// assigned explicitly so the draw order (and so the wipeout's occlusion of the line beneath it) does not depend
    /// on <see cref="BlockRecord.GetSortedEntities"/>'s tie-breaking for entities that would otherwise all share handle 0.
    /// </summary>
    public static BlockRecord EntityBlock()
    {
        BlockRecord block = new("entities");
        Layer faceLayer = new("Face") { Color = new Color(2) };
        Layer leaderLayer = new("Leader") { Color = new Color(4) };
        Layer wallLayer = new("Wall") { Color = new Color(6) };
        Layer underLayer = new("Under") { Color = new Color(1) };
        Layer coverLayer = new("Cover") { Color = new Color(8) };
        Layer roomsLayer = new("Rooms") { Color = new Color(9) };

        Face3D face = WithHandle(new Face3D
        {
            FirstCorner = new XYZ(0, 0, 0),
            SecondCorner = new XYZ(20, 0, 0),
            ThirdCorner = new XYZ(20, 15, 0),
            FourthCorner = new XYZ(0, 15, 0),
            Flags = InvisibleEdgeFlags.Third,
            Layer = faceLayer,
        }, 0x10);
        block.Entities.Add(face);

        DimensionStyle leaderStyle = new("ENTITIES") { ArrowSize = 2 };
        Leader straight = WithHandle(new Leader
        {
            ArrowHeadEnabled = true,
            Style = leaderStyle,
            Layer = leaderLayer,
            Vertices = { new XYZ(30, 0, 0), new XYZ(45, 10, 0), new XYZ(60, 10, 0) },
        }, 0x11);
        block.Entities.Add(straight);

        Leader spline = WithHandle(new Leader
        {
            ArrowHeadEnabled = true,
            PathType = LeaderPathType.Spline,
            Style = leaderStyle,
            Layer = leaderLayer,
            Vertices = { new XYZ(70, 0, 0), new XYZ(80, 10, 0), new XYZ(90, 0, 0), new XYZ(100, 10, 0) },
        }, 0x12);
        block.Entities.Add(spline);

        MLineStyle mlineStyle = new("ENTITIES") { Flags = MLineStyleFlags.FillOn, FillColor = new Color(3) };
        mlineStyle.AddElement(new MLineStyle.Element { Offset = 1, Color = new Color(1) });
        mlineStyle.AddElement(new MLineStyle.Element { Offset = -1, Color = new Color(5) });

        // The corner vertex's miter bisects the right-angle turn from +X to +Y; at offset +-1 the element points lie
        // sqrt(2) along it, not 1 (Position + Miter * along, so a non-unit "along" is what carries the offset across
        // the corner without narrowing the wall).
        double diagonal = Math.Sqrt(2);
        MLine mline = WithHandle(new MLine
        {
            Style = mlineStyle,
            Layer = wallLayer,
            Vertices =
            {
                MLineVertex(new XYZ(0, 30, 0), new XYZ(1, 0, 0), new XYZ(0, 1, 0), [1, 0], [-1, 0]),
                MLineVertex(new XYZ(40, 30, 0), new XYZ(0, 1, 0), new XYZ(-1, 1, 0) / diagonal, [diagonal, 0], [-diagonal, 0]),
                MLineVertex(new XYZ(40, 50, 0), new XYZ(0, 1, 0), new XYZ(-1, 0, 0), [1, 0], [-1, 0]),
            },
        }, 0x13);
        block.Entities.Add(mline);

        Line under = WithHandle(new Line(new XYZ(60, 30, 0), new XYZ(100, 30, 0)) { Layer = underLayer }, 0x14);
        block.Entities.Add(under);

        Wipeout wipeout = WithHandle(new Wipeout
        {
            InsertPoint = new XYZ(70, 25, 0),
            UVector = new XYZ(20, 0, 0),
            VVector = new XYZ(0, 10, 0),
            Size = new XY(1, 1),
            ClippingState = true,
            Layer = coverLayer,
        }, 0x15);
        wipeout.ClipBoundaryVertices.AddRange([new XY(-0.5, -0.5), new XY(0.5, 0.5)]);
        block.Entities.Add(wipeout);

        BlockRecord room = new("ROOM");
        room.Entities.Add(new AttributeDefinition { Tag = "ROOM", Value = "DEFAULT", InsertPoint = XYZ.Zero, Height = 3 });
        // Insert(BlockRecord) creates one AttributeEntity per ATTDEF at the identity transform; place it explicitly.
        Insert insert = WithHandle(new Insert(room) { InsertPoint = new XYZ(60, 45, 0), Layer = roomsLayer }, 0x16);
        AttributeEntity attribute = insert.Attributes.Single();
        attribute.Value = "A-101";
        attribute.InsertPoint = new XYZ(60, 45, 0);
        attribute.Height = 3;
        block.Entities.Add(insert);

        return block;
    }

    /// <summary>
    /// A block exercising every feature the remaining-limitations work added: a multi-line attribute, a hatch on a
    /// tilted plane inside a block, a leader with a custom arrowhead block, an inverted wipeout over a line, and an
    /// MLINE with a cut in both of its elements. Handles increase in draw order, so the wipeout follows the line it
    /// masks.
    /// </summary>
    /// <returns>The block, with every entity on its own named layer.</returns>
    public static BlockRecord FidelityBlock()
    {
        BlockRecord block = new("fidelity");
        Layer roomsLayer = new("Rooms") { Color = new Color(9) };
        Layer hatchLayer = new("Tilted") { Color = new Color(3) };
        Layer leaderLayer = new("Leader") { Color = new Color(4) };
        Layer underLayer = new("Under") { Color = new Color(1) };
        Layer coverLayer = new("Cover") { Color = new Color(8) };
        Layer wallLayer = new("Wall") { Color = new Color(6) };

        // Multi-line attribute: the single-line value must never appear in the output.
        BlockRecord label = new("LABEL");
        label.Entities.Add(new AttributeDefinition { Tag = "ROOM", Value = "FLAT", Layer = roomsLayer });
        Insert labelInsert = WithHandle(new Insert(label) { InsertPoint = new XYZ(10, 80, 0), Layer = roomsLayer }, 0x10);
        labelInsert.Attributes.Clear();
        labelInsert.Attributes.Add(WithHandle(new AttributeEntity
        {
            Tag = "ROOM",
            Value = "FLAT",
            AttributeType = AttributeType.MultiLine,
            InsertPoint = new XYZ(10, 80, 0),
            Height = 4,
            Layer = roomsLayer,
            MText = new MText { Value = "Room 1\\PLevel 2", InsertPoint = new XYZ(10, 80, 0), Height = 4, RectangleWidth = 40 },
        }, 0x11));
        block.Entities.Add(labelInsert);

        // Tilted hatch inside a block: normal (0,0,-1) mirrors X on the way to world.
        BlockRecord tilted = new("TILTED");
        Hatch hatch = new() { IsSolid = true, Normal = new XYZ(0, 0, -1), Elevation = 0d, Layer = hatchLayer };
        hatch.Paths.Add(SquarePath(0, 0, 20, 15));
        tilted.Entities.Add(hatch);
        block.Entities.Add(WithHandle(new Insert(tilted) { InsertPoint = new XYZ(80, 70, 0), Layer = hatchLayer }, 0x12));

        // Custom arrowhead block: tip at the base point, body back along local -X.
        BlockRecord arrow = new("FIDELITY_ARROW");
        arrow.Entities.Add(new Line(new XYZ(-1, 0, 0), new XYZ(0, 0, 0)));
        arrow.Entities.Add(new Solid
        {
            FirstCorner = new XYZ(-1, -0.25, 0),
            SecondCorner = new XYZ(0, 0, 0),
            ThirdCorner = new XYZ(-1, 0.25, 0),
            FourthCorner = new XYZ(0, 0, 0),
        });
        block.Entities.Add(WithHandle(new Leader
        {
            ArrowHeadEnabled = true,
            Style = new DimensionStyle("FIDELITY") { ArrowSize = 4, ScaleFactor = 1, LeaderArrow = arrow },
            Layer = leaderLayer,
            Vertices = { new XYZ(10, 40, 0), new XYZ(35, 55, 0), new XYZ(55, 55, 0) },
        }, 0x13));

        // Inverted wipeout over a line: only the middle band of the line survives.
        block.Entities.Add(WithHandle(new Line(new XYZ(60, 20, 0), new XYZ(110, 20, 0)) { Layer = underLayer }, 0x14));
        Wipeout wipeout = WithHandle(new Wipeout
        {
            InsertPoint = new XYZ(60, 10, 0),
            UVector = new XYZ(50, 0, 0),
            VVector = new XYZ(0, 20, 0),
            Size = new XY(1, 1),
            ClippingState = true,
            ClipType = ClipType.Rectangular,
            ClipMode = ClipMode.Inside,
            Layer = coverLayer,
        }, 0x15);
        wipeout.ClipBoundaryVertices.Add(new XY(-0.2, -0.5));
        wipeout.ClipBoundaryVertices.Add(new XY(0.2, 0.5));
        block.Entities.Add(wipeout);

        // Cut MLINE: both elements break between 20 and 30 along their own length.
        MLineStyle wallStyle = new("FIDELITY_WALL");
        wallStyle.AddElement(new MLineStyle.Element { Offset = 1 });
        wallStyle.AddElement(new MLineStyle.Element { Offset = -1 });
        block.Entities.Add(WithHandle(new MLine
        {
            Style = wallStyle,
            Layer = wallLayer,
            Vertices =
            {
                MLineVertex(new XYZ(10, 10, 0), new XYZ(1, 0, 0), new XYZ(0, 1, 0), [1, 0, 20, 30], [-1, 0, 20, 30]),
                MLineVertex(new XYZ(60, 10, 0), new XYZ(1, 0, 0), new XYZ(0, 1, 0), [1, 0, 20, 30], [-1, 0, 20, 30]),
            },
        }, 0x16));

        return block;
    }

    /// <summary>
    /// Builds one MLINE vertex: <paramref name="direction"/> is the segment direction leaving this vertex (or, at
    /// the last vertex, the direction of the segment arriving at it) and <paramref name="miter"/> is the vector each
    /// element's offset (<c>Position + Miter * Parameters[0]</c>) is carried along; <paramref name="parameters"/>
    /// supplies one segment (with its offset as <c>Parameters[0]</c>) per style element, in element order.
    /// </summary>
    private static MLine.Vertex MLineVertex(XYZ position, XYZ direction, XYZ miter, params double[][] parameters)
    {
        MLine.Vertex vertex = new() { Position = position, Direction = direction, Miter = miter };
        foreach (double[] segment in parameters)
        {
            MLine.Vertex.Segment element = new();
            element.Parameters.AddRange(segment);
            vertex.Segments.Add(element);
        }

        return vertex;
    }

    /// <summary>
    /// Assigns a deterministic handle via reflection: <see cref="CadObject.Handle"/> has an internal setter in
    /// ACadSharp 3.7.1, so tests that need a specific handle (to pin draw order, for instance) cannot set it directly.
    /// Shared by <c>ImagePageTests</c> and <c>EntityRenderDispatcherTests</c> as well as this class.
    /// </summary>
    internal static T WithHandle<T>(T entity, ulong handle)
        where T : CadObject
    {
        typeof(CadObject).GetProperty(nameof(CadObject.Handle))!.SetValue(entity, handle);
        return entity;
    }
}
