using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using ImageColor = SixLabors.ImageSharp.Color;
using ImagePoint = SixLabors.ImageSharp.Point;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// <see cref="IDrawingSurface"/> backed by an ImageSharp <see cref="Image{Rgba32}"/>.
/// </summary>
/// <remarks>
/// Primitives map onto the same ImageSharp.Drawing calls the pre-abstraction renderer used; callers keep the closing heuristic and curve tessellation, so routed output is pixel-identical.
/// Curves are not supported natively; callers tessellate them (<see cref="SupportsCurves"/> is false).
/// </remarks>
internal sealed class RasterDrawingSurface : IDrawingSurface
{
    private readonly ImageConfiguration _configuration;
    private readonly bool _ownsCanvas;
    private readonly Dictionary<ViewportSurface, (Image<Rgba32> Image, SurfaceRect Bounds)> _viewports = new();

    public RasterDrawingSurface(Image<Rgba32> canvas, ImageConfiguration configuration, bool ownsCanvas)
    {
        this.Canvas = canvas;
        this._configuration = configuration;
        this._ownsCanvas = ownsCanvas;
    }

    public Image<Rgba32> Canvas { get; }

    public bool SupportsCurves => false;

    public void BeginEntity(EntityRenderInfo info, LayerRenderInfo layer)
    {
    }

    public void EndEntity()
    {
    }

    public void DrawLine(ImageStyle style, SurfacePoint start, SurfacePoint end)
    {
        Pen pen = CreatePen(style);
        this.Canvas.Mutate(x => x.DrawLine(pen, ToPointF(start), ToPointF(end)));
    }

