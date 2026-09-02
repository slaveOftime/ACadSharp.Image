using ACadSharp.Entities;
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

    public ImagePageRenderer(ImageConfiguration configuration)
    {
        this._configuration = configuration;
        this._dispatcher = new EntityRenderDispatcher(configuration);
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
    /// Renders the page onto the raster page context (see <see cref="ImageRenderContext.CreatePageContext"/>).
    /// </summary>
    /// <param name="surface">The surface receiving the page content.</param>
    /// <param name="page">The page to render.</param>
    internal void RenderTo(IDrawingSurface surface, ImagePage page)
    {
        ImageRenderContext context = ImageRenderContext.CreatePageContext(surface, page, this._configuration);

        foreach (Viewport viewport in page.Viewports)
        {
            this.DrawViewport(context, viewport);
        }

        foreach (Entity entity in page.Entities)
        {
            this._dispatcher.Draw(context, entity);
        }
    }

    private void DrawViewport(ImageRenderContext pageContext, Viewport viewport)
    {
        BoundingBox viewportBounds = viewport.GetBoundingBox();
        double viewportWidth = Math.Max(1, (int)Math.Ceiling(pageContext.ToSurfaceLength(viewportBounds.LengthX)));
        double viewportHeight = Math.Max(1, (int)Math.Ceiling(pageContext.ToSurfaceLength(viewportBounds.LengthY)));
        BoundingBox modelBounds = viewport.GetModelBoundingBox();

        SurfacePoint topLeft = pageContext.ToSurfacePoint(new XY(viewportBounds.Min.X, viewportBounds.Max.Y));
        ViewportSurface viewportSurface = pageContext.Surface.BeginViewport(new SurfaceRect(topLeft.X, topLeft.Y, viewportWidth, viewportHeight));

        double scale = pageContext.SinglePrecision
            ? (float)pageContext.Scale * (float)viewport.ScaleFactor
            : pageContext.Scale * viewport.ScaleFactor;
        ImageRenderContext viewportContext = ImageRenderContext.CreateViewportContext(pageContext, viewport, viewportSurface, viewportWidth, modelBounds, scale);

        foreach (Entity entity in viewport.SelectEntities())
        {
            this._dispatcher.Draw(viewportContext, entity);
        }

        pageContext.Surface.EndViewport(viewportSurface);
    }
}
