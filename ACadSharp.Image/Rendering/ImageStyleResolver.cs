using ACadSharp.Entities;
using ACadSharp.Image.Extensions;

namespace ACadSharp.Image.Rendering;

internal sealed class ImageStyleResolver
{
    private readonly ImageConfiguration _configuration;

    public ImageStyleResolver(ImageConfiguration configuration)
    {
        this._configuration = configuration;
    }

    public ImageStyle Resolve(Entity entity)
    {
        return new ImageStyle(
            entity.GetActiveColor().ToImageColor(),
            this._configuration.GetLineWeightPixels(entity.GetActiveLineWeightType()));
    }
}
