using System.Globalization;
using System.Runtime.CompilerServices;
using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.Header;
using ACadSharp.Image.Extensions;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using ImageColor = SixLabors.ImageSharp.Color;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

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
    private readonly EntityVisibilityFilter _visibilityFilter;

    /// <summary>
    /// Per-block cache of <see cref="BlockSubtreeNeedsHeal"/>, so repeated inserts of the same block scan its
    /// subtree for MLINEs and LEADERs at most once per page. Cleared by <see cref="BeginPage"/>.
    /// </summary>
    /// <remarks>
    /// Keyed on <see cref="BlockRecord"/> identity, so this only pays off for repeated top-level inserts of the
    /// same block within one page: a nested <see cref="Insert"/> reached while exploding an outer one holds a
    /// deep-cloned block record (per <see cref="Insert.Clone"/> in ACadSharp 3.7.1), a different key every time, so
    /// it misses the cache on every call regardless of how many times the same source block appears nested. A
    /// <see cref="Dictionary{TKey, TValue}"/> would still write an entry for every one of those one-shot clones as
    /// <see cref="ScanBlockSubtree"/> walks them, pinning the whole cloned block graph of a page in memory until
    /// <see cref="BeginPage"/> for no benefit; a <see cref="ConditionalWeakTable{TKey, TValue}"/> gives the same
    /// lookup semantics without retaining a clone past the call that produced it.
    /// </remarks>
    private readonly ConditionalWeakTable<BlockRecord, StrongBox<bool>> _blocksNeedingHeal = new();

    public EntityRenderDispatcher(ImageConfiguration configuration)
    {
        this._configuration = configuration;
        this._splineRenderer = new SplineRenderer(configuration);
        this._styleResolver = new ImageStyleResolver();
        this._textRenderer = new TextRenderer();
        this._visibilityFilter = new EntityVisibilityFilter(configuration);
    }

    /// <summary>
    /// Clears the per-block MLINE/LEADER subtree cache used by <see cref="DrawBlockContents"/>. The dispatcher
    /// belongs to an <see cref="ImagePageRenderer"/>, which can render several pages (and the same document can be
    /// edited between them), so a cached result from an earlier page must not be trusted for a later one; call this
    /// once at the start of every page render.
    /// </summary>
    internal void BeginPage()
    {
        this._blocksNeedingHeal.Clear();
    }

    /// <summary>
    /// Draws a single CAD entity onto the drawing surface.
    /// </summary>
    /// <param name="context">The rendering context containing the surface and coordinate transforms.</param>
    /// <param name="entity">The entity to draw.</param>
    /// <remarks>
    /// <para>
    /// The entity's colour, line weight, linetype dashes and opacity are resolved automatically from the
    /// entity properties (ByLayer, ByBlock, or explicit values) using <see cref="ImageStyleResolver"/>.
    /// </para>
    /// <para>
    /// The entity may not be drawn at all: it is skipped without output when the visibility filter hides its
    /// layer, and skipped with a warning when its defining geometry carries NaN or infinity.
    /// </para>
    /// <para>
    /// If the entity type is not supported, a warning notification is raised but no
    /// exception is thrown.
    /// </para>
    /// </remarks>
    public void Draw(ImageRenderContext context, Entity entity)
    {
        this.Draw(context, entity, parentLayer: null, parentHandle: null, blockName: null, parent: null);
    }

    // source is the original block entity a TEXT, MTEXT, non-world SOLID or LEADER clone came from, whose geometry is
    // used instead of the clone's (see UsesOriginalGeometry), and placement is the transform of the insert that
    // placed it. Both are null outside a block reference, but they do not always travel together inside one: an
    // MLINE clone is always drawn with placement set and source null (UsesOriginalGeometry never recognises an
    // MLine original, since the heal already restores the clone's own vertices to local coordinates), and so is a
    // LEADER clone whose ordinal pairing with the block's original entities failed.
    private void Draw(ImageRenderContext context, Entity entity, Layer? parentLayer, ulong? parentHandle, string? blockName, ResolvedStyle? parent, Entity? source = null, Transform? placement = null)
    {
        // Visibility comes first: a hidden entity must not warn about geometry nobody is going to draw.
        Layer? layer = GetEffectiveLayer(entity, parentLayer);
        string layerName = layer?.Name ?? Layer.DefaultName;
        if (!this._visibilityFilter.IsVisible(entity, layer, layerName, context.Viewport))
        {
            return;
        }

        if (!HasFiniteGeometry(entity))
        {
            this._configuration.Notify(
                $"[{entity.SubclassMarker}] Handle {entity.Handle.ToString("X", CultureInfo.InvariantCulture)}: geometry contains non-finite values; entity skipped.",
                NotificationType.Warning);
            return;
        }

        ImageColor foreground = context.Configuration.ResolveForegroundColor();
        ResolvedStyle resolved = this._styleResolver.ResolveAttributes(entity, layer, parent);
        ImageStyle style = this._styleResolver.ToImageStyle(resolved, context, foreground);
        EntityRenderInfo info = new(layerName, entity.ObjectName, entity.Handle, parentHandle, blockName);
        LayerRenderInfo layerInfo = CreateLayerInfo(layer, layerName, context, foreground);

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
                    this.DrawDimension(context, dimension, layer, resolved);
                    break;
                case Leader leader:
                    this.DrawLeader(context, style, source as Leader ?? leader, placement);
                    break;
                case Solid solid:
                    DrawSolid(context, style, source as Solid ?? solid, placement);
                    break;
                case Face3D face:
                    DrawFace3D(context, style, face);
                    break;
                case ACadSharp.Entities.Point point:
                    this.DrawPoint(context, style, point);
                    break;
                case IPolyline polyline when context.Surface.SupportsCurves && IsWorldPlane(polyline.Normal):
                    DrawBulgePolyline(context, style, polyline);
                    break;
                case IPolyline polyline:
                    DrawPolyline(context, style, this.PolylinePoints(polyline), polyline.IsClosed);
                    break;
                case Spline spline:
                    this._splineRenderer.Draw(context, style, spline);
                    break;
                case MText mtext:
                    this._textRenderer.Draw(context, style, source as MText ?? mtext, placement);
                    break;
                case TextEntity textEntity:
                    this._textRenderer.Draw(context, style, source as TextEntity ?? textEntity, placement);
                    break;
                case IText text:
                    this._configuration.Notify($"[{entity.SubclassMarker}] Text rendering is not implemented yet.", NotificationType.NotImplemented);
                    break;
                case Hatch hatch:
                    this.DrawHatch(context, style, hatch);
                    break;
                case Insert insert:
                    this.DrawBlockContents(context, insert, layer, resolved);
                    break;
                case MLine mline:
                    this.DrawMLine(context, style, resolved, mline, placement);
                    break;
                case Wipeout wipeout:
                    this.DrawWipeout(context, style, wipeout);
                    break;
                default:
                    this._configuration.Notify($"[{entity.SubclassMarker}] Drawing not implemented.", NotificationType.NotImplemented);
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or ArithmeticException)
        {
            // A malformed entity (ACadSharp throws for a bulge between coincident vertices, for example) must not take the page down with it.
            // ArithmeticException is ImageSharp's: its scan-line fill rejects a non-finite vertex that slipped past HasFiniteGeometry.
            this._configuration.Notify(
                $"[{entity.SubclassMarker}] Handle {entity.Handle.ToString("X", CultureInfo.InvariantCulture)}: geometry could not be computed ({ex.Message}); entity skipped.",
                NotificationType.Warning,
                ex);
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

    private static LayerRenderInfo CreateLayerInfo(Layer? layer, string layerName, ImageRenderContext context, ImageColor foreground)
    {
        if (layer == null)
        {
            return new LayerRenderInfo(layerName, foreground, context.ToStrokeWidth(LineWeightType.Default));
        }

        return new LayerRenderInfo(layerName, layer.Color.ToImageColor(foreground), context.ToStrokeWidth(layer.LineWeight));
    }

    private void DrawPoint(ImageRenderContext context, ImageStyle style, ACadSharp.Entities.Point point)
    {
        // DotSizePixels is a pixel size; SVG surface units are drawing units, so it has to be converted.
        float radius = Math.Max(1f, this._configuration.DotSizePixels / 2f);
        context.Surface.FillCircle(style, context.ToSurfacePoint(point.Location), context.ToSurfacePixels(radius));
    }

    private void DrawDimension(ImageRenderContext context, Dimension dimension, Layer? layer, ResolvedStyle parent)
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

            this.Draw(context, entity, layer, dimension.Handle, blockName: null, parent);
        }
    }

    /// <summary>
    /// Fills a solid's four corners. The corners are OCS coordinates (ACadSharp leaves the normal to the caller), so a
    /// non-world normal is applied first, with each corner's Z as its elevation, and only then the insert transform
    /// that placed it (null at top level, since <c>Explode()</c> already transformed a world-plane clone). DXF SOLID
    /// stores corners in a Z pattern (first edge 1-2, opposite edge 3-4), so they are filled in order 1-2-4-3, not
    /// 1-2-3-4.
    /// </summary>
    private static void DrawSolid(ImageRenderContext context, ImageStyle style, Solid solid, Transform? placement)
    {
        OcsTransform? toWorld = IsWorldPlane(solid.Normal) ? null : OcsTransform.For(solid.Normal);
        SurfacePoint ToSurface(XYZ corner)
        {
            XYZ world = toWorld != null ? toWorld.ToWorld(corner.X, corner.Y, corner.Z) : corner;
            return context.ToSurfacePoint(InsertPlacement.MapPoint(placement, world));
        }

        SurfacePoint[] points =
        [
            ToSurface(solid.FirstCorner),
            ToSurface(solid.SecondCorner),
            ToSurface(solid.FourthCorner),
            ToSurface(solid.ThirdCorner),
        ];

        context.Surface.FillPolygon(style, points);
    }

    /// <summary>
    /// A 3DFACE is stroked edge by edge in plan view: edge n joins corner n to corner n+1 and edge 4 closes the ring;
    /// a triangle repeats its third corner, which makes edge 3 degenerate. Hidden edges (the invisible-edge flags)
    /// split the ring into open runs. Corners are world coordinates, so there is no OCS step.
    /// </summary>
    private static void DrawFace3D(ImageRenderContext context, ImageStyle style, Face3D face)
    {
        bool triangle = face.FourthCorner.Equals(face.ThirdCorner);
        XYZ[] corners = triangle
            ? [face.FirstCorner, face.SecondCorner, face.ThirdCorner]
            : [face.FirstCorner, face.SecondCorner, face.ThirdCorner, face.FourthCorner];
        bool[] hidden = triangle
            ? [face.Flags.HasFlag(InvisibleEdgeFlags.First), face.Flags.HasFlag(InvisibleEdgeFlags.Second), face.Flags.HasFlag(InvisibleEdgeFlags.Fourth)]
            : [face.Flags.HasFlag(InvisibleEdgeFlags.First), face.Flags.HasFlag(InvisibleEdgeFlags.Second), face.Flags.HasFlag(InvisibleEdgeFlags.Third), face.Flags.HasFlag(InvisibleEdgeFlags.Fourth)];

        int count = corners.Length;
        int firstHidden = Array.IndexOf(hidden, true);
        if (firstHidden < 0)
        {
            context.Surface.DrawPolyline(style, corners.Select(context.ToSurfacePoint).ToArray(), true);
            return;
        }

        // Start just after a hidden edge so no visible run wraps around the ring.
        List<SurfacePoint> run = new(count + 1);
        for (int step = 1; step <= count; step++)
        {
            int edge = (firstHidden + step) % count;
            if (hidden[edge])
            {
                Flush();
                continue;
            }

            if (run.Count == 0)
            {
                run.Add(context.ToSurfacePoint(corners[edge]));
            }

            run.Add(context.ToSurfacePoint(corners[(edge + 1) % count]));
        }

        Flush();

        void Flush()
        {
            if (run.Count >= 2)
            {
                context.Surface.DrawPolyline(style, run.ToArray(), false);
            }

            run.Clear();
        }
    }

    /// <summary>
    /// True when an entity's extrusion is the world Z axis, so its OCS coordinates are already world coordinates.
    /// </summary>
    /// <remarks>
    /// Native curve output uses the raw centre, radii and angles; ACadSharp applies the OCS transform only inside
    /// <c>PolygonalVertexes</c>. Anything but the default normal (a <c>(0,0,-1)</c> extrusion mirrors X, for example)
    /// therefore has to fall back to the tessellating path. Polylines, hatches and solids are never transformed by ACadSharp
    /// at all, so their points go through <see cref="OcsTransform"/> instead.
    /// </remarks>
    private static bool IsWorldPlane(XYZ normal) => OcsTransform.IsWorldPlane(normal);

    /// <summary>
    /// Tessellated polyline points in world XY. A polyline on the world plane keeps ACadSharp's points untouched (the
    /// raster output depends on that exact sequence); any other normal is brought into world space first, since
    /// <c>GetPoints</c> returns raw OCS vertices.
    /// </summary>
    private IEnumerable<XY> PolylinePoints(IPolyline polyline)
    {
        IEnumerable<XYZ> points = polyline.GetPoints<XYZ>(this._configuration.ArcPrecision);
        if (IsWorldPlane(polyline.Normal))
        {
            return points.Select(v => v.Convert<XY>());
        }

        OcsTransform toWorld = OcsTransform.For(polyline.Normal);
        double elevation = polyline.Elevation;
        return points.Select(p => toWorld.ToWorldXY(p.X, p.Y, elevation));
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
        Face3D face => IsFinite(face.FirstCorner) && IsFinite(face.SecondCorner) && IsFinite(face.ThirdCorner) && IsFinite(face.FourthCorner),
        Leader leader => leader.Vertices.All(IsFinite),
        // Every value that reaches a fill point has to be covered, not just the positions: Parameters[0] is the
        // element offset along the miter, and the clip vertices are mapped through WipeoutPixelToWorld.
        MLine mline => mline.Vertices.All(v => IsFinite(v.Position) && IsFinite(v.Miter)
            && v.Segments.All(s => s.Parameters.Count == 0 || double.IsFinite(s.Parameters[0]))),
        Wipeout wipeout => IsFinite(wipeout.InsertPoint) && IsFinite(wipeout.UVector) && IsFinite(wipeout.VVector)
            && double.IsFinite(wipeout.Size.X) && double.IsFinite(wipeout.Size.Y)
            && wipeout.ClipBoundaryVertices.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)),
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

    /// <summary>
    /// A leader is its stored path (the hookline is already the last vertex; the annotation is a separate entity)
    /// plus, when enabled, AutoCAD's default closed filled arrowhead at the first vertex: an isosceles triangle
    /// DIMASZ x DIMSCALE long and a third of that wide. A splined leader runs a Catmull-Rom curve through its
    /// vertices. Custom arrowhead blocks fall back to the default triangle with a notification. Path and arrowhead
    /// are built in the leader's own coordinates and mapped through <paramref name="placement"/> (null at top level)
    /// last, so a leader inside a scaled or rotated insert scales and rotates with it.
    /// </summary>
    private void DrawLeader(ImageRenderContext context, ImageStyle style, Leader leader, Transform? placement)
    {
        if (leader.Vertices.Count < 2)
        {
            return;
        }

        SurfacePoint Map(XYZ p) => context.ToSurfacePoint(InsertPlacement.MapPoint(placement, p));

        SurfacePoint[] points = leader.Vertices.Select(Map).ToArray();
        if (leader.PathType == LeaderPathType.Spline && points.Length > 2)
        {
            // Catmull-Rom control points are affine combinations of the input points, so mapping the vertices first
            // and building the curve from the mapped points gives the same result as building it in source space and
            // mapping every control point afterward.
            context.Surface.DrawCubicBezier(style, CatmullRomToBezier(points), false);
        }
        else
        {
            context.Surface.DrawPolyline(style, points, false);
        }

        if (!leader.ArrowHeadEnabled)
        {
            return;
        }

        double size = leader.Style.ArrowSize * (leader.Style.ScaleFactor > 0d ? leader.Style.ScaleFactor : 1d);
        XY tip = leader.Vertices[0].Convert<XY>();
        XY direction = tip - leader.Vertices[1].Convert<XY>();
        double length = direction.GetLength();

        // Every comparison with NaN is false, so the size has to be tested for finiteness explicitly. The degenerate
        // cases return before the custom-arrow notification, which would otherwise claim a substitute nobody drew.
        if (!double.IsFinite(size) || size <= 0d || length <= 0d)
        {
            return;
        }

        if (leader.Style.LeaderArrow != null)
        {
            this._configuration.Notify($"[{leader.SubclassMarker}] Arrowhead block '{leader.Style.LeaderArrow.Name}' is not rendered; the default closed arrow is drawn instead.", NotificationType.NotImplemented);
        }

        direction /= length;
        XY baseCenter = tip - (direction * size);
        XY half = new XY(-direction.Y, direction.X) * (size / 6d);
        XY baseLeft = baseCenter + half;
        XY baseRight = baseCenter - half;
        // The triangle is built flat (in the leader's own XY plane, ignoring any Z on the second vertex), but its
        // anchor must carry the first vertex's own Z so it maps to the same point as the path's own first vertex;
        // dropping it here would detach the arrow from the line under a placement whose normal couples Z into X/Y.
        double z = leader.Vertices[0].Z;
        context.Surface.FillPolygon(style, [Map(new XYZ(tip.X, tip.Y, z)), Map(new XYZ(baseLeft.X, baseLeft.Y, z)), Map(new XYZ(baseRight.X, baseRight.Y, z))]);
    }

    /// <summary>
    /// Control points (1 + 3n) of the cubic Bézier chain equivalent to a uniform Catmull-Rom spline through
    /// <paramref name="points"/>, with the end tangents clamped by repeating the end points.
    /// </summary>
    internal static SurfacePoint[] CatmullRomToBezier(IReadOnlyList<SurfacePoint> points)
    {
        int segments = points.Count - 1;
        SurfacePoint[] controls = new SurfacePoint[(segments * 3) + 1];
        controls[0] = points[0];
        for (int i = 0; i < segments; i++)
        {
            SurfacePoint previous = points[Math.Max(i - 1, 0)];
            SurfacePoint start = points[i];
            SurfacePoint end = points[i + 1];
            SurfacePoint next = points[Math.Min(i + 2, points.Count - 1)];
            controls[(3 * i) + 1] = new SurfacePoint(start.X + ((end.X - previous.X) / 6d), start.Y + ((end.Y - previous.Y) / 6d));
            controls[(3 * i) + 2] = new SurfacePoint(end.X - ((next.X - start.X) / 6d), end.Y - ((next.Y - start.Y) / 6d));
            controls[(3 * i) + 3] = end;
        }

        return controls;
    }

    /// <summary>
    /// The geometry stored in an MLINE's vertices is final: element j passes through
    /// <c>Position + Segments[j].Parameters[0] * Miter</c> at every vertex (DXF group 41), with justification and
    /// scale already applied by the writer. Vertices without parameters fall back to the style offsets with the
    /// justification shift, with a warning. Cuts made by MLEDIT (further group-41 values) are ignored with a
    /// warning; the elements stay continuous. Each element takes the style element's colour and linetype, falling
    /// back to the entity's own; a fill-on style fills the ring between the two outermost elements first. Square
    /// caps join the outermost elements at an open end unless the entity suppresses them; round and inner-arc
    /// caps and joints are not drawn.
    /// </summary>
    private void DrawMLine(ImageRenderContext context, ImageStyle style, ResolvedStyle resolved, MLine mline, Transform? placement)
    {
        IReadOnlyList<MLine.Vertex> vertices = mline.Vertices;
        MLineStyle.Element[] elements = mline.Style.Elements.ToArray();
        if (vertices.Count < 2 || elements.Length == 0)
        {
            // A genuinely degenerate MLINE returns silently, but a non-null placement means this is a block clone;
            // if it has no vertices here, snapshot/heal pairing failed to reach it, which would otherwise vanish
            // with no explanation.
            if (vertices.Count < 2 && placement != null)
            {
                this._configuration.Notify($"[{mline.SubclassMarker}] Handle {mline.Handle.ToString("X", CultureInfo.InvariantCulture)}: multiline has no vertices; skipped.", NotificationType.Warning);
            }

            return;
        }

        bool closed = mline.Flags.HasFlag(MLineFlags.Closed);
        double scale = mline.ScaleFactor == 0d ? 1d : mline.ScaleFactor;
        // Offsets are scaled before the extrema are taken: under a negative ScaleFactor, scaling flips which element
        // is geometrically outermost, so choosing extrema from the raw (unscaled) offsets would anchor Top/Bottom
        // justification (and pick the fill ring) at the wrong element.
        double[] scaled = elements.Select(e => e.Offset * scale).ToArray();
        string handle = mline.Handle.ToString("X", CultureInfo.InvariantCulture);
        if (!double.IsFinite(scale) || scaled.Any(v => !double.IsFinite(v)))
        {
            this._configuration.Notify($"[{mline.SubclassMarker}] Handle {handle}: multiline style has non-finite offsets or scale; entity skipped.", NotificationType.Warning);
            return;
        }

        double maxOffset = scaled.Max();
        double minOffset = scaled.Min();
        double shift = mline.Justification switch
        {
            MLineJustification.Top => -maxOffset,
            MLineJustification.Bottom => -minOffset,
            _ => 0d,
        };

        bool fallback = false;
        bool cuts = false;
        SurfacePoint[][] lines = new SurfacePoint[elements.Length][];
        for (int j = 0; j < elements.Length; j++)
        {
            lines[j] = new SurfacePoint[vertices.Count];
            for (int i = 0; i < vertices.Count; i++)
            {
                MLine.Vertex vertex = vertices[i];
                double along;
                if (j < vertex.Segments.Count && vertex.Segments[j].Parameters.Count > 0)
                {
                    along = vertex.Segments[j].Parameters[0];
                    cuts |= vertex.Segments[j].Parameters.Count > 2;
                }
                else
                {
                    along = scaled[j] + shift;
                    fallback = true;
                }

                XYZ world = vertex.Position + (vertex.Miter * along);
                lines[j][i] = context.ToSurfacePoint(InsertPlacement.MapPoint(placement, world));
            }
        }

        if (fallback)
        {
            this._configuration.Notify($"[{mline.SubclassMarker}] Handle {handle}: vertex parameters are missing; element offsets were computed from the style.", NotificationType.Warning);
        }

        if (cuts)
        {
            this._configuration.Notify($"[{mline.SubclassMarker}] Handle {handle}: cut segments are not rendered; elements are drawn continuous.", NotificationType.Warning);
        }

        ImageColor foreground = context.Configuration.ResolveForegroundColor();
        // scaled is already known finite (the non-finite check above returned early otherwise), so maxOffset and
        // minOffset, both drawn from it, are always found here; outer/inner are picked from the scaled offsets so
        // they name the geometrically outermost/innermost element even under a negative ScaleFactor.
        int outer = Array.FindIndex(scaled, v => v == maxOffset);
        int inner = Array.FindIndex(scaled, v => v == minOffset);
        bool hasRing = outer >= 0 && inner >= 0 && outer != inner;
        if (mline.Style.Flags.HasFlag(MLineStyleFlags.FillOn) && hasRing)
        {
            ImageStyle fill = style with { StrokeColor = ElementColor(mline.Style.FillColor), DashPattern = null };
            // An open MLINE's fill is the band between the two outer elements. A closed one needs the full annulus:
            // the outer and inner rings alone (as for the open case) leave the closing wall between the last and
            // first vertices outside the path, so a bridge back to the outer ring's start point turns it into a
            // keyhole that covers that wall too; the inner ring's reversal gives it the opposite winding, so both
            // nonzero and even-odd fill rules produce the ring, not its complement.
            SurfacePoint[] fillPoints = closed
                ? [.. lines[outer], lines[outer][0], lines[inner][0], .. Enumerable.Reverse(lines[inner])]
                : [.. lines[outer], .. Enumerable.Reverse(lines[inner])];
            context.Surface.FillPolygon(fill, fillPoints);
        }

        for (int j = 0; j < elements.Length; j++)
        {
            // An element linetype named ByLayer/ByBlock is not itself a drawable pattern: it means the element
            // inherits the entity's own resolved dashes, same as a null element linetype, rather than being handed
            // to the resolver, which would otherwise treat the placeholder name as an unknown (solid) linetype.
            LineType? elementType = elements[j].LineType;
            float[]? dashes = elementType == null
                || ImageStyleResolver.IsNamed(elementType, LineType.ByLayerName)
                || ImageStyleResolver.IsNamed(elementType, LineType.ByBlockName)
                ? style.DashPattern
                : LineTypeDashResolver.Resolve(elementType, resolved.Header, resolved.LineTypeScale, context, style.StrokeWidth);
            ImageStyle elementStyle = style with { StrokeColor = ElementColor(elements[j].Color), DashPattern = dashes };
            context.Surface.DrawPolyline(elementStyle, lines[j], closed);
        }

        if (!closed && hasRing)
        {
            if (mline.Style.Flags.HasFlag(MLineStyleFlags.StartSquareCap) && !mline.Flags.HasFlag(MLineFlags.NoStartCaps))
            {
                context.Surface.DrawLine(style, lines[outer][0], lines[inner][0]);
            }

            if (mline.Style.Flags.HasFlag(MLineStyleFlags.EndSquareCap) && !mline.Flags.HasFlag(MLineFlags.NoEndCaps))
            {
                context.Surface.DrawLine(style, lines[outer][^1], lines[inner][^1]);
            }
        }

        ImageColor ElementColor(ACadSharp.Color color) => color.IsByLayer || color.IsByBlock ? style.StrokeColor : color.ToImageColor(foreground);
    }

    /// <summary>
    /// A wipeout masks whatever was drawn before it: its clip boundary (or the whole image frame when clipping is
    /// off) is filled with the page background at full opacity, so the page must be drawn in the drawing's order.
    /// The frame is never stroked. An inverted clip (everything outside the boundary masked) and a background that is
    /// anything short of opaque cannot be honoured and are skipped with a notification.
    /// </summary>
    private void DrawWipeout(ImageRenderContext context, ImageStyle style, Wipeout wipeout)
    {
        // ShowImage and ClipMode.Inside are re-checked in WipeoutWorldBoundary (so it draws nothing when called
        // standalone from EntityBounds); a future skip condition belongs in both places, or the two can desync.
        if (!wipeout.Flags.HasFlag(ImageDisplayFlags.ShowImage))
        {
            return;
        }

        string handle = wipeout.Handle.ToString("X", CultureInfo.InvariantCulture);
        if (wipeout.ClipMode == ClipMode.Inside)
        {
            this._configuration.Notify($"[{wipeout.SubclassMarker}] Handle {handle}: inverted clip boundaries are not rendered.", NotificationType.NotImplemented);
            return;
        }

        ImageColor background = this._configuration.BackgroundColor;
        if (background.ToPixel<Rgba32>().A < 255)
        {
            // A translucent fill blends over what is underneath on the raster backend, while the SVG backend's Hex
            // drops the alpha and masks fully, so anything short of opaque is skipped rather than drawn two ways.
            this._configuration.Notify($"[{wipeout.SubclassMarker}] Handle {handle}: a wipeout needs an opaque background to mask; skipped.", NotificationType.Warning);
            return;
        }

        IReadOnlyList<XYZ> boundary = WipeoutWorldBoundary(wipeout);
        SurfacePoint[] points = boundary.Select(context.ToSurfacePoint).ToArray();
        context.Surface.FillPolygon(style with { StrokeColor = background, Opacity = 1f, DashPattern = null }, points);
    }

    /// <summary>
    /// The world polygon a wipeout masks: its clip boundary (a rectangular pair expanded to four corners) or the whole
    /// image frame when clipping is off, mapped through <see cref="WipeoutPixelToWorld"/>. Empty when the wipeout
    /// would draw nothing (image hidden or an inverted clip).
    /// </summary>
    internal static IReadOnlyList<XYZ> WipeoutWorldBoundary(Wipeout wipeout)
    {
        if (!wipeout.Flags.HasFlag(ImageDisplayFlags.ShowImage) || wipeout.ClipMode == ClipMode.Inside)
        {
            return [];
        }

        List<XY> pixels;
        if (wipeout.ClippingState && wipeout.ClipBoundaryVertices.Count >= 2)
        {
            if (wipeout.ClipType == ClipType.Rectangular || wipeout.ClipBoundaryVertices.Count == 2)
            {
                XY a = wipeout.ClipBoundaryVertices[0];
                XY b = wipeout.ClipBoundaryVertices[1];
                pixels = [a, new XY(b.X, a.Y), b, new XY(a.X, b.Y)];
            }
            else
            {
                pixels = wipeout.ClipBoundaryVertices.ToList();
            }
        }
        else
        {
            pixels = [new XY(-0.5, -0.5), new XY(wipeout.Size.X - 0.5, -0.5), new XY(wipeout.Size.X - 0.5, wipeout.Size.Y - 0.5), new XY(-0.5, wipeout.Size.Y - 0.5)];
        }

        return pixels.Select(p => WipeoutPixelToWorld(wipeout, p)).ToList();
    }

    /// <summary>
    /// Maps an image-space boundary vertex to world coordinates. Pixel (0,0) is the top-left pixel and Y grows
    /// downwards; <c>UVector</c> runs along the visual bottom and <c>VVector</c> up the visual left side, each one
    /// pixel long. The documented default boundary (-0.5,-0.5)..(Size-0.5) therefore covers exactly the image.
    /// </summary>
    internal static XYZ WipeoutPixelToWorld(CadWipeoutBase image, XY pixel)
        => image.InsertPoint + (image.UVector * (pixel.X + 0.5)) + (image.VVector * (image.Size.Y - pixel.Y - 0.5));

    /// <summary>
    /// True when an exploded <paramref name="clone"/> should be drawn from <paramref name="original"/>'s geometry,
    /// placed through the insert's transform, instead of the clone's own points: a TEXT or MTEXT (their alignment
    /// point and, for MTEXT, X axis are never transformed by <c>Explode()</c>), a LEADER (once healed, the clone
    /// shares the same local vertex list as the original, so either would draw identically; the original is used
    /// for consistency with TEXT, MTEXT and SOLID, not because it carries anything the clone lacks), or a SOLID
    /// whose normal is not the world Z axis (its OCS corners must be brought into world space before the insert
    /// transform, not after). The pairing requires <paramref name="original"/> to be the block entity at the
    /// clone's own index and of the same runtime type, since a mismatched index (an ATTDEF the clone stream
    /// skipped, for example) would pair the wrong entity.
    /// </summary>
    /// <param name="original">The block entity at the same index as <paramref name="clone"/>, or null past the end of the block's own entities.</param>
    /// <param name="clone">The entity <c>Explode()</c> produced.</param>
    /// <returns>True when <paramref name="clone"/> should be drawn from <paramref name="original"/> instead.</returns>
    private static bool UsesOriginalGeometry(Entity? original, Entity clone)
    {
        if (original == null || original.GetType() != clone.GetType())
        {
            return false;
        }

        if (original is TextEntity or MText or Leader)
        {
            return true;
        }

        return original is Solid solid && !IsWorldPlane(solid.Normal);
    }

    private void DrawBlockContents(ImageRenderContext context, Insert insert, Layer? layer, ResolvedStyle parent)
    {
        if (insert.Block == null)
        {
            this._configuration.Notify($"[{insert.SubclassMarker}] Handle {insert.Handle.ToString("X", CultureInfo.InvariantCulture)}: block reference has no block; skipped.", NotificationType.Warning);
            return;
        }

        // The exploded clones carry the block entities' own attributes but no owner or document; ByBlock and
        // layer-0 inheritance, and the header's LTSCALE, come from the insert's resolved style and effective layer.
        // ACadSharp 3.7.1's Explode() yields one clone per block entity, in order. Text geometry comes from the
        // original entity placed through the insert's transform, because the clones' alignment points and MTEXT
        // X axes are never transformed and mirrored inserts hand back world points with a flipped normal.
        Transform transform = insert.GetTransform();
        IReadOnlyList<Entity> originals = insert.Block.Entities.ToList();

        // ACadSharp 3.7.1's MLine.Clone() empties the vertex list an MLine shares with its source (by
        // MemberwiseClone), and Leader.Clone() shares its vertex list the same way but Explode()'s ApplyTransform
        // overwrites that shared list's contents (world coordinates) in place instead of emptying it; either way the
        // source document is left corrupted once Explode() runs, because the clone and its source are the very same
        // List object. Insert.Clone() deep-clones its entire block subtree, so exploding this insert destroys every
        // MLINE reachable through it, including ones nested inside a block placed inside this one, several levels
        // below anything Explode() itself returns, because cloning the nested Insert empties that MLINE's list the
        // moment it is cloned along the way; a nested LEADER's list, by contrast, is overwritten only when the
        // insert that directly contains it is the one exploded, so a deeply nested LEADER survives an ancestor's
        // Explode() unharmed and its snapshot below is a defensive backstop, not a load-bearing fix.
        // CollectSharedVertexLists walks the whole subtree (following nested Insert.Block references, not yet
        // cloned at this point) to snapshot every MLINE and LEADER before Explode() runs, and Heal repairs them
        // immediately after and again in `finally`. The repair is always in place (Clear + AddRange into the
        // *existing* list, never a reassignment): because a clone shares the very same list object as its source at
        // every depth, one in-place heal fixes the original and every clone below it at once; reassigning would
        // leave an outer level's shared list broken. The insert's transform still has to be applied manually to a
        // healed MLINE's or LEADER's points, because Explode()'s own ApplyTransform ran against the pre-heal list.
        Dictionary<MLine, List<MLine.Vertex>> mlineVertices = new();
        Dictionary<Leader, List<XYZ>> leaderVertices = new();
        // Walking the whole subtree just to find out there is nothing to snapshot is wasted work on every insert of
        // an MLINE/LEADER-free block; BlockSubtreeNeedsHeal answers that cheaply (memoised per block), so the actual
        // walk only runs when it can find something.
        if (this.BlockSubtreeNeedsHeal(insert.Block, new HashSet<BlockRecord>()))
        {
            CollectSharedVertexLists(insert.Block, mlineVertices, leaderVertices, new HashSet<BlockRecord>());
        }

        int index = 0;
        try
        {
            // Explode() is a lazy iterator and the heal must not be interleaved with the Clone() calls it makes, so
            // the clones are materialised (and held alive at once) only when there is something to heal.
            bool needsHeal = mlineVertices.Count > 0 || leaderVertices.Count > 0;
            IEnumerable<Entity> clones = needsHeal ? insert.Explode().ToList() : insert.Explode();
            if (needsHeal)
            {
                Heal(mlineVertices, leaderVertices);
            }

            foreach (Entity entity in clones)
            {
                Entity? original = index < originals.Count ? originals[index] : null;
                index++;
                if (entity is AttributeDefinition definition)
                {
                    // A non-constant ATTDEF is a template shown through its ATTRIB. A constant one is skipped too when
                    // an ATTRIB with its tag already exists (ACadSharp's Insert(BlockRecord) constructor emits one even
                    // for constant definitions, so the value would otherwise be drawn twice) or when ATTMODE/Hidden
                    // would hide it; DXF attribute tags are case-insensitive, so the tag comparison ignores case.
                    bool hasMatchingAttrib = insert.Attributes.Any(a => string.Equals(a.Tag, definition.Tag, StringComparison.OrdinalIgnoreCase));
                    if (!definition.Flags.HasFlag(AttributeFlags.Constant) || hasMatchingAttrib || !this.IsAttributeVisible(definition, insert, parent))
                    {
                        continue;
                    }
                }

                NormalizeExplodedClone(entity);
                Entity? source = null;
                Transform? entityPlacement = null;
                if (UsesOriginalGeometry(original, entity))
                {
                    source = original;
                    entityPlacement = transform;
                }
                else if (entity is MLine or Leader)
                {
                    entityPlacement = transform;
                }

                this.Draw(context, entity, layer, insert.Handle, insert.Block.Name, parent, source, entityPlacement);
            }
        }
        finally
        {
            Heal(mlineVertices, leaderVertices);
        }

        if (index != originals.Count)
        {
            this._configuration.Notify(
                $"[{insert.SubclassMarker}] Handle {insert.Handle.ToString("X", CultureInfo.InvariantCulture)}: block '{insert.Block.Name}' exploded into {index} entities but holds {originals.Count}; text inside it may be misplaced.",
                NotificationType.Warning);
        }

        this.DrawAttributes(context, insert, layer, parent);

        static void Heal(Dictionary<MLine, List<MLine.Vertex>> mlineSnapshot, Dictionary<Leader, List<XYZ>> leaderSnapshot)
        {
            foreach (KeyValuePair<MLine, List<MLine.Vertex>> pair in mlineSnapshot)
            {
                pair.Key.Vertices.Clear();
                pair.Key.Vertices.AddRange(pair.Value);
            }

            foreach (KeyValuePair<Leader, List<XYZ>> pair in leaderSnapshot)
            {
                pair.Key.Vertices.Clear();
                pair.Key.Vertices.AddRange(pair.Value);
            }
        }
    }

    /// <summary>
    /// Snapshots every MLINE's and LEADER's vertex list reachable from <paramref name="block"/>, following nested
    /// <see cref="Insert.Block"/> references. <see cref="Insert.Explode"/> deep-clones its entire block subtree, so
    /// an MLINE nested several blocks deep is corrupted by an ancestor insert's own explode even though it is never
    /// that ancestor's direct child, because its list is emptied the moment it is cloned; a nested LEADER's list, by
    /// contrast, is only overwritten when the insert that directly contains it is the one exploded, so snapshotting
    /// it here is a defensive backstop rather than the fix MLINE needs. This has to run, and capture the whole
    /// subtree, before that explode call.
    /// </summary>
    /// <param name="block">The block whose entities (and nested blocks) are searched.</param>
    /// <param name="mlineSnapshot">Receives one entry per MLINE found, keyed by the MLINE itself.</param>
    /// <param name="leaderSnapshot">Receives one entry per LEADER found, keyed by the LEADER itself.</param>
    /// <param name="visited">Blocks already walked, so a circular or diamond hierarchy is walked once.</param>
    private static void CollectSharedVertexLists(BlockRecord? block, Dictionary<MLine, List<MLine.Vertex>> mlineSnapshot, Dictionary<Leader, List<XYZ>> leaderSnapshot, HashSet<BlockRecord> visited)
    {
        if (block == null || !visited.Add(block))
        {
            return;
        }

        foreach (Entity entity in block.Entities)
        {
            switch (entity)
            {
                case MLine mline when !mlineSnapshot.ContainsKey(mline):
                    mlineSnapshot.Add(mline, new List<MLine.Vertex>(mline.Vertices));
                    break;
                case Leader leader when !leaderSnapshot.ContainsKey(leader):
                    leaderSnapshot.Add(leader, new List<XYZ>(leader.Vertices));
                    break;
                case Insert nestedInsert:
                    CollectSharedVertexLists(nestedInsert.Block, mlineSnapshot, leaderSnapshot, visited);
                    break;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="block"/>, or any block reachable from it through a nested <see cref="Insert.Block"/>,
    /// contains an MLINE or a LEADER — the entities <see cref="CollectSharedVertexLists"/> exists to snapshot.
    /// Answers are memoised per block in <see cref="_blocksNeedingHeal"/>, so an insert of a block already proven
    /// clean (or already proven to need healing) elsewhere on the page costs a dictionary lookup instead of a walk.
    /// </summary>
    /// <param name="block">The block to check, or null.</param>
    /// <param name="visited">Blocks already walked in this call, so a circular or diamond hierarchy is walked once.</param>
    /// <returns>True when the subtree contains an MLINE or a LEADER.</returns>
    private bool BlockSubtreeNeedsHeal(BlockRecord? block, HashSet<BlockRecord> visited)
    {
        return this.ScanBlockSubtree(block, visited).NeedsHeal;
    }

    /// <summary>
    /// The recursive core of <see cref="BlockSubtreeNeedsHeal"/>. Besides the answer, it reports whether the walk
    /// was cut short by a cycle: a truncated walk saw only part of the subtree, so its "clean" verdict must not be
    /// cached under <paramref name="block"/> — doing so would poison every future insert of this block with an
    /// answer taken from an incomplete scan. A "needs healing" verdict is always safe to cache, truncated or not:
    /// finding one MLINE/LEADER is a fact no missed branch can undo.
    /// </summary>
    /// <param name="block">The block to check, or null.</param>
    /// <param name="visited">Blocks already walked in this call, so a circular or diamond hierarchy is walked once.</param>
    /// <returns>Whether the subtree contains an MLINE or a LEADER, and whether a cycle cut the walk short.</returns>
    private (bool NeedsHeal, bool Truncated) ScanBlockSubtree(BlockRecord? block, HashSet<BlockRecord> visited)
    {
        if (block == null)
        {
            return (false, false);
        }

        if (this._blocksNeedingHeal.TryGetValue(block, out StrongBox<bool>? cached))
        {
            return (cached.Value, false);
        }

        if (!visited.Add(block))
        {
            // A block reachable from itself: treat the cyclic branch as clean rather than recurse forever, and tell
            // the caller this branch was truncated so it knows not to trust — or cache — a "clean" verdict built on
            // top of it.
            return (false, true);
        }

        bool needsHeal = false;
        bool truncated = false;
        foreach (Entity entity in block.Entities)
        {
            if (entity is MLine or Leader)
            {
                needsHeal = true;
                break;
            }

            if (entity is Insert nestedInsert)
            {
                (bool nestedNeedsHeal, bool nestedTruncated) = this.ScanBlockSubtree(nestedInsert.Block, visited);
                truncated |= nestedTruncated;
                if (nestedNeedsHeal)
                {
                    needsHeal = true;
                    break;
                }
            }
        }

        if (needsHeal || !truncated)
        {
            this._blocksNeedingHeal.AddOrUpdate(block, new StrongBox<bool>(needsHeal));
        }

        return (needsHeal, truncated);
    }

    /// <summary>
    /// ATTRIB entities store absolute coordinates in their own OCS (the insert's transform is already applied by
    /// the writer), so they go through the TEXT pipeline with no placement. Multi-line attributes are drawn from
    /// their single-line value.
    /// </summary>
    private void DrawAttributes(ImageRenderContext context, Insert insert, Layer? layer, ResolvedStyle parent)
    {
        foreach (AttributeEntity attribute in insert.Attributes)
        {
            if (this.IsAttributeVisible(attribute, insert, parent))
            {
                this.Draw(context, attribute, layer, insert.Handle, insert.Block?.Name, parent);
            }
        }
    }

    /// <summary>
    /// ATTMODE and the attribute's Hidden flag are drawing-visibility state, ignored under
    /// <see cref="LayerVisibilityMode.All"/> like entity invisibility; otherwise None hides every attribute,
    /// Normal hides the ones flagged Hidden and All shows them all.
    /// </summary>
    private bool IsAttributeVisible(AttributeBase attribute, Insert insert, ResolvedStyle parent)
    {
        if (this._configuration.LayerVisibility == LayerVisibilityMode.All)
        {
            return true;
        }

        // A nested insert exploded out of an outer block carries no Document of its own; its ATTMODE comes from
        // the outermost placing insert's header instead, via the resolved style that is threaded down for LTSCALE.
        CadHeader? header = insert.Document?.Header ?? parent.Header;
        AttributeVisibilityMode mode = header?.AttributeVisibility ?? AttributeVisibilityMode.Normal;
        return mode switch
        {
            AttributeVisibilityMode.None => false,
            AttributeVisibilityMode.All => true,
            _ => !attribute.Flags.HasFlag(AttributeFlags.Hidden),
        };
    }

    /// <summary>
    /// Hatch clones from <c>Insert.Explode()</c> carry world boundary points but the transformed normal (a mirrored
    /// insert gives <c>(0,0,-1)</c>); the renderer would apply that normal again. The points are already world, so
    /// the clone is marked as lying on the world plane. Clones are transient, so mutating them is safe.
    /// </summary>
    /// <param name="entity">The exploded clone to normalise.</param>
    private static void NormalizeExplodedClone(Entity entity)
    {
        if (entity is Hatch hatch && !IsWorldPlane(hatch.Normal))
        {
            hatch.Normal = XYZ.AxisZ;
        }
    }

    private void DrawHatch(ImageRenderContext context, ImageStyle style, Hatch hatch)
    {
        // Boundary paths and exploded pattern lines are OCS data; ACadSharp leaves the hatch normal to the caller.
        OcsTransform? toWorld = IsWorldPlane(hatch.Normal) ? null : OcsTransform.For(hatch.Normal);
        SurfacePoint ToSurface(XYZ point) => toWorld != null
            ? context.ToSurfacePoint(toWorld.ToWorldXY(point.X, point.Y, hatch.Elevation))
            : context.ToSurfacePoint(point);

        if (hatch.IsSolid || hatch.PatternType == HatchPatternType.SolidFill)
        {
            List<IReadOnlyList<SurfacePoint>> rings = new();
            foreach (Hatch.BoundaryPath path in hatch.Paths)
            {
                List<SurfacePoint> ring = new();
                foreach (XYZ point in path.GetPoints(this._configuration.ArcPrecision))
                {
                    ring.Add(ToSurface(point));
                }

                if (ring.Count >= 3)
                {
                    rings.Add(ring);
                }
            }

            if (rings.Count > 0)
            {
                context.Surface.FillPath(style, rings);
            }

            return;
        }

        if (hatch.Pattern == null)
        {
            this._configuration.Notify($"[{hatch.SubclassMarker}] Hatch pattern is not available.", NotificationType.Warning);
            return;
        }

        // ExplodePattern builds every line up front, so the cap has to be applied before calling it: a fine pattern
        // over a large boundary would otherwise allocate millions of entities before the first one is drawn.
        double scanLines = EstimateScanLines(hatch);
        if (scanLines > this._configuration.MaxHatchLines)
        {
            this._configuration.Notify(
                $"[{hatch.SubclassMarker}] Hatch pattern needs about {scanLines.ToString("F0", CultureInfo.InvariantCulture)} scan lines, more than MaxHatchLines ({this._configuration.MaxHatchLines.ToString(CultureInfo.InvariantCulture)}); hatch skipped.",
                NotificationType.Warning);
            return;
        }

        ImageStyle lineStyle = style with { DashPattern = null };
        int drawn = 0;
        foreach (Entity segment in hatch.ExplodePattern())
        {
            if (segment is not Line line)
            {
                continue;
            }

            if (drawn >= this._configuration.MaxHatchLines)
            {
                this._configuration.Notify($"[{hatch.SubclassMarker}] Hatch pattern exceeds {this._configuration.MaxHatchLines} lines; remaining lines were skipped.", NotificationType.Warning);
                return;
            }

            context.Surface.DrawLine(lineStyle, ToSurface(line.StartPoint), ToSurface(line.EndPoint));
            drawn++;
        }
    }

    /// <summary>
    /// Number of pattern scan lines <c>Hatch.ExplodePattern()</c> would sweep across the hatch's bounding box, using its
    /// own arithmetic (ACadSharp 3.7.1). Each scan line is clipped against every boundary edge and may emit several
    /// dashes, so this is the work the expansion costs, not the number of lines it draws.
    /// </summary>
    /// <param name="hatch">The pattern hatch.</param>
    /// <returns>The scan line count, or 0 when the pattern would not expand to anything.</returns>
    internal static double EstimateScanLines(Hatch hatch)
    {
        if (hatch.Pattern == null || hatch.Pattern.Lines.Count == 0 || hatch.Paths.Count == 0)
        {
            return 0d;
        }

        BoundingBox box = hatch.GetBoundingBox();
        if (!IsFinite(box.Min) || !IsFinite(box.Max))
        {
            return 0d;
        }

        XY[] corners =
        [
            new XY(box.Min.X, box.Min.Y),
            new XY(box.Min.X, box.Max.Y),
            new XY(box.Max.X, box.Min.Y),
            new XY(box.Max.X, box.Max.Y),
        ];

        double total = 0d;
        foreach (HatchPattern.Line patternLine in hatch.Pattern.Lines)
        {
            XY direction = patternLine.Direction;
            if (direction.IsZero())
            {
                continue;
            }

            XY normal = new(-direction.Y, direction.X);
            double minProjection = double.PositiveInfinity;
            double maxProjection = double.NegativeInfinity;
            foreach (XY corner in corners)
            {
                double projection = (corner.X * normal.X) + (corner.Y * normal.Y);
                minProjection = Math.Min(minProjection, projection);
                maxProjection = Math.Max(maxProjection, projection);
            }

            double offset = patternLine.LineOffset;
            if (Math.Abs(offset) <= MathHelper.Epsilon)
            {
                total += 1d;
                continue;
            }

            double origin = (patternLine.BasePoint.X * normal.X) + (patternLine.BasePoint.Y * normal.Y);
            double k1 = (minProjection - origin) / offset;
            double k2 = (maxProjection - origin) / offset;
            double first = Math.Floor(Math.Min(k1, k2)) - 1d;
            double last = Math.Ceiling(Math.Max(k1, k2)) + 1d;
            total += last - first + 1d;
        }

        return total;
    }
}
