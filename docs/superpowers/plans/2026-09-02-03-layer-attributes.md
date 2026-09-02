# Layer Attributes and Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Honour layer state (off, frozen, plot, viewport-frozen, invisible) behind an opt-in mode, add an include list, move all layer filtering into the render loop, render linetypes, transparency, ACI 7 by background, and hatches in both backends, and expose everything in the CLI and README.

**Architecture:** `EntityVisibilityFilter` decides per drawn entity (with its effective layer and current viewport) and runs inside `EntityRenderDispatcher.Draw` before `BeginEntity`. `ImageStyleResolver` grows opacity and dash resolution (`LineTypeDashResolver`). Hatch decomposition uses ACadSharp's `ExplodePattern()`. Both backends already consume `ImageStyle.DashPattern` and `Opacity` (plans 1 and 2), so this plan mostly feeds them real values.

**Tech Stack:** .NET 8/10, ACadSharp 3.7.1, ImageSharp.Drawing `PatternPen`, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-02-layers-and-svg-design.md` (sections 4, 6, 7). Requires plans 01 and 02 to be complete.

## Global Constraints

- Same as plans 1 and 2 (no new packages, style, worktree root, commit trailers):

```
Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz
```

- Default configuration output must not change: `LayerVisibility` defaults to `All`, `ForegroundColor` null resolves to black on the default white background, entities default to ByLayer transparency (opaque), and the sample files' `Continuous` linetypes stay solid. **If a parity baseline changes because a sample actually uses a dashed linetype or a non-opaque entity, that is expected**: regenerate that baseline once with `ACADSHARP_IMAGE_UPDATE_BASELINES=1`, inspect the PNG visually, and mention it in the commit message. Do the same for the SVG goldens.
- Recorded deviation (spec 4.3): ByLayer transparency resolves to opaque because ACadSharp 3.7.1's `Layer` has no transparency.

## File Structure

| File | Responsibility | Task |
| --- | --- | --- |
| `ACadSharp.Image/LayerVisibilityMode.cs` (create), `ImageConfiguration.cs` (modify) | Public options | 1 |
| `ACadSharp.Image.Tests/ImageConfigurationTests.cs` (modify) | | 1 |
| `ACadSharp.Image/Rendering/EntityVisibilityFilter.cs` (create), `EntityRenderDispatcher.cs`, `ImageExporter.cs` (modify) | Render-loop filtering, effective layer object | 2 |
| `ACadSharp.Image.Tests/RecordingDrawingSurface.cs`, `EntityRenderDispatcherTests.cs`, `ImageExporterTests.cs`, `LayerFilteringTests.cs` (modify/create) | | 2 |
| `ACadSharp.Image/Extensions/ColorExtensions.cs`, `ImageConfiguration.cs`, `Rendering/ImageStyleResolver.cs`, `EntityRenderDispatcher.cs` (modify) | ACI 7 and `ForegroundColor` | 3 |
| `ACadSharp.Image/Rendering/ImageStyleResolver.cs`, `EntityRenderDispatcher.cs` (modify) | Transparency to opacity | 4 |
| `ACadSharp.Image/Rendering/LineTypeDashResolver.cs` (create), `ImageStyleResolver.cs`, `ImagePageRenderer.cs`, `ImageRenderContext.cs` (modify) | Linetypes | 5 |
| `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (modify) | Hatch | 6 |
| `ACadSharp.Image.Cli/Program.cs`, `CliOptions.cs` (modify) | `--layer-visibility`, `--only-layer`, `--list-layers` | 7 |
| `README.md` (modify) | Docs and migration notes | 8 |

---

### Task 1: Configuration surface

**Files:**
- Create: `ACadSharp.Image/LayerVisibilityMode.cs`
- Modify: `ACadSharp.Image/ImageConfiguration.cs`
- Test: `ACadSharp.Image.Tests/ImageConfigurationTests.cs`

**Interfaces:**
- Produces: `public enum LayerVisibilityMode { All, Screen, Plot }`; on `ImageConfiguration`: `LayerVisibilityMode LayerVisibility { get; set; } = All`, `IReadOnlySet<string> IncludedLayers`, `void IncludeLayer(string)`, `void IncludeLayers(IEnumerable<string>)`, `bool ExcludeLayer(string)`, `void ClearIncludedLayers()`, `SixLabors.ImageSharp.Color? ForegroundColor { get; set; }`, `float MinimumDashPixels { get; set; } = 2f` (>= 0), `int MaxHatchLines { get; set; } = 20000` (> 0).

- [ ] **Step 1: Failing tests**

Append to `ImageConfigurationTests`:

```csharp
    [Fact]
    public void LayerVisibilityDefaultsToAll()
    {
        Assert.Equal(LayerVisibilityMode.All, new ImageConfiguration().LayerVisibility);
    }

    [Fact]
    public void IncludedLayersAreManagedThroughMethods()
    {
        ImageConfiguration configuration = new();

        configuration.IncludeLayer("Walls");
        configuration.IncludeLayers(["doors", "Windows"]);

        Assert.Equal(3, configuration.IncludedLayers.Count);
        Assert.Contains("WALLS", configuration.IncludedLayers);
        Assert.True(configuration.ExcludeLayer("DOORS"));
        Assert.False(configuration.ExcludeLayer("nope"));
        Assert.Throws<ArgumentException>(() => configuration.IncludeLayer(" "));

        configuration.ClearIncludedLayers();

        Assert.Empty(configuration.IncludedLayers);
    }

    [Fact]
    public void NewNumericSettingsAreValidated()
    {
        ImageConfiguration configuration = new();

        Assert.Null(configuration.ForegroundColor);
        Assert.Equal(2f, configuration.MinimumDashPixels);
        Assert.Equal(20000, configuration.MaxHatchLines);
        Assert.Throws<ArgumentOutOfRangeException>(() => configuration.MinimumDashPixels = -1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => configuration.MaxHatchLines = 0);
    }
```

- [ ] **Step 2: Run, expect compile failure**

- [ ] **Step 3: Implement**

Create `ACadSharp.Image/LayerVisibilityMode.cs`:

```csharp
namespace ACadSharp.Image;

/// <summary>
/// Controls how layer and entity state in the drawing affects what is rendered.
/// </summary>
public enum LayerVisibilityMode
{
    /// <summary>Render everything regardless of layer state. This is the default and matches earlier versions.</summary>
    All,

    /// <summary>Hide entities on layers that are off or frozen, entities flagged invisible, and layers frozen in the current viewport.</summary>
    Screen,

    /// <summary><see cref="Screen"/> plus hide entities on non-plottable layers.</summary>
    Plot,
}
```

In `ImageConfiguration`:
- Fields: `private readonly HashSet<string> _includedLayers = new(StringComparer.OrdinalIgnoreCase);`, `private readonly IReadOnlySet<string> _readOnlyIncludedLayers;` (initialise in the constructor with `new ReadOnlySet<string>(this._includedLayers)`), `private float _minimumDashPixels = 2f;`, `private int _maxHatchLines = 20000;`.
- Properties (with XML docs in the style of the existing ones):

