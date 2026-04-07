namespace ACadSharp.Image;

/// <summary>
/// Supported image formats for DWG/DXF export.
/// </summary>
public enum ImageExportFormat
{
    /// <summary>
    /// Portable Network Graphics format. Supports lossless compression and transparency.
    /// </summary>
    Png,

    /// <summary>
    /// Bitmap format. Uncompressed, widely compatible but produces large files.
    /// </summary>
    Bmp,

    /// <summary>
    /// JPEG format. Supports lossy compression with configurable quality.
    /// Does not support transparency.
    /// </summary>
    Jpeg,

    /// <summary>
    /// Graphics Interchange Format. Supports animation and transparency,
    /// but limited to 256 colors.
    /// </summary>
    Gif,

    /// <summary>
    /// WebP format. Modern format supporting lossy and lossless compression,
    /// transparency, and excellent compression ratios.
    /// </summary>
    Webp
}
