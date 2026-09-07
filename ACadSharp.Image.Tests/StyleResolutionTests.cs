using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class StyleResolutionTests
{
    [Fact]
    public void OpacityMapping()
    {
        Assert.Equal(1f, ImageStyleResolver.ResolveOpacity(new Line(), 1f)); // ByLayer default -> opaque (Layer has no transparency in ACadSharp 3.7.1)
        Assert.Equal(0.3f, ImageStyleResolver.ResolveOpacity(new Line { Transparency = new Transparency(70) }, 1f), 3);
        Assert.Equal(0.5f, ImageStyleResolver.ResolveOpacity(new Line { Transparency = Transparency.ByBlock }, 0.5f));
        Assert.Equal(1f, ImageStyleResolver.ResolveOpacity(new Line { Transparency = Transparency.ByBlock }, 1f));
    }

    [Fact]
    public void NestedEntitiesInheritByBlockOpacity()
    {
        ImageConfiguration configuration = new();
        RecordingDrawingSurface surface = new();
        Layout layout = new("t") { PaperWidth = 10, PaperHeight = 10 };
        ImageRenderContext context = new(surface, configuration, layout, 10, 10, 0, 0, 1d, 0, 0, singlePrecision: false, lineTypeScale: 1d);
        EntityRenderDispatcher dispatcher = new(configuration);
        BlockRecord block = new("B");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)) { Transparency = Transparency.ByBlock });
        Insert insert = new(block) { Transparency = new Transparency(50) };

        dispatcher.Draw(context, insert);

        Assert.Equal(0.5f, Assert.Single(surface.Styles).Opacity, 3);
    }
}
