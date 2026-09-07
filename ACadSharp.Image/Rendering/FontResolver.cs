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
    public static readonly IReadOnlyList<string> Fallbacks =
        Array.AsReadOnly(new[] { "Liberation Sans", "DejaVu Sans", "Arial", "Helvetica", "Noto Sans", "Segoe UI" });

    /// <summary>
    /// Tries to find the installed family for a configured name.
    /// </summary>
    /// <param name="familyName">The configured family, or null/blank for the fallback chain.</param>
    /// <param name="family">
    /// The configured family when installed, otherwise the first installed fallback, otherwise the first installed
    /// family; the default value when no font family is installed at all.
    /// </param>
    /// <returns><c>false</c> when the machine has no installed font family, so nothing can be resolved.</returns>
    public static bool TryResolve(string? familyName, out FontFamily family)
    {
        if (!string.IsNullOrWhiteSpace(familyName) && SystemFonts.TryGet(familyName, out family))
        {
            return true;
        }

        foreach (string fallback in Fallbacks)
        {
            if (SystemFonts.TryGet(fallback, out family))
            {
                return true;
            }
        }

        foreach (FontFamily installed in SystemFonts.Families)
        {
            family = installed;
            return true;
        }

        family = default;
        return false;
    }

    /// <summary>
    /// Finds the installed family for a configured name.
    /// </summary>
    /// <param name="familyName">The configured family, or null/blank for the fallback chain.</param>
    /// <returns>The configured family when installed, otherwise the first installed fallback, otherwise the first installed family.</returns>
    /// <exception cref="InvalidOperationException">No font family is installed.</exception>
    public static FontFamily Resolve(string? familyName)
    {
        if (!TryResolve(familyName, out FontFamily family))
        {
            throw new InvalidOperationException("No font families are installed; text cannot be rendered.");
        }

        return family;
    }

    /// <summary>
    /// Creates a font of the given size from the resolved family.
    /// </summary>
    /// <param name="familyName">The configured family.</param>
    /// <param name="size">Font size in points; clamped to at least 1, which the raster backend needs to rasterise.</param>
    /// <returns>The font.</returns>
    /// <remarks>
    /// Because of the clamp the returned font's size is not the requested one below 1 point, so callers that measure in
    /// drawing units must not rely on it: measure at a fixed reference size and scale the result instead, the way
    /// <c>SvgTextLayout.Wrap</c> does.
    /// </remarks>
    public static Font Create(string? familyName, float size)
    {
        return Resolve(familyName).CreateFont(Math.Max(1f, size));
    }
}
