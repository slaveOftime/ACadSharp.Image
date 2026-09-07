using ACadSharp.Objects;
using CSMath;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// The rectangle of drawing space a page maps onto the output: the paper size and the translation that moves the
/// content's minimum corner to the origin, plus the layout that supplies the unprintable margins.
/// </summary>
/// <remarks>
/// A frame is a value taken from an <see cref="ImagePage"/> (or computed from a subset of its entities when layers are
/// filtered) so that rendering can fit the visible content without writing anything back to the page.
/// </remarks>
/// <param name="Layout">Layout whose margins apply; the page's own or a default one.</param>
/// <param name="Translation">Offset applied to drawing coordinates before fitting.</param>
/// <param name="PaperWidth">Width of the framed area in drawing units.</param>
/// <param name="PaperHeight">Height of the framed area in drawing units.</param>
internal readonly record struct PageFrame(Layout Layout, XY Translation, double PaperWidth, double PaperHeight)
{
    /// <summary>
    /// The frame a page currently carries.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <returns>Its translation and paper size.</returns>
    public static PageFrame Of(ImagePage page)
    {
        Layout layout = page.Layout ?? new Layout("default_page");
        return new PageFrame(layout, page.Translation, layout.PaperWidth, layout.PaperHeight);
    }
}
