using ImageColor = SixLabors.ImageSharp.Color;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Represents the visual styling applied when rendering a single CAD entity.
/// </summary>
/// <remarks>
/// This immutable record bundles stroke color and width together, avoiding
/// repeated lookups during entity rendering. It is resolved from entity
/// properties (color, line weight) by <see cref="ImageStyleResolver"/>.
/// </remarks>
internal readonly record struct ImageStyle(ImageColor StrokeColor, float StrokeWidth);
