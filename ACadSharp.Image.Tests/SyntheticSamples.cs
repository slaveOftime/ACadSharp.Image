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
}
