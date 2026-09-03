using ACadSharp.Image.Rendering;

namespace ACadSharp.Image.Tests;

/// <summary>
/// Test double that records surface calls as strings and entity boundaries as infos.
/// </summary>
internal sealed class RecordingDrawingSurface : IDrawingSurface
{
    public List<string> Calls { get; } = new();

    public List<EntityRenderInfo> Entities { get; } = new();

    public List<LayerRenderInfo> Layers { get; } = new();

    public List<ImageStyle> Styles { get; } = new();

    public int Depth { get; private set; }

    public bool SupportsCurves { get; init; }

    /// <summary>Start and end of every DrawLine call, in order.</summary>
    public List<(SurfacePoint Start, SurfacePoint End)> Lines { get; } = new();

    /// <summary>Points of every DrawPolyline and DrawBulgePolyline call, in order.</summary>
    public List<IReadOnlyList<SurfacePoint>> Polylines { get; } = new();

    /// <summary>Rings of every FillPath call, in order.</summary>
    public List<IReadOnlyList<IReadOnlyList<SurfacePoint>>> FillPaths { get; } = new();

    /// <summary>Points of every FillPolygon call, in order.</summary>
    public List<IReadOnlyList<SurfacePoint>> Polygons { get; } = new();

    /// <summary>Every text run handed to DrawText, in order.</summary>
    public List<SurfaceText> Texts { get; } = new();

    public void BeginEntity(EntityRenderInfo info, LayerRenderInfo layer)
    {
        this.Depth++;
        this.Entities.Add(info);
        this.Layers.Add(layer);
        this.Calls.Add($"BeginEntity {info.EntityType} layer={info.LayerName} parent={info.ParentHandle?.ToString("X") ?? "-"} block={info.BlockName ?? "-"}");
    }

    public void EndEntity()
    {
        this.Depth--;
        this.Calls.Add("EndEntity");
    }

    public void DrawLine(ImageStyle style, SurfacePoint start, SurfacePoint end)
    {
        this.Styles.Add(style);
        this.Calls.Add($"DrawLine {start} {end} w={style.StrokeWidth}");
        this.Lines.Add((start, end));
    }

    public void DrawPolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, bool closed)
    {
        this.Styles.Add(style);
        this.Calls.Add($"DrawPolyline n={points.Count} closed={closed}");
        this.Polylines.Add(points.ToArray());
    }

    public void DrawArc(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation, double startAngle, double sweepAngle)
    {
        this.Styles.Add(style);
        this.Calls.Add($"DrawArc {center} rx={radiusX} ry={radiusY} rot={rotation} start={startAngle} sweep={sweepAngle}");
    }

    public void DrawEllipse(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation)
    {
        this.Styles.Add(style);
        this.Calls.Add($"DrawEllipse {center} rx={radiusX} ry={radiusY} rot={rotation}");
    }

    public void DrawCubicBezier(ImageStyle style, IReadOnlyList<SurfacePoint> controlPoints, bool closed)
    {
        this.Styles.Add(style);
        this.Calls.Add($"DrawCubicBezier n={controlPoints.Count} closed={closed}");
    }

    public void DrawBulgePolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, IReadOnlyList<double> bulges, bool closed)
    {
        this.Styles.Add(style);
        this.Calls.Add($"DrawBulgePolyline n={points.Count} closed={closed} bulges={string.Join(",", bulges)}");
        this.Polylines.Add(points.ToArray());
    }

    public void FillPolygon(ImageStyle style, IReadOnlyList<SurfacePoint> points)
    {
        this.Styles.Add(style);
        this.Calls.Add($"FillPolygon n={points.Count}");
        this.Polygons.Add(points.ToArray());
    }

    public void FillPath(ImageStyle style, IReadOnlyList<IReadOnlyList<SurfacePoint>> rings)
    {
        this.Styles.Add(style);
        this.Calls.Add($"FillPath rings={rings.Count}");
        this.FillPaths.Add(rings.Select(r => (IReadOnlyList<SurfacePoint>)r.ToArray()).ToArray());
    }

    public void FillCircle(ImageStyle style, SurfacePoint center, double radius)
    {
        this.Styles.Add(style);
        this.Calls.Add($"FillCircle {center} r={radius}");
    }

    public void DrawText(ImageStyle style, SurfaceText text)
    {
        this.Styles.Add(style);
        this.Calls.Add($"DrawText '{text.Text}' anchor={text.Anchor} baseline={text.Baseline}");
        this.Texts.Add(text);
    }

    public ViewportSurface BeginViewport(SurfaceRect bounds)
    {
        this.Calls.Add($"BeginViewport {bounds}");
        return new ViewportSurface(this, bounds.X, bounds.Y + bounds.Height);
    }

    public void EndViewport(ViewportSurface viewport) => this.Calls.Add("EndViewport");

    public void Dispose()
    {
    }
}
