using ACadSharp.Entities;
using ACadSharp.Image.Extensions;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Resolves <see cref="ImageStyle"/> values from CAD entity properties.
/// </summary>
/// <remarks>
/// This class reads color and line weight information from an <see cref="Entity"/>
/// and converts it into pixel-based rendering values using the configured
/// <see cref="ImageConfiguration"/>.
/// </remarks>
internal sealed class ImageStyleResolver
{
    private readonly ImageConfiguration _configuration;

    public ImageStyleResolver(ImageConfiguration configuration)
    {
        this._configuration = configuration;
    }

    /// <summary>
    /// Resolves the visual style for a CAD entity.
    /// </summary>
    /// <param name="entity">The entity whose style should be resolved.</param>
    /// <returns>
    /// An <see cref="ImageStyle"/> containing the stroke color (in RGBA)
    /// and stroke width (in pixels) for the entity.
    /// </returns>
    public ImageStyle Resolve(Entity entity)
    {
        return new ImageStyle(
            entity.GetActiveColor().ToImageColor(),
            this._configuration.GetLineWeightPixels(entity.GetActiveLineWeightType()));
    }
}
