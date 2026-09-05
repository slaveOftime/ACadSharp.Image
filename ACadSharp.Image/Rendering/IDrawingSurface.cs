namespace ACadSharp.Image.Rendering;

/// <summary>
/// Result of opening a viewport: the surface to draw into and where its origin sits relative to the parent.
/// </summary>
/// <param name="Surface">Surface that receives the viewport contents.</param>
/// <param name="OffsetX">X of the viewport's left edge in <paramref name="Surface"/> units.</param>
/// <param name="BottomY">Y of the viewport's bottom edge in <paramref name="Surface"/> units.</param>
internal readonly record struct ViewportSurface(IDrawingSurface Surface, double OffsetX, double BottomY);

/// <summary>
/// Backend-neutral drawing primitives. Coordinates are surface units with Y growing downward.
/// </summary>
internal interface IDrawingSurface : IDisposable
{
    /// <summary>
    /// True when the backend draws arcs, ellipses and bulges natively; false when it wants tessellated polylines.
    /// </summary>
    bool SupportsCurves { get; }

    /// <summary>
    /// Opens a scope for the entity about to be drawn.
    /// </summary>
    /// <remarks>
    /// Scopes nest: an <c>Insert</c> or <c>Dimension</c> opens a scope, draws nothing itself, and each nested entity
    /// opens its own scope inside it. Every <see cref="BeginEntity"/> is matched by an <see cref="EndEntity"/>, even
    /// when the entity type is unsupported.
    /// </remarks>
    void BeginEntity(EntityRenderInfo info, LayerRenderInfo layer);

    /// <summary>
    /// Closes the scope opened by the matching <see cref="BeginEntity"/>.
    /// </summary>
    void EndEntity();

    void DrawLine(ImageStyle style, SurfacePoint start, SurfacePoint end);

    void DrawPolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, bool closed);

    /// <summary>
    /// Draws an elliptical arc. Angles are radians in surface space (already sign-adjusted for the Y flip); a positive sweep turns clockwise on screen.
    /// </summary>
    void DrawArc(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation, double startAngle, double sweepAngle);

    void DrawEllipse(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation);

    /// <summary>
    /// Draws a chain of cubic Bezier segments given 3n+1 control points.
    /// </summary>
    /// <remarks>
    /// The raster backend ignores <paramref name="closed"/> (the chain ends where it starts for closed splines);
    /// structured backends may close the path.
    /// </remarks>
    void DrawCubicBezier(ImageStyle style, IReadOnlyList<SurfacePoint> controlPoints, bool closed);

    /// <summary>
    /// Draws a polyline whose segments may be circular arcs. <paramref name="bulges"/>[i] applies to the segment from points[i] to points[i+1]; 0 is straight.
    /// </summary>
    void DrawBulgePolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, IReadOnlyList<double> bulges, bool closed);

    void FillPolygon(ImageStyle style, IReadOnlyList<SurfacePoint> points);

    /// <summary>
    /// Fills several rings with the even-odd rule.
    /// </summary>
    void FillPath(ImageStyle style, IReadOnlyList<IReadOnlyList<SurfacePoint>> rings);

    void FillCircle(ImageStyle style, SurfacePoint center, double radius);

    void DrawText(ImageStyle style, SurfaceText text);

    /// <summary>
    /// Opens a clipped viewport region. <paramref name="bounds"/> is the viewport rectangle in this surface's units.
    /// </summary>
    ViewportSurface BeginViewport(SurfaceRect bounds);

    void EndViewport(ViewportSurface viewport);
}
