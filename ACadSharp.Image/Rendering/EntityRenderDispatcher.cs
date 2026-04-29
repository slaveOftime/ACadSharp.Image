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
    private readonly ImageStyleResolver _styleResolver;

    public EntityRenderDispatcher(ImageConfiguration configuration)
    {
        this._configuration = configuration;
        this._styleResolver = new ImageStyleResolver(configuration);
    }

    /// <summary>
    /// Draws a single CAD entity onto the rendering canvas.
    /// </summary>
    /// <param name="context">The rendering context containing the canvas and coordinate transforms.</param>
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
        if (this.drawBezierSpline(context, style, spline))
        {
            return;
        }

        XY[] sampledVertices = this.sampleSpline(spline);
        if (sampledVertices.Length > 1)
        {
            this.drawPolyline(context, style, sampledVertices, spline.IsClosed || spline.IsPeriodic);
            return;
        }

        if (spline.TryPolygonalVertexes(this._configuration.ArcPrecision, out List<XYZ>? points) && points.Count > 1)
        {
            this.drawPolyline(context, style, points.Select(v => v.Convert<XY>()), spline.IsClosed || spline.IsPeriodic);
            return;
        }

        this._configuration.Notify($"[{spline.SubclassMarker}] Could not approximate spline geometry.", NotificationType.Warning);
    }

    private bool drawBezierSpline(ImageRenderContext context, ImageStyle style, Spline spline)
    {
        if (!tryGetBezierSegments(spline, out int segmentCount))
        {
            return false;
        }

        PathBuilder builder = new();
        IReadOnlyList<XYZ> controlPoints = spline.ControlPoints;
        for (int segment = 0; segment < segmentCount; segment++)
        {
            int index = segment * 3;
            builder.AddCubicBezier(
                context.ToPixelPoint(controlPoints[index]),
                context.ToPixelPoint(controlPoints[index + 1]),
                context.ToPixelPoint(controlPoints[index + 2]),
                context.ToPixelPoint(controlPoints[index + 3]));
        }

        IPath path = builder.Build();
        context.Canvas.Mutate(x => x.Draw(style.StrokeColor, style.StrokeWidth, path));
        return true;
    }

    private static bool tryGetBezierSegments(Spline spline, out int segmentCount)
    {
        segmentCount = 0;
        if (spline.Degree != 3 ||
            spline.Weights.Count != 0 ||
            spline.Knots.Count != spline.ControlPoints.Count + 4 ||
            spline.ControlPoints.Count < 4 ||
            (spline.ControlPoints.Count - 1) % 3 != 0)
        {
            return false;
        }

        segmentCount = (spline.ControlPoints.Count - 1) / 3;
        return hasKnotMultiplicity(spline.Knots, 0, 4) &&
            hasKnotMultiplicity(spline.Knots, spline.Knots.Count - 4, 4) &&
            hasBezierInternalKnots(spline.Knots, segmentCount);
    }

    private static bool hasBezierInternalKnots(IReadOnlyList<double> knots, int segmentCount)
    {
        int index = 4;
        for (int segment = 1; segment < segmentCount; segment++)
        {
            if (!hasKnotMultiplicity(knots, index, 3))
            {
                return false;
            }

            index += 3;
        }

        return index == knots.Count - 4;
    }

    private static bool hasKnotMultiplicity(IReadOnlyList<double> knots, int startIndex, int count)
    {
        if (startIndex < 0 || startIndex + count > knots.Count)
        {
            return false;
        }

        double value = knots[startIndex];
        for (int i = 1; i < count; i++)
        {
            if (Math.Abs(knots[startIndex + i] - value) > double.Epsilon)
            {
                return false;
            }
        }

        return true;
    }

    private XY[] sampleSpline(Spline spline)
    {
        int degree = (int)spline.Degree;
        IReadOnlyList<double> knots = spline.Knots;
        IReadOnlyList<XYZ> controlPoints = spline.ControlPoints;
        IReadOnlyList<double> weights = spline.Weights;

        if (degree < 1 ||
            controlPoints.Count <= degree ||
            knots.Count != controlPoints.Count + degree + 1 ||
            (weights.Count != 0 && weights.Count != controlPoints.Count))
        {
            return [];
        }

        double start = knots[degree];
        double end = knots[controlPoints.Count];
        if (end <= start)
        {
            return [];
        }

        int knotSpans = 0;
        double previous = start;
        for (int i = degree + 1; i <= controlPoints.Count; i++)
        {
            double current = knots[i];
            if (current > previous)
            {
                knotSpans++;
                previous = current;
            }
        }

        int precision = Math.Max(this._configuration.ArcPrecision, knotSpans * 16);
        List<XY> vertices = new(precision + 1);
        for (int i = 0; i <= precision; i++)
        {
            double t = start + ((end - start) * i / precision);
            vertices.Add(evaluateSplinePoint(degree, knots, controlPoints, weights, t));
        }

        return vertices.ToArray();
    }

    private static XY evaluateSplinePoint(
        int degree,
        IReadOnlyList<double> knots,
        IReadOnlyList<XYZ> controlPoints,
        IReadOnlyList<double> weights,
        double t)
    {
        int span = findKnotSpan(degree, knots, controlPoints.Count, t);
        SplinePoint[] points = new SplinePoint[degree + 1];

        for (int i = 0; i <= degree; i++)
        {
            int pointIndex = span - degree + i;
            XYZ point = controlPoints[pointIndex];
            double weight = weights.Count == 0 ? 1d : weights[pointIndex];
            points[i] = new SplinePoint(point.X * weight, point.Y * weight, point.Z * weight, weight);
        }

        for (int level = 1; level <= degree; level++)
        {
            for (int i = degree; i >= level; i--)
            {
                int knotIndex = span - degree + i;
                double denominator = knots[knotIndex + degree + 1 - level] - knots[knotIndex];
                double alpha = denominator == 0d ? 0d : (t - knots[knotIndex]) / denominator;
                points[i] = SplinePoint.Lerp(points[i - 1], points[i], alpha);
            }
        }

        SplinePoint result = points[degree];
        return result.Weight == 0d
            ? new XY(result.X, result.Y)
            : new XY(result.X / result.Weight, result.Y / result.Weight);
    }

    private static int findKnotSpan(int degree, IReadOnlyList<double> knots, int controlPointCount, double t)
    {
        int maxSpan = controlPointCount - 1;
        if (t >= knots[controlPointCount])
        {
            return maxSpan;
        }

        int low = degree;
        int high = controlPointCount;
        int span = (low + high) / 2;
        while (t < knots[span] || t >= knots[span + 1])
        {
            if (t < knots[span])
            {
                high = span;
            }
            else
            {
                low = span;
            }

            span = (low + high) / 2;
        }

        return span;
    }

    private readonly record struct SplinePoint(double X, double Y, double Z, double Weight)
    {
        public static SplinePoint Lerp(SplinePoint start, SplinePoint end, double amount)
        {
            double inverse = 1d - amount;
            return new SplinePoint(
                (start.X * inverse) + (end.X * amount),
                (start.Y * inverse) + (end.Y * amount),
                (start.Z * inverse) + (end.Z * amount),
                (start.Weight * inverse) + (end.Weight * amount));
        }
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

    /// <summary>
    /// Determines whether a polyline should be closed based on a heuristic.
    /// </summary>
    /// <remarks>
    /// The heuristic compares the distance between the last and first points (closing length)
    /// to the average segment length. If the closing length is within 3x the average segment
    /// length, the polyline is considered closeable. This handles cases where polylines are
    /// nearly closed but have small gaps due to precision or modeling errors.
    /// </remarks>
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

        // 3x multiplier provides tolerance for small gaps in nearly-closed polylines
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
