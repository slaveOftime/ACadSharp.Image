using ACadSharp.Entities;
using ACadSharp.Objects;
using CSMath;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Maps drawing coordinates onto an <see cref="IDrawingSurface"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>x = OffsetX + (p.X - OriginX) * Scale</c> and <c>y = SurfaceHeight - OffsetY - (p.Y - OriginY) * Scale</c>.
/// </para>
/// <para>
/// When <see cref="SinglePrecision"/> is true the arithmetic is performed in <see cref="float"/> in the same order
/// the original raster renderer used, so raster output stays pixel-identical.
/// </para>
/// </remarks>
internal sealed class ImageRenderContext
{
    public ImageRenderContext(
        IDrawingSurface surface,
        ImageConfiguration configuration,
        Layout layout,
        double surfaceWidth,
        double surfaceHeight,
        double originX,
        double originY,
        double scale,
        double offsetX,
        double offsetY,
        bool singlePrecision,
        double lineTypeScale,
        Viewport? viewport = null,
        ImageRenderContext? parent = null)
    {
        this.Surface = surface;
        this.Configuration = configuration;
        this.Layout = layout;
        this.SurfaceWidth = surfaceWidth;
        this.SurfaceHeight = surfaceHeight;
        this.OriginX = originX;
        this.OriginY = originY;
        this.Scale = scale;
        this.OffsetX = offsetX;
        this.OffsetY = offsetY;
        this.SinglePrecision = singlePrecision;
        this.LineTypeScale = lineTypeScale;
        this.Viewport = viewport;
        this.Parent = parent;
    }

    /// <summary>Surface that receives the primitives produced from this context.</summary>
    public IDrawingSurface Surface { get; }

    /// <summary>Configuration driving the export.</summary>
    public ImageConfiguration Configuration { get; }

    /// <summary>Layout the content belongs to.</summary>
    public Layout Layout { get; }

    /// <summary>Surface width in surface units.</summary>
    public double SurfaceWidth { get; }

    /// <summary>Surface height in surface units; used to flip the Y axis.</summary>
    public double SurfaceHeight { get; }

    /// <summary>Drawing X coordinate that maps onto <see cref="OffsetX"/>.</summary>
    public double OriginX { get; }

    /// <summary>Drawing Y coordinate that maps onto the surface bottom.</summary>
    public double OriginY { get; }

    /// <summary>Surface units per drawing unit.</summary>
    public double Scale { get; }

    /// <summary>Surface X of the mapped <see cref="OriginX"/>.</summary>
    public double OffsetX { get; }

    /// <summary>Surface offset of the mapped <see cref="OriginY"/> from the surface bottom.</summary>
    public double OffsetY { get; }

    /// <summary>True for the raster backend: reproduces the original float arithmetic exactly.</summary>
    public bool SinglePrecision { get; }

    /// <summary>Surface units per linetype unit; differs from <see cref="Scale"/> inside viewports with paper-space linetype scaling.</summary>
    public double LineTypeScale { get; }

    /// <summary>Viewport whose contents are being drawn, or null for page-level content.</summary>
    public Viewport? Viewport { get; }

    /// <summary>Context that opened this one, or null for the page context.</summary>
    public ImageRenderContext? Parent { get; }

    /// <summary>
    /// Creates the page-level context that maps paper space onto the full surface.
    /// </summary>
    /// <param name="surface">Surface receiving the page content.</param>
    /// <param name="page">Page being rendered.</param>
    /// <param name="configuration">Configuration driving the export.</param>
    /// <returns>A context centred on the drawable area left by the configured padding.</returns>
    public static ImageRenderContext CreatePageContext(IDrawingSurface surface, ImagePage page, ImageConfiguration configuration)
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
            surface,
            configuration,
            layout,
            configuration.Width,
            configuration.Height,
            originX,
            originY,
            pixelsPerUnit,
            offsetX,
            offsetY,
            singlePrecision: true,
            lineTypeScale: pixelsPerUnit);
    }

    /// <summary>
    /// Creates the context that maps a viewport's model space onto the viewport surface.
    /// </summary>
    /// <param name="parent">Context that opened the viewport.</param>
    /// <param name="viewport">Viewport being drawn.</param>
    /// <param name="surface">Surface returned by <see cref="IDrawingSurface.BeginViewport"/>.</param>
    /// <param name="modelBounds">Model-space bounds shown by the viewport.</param>
    /// <param name="scale">Surface units per model unit.</param>
    /// <returns>A context whose origin is the bottom-left corner of <paramref name="modelBounds"/>.</returns>
    public static ImageRenderContext CreateViewportContext(ImageRenderContext parent, Viewport viewport, ViewportSurface surface, BoundingBox modelBounds, double scale)
    {
        return new ImageRenderContext(
            surface.Surface,
            parent.Configuration,
            parent.Layout,
            surfaceWidth: 0d,
            surfaceHeight: surface.BottomY,
            originX: modelBounds.Min.X,
            originY: modelBounds.Min.Y,
            scale: scale,
            offsetX: surface.OffsetX,
            offsetY: 0d,
            singlePrecision: parent.SinglePrecision,
            lineTypeScale: scale,
            viewport: viewport,
            parent: parent);
    }

    /// <summary>
    /// Projects a drawing point onto the surface.
    /// </summary>
    /// <param name="point">Point in drawing coordinates.</param>
    /// <returns>The point in surface coordinates.</returns>
    public SurfacePoint ToSurfacePoint(XY point)
    {
        if (this.SinglePrecision)
        {
            float x = (float)this.OffsetX + (float)((point.X - this.OriginX) * (float)this.Scale);
            float y = (float)this.SurfaceHeight - (float)this.OffsetY - (float)((point.Y - this.OriginY) * (float)this.Scale);
            return new SurfacePoint(x, y);
        }

        return new SurfacePoint(
            this.OffsetX + ((point.X - this.OriginX) * this.Scale),
            this.SurfaceHeight - this.OffsetY - ((point.Y - this.OriginY) * this.Scale));
    }

    /// <summary>
    /// Projects a drawing point onto the surface, discarding the Z coordinate.
    /// </summary>
    /// <param name="point">Point in drawing coordinates.</param>
    /// <returns>The point in surface coordinates.</returns>
    public SurfacePoint ToSurfacePoint(XYZ point)
    {
        return this.ToSurfacePoint(point.Convert<XY>());
    }

    /// <summary>
    /// Converts a drawing length into surface units.
    /// </summary>
    /// <param name="value">Length in drawing units.</param>
    /// <returns>The length in surface units.</returns>
    public double ToSurfaceLength(double value)
    {
        return this.SinglePrecision
            ? (float)value * (float)this.Scale
            : value * this.Scale;
    }

    /// <summary>
    /// Stroke width in surface units for a line weight. Raster: pixels from the configuration table.
    /// </summary>
    /// <param name="lineWeight">Line weight to convert.</param>
    /// <returns>The stroke width in surface units.</returns>
    public float ToStrokeWidth(LineWeightType lineWeight)
    {
        return this.Configuration.GetLineWeightPixels(lineWeight);
    }
}
