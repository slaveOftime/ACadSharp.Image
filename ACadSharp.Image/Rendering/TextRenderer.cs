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

        SurfaceText run = new(
            text,
            context.ToSurfacePoint(GetTextOrigin(textEntity)),
            context.ToSurfaceLength(textEntity.Height),
            textEntity.Rotation,
            GetAnchor(textEntity.HorizontalAlignment),
            GetBaseline(textEntity.VerticalAlignment),
            WrappingWidth: -1d,
            LineSpacingFactor: 1d,
            GetFixedLength(context, textEntity));

        context.Surface.DrawText(style, run);
    }

    private static double GetFixedLength(ImageRenderContext context, TextEntity textEntity)
    {
        if (textEntity.HorizontalAlignment is not (TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Fit))
        {
            return -1d;
        }

        double dx = textEntity.AlignmentPoint.X - textEntity.InsertPoint.X;
        double dy = textEntity.AlignmentPoint.Y - textEntity.InsertPoint.Y;
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
