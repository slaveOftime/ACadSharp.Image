using ACadSharp.Entities;
using ACadSharp.Image.Extensions;
using CSMath;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using ImageColor = SixLabors.ImageSharp.Color;
using ImagePoint = SixLabors.ImageSharp.Point;
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
    /// Renders the specified page into a <see cref="RenderedImagePage"/>.
    /// </summary>
    /// <param name="page">The page to render.</param>
    /// <returns>A <see cref="RenderedImagePage"/> containing the rendered canvas.</returns>
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
    public RenderedImagePage Render(ImagePage page)
    {
        var image = new Image<Rgba32>(this._configuration.Width, this._configuration.Height, this._configuration.BackgroundColor);
        ImageRenderContext context = ImageRenderContext.CreatePageContext(image, page, this._configuration);

        foreach (Viewport viewport in page.Viewports)
        {
            this.DrawViewport(context, viewport);
        }

        foreach (Entity entity in page.Entities)
        {
            this._dispatcher.Draw(context, entity);
        }

        return new RenderedImagePage(page.Name, image);
    }

    private void DrawViewport(ImageRenderContext pageContext, Viewport viewport)
    {
        BoundingBox viewportBounds = viewport.GetBoundingBox();
        int viewportWidth = Math.Max(1, (int)Math.Ceiling(pageContext.ToPixelLength(viewportBounds.LengthX)));
        int viewportHeight = Math.Max(1, (int)Math.Ceiling(pageContext.ToPixelLength(viewportBounds.LengthY)));
        BoundingBox modelBounds = viewport.GetModelBoundingBox();

        using var viewportImage = new Image<Rgba32>(viewportWidth, viewportHeight, ImageColor.Transparent);
        ImageRenderContext viewportContext = ImageRenderContext.CreateViewportContext(
            viewportImage,
            pageContext.Layout,
            this._configuration,
            modelBounds,
            pageContext.PixelsPerUnit * (float)viewport.ScaleFactor);

        foreach (Entity entity in viewport.SelectEntities())
        {
            this._dispatcher.Draw(viewportContext, entity);
        }

        PointF destination = pageContext.ToPixelPoint(new XY(viewportBounds.Min.X, viewportBounds.Max.Y));
        pageContext.Canvas.Mutate(x => x.DrawImage(viewportImage, new ImagePoint((int)MathF.Round(destination.X), (int)MathF.Round(destination.Y)), 1f));
    }
}