```csharp
    public LayerVisibilityMode LayerVisibility { get; set; } = LayerVisibilityMode.All;

    public IReadOnlySet<string> IncludedLayers => this._readOnlyIncludedLayers;

    public ImageColor? ForegroundColor { get; set; }

    public float MinimumDashPixels
    {
        get => this._minimumDashPixels;
        set => this._minimumDashPixels = value >= 0f ? value : throw new ArgumentOutOfRangeException(nameof(value), "Minimum dash length must be zero or greater.");
    }

    public int MaxHatchLines
    {
        get => this._maxHatchLines;
        set => this._maxHatchLines = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Maximum hatch lines must be greater than zero.");
    }
```

Doc text: `LayerVisibility` "Gets or sets how layer state (on/off, frozen, plottable) affects rendering. Default <see cref="LayerVisibilityMode.All"/>." `IncludedLayers` "Gets the layers to render when the set is not empty; all other layers are skipped. Applied before <see cref="HiddenLayers"/>. Case-insensitive." `ForegroundColor` "Gets or sets the colour used for AutoCAD colour index 7 (\"white/black by background\"). Null (default) picks black or white from the luminance of <see cref="BackgroundColor"/>." `MinimumDashPixels` "Gets or sets the pattern length in pixels below which dashed linetypes are drawn solid. Default 2." `MaxHatchLines` "Gets or sets the maximum number of pattern lines drawn per hatch; beyond it a warning is raised and the remainder is skipped. Default 20000."

- Methods next to the hidden-layer ones:

```csharp
    public void IncludeLayer(string layerName)
    {
        ThrowIfNullOrWhiteSpace(layerName);
        this._includedLayers.Add(layerName);
    }

    public void IncludeLayers(IEnumerable<string> layerNames)
    {
        ArgumentNullException.ThrowIfNull(layerNames);
        foreach (string layerName in layerNames)
        {
            this.IncludeLayer(layerName);
        }
    }

    public bool ExcludeLayer(string layerName)
    {
        ThrowIfNullOrWhiteSpace(layerName);
        return this._includedLayers.Remove(layerName);
    }

    public void ClearIncludedLayers()
    {
        this._includedLayers.Clear();
    }
```

- [ ] **Step 4: Run tests, commit**

```bash
dotnet test ACadSharp.Image.sln -c Release --nologo -v q
git add ACadSharp.Image/LayerVisibilityMode.cs ACadSharp.Image/ImageConfiguration.cs ACadSharp.Image.Tests/ImageConfigurationTests.cs
git commit -m "Add layer visibility, include list and related configuration

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

---

### Task 2: Render-loop filtering

**Files:**
- Create: `ACadSharp.Image/Rendering/EntityVisibilityFilter.cs`
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs`
- Modify: `ACadSharp.Image/ImageExporter.cs` (remove layer filtering from `Add`)
- Modify: `ACadSharp.Image.Tests/RecordingDrawingSurface.cs` (record styles), `EntityRenderDispatcherTests.cs` (effective layer test), `ImageExporterTests.cs` (rewrite the three hidden-layer tests)
- Create: `ACadSharp.Image.Tests/LayerFilteringTests.cs`

**Interfaces:**
- Produces: `internal sealed class EntityVisibilityFilter(ImageConfiguration configuration) { bool IsVisible(Entity entity, Layer? effectiveLayer, string effectiveLayerName, Viewport? viewport); }`; dispatcher's `internal static Layer? GetEffectiveLayer(Entity entity, Layer? parentLayer)` replacing `GetEffectiveLayerName(Entity, string?)` (name is `effectiveLayer?.Name ?? Layer.DefaultName`); `RecordingDrawingSurface.Styles : List<ImageStyle>`.

Rules (spec 4.1, 4.2), evaluated in order, first match hides:
1. `IncludedLayers.Count > 0 && !IncludedLayers.Contains(name)`
2. `HiddenLayers.Contains(name)`
3. mode `All` → visible. Otherwise: `layer != null && !layer.IsOn`; `layer != null && layer.Flags.HasFlag(LayerFlags.Frozen)`; `entity.IsInvisible`; `viewport != null && layer != null && viewport.FrozenLayers.Any(f => string.Equals(f.Name, name, OrdinalIgnoreCase))`.
4. mode `Plot`: `layer != null && !layer.PlotFlag`.

- [ ] **Step 1: Failing tests**

In `RecordingDrawingSurface` add `public List<ImageStyle> Styles { get; } = new();` and `this.Styles.Add(style);` as the first line of every `Draw*`/`Fill*` method.

Create `ACadSharp.Image.Tests/LayerFilteringTests.cs`:

```csharp
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
```

Rewrite the three hidden-layer tests in `ImageExporterTests` (`HiddenLayersFiltersOutEntitiesOnSpecifiedLayers`, `HiddenLayersIsCaseInsensitive`, `MultipleHiddenLayersCanBeConfigured`) to assert on rendered output instead of `page.Entities`:

```csharp
    private static int CountDrawnLines(ImageExporter exporter)
    {
        RecordingDrawingSurface surface = new();
        ImagePageRenderer renderer = new(exporter.Configuration);
        renderer.RenderTo(surface, exporter.Pages[0]);
        return surface.Calls.Count(c => c.StartsWith("DrawLine", StringComparison.Ordinal));
    }
```

and, for example, `HiddenLayersFiltersOutEntitiesOnSpecifiedLayers` ends with:

```csharp
        Assert.Equal(3, exporter.Pages[0].Entities.Count); // pages keep every entity; filtering happens at render time
        Assert.Equal(2, CountDrawnLines(exporter));
```

`HiddenLayersIsCaseInsensitive` asserts `Assert.Equal(0, CountDrawnLines(exporter));` and `MultipleHiddenLayersCanBeConfigured` asserts `Assert.Equal(1, CountDrawnLines(exporter));`. `ImagePageRenderer.RenderTo(IDrawingSurface, ImagePage)` must exist as `internal` (plan 2 Task 7 changed the private overload to take a context; keep a public-to-tests `internal void RenderTo(IDrawingSurface surface, ImagePage page)` that builds the raster-style page context with `CreatePageContext`). Add this test too:

```csharp
    [Fact]
    public void ChangingHiddenLayersAfterAddTakesEffect()
    {
        BlockRecord block = new("late-hide");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 1, 0)) { Layer = new Layer("Late") });
        ImageExporter exporter = new();
        exporter.Add(block);

        Assert.Equal(1, CountDrawnLines(exporter));
        exporter.Configuration.HideLayer("Late");
        Assert.Equal(0, CountDrawnLines(exporter));
    }
```

In `EntityRenderDispatcherTests.NestedEntityOnLayerZeroInheritsInsertLayer` nothing changes (it asserts on names). Add:

