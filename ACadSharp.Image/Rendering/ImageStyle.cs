using ImageColor = SixLabors.ImageSharp.Color;

namespace ACadSharp.Image.Rendering;

internal readonly record struct ImageStyle(ImageColor StrokeColor, float StrokeWidth);
