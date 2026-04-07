using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace ACadSharp.Image;

/// <summary>
/// Represents a single rendered page (layout or model space view) as an image.
/// </summary>
/// <remarks>
/// <para>
/// Instances of this class are produced by <see cref="ImageExporter"/> and wrap
/// a <see cref="SixLabors.ImageSharp.Image{Rgba32}"/> canvas along with a descriptive name.
/// </para>
/// <para>
/// This class implements <see cref="IDisposable"/> and owns the underlying image buffer.
/// Callers must dispose of the instance when finished to release unmanaged resources.
/// </para>
/// </remarks>
public sealed class RenderedImagePage : IDisposable
{
    /// <summary>
    /// Gets the name of this page (e.g., layout name or "Model").
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the rendered image canvas.
    /// </summary>
    /// <remarks>
    /// The canvas is a 32-bit RGBA image. It should not be modified after
    /// the page has been rendered.
    /// </remarks>
    public SixLabors.ImageSharp.Image<Rgba32> Canvas { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RenderedImagePage"/> class.
    /// </summary>
    /// <param name="name">The name of the page.</param>
    /// <param name="canvas">The rendered image canvas.</param>
    public RenderedImagePage(string name, SixLabors.ImageSharp.Image<Rgba32> canvas)
    {
        this.Name = name;
        this.Canvas = canvas;
    }

    /// <summary>
    /// Releases the unmanaged resources used by the underlying image canvas.
    /// </summary>
    /// <remarks>
    /// After calling this method, the <see cref="Canvas"/> property should no longer
    /// be accessed. The method is safe to call multiple times.
    /// </remarks>
    public void Dispose()
    {
        this.Canvas.Dispose();
    }
}
