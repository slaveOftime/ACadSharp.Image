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

    // source is the original block entity a clone came from, whose geometry is used instead of the clone's, and
    // placement is the transform of the insert that placed it. UsesOriginalGeometry's doc is the canonical list of
    // the types drawn from their original, and it is not repeated here.
    // Both are null outside a block reference, but they do not always travel together inside
    // one: an MLINE clone is always drawn with placement set and source null (UsesOriginalGeometry never recognises
    // an MLine original, since the heal already restores the clone's own vertices to local coordinates), and so is a
    // LEADER clone whose ordinal pairing with the block's original entities failed. A HATCH or WIPEOUT clone has no
    // such fallback: when its pairing fails it is drawn with neither source nor placement, from its own
    // un-normalised clone geometry (see the count-mismatch Warning in DrawBlockContents).
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
                    this.DrawLeader(context, style, resolved, layer, source as Leader ?? leader, placement);
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
                case AttributeBase attribute when attribute.AttributeType is AttributeType.MultiLine or AttributeType.ConstantMultiLine:
                    this._textRenderer.DrawAttribute(context, style, source as AttributeBase ?? attribute, placement);
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
                    this.DrawHatch(context, style, source as Hatch ?? hatch, placement);
                    break;
                case Insert insert:
                    this.DrawBlockContents(context, insert, layer, resolved);
                    break;
                case MLine mline:
                    this.DrawMLine(context, style, resolved, mline, placement);
                    break;
                case Wipeout wipeout:
                    this.DrawWipeout(context, style, source as Wipeout ?? wipeout, placement);
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

    /// <summary>
    /// Draws a dimension from the picture block ACadSharp generates for it, generating that block first when the
    /// drawing did not store one.
    /// </summary>
    /// <remarks>
    /// <c>UpdateBlock()</c> is the second place in the renderer, after <see cref="DrawArrowBlock"/>, that makes
    /// ACadSharp construct an <c>Insert</c> of a block the caller owns: for a linear or aligned dimension it builds
    /// one of each of the style's arrow blocks. ACadSharp 3.7.1's <c>Insert(BlockRecord)</c> constructor clones a
    /// document-owned block's entities, so that call empties the vertex list of any MLINE inside one of them —
    /// measured at two vertices to none with no renderer involved. This is the top-level dimension path, where
    /// nothing else takes a snapshot: a dimension reached through a block reference is covered by
    /// <see cref="DrawBlockContents"/>'s own snapshot and its <c>finally</c> heal, but the entity-type switch in
    /// <c>Draw</c> routes a top-level dimension straight here. The heal is in a <c>finally</c> for the same reason
    /// it is there, so a throw while generating the picture cannot leave the caller's document broken. Page framing
    /// runs ahead of every draw and does not get there first: <c>Dimension.GetBoundingBox()</c> was probed to leave
    /// <c>Block</c> null, so <see cref="EntityBounds"/> never reaches the constructor.
    /// <para>
    /// The cycle pre-check is not symmetry: the clone that constructor performs is the same deep clone
    /// <c>Explode()</c> performs, so an arrow block reachable from itself exhausts the stack inside ACadSharp before
    /// <c>UpdateBlock()</c> returns, and a <c>StackOverflowException</c> cannot be caught. The dimension's own
    /// picture block is not among the blocks checked, because this branch only runs when there is not one yet.
    /// </para>
    /// </remarks>
    private void DrawDimension(ImageRenderContext context, Dimension dimension, Layer? layer, ResolvedStyle parent)
    {
        string handle = dimension.Handle.ToString("X", CultureInfo.InvariantCulture);
        BlockRecord? block = dimension.Block;
        if (block == null)
        {
            Dictionary<MLine, List<MLine.Vertex>> mlineVertices = new();
            Dictionary<Leader, List<XYZ>> leaderVertices = new();
            HashSet<BlockRecord> collected = new();
            foreach (BlockRecord referenced in ReferencedBlocks(dimension))
            {
                if (BlockGraphIsCircular(referenced))
                {
                    this._configuration.Notify($"[{dimension.SubclassMarker}] Handle {handle}: block '{referenced.Name}' references itself; dimension skipped.", NotificationType.Warning);
                    return;
                }

                if (this.BlockSubtreeNeedsHeal(referenced, new HashSet<BlockRecord>()))
                {
                    CollectSharedVertexLists(referenced, mlineVertices, leaderVertices, collected);
                }
            }

            try
            {
                dimension.UpdateBlock();
            }
            finally
            {
                Heal(mlineVertices, leaderVertices);
            }

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
        // element offset along the miter, the values after it are cut positions that place the ends of a run, and
        // the clip vertices are mapped through WipeoutPixelToWorld.
        MLine mline => mline.Vertices.All(v => IsFinite(v.Position) && IsFinite(v.Miter)
            && v.Segments.All(s => s.Parameters.All(double.IsFinite))),
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
    /// vertices. A custom arrowhead block is drawn in the triangle's place by <see cref="DrawArrowBlock"/>, which
    /// falls back to the triangle when the block cannot be placed. Path and arrowhead are built in the leader's own
    /// coordinates and mapped through <paramref name="placement"/> (null at top level) last, so a leader inside a
    /// scaled or rotated insert scales and rotates with it.
    /// </summary>
    /// <param name="context">The context that maps drawing units onto the surface.</param>
    /// <param name="style">The leader's stroke and fill style.</param>
    /// <param name="resolved">The leader's resolved style, which a custom arrowhead's ByBlock entities inherit.</param>
    /// <param name="layer">The leader's effective layer, which a custom arrowhead's layer-0 entities inherit.</param>
    /// <param name="leader">The leader to draw.</param>
    /// <param name="placement">The transform of the insert that placed the leader, or null at top level.</param>
    private void DrawLeader(ImageRenderContext context, ImageStyle style, ResolvedStyle resolved, Layer? layer, Leader leader, Transform? placement)
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

        direction /= length;
        double tipZ = leader.Vertices[0].Z;
        if (leader.Style.LeaderArrow != null
            && this.DrawArrowBlock(context, layer, resolved, leader, leader.Style.LeaderArrow, tip, direction, size, tipZ, placement))
        {
            return;
        }

        XY baseCenter = tip - (direction * size);
        XY half = new XY(-direction.Y, direction.X) * (size / 6d);
        XY baseLeft = baseCenter + half;
        XY baseRight = baseCenter - half;
        // The triangle is built flat (in the leader's own XY plane, ignoring any Z on the second vertex), but its
        // anchor must carry the first vertex's own Z so it maps to the same point as the path's own first vertex;
        // dropping it here would detach the arrow from the line under a placement whose normal couples Z into X/Y.
        context.Surface.FillPolygon(style, [Map(new XYZ(tip.X, tip.Y, tipZ)), Map(new XYZ(baseLeft.X, baseLeft.Y, tipZ)), Map(new XYZ(baseRight.X, baseRight.Y, tipZ))]);
    }

    /// <summary>
    /// Draws a custom arrowhead block at a leader's tip: the block's base point goes to the tip, its local +X axis
    /// turns to point outward along <paramref name="direction"/>, and it is scaled by <paramref name="size"/>, all
    /// composed with the placement of the block reference that placed the leader.
    /// </summary>
    /// <param name="context">The context that maps drawing units onto the surface.</param>
    /// <param name="layer">The leader's effective layer, which the arrow's layer-0 entities inherit.</param>
    /// <param name="parent">The leader's resolved style, which the arrow's ByBlock entities inherit.</param>
    /// <param name="leader">The leader the arrow belongs to, for notifications.</param>
    /// <param name="arrow">The arrow block.</param>
    /// <param name="tip">The leader's first vertex, in the leader's own coordinates.</param>
    /// <param name="direction">The outward unit direction at the tip, in the leader's own coordinates.</param>
    /// <param name="size">The arrow size, already multiplied by the dimension style's overall scale.</param>
    /// <param name="z">The tip's own Z, so a leader off the world plane keeps its arrow attached to its line.</param>
    /// <param name="placement">The transform of the insert that placed the leader, or null at top level.</param>
    /// <returns>True when the block was drawn; false when the caller should fall back to the default triangle.</returns>
    /// <remarks>
    /// The block is drawn by handing a transient <c>Insert</c> of it to the ordinary block-content path, rather than
    /// by walking its entities with a transform: most entity types are drawn from their own stored points and ignore
    /// a placement, so only <c>Insert.Explode()</c> transforms an arbitrary block's contents correctly.
    /// <para>
    /// Two ACadSharp 3.7.1 behaviours shape the construction. An <c>Insert</c> cannot represent shear, so a composed
    /// transform that is not a planar similarity has no equivalent insert and the caller falls back. And
    /// <c>Insert.GetTransform()</c> computes <c>R * S * p + (InsertPoint - BasePoint)</c>, where AutoCAD specifies
    /// <c>InsertPoint + R * S * (p - BasePoint)</c>; the two agree only when the rotation and scale are identity, so
    /// the insertion point below is compensated to produce AutoCAD's placement. A package upgrade that corrects this
    /// will break <c>ACustomArrowHonoursANonZeroBlockBasePoint</c>, which is the intended tripwire.
    /// </para>
    /// </remarks>
    private bool DrawArrowBlock(ImageRenderContext context, Layer? layer, ResolvedStyle parent, Leader leader, BlockRecord arrow, XY tip, XY direction, double size, double z, Transform? placement)
    {
        string handle = leader.Handle.ToString("X", CultureInfo.InvariantCulture);
        if (arrow.Entities.Count == 0)
        {
            this._configuration.Notify($"[{leader.SubclassMarker}] Handle {handle}: arrowhead block '{arrow.Name}' is empty; the default closed arrow is drawn instead.", NotificationType.Warning);
            return false;
        }

        if (BlockGraphIsCircular(arrow))
        {
            this._configuration.Notify($"[{leader.SubclassMarker}] Handle {handle}: arrowhead block '{arrow.Name}' references itself; the default closed arrow is drawn instead.", NotificationType.Warning);
            return false;
        }

        // The map the arrow block's own coordinates must go through: base point to the tip, local +X onto the
        // outward direction, scaled by the arrow size, and then the outer placement.
        XYZ basePoint = arrow.BlockEntity.BasePoint;
        XY across = new(-direction.Y, direction.X);
        XYZ Arrow(XYZ p)
        {
            XY local = new(p.X - basePoint.X, p.Y - basePoint.Y);
            XY placed = tip + (direction * (local.X * size)) + (across * (local.Y * size));
            return InsertPlacement.MapPoint(placement, new XYZ(placed.X, placed.Y, z + ((p.Z - basePoint.Z) * size)));
        }

        // The arrow's own map is a rotation and one uniform scale, so the composition is a similarity exactly when
        // the outer placement is one. Testing the outer placement directly also catches the case a length-only check
        // misses: a non-uniform scale turned 45 degrees leaves both axes the same length but not at right angles.
        if (!InsertPlacement.TryGetPlanarSimilarity(placement, out double outerScale, out _, out _))
        {
            this._configuration.Notify($"[{leader.SubclassMarker}] Handle {handle}: arrowhead block '{arrow.Name}' cannot be placed under a non-uniform transform; the default closed arrow is drawn instead.", NotificationType.Warning);
            return false;
        }

        XYZ origin = Arrow(basePoint);
        XYZ ex = Arrow(basePoint + XYZ.AxisX) - origin;
        XYZ ey = Arrow(basePoint + XYZ.AxisY) - origin;
        double scale = size * outerScale;
        bool mirrored = (ex.X * ey.Y) - (ex.Y * ey.X) < 0d;
        double rotation = Math.Atan2(ex.Y, ex.X);
        if (!double.IsFinite(scale) || scale < 1e-12 || !double.IsFinite(rotation))
        {
            this._configuration.Notify($"[{leader.SubclassMarker}] Handle {handle}: arrowhead block '{arrow.Name}' has a degenerate size; the default closed arrow is drawn instead.", NotificationType.Warning);
            return false;
        }

        // ACadSharp 3.7.1's Insert(BlockRecord) constructor clones a document-owned block's entities, so merely
        // building the transient insert empties the vertex list of any MLINE in the arrow block (and of any MLINE
        // in a further arrowhead block below it). The snapshot therefore has to be taken before the constructor
        // runs, not inside DrawBlockContents, which only gets to look once the insert already exists.
        Dictionary<MLine, List<MLine.Vertex>> mlineVertices = new();
        Dictionary<Leader, List<XYZ>> leaderVertices = new();
        if (this.BlockSubtreeNeedsHeal(arrow, new HashSet<BlockRecord>()))
        {
            CollectSharedVertexLists(arrow, mlineVertices, leaderVertices, new HashSet<BlockRecord>());
        }

        try
        {
            // A reflection is expressed as a negative X scale, which turns the mapped X axis around, so the rotation
            // is taken half a turn further to bring it back.
            Insert transient = new(arrow)
            {
                Rotation = mirrored ? rotation + Math.PI : rotation,
                XScale = mirrored ? -scale : scale,
                YScale = scale,
                ZScale = scale,
                InsertPoint = origin,
            };
            transient.Attributes.Clear();

            // Repaired straight away, so DrawBlockContents takes its own snapshot from intact lists.
            Heal(mlineVertices, leaderVertices);

            // Where the block's base point actually lands under the insert as built, corrected by the difference.
            // Both formulas differ from the wanted placement by a translation that moves one for one with the
            // insertion point — their derivative with respect to it is the identity — so a single correction lands
            // the base point on the tip whichever one the package uses, which keeps this right if a later ACadSharp
            // fixes its own divergence from AutoCAD's documented insert semantics.
            XYZ landed = transient.GetTransform().ApplyTransform(basePoint);
            transient.InsertPoint = origin + (origin - landed);
            this.DrawBlockContents(context, transient, layer, parent, leader.Handle);
        }
        finally
        {
            Heal(mlineVertices, leaderVertices);
        }

        return true;
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
    /// justification shift, with a warning. Cuts made by MLEDIT (further group-41 values) break an element into the
    /// visible runs <see cref="VisibleRuns"/> computes, each drawn as its own line; an element with no usable cut
    /// position stays a single polyline so its linetype phase is unbroken. Fill cuts (group 42) are notified, not
    /// drawn. Each element takes the style element's colour and linetype, falling
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
        SurfacePoint[][] lines = new SurfacePoint[elements.Length][];
        // The same points before any placement or projection: cut positions are distances in the multiline's own
        // drawing units, so they can only be measured against a segment length taken in those units. Measuring the
        // placed points instead would leave a cut at its stored distance while the geometry around it scaled.
        XYZ[][] local = new XYZ[elements.Length][];
        for (int j = 0; j < elements.Length; j++)
        {
            lines[j] = new SurfacePoint[vertices.Count];
            local[j] = new XYZ[vertices.Count];
            for (int i = 0; i < vertices.Count; i++)
            {
                MLine.Vertex vertex = vertices[i];
                double along;
                if (j < vertex.Segments.Count && vertex.Segments[j].Parameters.Count > 0)
                {
                    along = vertex.Segments[j].Parameters[0];
                }
                else
                {
                    along = scaled[j] + shift;
                    fallback = true;
                }

                XYZ point = vertex.Position + (vertex.Miter * along);
                local[j][i] = point;
                lines[j][i] = context.ToSurfacePoint(InsertPlacement.MapPoint(placement, point));
            }
        }

        if (fallback)
        {
            this._configuration.Notify($"[{mline.SubclassMarker}] Handle {handle}: vertex parameters are missing; element offsets were computed from the style.", NotificationType.Warning);
        }

        if (vertices.Any(v => v.Segments.Any(s => s.AreaFillParameters.Count > 0)))
        {
            this._configuration.Notify($"[{mline.SubclassMarker}] Handle {handle}: fill cuts are not drawn; the filled band is continuous.", NotificationType.NotImplemented);
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

            // An uncut element stays one polyline: drawing it as a chain of separate lines would restart a dashed
            // linetype's phase at every vertex and would move every existing golden.
            if (!HasCut(j))
            {
                context.Surface.DrawPolyline(elementStyle, lines[j], closed);
                continue;
            }

            int lastVertex = closed ? vertices.Count : vertices.Count - 1;
            for (int i = 0; i < lastVertex; i++)
            {
                int next = (i + 1) % vertices.Count;
                SurfacePoint from = lines[j][i];
                SurfacePoint to = lines[j][next];
                foreach ((double t0, double t1) in RunFractions(j, i, next))
                {
                    SurfacePoint a = new(from.X + ((to.X - from.X) * t0), from.Y + ((to.Y - from.Y) * t0));
                    SurfacePoint b = new(from.X + ((to.X - from.X) * t1), from.Y + ((to.Y - from.Y) * t1));
                    context.Surface.DrawLine(elementStyle, a, b);
                }
            }
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

        // The visible runs of one segment, as fractions of its length. The stored cut positions are distances in
        // the multiline's own drawing units, so the segment they are measured against has to be the unplaced one;
        // the fractions are then applied to the already-placed and projected surface points, which is exact because
        // both steps are affine.
        IReadOnlyList<(double Start, double End)> RunFractions(int element, int from, int to)
        {
            double segmentLength = (local[element][to] - local[element][from]).GetLength();
            if (segmentLength <= 0d || !double.IsFinite(segmentLength))
            {
                // A zero-length segment (coincident vertices) has nothing to cut: report one full run so the element
                // is not pushed onto the per-run path, where it would lose its linetype phase for no reason.
                return [(0d, 1d)];
            }

            IReadOnlyList<double> parameters = element < vertices[from].Segments.Count ? vertices[from].Segments[element].Parameters : [];
            return VisibleRuns(parameters, segmentLength).Select(run => (run.Start / segmentLength, run.End / segmentLength)).ToList();
        }

        // Whether any segment of this element is broken, i.e. yields anything other than one run covering the whole
        // segment. An unbroken element keeps its single polyline.
        bool HasCut(int element)
        {
            int lastVertex = closed ? vertices.Count : vertices.Count - 1;
            for (int i = 0; i < lastVertex; i++)
            {
                IReadOnlyList<(double Start, double End)> runs = RunFractions(element, i, (i + 1) % vertices.Count);
                if (runs.Count != 1 || runs[0].Start > 1e-12 || runs[0].End < 1d - 1e-12)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The visible runs of one MLINE element, as distances from the element's own start. DXF group 41 stores, after
    /// the miter offset and the element's start offset, the positions at which the element breaks and resumes,
    /// alternating; an odd count leaves the element hidden to its end. Values are clamped to the element's length,
    /// and the list is cut short at the first value that is not finite or not greater than the one before it.
    /// </summary>
    /// <param name="parameters">The element's stored parameters, starting with the miter offset.</param>
    /// <param name="length">The element's length between this vertex and the next.</param>
    /// <returns>The visible runs, in order; a single full-length run when there are no usable cut positions.</returns>
    /// <remarks>
    /// Reading these as absolute positions is the literal sense of the DXF reference. ezdxf's model comments read the
    /// same array as relative dash and gap lengths, and neither ezdxf nor LibreDWG draws cuts at all, so no
    /// implementation settles it; the two readings agree only on a single cut. This is the interpretation the
    /// renderer implements and the README records it as unconfirmed.
    /// <para>
    /// <c>p[1]</c>, the offset from the miter intersection to the element's actual start, is not applied: runs are
    /// measured from the intersection, which is where the renderer already starts every element. Real values are a
    /// small fraction of a unit, so applying it would move existing output for no visible gain; it is recorded here
    /// so a later change is a deliberate one.
    /// </para>
    /// <para>
    /// The non-finite guard on a parameter value is unreachable through <c>Draw</c>, which skips a multiline with any
    /// non-finite parameter outright (<c>HasFiniteGeometry</c>); it is kept as a backstop for direct callers of this
    /// method, which is why the two policies differ — skipping the entity there, truncating the cut list here.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<(double Start, double End)> VisibleRuns(IReadOnlyList<double> parameters, double length)
    {
        if (!double.IsFinite(length) || length <= 0d)
        {
            return [];
        }

        List<double> breaks = new();
        double previous = 0d;
        for (int i = 2; i < parameters.Count; i++)
        {
            double value = parameters[i];
            if (!double.IsFinite(value) || value <= previous)
            {
                break;
            }

            if (value >= length)
            {
                break;
            }

            breaks.Add(value);
            previous = value;
        }

        if (breaks.Count == 0)
        {
            return [(0d, length)];
        }

        List<(double Start, double End)> runs = new();
        double start = 0d;
        for (int i = 0; i < breaks.Count; i += 2)
        {
            runs.Add((start, breaks[i]));
            start = i + 1 < breaks.Count ? breaks[i + 1] : double.NaN;
            if (double.IsNaN(start))
            {
                return runs;
            }
        }

        runs.Add((start, length));
        return runs;
    }

    /// <summary>
    /// A wipeout masks whatever was drawn before it: its clip boundary (or the whole image frame when clipping is
    /// off) is filled with the page background at full opacity, so the page must be drawn in the drawing's order.
    /// The frame is never stroked. An inverted clip masks the frame minus the boundary as a single even-odd path. A
    /// background that is anything short of opaque cannot be honoured and is skipped with a notification.
    /// </summary>
    private void DrawWipeout(ImageRenderContext context, ImageStyle style, Wipeout wipeout, Transform? placement)
    {
        if (!wipeout.Flags.HasFlag(ImageDisplayFlags.ShowImage))
        {
            return;
        }

        string handle = wipeout.Handle.ToString("X", CultureInfo.InvariantCulture);
        ImageColor background = this._configuration.BackgroundColor;
        if (background.ToPixel<Rgba32>().A < 255)
        {
            // A translucent fill blends over what is underneath on the raster backend, while the SVG backend's Hex
            // drops the alpha and masks fully, so anything short of opaque is skipped rather than drawn two ways.
            this._configuration.Notify($"[{wipeout.SubclassMarker}] Handle {handle}: a wipeout needs an opaque background to mask; skipped.", NotificationType.Warning);
            return;
        }

        IReadOnlyList<IReadOnlyList<XYZ>> rings = WipeoutWorldRings(wipeout, placement);
        if (rings.Count == 0)
        {
            return;
        }

        ImageStyle maskStyle = style with { StrokeColor = background, Opacity = 1f, DashPattern = null };
        if (rings.Count == 1)
        {
            context.Surface.FillPolygon(maskStyle, rings[0].Select(context.ToSurfacePoint).ToArray());
            return;
        }

        // An inverted clip masks everything except the boundary, which is the frame with the boundary as a hole: an
        // even-odd fill over both rings.
        context.Surface.FillPath(maskStyle, rings.Select(ring => (IReadOnlyList<SurfacePoint>)ring.Select(context.ToSurfacePoint).ToArray()).ToList());
    }

    /// <summary>
    /// The world rings a wipeout masks: none when the image is hidden, one when it masks a single region, and two —
    /// the whole image frame and the boundary inside it — for an inverted clip, which masks everything except the
    /// boundary. Clipping that is switched off masks the whole frame whatever the clip mode says.
    /// </summary>
    /// <param name="wipeout">The wipeout entity.</param>
    /// <param name="placement">The transform of the insert that placed it, or null at top level.</param>
    /// <returns>Zero, one or two rings of world points.</returns>
    /// <remarks>
    /// The insertion point is mapped as a point and the U and V vectors as directions, from the original entity:
    /// ACadSharp 3.7.1's <c>Wipeout.ApplyTransform</c> maps U and V as points, so a translated clone's vectors carry
    /// the translation and the mask is stretched across the drawing.
    /// </remarks>
    internal static IReadOnlyList<IReadOnlyList<XYZ>> WipeoutWorldRings(Wipeout wipeout, Transform? placement)
    {
        if (!wipeout.Flags.HasFlag(ImageDisplayFlags.ShowImage))
        {
            return [];
        }

        List<XY> frame =
        [
            new XY(-0.5, -0.5),
            new XY(wipeout.Size.X - 0.5, -0.5),
            new XY(wipeout.Size.X - 0.5, wipeout.Size.Y - 0.5),
            new XY(-0.5, wipeout.Size.Y - 0.5),
        ];

        if (!wipeout.ClippingState || wipeout.ClipBoundaryVertices.Count < 2)
        {
            return [Map(frame)];
        }

        List<XY> boundary;
        if (wipeout.ClipType == ClipType.Rectangular || wipeout.ClipBoundaryVertices.Count == 2)
        {
            XY a = wipeout.ClipBoundaryVertices[0];
            XY b = wipeout.ClipBoundaryVertices[1];
            boundary = [a, new XY(b.X, a.Y), b, new XY(a.X, b.Y)];
        }
        else
        {
            boundary = wipeout.ClipBoundaryVertices.ToList();
        }

        return wipeout.ClipMode == ClipMode.Inside
            ? [Map(frame), Map(boundary)]
            : [Map(boundary)];

        IReadOnlyList<XYZ> Map(IEnumerable<XY> pixels) => pixels.Select(p => WipeoutPixelToWorld(wipeout, p, placement)).ToList();
    }

    /// <summary>
    /// Maps an image-space boundary vertex to world coordinates. Pixel (0,0) is the top-left pixel and Y grows
    /// downwards; <c>UVector</c> runs along the visual bottom and <c>VVector</c> up the visual left side, each one
    /// pixel long. The documented default boundary (-0.5,-0.5)..(Size-0.5) therefore covers exactly the image. The
    /// insertion point is mapped as a point and the two vectors as directions.
    /// </summary>
    internal static XYZ WipeoutPixelToWorld(CadWipeoutBase image, XY pixel, Transform? placement)
    {
        XYZ insertPoint = InsertPlacement.MapPoint(placement, image.InsertPoint);
        XYZ u = InsertPlacement.MapVector(placement, image.UVector);
        XYZ v = InsertPlacement.MapVector(placement, image.VVector);
        return insertPoint + (u * (pixel.X + 0.5)) + (v * (image.Size.Y - pixel.Y - 0.5));
    }

    /// <summary>
    /// True when an exploded <paramref name="clone"/> should be drawn from <paramref name="original"/>'s geometry,
    /// placed through the insert's transform, instead of the clone's own points. This doc is the canonical list of
    /// the types that are drawn that way; the block-content path and <c>Draw</c> point here rather than repeat it.
    /// They are: a TEXT or MTEXT (their alignment point and, for MTEXT, X axis are never transformed by
    /// <c>Explode()</c>), an ATTRIB or ATTDEF (an
    /// <c>AttributeBase</c> is a <c>TextEntity</c>, so the TEXT arm covers it; in practice this is the constant
    /// ATTDEF <see cref="DrawBlockContents"/> draws — a non-constant one is a template and is skipped there — and a
    /// multi-line one is then drawn from the original's embedded MTEXT through the insert transform rather than
    /// block-local), a LEADER (once healed, the clone
    /// shares the same local vertex list as the original, so either would draw identically; the original is used
    /// for consistency with TEXT, MTEXT and SOLID, not because it carries anything the clone lacks), a SOLID whose
    /// normal is not the world Z axis (its OCS corners must be brought into world space before the insert
    /// transform, not after), a HATCH (its boundary and pattern are OCS data too, and <c>Hatch.ApplyTransform</c>
    /// maps the raw OCS boundary as if it were world data and never folds in <c>Elevation</c>, so the clone can
    /// never be trusted; only the original, drawn through its own OCS frame and then the placement, is correct), or
    /// a WIPEOUT (<c>Wipeout.ApplyTransform</c> maps its U and V vectors as points, so a translated clone's vectors
    /// carry the translation; only the original, mapped through <see cref="InsertPlacement.MapVector"/>, keeps them
    /// as directions).
    /// The pairing requires <paramref name="original"/> to be the block entity at the clone's own index and of the
    /// same runtime type, since a mismatched index (an ATTDEF the clone stream skipped, for example) would pair the
    /// wrong entity.
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

        if (original is TextEntity or MText or Leader or Hatch or Wipeout)
        {
            return true;
        }

        return original is Solid solid && !IsWorldPlane(solid.Normal);
    }

    /// <summary>
    /// Draws the contents of a block reference by exploding it, healing the vertex lists ACadSharp 3.7.1's clones
    /// share with their sources, and drawing every clone — some of them from the original block entity through the
    /// insert's transform rather than from the clone's own points. <see cref="UsesOriginalGeometry"/>'s doc is the
    /// canonical list of which types those are and why; it is not repeated here.
    /// </summary>
    /// <param name="context">The context that maps drawing units onto the surface.</param>
    /// <param name="insert">The block reference to draw the contents of.</param>
    /// <param name="layer">The insert's effective layer, which its layer-0 contents inherit.</param>
    /// <param name="parent">The insert's resolved style, which its ByBlock contents inherit.</param>
    /// <param name="parentHandleOverride">
    /// The handle to record as the contents' parent instead of the insert's own. Passed for the transient insert
    /// <see cref="DrawArrowBlock"/> builds, whose handle is zero and belongs to no entity in the drawing, so an
    /// arrowhead's parts point at the leader they belong to.
    /// </param>
    private void DrawBlockContents(ImageRenderContext context, Insert insert, Layer? layer, ResolvedStyle parent, ulong? parentHandleOverride = null)
    {
        if (insert.Block == null)
        {
            this._configuration.Notify($"[{insert.SubclassMarker}] Handle {insert.Handle.ToString("X", CultureInfo.InvariantCulture)}: block reference has no block; skipped.", NotificationType.Warning);
            return;
        }

        if (BlockGraphIsCircular(insert.Block))
        {
            this._configuration.Notify($"[{insert.SubclassMarker}] Handle {insert.Handle.ToString("X", CultureInfo.InvariantCulture)}: block '{insert.Block.Name}' references itself; skipped.", NotificationType.Warning);
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
        // CollectSharedVertexLists walks the whole subtree, following every edge ReferencedBlocks reports — nested
        // Insert.Block references, a DIMENSION's own picture block, and the four arrowhead blocks of a LEADER's or a
        // DIMENSION's style, none of them cloned at this point — to snapshot every MLINE and LEADER before
        // Explode() runs, and Heal repairs them
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

                this.Draw(context, entity, layer, parentHandleOverride ?? insert.Handle, insert.Block.Name, parent, source, entityPlacement);
            }
        }
        finally
        {
            Heal(mlineVertices, leaderVertices);
        }

        if (index != originals.Count)
        {
            this._configuration.Notify(
                $"[{insert.SubclassMarker}] Handle {insert.Handle.ToString("X", CultureInfo.InvariantCulture)}: block '{insert.Block.Name}' exploded into {index} entities but holds {originals.Count}; geometry drawn from originals inside it may be misplaced.",
                NotificationType.Warning);
        }

        this.DrawAttributes(context, insert, layer, parent);
    }

    /// <summary>
    /// Restores every snapshotted MLINE and LEADER vertex list in place (Clear + AddRange, never a reassignment):
    /// a clone shares the very same list object as its source at every depth, so one in-place repair fixes the
    /// original and every clone below it at once, where reassigning would leave an outer level's list broken.
    /// </summary>
    /// <param name="mlineSnapshot">The MLINE vertex lists captured before cloning.</param>
    /// <param name="leaderSnapshot">The LEADER vertex lists captured before cloning.</param>
    private static void Heal(Dictionary<MLine, List<MLine.Vertex>> mlineSnapshot, Dictionary<Leader, List<XYZ>> leaderSnapshot)
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

    /// <summary>
    /// The blocks <paramref name="entity"/> reaches when it is cloned, and the only edges the block-graph walks
    /// below follow:
    /// <list type="bullet">
    /// <item>a block reference's own <see cref="Insert.Block"/>;</item>
    /// <item>a DIMENSION's <see cref="Dimension.Block"/>, the anonymous block holding the picture ACadSharp
    /// generates for it — its lines, arrowheads and measurement text. <c>Dimension.Clone()</c> deep-clones it
    /// (probed on 3.7.1: an MLINE inside a picture block goes from two vertices to none across a single
    /// <c>Clone()</c>, and the clone's block is a different instance), so it corrupts a source document exactly as
    /// the arrowhead edges do. It differs from them in being on the ordinary render path — <c>DrawDimension</c>
    /// draws through it — rather than reachable only from an exotic file, and in being generated geometry rather
    /// than something the drawing's author named. When it is still null the dimension has not been generated yet
    /// and there is nothing to clone or to walk;</item>
    /// <item>every non-null block-valued property of a LEADER's or a DIMENSION's dimension style —
    /// <c>ArrowBlock</c> (DIMBLK), <c>DimArrow1</c> (DIMBLK1), <c>DimArrow2</c> (DIMBLK2) and <c>LeaderArrow</c>
    /// (DIMLDRBLK). ACadSharp 3.7.1's <c>DimensionStyle.Clone()</c> deep-clones all four, and <c>Leader</c> and
    /// <c>Dimension</c> both clone their style, so an MLINE inside any of them is emptied by a clone that never
    /// names it — whether or not the renderer ever draws that particular arrowhead, since only <c>LeaderArrow</c>
    /// is drawn.</item>
    /// </list>
    /// Every one of these edges is also one a cycle can run through, which is why the cycle walk consumes this
    /// enumerator too: each is followed by a deep clone that recurses, and a cycle through any of them exhausts the
    /// stack inside ACadSharp uncatchably. One block can be reached twice (the same record set as two arrowheads, or
    /// as both a nested insert and a dimension picture); de-duplication is left to the callers, which all track the
    /// blocks they have already walked.
    /// </summary>
    /// <param name="entity">The entity whose outgoing block references are wanted.</param>
    /// <returns>Each referenced block, possibly yielding the same block more than once.</returns>
    private static IEnumerable<BlockRecord> ReferencedBlocks(Entity entity)
    {
        if (entity is Insert insert)
        {
            if (insert.Block != null)
            {
                yield return insert.Block;
            }

            yield break;
        }

        DimensionStyle? style;
        if (entity is Dimension dimension)
        {
            if (dimension.Block != null)
            {
                yield return dimension.Block;
            }

            style = dimension.Style;
        }
        else if (entity is Leader leader)
        {
            style = leader.Style;
        }
        else
        {
            yield break;
        }

        if (style == null)
        {
            yield break;
        }

        if (style.ArrowBlock != null)
        {
            yield return style.ArrowBlock;
        }

        if (style.DimArrow1 != null)
        {
            yield return style.DimArrow1;
        }

        if (style.DimArrow2 != null)
        {
            yield return style.DimArrow2;
        }

        if (style.LeaderArrow != null)
        {
            yield return style.LeaderArrow;
        }
    }

    /// <summary>
    /// Snapshots every MLINE's and LEADER's vertex list reachable from <paramref name="block"/>, following every
    /// edge <see cref="ReferencedBlocks"/> reports: nested <see cref="Insert.Block"/> references, and all four
    /// arrowhead blocks of every LEADER's and DIMENSION's dimension style on the way.
    /// <see cref="Insert.Explode"/> deep-clones its entire block subtree, so an MLINE nested several blocks deep is
    /// corrupted by an ancestor insert's own explode even though it is never that ancestor's direct child, because
    /// its list is emptied the moment it is cloned; a nested LEADER's list, by contrast, is only overwritten when
    /// the insert that directly contains it is the one exploded, so snapshotting it here is a defensive backstop
    /// rather than the fix MLINE needs. Cloning a LEADER or a DIMENSION also clones its dimension style, and that
    /// clones all four of the style's arrowhead blocks, and cloning a DIMENSION clones its picture block too, which
    /// is how an MLINE inside a custom arrowhead — or inside a dimension's own generated geometry — is reached by a
    /// clone that never names it. This has to run, and capture the whole subtree, before the clone that
    /// corrupts those lists — the explode itself, or, for a document-owned block, the <c>Insert(BlockRecord)</c>
    /// constructor.
    /// </summary>
    /// <param name="block">The block whose entities, nested blocks, dimension pictures and dimension-style arrowhead blocks are searched.</param>
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
            }

            foreach (BlockRecord referenced in ReferencedBlocks(entity))
            {
                CollectSharedVertexLists(referenced, mlineSnapshot, leaderSnapshot, visited);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="block"/>, or any block reachable from it through the edges
    /// <see cref="ReferencedBlocks"/> reports, contains an MLINE, a LEADER or a DIMENSION. The first two are the
    /// entities <see cref="CollectSharedVertexLists"/> exists to snapshot; a DIMENSION carries none of its own but
    /// reaches its picture block and its style's arrowhead blocks, either of which may hold one, so it has to answer
    /// yes here or that walk would never be run.
    /// A LEADER answers yes for the same reason as well as for its own vertices. Over-approximating costs one
    /// wasted subtree walk and cannot lose a snapshot.
    /// Answers are memoised per block in <see cref="_blocksNeedingHeal"/>, so an insert of a block already proven
    /// clean (or already proven to need healing) elsewhere on the page costs a dictionary lookup instead of a walk.
    /// </summary>
    /// <param name="block">The block to check, or null.</param>
    /// <param name="visited">Blocks already walked in this call, so a circular or diamond hierarchy is walked once.</param>
    /// <returns>True when the subtree contains an MLINE, a LEADER or a DIMENSION.</returns>
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
    /// <returns>Whether the subtree contains an MLINE, a LEADER or a DIMENSION, and whether a cycle cut the walk short.</returns>
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
            // A DIMENSION is included even though it holds no vertex list of its own: it reaches its own picture
            // block and its style's arrowhead blocks, either of which may hold an MLINE, and a "clean" answer here
            // means no snapshot is ever taken.
            if (entity is MLine or Leader or Dimension)
            {
                needsHeal = true;
                break;
            }

            foreach (BlockRecord referenced in ReferencedBlocks(entity))
            {
                (bool nestedNeedsHeal, bool nestedTruncated) = this.ScanBlockSubtree(referenced, visited);
                truncated |= nestedTruncated;
                if (nestedNeedsHeal)
                {
                    needsHeal = true;
                    break;
                }
            }

            if (needsHeal)
            {
                break;
            }
        }

        if (needsHeal || !truncated)
        {
            this._blocksNeedingHeal.AddOrUpdate(block, new StrongBox<bool>(needsHeal));
        }

        return (needsHeal, truncated);
    }

    /// <summary>
    /// Whether a block's own graph contains a cycle, so that a reference to it cannot be exploded.
    /// </summary>
    /// <param name="block">The block a reference points at.</param>
    /// <returns>True when walking the block's nested references reaches a block already on the walk.</returns>
    /// <remarks>
    /// This walks the whole graph without stopping early, unlike the heal scan: a cycle can hide behind any branch,
    /// and an answer that stopped at the first interesting entity would miss it. Cycle detection itself is done with
    /// a set of the blocks on the *current path*, not a global one, so a diamond — two references to the same block
    /// from different places — is not mistaken for a cycle. A second set records the blocks already proven acyclic
    /// anywhere in this walk, which is what keeps a heavily shared DAG (each block holding two references to the
    /// next) from costing exponential time; it is sound because a block that reaches no cycle and no on-path
    /// ancestor from one path cannot reach one from another — if it could, that ancestor would be reachable from it
    /// and the first walk would already have come back to the block itself. It is scoped to the one call rather than
    /// held in a field, because the caller's document may change between renders.
    /// The edges followed are the ones <see cref="ReferencedBlocks"/> reports: a nested <c>Insert</c>, a DIMENSION's
    /// own picture block, and all four arrowhead blocks of a LEADER's or a DIMENSION's dimension style. Cloning
    /// either entity deep-clones its style and with it those blocks, and cloning a DIMENSION deep-clones its
    /// picture, so a leader inside its own arrowhead block — or a dimension whose picture places the block that
    /// holds the dimension — exhausts the stack in exactly the same way a self-referencing insert does. Refusing on
    /// the picture edge cannot cost a legitimate drawing: a picture block is geometry ACadSharp generates from the
    /// dimension's own definition points and never places the dimension's container in it, so a cycle there means a
    /// file that would otherwise take the process down. A picture block shared by two dimensions is a diamond, not
    /// a cycle, and the on-path set already tells the two apart.
    /// <para>
    /// It has to be answered before <c>Insert.Explode()</c> is called, not while drawing: exploding deep-clones the
    /// whole block graph, so a cycle exhausts the stack inside ACadSharp before the renderer sees a single entity,
    /// and a <c>StackOverflowException</c> cannot be caught in .NET — the process dies. A draw-time guard keyed on
    /// the block record could not recognise a nested level anyway, because the inserts reached down there hold
    /// deep-cloned records with a different identity at every level.
    /// </para>
    /// </remarks>
    internal static bool BlockGraphIsCircular(BlockRecord? block)
    {
        return block != null && Walk(block, new HashSet<BlockRecord>(), new HashSet<BlockRecord>());

        static bool Walk(BlockRecord block, HashSet<BlockRecord> onPath, HashSet<BlockRecord> acyclic)
        {
            if (acyclic.Contains(block))
            {
                return false;
            }

            if (!onPath.Add(block))
            {
                return true;
            }

            try
            {
                foreach (Entity entity in block.Entities)
                {
                    foreach (BlockRecord reached in ReferencedBlocks(entity))
                    {
                        if (Walk(reached, onPath, acyclic))
                        {
                            return true;
                        }
                    }
                }

                acyclic.Add(block);
                return false;
            }
            finally
            {
                onPath.Remove(block);
            }
        }
    }

    /// <summary>
    /// ATTRIB entities store absolute coordinates in their own OCS (the insert's transform is already applied by
    /// the writer), so they go through the TEXT (or, for a multi-line attribute, MTEXT) pipeline with no placement.
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
    /// A solid hatch fills its boundary loops (<c>path.GetPoints</c>) with the even-odd rule; a pattern hatch draws
    /// each line <c>ExplodePattern()</c> yields, capped at <see cref="ImageConfiguration.MaxHatchLines"/>. Boundary
    /// and pattern points are drawn from the original block entity in its own OCS (its normal and elevation), then
    /// mapped through <paramref name="placement"/> (null at top level), never from an exploded clone — except when
    /// ordinal pairing fails, in which case <paramref name="hatch"/> is the clone itself and <paramref name="placement"/>
    /// is null (see <see cref="UsesOriginalGeometry"/> and the count-mismatch Warning in <c>DrawBlockContents</c>).
    /// </summary>
    private void DrawHatch(ImageRenderContext context, ImageStyle style, Hatch hatch, Transform? placement)
    {
        // Boundary paths and exploded pattern lines are OCS data; the OCS frame and the entity's own elevation are
        // applied here and the insert transform after them, because ACadSharp 3.7.1's Hatch.ApplyTransform maps the
        // raw OCS boundary as if it were world data and never folds the elevation in, so a clone from a block cannot
        // be trusted for a hatch on a tilted plane.
        OcsTransform? toWorld = IsWorldPlane(hatch.Normal) ? null : OcsTransform.For(hatch.Normal);
        SurfacePoint ToSurface(XYZ point) => context.ToSurfacePoint(InsertPlacement.MapOcsPoint(placement, toWorld, hatch.Elevation, point));

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
