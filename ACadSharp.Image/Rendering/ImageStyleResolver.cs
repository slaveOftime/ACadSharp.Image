using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Image.Extensions;
using ACadSharp.Tables;
using CadColor = ACadSharp.Color;
using ImageColor = SixLabors.ImageSharp.Color;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Resolves <see cref="ImageStyle"/> values from CAD entity properties.
/// </summary>
/// <remarks>
/// <para>
/// Resolution happens in two steps. <see cref="ResolveAttributes"/> substitutes ByLayer and ByBlock: ByLayer reads the
/// entity's <em>effective</em> layer (an entity on layer "0" inside a block takes the layer of the insert that placed
/// it, see <see cref="EntityRenderDispatcher.GetEffectiveLayer"/>), ByBlock reads the placing entity's resolved
/// attributes, and at top level ByBlock falls back to the defaults AutoCAD draws it with (colour 7, default weight,
/// continuous). <see cref="ToImageStyle"/> then converts the result into surface units for a render context.
/// </para>
/// <para>
/// ACadSharp's own <c>GetActiveColor</c> family cannot do this: it resolves ByLayer against the entity's stored layer
/// and ByBlock against the block record's owner, and the block-content clones it hands out have neither owner nor
/// document.
/// </para>
/// </remarks>
internal sealed class ImageStyleResolver
{
    /// <summary>AutoCAD colour index 7, the colour ByBlock resolves to when there is no block.</summary>
    private static readonly CadColor ByBackground = new(7);

    /// <summary>
    /// Resolves an entity's colour, line weight, linetype, linetype scale and opacity.
    /// </summary>
    /// <param name="entity">The entity whose attributes should be resolved.</param>
    /// <param name="effectiveLayer">The layer the entity is drawn on after layer-0 inheritance; null when it has none.</param>
    /// <param name="parent">The resolved attributes of the insert or dimension that placed the entity, or null at top level.</param>
    /// <returns>The resolved attributes, in CAD terms.</returns>
    public ResolvedStyle ResolveAttributes(Entity entity, Layer? effectiveLayer, ResolvedStyle? parent)
    {
        CadHeader? header = entity.Document?.Header ?? parent?.Header;

        CadColor color = entity.Color;
        if (color.IsByLayer)
        {
            color = effectiveLayer?.Color ?? ByBackground;
        }
        else if (color.IsByBlock)
        {
            color = parent?.Color ?? ByBackground;
        }

        if (color.IsByLayer || color.IsByBlock)
        {
            // A layer table entry itself set to ByLayer/ByBlock is malformed; draw it as the default colour.
            color = ByBackground;
        }

        LineWeightType lineWeight = entity.LineWeight switch
        {
            LineWeightType.ByLayer => effectiveLayer?.LineWeight ?? LineWeightType.Default,
            LineWeightType.ByBlock => parent?.LineWeight ?? LineWeightType.Default,
            _ => entity.LineWeight,
        };
        if (lineWeight is LineWeightType.ByLayer or LineWeightType.ByBlock)
        {
            lineWeight = LineWeightType.Default;
        }

        LineType? lineType = entity.LineType;
        if (IsNamed(lineType, LineType.ByLayerName))
        {
            lineType = effectiveLayer?.LineType;
        }
        else if (IsNamed(lineType, LineType.ByBlockName))
        {
            lineType = parent?.LineType;
        }

        if (IsNamed(lineType, LineType.ByLayerName) || IsNamed(lineType, LineType.ByBlockName))
        {
            lineType = null;
        }

        double lineTypeScale = (entity.LineTypeScale > 0d ? entity.LineTypeScale : 1d) * (parent?.LineTypeScale ?? 1d);
        float opacity = ResolveOpacity(entity, parent?.Opacity ?? 1f);

        return new ResolvedStyle(color, lineWeight, lineType, lineTypeScale, opacity, header);
    }

    /// <summary>
    /// Converts resolved attributes into the surface-unit style a context draws with.
    /// </summary>
    /// <param name="resolved">Attributes from <see cref="ResolveAttributes"/>.</param>
    /// <param name="context">The context that maps drawing units onto the surface.</param>
    /// <param name="foreground">The colour to use for AutoCAD colour index 7 ("ByBackground").</param>
    /// <returns>
    /// An <see cref="ImageStyle"/> containing the stroke color (in RGBA), stroke width and dash pattern (in surface
    /// units), and opacity.
    /// </returns>
    public ImageStyle ToImageStyle(ResolvedStyle resolved, ImageRenderContext context, ImageColor foreground)
    {
        float width = context.ToStrokeWidth(resolved.LineWeight);
        return new ImageStyle(
            resolved.Color.ToImageColor(foreground),
            width,
            LineTypeDashResolver.Resolve(resolved.LineType, resolved.Header, resolved.LineTypeScale, context, width),
            resolved.Opacity);
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

    /// <summary>
    /// True when <paramref name="lineType"/> is not null and its name matches <paramref name="name"/> case-insensitively,
    /// as used to recognise the synthetic "ByLayer"/"ByBlock" linetypes.
    /// </summary>
    /// <param name="lineType">The linetype to check, or null.</param>
    /// <param name="name">The name to compare against, typically <see cref="LineType.ByLayerName"/> or <see cref="LineType.ByBlockName"/>.</param>
    /// <returns>True when <paramref name="lineType"/> is named <paramref name="name"/>.</returns>
    internal static bool IsNamed(LineType? lineType, string name)
    {
        return lineType != null && string.Equals(lineType.Name, name, StringComparison.OrdinalIgnoreCase);
    }
}
