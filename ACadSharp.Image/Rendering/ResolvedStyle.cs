using ACadSharp.Header;
using ACadSharp.Tables;
using CadColor = ACadSharp.Color;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// An entity's drawing attributes with ByLayer and ByBlock already substituted, in CAD terms (before any conversion to
/// surface units).
/// </summary>
/// <remarks>
/// The record doubles as the inheritance source for the entity's children: block contents and dimension geometry
/// resolve their ByBlock attributes against the placing entity's resolved values, and inherit its document header,
/// since the clones <c>Insert.Explode()</c> produces in ACadSharp 3.7.1 belong to no document.
/// </remarks>
/// <param name="Color">Resolved colour; never ByLayer or ByBlock.</param>
/// <param name="LineWeight">Resolved line weight; never ByLayer or ByBlock.</param>
/// <param name="LineType">Resolved linetype, or null for a solid stroke.</param>
/// <param name="LineTypeScale">Effective CELTSCALE: the entity's own times every enclosing insert's.</param>
/// <param name="Opacity">Resolved opacity, 0..1.</param>
/// <param name="Header">Header of the document the entity (or its outermost placing entity) belongs to, for LTSCALE.</param>
internal sealed record ResolvedStyle(CadColor Color, LineWeightType LineWeight, LineType? LineType, double LineTypeScale, float Opacity, CadHeader? Header);
