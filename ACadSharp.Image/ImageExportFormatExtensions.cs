namespace ACadSharp.Image;

/// <summary>
/// Extension methods for <see cref="ImageExportFormat"/>.
/// </summary>
/// <remarks>
/// Provides utilities for converting between <see cref="ImageExportFormat"/> values
/// and their corresponding file extension strings.
/// </remarks>
public static class ImageExportFormatExtensions
{
    /// <summary>
    /// Gets the file extension string for the specified image format.
    /// </summary>
    /// <param name="format">The image export format.</param>
    /// <returns>
    /// The file extension including the leading dot (e.g., <c>".png"</c>, <c>".jpg"</c>).
    /// </returns>
    /// <example>
    /// <code>
    /// string ext = ImageExportFormat.Png.GetFileExtension(); // ".png"
    /// </code>
    /// </example>
    public static string GetFileExtension(this ImageExportFormat format)
    {
        return format switch
        {
            ImageExportFormat.Bmp => ".bmp",
            ImageExportFormat.Jpeg => ".jpg",
            ImageExportFormat.Gif => ".gif",
            ImageExportFormat.Webp => ".webp",
            _ => ".png",
        };
    }

    /// <summary>
    /// Tries to parse a format name or file extension into an <see cref="ImageExportFormat"/>.
    /// </summary>
    /// <param name="value">
    /// The format string to parse (e.g., <c>"png"</c>, <c>"jpeg"</c>, <c>"jpg"</c>).
    /// Whitespace and case are ignored.
    /// </param>
    /// <param name="format">
    /// When this method returns, contains the parsed <see cref="ImageExportFormat"/>,
    /// or <see cref="ImageExportFormat.Png"/> (the default) if parsing failed.
    /// </param>
    /// <returns>
    /// <c>true</c> if the string was successfully parsed; otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Accepts both format names (<c>"jpeg"</c>) and common file extensions (<c>"jpg"</c>).
    /// Returns <c>false</c> for <c>null</c>, empty, or unrecognized values.
    /// </remarks>
    /// <example>
    /// <code>
    /// if (ImageExportFormatExtensions.TryParse("webp", out var format))
    /// {
    ///     // format == ImageExportFormat.Webp
    /// }
    /// </code>
    /// </example>
    public static bool TryParse(string? value, out ImageExportFormat format)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            format = default;
            return false;
        }

        return normalized(value) switch
        {
            "png" => success(ImageExportFormat.Png, out format),
            "bmp" => success(ImageExportFormat.Bmp, out format),
            "jpg" or "jpeg" => success(ImageExportFormat.Jpeg, out format),
            "gif" => success(ImageExportFormat.Gif, out format),
            "webp" => success(ImageExportFormat.Webp, out format),
            _ => failure(out format),
        };
    }

    /// <summary>
    /// Tries to parse a file extension (with or without the leading dot) into an <see cref="ImageExportFormat"/>.
    /// </summary>
    /// <param name="extension">
    /// The file extension to parse (e.g., <c>".png"</c>, <c>"png"</c>, <c>".JPG"</c>).
    /// Whitespace and case are ignored. The leading dot is optional.
    /// </param>
    /// <param name="format">
    /// When this method returns, contains the parsed <see cref="ImageExportFormat"/>,
    /// or <see cref="ImageExportFormat.Png"/> (the default) if parsing failed.
    /// </param>
    /// <returns>
    /// <c>true</c> if the extension was successfully parsed; otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This method is a convenience wrapper around <see cref="TryParse"/> that
    /// automatically strips a leading dot from the extension string.
    /// </remarks>
    /// <example>
    /// <code>
    /// if (ImageExportFormatExtensions.TryParseFileExtension(".webp", out var format))
    /// {
    ///     // format == ImageExportFormat.Webp
    /// }
    /// </code>
    /// </example>
    public static bool TryParseFileExtension(string? extension, out ImageExportFormat format)
    {
        return TryParse(normalized(extension).TrimStart('.'), out format);
    }

    private static string normalized(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static bool success(ImageExportFormat value, out ImageExportFormat format)
    {
        format = value;
        return true;
    }

    private static bool failure(out ImageExportFormat format)
    {
        format = default;
        return false;
    }
}
