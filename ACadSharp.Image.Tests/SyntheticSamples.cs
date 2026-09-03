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
}
