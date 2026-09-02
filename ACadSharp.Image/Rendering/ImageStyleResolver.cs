using ACadSharp.Entities;
using ACadSharp.Image.Extensions;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Resolves <see cref="ImageStyle"/> values from CAD entity properties.
/// </summary>
/// <remarks>
/// This class reads color, line weight and linetype information from an <see cref="Entity"/>
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
    /// <param name="parentOpacity">The opacity to inherit when the entity's transparency is ByBlock.</param>
    /// <returns>
    /// An <see cref="ImageStyle"/> containing the stroke color (in RGBA),
    /// stroke width and dash pattern (in surface units), and opacity for the entity.
    /// </returns>
    public ImageStyle Resolve(Entity entity, ImageRenderContext context, float parentOpacity)
    {
        float width = context.ToStrokeWidth(entity.GetActiveLineWeightType());
        return new ImageStyle(
            entity.GetActiveColor().ToImageColor(context.Configuration.ResolveForegroundColor()),
            width,
            LineTypeDashResolver.Resolve(entity, context, width),
            ResolveOpacity(entity, parentOpacity));
    }

    /// <summary>
    /// Maps CAD transparency to opacity. ByLayer is opaque (ACadSharp 3.7.1 layers carry no transparency);
    /// ByBlock inherits the parent's opacity; explicit values 0..90 mean that percentage transparent.
    /// </summary>
    internal static float ResolveOpacity(Entity entity, float parentOpacity)
    {
        Transparency transparency = entity.Transparency;
        if (transparency.IsByLayer)
        {
            return 1f;
        }

        if (transparency.IsByBlock)
        {
            return parentOpacity;
        }

        return Math.Clamp(1f - (transparency.Value / 100f), 0f, 1f);
    }
}
