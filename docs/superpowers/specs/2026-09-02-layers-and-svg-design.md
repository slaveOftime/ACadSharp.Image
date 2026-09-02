# Layer attributes and SVG output: design spec

- Date: 2026-09-02
- Branch: `mubeda/svg-support`
- Status: approved by the repository owner after a structured design interview (four rounds).
- Research inputs: `docs/research/layers-and-svg-support.md` (repo state), plus web research on SVG conventions for React apps and on SkiaSharp's SVG canvas (summarised in section 8).

## 1. Goal

Add full layer attribute support and a hand-written SVG output backend to ACadSharp.Image, sharing all entity decomposition and layer logic between the existing ImageSharp raster backend and the new SVG backend.

## 2. Non-goals

- Non-rectangular viewport clipping (follow-up).
- Text outlined to paths in SVG (follow-up, opt-in).
- Embedded shapes or text inside linetypes (rendered as gaps).
- Wildcard or regex layer selection.
- Any change to the DXF/DWG reading side (ACadSharp does that).

## 3. Global constraints

- ACadSharp pinned to **3.7.1** (verified: solution builds and all 28 tests pass against it).
- Target frameworks for the library: **net8.0;net10.0** (net6.0 dropped). CLI and tests stay net10.0. Remove `6.0.x` from both GitHub workflows.
- **No new NuGet dependencies.** SVG is built with the in-box `System.Xml.Linq` types (`XDocument`/`XElement`), which allow appending to per-layer groups in any order.
- SkiaSharp is rejected (section 8.2).
- Single branch, single PR. Plans are executed in order: foundation, SVG backend, layer attributes.
- Release versioning comes from the git tag (`release.yml` passes `-p:Version=${GITHUB_REF_NAME#v}`); the next release tag must be a **major** bump because `RenderedImagePage`, `Render()`, and `ImagePage.Entities` semantics change. Record this in the README migration notes.
- Existing raster output must stay **pixel-identical** for the default configuration after the abstraction refactor. Parity is verified against committed baseline PNGs rendered from the three files in `Samples/` before the refactor (font pinned to `DejaVu Sans`, which is installed on this machine and on `ubuntu-latest`).
- Coding conventions already in the repo: `this.` prefix on members, explicit types except LINQ lambdas, XML docs on public members, `sealed` classes, file-scoped namespaces, `internal` for rendering internals with `InternalsVisibleTo` for the test project.

## 4. Layer attributes

### 4.1 Visibility mode (opt-in)

```csharp
public enum LayerVisibilityMode
{
    /// <summary>Render everything regardless of layer state. Default; today's behaviour.</summary>
    All,
    /// <summary>Hide entities on layers that are off or frozen, entities flagged invisible, and layers frozen in the current viewport.</summary>
    Screen,
    /// <summary>Screen rules plus hide entities on non-plottable layers.</summary>
    Plot,
}
```

`ImageConfiguration.LayerVisibility { get; set; } = LayerVisibilityMode.All`.

Facts from ACadSharp 3.7.1 used by the rules: `Layer.IsOn` (bool, default true), `Layer.Flags` has `LayerFlags.Frozen`, `Layer.PlotFlag` (bool, default true), `Entity.IsInvisible`, `Viewport.FrozenLayers` is `List<Layer>`.

### 4.2 Selection

- Existing exclude list stays: `HideLayer`, `HideLayers`, `ShowLayer`, `ClearHiddenLayers`, `HiddenLayers`.
- New include list with the same shape: `IncludeLayer(string)`, `IncludeLayers(IEnumerable<string>)`, `ExcludeLayer(string)` (removes from include list, returns bool), `ClearIncludedLayers()`, `IReadOnlySet<string> IncludedLayers`. Case-insensitive.
- Composition order, evaluated per drawn entity: include list (if non-empty, the layer must be in it), then hide list, then visibility mode.
- All checks run in the **render loop**, not at add time. `ImageExporter.Add(...)` no longer filters by layer (it still skips `Viewport` entities in the entity list). Consequences accepted: `ImagePage.Entities` contains entities that may not be drawn; changing the configuration after `Add` takes effect; viewport contents, exploded `Insert` sub-entities and dimension block entities are all filtered.
- Effective layer of a nested entity: its own layer, except entities on layer `0` (`Layer.DefaultName`) inherit the parent `Insert`'s effective layer.
- Framing of auto-sized pages (block and model-space pages, whose extents come from their entities) is recomputed at render time from the entities that pass the filters, so hiding a far-away layer still tightens the frame exactly as it did with add-time filtering. Layout pages keep their paper size.

