using ImageColor = SixLabors.ImageSharp.Color;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Resolved visual style for one entity. Widths and dash lengths are in surface units.
/// </summary>
/// <remarks>
/// <c>new ImageStyle()</c> / <see langword="default"/> has <see cref="Opacity"/> 0 and is therefore invisible; use one
/// of the constructors instead.
/// </remarks>
/// <param name="StrokeColor">Stroke and fill colour.</param>
/// <param name="StrokeWidth">Stroke width in surface units.</param>
/// <param name="DashPattern">Alternating dash and gap lengths in surface units, or <see langword="null"/> for a solid stroke.</param>
/// <param name="Opacity">Opacity from 0 (invisible) to 1 (opaque).</param>
internal readonly record struct ImageStyle(ImageColor StrokeColor, float StrokeWidth, float[]? DashPattern, float Opacity)
{
    public ImageStyle(ImageColor strokeColor, float strokeWidth)
        : this(strokeColor, strokeWidth, null, 1f)
    {
    }

    /// <summary>
    /// Gets the stroke colour with <see cref="Opacity"/> applied to its alpha channel.
    /// </summary>
    public ImageColor EffectiveColor => this.Opacity >= 1f ? this.StrokeColor : this.StrokeColor.WithAlpha(this.Opacity);
}
