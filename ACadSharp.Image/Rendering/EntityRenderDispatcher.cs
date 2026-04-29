using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Dispatches rendering of individual CAD entities to type-specific rendering methods.
/// </summary>
/// <remarks>
/// <para>
/// This class acts as a type router: it inspects the runtime type of each
/// <see cref="Entity"/> and delegates to the appropriate drawing method.
/// Supported entity types include lines, circles, arcs, polylines, splines,
/// text, dimensions, points, solids, and more.
/// </para>
/// <para>
/// Unsupported entity types trigger a notification via <see cref="ImageConfiguration.Notify"/>
/// rather than throwing an exception, allowing the export to continue gracefully.
/// </para>
/// </remarks>
internal sealed class EntityRenderDispatcher
{
    private readonly ImageConfiguration _configuration;
    private readonly SplineRenderer _splineRenderer;
    private readonly ImageStyleResolver _styleResolver;
    private readonly TextRenderer _textRenderer;

    public EntityRenderDispatcher(ImageConfiguration configuration)
    {
        this._configuration = configuration;
        this._splineRenderer = new SplineRenderer(configuration);
        this._styleResolver = new ImageStyleResolver(configuration);
        this._textRenderer = new TextRenderer(configuration);
    }

    /// <summary>
    /// Draws a single CAD entity onto the rendering canvas.
    /// </summary>
    /// <param name="context">The rendering context containing the canvas and coordinate transforms.</param>
    /// <param name="entity">The entity to draw.</param>
    /// <remarks>
    /// <para>
    /// The entity's color and line weight are resolved automatically from the entity
    /// properties (ByLayer, ByBlock, or explicit values) using <see cref="ImageStyleResolver"/>.
    /// </para>
    /// <para>
    /// If the entity type is not supported, a warning notification is raised but no
    /// exception is thrown.
    /// </para>
    /// </remarks>
    public void Draw(ImageRenderContext context, Entity entity)
    {
        ImageStyle style = this._styleResolver.Resolve(entity);

        switch (entity)
        {
            case Arc arc:
                this.DrawPolyline(context, style, arc.PolygonalVertexes(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), false);
                break;
            case Circle circle:
                this.DrawPolyline(context, style, circle.PolygonalVertexes(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), true);
                break;
            case Ellipse ellipse:
                this.DrawPolyline(context, style, ellipse.PolygonalVertexes(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), true);
                break;
            case Line line:
                context.Canvas.Mutate(x => x.DrawLine(style.StrokeColor, style.StrokeWidth, context.ToPixelPoint(line.StartPoint), context.ToPixelPoint(line.EndPoint)));
                break;
            case Dimension dimension:
                this.DrawDimension(context, dimension);
                break;
            case Solid solid:
                this.DrawSolid(context, style, solid);
                break;
            case ACadSharp.Entities.Point point:
                this.DrawPoint(context, style, point);
                break;
            case IPolyline polyline:
                this.DrawPolyline(context, style, polyline.GetPoints<XYZ>(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), polyline.IsClosed);
                break;
            case Spline spline:
                this._splineRenderer.Draw(context, style, spline);
                break;
            case MText mtext:
                this._textRenderer.Draw(context, style, mtext);
                break;
            case TextEntity textEntity:
                this._textRenderer.Draw(context, style, textEntity);
                break;
            case IText text:
                this._configuration.Notify($"[{entity.SubclassMarker}] Text rendering is not implemented yet.", NotificationType.NotImplemented);
                break;
            default:
                this._configuration.Notify($"[{entity.SubclassMarker}] Drawing not implemented.", NotificationType.NotImplemented);
                break;
        }
    }

    private void DrawPoint(ImageRenderContext context, ImageStyle style, ACadSharp.Entities.Point point)
    {
        PointF center = context.ToPixelPoint(point.Location);
        float radius = Math.Max(1f, this._configuration.DotSizePixels / 2f);

        context.Canvas.Mutate(x => x.Fill(style.StrokeColor, new EllipsePolygon(center.X, center.Y, radius)));
    }

    private void DrawDimension(ImageRenderContext context, Dimension dimension)
    {
        BlockRecord? block = dimension.Block;
        if (block == null)
        {
            dimension.UpdateBlock();
            block = dimension.Block;
        }

        if (block == null)
        {
            this._configuration.Notify($"[{dimension.SubclassMarker}] Dimension block is not available.", NotificationType.Warning);
            return;
        }

        foreach (Entity entity in block.Entities)
        {
            if (entity is ACadSharp.Entities.Point)
            {
                continue;
            }

            this.Draw(context, entity);
        }
    }

    private void DrawSolid(ImageRenderContext context, ImageStyle style, Solid solid)
    {
        PointF[] points =
        [
            context.ToPixelPoint(solid.FirstCorner),
            context.ToPixelPoint(solid.SecondCorner),
            context.ToPixelPoint(solid.ThirdCorner),
            context.ToPixelPoint(solid.FourthCorner),
        ];

        context.Canvas.Mutate(x => x.FillPolygon(style.StrokeColor, points));
    }

    private void DrawPolyline(ImageRenderContext context, ImageStyle style, IEnumerable<XY> vertices, bool close)
    {
        PointF[] points = vertices.Select(context.ToPixelPoint).ToArray();
        if (points.Length < 2)
        {
            return;
        }

        if (close && this.ShouldClose(points))
        {
            PointF[] closedPoints = new PointF[points.Length + 1];
            Array.Copy(points, closedPoints, points.Length);
            closedPoints[^1] = points[0];
            points = closedPoints;
        }

        context.Canvas.Mutate(x => x.DrawLine(style.StrokeColor, style.StrokeWidth, points));
    }

    /// <summary>
    /// Determines whether a polyline should be closed based on a heuristic.
    /// </summary>
    /// <remarks>
    /// The heuristic compares the distance between the last and first points (closing length)
    /// to the average segment length. If the closing length is within 3x the average segment
    /// length, the polyline is considered closeable. This handles cases where polylines are
    /// nearly closed but have small gaps due to precision or modeling errors.
    /// </remarks>
    private bool ShouldClose(IReadOnlyList<PointF> points)
    {
        if (points.Count < 3)
        {
            return false;
        }

        float totalLength = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            totalLength += Distance(points[i - 1], points[i]);
        }

        float averageSegmentLength = totalLength / (points.Count - 1);
        float closingLength = Distance(points[^1], points[0]);

        // 3x multiplier provides tolerance for small gaps in nearly-closed polylines
        return closingLength <= averageSegmentLength * 3f;
    }

    private static float Distance(PointF a, PointF b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

}