### 4.3 Attributes honoured

| Attribute | Source | Raster | SVG |
| --- | --- | --- | --- |
| Colour ByLayer/ByBlock | `Entity.GetActiveColor()` (unchanged) | stroke colour | `stroke`/`fill` |
| ACI 7 | luminance of `BackgroundColor`, or `ImageConfiguration.ForegroundColor` when set | colour | colour |
| Line weight ByLayer/ByBlock | `Entity.GetActiveLineWeightType()` (unchanged) | px via `GetLineWeightPixels` | px (non-scaling mode) or drawing units (section 5.5) |
| Linetype | `Entity.GetActiveLineType()`, `LineType.Segments` | `PatternPen` (pattern is in multiples of stroke width) | `stroke-dasharray` |
| Transparency | `Entity.Transparency` | alpha blended into colour | `opacity` attribute |
| Off / frozen / plot / viewport-frozen / invisible | section 4.1 | skipped | omitted from file |

**Deviation from the interview (recorded):** ACadSharp 3.7.1's `Layer` has no `Transparency` property, and `Entity.Transparency` defaults to ByLayer (`Value == -1`). Resolution: ByLayer resolves to opaque; ByBlock resolves to the parent `Insert`'s resolved opacity (opaque at top level); explicit values map `Value` 0..90 to `opacity = 1 - Value / 100.0`.

### 4.4 Linetype scaling rules

- Dash lengths in drawing units: `segment.Length * header.LineTypeScale * entity.LineTypeScale`. `header` is `entity.Document?.Header`; when the entity has no document, `LineTypeScale` is 1.
- Segment mapping: `Length > 0` is a dash, `Length < 0` is a gap of `|Length|`, `Length == 0` is a dot rendered as a dash of one stroke width, `IsShape` or `IsText` segments are gaps of `|Length|`.
- Paper-space viewports: when `header.PaperSpaceLineTypeScaling == SpaceLineTypeScaling.Viewport` (the DXF default, PSLTSCALE=1), dash lengths inside a viewport are scaled by the **page** scale, not the viewport scale, so dashes look uniform on the sheet. Otherwise they scale with the viewport.
- Raster: when the full pattern length in pixels is below `ImageConfiguration.MinimumDashPixels` (default 2), draw solid. SVG in pixel-width mode applies the same threshold; in drawing-unit mode it does not.
- `LineType.Continuous` (no segments) and null linetypes are solid.

### 4.5 Hatch (new entity, both backends)

- Solid (`hatch.IsSolid`): fill the boundary loops with the even-odd rule. Loop points come from `path.GetPoints(ArcPrecision)`.
- Pattern: `hatch.ExplodePattern()` returns `Line` entities already clipped to the boundary and already honouring `PatternScale`, `PatternAngle`, and `DashLengths` (verified empirically against 3.7.1). Draw each as a line with the hatch's style. Cap at `ImageConfiguration.MaxHatchLines` (default 20000): beyond the cap, stop and raise a `Warning` notification.

## 5. SVG backend

### 5.1 Coordinate system

- `viewBox="0 0 W H"` where `W`/`H` are the page size in drawing units (`Layout.PaperWidth/PaperHeight`, same values the raster mapping uses). Y is flipped by the render context, so SVG y grows downward like the raster canvas.
- No `width`/`height` attributes by default. When `SvgOptions.EmitSize` is true, emit `width="{Configuration.Width}"` and `height="{Configuration.Height}"` (pixels) so the SVG has an intrinsic size and the browser letterboxes with the default `preserveAspectRatio="xMidYMid meet"`.
- Padding is applied as viewBox margin: the viewBox becomes `-padL -padT (W + padL + padR) (H + padT + padB)` where paddings are converted from pixels to drawing units using the raster fit scale (`min(drawableWidth/W, drawableHeight/H)`), so the framing matches the PNG.

