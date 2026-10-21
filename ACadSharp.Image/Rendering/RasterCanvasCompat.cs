using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
#if !NET6_0
using SixLabors.ImageSharp.Drawing.Text;
#endif
using ImageColor = SixLabors.ImageSharp.Color;
using ImagePoint = SixLabors.ImageSharp.Point;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Bridges the ImageSharp.Drawing 2.x API used on net6.0 and the 3.x API used on net8.0+.
/// </summary>
/// <remarks>
/// Six Labors 4.x (ImageSharp) and 3.x (Drawing, Fonts) require .NET 8 or later, so net6.0 stays on
/// ImageSharp 3.2.0/Drawing 2.2.0. The drawing surface calls through this class to keep the rendering
/// code single-sourced.
/// </remarks>
internal static class RasterCanvasCompat
{
    public static Image<Rgba32> CreateImage(int width, int height, ImageColor background)
    {
        return new Image<Rgba32>(width, height, background.ToPixel<Rgba32>());
    }

    public static void DrawLine(Image<Rgba32> canvas, Pen pen, PointF[] points)
    {
#if NET6_0
        canvas.Mutate(x => x.DrawLine(pen, points));
#else
        canvas.Mutate(x => x.Paint(c => c.DrawLine(pen, points)));
#endif
    }

    public static void DrawPath(Image<Rgba32> canvas, Pen pen, IPath path)
    {
#if NET6_0
        canvas.Mutate(x => x.Draw(pen, path));
#else
        canvas.Mutate(x => x.Paint(c => c.Draw(pen, path)));
#endif
    }

    public static void FillPolygon(Image<Rgba32> canvas, ImageColor color, PointF[] points)
    {
#if NET6_0
        canvas.Mutate(x => x.FillPolygon(color, points));
#else
        canvas.Mutate(x => x.Paint(c => c.Fill(new SolidBrush(color), new Polygon(new LinearLineSegment(points)))));
#endif
    }

    public static DrawingOptions CreateDrawingOptions(Matrix3x2? transform = null, bool evenOdd = false)
    {
        DrawingOptions options = new();
#if NET6_0
        if (evenOdd)
        {
            options.ShapeOptions.IntersectionRule = IntersectionRule.EvenOdd;
        }

        if (transform.HasValue)
        {
            options.Transform = transform.Value;
        }
#else
        if (evenOdd)
        {
            options.IntersectionRule = IntersectionRule.EvenOdd;
        }

        if (transform.HasValue)
        {
            options.Transform = ToMatrix4x4(transform.Value);
        }
#endif
        return options;
    }

    public static void FillPath(Image<Rgba32> canvas, DrawingOptions options, ImageColor color, IPath path)
    {
#if NET6_0
        canvas.Mutate(x => x.Fill(options, color, path));
#else
        canvas.Mutate(x => x.Paint(options, c => c.Fill(new SolidBrush(color), path)));
#endif
    }

    public static void FillGlyphs(Image<Rgba32> canvas, DrawingOptions options, ImageColor color, IPathCollection glyphs)
    {
#if NET6_0
        canvas.Mutate(x => x.Fill(options, color, glyphs));
#else
        canvas.Mutate(x => x.Paint(options, c => c.Fill(new SolidBrush(color), glyphs)));
#endif
    }

    public static void FillEllipse(Image<Rgba32> canvas, ImageColor color, PointF center, float radius)
    {
#if NET6_0
        canvas.Mutate(x => x.Fill(color, new EllipsePolygon(center.X, center.Y, radius)));
#else
        canvas.Mutate(x => x.Paint(c => c.FillEllipse(new SolidBrush(color), center, new SizeF(radius * 2f, radius * 2f))));
#endif
    }

    public static IPathCollection GenerateTextPaths(string text, TextOptions options)
    {
#if NET6_0
        return TextBuilder.GenerateGlyphs(text, options);
#else
        return TextBuilder.GeneratePaths(text, options);
#endif
    }

    public static void DrawImage(Image<Rgba32> canvas, Image<Rgba32> image, ImagePoint location)
    {
#if NET6_0
        canvas.Mutate(x => x.DrawImage(image, location, 1f));
#else
        canvas.Mutate(x => x.Paint(c => c.DrawImage(
            image,
            new Rectangle(0, 0, image.Width, image.Height),
            new RectangleF(location.X, location.Y, image.Width, image.Height),
            KnownResamplers.NearestNeighbor)));
#endif
    }

#if !NET6_0
    private static Matrix4x4 ToMatrix4x4(Matrix3x2 matrix)
    {
        return new Matrix4x4(
            matrix.M11, matrix.M12, 0f, 0f,
            matrix.M21, matrix.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            matrix.M31, matrix.M32, 0f, 1f);
    }
#endif
}
