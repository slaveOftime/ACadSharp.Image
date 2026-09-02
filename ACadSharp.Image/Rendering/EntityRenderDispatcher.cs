using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.Image.Extensions;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;

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
        this._styleResolver = new ImageStyleResolver();
        this._textRenderer = new TextRenderer();
    }

    /// <summary>
    /// Draws a single CAD entity onto the drawing surface.
    /// </summary>
    /// <param name="context">The rendering context containing the surface and coordinate transforms.</param>
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
        this.Draw(context, entity, parentLayerName: null, parentHandle: null, blockName: null);
    }

    private void Draw(ImageRenderContext context, Entity entity, string? parentLayerName, ulong? parentHandle, string? blockName)
    {
        ImageStyle style = this._styleResolver.Resolve(entity, context);
        string layerName = GetEffectiveLayerName(entity, parentLayerName);
        EntityRenderInfo info = new(layerName, entity.ObjectName, entity.Handle, parentHandle, blockName);
        LayerRenderInfo layerInfo = CreateLayerInfo(entity.Layer, layerName, context);

        context.Surface.BeginEntity(info, layerInfo);
        try
        {
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
                    context.Surface.DrawLine(style, context.ToSurfacePoint(line.StartPoint), context.ToSurfacePoint(line.EndPoint));
                    break;
                case Dimension dimension:
                    this.DrawDimension(context, dimension, layerName);
                    break;
                case Solid solid:
                    DrawSolid(context, style, solid);
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
                case Insert insert:
                    this.DrawBlockContents(context, insert, layerName);
                    break;
                default:
                    this._configuration.Notify($"[{entity.SubclassMarker}] Drawing not implemented.", NotificationType.NotImplemented);
                    break;
            }
        }
        finally
        {
            context.Surface.EndEntity();
        }
    }

    /// <summary>
    /// Entities on layer "0" inside a block take the layer of the insert that placed them.
    /// </summary>
    internal static string GetEffectiveLayerName(Entity entity, string? parentLayerName)
    {
        string? own = entity.Layer?.Name;
        if (string.IsNullOrEmpty(own))
        {
            return parentLayerName ?? Layer.DefaultName;
        }

        if (parentLayerName != null && string.Equals(own, Layer.DefaultName, StringComparison.Ordinal))
        {
            return parentLayerName;
        }

        return own;
    }

    private static LayerRenderInfo CreateLayerInfo(Layer? layer, string layerName, ImageRenderContext context)
    {
        if (layer == null)
        {
            return new LayerRenderInfo(layerName, SixLabors.ImageSharp.Color.Black, context.ToStrokeWidth(LineWeightType.Default));
        }

        return new LayerRenderInfo(layerName, layer.Color.ToImageColor(), context.ToStrokeWidth(layer.LineWeight));
    }

    private void DrawPoint(ImageRenderContext context, ImageStyle style, ACadSharp.Entities.Point point)
    {
        float radius = Math.Max(1f, this._configuration.DotSizePixels / 2f);
        context.Surface.FillCircle(style, context.ToSurfacePoint(point.Location), radius);
    }

    private void DrawDimension(ImageRenderContext context, Dimension dimension, string layerName)
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

            this.Draw(context, entity, layerName, dimension.Handle, blockName: null);
        }
    }

    private static void DrawSolid(ImageRenderContext context, ImageStyle style, Solid solid)
    {
        SurfacePoint[] points =
        [
            context.ToSurfacePoint(solid.FirstCorner),
            context.ToSurfacePoint(solid.SecondCorner),
            context.ToSurfacePoint(solid.ThirdCorner),
            context.ToSurfacePoint(solid.FourthCorner),
        ];

        context.Surface.FillPolygon(style, points);
    }

    private void DrawPolyline(ImageRenderContext context, ImageStyle style, IEnumerable<XY> vertices, bool close)
    {
        SurfacePoint[] points = vertices.Select(context.ToSurfacePoint).ToArray();
        if (points.Length < 2)
        {
            return;
        }

        context.Surface.DrawPolyline(style, points, SplineRenderer.ShouldClosePoints(points, close));
    }

    private void DrawBlockContents(ImageRenderContext context, Insert insert, string layerName)
    {
        foreach (Entity entity in insert.Explode())
        {
            this.Draw(context, entity, layerName, insert.Handle, insert.Block?.Name);
        }
    }
}
