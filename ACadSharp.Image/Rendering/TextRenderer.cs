using ACadSharp.Entities;
using CSMath;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Converts <see cref="MText"/> and <see cref="TextEntity"/> into <see cref="SurfaceText"/> runs and hands them to the surface.
/// </summary>
internal sealed class TextRenderer
{
    /// <summary>
    /// Draws a multiline text entity.
    /// </summary>
    /// <param name="context">The context that maps drawing units onto the surface.</param>
    /// <param name="style">The resolved style for the entity.</param>
    /// <param name="mtext">The entity to draw.</param>
    public void Draw(ImageRenderContext context, ImageStyle style, MText mtext) => this.Draw(context, style, mtext, null);

    /// <summary>
    /// Draws a multiline text entity placed by a block reference.
    /// </summary>
    /// <param name="context">The context that maps drawing units onto the surface.</param>
    /// <param name="style">The resolved style for the entity.</param>
    /// <param name="mtext">The entity to draw.</param>
    /// <param name="placement">The transform of the insert that placed the entity, or null at top level.</param>
    /// <remarks>
    /// An MTEXT insertion point and X axis are WCS in DXF. The insert transform is applied here, by the renderer,
    /// because ACadSharp 3.7.1's <c>Insert.Explode()</c> moves the insertion point but leaves the X axis untouched,
    /// so an MTEXT inside a rotated insert would otherwise be drawn unrotated.
    /// </remarks>
    public void Draw(ImageRenderContext context, ImageStyle style, MText mtext, Transform? placement)
    {
        string text = NormalizeText(mtext.PlainText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        XYZ xAxis = mtext.AlignmentPoint;
        if (xAxis.GetLength() < 1e-12)
        {
            xAxis = new XYZ(Math.Cos(mtext.Rotation), Math.Sin(mtext.Rotation), 0d);
        }

        xAxis /= xAxis.GetLength();
        XYZ yAxis = new(-xAxis.Y, xAxis.X, 0d);
        Placement? placed = Place(placement, mtext.InsertPoint, xAxis, yAxis);
        if (placed is not Placement p)
        {
            return;
        }

        (double rotation, SurfaceTextAnchor anchor) = Orient(p, GetAnchor(mtext.AttachmentPoint));
        SurfaceText run = new(
            text,
            context.ToSurfacePoint(p.Origin),
            context.ToSurfaceLength(mtext.Height * p.Scale),
            rotation,
            anchor,
            GetBaseline(mtext.AttachmentPoint),
            mtext.RectangleWidth > 0 ? context.ToSurfaceLength(mtext.RectangleWidth * p.Scale) : -1d,
            mtext.LineSpacing,
            FixedLength: -1d);

        context.Surface.DrawText(style, run);
    }

    /// <summary>
    /// Draws a single-line text entity.
    /// </summary>
    /// <param name="context">The context that maps drawing units onto the surface.</param>
    /// <param name="style">The resolved style for the entity.</param>
    /// <param name="textEntity">The entity to draw.</param>
    public void Draw(ImageRenderContext context, ImageStyle style, TextEntity textEntity) => this.Draw(context, style, textEntity, null);

    /// <summary>
    /// Draws a single-line text entity placed by a block reference.
    /// </summary>
    /// <param name="context">The context that maps drawing units onto the surface.</param>
    /// <param name="style">The resolved style for the entity.</param>
    /// <param name="textEntity">The entity to draw.</param>
    /// <param name="placement">The transform of the insert that placed the entity, or null at top level.</param>
    /// <remarks>
    /// A TEXT entity's points and rotation live in its own OCS; the insert transform is applied after the OCS, by the
    /// renderer, because ACadSharp 3.7.1's <c>Insert.Explode()</c> transforms the insertion point but not the
    /// alignment point, and mixes world points with a mirrored normal.
    /// </remarks>
    public void Draw(ImageRenderContext context, ImageStyle style, TextEntity textEntity, Transform? placement)
    {
        string text = NormalizeText(textEntity.Value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // TEXT stores its points and rotation in its own OCS (MTEXT does not: its insertion point and X axis are WCS).
        OcsTransform? toWorld = OcsTransform.IsWorldPlane(textEntity.Normal) ? null : OcsTransform.For(textEntity.Normal);
        XYZ origin = ToWorld(toWorld, GetTextOrigin(textEntity));
        XYZ xAxis = Direction(toWorld, textEntity.Rotation);
        XYZ yAxis = Direction(toWorld, textEntity.Rotation + (Math.PI / 2d));
        Placement? placed = Place(placement, origin, xAxis, yAxis);
        if (placed is not Placement p)
        {
            return;
        }

        (double rotation, SurfaceTextAnchor anchor) = Orient(p, GetAnchor(textEntity.HorizontalAlignment));
        SurfaceText run = new(
            text,
            context.ToSurfacePoint(p.Origin),
            context.ToSurfaceLength(textEntity.Height * p.Scale),
            rotation,
            anchor,
            GetBaseline(textEntity.VerticalAlignment),
            WrappingWidth: -1d,
            LineSpacingFactor: 1d,
            GetFixedLength(context, textEntity, toWorld, placement));

        context.Surface.DrawText(style, run);
    }

    /// <summary>A text run placed in world XY: where it starts, the direction it reads along and its up direction.</summary>
    /// <param name="Origin">World origin of the run.</param>
    /// <param name="Direction">Unit world direction the baseline reads along.</param>
    /// <param name="Mirrored">True when the up direction lies to the right of the reading direction, i.e. the plane is seen from behind.</param>
    /// <param name="Scale">Factor the text height is multiplied by (the length of the transformed up vector).</param>
    internal readonly record struct Placement(XY Origin, XY Direction, bool Mirrored, double Scale);

    /// <summary>
    /// Builds a placement by mapping the origin and the tips of its unit X and Y axes through the optional insert
    /// transform and projecting onto world XY.
    /// </summary>
    /// <param name="placement">The transform of the insert that placed the entity, or null at top level.</param>
    /// <param name="origin">World origin of the run.</param>
    /// <param name="xAxis">Unit world direction the baseline reads along, before the insert transform.</param>
    /// <param name="yAxis">Unit world up direction, before the insert transform.</param>
    /// <returns>The placement, or null when the plane is seen edge-on (either axis projects to nothing).</returns>
    internal static Placement? Place(Transform? placement, XYZ origin, XYZ xAxis, XYZ yAxis)
    {
        XYZ o = Apply(placement, origin);
        XYZ dx = Apply(placement, origin + xAxis) - o;
        XYZ dy = Apply(placement, origin + yAxis) - o;
        XY direction = new(dx.X, dx.Y);
        XY up = new(dy.X, dy.Y);
        double length = direction.GetLength();
        double scale = up.GetLength();
        if (length < 1e-12 || scale < 1e-12)
        {
            return null;
        }

        bool mirrored = (direction.X * up.Y) - (direction.Y * up.X) < 0d;
        return new Placement(new XY(o.X, o.Y), direction / length, mirrored, scale);
    }

    /// <summary>
    /// Rotation and anchor for a placement.
    /// </summary>
    /// <param name="placement">The placement to orient.</param>
    /// <param name="anchor">Anchor derived from the horizontal alignment or attachment point.</param>
    /// <returns>The rotation to draw with (radians, drawing convention) and the anchor to use.</returns>
    /// <remarks>
    /// A plane seen from behind (what MIRROR writes) would show mirrored glyphs; the renderer keeps them readable and
    /// lets the run occupy the mirrored extent instead, which is the same baseline read from the other end: half a
    /// turn added to the direction, and <see cref="SurfaceTextAnchor.Start"/> and <see cref="SurfaceTextAnchor.End"/>
    /// swapped. AutoCAD draws the glyphs themselves mirrored; this is a deliberate readability choice.
    /// </remarks>
    internal static (double Rotation, SurfaceTextAnchor Anchor) Orient(Placement placement, SurfaceTextAnchor anchor)
    {
        double angle = Math.Atan2(placement.Direction.Y, placement.Direction.X);
        if (!placement.Mirrored)
        {
            return (angle, anchor);
        }

        SurfaceTextAnchor flipped = anchor switch
        {
            SurfaceTextAnchor.Start => SurfaceTextAnchor.End,
            SurfaceTextAnchor.End => SurfaceTextAnchor.Start,
            _ => anchor,
        };
        double turned = angle + Math.PI;
        return (Math.Atan2(Math.Sin(turned), Math.Cos(turned)), flipped);
    }

    private static XYZ Apply(Transform? placement, XYZ point) => placement == null ? point : placement.ApplyTransform(point);

    private static XYZ ToWorld(OcsTransform? toWorld, XYZ point) => toWorld == null ? point : toWorld.ToWorld(point.X, point.Y, point.Z);

    private static XYZ Direction(OcsTransform? toWorld, double angle)
    {
        XYZ ocs = new(Math.Cos(angle), Math.Sin(angle), 0d);
        return toWorld == null ? ocs : toWorld.ToWorld(ocs.X, ocs.Y, 0d);
    }

    /// <summary>
    /// Measures the Fit/Aligned advance between a TEXT entity's insertion and alignment points.
    /// </summary>
    /// <param name="context">The context that maps drawing units onto the surface.</param>
    /// <param name="textEntity">The entity to measure.</param>
    /// <param name="toWorld">The OCS frame, or null for the world plane.</param>
    /// <param name="placement">The transform of the insert that placed the entity, or null at top level.</param>
    /// <returns>The fixed advance in surface units, or -1 when the alignment does not apply.</returns>
    private static double GetFixedLength(ImageRenderContext context, TextEntity textEntity, OcsTransform? toWorld, Transform? placement)
    {
        if (textEntity.HorizontalAlignment is not (TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Fit))
        {
            return -1d;
        }

        XYZ insert = Apply(placement, ToWorld(toWorld, textEntity.InsertPoint));
        XYZ alignment = Apply(placement, ToWorld(toWorld, textEntity.AlignmentPoint));
        double dx = alignment.X - insert.X;
        double dy = alignment.Y - insert.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        return length > 0 ? context.ToSurfaceLength(length) : -1d;
    }

    private static XYZ GetTextOrigin(TextEntity textEntity)
    {
        return textEntity.HorizontalAlignment == TextHorizontalAlignment.Left && textEntity.VerticalAlignment == TextVerticalAlignmentType.Baseline
            ? textEntity.InsertPoint
            : textEntity.AlignmentPoint;
    }

    private static SurfaceTextAnchor GetAnchor(AttachmentPointType attachment)
    {
        return attachment switch
        {
            AttachmentPointType.TopCenter or AttachmentPointType.MiddleCenter or AttachmentPointType.BottomCenter => SurfaceTextAnchor.Middle,
            AttachmentPointType.TopRight or AttachmentPointType.MiddleRight or AttachmentPointType.BottomRight => SurfaceTextAnchor.End,
            _ => SurfaceTextAnchor.Start,
        };
    }

    private static SurfaceTextBaseline GetBaseline(AttachmentPointType attachment)
    {
        return attachment switch
        {
            AttachmentPointType.TopLeft or AttachmentPointType.TopCenter or AttachmentPointType.TopRight => SurfaceTextBaseline.Hanging,
            AttachmentPointType.MiddleLeft or AttachmentPointType.MiddleCenter or AttachmentPointType.MiddleRight => SurfaceTextBaseline.Central,
            _ => SurfaceTextBaseline.Alphabetic,
        };
    }

    private static SurfaceTextAnchor GetAnchor(TextHorizontalAlignment alignment)
    {
        return alignment switch
        {
            TextHorizontalAlignment.Center or TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Middle or TextHorizontalAlignment.Fit => SurfaceTextAnchor.Middle,
            TextHorizontalAlignment.Right => SurfaceTextAnchor.End,
            _ => SurfaceTextAnchor.Start,
        };
    }

    private static SurfaceTextBaseline GetBaseline(TextVerticalAlignmentType alignment)
    {
        return alignment switch
        {
            TextVerticalAlignmentType.Middle => SurfaceTextBaseline.Central,
            TextVerticalAlignmentType.Top => SurfaceTextBaseline.Hanging,
            _ => SurfaceTextBaseline.Alphabetic,
        };
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("%%C", "Ø", StringComparison.OrdinalIgnoreCase)
            .Replace("%%D", "°", StringComparison.OrdinalIgnoreCase)
            .Replace("%%P", "±", StringComparison.OrdinalIgnoreCase)
            .Replace("\\P", "\n", StringComparison.OrdinalIgnoreCase);
    }
}