### 5.2 Document structure

```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 W H" [width height]>
  <g class="cad-root">
    <g fill="none" stroke-linecap="round" stroke-linejoin="round" font-family="...">
      <rect class="cad-background" x y width height fill="#rrggbb"/>   <!-- only when background alpha > 0 -->
      <g id="{prefix}layer-{sanitised}" class="cad-layer" data-layer="{raw name}" stroke="#rrggbb" stroke-width="w">
        <path data-handle="1F3" data-type="Line" [data-parent="1A0"] [data-block="DOOR"] d="..."/>
      </g>
    </g>
  </g>
</svg>
```

- One `<g>` per layer, in order of first appearance while drawing. Elements are appended to the group of their **effective** layer (section 4.2), so an Insert's sub-entities land in their own layers' groups. Accepted: this changes z-order relative to entity order.
- Layer group carries the layer's own colour and pixel/unit width as defaults; every element still writes its resolved `stroke`/`stroke-width`/`stroke-dasharray`/`opacity` when they differ from the group default.
- `id` sanitisation: lower-case, characters outside `[a-z0-9_-]` replaced by `-`, collapsed; prefixed by `SvgOptions.IdPrefix` (default empty). `data-layer` carries the raw name.
- Entity attributes (`data-handle` hex upper-case, `data-type` = entity `ObjectName`, `data-parent`, `data-block`) are emitted when `SvgOptions.EmitEntityAttributes` is true (default). `data-handle` is omitted when the handle is 0, which is the case for the transient clones `Insert.Explode()` produces for block contents in ACadSharp 3.7.1; such elements still carry `data-parent` (the insert's handle) and `data-block`.
- Hidden or filtered entities are **omitted**, never written with `display="none"`.
- Background rect only when `BackgroundColor` alpha is greater than 0.
- No XML declaration (inline SVG in HTML must not carry one); files are written as UTF-8 without a BOM.
- Layer group ids are unique per document: `{prefix}layer-{name}` at page level and `{prefix}clip-{n}-layer-{name}` inside viewport `n`.

### 5.3 Primitives

- Line: `<line x1 y1 x2 y2/>`. Polyline: `<polyline points="..."/>` or `<polygon>` when closed. Filled polygon: `<polygon fill=... stroke="none"/>`. Even-odd multi-loop fill: `<path fill-rule="evenodd" d="M...Z M...Z"/>`.
- Arc: `<path d="M x0 y0 A rx ry rot large-arc sweep x1 y1"/>`. Because the context flips Y, a counter-clockwise CAD arc (positive sweep) has `sweep-flag="0"` in SVG space; `large-arc-flag = |sweep| > PI ? 1 : 0`.
- Full circle: `<circle cx cy r/>`; full ellipse: `<ellipse cx cy rx ry transform="rotate(deg cx cy)"/>`; partial ellipse: one `A` command with `rx ry rot` from `MajorAxis`, `MinorAxis`, `Rotation` (parameters converted to points with `PolarCoordinateRelativeToCenter`).
- Polyline bulges: each bulged segment becomes an `A` command. With bulge `b`, chord length `c`, included angle `theta = 4 * atan(|b|)`, radius `r = c / (2 * sin(theta / 2))`, `large-arc = theta > PI`, `sweep-flag = b > 0 ? 0 : 1` after the Y flip.
- Splines: Bezier-form clamped cubic splines (already detected by the existing `SplineRenderer.TryGetBezierSegments`) emit `C` segments directly. Other clamped, non-rational degree-3 splines are converted to Bezier segments by knot insertion (Boehm) until every interior knot has multiplicity 3, then emitted as `C` segments. Everything else uses the existing tessellation and emits a polyline.
- Text: `<text x y font-size font-family text-anchor dominant-baseline [transform="rotate(-deg x y)"] [textLength lengthAdjust="spacingAndGlyphs"]>`. Multi-line MText uses one `<tspan x="{x}" dy="{lineHeight}">` per line. `dominant-baseline` is limited to `alphabetic`, `central`, `hanging`. `text-anchor` from CAD justification. `textLength` only for `TextEntity` with `Fit` or `Aligned` alignment (distance between insert and alignment points). Font stack: `FontFamilyName` followed by `Arial, Helvetica, sans-serif` (duplicates removed).
- Viewports: `<g clip-path="url(#{prefix}clip-{n})">` with `<clipPath id=... clipPathUnits="userSpaceOnUse"><rect .../></clipPath>` in `<defs>`; contents transformed into page units by the viewport render context (no `transform` attribute needed).

### 5.4 Numbers and precision

- `SvgOptions.Precision` (int?, default null = adaptive). Adaptive: `decimals = clamp(4 - floor(log10(max(W, H))), 0, 8)`, giving a resolution of one ten-thousandth of the larger viewBox side.
- All numbers written with `InvariantCulture`, trailing zeros trimmed.
- The adaptive precision applies to coordinates, radii, sizes and the viewBox only. Style scalars (`stroke-width`, `stroke-dasharray` values, `opacity`) always use a fixed 3 decimals, so a 0.25 line weight on a 20 m drawing is not rounded to 0.

### 5.5 Stroke widths

- `SvgOptions.NonScalingStroke` (default true): every stroked element gets `vector-effect="non-scaling-stroke"` and `stroke-width` in **pixels** from `GetLineWeightPixels`.
- When false: `stroke-width` in **drawing units** = millimetres from the line weight table converted by `CadHeader.InsUnits` (`Millimeters` 1, `Centimeters` 0.1, `Meters` 0.001, `Inches` 1/25.4, `Feet` 1/304.8, `Unitless` or unknown treated as millimetres), times `LineWeightScale`. Dash arrays use the same unit.
- Dash arrays follow the width's unit: with `non-scaling-stroke` the browser computes the whole stroke outline, dashes included, in pixel space, so `stroke-dasharray` values are pixels (the SVG page context's `LineTypeScale` is the raster fit scale in pixels per unit). In drawing-unit mode they are drawing units (`LineTypeScale` 1).

