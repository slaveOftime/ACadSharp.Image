namespace ACadSharp.Image;

public static class ImageExportFormatExtensions
{
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
