using System.Text;

namespace ACadSharp.Image.Rendering.Svg;

/// <summary>
/// Builds HTML-safe ids: <c>{prefix}{kind}-{name}</c> with the name lower-cased and every run of characters outside
/// <c>[a-z0-9_-]</c> collapsed into a single dash.
/// </summary>
internal static class SvgIdSanitizer
{
    /// <summary>
    /// Restricts a caller-supplied id prefix to <c>[A-Za-z0-9_-]</c>, keeping its case and collapsing every run of other
    /// characters into a single dash. An id containing a space or a quote would break the <c>url(#id)</c> references
    /// that clip paths rely on.
    /// </summary>
    /// <param name="prefix">The configured <see cref="SvgOptions.IdPrefix"/>.</param>
    /// <returns>The prefix with only id-safe characters.</returns>
    public static string SanitizePrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return string.Empty;
        }

        StringBuilder builder = new(prefix.Length);
        bool pendingDash = false;
        foreach (char c in prefix)
        {
            bool safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (safe)
            {
                if (pendingDash && builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }

                pendingDash = false;
                builder.Append(c);
            }
            else
            {
                pendingDash = true;
            }
        }

        if (pendingDash && builder.Length > 0 && builder[^1] != '-')
        {
            builder.Append('-');
        }

        return builder.ToString();
    }

    public static string Sanitize(string prefix, string kind, string name)
    {
        StringBuilder builder = new(prefix.Length + kind.Length + name.Length + 1);
        builder.Append(prefix).Append(kind).Append('-');

        bool pendingDash = false;
        foreach (char c in name.ToLowerInvariant())
        {
            bool safe = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (safe)
            {
                if (pendingDash && builder[^1] != '-')
                {
                    builder.Append('-');
                }

                pendingDash = false;
                builder.Append(c);
            }
            else
            {
                pendingDash = true;
            }
        }

        return builder.ToString();
    }
}