```csharp
    [Fact]
    public void EffectiveLayerReturnsParentLayerObjectForLayerZero()
    {
        Layer parent = new("Doors") { IsOn = false };
        Line onZero = new() { Layer = new Layer(Layer.DefaultName) };
        Line onOwn = new() { Layer = new Layer("Own") };

        Assert.Same(parent, EntityRenderDispatcher.GetEffectiveLayer(onZero, parent));
        Assert.Equal("Own", EntityRenderDispatcher.GetEffectiveLayer(onOwn, parent)!.Name);
        Assert.Equal(Layer.DefaultName, EntityRenderDispatcher.GetEffectiveLayer(onZero, null)!.Name);
    }
```

- [ ] **Step 2: Run, expect failures/compile errors**

- [ ] **Step 3: Implement the filter**

Create `ACadSharp.Image/Rendering/EntityVisibilityFilter.cs`:

```csharp
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Decides whether an entity is drawn, combining the include list, the hide list and <see cref="ImageConfiguration.LayerVisibility"/>.
/// </summary>
internal sealed class EntityVisibilityFilter
{
    private readonly ImageConfiguration _configuration;

    public EntityVisibilityFilter(ImageConfiguration configuration)
    {
        this._configuration = configuration;
    }

    public bool IsVisible(Entity entity, Layer? effectiveLayer, string effectiveLayerName, Viewport? viewport)
    {
        if (this._configuration.IncludedLayers.Count > 0 && !this._configuration.IncludedLayers.Contains(effectiveLayerName))
        {
            return false;
        }

        if (this._configuration.HiddenLayers.Count > 0 && this._configuration.HiddenLayers.Contains(effectiveLayerName))
        {
            return false;
        }

        LayerVisibilityMode mode = this._configuration.LayerVisibility;
        if (mode == LayerVisibilityMode.All)
        {
            return true;
        }

        if (entity.IsInvisible)
        {
            return false;
        }

        if (effectiveLayer != null)
        {
            if (!effectiveLayer.IsOn || effectiveLayer.Flags.HasFlag(LayerFlags.Frozen))
            {
                return false;
            }

            if (viewport != null && viewport.FrozenLayers.Any(frozen => string.Equals(frozen.Name, effectiveLayerName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (mode == LayerVisibilityMode.Plot && !effectiveLayer.PlotFlag)
            {
                return false;
            }
        }

        return true;
    }
}
```

- [ ] **Step 4: Wire the dispatcher**

In `EntityRenderDispatcher`:
- Add field `private readonly EntityVisibilityFilter _visibilityFilter;` initialised in the constructor.
- Replace `GetEffectiveLayerName(Entity, string?)` with:

```csharp
    /// <summary>
    /// Entities on layer "0" inside a block take the layer of the insert that placed them.
    /// </summary>
    internal static Layer? GetEffectiveLayer(Entity entity, Layer? parentLayer)
    {
        Layer? own = entity.Layer;
        if (own == null || string.IsNullOrEmpty(own.Name))
        {
            return parentLayer ?? own ?? Layer.Default;
        }

        if (parentLayer != null && string.Equals(own.Name, Layer.DefaultName, StringComparison.Ordinal))
        {
            return parentLayer;
        }

        return own;
    }
```

- Change the private recursive `Draw` signature to `Draw(ImageRenderContext context, Entity entity, Layer? parentLayer, ulong? parentHandle, string? blockName)` and its start to:

```csharp
        Layer? layer = GetEffectiveLayer(entity, parentLayer);
        string layerName = layer?.Name ?? Layer.DefaultName;
        if (!this._visibilityFilter.IsVisible(entity, layer, layerName, context.Viewport))
        {
            return;
        }

        ImageStyle style = this._styleResolver.Resolve(entity, context);
        EntityRenderInfo info = new(layerName, entity.ObjectName, entity.Handle, parentHandle, blockName);
        LayerRenderInfo layerInfo = CreateLayerInfo(layer, layerName, context);
```

- `DrawDimension` and `DrawBlockContents` take `Layer? layer` instead of `string layerName` and pass it down. The public `Draw(context, entity)` passes `parentLayer: null`.

If `Layer.Default` is not a static property in 3.7.1 (plan research shows `Layer.Default` and `Layer.DefaultName` exist), fall back to `new Layer(Layer.DefaultName)`.

- [ ] **Step 5: Remove add-time layer filtering from `ImageExporter`**

In `ImageExporter`: `ShouldIncludeEntity` becomes `entity is not Viewport` (delete `IsHiddenLayer`). Update its XML remarks on `Add(Layout)`/`Add(BlockRecord)`: "Layer filters and visibility settings are applied when rendering, so all entities are kept on the page."

- [ ] **Step 5b: Keep auto-sized pages framed on visible content**

Before this change, hidden entities were excluded before `ImagePage.UpdateLayoutSize` computed the block page's extents, so hiding a far-away layer tightened the frame. Hidden entities now stay on the page, so the frame must be recomputed from visible entities at render time.

Failing test (append to `ImageExporterTests`):

```csharp
    [Fact]
    public void HiddenEntitiesDoNotAffectAutoSizedFraming()
    {
        static ImageExporter Build(bool withFarHiddenLine)
        {
            BlockRecord block = new("framing");
            block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)) { Layer = new Layer("Visible") });
            if (withFarHiddenLine)
            {
                block.Entities.Add(new Line(new XYZ(1000, 1000, 0), new XYZ(1010, 1010, 0)) { Layer = new Layer("Far") });
            }

            ImageExporter exporter = new();
            exporter.Configuration.Width = 200;
            exporter.Configuration.Height = 200;
            exporter.Configuration.HideLayer("Far");
            exporter.Add(block);
            return exporter;
        }

        static string FirstLineCall(ImageExporter exporter)
        {
            RecordingDrawingSurface surface = new();
            new ImagePageRenderer(exporter.Configuration).RenderTo(surface, exporter.Pages[0]);
            return surface.Calls.Single(c => c.StartsWith("DrawLine", StringComparison.Ordinal));
        }

        Assert.Equal(FirstLineCall(Build(withFarHiddenLine: false)), FirstLineCall(Build(withFarHiddenLine: true)));
    }
```

Implementation:
- `ImagePage`: add `internal bool AutoSized { get; private set; }`, set to `true` at the end of `UpdateLayoutSize()` when it assigned the paper size. Add an overload `public void UpdateLayoutSize(Func<Entity, bool>? include)` that computes the bounding box only over entities for which `include` returns true (null includes all); the existing parameterless method calls it with `null`. When the filtered set is empty, leave `Translation` and the layout size unchanged.
- `ImagePageRenderer`: in both `RenderTo(IDrawingSurface, ImagePage)` (before `CreatePageContext`) and `RenderSvg` (before `ComputeSvgViewBox`), when `page.AutoSized` call

