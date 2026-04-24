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
        int drawableWidth = configuration.Width - configuration.PaddingLeft - configuration.PaddingRight;
        int drawableHeight = configuration.Height - configuration.PaddingTop - configuration.PaddingBottom;
        if (drawableWidth <= 0 || drawableHeight <= 0)
        {
            throw new InvalidOperationException("Padding must leave at least one drawable pixel in both dimensions.");
        }

        Layout layout = page.Layout ?? new Layout("default_page");
        double pageWidth = Math.Max(1d, layout.PaperWidth);
        double pageHeight = Math.Max(1d, layout.PaperHeight);
        float pixelsPerUnit = Math.Min(
            drawableWidth / (float)pageWidth,
            drawableHeight / (float)pageHeight);

        float scaledWidth = (float)pageWidth * pixelsPerUnit;
        float scaledHeight = (float)pageHeight * pixelsPerUnit;
        float offsetX = configuration.PaddingLeft + ((drawableWidth - scaledWidth) / 2f);
        float offsetY = configuration.PaddingBottom + ((drawableHeight - scaledHeight) / 2f);

        double originX = -page.Translation.X - layout.UnprintableMargin.Left;
        double originY = -page.Translation.Y - layout.UnprintableMargin.Bottom;

        return new ImageRenderContext(
            canvas,
            configuration,
            layout,
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
