using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using System.Numerics;

namespace ACadSharp.Image.Rendering;

internal sealed class EntityRenderDispatcher
{
    private readonly ImageConfiguration _configuration;
    private readonly ImageStyleResolver _styleResolver;

    public EntityRenderDispatcher(ImageConfiguration configuration)
    {
        this._configuration = configuration;
        this._styleResolver = new ImageStyleResolver(configuration);
    }

    public void Draw(ImageRenderContext context, Entity entity)
    {
        ImageStyle style = this._styleResolver.Resolve(entity);

        switch (entity)
        {
            case Arc arc:
                this.drawPolyline(context, style, arc.PolygonalVertexes(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), false);
                break;
            case Circle circle:
                this.drawPolyline(context, style, circle.PolygonalVertexes(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), true);
                break;
            case Ellipse ellipse:
                this.drawPolyline(context, style, ellipse.PolygonalVertexes(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), true);
                break;
            case Line line:
                context.Canvas.Mutate(x => x.DrawLine(style.StrokeColor, style.StrokeWidth, context.ToPixelPoint(line.StartPoint), context.ToPixelPoint(line.EndPoint)));
                break;
            case Dimension dimension:
                this.drawDimension(context, dimension);
                break;
            case Solid solid:
                this.drawSolid(context, style, solid);
                break;
            case ACadSharp.Entities.Point point:
                this.drawPoint(context, style, point);
                break;
            case IPolyline polyline:
                this.drawPolyline(context, style, polyline.GetPoints<XYZ>(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), polyline.IsClosed);
                break;
            case Spline spline:
                this.drawSpline(context, style, spline);
                break;
            case MText mtext:
                this.drawMText(context, style, mtext);
                break;
            case TextEntity textEntity:
                this.drawTextEntity(context, style, textEntity);
                break;
            case IText text:
                this._configuration.Notify($"[{entity.SubclassMarker}] Text rendering is not implemented yet.", NotificationType.NotImplemented);
                break;
            default:
                this._configuration.Notify($"[{entity.SubclassMarker}] Drawing not implemented.", NotificationType.NotImplemented);
                break;
        }
    }

    private void drawPoint(ImageRenderContext context, ImageStyle style, ACadSharp.Entities.Point point)
    {
        PointF center = context.ToPixelPoint(point.Location);
        float radius = Math.Max(1f, this._configuration.DotSizePixels / 2f);

        context.Canvas.Mutate(x => x.Fill(style.StrokeColor, new EllipsePolygon(center.X, center.Y, radius)));
    }

    private void drawDimension(ImageRenderContext context, Dimension dimension)
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

