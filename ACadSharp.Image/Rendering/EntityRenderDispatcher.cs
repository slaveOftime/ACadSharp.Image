using System.Globalization;
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
        this.Draw(context, entity, parentLayer: null, parentHandle: null, blockName: null);
    }

    private void Draw(ImageRenderContext context, Entity entity, Layer? parentLayer, ulong? parentHandle, string? blockName)
    {
        if (!HasFiniteGeometry(entity))
        {
            this._configuration.Notify(
                $"[{entity.SubclassMarker}] Handle {entity.Handle.ToString("X", CultureInfo.InvariantCulture)}: geometry contains non-finite values; entity skipped.",
                NotificationType.Warning);
            return;
        }

        ImageStyle style = this._styleResolver.Resolve(entity, context);
        Layer? layer = GetEffectiveLayer(entity, parentLayer);
        string layerName = layer?.Name ?? Layer.DefaultName;
        EntityRenderInfo info = new(layerName, entity.ObjectName, entity.Handle, parentHandle, blockName);
        LayerRenderInfo layerInfo = CreateLayerInfo(layer, layerName, context);

        context.Surface.BeginEntity(info, layerInfo);
        try
        {
            switch (entity)
            {
                case Arc arc when context.Surface.SupportsCurves && IsWorldPlane(arc.Normal):
                    DrawArc(context, style, arc);
                    break;
                case Arc arc:
                    DrawPolyline(context, style, arc.PolygonalVertexes(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), false);
                    break;
                case Circle circle when context.Surface.SupportsCurves && IsWorldPlane(circle.Normal):
                    context.Surface.DrawEllipse(style, context.ToSurfacePoint(circle.Center), context.ToSurfaceLength(circle.Radius), context.ToSurfaceLength(circle.Radius), 0d);
                    break;
                case Circle circle:
                    DrawPolyline(context, style, circle.PolygonalVertexes(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), true);
                    break;
                case Ellipse ellipse when context.Surface.SupportsCurves && IsWorldPlane(ellipse.Normal):
                    DrawEllipse(context, style, ellipse);
                    break;
                case Ellipse ellipse:
                    DrawPolyline(context, style, ellipse.PolygonalVertexes(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), true);
                    break;
                case Line line:
                    context.Surface.DrawLine(style, context.ToSurfacePoint(line.StartPoint), context.ToSurfacePoint(line.EndPoint));
                    break;
                case Dimension dimension:
                    this.DrawDimension(context, dimension, layer);
                    break;
                case Solid solid:
                    DrawSolid(context, style, solid);
                    break;
                case ACadSharp.Entities.Point point:
                    this.DrawPoint(context, style, point);
                    break;
                case IPolyline polyline when context.Surface.SupportsCurves:
                    DrawBulgePolyline(context, style, polyline);
                    break;
                case IPolyline polyline:
                    DrawPolyline(context, style, polyline.GetPoints<XYZ>(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), polyline.IsClosed);
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
                    this.DrawBlockContents(context, insert, layer);
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
    internal static Layer? GetEffectiveLayer(Entity entity, Layer? parentLayer)
    {
        Layer? own = entity.Layer;
        if (own == null || string.IsNullOrEmpty(own.Name))
        {
            return parentLayer ?? own;
        }

        if (parentLayer != null && string.Equals(own.Name, Layer.DefaultName, StringComparison.Ordinal))
        {
            return parentLayer;
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
        // DotSizePixels is a pixel size; SVG surface units are drawing units, so it has to be converted.
        float radius = Math.Max(1f, this._configuration.DotSizePixels / 2f);
        context.Surface.FillCircle(style, context.ToSurfacePoint(point.Location), context.ToSurfacePixels(radius));
    }

    private void DrawDimension(ImageRenderContext context, Dimension dimension, Layer? layer)
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

            this.Draw(context, entity, layer, dimension.Handle, blockName: null);
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

    /// <summary>
    /// True when an entity's extrusion is the world Z axis, so its OCS coordinates are already world coordinates.
    /// </summary>
    /// <remarks>
    /// Native curve output uses the raw centre, radii and angles; ACadSharp applies the OCS transform only inside
    /// <c>PolygonalVertexes</c>. Anything but the default normal (a <c>(0,0,-1)</c> extrusion mirrors X, for example)
    /// therefore has to fall back to the tessellating path.
    /// </remarks>
    private static bool IsWorldPlane(XYZ normal)
    {
        return Math.Abs(normal.X) < 1e-9 && Math.Abs(normal.Y) < 1e-9 && Math.Abs(normal.Z - 1d) < 1e-9;
    }

    /// <summary>
    /// Brings a drawing sweep into (0, 2*PI]. An exact zero (equal start and end angles) becomes a full turn,
    /// and non-finite input degrades to a full turn rather than looping.
    /// </summary>
    private static double NormalizeSweep(double sweep)
    {
        double full = 2d * Math.PI;
        if (double.IsNaN(sweep) || double.IsInfinity(sweep))
        {
            return full;
        }

        sweep %= full;
        if (sweep <= 0d)
        {
            sweep += full;
        }

        return sweep;
    }

    /// <summary>
    /// False when an entity's defining geometry carries NaN or infinity, as some DXF files do
    /// (Samples/6-57-1119.dxf handle 1FA is an ARC with radius Infinity and NaN angles).
    /// </summary>
    /// <param name="entity">The entity to inspect.</param>
    /// <returns>True when the geometry can be drawn.</returns>
    internal static bool HasFiniteGeometry(Entity entity) => entity switch
    {
        // Arc derives from Circle: this case must stay first.
        Arc arc => IsFinite(arc.Center) && IsFinitePositive(arc.Radius) && double.IsFinite(arc.StartAngle) && double.IsFinite(arc.EndAngle),
        Circle circle => IsFinite(circle.Center) && IsFinitePositive(circle.Radius),
        Ellipse ellipse => IsFinite(ellipse.Center) && IsFinite(ellipse.MajorAxisEndPoint) && double.IsFinite(ellipse.RadiusRatio) && double.IsFinite(ellipse.StartParameter) && double.IsFinite(ellipse.EndParameter),
        Line line => IsFinite(line.StartPoint) && IsFinite(line.EndPoint),
        _ => true,
    };

    private static bool IsFinite(XYZ p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    private static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0d;

    /// <summary>
    /// Emits an arc natively. Drawing angles turn counter-clockwise; the surface Y axis points down, so both the start
    /// angle and the sweep change sign.
    /// </summary>
    private static void DrawArc(ImageRenderContext context, ImageStyle style, Arc arc)
    {
        double sweep = NormalizeSweep(arc.EndAngle - arc.StartAngle);
        double radius = context.ToSurfaceLength(arc.Radius);
        context.Surface.DrawArc(style, context.ToSurfacePoint(arc.Center), radius, radius, 0d, -arc.StartAngle, -sweep);
    }

    /// <summary>
    /// Emits an ellipse or elliptical arc natively.
    /// </summary>
    /// <remarks>
    /// <c>Ellipse.MajorAxis</c> and <c>Ellipse.MinorAxis</c> are full axis lengths in ACadSharp 3.7.1
    /// (<c>MajorAxis</c> is twice the length of <c>MajorAxisEndPoint</c>), so they are halved into surface radii.
    /// </remarks>
    private static void DrawEllipse(ImageRenderContext context, ImageStyle style, Ellipse ellipse)
    {
        double radiusX = context.ToSurfaceLength(ellipse.MajorAxis / 2d);
        double radiusY = context.ToSurfaceLength(ellipse.MinorAxis / 2d);
        SurfacePoint center = context.ToSurfacePoint(ellipse.Center);
        if (ellipse.IsFullEllipse)
        {
            context.Surface.DrawEllipse(style, center, radiusX, radiusY, -ellipse.Rotation);
            return;
        }

        double sweep = NormalizeSweep(ellipse.EndParameter - ellipse.StartParameter);
        context.Surface.DrawArc(style, center, radiusX, radiusY, -ellipse.Rotation, -ellipse.StartParameter, -sweep);
    }

    /// <summary>
    /// Emits a polyline with its bulges intact instead of tessellating the arc segments.
    /// </summary>
    private static void DrawBulgePolyline(ImageRenderContext context, ImageStyle style, IPolyline polyline)
    {
        List<SurfacePoint> points = new();
        List<double> bulges = new();
        foreach (IVertex vertex in polyline.Vertices)
        {
            // IVertex.Location is a CSMath.IVector; it only exposes an indexer.
            points.Add(context.ToSurfacePoint(new XY(vertex.Location[0], vertex.Location[1])));
            bulges.Add(vertex.Bulge);
        }

        if (points.Count < 2)
        {
            return;
        }

        context.Surface.DrawBulgePolyline(style, points, bulges, polyline.IsClosed);
    }

    private static void DrawPolyline(ImageRenderContext context, ImageStyle style, IEnumerable<XY> vertices, bool close)
    {
        SurfacePoint[] points = vertices.Select(context.ToSurfacePoint).ToArray();
        if (points.Length < 2)
        {
            return;
        }

        context.Surface.DrawPolyline(style, points, SplineRenderer.ShouldClosePoints(points, close));
    }

    private void DrawBlockContents(ImageRenderContext context, Insert insert, Layer? layer)
    {
        foreach (Entity entity in insert.Explode())
        {
            this.Draw(context, entity, layer, insert.Handle, insert.Block?.Name);
        }
    }
}