```csharp
            page.UpdateLayoutSize(entity =>
            {
                Layer? layer = EntityRenderDispatcher.GetEffectiveLayer(entity, null);
                return this._visibilityFilter.IsVisible(entity, layer, layer?.Name ?? Layer.DefaultName, null);
            });
```

with `private readonly EntityVisibilityFilter _visibilityFilter = new(configuration);` added to the renderer. Layout pages (`Add(Layout)`) keep their paper size and are never auto-sized.

Run the test: the two `DrawLine` strings must be identical. Parity holds because the samples render with no hidden layers, so the filtered bounding box equals the unfiltered one.

- [ ] **Step 6: Run everything**

Run: `dotnet test ACadSharp.Image.sln -c Release --nologo -v q`
Expected: PASS including parity (default `All` mode and empty lists draw exactly what add-time filtering drew, since the samples are rendered with no hidden layers).

- [ ] **Step 7: Commit**

```bash
git add ACadSharp.Image ACadSharp.Image.Tests
git commit -m "Apply layer visibility and selection in the render loop

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

---

### Task 3: ACI 7 by background and `ForegroundColor`

**Files:**
- Modify: `ACadSharp.Image/Extensions/ColorExtensions.cs`
- Modify: `ACadSharp.Image/ImageConfiguration.cs` (`ResolveForegroundColor`)
- Modify: `ACadSharp.Image/Rendering/ImageStyleResolver.cs`, `EntityRenderDispatcher.CreateLayerInfo`
- Test: `ACadSharp.Image.Tests/ColorResolutionTests.cs` (create)

**Interfaces:**
- Produces: `ColorExtensions.ToImageColor(this CadColor color, ImageColor foreground)`; `internal ImageColor ImageConfiguration.ResolveForegroundColor()`.

- [ ] **Step 1: Failing tests**

```csharp
using ACadSharp.Image.Extensions;
using SixLabors.ImageSharp;

namespace ACadSharp.Image.Tests;

public sealed class ColorResolutionTests
{
    [Fact]
    public void Index7IsBlackOnLightAndWhiteOnDarkBackgrounds()
    {
        ImageConfiguration light = new();
        ImageConfiguration dark = new() { BackgroundColor = Color.FromRgb(20, 20, 40) };
        ImageConfiguration transparent = new() { BackgroundColor = Color.Transparent };

        Assert.Equal(Color.Black, light.ResolveForegroundColor());
        Assert.Equal(Color.White, dark.ResolveForegroundColor());
        Assert.Equal(Color.Black, transparent.ResolveForegroundColor());
    }

    [Fact]
    public void ExplicitForegroundWins()
    {
        ImageConfiguration configuration = new() { BackgroundColor = Color.Black, ForegroundColor = Color.Yellow };

        Assert.Equal(Color.Yellow, configuration.ResolveForegroundColor());
        Assert.Equal(Color.Yellow, new ACadSharp.Color(7).ToImageColor(configuration.ResolveForegroundColor()));
        Assert.Equal(Color.FromRgb(255, 0, 0), new ACadSharp.Color(1).ToImageColor(configuration.ResolveForegroundColor()));
    }
}
```

- [ ] **Step 2: Run, expect compile failure**

- [ ] **Step 3: Implement**

`ColorExtensions`:

```csharp
    public static ImageColor ToImageColor(this CadColor color, ImageColor foreground)
    {
        if (color.Index == ByBackgroundIndex)
        {
            return foreground;
        }

        return ImageColor.FromRgb(color.R, color.G, color.B);
    }

    public static ImageColor ToImageColor(this CadColor color) => color.ToImageColor(ImageColor.Black);
```

`ImageConfiguration`:

```csharp
    /// <summary>
    /// Colour used for AutoCAD colour index 7: <see cref="ForegroundColor"/> when set, else black on light or transparent
    /// backgrounds and white on dark ones.
    /// </summary>
    internal ImageColor ResolveForegroundColor()
    {
        if (this.ForegroundColor is ImageColor explicitColor)
        {
            return explicitColor;
        }

        SixLabors.ImageSharp.PixelFormats.Rgba32 background = this.BackgroundColor.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgba32>();
        if (background.A == 0)
        {
            return ImageColor.Black;
        }

        double luminance = (0.299d * background.R) + (0.587d * background.G) + (0.114d * background.B);
        return luminance < 128d ? ImageColor.White : ImageColor.Black;
    }
```

`ImageStyleResolver.Resolve`: `entity.GetActiveColor().ToImageColor(context.Configuration.ResolveForegroundColor())`. `EntityRenderDispatcher.CreateLayerInfo`: `layer.Color.ToImageColor(context.Configuration.ResolveForegroundColor())`, and the null-layer branch uses `context.Configuration.ResolveForegroundColor()` instead of `Black`.

- [ ] **Step 4: Run everything (parity must hold: white background still gives black), commit**

```bash
git add ACadSharp.Image ACadSharp.Image.Tests/ColorResolutionTests.cs
git commit -m "Resolve colour index 7 from the background or ForegroundColor

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

---

### Task 4: Transparency to opacity

**Files:**
- Modify: `ACadSharp.Image/Rendering/ImageStyleResolver.cs`, `EntityRenderDispatcher.cs`
- Test: `ACadSharp.Image.Tests/LayerFilteringTests.cs` (append) or a new `StyleResolutionTests.cs`

**Interfaces:**
- Produces: `ImageStyleResolver.Resolve(Entity entity, ImageRenderContext context, float parentOpacity)`; `internal static float ImageStyleResolver.ResolveOpacity(Entity entity, float parentOpacity)`. Dispatcher threads `parentOpacity` through nested draws (top level 1).

- [ ] **Step 1: Failing tests**

Create `ACadSharp.Image.Tests/StyleResolutionTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run, expect failure**

- [ ] **Step 3: Implement**

`ImageStyleResolver`:

```csharp
    public ImageStyle Resolve(Entity entity, ImageRenderContext context, float parentOpacity)
    {
        return new ImageStyle(
            entity.GetActiveColor().ToImageColor(context.Configuration.ResolveForegroundColor()),
            context.ToStrokeWidth(entity.GetActiveLineWeightType()),
            null,
            ResolveOpacity(entity, parentOpacity));
    }

    /// <summary>
    /// Maps CAD transparency to opacity. ByLayer is opaque (ACadSharp 3.7.1 layers carry no transparency);
    /// ByBlock inherits the parent's opacity; explicit values 0..90 mean that percentage transparent.
    /// </summary>
    internal static float ResolveOpacity(Entity entity, float parentOpacity)
    {
        Transparency transparency = entity.Transparency;
        if (transparency.IsByLayer)
        {
            return 1f;
        }

        if (transparency.IsByBlock)
        {
            return parentOpacity;
        }

        return Math.Clamp(1f - (transparency.Value / 100f), 0f, 1f);
    }
