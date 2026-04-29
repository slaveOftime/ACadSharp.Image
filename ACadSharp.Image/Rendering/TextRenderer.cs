using System.Numerics;
using ACadSharp.Entities;
using CSMath;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace ACadSharp.Image.Rendering;

internal sealed class TextRenderer(ImageConfiguration configuration)
{
    private readonly ImageConfiguration _configuration = configuration;

    public void Draw(ImageRenderContext context, ImageStyle style, MText mtext)
    {
        string text = NormalizeText(mtext.PlainText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        PointF origin = context.ToPixelPoint(mtext.InsertPoint);
        Font font = this.CreateFont(context, mtext.Height);
        TextOptions options = new(font)
        {
            Dpi = context.Configuration.Dpi,
            Origin = origin,
            HorizontalAlignment = GetHorizontalAlignment(mtext.AttachmentPoint),
            VerticalAlignment = GetVerticalAlignment(mtext.AttachmentPoint),
            WrappingLength = mtext.RectangleWidth > 0 ? context.ToPixelLength(mtext.RectangleWidth) : -1,
            LineSpacing = (float)mtext.LineSpacing,
        };

        IPathCollection glyphs = TextBuilder.GenerateGlyphs(text, options);
        DrawingOptions drawingOptions = CreateDrawingOptions(origin, mtext.Rotation);

        context.Canvas.Mutate(x => x.Fill(drawingOptions, style.StrokeColor, glyphs));
    }

    public void Draw(ImageRenderContext context, ImageStyle style, TextEntity textEntity)
    {
        string text = NormalizeText(textEntity.Value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        PointF origin = context.ToPixelPoint(GetTextOrigin(textEntity));
        Font font = this.CreateFont(context, textEntity.Height);
        TextOptions options = new(font)
        {
            Dpi = context.Configuration.Dpi,
            Origin = origin,
            HorizontalAlignment = GetHorizontalAlignment(textEntity.HorizontalAlignment),
            VerticalAlignment = GetVerticalAlignment(textEntity.VerticalAlignment),
        };

        IPathCollection glyphs = TextBuilder.GenerateGlyphs(text, options);
        DrawingOptions drawingOptions = CreateDrawingOptions(origin, textEntity.Rotation);

        context.Canvas.Mutate(x => x.Fill(drawingOptions, style.StrokeColor, glyphs));
    }

    private Font CreateFont(ImageRenderContext context, double height)
    {
        float size = Math.Max(1f, context.ToPixelLength(height));
        if (SystemFonts.TryGet(this._configuration.FontFamilyName, out FontFamily family))
        {
            return family.CreateFont(size);
        }

        return SystemFonts.Families.First().CreateFont(size);
    }

    private static DrawingOptions CreateDrawingOptions(PointF origin, double rotation)
    {
        DrawingOptions options = new();
        if (Math.Abs(rotation) > double.Epsilon)
        {
            options.Transform = Matrix3x2.CreateRotation((float)-rotation, new Vector2(origin.X, origin.Y));
        }

        return options;
    }

    private static XYZ GetTextOrigin(TextEntity textEntity)
    {
        return textEntity.HorizontalAlignment == TextHorizontalAlignment.Left && textEntity.VerticalAlignment == TextVerticalAlignmentType.Baseline
            ? textEntity.InsertPoint
            : textEntity.AlignmentPoint;
    }

    private static HorizontalAlignment GetHorizontalAlignment(AttachmentPointType attachment)
    {
        return attachment switch
        {
            AttachmentPointType.TopCenter or AttachmentPointType.MiddleCenter or AttachmentPointType.BottomCenter => HorizontalAlignment.Center,
            AttachmentPointType.TopRight or AttachmentPointType.MiddleRight or AttachmentPointType.BottomRight => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
    }

    private static VerticalAlignment GetVerticalAlignment(AttachmentPointType attachment)
    {
        return attachment switch
        {
            AttachmentPointType.TopLeft or AttachmentPointType.TopCenter or AttachmentPointType.TopRight => VerticalAlignment.Top,
            AttachmentPointType.MiddleLeft or AttachmentPointType.MiddleCenter or AttachmentPointType.MiddleRight => VerticalAlignment.Center,
            _ => VerticalAlignment.Bottom,
        };
    }

    private static HorizontalAlignment GetHorizontalAlignment(TextHorizontalAlignment alignment)
    {
        return alignment switch
        {
            TextHorizontalAlignment.Center or TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Middle or TextHorizontalAlignment.Fit => HorizontalAlignment.Center,
            TextHorizontalAlignment.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
    }

    private static VerticalAlignment GetVerticalAlignment(TextVerticalAlignmentType alignment)
    {
        return alignment switch
        {
            TextVerticalAlignmentType.Middle => VerticalAlignment.Center,
            TextVerticalAlignmentType.Top => VerticalAlignment.Top,
            _ => VerticalAlignment.Bottom,
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