### 5.6 Options object

```csharp
public sealed class SvgOptions
{
    public bool NonScalingStroke { get; set; } = true;
    public bool EmitEntityAttributes { get; set; } = true;
    public bool EmitSize { get; set; } = false;
    public string IdPrefix { get; set; } = string.Empty;
    public int? Precision { get; set; }   // null = adaptive; setter validates 0..8
}
```

Exposed as `ImageConfiguration.Svg { get; }` (never null).

## 6. Public API changes

- `ImageExportFormat.Svg` added; extension `.svg`; `TryParse` accepts `svg`.
- `public abstract class RenderedPage : IDisposable { string Name; ImageExportFormat Format; abstract void Save(string path); abstract void Save(Stream stream); }`
- `RenderedImagePage : RenderedPage` keeps `Image<Rgba32> Canvas`; `Save` encodes with the page's `Format` and the quality captured at render time.
- `public sealed class RenderedSvgPage : RenderedPage { string Content; }`.
- `ImageExporter.Render()` becomes `Render(ImageExportFormat format = ImageExportFormat.Png)` returning `IReadOnlyList<RenderedPage>`. `Save(path, format)` calls `Render(format)` and then `page.Save(path)`.
- `ImageConfiguration` additions: `LayerVisibility`, `IncludedLayers` + methods, `ForegroundColor` (`SixLabors.ImageSharp.Color?`, default null), `MinimumDashPixels` (float, default 2), `MaxHatchLines` (int, default 20000), `Svg`.
- `ImagePage.Entities` semantics: contains all added entities; filtering happens at render.

## 7. CLI

- `--format svg` and `.svg` output extension inference.
- `--layer-visibility <all|screen|plot>`.
- `--only-layer <name>` (repeatable).
- `--list-layers`: prints a fixed-width table (name, on, frozen, plottable, colour, lineweight, linetype, entity count in model space) and exits 0 without rendering.
- `--svg-no-scaling-stroke`, `--svg-no-entity-attributes`, `--svg-size`, `--svg-id-prefix <p>`, `--svg-precision <0-8>`.

## 8. Research conclusions that shaped the design

