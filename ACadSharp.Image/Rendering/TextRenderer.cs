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
    public void Draw(ImageRenderContext context, ImageStyle style, MText mtext)
    {
        string text = NormalizeText(mtext.PlainText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        SurfaceText run = new(
            text,
            context.ToSurfacePoint(mtext.InsertPoint),
            context.ToSurfaceLength(mtext.Height),
            mtext.Rotation,
            GetAnchor(mtext.AttachmentPoint),
            GetBaseline(mtext.AttachmentPoint),
            mtext.RectangleWidth > 0 ? context.ToSurfaceLength(mtext.RectangleWidth) : -1d,
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
    public void Draw(ImageRenderContext context, ImageStyle style, TextEntity textEntity)
    {
        string text = NormalizeText(textEntity.Value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // TEXT stores its points and rotation in its own OCS (MTEXT does not: its insertion point and X axis are WCS).
        OcsTransform? toWorld = OcsTransform.IsWorldPlane(textEntity.Normal) ? null : OcsTransform.For(textEntity.Normal);
        XYZ origin = GetTextOrigin(textEntity);
        SurfacePoint surfaceOrigin = toWorld == null
            ? context.ToSurfacePoint(origin)
            : context.ToSurfacePoint(toWorld.ToWorldXY(origin.X, origin.Y, origin.Z));
        (double rotation, SurfaceTextAnchor anchor) = ResolvePlacement(textEntity.Rotation, GetAnchor(textEntity.HorizontalAlignment), toWorld);

        SurfaceText run = new(
            text,
            surfaceOrigin,
            context.ToSurfaceLength(textEntity.Height),
            rotation,
            anchor,
            GetBaseline(textEntity.VerticalAlignment),
            WrappingWidth: -1d,
            LineSpacingFactor: 1d,
            GetFixedLength(context, textEntity, toWorld));

        context.Surface.DrawText(style, run);
    }

    /// <summary>
    /// Maps a TEXT entity's in-plane rotation and anchor onto the page.
    /// </summary>
    /// <param name="rotation">Rotation in the entity's OCS, radians.</param>
    /// <param name="anchor">Anchor derived from the horizontal alignment.</param>
    /// <param name="toWorld">The OCS frame, or null for the world plane.</param>
    /// <returns>The rotation to draw with (radians, drawing convention) and the anchor to use.</returns>
    /// <remarks>
    /// The OCS X direction rotated by <paramref name="rotation"/> is projected onto world XY. A plane seen from the
    /// front keeps that direction. A plane seen from behind (normal Z below zero, what MIRROR writes) would show
    /// mirrored glyphs; the renderer keeps them readable and lets the run occupy the mirrored extent instead, which is
    /// the same baseline read from the other end: half a turn added to the projected direction, and
    /// <see cref="SurfaceTextAnchor.Start"/> and <see cref="SurfaceTextAnchor.End"/> swapped. A plane seen edge-on
    /// (normal in the XY plane) projects the direction to a zero vector, in which case <c>Atan2</c> returns 0 and the
    /// run is drawn horizontal; no behaviour change.
    /// </remarks>
    internal static (double Rotation, SurfaceTextAnchor Anchor) ResolvePlacement(double rotation, SurfaceTextAnchor anchor, OcsTransform? toWorld)
    {
        if (toWorld == null)
        {
            return (rotation, anchor);
        }

        XYZ direction = toWorld.ToWorld(Math.Cos(rotation), Math.Sin(rotation), 0d);
        double projected = Math.Atan2(direction.Y, direction.X);
        if (toWorld.Normal.Z >= 0d)
        {
            return (projected, anchor);
        }

        SurfaceTextAnchor flipped = anchor switch
        {
            SurfaceTextAnchor.Start => SurfaceTextAnchor.End,
            SurfaceTextAnchor.End => SurfaceTextAnchor.Start,
            _ => anchor,
        };
        double turned = projected + Math.PI;
        return (Math.Atan2(Math.Sin(turned), Math.Cos(turned)), flipped);
    }

    /// <summary>
    /// Measures the Fit/Aligned advance between a TEXT entity's insertion and alignment points.
    /// </summary>
    /// <param name="context">The context that maps drawing units onto the surface.</param>
    /// <param name="textEntity">The entity to measure.</param>
    /// <param name="toWorld">The OCS frame, or null for the world plane; when present, the two points are mapped
    /// into world space before the distance is measured (the -Z frame is an isometry, so this matches the on-page
    /// extent even for a mirrored insert).</param>
    /// <returns>The fixed advance in surface units, or -1 when the alignment does not apply.</returns>
    private static double GetFixedLength(ImageRenderContext context, TextEntity textEntity, OcsTransform? toWorld)
    {
        if (textEntity.HorizontalAlignment is not (TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Fit))
        {
            return -1d;
        }

        XYZ insert = textEntity.InsertPoint;
        XYZ alignment = textEntity.AlignmentPoint;
        if (toWorld != null)
        {
            insert = toWorld.ToWorld(insert.X, insert.Y, insert.Z);
            alignment = toWorld.ToWorld(alignment.X, alignment.Y, alignment.Z);
        }

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
