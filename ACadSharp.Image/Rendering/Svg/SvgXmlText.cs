using System.Text;
using System.Xml;

namespace ACadSharp.Image.Rendering.Svg;

/// <summary>
/// Removes characters that XML 1.0 forbids from strings that come out of a drawing (text, layer names, block names,
/// font names). LINQ to XML escapes markup characters itself, but a control character such as U+0001 makes the
/// serialiser throw, and a drawing must never take the whole export down with it.
/// </summary>
internal static class SvgXmlText
{
    /// <summary>
    /// Returns <paramref name="value"/> with every character that is not a legal XML character removed. Tab, line feed and
    /// carriage return are legal and kept; valid surrogate pairs are kept; lone surrogates are dropped.
    /// </summary>
    /// <param name="value">The string to clean.</param>
    /// <returns>The same instance when nothing had to be removed, otherwise a cleaned copy.</returns>
    public static string Clean(string value)
    {
        int first = FirstInvalidIndex(value);
        if (first < 0)
        {
            return value;
        }

        StringBuilder builder = new(value.Length);
        builder.Append(value, 0, first);
        for (int i = first; i < value.Length; i++)
        {
            char c = value[i];
            if (XmlConvert.IsXmlChar(c))
            {
                builder.Append(c);
            }
            else if (i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], c))
            {
                builder.Append(c).Append(value[i + 1]);
                i++;
            }
        }

        return builder.ToString();
    }

    private static int FirstInvalidIndex(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (XmlConvert.IsXmlChar(c))
            {
                continue;
            }

            if (i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], c))
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }
}