### 8.1 SVG for React consumers (primary sources: MDN, SVG 2, SVGO, SVGR docs)

- Responsive SVG is `viewBox` without `width`/`height`; the host sizes it with CSS.
- SVGR runs SVGO with `preset-default` + `prefixIds`; `cleanupIds` deletes unreferenced ids, `collapseGroups` flattens attribute-free groups, `removeHiddenElems` deletes `display="none"` elements. Only `data-*` attributes survive untouched, so `data-layer` is the durable handle and hidden content must be omitted rather than hidden.
- Three of four popular pan/zoom libraries own a `transform` on a `<g>` inside the SVG; one discards root `<svg>` attributes. Hence the attribute-free `cad-root` group and defaults on an inner group.
- `vector-effect="non-scaling-stroke"` keeps stroke width constant under in-SVG transforms; widths must then be pixel-scale.
- Real `<text>` is selectable and accessible; `textLength` keeps widths stable under font substitution.

### 8.2 SkiaSharp rejected

SkiaSharp's `SKSvgCanvas` emits no consumer-controlled groups, no `viewBox`, no `stroke-dasharray` (dashes are flattened to filled polygons), no way to add ids or data attributes, auto-generated ids that collide across inlined SVGs, and requires a 58 MB native asset package with documented Linux loading failures.

### 8.3 ACadSharp facts verified against 3.7.1

- `Hatch.ExplodePattern()` returns boundary-clipped `Line` entities honouring scale, angle and dash lengths. `Hatch.Explode()` returns only the boundary polylines.
- `Viewport.SelectEntities(bool includePartial = true)`; `Viewport.FrozenLayers` is initialised (non-null) on a new viewport.
- `Hatch.BoundaryPath.GetPoints(int)` returns `IEnumerable<XYZ>`; `IVertex.Location` is a `CSMath.IVector` exposing only an indexer; `XYZ` has no distance helper.
- `DwgReader.Read(string filename, NotificationEventHandler notification = null)` and the `DxfReader` equivalent, so single-argument calls compile.
- `Transparency.Value` is -1 ByLayer, 100 ByBlock, 0..90 percent transparent. `Layer` has no transparency.
- `CadHeader.LineTypeScale`, `CurrentEntityLinetypeScale`, `PaperSpaceLineTypeScaling` (`SpaceLineTypeScaling.Viewport | Normal`), `InsUnits`.
- ImageSharp.Drawing `PatternPen(Color, float strokeWidth, float[] pattern)`: pattern values are multiples of the stroke width. `IImageProcessingContext.Clip(IPath, Action<IImageProcessingContext>)` exists. `ShapeOptions.IntersectionRule` defaults to `EvenOdd`.

## 9. Interface appendix (names every plan must use verbatim)

