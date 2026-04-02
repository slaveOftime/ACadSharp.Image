using ACadSharp.Objects;
using ACadSharp.Image.Extensions;
using CSMath;
using SixLabors.ImageSharp;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace ACadSharp.Image.Rendering;

internal sealed class ImageRenderContext
{
    public SixLabors.ImageSharp.Image<Rgba32> Canvas { get; }

    public ImageConfiguration Configuration { get; }

    public Layout Layout { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public double OriginX { get; }

    public double OriginY { get; }

    public float PixelsPerUnit { get; }

    public float OffsetX { get; }

    public float OffsetY { get; }

    public ImageRenderContext(
        SixLabors.ImageSharp.Image<Rgba32> canvas,
        ImageConfiguration configuration,
        Layout layout,
        int pixelWidth,
        int pixelHeight,
        double originX,
        double originY,
        float pixelsPerUnit,
        float offsetX = 0f,
        float offsetY = 0f)
    {
        this.Canvas = canvas;
        this.Configuration = configuration;
        this.Layout = layout;
        this.PixelWidth = pixelWidth;
        this.PixelHeight = pixelHeight;
        this.OriginX = originX;
        this.OriginY = originY;
        this.PixelsPerUnit = pixelsPerUnit;
        this.OffsetX = offsetX;
        this.OffsetY = offsetY;
    }

    public static ImageRenderContext CreatePageContext(
        SixLabors.ImageSharp.Image<Rgba32> canvas,
        ImagePage page,
        ImageConfiguration configuration)
    {
        double pageWidth = Math.Max(1d, page.Layout.PaperWidth);
        double pageHeight = Math.Max(1d, page.Layout.PaperHeight);
        float pixelsPerUnit = Math.Min(
            configuration.Width / (float)pageWidth,
            configuration.Height / (float)pageHeight);

        float scaledWidth = (float)pageWidth * pixelsPerUnit;
        float scaledHeight = (float)pageHeight * pixelsPerUnit;
        float offsetX = (configuration.Width - scaledWidth) / 2f;
        float offsetY = (configuration.Height - scaledHeight) / 2f;

        double originX = -page.Translation.X - page.Layout.UnprintableMargin.Left;
        double originY = -page.Translation.Y - page.Layout.UnprintableMargin.Bottom;

        return new ImageRenderContext(
            canvas,
            configuration,
            page.Layout,
            configuration.Width,
            configuration.Height,
            originX,
            originY,
            pixelsPerUnit,
            offsetX,
            offsetY);
    }

    public static ImageRenderContext CreateViewportContext(
        SixLabors.ImageSharp.Image<Rgba32> canvas,
        Layout layout,
        ImageConfiguration configuration,
        BoundingBox modelBounds,
        float pixelsPerUnit)
    {
        return new ImageRenderContext(
            canvas,
            configuration,
            layout,
            canvas.Width,
            canvas.Height,
            modelBounds.Min.X,
            modelBounds.Min.Y,
            pixelsPerUnit);
    }

    public PointF ToPixelPoint(XY point)
    {
        float x = this.OffsetX + (float)((point.X - this.OriginX) * this.PixelsPerUnit);
        float y = this.PixelHeight - this.OffsetY - (float)((point.Y - this.OriginY) * this.PixelsPerUnit);
        return new PointF(x, y);
    }

    public PointF ToPixelPoint(XYZ point)
    {
        return this.ToPixelPoint(point.Convert<XY>());
    }

    public float ToPixelLength(double value)
    {
        return (float)value * this.PixelsPerUnit;
    }
}
