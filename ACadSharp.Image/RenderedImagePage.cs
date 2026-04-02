using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace ACadSharp.Image;

public sealed class RenderedImagePage : IDisposable
{
    public string Name { get; }

    public SixLabors.ImageSharp.Image<Rgba32> Canvas { get; }

    public RenderedImagePage(string name, SixLabors.ImageSharp.Image<Rgba32> canvas)
    {
        this.Name = name;
        this.Canvas = canvas;
    }

    public void Dispose()
    {
        this.Canvas.Dispose();
    }
}