    public void DrawPolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, bool closed)
    {
        if (points.Count < 2)
        {
            return;
        }

        PointF[] pixels = new PointF[closed ? points.Count + 1 : points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            pixels[i] = ToPointF(points[i]);
        }

        if (closed)
        {
            pixels[^1] = pixels[0];
        }

        Pen pen = CreatePen(style);
        this.Canvas.Mutate(x => x.DrawLine(pen, pixels));
    }

    public void DrawArc(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation, double startAngle, double sweepAngle)
    {
        int segments = CurveTessellation.SegmentsForSweep(sweepAngle, this._configuration.ArcPrecision);
        this.DrawPolyline(style, CurveTessellation.ArcPoints(center, radiusX, radiusY, rotation, startAngle, sweepAngle, segments), closed: false);
    }

    public void DrawEllipse(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation)
    {
        this.DrawPolyline(style, CurveTessellation.ArcPoints(center, radiusX, radiusY, rotation, 0d, 2d * Math.PI, this._configuration.ArcPrecision), closed: true);
    }

    public void DrawCubicBezier(ImageStyle style, IReadOnlyList<SurfacePoint> controlPoints, bool closed)
    {
        if (controlPoints.Count < 4)
        {
            return;
        }

        PathBuilder builder = new();
        for (int index = 0; index + 3 < controlPoints.Count; index += 3)
        {
            builder.AddCubicBezier(
                ToPointF(controlPoints[index]),
                ToPointF(controlPoints[index + 1]),
                ToPointF(controlPoints[index + 2]),
                ToPointF(controlPoints[index + 3]));
        }

        IPath path = builder.Build();
        Pen pen = CreatePen(style);
        this.Canvas.Mutate(x => x.Draw(pen, path));
    }

    public void DrawBulgePolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, IReadOnlyList<double> bulges, bool closed)
    {
        if (points.Count < 2)
        {
            return;
        }

        List<SurfacePoint> flattened = new(points.Count * 4) { points[0] };
        int segmentCount = closed ? points.Count : points.Count - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            SurfacePoint start = points[i];
            SurfacePoint end = points[(i + 1) % points.Count];
            double bulge = i < bulges.Count ? bulges[i] : 0d;
            if (Math.Abs(bulge) < 1e-12 || start == end)
            {
                flattened.Add(end);
                continue;
            }

            CurveTessellation.BulgeArc(start, end, bulge, out SurfacePoint center, out double radius, out double startAngle, out double sweep);
            IReadOnlyList<SurfacePoint> arc = CurveTessellation.ArcPoints(center, radius, radius, 0d, startAngle, sweep, CurveTessellation.SegmentsForSweep(sweep, this._configuration.ArcPrecision));
            for (int j = 1; j < arc.Count; j++)
            {
                flattened.Add(arc[j]);
            }
        }

        this.DrawPolyline(style, flattened, closed: false);
    }

    public void FillPolygon(ImageStyle style, IReadOnlyList<SurfacePoint> points)
    {
        if (points.Count < 3)
        {
            return;
        }

        PointF[] pixels = points.Select(ToPointF).ToArray();
        ImageColor color = style.EffectiveColor;
        this.Canvas.Mutate(x => x.FillPolygon(color, pixels));
    }

    public void FillPath(ImageStyle style, IReadOnlyList<IReadOnlyList<SurfacePoint>> rings)
    {
        IPath[] polygons = rings
            .Where(ring => ring.Count >= 3)
            .Select(ring => (IPath)new Polygon(new LinearLineSegment(ring.Select(ToPointF).ToArray())))
            .ToArray();
        if (polygons.Length == 0)
        {
            return;
        }

        IPath shape = polygons.Length == 1 ? polygons[0] : new ComplexPolygon(polygons);
        ImageColor color = style.EffectiveColor;
        DrawingOptions options = new()
        {
            ShapeOptions = { IntersectionRule = IntersectionRule.EvenOdd },
        };
        this.Canvas.Mutate(x => x.Fill(options, color, shape));
    }

    public void FillCircle(ImageStyle style, SurfacePoint center, double radius)
    {
        PointF pixel = ToPointF(center);
        ImageColor color = style.EffectiveColor;
        this.Canvas.Mutate(x => x.Fill(color, new EllipsePolygon(pixel.X, pixel.Y, (float)radius)));
    }

    public void DrawText(ImageStyle style, SurfaceText text)
    {
        if (string.IsNullOrWhiteSpace(text.Text))
        {
            return;
        }

        PointF origin = ToPointF(text.Origin);
        Font font = this.CreateFont(text.Height);
        TextOptions options = new(font)
        {
            Dpi = this._configuration.Dpi,
            Origin = origin,
            HorizontalAlignment = text.Anchor switch
            {
                SurfaceTextAnchor.Middle => HorizontalAlignment.Center,
                SurfaceTextAnchor.End => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left,
            },
            VerticalAlignment = text.Baseline switch
            {
                SurfaceTextBaseline.Hanging => VerticalAlignment.Top,
                SurfaceTextBaseline.Central => VerticalAlignment.Center,
                _ => VerticalAlignment.Bottom,
            },
            WrappingLength = text.WrappingWidth > 0 ? (float)text.WrappingWidth : -1,
            LineSpacing = (float)text.LineSpacingFactor,
        };

        IPathCollection glyphs = TextBuilder.GenerateGlyphs(text.Text, options);
        DrawingOptions drawingOptions = new();
        if (Math.Abs(text.Rotation) > double.Epsilon)
        {
            drawingOptions.Transform = Matrix3x2.CreateRotation((float)-text.Rotation, new Vector2(origin.X, origin.Y));
        }

        ImageColor color = style.EffectiveColor;
        this.Canvas.Mutate(x => x.Fill(drawingOptions, color, glyphs));
    }

    public ViewportSurface BeginViewport(SurfaceRect bounds)
    {
        // The child image needs whole pixels, but the content is placed against the viewport's exact height:
        // rounding the flip origin up used to shift every point down by the fraction and push geometry on the
        // view's lower edge out of the image.
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        Image<Rgba32> image = new(width, height, ImageColor.Transparent);
        RasterDrawingSurface child = new(image, this._configuration, ownsCanvas: true);
        ViewportSurface viewport = new(child, 0d, bounds.Height);
        this._viewports[viewport] = (image, bounds);
        return viewport;
    }

    public void EndViewport(ViewportSurface viewport)
    {
        if (!this._viewports.Remove(viewport, out (Image<Rgba32> Image, SurfaceRect Bounds) entry))
        {
            throw new InvalidOperationException("EndViewport was called for a viewport this surface did not begin.");
        }

        ImagePoint destination = new((int)MathF.Round((float)entry.Bounds.X), (int)MathF.Round((float)entry.Bounds.Y));
        this.Canvas.Mutate(x => x.DrawImage(entry.Image, destination, 1f));
        viewport.Surface.Dispose();
    }

    public void Dispose()
    {
        foreach ((Image<Rgba32> image, _) in this._viewports.Values)
        {
            image.Dispose();
        }

        this._viewports.Clear();
        if (this._ownsCanvas)
        {
            this.Canvas.Dispose();
        }
    }

    private Font CreateFont(double height)
    {
        float size = Math.Max(1f, (float)height);
        if (SystemFonts.TryGet(this._configuration.FontFamilyName, out FontFamily family))
        {
            return family.CreateFont(size);
        }

        return SystemFonts.Families.First().CreateFont(size);
    }

    private static Pen CreatePen(ImageStyle style)
    {
        ImageColor color = style.EffectiveColor;
        if (style.DashPattern is not { Length: > 0 })
        {
            return new SolidPen(color, style.StrokeWidth);
        }

        // ImageSharp.Drawing pattern values are multiples of the stroke width.
        float width = Math.Max(0.01f, style.StrokeWidth);
        float[] pattern = new float[style.DashPattern.Length];
        for (int i = 0; i < pattern.Length; i++)
        {
            pattern[i] = Math.Max(0.001f, style.DashPattern[i] / width);
        }

        return new PatternPen(color, width, pattern);
    }

    private static PointF ToPointF(SurfacePoint point)
    {
        return new PointF((float)point.X, (float)point.Y);
    }
}
