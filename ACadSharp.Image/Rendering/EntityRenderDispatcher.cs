using System.Globalization;
using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.Header;
using ACadSharp.Image.Extensions;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using ImageColor = SixLabors.ImageSharp.Color;

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

    public EntityRenderDispatcher(ImageConfiguration configuration)
    {
        this._configuration = configuration;
        this._splineRenderer = new SplineRenderer(configuration);
        this._styleResolver = new ImageStyleResolver();
        this._textRenderer = new TextRenderer();
        this._visibilityFilter = new EntityVisibilityFilter(configuration);
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

    // textSource is the original block entity a TEXT or MTEXT clone came from, whose geometry is used instead of the
    // clone's, and placement is the transform of the insert that placed it; both are null outside a block reference.
    private void Draw(ImageRenderContext context, Entity entity, Layer? parentLayer, ulong? parentHandle, string? blockName, ResolvedStyle? parent, Entity? textSource = null, Transform? placement = null)
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
                case Solid solid:
                    DrawSolid(context, style, solid);
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
                    this._textRenderer.Draw(context, style, textSource as MText ?? mtext, placement);
                    break;
                case TextEntity textEntity:
                    this._textRenderer.Draw(context, style, textSource as TextEntity ?? textEntity, placement);
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
                default:
                    this._configuration.Notify($"[{entity.SubclassMarker}] Drawing not implemented.", NotificationType.NotImplemented);
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // A malformed entity (ACadSharp throws for a bulge between coincident vertices, for example) must not take the page down with it.
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
    /// non-world normal is applied first, with each corner's Z as its elevation. DXF SOLID stores corners in a Z
    /// pattern (first edge 1-2, opposite edge 3-4), so they are filled in order 1-2-4-3, not 1-2-3-4.
    /// </summary>
    private static void DrawSolid(ImageRenderContext context, ImageStyle style, Solid solid)
    {
        OcsTransform? toWorld = IsWorldPlane(solid.Normal) ? null : OcsTransform.For(solid.Normal);
        SurfacePoint ToSurface(XYZ corner) => toWorld != null
            ? context.ToSurfacePoint(toWorld.ToWorldXY(corner.X, corner.Y, corner.Z))
            : context.ToSurfacePoint(corner);

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

    private void DrawBlockContents(ImageRenderContext context, Insert insert, Layer? layer, ResolvedStyle parent)
    {
        // The exploded clones carry the block entities' own attributes but no owner or document; ByBlock and
        // layer-0 inheritance, and the header's LTSCALE, come from the insert's resolved style and effective layer.
        // ACadSharp 3.7.1's Explode() yields one clone per block entity, in order. Text geometry comes from the
        // original entity placed through the insert's transform, because the clones' alignment points and MTEXT
        // X axes are never transformed and mirrored inserts hand back world points with a flipped normal.
        Transform transform = insert.GetTransform();
        IReadOnlyList<Entity> originals = insert.Block?.Entities.ToList() ?? (IReadOnlyList<Entity>)Array.Empty<Entity>();
        int index = 0;
        foreach (Entity entity in insert.Explode())
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
            bool placeText = original is TextEntity or MText && original.GetType() == entity.GetType();
            this.Draw(context, entity, layer, insert.Handle, insert.Block?.Name, parent, placeText ? original : null, placeText ? transform : null);
        }

        if (index != originals.Count)
        {
            this._configuration.Notify(
                $"[{insert.SubclassMarker}] Handle {insert.Handle.ToString("X", CultureInfo.InvariantCulture)}: block '{insert.Block?.Name}' exploded into {index} entities but holds {originals.Count}; text inside it may be misplaced.",
                NotificationType.Warning);
        }

        this.DrawAttributes(context, insert, layer, parent);
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
