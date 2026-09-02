using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Image.Rendering.Svg;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using SixLabors.ImageSharp;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Renders a single <see cref="ImagePage"/> (including its viewports and entities) into an image.
/// </summary>
/// <remarks>
/// <para>
/// This class is the core rendering engine for a single page. It creates the output canvas,
/// processes all viewports (which may contain nested model-space views), and dispatches
/// entity rendering to <see cref="EntityRenderDispatcher"/>.
/// </para>
/// <para>
/// Each instance is scoped to a specific <see cref="ImageConfiguration"/> and should not
/// be shared across threads.
/// </para>
/// </remarks>
internal sealed class ImagePageRenderer
{
    private readonly ImageConfiguration _configuration;
    private readonly EntityRenderDispatcher _dispatcher;
    private readonly EntityVisibilityFilter _visibilityFilter;

    public ImagePageRenderer(ImageConfiguration configuration)
    {
        this._configuration = configuration;
        this._dispatcher = new EntityRenderDispatcher(configuration);
        this._visibilityFilter = new EntityVisibilityFilter(configuration);
    }

    /// <summary>
    /// Renders the specified page into a <see cref="RenderedPage"/>.
    /// </summary>
    /// <param name="page">The page to render.</param>
    /// <param name="format">The output format the rendered page will be saved as.</param>
    /// <returns>A <see cref="RenderedPage"/> containing the rendered canvas.</returns>
    /// <remarks>
    /// <para>
    /// The rendering process follows these steps:
    /// <list type="number">
    ///   <item>Creates a canvas with the configured dimensions and background color.</item>
    ///   <item>Renders each viewport's model-space contents at the appropriate scale and position.</item>
    ///   <item>Renders page-level entities (e.g., annotations) on top.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public RenderedPage Render(ImagePage page, ImageExportFormat format)
    {
        if (format == ImageExportFormat.Svg)
        {
            return this.RenderSvg(page);
        }

        Image<Rgba32> image = new(this._configuration.Width, this._configuration.Height, this._configuration.BackgroundColor);
        try
        {
            using RasterDrawingSurface surface = new(image, this._configuration, ownsCanvas: false);
            this.RenderTo(surface, page);
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return new RenderedImagePage(page.Name, image, format, this._configuration.OutputQuality);
    }

    /// <summary>
    /// Re-frames an auto-sized page on its visible entities, then renders it onto the raster page context
    /// (see <see cref="ImageRenderContext.CreatePageContext"/>).
    /// </summary>
    /// <param name="surface">The surface receiving the page content.</param>
    /// <param name="page">The page to render.</param>
    internal void RenderTo(IDrawingSurface surface, ImagePage page)
    {
        this.RefitAutoSizedPage(page);
        this.RenderTo(ImageRenderContext.CreatePageContext(surface, page, this._configuration), page);
    }

    /// <summary>
    /// Re-frames a page whose size was derived from its content, so that hidden entities do not stretch the frame.
    /// </summary>
    /// <param name="page">The page about to be rendered.</param>
    /// <remarks>
    /// Only top-level page entities take part; entities shown through a viewport are framed by the viewport itself,
    /// and pages that carry a layout's paper size are left alone.
    /// </remarks>
    private void RefitAutoSizedPage(ImagePage page)
    {
        if (!page.AutoSized)
        {
            return;
        }

        page.UpdateLayoutSize(entity =>
        {
            Layer? layer = EntityRenderDispatcher.GetEffectiveLayer(entity, null);
            return this._visibilityFilter.IsVisible(entity, layer, layer?.Name ?? Layer.DefaultName, null);
        });
    }

    /// <summary>
    /// Renders the page's viewports and then its page-level entities through the given page context.
    /// </summary>
    /// <param name="context">The page-level context.</param>
    /// <param name="page">The page to render.</param>
    private void RenderTo(ImageRenderContext context, ImagePage page)
    {
        foreach (Viewport viewport in page.Viewports)
        {
            this.DrawViewport(context, viewport);
        }

        foreach (Entity entity in page.Entities)
        {
            this._dispatcher.Draw(context, entity);
        }
    }

    /// <summary>
    /// Renders the page into SVG markup.
    /// </summary>
    /// <param name="page">The page to render.</param>
    /// <returns>The rendered SVG page.</returns>
    private RenderedSvgPage RenderSvg(ImagePage page)
    {
        this.RefitAutoSizedPage(page);
        SurfaceRect viewBox = ImageRenderContext.ComputeSvgViewBox(page, this._configuration);
        SvgOptions options = this._configuration.Svg;
        double? strokeUnits = options.NonScalingStroke
            ? null
            : ImageRenderContext.UnitsPerMillimeter(page.Document?.Header.InsUnits ?? UnitsType.Unitless);

        using SvgDrawingSurface surface = new(
            this._configuration,
            viewBox,
            options.EmitSize ? this._configuration.Width : null,
            options.EmitSize ? this._configuration.Height : null,
            strokeUnits);

        ImageRenderContext context = ImageRenderContext.CreateSvgPageContext(surface, page, this._configuration, strokeUnits);

        this.RenderTo(context, page);

        return new RenderedSvgPage(page.Name, surface.ToSvgString());
    }

    private void DrawViewport(ImageRenderContext pageContext, Viewport viewport)
    {
        BoundingBox viewportBounds = viewport.GetBoundingBox();
        double viewportWidth = pageContext.SinglePrecision
            ? Math.Max(1, (int)Math.Ceiling(pageContext.ToSurfaceLength(viewportBounds.LengthX)))
            : pageContext.ToSurfaceLength(viewportBounds.LengthX);
        double viewportHeight = pageContext.SinglePrecision
            ? Math.Max(1, (int)Math.Ceiling(pageContext.ToSurfaceLength(viewportBounds.LengthY)))
            : pageContext.ToSurfaceLength(viewportBounds.LengthY);
        BoundingBox modelBounds = viewport.GetModelBoundingBox();

        SurfacePoint topLeft = pageContext.ToSurfacePoint(new XY(viewportBounds.Min.X, viewportBounds.Max.Y));
        ViewportSurface viewportSurface = pageContext.Surface.BeginViewport(new SurfaceRect(topLeft.X, topLeft.Y, viewportWidth, viewportHeight));

        double scale = pageContext.SinglePrecision
            ? (float)pageContext.Scale * (float)viewport.ScaleFactor
            : pageContext.Scale * viewport.ScaleFactor;
        bool paperSpaceLineTypeScaling = (viewport.Document?.Header.PaperSpaceLineTypeScaling ?? SpaceLineTypeScaling.Viewport) == SpaceLineTypeScaling.Viewport;
        double lineTypeScale = paperSpaceLineTypeScaling
            ? pageContext.LineTypeScale
            : pageContext.LineTypeScale * viewport.ScaleFactor;
        ImageRenderContext viewportContext = ImageRenderContext.CreateViewportContext(pageContext, viewport, viewportSurface, viewportWidth, modelBounds, scale, lineTypeScale);

        foreach (Entity entity in viewport.SelectEntities())
        {
            this._dispatcher.Draw(viewportContext, entity);
        }

        pageContext.Surface.EndViewport(viewportSurface);
    }
}
