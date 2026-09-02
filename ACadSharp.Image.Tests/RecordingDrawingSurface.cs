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

    public int Depth { get; private set; }

    public bool SupportsCurves { get; init; }

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

    public void DrawLine(ImageStyle style, SurfacePoint start, SurfacePoint end) => this.Calls.Add($"DrawLine {start} {end} w={style.StrokeWidth}");

    public void DrawPolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, bool closed) => this.Calls.Add($"DrawPolyline n={points.Count} closed={closed}");

    public void DrawArc(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation, double startAngle, double sweepAngle) => this.Calls.Add($"DrawArc {center} rx={radiusX} ry={radiusY} start={startAngle} sweep={sweepAngle}");

    public void DrawEllipse(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation) => this.Calls.Add($"DrawEllipse {center} rx={radiusX} ry={radiusY}");

    public void DrawCubicBezier(ImageStyle style, IReadOnlyList<SurfacePoint> controlPoints, bool closed) => this.Calls.Add($"DrawCubicBezier n={controlPoints.Count} closed={closed}");

    public void DrawBulgePolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, IReadOnlyList<double> bulges, bool closed) => this.Calls.Add($"DrawBulgePolyline n={points.Count} closed={closed}");

    public void FillPolygon(ImageStyle style, IReadOnlyList<SurfacePoint> points) => this.Calls.Add($"FillPolygon n={points.Count}");

    public void FillPath(ImageStyle style, IReadOnlyList<IReadOnlyList<SurfacePoint>> rings) => this.Calls.Add($"FillPath rings={rings.Count}");

    public void FillCircle(ImageStyle style, SurfacePoint center, double radius) => this.Calls.Add($"FillCircle {center} r={radius}");

    public void DrawText(ImageStyle style, SurfaceText text) => this.Calls.Add($"DrawText '{text.Text}' anchor={text.Anchor} baseline={text.Baseline}");

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