```

Dispatcher: the private `Draw` gains `float parentOpacity`; the public entry passes `1f`; `DrawDimension`/`DrawBlockContents` receive the resolved `style.Opacity` and pass it down as `parentOpacity`.

- [ ] **Step 4: Run everything (parity: sample entities default to ByLayer, so opaque), commit**

```bash
git add ACadSharp.Image ACadSharp.Image.Tests/StyleResolutionTests.cs
git commit -m "Map entity transparency to opacity

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

---

### Task 5: Linetypes as dash patterns

**Files:**
- Create: `ACadSharp.Image/Rendering/LineTypeDashResolver.cs`
- Modify: `ACadSharp.Image/Rendering/ImageStyleResolver.cs` (fill `DashPattern`)
- Modify: `ACadSharp.Image/Rendering/ImageRenderContext.cs` (`CreateViewportContext` takes `lineTypeScale`), `ImagePageRenderer.DrawViewport` (PSLTSCALE)
- Test: `ACadSharp.Image.Tests/LineTypeDashResolverTests.cs` (create)

**Interfaces:**
- Produces: `internal static class LineTypeDashResolver { static float[]? Resolve(Entity entity, ImageRenderContext context, float strokeWidth); static float[]? BuildPattern(LineType lineType, double scale, float strokeWidth); static bool EnforcesMinimumDash(ImageRenderContext context); }`.

Rules (spec 4.4): `scale = ltscale * celtscale * context.LineTypeScale` with `ltscale = header.LineTypeScale > 0 ? header.LineTypeScale : 1`, `celtscale = entity.LineTypeScale > 0 ? entity.LineTypeScale : 1`. Segment kinds: dash (`Length > 0`), gap (`Length < 0`, or `IsShape`/`IsText`), dot (`Length == 0` → dash of `strokeWidth`). Adjacent same-kind entries are merged; the pattern starts with a dash (prepend a `0` dash when it starts with a gap); odd counts are doubled so the array is even. Minimum dash rule applies when `context.StrokeUnitsPerMillimeter == null` (pixel widths): total length `< MinimumDashPixels` → null (solid).

- [ ] **Step 1: Failing tests**

```csharp
using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Objects;
using ACadSharp.Tables;

namespace ACadSharp.Image.Tests;

public sealed class LineTypeDashResolverTests
{
    private static LineType Dashed(params double[] lengths)
    {
        LineType lineType = new("DASHED");
        foreach (double length in lengths)
        {
            lineType.AddSegment(new LineType.Segment { Length = length });
        }

        return lineType;
    }

    private static ImageRenderContext Context(double scale, double? unitsPerMillimeter = null, float minimumDash = 2f)
    {
        ImageConfiguration configuration = new() { MinimumDashPixels = minimumDash };
        Layout layout = new("t") { PaperWidth = 10, PaperHeight = 10 };
        return new ImageRenderContext(new RecordingDrawingSurface(), configuration, layout, 10, 10, 0, 0, scale, 0, 0, singlePrecision: false, lineTypeScale: scale, strokeUnitsPerMillimeter: unitsPerMillimeter);
    }

    [Fact]
    public void ContinuousIsSolid()
    {
        Assert.Null(LineTypeDashResolver.BuildPattern(LineType.Continuous, 1d, 1f));
    }

    [Fact]
    public void DashGapPatternScales()
    {
        float[]? pattern = LineTypeDashResolver.BuildPattern(Dashed(0.5, -0.25), 4d, 1f);

        Assert.Equal([2f, 1f], pattern);
    }

    [Fact]
    public void DotsBecomeStrokeWidthDashesAndGapsMerge()
    {
        LineType lineType = Dashed(0.5, -0.25, 0, -0.25);

        float[]? pattern = LineTypeDashResolver.BuildPattern(lineType, 2d, 1.5f);

        // dash 1, gap 0.5, dot -> 1.5, gap 0.5
        Assert.Equal([1f, 0.5f, 1.5f, 0.5f], pattern);
    }

    [Fact]
    public void ShapeSegmentsAreGaps()
    {
        LineType lineType = new("GAS");
        lineType.AddSegment(new LineType.Segment { Length = 0.5 });
        lineType.AddSegment(new LineType.Segment { Length = -0.2 });
        lineType.AddSegment(new LineType.Segment { Length = 0.3, IsText = true, Text = "GAS" });
        lineType.AddSegment(new LineType.Segment { Length = -0.2 });

        float[]? pattern = LineTypeDashResolver.BuildPattern(lineType, 10d, 1f);

        Assert.Equal([5f, 7f], pattern); // gaps 2 + 3 + 2 merged
    }

    [Fact]
    public void PatternStartingWithGapGetsZeroDash()
    {
        float[]? pattern = LineTypeDashResolver.BuildPattern(Dashed(-0.5, 0.5), 1d, 1f);

        Assert.Equal([0f, 0.5f, 0.5f, 0f], pattern);
    }

    [Fact]
    public void TinyPatternsAreSolidInPixelMode()
    {
        Line line = new() { LineType = Dashed(0.1, -0.1) };

        Assert.Null(LineTypeDashResolver.Resolve(line, Context(1d), 1f));
        Assert.NotNull(LineTypeDashResolver.Resolve(line, Context(20d), 1f));
        Assert.NotNull(LineTypeDashResolver.Resolve(line, Context(1d, unitsPerMillimeter: 1d), 1f));
    }

    [Fact]
    public void EntityLineTypeScaleMultiplies()
    {
        Line line = new() { LineType = Dashed(1, -1), LineTypeScale = 3 };

        Assert.Equal([3f, 3f], LineTypeDashResolver.Resolve(line, Context(1d), 1f));
    }
}
```

`LineType.Segment` in 3.7.1 has a public parameterless constructor and settable `Length`, `IsText`, `IsShape`, `Text` (from the API dump). If `Continuous` cannot be referenced as `LineType.Continuous`, use `new LineType("Continuous")` (no segments).

- [ ] **Step 2: Run, expect compile failure**

- [ ] **Step 3: Implement**

