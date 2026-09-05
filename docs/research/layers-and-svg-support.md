# Layers, layer selection, and SVG output in ACadSharp.Image

- Date: 2026-09-02
- Branch: `mubeda/svg-support`
- HEAD: `178e4c2f3d9e3721f7335ba44698d9e1ed1e3cb3` (identical to `main`; see [SVG section](#3-svg-image-generation))
- Method: repository source read directly (line numbers below are from the working tree at HEAD), pinned NuGet package XML docs restored to `~/.nuget/packages`, and upstream source/docs at the pinned versions where the behaviour lives outside this repo.

## Summary

| Capability | Status | Where |
| --- | --- | --- |
| Reading layer info during rendering: ByLayer colour and ByLayer line weight inheritance | Supported | `ACadSharp.Image/Rendering/ImageStyleResolver.cs:31-36` (delegates to ACadSharp `Entity.GetActiveColor()` / `GetActiveLineWeightType()`) |
| Honouring layer state: off (`Layer.IsOn`), frozen (`LayerFlags.Frozen`), non-plottable (`Layer.PlotFlag`), per-viewport frozen layers (`Viewport.FrozenLayers`), `Entity.IsInvisible` | Not supported | No reference anywhere in `ACadSharp.Image/` or `ACadSharp.Image.Cli/` (grep detailed in [1.2](#12-layer-attributes-not-honoured)) |
| Layer linetype (`Layer.LineType`, dash patterns) and transparency | Not supported | Same grep; all strokes are solid `DrawLine`/`Draw` calls (`EntityRenderDispatcher.cs`, `SplineRenderer.cs`) |
| Selecting layers (library API): hide by name, exclude-list | Supported (exclude-only) | `ACadSharp.Image/ImageConfiguration.cs:106,217,354-391`; applied in `ACadSharp.Image/ImageExporter.cs:150-174` |
| Selecting layers (CLI) | Supported (exclude-only) | `ACadSharp.Image.Cli/Program.cs:79-82,195-197,305` |
| Include-only / whitelist / wildcard layer selection | Not supported | No such API; only `HideLayer*`/`ShowLayer` exist (`ImageConfiguration.cs:354-391`) |
| Hidden-layer filter applied to viewport contents, exploded `Insert`s, dimension blocks | Not supported (bypass) | `ACadSharp.Image/Rendering/ImagePageRenderer.cs:85`, `EntityRenderDispatcher.cs:132,177` |
| Raster output: PNG, BMP, JPEG, GIF, WebP | Supported | `ACadSharp.Image/ImageExportFormat.cs:6-35`, `ACadSharp.Image/ImageExporter.cs:252-273` |
| SVG output | Not supported | No `svg` token in any source file; `ImageExportFormat` has no `Svg` member; branch diff vs `main` is empty |
| SVG work in progress on this branch | None | `git log --oneline main..HEAD` and `git diff main --stat` are both empty; sibling worktree clean |

## 1. Layers — reading layer info and using it during rendering

### 1.1 What is honoured

**Layer name (for filtering).** `ImageExporter.IsHiddenLayer` reads `entity.Layer?.Name` and checks it against the configured hidden set (`ACadSharp.Image/ImageExporter.cs:160-174`). Details in [section 2](#2-selection-of-layers).

**ByLayer / ByBlock colour and line weight.** The only place style is resolved is `ImageStyleResolver.Resolve`:

```csharp
// ACadSharp.Image/Rendering/ImageStyleResolver.cs:31-36
public ImageStyle Resolve(Entity entity)
{
    return new ImageStyle(
        entity.GetActiveColor().ToImageColor(),
        this._configuration.GetLineWeightPixels(entity.GetActiveLineWeightType()));
}
```

`GetActiveColor()` and `GetActiveLineWeightType()` are ACadSharp members, not this repo's code. The pinned package is ACadSharp 3.4.24 (`Directory.Packages.props:7`). Its XML docs describe them as "Get the active color for the entity, process the colors like `Color.ByBlock` and `Color.ByLayer`" and "Get the active line weight for the entity, process the line weights like `LineWeightType.ByBlock` and `LineWeightType.ByLayer`" (`~/.nuget/packages/acadsharp/3.4.24/lib/net48/ACadSharp.xml`, members `M:ACadSharp.Entities.IEntity.GetActiveColor` and `M:ACadSharp.Entities.IEntity.GetActiveLineWeightType`). The source at the pinned tag (https://github.com/DomCR/ACadSharp/blob/v3.4.24/src/ACadSharp/Entities/Entity.cs) resolves:

- `GetActiveColor()`: `Color.IsByLayer` -> `this.Layer.Color`; `Color.IsByBlock` with `Owner is BlockRecord record` -> `record.BlockEntity.Color`; otherwise the entity's own `Color`.
- `GetActiveLineWeightType()`: `LineWeightType.ByLayer` -> `this.Layer.LineWeight`; `ByBlock` with a `BlockRecord` owner -> `record.BlockEntity.GetActiveLineWeightType()`; otherwise the entity's own `LineWeight`.

So layer colour and layer line weight are inherited when the entity says ByLayer. The resolved `LineWeightType` is turned into pixels by `ImageConfiguration.GetLineWeightPixels` (`ImageConfiguration.cs:299-314`) using the mm table (`:52-80`), `Dpi` (`:139`) and `LineWeightScale` (`:156`), with per-type overrides via `SetLineWeight` (`:398-406`).

The style is resolved once per entity at the top of the dispatcher (`ACadSharp.Image/Rendering/EntityRenderDispatcher.cs:58-60`) and passed to every drawing primitive, including the spline and text renderers (`SplineRenderer.cs:16`, `TextRenderer.cs:16,42`).

**Colour conversion caveat.** `ColorExtensions.ToImageColor` maps ACI index 7 to black unconditionally, regardless of `ImageConfiguration.BackgroundColor` (`ACadSharp.Image/Extensions/ColorExtensions.cs:15-26`). A layer whose colour is index 7 therefore renders black even on a dark background.

### 1.2 Layer attributes not honoured

ACadSharp 3.4.24 exposes `Layer.IsOn` ("Indicates if the Layer is visible in the model"), `Layer.Flags` (`LayerFlags.Frozen`, `FrozenNewViewports`, `Locked`, ...), `Layer.PlotFlag` ("Specifies if the layer is plottable"), `Layer.LineType`, `Viewport.FrozenLayers` ("Frozen layer object ID/handle"), `Entity.IsInvisible`, `Entity.LineType`, `Entity.LineTypeScale` and `Entity.Transparency` (all from the pinned `ACadSharp.xml`, members `P:ACadSharp.Tables.Layer.*`, `F:ACadSharp.Tables.LayerFlags.*`, `P:ACadSharp.Entities.Viewport.FrozenLayers`, `P:ACadSharp.Entities.Entity.*`).

None of these are read by this repository. Search performed:

```
grep -rn -i "IsOn\|Frozen\|Plottable\|PlotFlag\|\.Layer\b\|Layer\.\|LineType\|Linetype\|Transparency\|IsInvisible\|Invisible" ACadSharp.Image/ ACadSharp.Image.Cli/ --include="*.cs"
```

The only layer-related hit in library code is `entity.Layer?.Name` at `ACadSharp.Image/ImageExporter.cs:167`; the remaining hits are the word "transparency" in format XML comments (`ImageExportFormat.cs:9,20,25,32`, `ImageConfiguration.cs:199`), `OrdinalIgnoreCase` string comparisons, and the CLI help text. Consequences:

- Entities on layers that are **off** or **frozen** in the drawing are rendered.
- Entities on **non-plottable** layers (e.g. `DEFPOINTS`) are rendered unless the caller hides them by name (the README's own example hides `DEFPOINTS` manually, `README.md:183`).
- **Per-viewport frozen layers** are ignored: `DrawViewport` draws everything returned by `viewport.SelectEntities()` (`ImagePageRenderer.cs:85`).
- **Linetype** is never consulted (ACadSharp provides `Entity.GetActiveLineType()` but nothing in this repo calls it); every stroke is a solid `DrawLine`/`Draw` (`EntityRenderDispatcher.cs`, `SplineRenderer.cs:33,46,74`).
- **Transparency** and `IsInvisible` are ignored.

## 2. Selection of layers

### 2.1 Library API

All mutation goes through `ImageConfiguration` (`ACadSharp.Image/ImageConfiguration.cs`):

| Member | Signature | Lines |
| --- | --- | --- |
| Backing store | `private readonly HashSet<string> _hiddenLayers = new(StringComparer.OrdinalIgnoreCase);` | 106 |
| Read view | `public IReadOnlySet<string> HiddenLayers { get; }` | 217 |
| Hide one | `public void HideLayer(string layerName)` — throws `ArgumentException` on null/whitespace | 354-358 |
| Hide many | `public void HideLayers(IEnumerable<string> layerNames)` | 364-372 |
| Un-hide | `public bool ShowLayer(string layerName)` — returns whether it was removed | 379-383 |
| Reset | `public void ClearHiddenLayers()` | 388-391 |

Names are compared case-insensitively (`:106`, `:215`). There is no include-list, whitelist, wildcard, regex, or "only these layers" API — the model is exclude-only.

### 2.2 Where the filter is applied

The filter runs at **add time**, in `ImageExporter`, not in the render loop:

- `ShouldIncludeEntity(Entity)` (`ACadSharp.Image/ImageExporter.cs:150-158`) drops `Viewport` entities and anything for which `IsHiddenLayer` is true.
- `IsHiddenLayer(Entity)` (`:160-174`) short-circuits when `HiddenLayers.Count == 0`, reads `entity.Layer?.Name`, returns `false` for a null/empty name, otherwise `HiddenLayers.Contains(layerName)`.
- `Add(Layout)` applies it to `layout.AssociatedBlock.Entities` only (`:112-118`); viewports are added unfiltered (`:120-128`).
- `Add(BlockRecord)` passes it as the `entityFilter` predicate to `ImagePage.Add(BlockRecord, Func<Entity,bool>?, bool)` (`:146`; `ACadSharp.Image/ImagePage.cs:77-93`).
- `AddModelSpace(CadDocument)` and `AddPaperLayouts(CadDocument)` are thin wrappers over the two `Add` overloads (`:62-76`).

The render loop itself (`ImagePageRenderer.Render`, `ACadSharp.Image/Rendering/ImagePageRenderer.cs:52-68`) iterates `page.Viewports` and `page.Entities` with no layer check, and `EntityRenderDispatcher.Draw` (`EntityRenderDispatcher.cs:58`) has none either.

Consequences of add-time filtering:

1. **`--paper-layouts --hide-layer X` does not hide model-space content.** Paper-space viewport contents come from `viewport.SelectEntities()` inside `DrawViewport` (`ImagePageRenderer.cs:85`), which is never passed through `ShouldIncludeEntity`. Only paper-space entities (title block, annotations) on layer X are removed.
2. **Nested entities bypass the filter.** `Insert` is drawn via `insert.Explode()` (`EntityRenderDispatcher.cs:175-181`, loop at `:177`) and `Dimension` via its block's entities (`:117-141`, loop at `:132`); sub-entities on a hidden layer are still drawn. Only the top-level `Insert`/`Dimension` entity's own layer is tested.
3. **Public `ImagePage` mutators skip the filter.** `ImagePage.Add(BlockRecord, bool)` (`ImagePage.cs:66`), `ImagePage.AddEntity` (`:109`) and `ImagePage.AddViewport` (`:119`) do not know about `HiddenLayers`.
4. **Changing `HiddenLayers` after `Add(...)` has no effect** on pages already built, since `page.Entities` is materialised at add time.

### 2.3 CLI

`ACadSharp.Image.Cli`:

- Option: `--hide-layer <name>`, repeatable, no short alias (`ACadSharp.Image.Cli/Program.cs:195-197`; help text `:305`). Stored as `IReadOnlyList<string> HideLayers` on the options record (`ACadSharp.Image.Cli/CliOptions.cs:16`).
- Applied by `Configure`, which calls `configuration.HideLayer(layer)` for each value (`Program.cs:79-82`).
- No `--show-layer`, `--only-layer`, `--layers` include list, or layer-listing command exists (the full option switch is `Program.cs:162-201`).

README documents the same surface (`README.md:22,74-76,112-116,141,174-187,281`).

### 2.4 Test coverage

- `ACadSharp.Image.Tests/ImageConfigurationTests.cs:8-24` — `HiddenLayersAreManagedThroughMethods`: `HideLayer`, case-insensitive `Contains`, `ShowLayer` returns `true`, `ClearHiddenLayers`.
- `ACadSharp.Image.Tests/ImageExporterTests.cs:217-244` — `HiddenLayersFiltersOutEntitiesOnSpecifiedLayers`: three lines on `Layer1/2/3`, hide `Layer2`, `Add(BlockRecord)`, asserts `page.Entities.Count == 2` (`:243`).
- `ImageExporterTests.cs:246-262` — `HiddenLayersIsCaseInsensitive`: hide `mylayer`, entity on `MyLayer`, asserts `Assert.Empty(page.Entities)` (`:261`).
- `ImageExporterTests.cs:264-290` — `MultipleHiddenLayersCanBeConfigured`: hide `Layer1`,`Layer3`, asserts `Assert.Single(page.Entities)` (`:289`).

All three exporter tests go through `Add(BlockRecord)` and assert on `page.Entities` before rendering. Not covered: the `Add(Layout)` path, the viewport bypass, nested `Insert`/`Dimension` bypass, the CLI `--hide-layer` parsing, and any pixel-level check that hidden content is absent from the output image. (CodeGraph's blast-radius note flags `IsHiddenLayer` as having "no covering tests"; that is a direct-caller heuristic — the three exporter tests do exercise it indirectly via `Add(BlockRecord)`.)

## 3. SVG image generation

### 3.1 Output formats and backend as of HEAD

- Formats: `ImageExportFormat { Png, Bmp, Jpeg, Gif, Webp }` (`ACadSharp.Image/ImageExportFormat.cs:6-35`). Extension mapping and parsing accept only those five plus the `jpg` alias (`ACadSharp.Image/ImageExportFormatExtensions.cs:24-33,62-78,107-110`). CLI help lists `png, bmp, jpg, jpeg, gif, webp` (`Program.cs:298`); `ResolveFormat` falls back to PNG (`:107-125`).
- Encoding: `ImageExporter.SavePage` switches on the enum and calls `page.Canvas.Save(path, new {Bmp,Jpeg,Gif,Webp,Png}Encoder())` (`ACadSharp.Image/ImageExporter.cs:252-273`).
- Backend: SixLabors.ImageSharp 3.1.12, SixLabors.ImageSharp.Drawing 2.1.7, SixLabors.Fonts 2.1.3 (`Directory.Packages.props:10-12`; referenced at `ACadSharp.Image/ACadSharp.Image.csproj:18-21`). The package describes itself as a "Raster image exporter ... using SixLabors.ImageSharp" (`ACadSharp.Image.csproj:10`). The canvas type is `SixLabors.ImageSharp.Image<Rgba32>` in both `ImageRenderContext.Canvas` (`ACadSharp.Image/Rendering/ImageRenderContext.cs:11`) and `RenderedImagePage.Canvas` (`ACadSharp.Image/RenderedImagePage.cs:32`), and every primitive is an ImageSharp.Drawing `Mutate(...)` call (`EntityRenderDispatcher.cs`, `SplineRenderer.cs:33,46,74`, `TextRenderer.cs:39,63`).
- No SkiaSharp or System.Drawing reference exists (`Directory.Packages.props:6-15` is the full package list).

### 3.2 SVG is absent

Searches performed:

```
grep -rniE "svg" --include="*.cs" --include="*.csproj" --include="*.props" --include="*.md" --include="*.sln" --include="*.json" .
```

Only hits: `README.md:4` and `README.md:6` — the `License-MIT-blue.svg` and `ci.yml/badge.svg` badge image URLs. No `Svg` enum member, encoder, writer, test, or CLI value exists.

History: `git log --all --oneline -S svg -i` returns only `6ae39c2 Update README`, whose `svg` matches are the same two badge URLs.

### 3.3 What the `svg-support` branch changed vs `main`

Nothing.

- `git log --oneline main..HEAD` — empty.
- `git diff main --stat` — empty.
- `git log --oneline HEAD..main` — empty (branch is not behind either).
- `git branch -a` shows a local `svg-support` and this `mubeda/svg-support`, both at `178e4c2`, and only `origin/main` as a remote branch (no pushed SVG branch).
- `git worktree list` shows the sibling worktree `/work/github/ACadSharp.Image` on `svg-support` at the same SHA; `git -C /work/github/ACadSharp.Image status --porcelain` is clean and `git stash list` is empty, so there is no uncommitted SVG work either.

The branch name is, at HEAD, an intention only.

### 3.4 Backend capability: ImageSharp cannot emit SVG

- ImageSharp's format page lists 13 raster codecs (ANI, BMP, CUR, EXR, GIF, ICO, JPEG, PBM, PNG, QOI, TGA, TIFF, WebP) and states "ImageSharp works with raster images. Vector artwork, document formats, and application-native design files are outside the built-in codec set." — https://docs.sixlabors.com/articles/imagesharp/imageformats.html
- The pinned 3.1.12 assembly's encoder types (`T:SixLabors.ImageSharp.Formats.*Encoder` in `~/.nuget/packages/sixlabors.imagesharp/3.1.12/lib/net6.0/SixLabors.ImageSharp.xml`) are Bmp, Gif, Jpeg, Pbm, Png, Qoi, Tga, Tiff, Webp — no SVG.
- The only `svg` in ImageSharp.Drawing 2.1.7 is `Path.TryParseSvgPath(string, out IPath)` (`~/.nuget/packages/sixlabors.imagesharp.drawing/2.1.7/lib/net6.0/SixLabors.ImageSharp.Drawing.xml`, member `M:SixLabors.ImageSharp.Drawing.Path.TryParseSvgPath`), which parses SVG path *input* into geometry; it does not write SVG.

### 3.5 What adding SVG would take (factual options, no recommendation)

**Option A — delegate to ACadSharp's own SVG writer.** The already-referenced ACadSharp 3.4.24 ships `ACadSharp.IO.SvgWriter` ("Writer to support the creation of SVG from `BlockRecord` and `Layout`") and `ACadSharp.IO.SvgConfiguration` (pinned `ACadSharp.xml`, members `T:ACadSharp.IO.SvgWriter`, `T:ACadSharp.IO.SvgConfiguration`).

- Constructors documented in the XML: `SvgWriter(Stream)`, `SvgWriter(string filename)`, `SvgWriter(string filename, CadDocument document)`. The source at the tag also has `SvgWriter(Stream, CadDocument)` (https://github.com/DomCR/ACadSharp/blob/v3.4.24/src/ACadSharp/IO/SvgWriter.cs).
- Methods documented in the XML: `Write()` ("will draw all the content in the model space"; requires a non-null `CadDocument`), `Write(Layout)`, `Dispose()`. The tagged source additionally has `Write(BlockRecord)` (https://github.com/DomCR/ACadSharp/blob/v3.4.24/src/ACadSharp/IO/SvgWriter.cs).
- `SvgConfiguration` documented in the XML: `LineWeightRatio` (default 100), `DefaultLineWeight` (mm), `PointRadius`, `GetLineWeightValue(LineWeightType, UnitsType)`. The tagged source additionally has an undocumented `ArcPoints` property (https://github.com/DomCR/ACadSharp/blob/v3.4.24/src/ACadSharp/IO/SVG/SvgConfiguration.cs).
- The drawing code `SvgXmlWriter` derives `stroke` from `entity.GetActiveColor()` and `stroke-width` from `entity.GetActiveLineWeightType()` via `GetLineWeightValue`, so ByLayer inheritance matches this repo's raster path. Per the fetched source at v3.4.24 (https://github.com/DomCR/ACadSharp/blob/v3.4.24/src/ACadSharp/IO/SVG/SvgXmlWriter.cs) it contains no references to `IsOn`, `Frozen`, `PlotFlag` or `IsInvisible`, its `writeSpline` is commented out, and it has writers for Arc, Circle, Dimension, Ellipse, Hatch, Insert, Line, Point, Polyline, Solid, Text.
- Implications for this repo: `SvgWriter` has no hidden-layer concept, so `HiddenLayers` would have to be applied by pre-building a filtered `BlockRecord`/`Layout` before calling `Write`; raster-only settings (`Width`, `Height`, padding, `BackgroundColor`, `OutputQuality`, `Dpi`, `FontFamilyName`, `ArcPrecision`) do not map onto `SvgConfiguration`; splines would be dropped; and `RenderedImagePage.Canvas` (`Image<Rgba32>`) has no SVG counterpart, so `Render()`/`Save()` would need a separate code path.

**Option B — introduce a vector-capable drawing surface.** Because `Image<Rgba32>` is baked into `ImageRenderContext.Canvas` (`ImageRenderContext.cs:11`), `RenderedImagePage.Canvas` (`RenderedImagePage.cs:32`) and every `Canvas.Mutate(...)` call in `EntityRenderDispatcher`, `SplineRenderer` and `TextRenderer`, an SVG target would require abstracting those primitives behind an interface and adding an SVG implementation. One such implementation is SkiaSharp's `SKSvgCanvas` — "A specialized SKCanvas which generates SVG commands from its draw calls", with `Create(SKRect, Stream)` and `Create(SKRect, SKWStream)` (https://learn.microsoft.com/en-us/dotnet/api/skiasharp.sksvgcanvas) — which would add a native-dependency package the project currently does not have (`Directory.Packages.props:6-15`). Either route also needs a new `ImageExportFormat.Svg` member, extension/parse entries (`ImageExportFormatExtensions.cs:24-33,62-78`), a `SavePage` branch (`ImageExporter.cs:252-273`), and the CLI format list (`Program.cs:298`).

## 4. Gaps / open questions

1. **Layer state is ignored.** Off, frozen, non-plottable, and viewport-frozen layers all render; `Entity.IsInvisible` renders. A "respect drawing visibility" mode would need reads of `Layer.IsOn`, `Layer.Flags`, `Layer.PlotFlag`, `Viewport.FrozenLayers` (all available in ACadSharp 3.4.24), and the decision of whether it should be default-on or opt-in.
2. **Hidden-layer filter does not reach viewport contents or nested block/dimension entities** (`ImagePageRenderer.cs:85`, `EntityRenderDispatcher.cs:132,177`). Moving the check into `EntityRenderDispatcher.Draw` (or `DrawViewport`) would close this, at the cost of also changing what `ImagePage.Entities` contains.
3. **Exclude-only selection.** No include-list; a caller wanting "only layer X" must enumerate every other layer from `CadDocument.Layers` themselves.
4. **Linetype is never rendered** (dash patterns from `Layer.LineType`/`Entity.LineType` are dropped); ACI 7 is hard-coded to black (`ColorExtensions.cs:15-26`).
5. **SVG does not exist and the branch has no work on it.** Whether the intended route is ACadSharp's `SvgWriter` (already a dependency, no hidden-layer support, no splines) or a new drawing-surface abstraction is undecided; both are described in [3.5](#35-what-adding-svg-would-take-factual-options-no-recommendation).
6. **Test gaps.** No tests for `Add(Layout)` + `HideLayer`, viewport/nested bypass, CLI `--hide-layer` parsing, or output-pixel assertions for hidden layers.
7. **Not verified here.** The `SvgXmlWriter` "no layer checks" statement rests on a fetch of the tagged source summarised for those identifiers, not on a local compile; the local `ACadSharp.xml` confirms the public surface but not method bodies. No sample DXF/DWG in `Samples/` was rendered to confirm the off/frozen-layer behaviour empirically — the conclusion is from the absence of any code reading those properties.