            this.Draw(context, entity);
        }
    }

    private void drawSolid(ImageRenderContext context, ImageStyle style, Solid solid)
    {
        PointF[] points =
        [
            context.ToPixelPoint(solid.FirstCorner),
            context.ToPixelPoint(solid.SecondCorner),
            context.ToPixelPoint(solid.ThirdCorner),
            context.ToPixelPoint(solid.FourthCorner),
        ];

        context.Canvas.Mutate(x => x.FillPolygon(style.StrokeColor, points));
    }

    private void drawMText(ImageRenderContext context, ImageStyle style, MText mtext)
    {
        string text = this.normalizeText(mtext.PlainText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        PointF origin = context.ToPixelPoint(mtext.InsertPoint);
        Font font = this.createFont(context, mtext.Height);
        TextOptions options = new(font)
        {
            Dpi = context.Configuration.Dpi,
            Origin = origin,
            HorizontalAlignment = this.getHorizontalAlignment(mtext.AttachmentPoint),
            VerticalAlignment = this.getVerticalAlignment(mtext.AttachmentPoint),
            WrappingLength = mtext.RectangleWidth > 0 ? context.ToPixelLength(mtext.RectangleWidth) : -1,
            LineSpacing = (float)mtext.LineSpacing,
        };

        IPathCollection glyphs = TextBuilder.GenerateGlyphs(text, options);
        DrawingOptions drawingOptions = this.createDrawingOptions(origin, mtext.Rotation);

        context.Canvas.Mutate(x => x.Fill(drawingOptions, style.StrokeColor, glyphs));
    }

    private void drawTextEntity(ImageRenderContext context, ImageStyle style, TextEntity textEntity)
    {
        string text = this.normalizeText(textEntity.Value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        PointF origin = context.ToPixelPoint(this.getTextOrigin(textEntity));
        Font font = this.createFont(context, textEntity.Height);
        TextOptions options = new(font)
        {
            Dpi = context.Configuration.Dpi,
            Origin = origin,
            HorizontalAlignment = this.getHorizontalAlignment(textEntity.HorizontalAlignment),
            VerticalAlignment = this.getVerticalAlignment(textEntity.VerticalAlignment),
        };

        IPathCollection glyphs = TextBuilder.GenerateGlyphs(text, options);
        DrawingOptions drawingOptions = this.createDrawingOptions(origin, textEntity.Rotation);

        context.Canvas.Mutate(x => x.Fill(drawingOptions, style.StrokeColor, glyphs));
    }

    private void drawSpline(ImageRenderContext context, ImageStyle style, Spline spline)
    {
        if (spline.IsClosed || spline.IsPeriodic)
        {
            XY[] sampledVertices = this.sampleClosedSpline(spline);
            if (sampledVertices.Length > 1)
            {
                this.drawPolyline(context, style, sampledVertices, true);
                return;
            }
        }

        if (spline.TryPolygonalVertexes(this._configuration.ArcPrecision, out List<XYZ>? points) && points.Count > 1)
        {
            this.drawPolyline(context, style, points.Select(v => v.Convert<XY>()), spline.IsClosed || spline.IsPeriodic);
            return;
        }

        this._configuration.Notify($"[{spline.SubclassMarker}] Could not approximate spline geometry.", NotificationType.Warning);
    }

    private XY[] sampleClosedSpline(Spline spline)
    {
        int precision = Math.Max(8, (int)this._configuration.ArcPrecision);
        List<XY> vertices = new(precision);

        for (int i = 0; i < precision; i++)
        {
            double t = (i + 0.5d) / precision;
            if (spline.TryPointOnSpline(t, out XYZ point))
            {
                vertices.Add(point.Convert<XY>());
            }
        }

        return vertices.ToArray();
    }

    private void drawPolyline(ImageRenderContext context, ImageStyle style, IEnumerable<XY> vertices, bool close)
    {
        PointF[] points = vertices.Select(context.ToPixelPoint).ToArray();
        if (points.Length < 2)
        {
            return;
        }

        if (close && this.shouldClose(points))
        {
            points = points.Concat(new[] { points[0] }).ToArray();
        }

        context.Canvas.Mutate(x => x.DrawLine(style.StrokeColor, style.StrokeWidth, points));
    }

    private bool shouldClose(IReadOnlyList<PointF> points)
    {
        if (points.Count < 3)
        {
            return false;
        }

        float totalLength = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            totalLength += distance(points[i - 1], points[i]);
        }

        float averageSegmentLength = totalLength / (points.Count - 1);
        float closingLength = distance(points[^1], points[0]);

        return closingLength <= averageSegmentLength * 3f;
    }

    private static float distance(PointF a, PointF b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private Font createFont(ImageRenderContext context, double height)
    {
        float size = Math.Max(1f, context.ToPixelLength(height));
        if (SystemFonts.TryGet(this._configuration.FontFamilyName, out FontFamily family))
        {
            return family.CreateFont(size);
        }

        return SystemFonts.Families.First().CreateFont(size);
    }

    private DrawingOptions createDrawingOptions(PointF origin, double rotation)
    {
        DrawingOptions options = new();
        if (Math.Abs(rotation) > double.Epsilon)
        {
            options.Transform = Matrix3x2.CreateRotation((float)-rotation, new Vector2(origin.X, origin.Y));
        }

        return options;
    }

    private XYZ getTextOrigin(TextEntity textEntity)
    {
        return textEntity.HorizontalAlignment == TextHorizontalAlignment.Left && textEntity.VerticalAlignment == TextVerticalAlignmentType.Baseline
            ? textEntity.InsertPoint
            : textEntity.AlignmentPoint;
    }

    private HorizontalAlignment getHorizontalAlignment(AttachmentPointType attachment)
    {
        return attachment switch
        {
            AttachmentPointType.TopCenter or AttachmentPointType.MiddleCenter or AttachmentPointType.BottomCenter => HorizontalAlignment.Center,
            AttachmentPointType.TopRight or AttachmentPointType.MiddleRight or AttachmentPointType.BottomRight => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
    }

    private VerticalAlignment getVerticalAlignment(AttachmentPointType attachment)
    {
        return attachment switch
        {
            AttachmentPointType.TopLeft or AttachmentPointType.TopCenter or AttachmentPointType.TopRight => VerticalAlignment.Top,
            AttachmentPointType.MiddleLeft or AttachmentPointType.MiddleCenter or AttachmentPointType.MiddleRight => VerticalAlignment.Center,
            _ => VerticalAlignment.Bottom,
        };
    }

    private HorizontalAlignment getHorizontalAlignment(TextHorizontalAlignment alignment)
    {
        return alignment switch
        {
            TextHorizontalAlignment.Center or TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Middle or TextHorizontalAlignment.Fit => HorizontalAlignment.Center,
            TextHorizontalAlignment.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
    }

    private VerticalAlignment getVerticalAlignment(TextVerticalAlignmentType alignment)
    {
        return alignment switch
        {
            TextVerticalAlignmentType.Middle => VerticalAlignment.Center,
            TextVerticalAlignmentType.Top => VerticalAlignment.Top,
            _ => VerticalAlignment.Bottom,
        };
    }

    private string normalizeText(string? value)
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