```csharp
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Tables;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Turns a CAD linetype into an alternating dash/gap array in surface units.
/// </summary>
internal static class LineTypeDashResolver
{
    public static float[]? Resolve(Entity entity, ImageRenderContext context, float strokeWidth)
    {
        LineType? lineType = entity.GetActiveLineType();
        if (lineType == null)
        {
            return null;
        }

        CadHeader? header = entity.Document?.Header;
        double ltscale = header != null && header.LineTypeScale > 0d ? header.LineTypeScale : 1d;
        double celtscale = entity.LineTypeScale > 0d ? entity.LineTypeScale : 1d;
        float[]? pattern = BuildPattern(lineType, ltscale * celtscale * context.LineTypeScale, strokeWidth);
        if (pattern == null)
        {
            return null;
        }

        if (EnforcesMinimumDash(context) && pattern.Sum() < context.Configuration.MinimumDashPixels)
        {
            return null;
        }

        return pattern;
    }

    public static bool EnforcesMinimumDash(ImageRenderContext context)
    {
        return context.StrokeUnitsPerMillimeter == null;
    }

    public static float[]? BuildPattern(LineType lineType, double scale, float strokeWidth)
    {
        List<(bool On, float Length)> entries = new();
        foreach (LineType.Segment segment in lineType.Segments)
        {
            double length = segment.Length * scale;
            bool on;
            float value;
            if (segment.IsShape || segment.IsText)
            {
                on = false;
                value = (float)Math.Abs(length);
            }
            else if (length > 0d)
            {
                on = true;
                value = (float)length;
            }
            else if (length < 0d)
            {
                on = false;
                value = (float)-length;
            }
            else
            {
                on = true;
                value = strokeWidth;
            }

            if (entries.Count > 0 && entries[^1].On == on)
            {
                entries[^1] = (on, entries[^1].Length + value);
            }
            else
            {
                entries.Add((on, value));
            }
        }

        if (entries.Count == 0 || !entries.Any(e => !e.On))
        {
            return null;
        }

        if (!entries[0].On)
        {
            entries.Insert(0, (true, 0f));
        }

        if (entries.Count % 2 == 1)
        {
            entries.Add((false, 0f));
        }

        return entries.Select(e => e.Length).ToArray();
    }
}
```

Check `PatternStartingWithGapGetsZeroDash`: entries gap 0.5, dash 0.5 → prepend dash 0 → [0 on, 0.5 off, 0.5 on] → odd → append 0 off → `[0, 0.5, 0.5, 0]`. Check `DotsBecomeStrokeWidthDashesAndGapsMerge`: 0.5·2=1 on, 0.5 off, dot 1.5 on, 0.5 off → `[1, 0.5, 1.5, 0.5]`. Check `ShapeSegmentsAreGaps`: 5 on, 2 off, 3 off (merged → 5), 2 off (merged → 7) → `[5, 7]`.

`ImageStyleResolver.Resolve` builds the style in two steps so the dash resolver gets the width:

```csharp
        float width = context.ToStrokeWidth(entity.GetActiveLineWeightType());
        return new ImageStyle(
            entity.GetActiveColor().ToImageColor(context.Configuration.ResolveForegroundColor()),
            width,
            LineTypeDashResolver.Resolve(entity, context, width),
            ResolveOpacity(entity, parentOpacity));
```

PSLTSCALE: `ImageRenderContext.CreateViewportContext` gets a new parameter `double lineTypeScale` used instead of `scale`; in `ImagePageRenderer.DrawViewport`:

```csharp
        bool paperSpaceLineTypeScaling = (viewport.Document?.Header.PaperSpaceLineTypeScaling ?? SpaceLineTypeScaling.Viewport) == SpaceLineTypeScaling.Viewport;
        double lineTypeScale = paperSpaceLineTypeScaling
            ? pageContext.LineTypeScale
            : pageContext.LineTypeScale * viewport.ScaleFactor;
        ImageRenderContext viewportContext = ImageRenderContext.CreateViewportContext(pageContext, viewport, viewportSurface, modelBounds, scale, lineTypeScale);
```

(`SpaceLineTypeScaling` is in `ACadSharp.Header`.) Deriving from the page's `LineTypeScale` rather than its `Scale` keeps the units right for every backend: raster pages have `LineTypeScale == Scale` (pixels per unit), SVG pages in non-scaling-stroke mode have `LineTypeScale == fit` (pixels per unit, matching the pixel stroke widths the browser also computes in pixel space) and in drawing-unit mode `LineTypeScale == 1`. Raster surface `CreatePen` already builds a `PatternPen` from `DashPattern`; the SVG surface already writes `stroke-dasharray`.

- [ ] **Step 4: Run everything**

Run: `dotnet test ACadSharp.Image.sln -c Release --nologo -v q`
Expected: PASS. If a `SampleParityTests` case or SVG golden now differs, confirm the sample really contains a non-continuous linetype (`grep -c "^DASHED\|^HIDDEN\|^CENTER" Samples/6-57-1119.dxf` or inspect the golden diff for `stroke-dasharray`), regenerate that baseline with the update flag, look at the PNG, and state it in the commit message.

- [ ] **Step 5: Commit**

```bash
git add ACadSharp.Image ACadSharp.Image.Tests
git commit -m "Render linetypes as dash patterns with LTSCALE and PSLTSCALE

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

---

### Task 6: Hatch

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs`
- Test: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`

- [ ] **Step 1: Failing tests**

```csharp
    private static Hatch SquareHatch(bool solid)
    {
        Hatch hatch = new();
        Hatch.BoundaryPath path = new();
        Hatch.BoundaryPath.Polyline polyline = new() { IsClosed = true };
        polyline.Vertices.AddRange([new XYZ(0, 0, 0), new XYZ(10, 0, 0), new XYZ(10, 10, 0), new XYZ(0, 10, 0)]);
        path.Edges.Add(polyline);
        hatch.Paths.Add(path);
        if (solid)
        {
            hatch.IsSolid = true;
            hatch.PatternType = HatchPatternType.SolidFill;
            hatch.Pattern = HatchPattern.Solid;
        }
        else
        {
            hatch.IsSolid = false;
            hatch.PatternType = HatchPatternType.PatternFill;
            hatch.Pattern = new HatchPattern("ANSI31");
            hatch.Pattern.Lines.Add(new HatchPattern.Line { Angle = Math.PI / 4, BasePoint = XY.Zero, Offset = new XY(0, 3.175) });
            hatch.PatternScale = 1;
        }

        return hatch;
    }

    [Fact]
    public void SolidHatchFillsBoundaryRings()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(CreateContext(surface, configuration), SquareHatch(solid: true));

        Assert.Contains("FillPath rings=1", surface.Calls);
        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("DrawLine", StringComparison.Ordinal));
    }

    [Fact]
    public void PatternHatchDrawsClippedLines()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(CreateContext(surface, configuration), SquareHatch(solid: false));

        int lines = surface.Calls.Count(c => c.StartsWith("DrawLine", StringComparison.Ordinal));
        Assert.InRange(lines, 5, 9); // 45-degree lines 3.175 apart across a 10x10 square
        Assert.All(surface.Styles, s => Assert.Null(s.DashPattern));
    }

    [Fact]
    public void PatternHatchIsCappedWithWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { MaxHatchLines = 3 };
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(CreateContext(surface, configuration), SquareHatch(solid: false));

        Assert.Equal(3, surface.Calls.Count(c => c.StartsWith("DrawLine", StringComparison.Ordinal)));
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("hatch", StringComparison.OrdinalIgnoreCase));
    }
```

(`NotificationEventArgs`/`NotificationType` are in `ACadSharp.IO`; add the using.) The probe against 3.7.1 produced 7 lines for exactly this hatch.

- [ ] **Step 2: Run, expect failure (`Drawing not implemented` notification, no calls)**

- [ ] **Step 3: Implement**

Add before the `Insert` case in `Draw`:

```csharp
                case Hatch hatch:
                    this.DrawHatch(context, style, hatch);
                    break;
