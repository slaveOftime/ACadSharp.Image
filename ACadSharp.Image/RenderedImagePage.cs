using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace ACadSharp.Image;

/// <summary>
/// A page rendered to a raster canvas.
/// </summary>
/// <remarks>
/// Owns the underlying <see cref="SixLabors.ImageSharp.Image{Rgba32}"/>; dispose the page to release it.
/// </remarks>
public sealed class RenderedImagePage : RenderedPage
{
    private readonly int _quality;

    /// <summary>
    /// Initializes a new instance of the <see cref="RenderedImagePage"/> class.
    /// </summary>
    /// <param name="name">Page name.</param>
    /// <param name="canvas">Rendered canvas; ownership transfers to the page.</param>
    /// <param name="format">Raster format used by <see cref="RenderedPage.Save(Stream)"/>. Must not be <c>Svg</c>.</param>
    /// <param name="quality">Quality 1..100 for lossy formats.</param>
    public RenderedImagePage(string name, SixLabors.ImageSharp.Image<Rgba32> canvas, ImageExportFormat format = ImageExportFormat.Png, int quality = 90)
        : base(name, format)
    {
        this.Canvas = canvas;
        this._quality = quality;
    }

    /// <summary>
    /// Gets the rendered image canvas (32-bit RGBA).
    /// </summary>
    public SixLabors.ImageSharp.Image<Rgba32> Canvas { get; }

    /// <inheritdoc/>
    public override void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        switch (this.Format)
        {
            case ImageExportFormat.Bmp:
                this.Canvas.Save(stream, new BmpEncoder());
                break;
            case ImageExportFormat.Jpeg:
                this.Canvas.Save(stream, new JpegEncoder { Quality = this._quality });
                break;
            case ImageExportFormat.Gif:
                this.Canvas.Save(stream, new GifEncoder());
                break;
            case ImageExportFormat.Webp:
                this.Canvas.Save(stream, new WebpEncoder { Quality = this._quality });
                break;
            default:
                this.Canvas.Save(stream, new PngEncoder());
                break;
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        this.Canvas.Dispose();
    }
}
