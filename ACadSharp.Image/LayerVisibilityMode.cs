namespace ACadSharp.Image;

/// <summary>
/// Controls how layer and entity state in the drawing affects what is rendered.
/// </summary>
public enum LayerVisibilityMode
{
    /// <summary>Render everything regardless of layer state. This is the default and matches earlier versions.</summary>
    All,

    /// <summary>Hide entities on layers that are off or frozen, entities flagged invisible, and layers frozen in the current viewport.</summary>
    Screen,

    /// <summary><see cref="Screen"/> plus hide entities on non-plottable layers.</summary>
    Plot,
}