```

and:

```csharp
    private void DrawHatch(ImageRenderContext context, ImageStyle style, Hatch hatch)
    {
        if (hatch.IsSolid || hatch.PatternType == HatchPatternType.SolidFill)
        {
            List<IReadOnlyList<SurfacePoint>> rings = new();
            foreach (Hatch.BoundaryPath path in hatch.Paths)
            {
                List<SurfacePoint> ring = new();
                foreach (XYZ point in path.GetPoints(this._configuration.ArcPrecision))
                {
                    ring.Add(context.ToSurfacePoint(point));
                }

                if (ring.Count >= 3)
                {
                    rings.Add(ring);
                }
            }

            if (rings.Count > 0)
            {
                context.Surface.FillPath(style, rings);
            }

            return;
        }

        ImageStyle lineStyle = style with { DashPattern = null };
        int drawn = 0;
        foreach (Entity segment in hatch.ExplodePattern())
        {
            if (segment is not Line line)
            {
                continue;
            }

            if (drawn >= this._configuration.MaxHatchLines)
            {
                this._configuration.Notify($"[{hatch.SubclassMarker}] Hatch pattern exceeds {this._configuration.MaxHatchLines} lines; remaining lines were skipped.", NotificationType.Warning);
                return;
            }

            context.Surface.DrawLine(lineStyle, context.ToSurfacePoint(line.StartPoint), context.ToSurfacePoint(line.EndPoint));
            drawn++;
        }
    }
```

`BoundaryPath.GetPoints` returns `IEnumerable<XYZ>` in ACadSharp 3.7.1 (verified by reflection). If `ExplodePattern` throws for a hatch without a pattern (`Pattern == null`), guard with `if (hatch.Pattern == null) return;` before the loop and add a `Warning` notification.

- [ ] **Step 4: Run everything, commit**

```bash
dotnet test ACadSharp.Image.sln -c Release --nologo -v q
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs
git commit -m "Render solid and pattern hatches

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

If a sample contains hatches, the parity PNG and SVG golden for it will change; regenerate as described in Global Constraints and say so in the commit message.

---

### Task 7: CLI layer options and `--list-layers`

**Files:**
- Modify: `ACadSharp.Image.Cli/CliOptions.cs`, `ACadSharp.Image.Cli/Program.cs`

- [ ] **Step 1: Options**

Add to `CliOptions`: `string? LayerVisibility, IReadOnlyList<string> OnlyLayers, bool ListLayers`. In `ParseArgs`:

```csharp
                case "--layer-visibility":
                    layerVisibility = GetRequiredValue(args, ref i, current);
                    break;
                case "--only-layer":
                    onlyLayers.Add(GetRequiredValue(args, ref i, current));
                    break;
                case "--list-layers":
                    listLayers = true;
                    break;
```

In `Configure`:

```csharp
        foreach (string layer in options.OnlyLayers)
        {
            configuration.IncludeLayer(layer);
        }

        if (!string.IsNullOrWhiteSpace(options.LayerVisibility))
        {
            configuration.LayerVisibility = Enum.TryParse(options.LayerVisibility, ignoreCase: true, out LayerVisibilityMode mode)
                ? mode
                : throw new InvalidOperationException($"Invalid --layer-visibility '{options.LayerVisibility}'. Use all, screen or plot.");
        }
```

- [ ] **Step 2: `--list-layers`**

In `Main`, after `CadDocument document = LoadDocument(inputPath);` and before adding content:

```csharp
            if (options.ListLayers)
            {
                WriteLayerTable(document);
                return 0;
            }
```

Move `ResolveFormat`/`ResolveOutputPath` after this block so listing never requires an output path. Add:

```csharp
    private static void WriteLayerTable(CadDocument document)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (ACadSharp.Entities.Entity entity in document.ModelSpace.Entities)
        {
            string name = entity.Layer?.Name ?? "0";
            counts[name] = counts.TryGetValue(name, out int count) ? count + 1 : 1;
        }

        List<ACadSharp.Tables.Layer> layers = document.Layers.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        int nameWidth = Math.Max(5, layers.Max(l => l.Name.Length));
        int lineTypeWidth = Math.Max(8, layers.Max(l => (l.LineType?.Name ?? "-").Length));

        Console.WriteLine($"{"Layer".PadRight(nameWidth)}  On   Frozen  Plot  Color        Weight  {"Linetype".PadRight(lineTypeWidth)}  Entities");
        foreach (ACadSharp.Tables.Layer layer in layers)
        {
            string color = layer.Color.IsTrueColor
                ? $"#{layer.Color.R:x2}{layer.Color.G:x2}{layer.Color.B:x2}"
                : layer.Color.Index.ToString(CultureInfo.InvariantCulture);
            counts.TryGetValue(layer.Name, out int count);
            Console.WriteLine(
                $"{layer.Name.PadRight(nameWidth)}  {(layer.IsOn ? "yes" : "no ")}  {(layer.Flags.HasFlag(ACadSharp.Tables.LayerFlags.Frozen) ? "yes   " : "no    ")}  {(layer.PlotFlag ? "yes " : "no  ")}  {color.PadRight(11)}  {layer.LineWeight.ToString().PadRight(6)}  {(layer.LineType?.Name ?? "-").PadRight(lineTypeWidth)}  {count}");
        }
    }
```

Help text additions:

```
      --only-layer <name>     Render only the specified layer(s). Can be used multiple times.
      --layer-visibility <m>  all (default), screen (honour off/frozen), or plot (also honour non-plottable).
      --list-layers           Print the drawing's layers and exit without rendering.
```

- [ ] **Step 3: Verify manually**

Run: `dotnet run --project ./ACadSharp.Image.Cli/ACadSharp.Image.Cli.csproj -c Release -- ./Samples/6-57-1119.dxf --list-layers`
Expected: a table with a header row and one row per layer including `OPTIONAL_DIMENSIONS`. Then:

```bash
dotnet run --project ./ACadSharp.Image.Cli/ACadSharp.Image.Cli.csproj -c Release -- ./Samples/6-57-1119.dxf --only-layer OPTIONAL_DIMENSIONS --format svg -o /tmp/claude-1000/-work-workspaces-orca-ACadSharp-Image-svg-support/f63cfd24-08c1-4c72-b680-352d15a25a0a/scratchpad/only.svg && grep -o 'data-layer="[^"]*"' /tmp/claude-1000/-work-workspaces-orca-ACadSharp-Image-svg-support/f63cfd24-08c1-4c72-b680-352d15a25a0a/scratchpad/only.svg | sort -u
```

Expected: exactly one `data-layer="OPTIONAL_DIMENSIONS"`.

- [ ] **Step 4: Tests and commit**

