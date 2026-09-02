using ACadSharp.Entities;
using ACadSharp.Image.Extensions;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Resolves <see cref="ImageStyle"/> values from CAD entity properties.
/// </summary>
/// <remarks>
/// This class reads color and line weight information from an <see cref="Entity"/>
/// and converts it into surface-unit rendering values using the
/// <see cref="ImageRenderContext"/> the entity is drawn in.
/// </remarks>
internal sealed class ImageStyleResolver
{
    /// <summary>
    /// Resolves the visual style for a CAD entity in the given context.
    /// </summary>
    /// <param name="entity">The entity whose style should be resolved.</param>
    /// <param name="context">The context that maps drawing units onto the surface.</param>
    /// <returns>
    /// An <see cref="ImageStyle"/> containing the stroke color (in RGBA)
    /// and stroke width (in surface units) for the entity.
    /// </returns>
    public ImageStyle Resolve(Entity entity, ImageRenderContext context)
    {
        return new ImageStyle(
            entity.GetActiveColor().ToImageColor(context.Configuration.ResolveForegroundColor()),
            context.ToStrokeWidth(entity.GetActiveLineWeightType()));
    }
}
