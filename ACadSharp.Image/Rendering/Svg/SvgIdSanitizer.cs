using System.Text;

namespace ACadSharp.Image.Rendering.Svg;

/// <summary>
/// Builds HTML-safe ids: <c>{prefix}{kind}-{name}</c> with the name lower-cased and every run of characters outside
/// <c>[a-z0-9_-]</c> collapsed into a single dash.
/// </summary>
internal static class SvgIdSanitizer
{
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
