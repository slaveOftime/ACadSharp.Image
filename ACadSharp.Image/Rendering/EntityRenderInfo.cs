using ImageColor = SixLabors.ImageSharp.Color;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Identifies the entity currently being drawn so structured backends can group and tag their output.
/// </summary>
/// <param name="LayerName">Effective layer name (entities on layer "0" inside a block inherit the insert's layer).</param>
/// <param name="EntityType">DXF object name, e.g. <c>LINE</c>.</param>
/// <param name="Handle">Entity handle.</param>
/// <param name="ParentHandle">Handle of the owning <c>Insert</c> or <c>Dimension</c> when drawing nested content.</param>
/// <param name="BlockName">Block name when drawing nested content of an <c>Insert</c>.</param>
internal sealed record EntityRenderInfo(string LayerName, string EntityType, ulong Handle, ulong? ParentHandle, string? BlockName);

/// <summary>
/// Layer defaults a structured backend may hoist onto a group element.
/// </summary>
internal sealed record LayerRenderInfo(string LayerName, ImageColor Color, float StrokeWidth);