```csharp
namespace ACadSharp.Image.Rendering;

/// <summary>A point in surface coordinates (pixels for raster, drawing units for SVG). Y grows downward.</summary>
internal readonly record struct SurfacePoint(double X, double Y);

internal readonly record struct SurfaceRect(double X, double Y, double Width, double Height);

/// <summary>Resolved style. Widths and dash lengths are in surface units. DashPattern null means solid. Opacity 0..1.</summary>
internal readonly record struct ImageStyle(
    SixLabors.ImageSharp.Color StrokeColor,
    float StrokeWidth,
    float[]? DashPattern,
    float Opacity)
{
    public ImageStyle(SixLabors.ImageSharp.Color strokeColor, float strokeWidth) : this(strokeColor, strokeWidth, null, 1f) { }
}

internal enum SurfaceTextAnchor { Start, Middle, End }
internal enum SurfaceTextBaseline { Alphabetic, Central, Hanging }

/// <summary>Everything a backend needs to place text. Origin is in surface units; Height in surface units; Rotation in radians, counter-clockwise in drawing space (backends negate because Y is flipped).</summary>
internal sealed record SurfaceText(
    string Text,
    SurfacePoint Origin,
    double Height,
    double Rotation,
    SurfaceTextAnchor Anchor,
    SurfaceTextBaseline Baseline,
    double WrappingWidth,      // <= 0 means no wrapping
    double LineSpacingFactor,  // 1.0 = single
    double FixedLength);       // <= 0 means none; SVG textLength

/// <summary>Identifies the entity being drawn so structured backends can group and tag output.</summary>
internal sealed record EntityRenderInfo(
    string LayerName,
    string EntityType,
    ulong Handle,
    ulong? ParentHandle,
    string? BlockName);

internal sealed record LayerRenderInfo(string LayerName, SixLabors.ImageSharp.Color Color, float StrokeWidth);

/// <summary>Result of opening a viewport: the surface to draw into and where its origin sits.</summary>
internal readonly record struct ViewportSurface(IDrawingSurface Surface, double OffsetX, double BottomY);

internal interface IDrawingSurface : IDisposable
{
    /// <summary>True when the backend draws arcs, ellipses and bulges natively; false when it wants tessellated polylines.</summary>
    bool SupportsCurves { get; }

    void BeginEntity(EntityRenderInfo info, LayerRenderInfo layer);
    void EndEntity();

    void DrawLine(ImageStyle style, SurfacePoint start, SurfacePoint end);
    void DrawPolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, bool closed);
    /// <summary>Arc of an ellipse. Angles in radians in surface space (already sign-adjusted for the Y flip). Rotation in radians in surface space.</summary>
    void DrawArc(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation, double startAngle, double sweepAngle);
    void DrawEllipse(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation);
    /// <summary>Cubic Bezier chain: 3n+1 points.</summary>
    void DrawCubicBezier(ImageStyle style, IReadOnlyList<SurfacePoint> controlPoints, bool closed);
    /// <summary>Polyline whose segments may be arcs. Bulges[i] applies to segment i (from points[i] to points[i+1]); 0 = straight.</summary>
    void DrawBulgePolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, IReadOnlyList<double> bulges, bool closed);
    void FillPolygon(ImageStyle style, IReadOnlyList<SurfacePoint> points);
    /// <summary>Even-odd fill of several rings.</summary>
    void FillPath(ImageStyle style, IReadOnlyList<IReadOnlyList<SurfacePoint>> rings);
    void FillCircle(ImageStyle style, SurfacePoint center, double radius);
    void DrawText(ImageStyle style, SurfaceText text);

    /// <summary>Opens a clipped viewport region. <paramref name="bounds"/> is the viewport rectangle in this surface's units.</summary>
    ViewportSurface BeginViewport(SurfaceRect bounds);
    void EndViewport(ViewportSurface viewport);
}
```

`ImageRenderContext` (internal) becomes backend-neutral:

```csharp
internal sealed class ImageRenderContext
{
    public IDrawingSurface Surface { get; }
    public ImageConfiguration Configuration { get; }
    public Layout Layout { get; }
    public double SurfaceWidth { get; }
    public double SurfaceHeight { get; }
    public double OriginX { get; }
    public double OriginY { get; }
    public double Scale { get; }                 // surface units per drawing unit
    public double OffsetX { get; }
    public double OffsetY { get; }
    public double LineTypeScale { get; }         // surface units per linetype unit (section 4.4)
    public Viewport? Viewport { get; }           // non-null while drawing viewport contents
    public ImageRenderContext? Parent { get; }

    public SurfacePoint ToSurfacePoint(XY point);   // x = OffsetX + (p.X - OriginX) * Scale; y = SurfaceHeight - OffsetY - (p.Y - OriginY) * Scale
    public SurfacePoint ToSurfacePoint(XYZ point);
    public double ToSurfaceLength(double value);     // value * Scale
    public float ToStrokeWidth(LineWeightType lineWeight);  // raster: GetLineWeightPixels; SVG: px or drawing units per section 5.5
}
```

Public additions (namespace `ACadSharp.Image`): `LayerVisibilityMode`, `SvgOptions`, `RenderedPage`, `RenderedSvgPage`, `ImageExportFormat.Svg`, and the `ImageConfiguration` members listed in section 6.

Internal additions: `RasterDrawingSurface` (ImageSharp), `SvgDrawingSurface`, `EntityVisibilityFilter`, `LineTypeDashResolver`, `SplineBezierConverter`, `SvgNumberFormatter`, `SvgIdSanitizer`.
