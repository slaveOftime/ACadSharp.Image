using ACadSharp.Entities;
using ACadSharp.Tables;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Decides whether an entity is drawn, combining the include list, the hide list and <see cref="ImageConfiguration.LayerVisibility"/>.
/// </summary>
internal sealed class EntityVisibilityFilter
{
    private readonly ImageConfiguration _configuration;

    public EntityVisibilityFilter(ImageConfiguration configuration)
    {
        this._configuration = configuration;
    }

    /// <summary>
    /// True when the entity should be drawn.
    /// </summary>
    /// <param name="entity">The entity being considered.</param>
    /// <param name="effectiveLayer">The layer the entity renders with, which for a layer "0" block member is the insert's layer.</param>
    /// <param name="effectiveLayerName">The name of <paramref name="effectiveLayer"/>, or <see cref="Layer.DefaultName"/> when there is none.</param>
    /// <param name="viewport">The viewport being drawn into, or null for page-level content.</param>
    /// <returns>True when the entity is visible under the current configuration.</returns>
    public bool IsVisible(Entity entity, Layer? effectiveLayer, string effectiveLayerName, Viewport? viewport)
    {
        if (this._configuration.IncludedLayers.Count > 0 && !this._configuration.IncludedLayers.Contains(effectiveLayerName))
        {
            return false;
        }

        if (this._configuration.HiddenLayers.Count > 0 && this._configuration.HiddenLayers.Contains(effectiveLayerName))
        {
            return false;
        }

        LayerVisibilityMode mode = this._configuration.LayerVisibility;
        if (mode == LayerVisibilityMode.All)
        {
            return true;
        }

        if (entity.IsInvisible)
        {
            return false;
        }

        if (effectiveLayer != null)
        {
            if (!effectiveLayer.IsOn || effectiveLayer.Flags.HasFlag(LayerFlags.Frozen))
            {
                return false;
            }

            if (viewport != null && viewport.FrozenLayers.Any(frozen => string.Equals(frozen.Name, effectiveLayerName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (mode == LayerVisibilityMode.Plot && !effectiveLayer.PlotFlag)
            {
                return false;
            }
        }

        return true;
    }
}
