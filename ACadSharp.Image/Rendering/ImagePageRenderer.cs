using System.Globalization;
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Image.Rendering.Svg;
using ACadSharp.IO;
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
    /// Renders the page onto the raster page context of its visible frame (see <see cref="ResolveFrame"/> and
    /// <see cref="ImageRenderContext.CreatePageContext(IDrawingSurface, PageFrame, ImageConfiguration)"/>).
    /// </summary>
    /// <param name="surface">The surface receiving the page content.</param>
    /// <param name="page">The page to render.</param>
    internal void RenderTo(IDrawingSurface surface, ImagePage page)
    {
        this.RenderTo(ImageRenderContext.CreatePageContext(surface, this.ResolveFrame(page), this._configuration), page);
    }

    /// <summary>
    /// The frame to render a page in: for a page whose size was derived from its content and a configuration that can
    /// hide entities, the frame fitted to the visible entities, so hidden ones do not stretch it; otherwise the page's
    /// own frame.
    /// </summary>
    /// <param name="page">The page about to be rendered.</param>
    /// <returns>The frame to map onto the surface.</returns>
    /// <remarks>
    /// The page itself is never modified, so a later render with different filters starts from the frame the caller
    /// built. Only top-level page entities take part; entities shown through a viewport are framed by the viewport
    /// itself, and pages that carry a layout's paper size keep it. When every visible entity has non-finite bounds
    /// the page's own frame is used.
    /// </remarks>
    private PageFrame ResolveFrame(ImagePage page)
    {
        if (!page.AutoSized || !this.HasActiveFilters())
        {
            return PageFrame.Of(page);
        }

        return page.ComputeFrame(entity =>
        {
            Layer? layer = EntityRenderDispatcher.GetEffectiveLayer(entity, null);
            return this._visibilityFilter.IsVisible(entity, layer, layer?.Name ?? Layer.DefaultName, null);
        }) ?? PageFrame.Of(page);
    }

    /// <summary>
    /// True when the configuration can hide an entity, so the visible extents may differ from the page extents.
    /// </summary>
    /// <returns>True when an include list, a hide list or a visibility mode other than <see cref="LayerVisibilityMode.All"/> is set.</returns>
    private bool HasActiveFilters() =>
        this._configuration.IncludedLayers.Count > 0
        || this._configuration.HiddenLayers.Count > 0
        || this._configuration.LayerVisibility != LayerVisibilityMode.All;

    /// <summary>
    /// Renders the page's entities and viewports through the given page context, in the order they were added.
    /// </summary>
    /// <param name="context">The page-level context.</param>
    /// <param name="page">The page to render.</param>
    /// <remarks>
    /// Only a viewport added through <see cref="ImagePage.AddViewport(Viewport)"/> is drawn as a window onto model
    /// space. One that reached the page through <see cref="ImagePage.AddEntity(Entity)"/> (as the paper viewport of a
    /// layout block does) is an ordinary page entity and goes to the dispatcher, which reports it as not implemented.
    /// </remarks>
    private void RenderTo(ImageRenderContext context, ImagePage page)
    {
        // The dispatcher outlives a single page render (this renderer can render several pages, see the class
        // remarks), so its per-block MLINE/LEADER subtree cache must not carry a result computed for a different
        // page — or an earlier render of this same page, whose document may have been edited since — into this one.
        this._dispatcher.BeginPage();

        // Viewport does not override Equals, so the default comparer is reference equality: the set answers
        // "did this very viewport come through AddViewport?", not "is there an equal-looking one".
        HashSet<Viewport> windows = new(page.Viewports);
        foreach (Entity item in page.DrawSequence)
        {
            if (item is Viewport viewport && windows.Contains(viewport))
            {
                this.DrawViewport(context, viewport);
            }
            else
            {
                this._dispatcher.Draw(context, item);
            }
        }
    }

    /// <summary>
    /// Renders the page into SVG markup.
    /// </summary>
    /// <param name="page">The page to render.</param>
    /// <returns>The rendered SVG page.</returns>
    private RenderedSvgPage RenderSvg(ImagePage page)
    {
        PageFrame frame = this.ResolveFrame(page);
        SurfaceRect viewBox = ImageRenderContext.ComputeSvgViewBox(frame, this._configuration);
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

        ImageRenderContext context = ImageRenderContext.CreateSvgPageContext(surface, frame, this._configuration, strokeUnits);

        this.RenderTo(context, page);

        return new RenderedSvgPage(page.Name, surface.ToSvgString());
    }

    /// <summary>
    /// Surface units per linetype unit inside a viewport, decided by the PSLTSCALE header variable.
    /// </summary>
    /// <param name="header">Header of the document being rendered, or null when there is none.</param>
    /// <param name="pageLineTypeScale">Surface units per linetype unit on the page around the viewport.</param>
    /// <param name="viewportScaleFactor">Paper units per model unit shown by the viewport.</param>
    /// <returns>
    /// <paramref name="pageLineTypeScale"/> when linetypes are scaled to paper space, so dashes are the same length
    /// everywhere on the sheet; otherwise it times <paramref name="viewportScaleFactor"/>, so dashes keep their model-space
    /// length and shrink with the viewport.
    /// </returns>
    /// <remarks>
    /// The branch is on the raw DXF value of <c>$PSLTSCALE</c> (0 = model-space lengths, 1 = paper-space lengths, and the
    /// AutoCAD default) rather than on the name of the <see cref="SpaceLineTypeScaling"/> member, because the ACadSharp
    /// 3.7.1 names are swapped with respect to those semantics: <c>SpaceLineTypeScaling.Viewport</c> is 0 and
    /// <c>SpaceLineTypeScaling.Normal</c> is 1.
    /// </remarks>
    internal static double ResolveViewportLineTypeScale(CadHeader? header, double pageLineTypeScale, double viewportScaleFactor)
    {
        int psltscale = header == null ? 1 : (int)header.PaperSpaceLineTypeScaling;
        return psltscale == 1 ? pageLineTypeScale : pageLineTypeScale * viewportScaleFactor;
    }

    /// <summary>
    /// Draws one paper-space viewport: its window on the page surface, and the model-space entities it shows.
    /// </summary>
    /// <param name="pageContext">The page-level context the viewport sits on.</param>
    /// <param name="viewport">The viewport to draw.</param>
    private void DrawViewport(ImageRenderContext pageContext, Viewport viewport)
    {
        BoundingBox viewportBounds = viewport.GetBoundingBox();
        // Exact size for both backends; the raster surface rounds its own image up to whole pixels.
        double viewportWidth = pageContext.ToSurfaceLength(viewportBounds.LengthX);
        double viewportHeight = pageContext.ToSurfaceLength(viewportBounds.LengthY);
        BoundingBox modelBounds = viewport.GetModelBoundingBox();

        SurfacePoint topLeft = pageContext.ToSurfacePoint(new XY(viewportBounds.Min.X, viewportBounds.Max.Y));
        ViewportSurface viewportSurface = pageContext.Surface.BeginViewport(new SurfaceRect(topLeft.X, topLeft.Y, viewportWidth, viewportHeight));

        double scale = pageContext.SinglePrecision
            ? (float)pageContext.Scale * (float)viewport.ScaleFactor
            : pageContext.Scale * viewport.ScaleFactor;
        double lineTypeScale = ResolveViewportLineTypeScale(viewport.Document?.Header, pageContext.LineTypeScale, viewport.ScaleFactor);
        ImageRenderContext viewportContext = ImageRenderContext.CreateViewportContext(pageContext, viewport, viewportSurface, viewportWidth, modelBounds, scale, lineTypeScale);

        foreach (Entity entity in this.SelectViewportEntities(viewport))
        {
            this._dispatcher.Draw(viewportContext, entity);
        }

        pageContext.Surface.EndViewport(viewportSurface);
    }

    /// <summary>
    /// The model-space entities a viewport shows, in the drawing's draw order: those whose bounds
    /// (<see cref="EntityBounds"/>, the same bounds the page framer uses, so a wipeout or an OCS solid is culled by
    /// the region it actually draws, not ACadSharp's raw <c>GetBoundingBox</c>) overlap or touch the view box in the
    /// XY plane (<see cref="OverlapsInPlane"/>), including an entity that encloses the view box or crosses it
    /// without either endpoint inside it. This does not mirror <c>Viewport.SelectEntities</c>, whose corner-based
    /// <c>BoundingBox.IsIn</c> check culls both of those cases; an entity <see cref="EntityBounds"/> cannot bound at
    /// all is skipped with a warning instead of aborting the page.
    /// </summary>
    /// <param name="viewport">The viewport to select the contents of.</param>
    /// <returns>The model-space entities to draw inside the viewport.</returns>
    internal IEnumerable<Entity> SelectViewportEntities(Viewport viewport)
    {
        if (viewport.Document == null)
        {
            this._configuration.Notify($"[{viewport.SubclassMarker}] Handle {viewport.Handle.ToString("X", CultureInfo.InvariantCulture)}: viewport has no document; contents skipped.", NotificationType.Warning);
            yield break;
        }

        BoundingBox box = viewport.GetModelBoundingBox();
        foreach (Entity entity in viewport.Document.ModelSpace.GetSortedEntities())
        {
            if (entity is Insert { Block: null })
            {
                // Called out ahead of EntityBounds.TryGet so the warning names the actual cause: an unresolved
                // block reference, not "bounds could not be computed".
                this._configuration.Notify($"[{entity.SubclassMarker}] Handle {entity.Handle.ToString("X", CultureInfo.InvariantCulture)}: block reference has no block; skipped in viewport.", NotificationType.Warning);
                continue;
            }

            if (!EntityBounds.TryGet(entity, out BoundingBox bounds, out Exception? error))
            {
                // error is null when the entity has no bounds for a reason that is not a computation failure (a
                // wipeout that would draw nothing, e.g. ShowImage off or an inverted clip DrawWipeout already
                // handles at the page level): nothing is wrong with it, so it is skipped without a Warning.
                if (error != null)
                {
                    this._configuration.Notify($"[{entity.SubclassMarker}] Handle {entity.Handle.ToString("X", CultureInfo.InvariantCulture)}: bounds could not be computed ({error.Message}); entity skipped in viewport.", NotificationType.Warning, error);
                }

                continue;
            }

            if (OverlapsInPlane(box, bounds))
            {
                yield return entity;
            }
        }
    }

    /// <summary>
    /// True when two bounds overlap or touch in the XY plane (Z ignored), by axis-aligned interval overlap on X and
    /// Y independently. Unlike <c>BoundingBox.IsIn</c>, this also keeps an entity whose bounds enclose
    /// <paramref name="window"/> entirely, or cross it without either bound's own corner lying inside the other.
    /// </summary>
    /// <param name="window">The viewport's model-space view box.</param>
    /// <param name="bounds">The candidate entity's bounds.</param>
    /// <returns>True when the two bounds overlap or touch on both axes.</returns>
    private static bool OverlapsInPlane(BoundingBox window, BoundingBox bounds)
    {
        return bounds.Min.X <= window.Max.X && bounds.Max.X >= window.Min.X
            && bounds.Min.Y <= window.Max.Y && bounds.Max.Y >= window.Min.Y;
    }
}