```bash
dotnet test ACadSharp.Image.sln -c Release --nologo -v q
git add ACadSharp.Image.Cli
git commit -m "Add layer visibility, include list and --list-layers to the CLI

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

---

### Task 8: README

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Features list (line 16 onwards)**

Add bullets: "SVG output with one `<g>` per layer, `data-*` attributes and real `<text>`, ready for React pan/zoom viewers", "Layer visibility modes (`screen`, `plot`) honouring off, frozen, non-plottable and viewport-frozen layers", "Include and exclude layer lists", "Linetypes, transparency and hatches".

- [ ] **Step 2: CLI reference (line 126 onwards)**

Replace the option table/list so it matches `WriteHelp` exactly, including `svg` in `--format`, `--only-layer`, `--layer-visibility`, `--list-layers` and the five `--svg-*` flags.

- [ ] **Step 3: Architecture tree (line 147)**

Update to:

```
ACadSharp.Image/
├── ImageExporter.cs             # Main public API
├── ImageConfiguration.cs        # Configuration (layers, colours, SVG options)
├── ImagePage.cs                 # Page representation
├── RenderedPage.cs              # Abstract rendered output (Save to path/stream)
├── RenderedImagePage.cs         # Raster output (ImageSharp)
├── RenderedSvgPage.cs           # SVG output
└── Rendering/
    ├── IDrawingSurface.cs           # Backend-neutral primitives
    ├── RasterDrawingSurface.cs      # ImageSharp backend
    ├── Svg/SvgDrawingSurface.cs     # SVG backend
    ├── ImagePageRenderer.cs         # Page-level rendering and viewports
    ├── EntityRenderDispatcher.cs    # Entity routing, layer filtering, hatches
    ├── EntityVisibilityFilter.cs    # Include/hide lists and layer state
    ├── ImageStyleResolver.cs        # Colour, width, dashes, opacity
    ├── LineTypeDashResolver.cs      # Linetype to dash array
    ├── SplineRenderer.cs / SplineBezierConverter.cs
    ├── TextRenderer.cs              # Text to SurfaceText
    └── ImageRenderContext.cs        # Coordinate transforms
```

- [ ] **Step 4: Advanced usage (line 172 onwards)**

Replace "Layer Filtering" with three subsections and add an SVG one:

````markdown
### Layer selection

```csharp
var exporter = new ImageExporter();
exporter.Configuration.IncludeLayers(["A-WALL", "A-DOOR"]);   // render only these (optional)
exporter.Configuration.HideLayer("DEFPOINTS");                 // then remove these
exporter.AddModelSpace(document);
```

Filtering happens when rendering, so it also applies to block contents, dimension geometry and paper-space viewport contents. Entities on layer `0` inside a block take the layer of the insert that placed them.

### Layer visibility

```csharp
exporter.Configuration.LayerVisibility = LayerVisibilityMode.Plot; // All (default), Screen, Plot
```

`Screen` hides off and frozen layers, invisible entities and layers frozen per viewport. `Plot` also hides non-plottable layers such as `DEFPOINTS`.

### Linetypes, transparency and colour 7

Dashed linetypes are rendered using `LTSCALE`, the entity linetype scale and `PSLTSCALE` in paper space; patterns shorter than `MinimumDashPixels` are drawn solid. Entity transparency becomes opacity (ByLayer is treated as opaque because the ACadSharp layer table carries no transparency). Colour index 7 resolves to black or white from the background luminance, or to `ForegroundColor` when set.

### SVG output

```csharp
exporter.Configuration.Svg.NonScalingStroke = true;      // constant on-screen stroke width when zooming (default)
exporter.Configuration.Svg.IdPrefix = "plan1-";          // when inlining several drawings in one page
exporter.Save("plan.svg", ImageExportFormat.Svg);
```

The SVG has a drawing-unit `viewBox`, no `width`/`height` unless `Svg.EmitSize` is set, an attribute-free `<g class="cad-root">` for your pan/zoom transform, and one `<g data-layer="...">` per layer. Every element carries `data-handle` and `data-type` (plus `data-parent`/`data-block` for block contents). In React, prefer injecting the markup at runtime or configure SVGO to keep ids; `data-*` attributes survive the default SVGR pipeline. Toggle a layer with CSS `display: none` on its group.
````

- [ ] **Step 5: Migration notes (line 274)**

Add a new list under a heading `### 2.0` (or the next major version):

- `ImageExporter.Render()` now takes an optional `ImageExportFormat` and returns `IReadOnlyList<RenderedPage>`; cast to `RenderedImagePage` for the canvas or `RenderedSvgPage` for the markup, or call `Save`.
- `RenderedImagePage` derives from `RenderedPage` and knows its format and quality.
- `ImagePage.Entities` keeps every added entity; hidden layers are applied at render time, so changing `HiddenLayers` after `Add` takes effect.
- net6.0 is no longer targeted; ACadSharp 3.7.1 is required.
- Release with a major version tag (`v2.0.0`).

- [ ] **Step 6: Run examples section (line 239)**

Add: `dotnet run --project ./ACadSharp.Image.Cli/ACadSharp.Image.Cli.csproj -- "./Samples/6-57-1119.dxf" --format svg --layer-visibility plot`.

- [ ] **Step 7: Final full verification and commit**

```bash
dotnet build ACadSharp.Image.sln -c Release --nologo -v q 2>&1 | grep -E "warn|error" ; dotnet test ACadSharp.Image.sln -c Release --nologo -v q
git add README.md
git commit -m "Document layer visibility, selection and SVG output

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

Expected: 0 warnings introduced by this work (XML doc warnings count), all tests green.

---

## Self-review checklist

- Spec 4.1 → Tasks 1, 2. 4.2 (include list, render-loop filtering, layer-0 rule) → Tasks 1, 2. 4.3 (ACI 7, transparency, linetype, deviations) → Tasks 3, 4, 5. 4.4 (LTSCALE, PSLTSCALE, dots, shapes, minimum dash) → Task 5. 4.5 (hatch, cap) → Task 6. Section 7 (CLI layer flags, `--list-layers`) → Task 7. README and migration → Task 8.
- Names: `EntityVisibilityFilter.IsVisible(Entity, Layer?, string, Viewport?)`, `EntityRenderDispatcher.GetEffectiveLayer(Entity, Layer?)`, `ImageStyleResolver.Resolve(Entity, ImageRenderContext, float)`, `ImageStyleResolver.ResolveOpacity`, `LineTypeDashResolver.Resolve/BuildPattern/EnforcesMinimumDash`, `ImageConfiguration.ResolveForegroundColor`, `ColorExtensions.ToImageColor(CadColor, ImageColor)`, `ImageRenderContext.CreateViewportContext(..., double scale, double lineTypeScale)`, `RecordingDrawingSurface.Styles`, `ImagePageRenderer.RenderTo(IDrawingSurface, ImagePage)`.
- Parity: default settings keep raster output; any sample-driven baseline change is inspected and called out in the commit.
