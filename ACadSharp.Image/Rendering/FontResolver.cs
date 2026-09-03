using SixLabors.Fonts;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Resolves the configured font family to an installed one. When the configured family is missing, the fallback chain
/// mirrors the SVG font stack (<c>Arial, Helvetica, sans-serif</c>): metric-compatible Liberation Sans first, then the
/// common Linux and Windows sans faces, and only then the first installed family.
/// </summary>
internal static class FontResolver
{
    /// <summary>Families tried, in order, when the configured one is not installed.</summary>
    public static readonly string[] Fallbacks = ["Liberation Sans", "DejaVu Sans", "Arial", "Helvetica", "Noto Sans", "Segoe UI"];

    /// <summary>
    /// Finds the installed family for a configured name.
    /// </summary>
    /// <param name="familyName">The configured family, or null/blank for the fallback chain.</param>
    /// <returns>The configured family when installed, otherwise the first installed fallback, otherwise the first installed family.</returns>
    public static FontFamily Resolve(string? familyName)
    {
        if (!string.IsNullOrWhiteSpace(familyName) && SystemFonts.TryGet(familyName, out FontFamily configured))
        {
            return configured;
        }

        foreach (string fallback in Fallbacks)
        {
            if (SystemFonts.TryGet(fallback, out FontFamily family))
            {
                return family;
            }
        }

        return SystemFonts.Families.First();
    }

    /// <summary>
    /// Creates a font of the given size from the resolved family.
    /// </summary>
    /// <param name="familyName">The configured family.</param>
    /// <param name="size">Font size in points.</param>
    /// <returns>The font.</returns>
    public static Font Create(string? familyName, float size)
    {
        return Resolve(familyName).CreateFont(Math.Max(1f, size));
    }
}
