using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class LayerFilteringTests
{
    private static (RecordingDrawingSurface Surface, EntityRenderDispatcher Dispatcher, ImageRenderContext Context) Setup(Action<ImageConfiguration>? configure = null, Viewport? viewport = null)
    {
        ImageConfiguration configuration = new();
        configure?.Invoke(configuration);
        RecordingDrawingSurface surface = new();
        Layout layout = new("test") { PaperWidth = 100, PaperHeight = 100 };
        ImageRenderContext context = new(surface, configuration, layout, 100, 100, 0, 0, 1d, 0, 0, singlePrecision: false, lineTypeScale: 1d, viewport: viewport);
        return (surface, new EntityRenderDispatcher(configuration), context);
    }

    private static Line LineOn(Layer layer) => new(new XYZ(0, 0, 0), new XYZ(10, 0, 0)) { Layer = layer };

    private static int Drawn(RecordingDrawingSurface surface) => surface.Calls.Count(c => c.StartsWith("DrawLine", StringComparison.Ordinal));

    [Fact]
    public void AllModeDrawsOffAndFrozenLayers()
    {
        (RecordingDrawingSurface surface, EntityRenderDispatcher dispatcher, ImageRenderContext context) = Setup();

        dispatcher.Draw(context, LineOn(new Layer("Off") { IsOn = false }));
        dispatcher.Draw(context, LineOn(new Layer("Frozen") { Flags = LayerFlags.Frozen }));
        dispatcher.Draw(context, LineOn(new Layer("NoPlot") { PlotFlag = false }));

        Assert.Equal(3, Drawn(surface));
    }

    [Fact]
    public void ScreenModeHidesOffFrozenAndInvisibleButNotNonPlottable()
    {
        (RecordingDrawingSurface surface, EntityRenderDispatcher dispatcher, ImageRenderContext context) = Setup(c => c.LayerVisibility = LayerVisibilityMode.Screen);

        dispatcher.Draw(context, LineOn(new Layer("Off") { IsOn = false }));
        dispatcher.Draw(context, LineOn(new Layer("Frozen") { Flags = LayerFlags.Frozen }));
        Line invisible = LineOn(new Layer("Vis"));
        invisible.IsInvisible = true;
        dispatcher.Draw(context, invisible);
        dispatcher.Draw(context, LineOn(new Layer("NoPlot") { PlotFlag = false }));

        Assert.Equal(1, Drawn(surface));
        Assert.Equal("NoPlot", Assert.Single(surface.Entities).LayerName);
    }

    [Fact]
    public void PlotModeAlsoHidesNonPlottable()
    {
        (RecordingDrawingSurface surface, EntityRenderDispatcher dispatcher, ImageRenderContext context) = Setup(c => c.LayerVisibility = LayerVisibilityMode.Plot);

        dispatcher.Draw(context, LineOn(new Layer("NoPlot") { PlotFlag = false }));
        dispatcher.Draw(context, LineOn(new Layer("Plot")));

        Assert.Equal(1, Drawn(surface));
    }

    [Fact]
    public void ViewportFrozenLayersHideOnlyInsideThatViewport()
    {
        Layer frozenHere = new("Site");
        Viewport viewport = new();
        viewport.FrozenLayers.Add(frozenHere);
        (RecordingDrawingSurface inside, EntityRenderDispatcher dispatcher, ImageRenderContext viewportContext) = Setup(c => c.LayerVisibility = LayerVisibilityMode.Screen, viewport);
        (RecordingDrawingSurface outside, EntityRenderDispatcher dispatcher2, ImageRenderContext pageContext) = Setup(c => c.LayerVisibility = LayerVisibilityMode.Screen);

        dispatcher.Draw(viewportContext, LineOn(new Layer("Site")));
        dispatcher2.Draw(pageContext, LineOn(new Layer("Site")));

        Assert.Equal(0, Drawn(inside));
        Assert.Equal(1, Drawn(outside));
    }

    [Fact]
    public void IncludeListRestrictsThenHideListRemoves()
    {
        (RecordingDrawingSurface surface, EntityRenderDispatcher dispatcher, ImageRenderContext context) = Setup(c =>
        {
            c.IncludeLayers(["A", "B"]);
            c.HideLayer("b");
        });

        dispatcher.Draw(context, LineOn(new Layer("A")));
        dispatcher.Draw(context, LineOn(new Layer("B")));
        dispatcher.Draw(context, LineOn(new Layer("C")));

        Assert.Equal(1, Drawn(surface));
        Assert.Equal("A", Assert.Single(surface.Entities).LayerName);
    }

    [Fact]
    public void IncludedLayerStillObeysVisibilityMode()
    {
        (RecordingDrawingSurface surface, EntityRenderDispatcher dispatcher, ImageRenderContext context) = Setup(c =>
        {
            c.IncludeLayer("A");
            c.LayerVisibility = LayerVisibilityMode.Screen;
        });

        dispatcher.Draw(context, LineOn(new Layer("A") { IsOn = false }));

        Assert.Equal(0, Drawn(surface));
    }

    [Fact]
    public void NestedEntitiesAreFilteredByTheirOwnLayerWithLayerZeroInheritance()
    {
        (RecordingDrawingSurface surface, EntityRenderDispatcher dispatcher, ImageRenderContext context) = Setup(c => c.HideLayer("Hardware"));
        BlockRecord block = new("DOOR");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)) { Layer = new Layer(Layer.DefaultName) });
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)) { Layer = new Layer("Hardware") });
        Insert insert = new(block) { Layer = new Layer("Doors") };

        dispatcher.Draw(context, insert);

        Assert.Equal(1, Drawn(surface));
        Assert.Equal("Doors", surface.Entities.Last().LayerName);
    }

    [Fact]
    public void HidingTheInsertLayerHidesTheWholeBlock()
    {
        (RecordingDrawingSurface surface, EntityRenderDispatcher dispatcher, ImageRenderContext context) = Setup(c => c.HideLayer("Doors"));
        BlockRecord block = new("DOOR");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)) { Layer = new Layer("Hardware") });
        Insert insert = new(block) { Layer = new Layer("Doors") };

        dispatcher.Draw(context, insert);

        Assert.Equal(0, Drawn(surface));
        Assert.Empty(surface.Entities);
    }

    [Fact]
    public void LayerZeroSubEntitiesFollowTheInsertLayerState()
    {
        (RecordingDrawingSurface visibleSurface, EntityRenderDispatcher dispatcher, ImageRenderContext context) = Setup(c => c.LayerVisibility = LayerVisibilityMode.Screen);
        BlockRecord block = new("SYM");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)) { Layer = new Layer(Layer.DefaultName) });

        // Visible insert layer: the layer-0 sub-entity is drawn.
        dispatcher.Draw(context, new Insert(block) { Layer = new Layer("Symbols") });
        Assert.Equal(1, Drawn(visibleSurface));

        // Frozen insert layer: the same sub-entity inherits the frozen layer and is hidden.
        (RecordingDrawingSurface frozenSurface, EntityRenderDispatcher dispatcher2, ImageRenderContext context2) = Setup(c => c.LayerVisibility = LayerVisibilityMode.Screen);
        dispatcher2.Draw(context2, new Insert(block) { Layer = new Layer("Symbols") { Flags = LayerFlags.Frozen } });
        Assert.Equal(0, Drawn(frozenSurface));
    }
}
