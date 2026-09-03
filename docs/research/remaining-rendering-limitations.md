# Remaining rendering limitations: 3DFACE, MLINE, WIPEOUT, LEADER, insert attributes, explode pairing, raster text sizing

- Date: 2026-09-03
- Branch: `mubeda/svg-support`
- HEAD: `8ec289411faa195d0ee4a5c9b2376c2764b17c3c`
- Method: repository source read directly (line numbers are from the working tree at HEAD); ACadSharp 3.7.1 public surface obtained by **reflecting the pinned assembly** `~/.nuget/packages/acadsharp/3.7.1/lib/net10.0/ACadSharp.dll` and by reading its XML docs (`.../ACadSharp.xml`) — claims from that route are marked *(verified in package)*; method bodies read from the tagged GitHub source `DomCR/ACadSharp` at `v3.7.1` — marked *(v3.7.1 source)*; unreleased fixes read at `master` — marked *(main)*; behaviour confirmed by running throwaway probe programs against the pinned package — marked *(probe)*. DXF semantics from Autodesk's published *AutoCAD 2012 DXF Reference* PDF. SixLabors.Fonts behaviour from the pinned package XML docs, the tagged `v2.1.3` source, and measurements made against the pinned assembly.

## Summary

| Topic | Verdict | Effort | Touches a baseline/golden? |
| --- | --- | --- | --- |
| 3DFACE (`Face3D`) | Fully supportable today; corners are WCS, four edges with per-edge invisibility | **S** | No (no sample contains one) |
| MLINE (`MLine`) | Supportable from the stored per-vertex parameters; `MLineStyle` **is** resolved by both readers | **M** | No |
| WIPEOUT (`Wipeout`) | Supportable as an opaque polygon; boundary needs a pixel→WCS mapping the library does not provide | **M** | No |
| LEADER (`Leader`) | Supportable as polyline/spline + arrowhead; hookline is already in `Vertices` | **M** | No |
| INSERT attributes (`Insert.Attributes`) | Not drawn at all today; they are ordinary `TextEntity` geometry in absolute coordinates | **S** | No sample has one; the `features` golden would change only if the synthetic sample gains attributes |
| Original↔clone pairing after `Insert.Explode()` | Order **is** structurally guaranteed in 3.7.1; handles are not usable; no newer ACadSharp exists | **S** (hardening only) | No |
| Raster text size depends on `Dpi` | Real; fix is `TextOptions.Dpi = 72` with size in ems | **S** | Arithmetically a no-op at the default `Dpi = 96`; float rounding differs — **run the golden suite** |
| Single-line raster text shift at `LineSpacing ≠ 1` | Compensation is exact only at factor 1; wrong for MTEXT with a non-unit line-spacing factor | **S** | No golden uses a non-unit factor |
| *(incidental)* ATTDEF default values are drawn inside every `Insert` | Existing bug | **S** | No sample has an ATTDEF |
| *(incidental)* `MLine.Clone()` destroys the source MLINE in 3.7.1 | Upstream bug, fixed on `main`, still broken for segments | — | No |

---

## 1. Unimplemented entity types

All four fall through `EntityRenderDispatcher.Draw`'s `default:` arm and raise a `NotImplemented` notification (`ACadSharp.Image/Rendering/EntityRenderDispatcher.cs:153-155`). None of them appears in any drawing under `Samples/`: a probe that read all three sample files and grouped every block's entities by type found only `Line`, `Arc`, `TextEntity`, `DimensionAngular3Pt` (`Samples/6-57-1119.dxf`), `Line`, `Arc`, `Point`, `Solid`, `MText`, `DimensionLinear`, `Circle`, `Viewport` (`Samples/HSK80AHCP16190M_BMG.dwg`), and `Spline` (`Samples/Subaru Logo Vector Free Wrap.dxf`) *(probe)*. So none of the four sections below can move an existing PNG baseline or SVG golden.

### 1.1 3DFACE (`AcDbFace`)

**What ACadSharp 3.7.1 exposes.** `ACadSharp.Entities.Face3D : Entity`, with `XYZ FirstCorner/SecondCorner/ThirdCorner/FourthCorner`, `InvisibleEdgeFlags Flags`, `ApplyTransform`, `GetBoundingBox` *(verified in package)*. `InvisibleEdgeFlags` is `[Flags]`-shaped with `None = 0, First = 1, Second = 2, Third = 4, Fourth = 8` *(verified in package)*. It does **not** implement `IOrientable` and has no `Normal` property — its corners are world coordinates and need no OCS step, unlike `Solid` (contrast `EntityRenderDispatcher.DrawSolid`, `:239-255`, which does apply `OcsTransform`). `ApplyTransform` simply maps all four corners *(v3.7.1 source, `Entities/Face3D.cs:69-75`)*.

**DXF semantics.** Group 10/11/12/13 are the first…fourth corner, each *"(in WCS)"*; *"If only three corners are entered, this [the fourth] is the same as the third corner"*; group 70 is *"Invisible edge flags (optional; default = 0): 1 = First edge is invisible / 2 = Second edge is invisible / 4 = Third edge is invisible / 8 = Fourth edge is invisible"* (DXF Reference, *3dface group codes*). Edge *n* runs from corner *n* to corner *n+1*, with edge 4 closing corner 4 back to corner 1.

**Rendering approach (both backends).** Project the four corners with `context.ToSurfacePoint(...)` and emit the visible edges only. Because per-edge visibility breaks the ring, this cannot go through `IDrawingSurface.DrawPolyline` as one call in the general case:

- Build the four edges `(1→2, 2→3, 3→4, 4→1)`, drop each one whose flag bit is set, and drop the degenerate `3→4` edge when `FourthCorner == ThirdCorner` (the documented triangle encoding).
- Emit maximal runs of consecutive kept edges as `DrawPolyline(..., closed: false)`, and a single `DrawPolyline(..., closed: true)` in the common `Flags == None` case so the SVG gets one `<polygon>` rather than four `<line>`s (`SvgDrawingSurface.DrawPolyline` picks `polygon`/`polyline` from the `closed` argument, `ACadSharp.Image/Rendering/Svg/SvgDrawingSurface.cs`).
- Do **not** `FillPolygon`: a 3DFACE plots as edges in a 2D/wireframe view, which is the only view this renderer produces, and per-edge invisibility only makes sense for a wireframe. (Not primary-sourced; see [section 7](#7-not-verified).) Nothing else is backend-specific; both surfaces already implement `DrawPolyline`.

**Gaps.** None. 3DFACE is the cheapest of the four.

**Recommendation — effort S, no baseline impact.** Add a `case Face3D face:` above the `default:` arm and a `DrawFace3D` helper next to `DrawSolid`. Cover it with a `RecordingDrawingSurface` unit test per flag combination, plus one entity in the synthetic `features` sample if a visual golden is wanted (that *would* rewrite `features.model.01.png/.svg`).

### 1.2 MLINE (`AcDbMline`)

**What ACadSharp 3.7.1 exposes.** `ACadSharp.Entities.MLine : Entity, IOrientable` with `MLineFlags Flags` (`Has = 1, Closed = 2, NoStartCaps = 4, NoEndCaps = 8`), `MLineJustification Justification` (`Top = 0, Zero = 1, Bottom = 2`), `XYZ Normal`, `double ScaleFactor`, `XYZ StartPoint`, `MLineStyle Style`, `List<MLine.Vertex> Vertices` *(verified in package)*. `MLine.Vertex` has `XYZ Position`, `XYZ Direction` ("Direction vector of segment starting at this vertex"), `XYZ Miter` ("Direction vector of miter at this vertex") and `List<Vertex.Segment> Segments` ("Segments in MLineStyle definition"); `Vertex.Segment` has `List<double> Parameters` ("Element parameters") and `List<double> AreaFillParameters` *(verified in package; XML docs)*. `ACadSharp.Objects.MLineStyle : NonGraphicalObject` has `IEnumerable<Element> Elements`, `Color FillColor`, `MLineStyleFlags Flags` (`FillOn = 1, DisplayJoints = 2, StartSquareCap = 16, StartInnerArcsCap = 32, StartRoundCap = 64, EndSquareCap = 256, EndInnerArcsCap = 512, EndRoundCap = 1024`), `StartAngle`/`EndAngle`, and `MLineStyle.Element` has `double Offset`, `Color Color`, `LineType LineType` *(verified in package)*. `MLine.Style` can never be null: the setter throws on null and the field is initialised to `MLineStyle.Default` *(v3.7.1 source, `Entities/MLine.cs:63-93`)*; `MLineStyle.Default` is named `Standard` and carries 2 elements *(probe)*.

**Is `MLineStyle` resolved?** Yes — this is *not* a gap. A probe authored an `MLine` with a custom three-element `MLineStyle` (offsets `0.5 / 0 / -0.5`), wrote it to DXF and to DWG with ACadSharp's own writers, read both back, and in both cases got `style='PROBE3' elements=3 offsets=[0.5,0,-0.5] scale=2 just=Zero verts=3` *(probe)*. The 340 handle reference is declared on the property (`[DxfCodeValue(DxfReferenceType.Handle | DxfReferenceType.Name, 340)]`, *v3.7.1 source*), which is what the DXF Reference says to use: *"The correct fields to modify are as follows: Mline The 340 group in the same object, which indicates the proper MLINESTYLE object"*.

**DXF semantics — the important part.** The offsets are **already baked into each vertex**, so justification and scale do not have to be re-derived. From the DXF Reference (*Mline group codes*, prose after the table):

> The group code 41 parameterization is a list of real values, one real per group code 41. … The first group code 41 value is the distance from the segment vertex along the miter vector to the point where the line element's path intersects the miter vector. The next group code 41 value is the distance along the line element's path from the point defined by the first group 41 to the actual start of the line element. The next is the distance from the start of the line element to the first break (or cut) in the line element. The successive group code 41 values continue to list the start and stop points of the line element in this segment of the mline. Linetypes do not affect group 41 lists.

Group 41 is ACadSharp's `Vertex.Segments[j].Parameters`, and `Segments[j]` corresponds to element *j* of the style (group 73 is *"Number of elements in MLINESTYLE definition"*). Group 42 (`AreaFillParameters`) describes the fill-area boundary the same way. Group 71 bit 2 is `Closed`, bits 4/8 suppress the start/end caps. Group 70 justification and group 40 scale are the authoring inputs that produced those parameters; re-applying them would double-count.

**Rendering approach (both backends).**

- For element *j*, walk the vertices; at vertex *i* the element's line passes through `Vertices[i].Position + Segments[j].Parameters[0] * Vertices[i].Miter`. Join consecutive vertices with `DrawPolyline` (closed when `Flags.HasFlag(MLineFlags.Closed)`), one polyline per style element. This is a plain polyline on both surfaces.
- The remaining group-41 values are **breaks**, not decoration: per the quoted prose, `Parameters[1]` is the distance along the element's path to where the line actually starts, and the values after it alternate stop/start of the cut segments (what `MLEDIT` writes when one mline crosses another). Ignoring them draws a solid line straight through every gap. A first version can honour `Parameters[0]` only and `Notify` when `Parameters.Count > 2`; a complete one walks the pairs and emits one polyline per surviving run.
- Style each element from `MLineStyle.Element.Color` and `.LineType` rather than the entity's resolved style: build `ImageStyle` from the element colour (`ColorExtensions.ToImageColor(foreground)`) and, for dashes, the existing `LineTypeDashResolver` (`ACadSharp.Image/Rendering/LineTypeDashResolver.cs`). Keep the entity's resolved stroke width and opacity.
- Fill: when `Style.Flags.HasFlag(MLineStyleFlags.FillOn)`, fill the ring formed by the outermost two elements with `Style.FillColor` via `FillPolygon`, before stroking, so the strokes stay on top.
- Caps and joints (`StartRoundCap`, `DisplayJoints`, …) are cosmetic; a first version can skip them and add a straight cap segment between the outermost elements when the corresponding suppress flag is absent.
- **Fallback**: when `Vertices[i].Segments` is empty (some writers omit group 74/41), fall back to computing offsets from the style: element offset `o_j`, scale `ScaleFactor`, and a justification shift of `-max(o)` for `Top`, `0` for `Zero`, `-min(o)` for `Bottom`, applied along `Miter`. Say so in a `Notify` warning so the approximation is visible.

**Gaps to be aware of.** `MLine.Clone()` in 3.7.1 is destructive: it calls `base.Clone()` (a `MemberwiseClone`, so `clone.Vertices` is the *same* `List<Vertex>` instance) and then `clone.Vertices.Clear()` *(v3.7.1 source, `Entities/MLine.cs:108-121`)*. A probe confirms it: a source MLINE with one vertex has **zero** vertices after `Clone()`, and so does the clone *(probe)*. This fires whenever an MLINE lives inside a block, because `Insert.Explode()` calls `CloneTyped()` on every block entity (see [section 3](#3-originaltoclone-pairing-after-insertexplode)) — so exploding an insert silently empties the block's MLINE. On `main` the entity-level bug is fixed (`clone.Vertices = new List<Vertex>()`), but `MLine.Vertex.cs` is byte-identical to v3.7.1 and still does `clone.Segments.Clear()` on a memberwise-shared list, so the per-vertex `Segments` are still destroyed *(main)*. **Practical consequence:** render MLINEs from the top-level entity, and for MLINEs inside blocks either skip them with a warning or read them from `insert.Block.Entities` *before* enumerating `Explode()`.

**Recommendation — effort M, no baseline impact.** Implement the parameter-driven path plus the style-offset fallback; guard against the clone bug. Unit-test with `RecordingDrawingSurface` against a hand-built `MLine` (offsets, closed flag, fill on/off).

### 1.3 WIPEOUT (`AcDbWipeout`)

**What ACadSharp 3.7.1 exposes.** `ACadSharp.Entities.Wipeout : CadWipeoutBase : Entity` — `CadWipeoutBase` is documented as the *"Common base class for `RasterImage` and `Wipeout`"* and carries `List<XY> ClipBoundaryVertices`, `ClipType ClipType` (`Rectangular = 1, Polygonal = 2`), `bool ClippingState`, `ClipMode ClipMode` (`Outside = 0, Inside = 1`), `XYZ InsertPoint`, `XYZ UVector`, `XYZ VVector`, `XY Size`, `ImageDisplayFlags Flags` (`ShowImage = 1, ShowNotAlignedImage = 2, UseClippingBoundary = 4, TransparencyIsOn = 8`), `bool ShowImage`, `byte Brightness/Contrast/Fade`, `ImageDefinition Definition` *(verified in package)*.

**Is it read?** Yes, on both formats. The DXF reader has `case DxfFileToken.EntityWipeout: return this.readEntityCodes<Wipeout>(new CadWipeoutBaseTemplate(new Wipeout()), this.readWipeoutBase);` and the sibling `EntityImage → RasterImage` *(v3.7.1 source, `IO/DXF/DxfStreamReader/DxfSectionReaderBase.cs:231-234`, boundary vertices appended at `:1531`)*; the DWG reader has `case "WIPEOUT": template = this.readCadImage(new Wipeout());` next to `case "IMAGE": … new RasterImage()` *(v3.7.1 source, `IO/DWG/DwgStreamReaders/DwgObjectReader.cs:5608,5655`, shared body at `:1217-1233`)*. A write→read round trip could **not** be used to confirm this end-to-end: a `Wipeout` authored in memory did not survive ACadSharp's own DXF or DWG writers *(probe)*, i.e. the *write* path drops it. That does not affect rendering, which only reads.

**DXF semantics.** WIPEOUT's subclass marker is `AcDbRasterImage` and its group codes are the IMAGE codes (DXF Reference, *Wipeout group codes* and *Image group codes*):

- 10 = *"Insertion point (in WCS)"*.
- 11 = *"U-vector of a single pixel (points along the visual bottom of the image, starting at the insertion point) (in WCS)"*.
- 12 = *"V-vector of a single pixel (points along the visual left side of the image, starting at the insertion point) (in WCS)"*.
- 13/23 = *"Image size in pixels"*.
- 71 = *"Clipping boundary type. 1 = Rectangular; 2 = Polygonal"*; 91 = vertex count; 14/24 = *"Clip boundary vertex (in OCS)"*, with the note *"1) For rectangular clip boundary type, two opposite corners must be specified. Default is (-0.5,-0.5), (size.x-0.5, size.y-0.5). 2) For polygonal clip boundary type, three or more vertices must be specified. Polygonal vertices must be listed sequentially"*.
- 70 = display flags (`1 = Show image`, `4 = Use clipping boundary`); 280 = clipping state; 290 = clip mode (Outside/Inside).

So the boundary vertices are in **pixel space**, not drawing units, and the mapping to world is `world(p) = InsertPoint + (p.X + 0.5) · UVector + (p.Y + 0.5) · VVector`. The `+0.5` follows from the documented default rectangular boundary: `(-0.5, -0.5)` then maps to `InsertPoint` and `(size.x-0.5, size.y-0.5)` to `InsertPoint + size.x·U + size.y·V`, i.e. exactly the image extent.

**ACadSharp gap.** `CadWipeoutBase.GetBoundingBox()` does **not** perform this mapping — it takes the min/max of the raw boundary vertices and adds `InsertPoint`, ignoring `UVector`, `VVector` and the half-pixel offset *(v3.7.1 source, `Entities/CadWipeoutBase.cs:230-244`; unchanged on `main`)*. It is therefore only correct when the pixel vectors happen to be the unit axes. The renderer must do the mapping itself; it must **not** rely on `GetBoundingBox()` for wipeout extents.

**Rendering approach (both backends).**

- **Visibility.** `Flags` decides whether anything is painted: no `ImageDisplayFlags.ShowImage` (group 70 bit 1) means draw nothing. `ClippingState` (group 280) does **not** hide the entity — it selects *which* region is painted.
- **Region.** When `ClippingState` is true, use `ClipBoundaryVertices`, expanding a `Rectangular` pair of opposite corners into four pixel-space corners first; a `Polygonal` boundary is used as listed. When `ClippingState` is false, use the full frame `(-0.5, -0.5) … (Size.X - 0.5, Size.Y - 0.5)`, which is the documented default boundary.
- **`ClipMode`.** `ClipMode.Inside` (group 290 = 1) inverts the region — everything *outside* the boundary is painted. That needs an even-odd fill of the frame minus the boundary, or a clip; the honest first version raises a `Notify` and skips, rather than filling the wrong half.
- Map every vertex with the formula above, project through `context.ToSurfacePoint`, and call `IDrawingSurface.FillPolygon` with a style whose colour is the page background and whose opacity is forced to 1: `style with { StrokeColor = configuration.BackgroundColor, Opacity = 1f }`. The `Opacity` reset matters — `ImageStyle.EffectiveColor` applies the entity's resolved transparency to the alpha channel (`ACadSharp.Image/Rendering/ImageStyle.cs:16-26`), and a translucent wipeout would not occlude. `FillPolygon` uses `style.EffectiveColor` on both surfaces (`RasterDrawingSurface.cs:137-147` and the SVG `FillPolygon`).
- **Draw order matters, and this repo's order is not ACadSharp's default.** `ImagePage` materialises entities in `block.Entities` order (`ACadSharp.Image/ImagePage.cs:95,105`; layouts via `ImageExporter.cs:110`), i.e. DXF file order. ACadSharp's own `BlockRecord.GetSortedEntities()` returns `this.Entities.OrderBy(e => e.Handle)` when there is no sort table and applies the `SortEntitiesTable` (DRAWORDER) when there is one *(v3.7.1 source, `Tables/BlockRecord.cs:243-251,470-486`)*. File order and handle order usually coincide, but nothing guarantees it, and DRAWORDER is ignored entirely today. Since a wipeout is only correct if everything it should hide is drawn *before* it, the WIPEOUT work should switch `ImagePage`'s two `block.Entities` loops to `block.GetSortedEntities()` — one change that fixes both the default ordering and DRAWORDER. That is the part of this item that **can** move existing baselines, so measure it separately.
- **Transparent backgrounds break the effect.** `SvgDrawingSurface` emits its `cad-background` `<rect>` only when `configuration.BackgroundColor` has non-zero alpha (`SvgDrawingSurface.cs:62-79`), and a fill with alpha 0 paints nothing on either backend. With `BackgroundColor = Transparent` a wipeout therefore cannot occlude. Options: paint it with `ResolveForegroundColor()`'s complement, or add an explicit `WipeoutColor` configuration knob defaulting to `BackgroundColor`. Document whichever is chosen.
- **Frame.** AutoCAD's WIPEOUTFRAME system variable controls whether the boundary is stroked. ACadSharp 3.7.1's `CadHeader` exposes no `WipeoutFrame`/`WIPEOUTFRAME` member *(verified in package — no such member in the reflected `CadHeader` surface or the XML docs)*, and the DXF Reference documents the frame setting on the `WIPEOUTVARIABLES` object, not on the entity. Simplest correct default: **do not** draw the frame (matching `WIPEOUTFRAME = 0`, AutoCAD's plot behaviour), and expose an opt-in flag if a frame is ever wanted.

**Recommendation — effort M, no baseline impact for the entity itself; the draw-order change is separate and *does* risk moving baselines.** Implement the wipeout as an opaque background-coloured `FillPolygon` with the pixel→WCS mapping written locally, no frame, and an explicit decision about transparent backgrounds; unit-test the mapping against the documented default boundary. Land the `GetSortedEntities()` switch as its own commit and re-run the golden suite for it.

### 1.4 LEADER (`AcDbLeader`)

**What ACadSharp 3.7.1 exposes.** `ACadSharp.Entities.Leader : Entity, IOrientable` with `List<XYZ> Vertices`, `bool ArrowHeadEnabled`, `LeaderPathType PathType` (`StraightLineSegments = 0, Spline = 1`), `bool HasHookline` *(get-only)*, `HookLineDirection HookLineDirection` (`Opposite = 0, Same = 1`), `XYZ HorizontalDirection`, `XYZ Normal`, `DimensionStyle Style`, `Entity AssociatedAnnotation`, `LeaderCreationType CreationType` (`CreatedWithTextAnnotation = 0, CreatedWithToleranceAnnotation = 1, CreatedWithBlockReferenceAnnotation = 2, CreatedWithoutAnnotation = 3`), `XYZ AnnotationOffset`, `XYZ BlockOffset`, `double TextHeight`, `double TextWidth`, plus `SetDimensionOverride`/`SetStyleOverrideMap` *(verified in package)*. `Style` defaults to `DimensionStyle.Default` and cannot be set to null *(v3.7.1 source, `Entities/Leader.cs:113-128`)*; `DimensionStyle.Default.ArrowSize` is `0.18`, `ScaleFactor` is `1`, `LeaderArrow` is `null` *(probe)*. `DimensionStyle.ArrowSize` is documented as *"Controls the size of dimension line and leader line arrowheads. Also controls the size of hook lines (see DIMASZ System Variable)"* and `DimensionStyle.LeaderArrow` as *"Specifies the arrow type for leaders (see DIMLDRBLK System Variable). A [BlockRecord] that makes up an arrowhead or null if the default, closed-filled arrowhead is to be displayed"* *(XML docs)*.

**DXF semantics.** From the DXF Reference (*Leader group codes*): 3 = dimension style name; 71 = *"Arrowhead flag: 0 = Disabled; 1 = Enabled"*; 72 = *"Leader path type: 0 = Straight line segments; 1 = Spline"*; 73 = creation flag; 74 = hookline direction; 75 = *"Hookline flag: 0 = No hookline; 1 = Has a hookline"*; 40/41 = text annotation height/width; 76 = vertex count; 10/20/30 = *"Vertex coordinates (one entry for each vertex)"* with no OCS qualifier, i.e. WCS; 340 = *"Hard reference to associated annotation (mtext, tolerance, or insert entity)"*; 211 = the "horizontal" direction; 212/213 = offsets of the last vertex from the block/annotation placement point.

Two consequences: **the hookline is already one of the stored vertices** (it is the last, horizontal, segment — no extra geometry to synthesise), and **the annotation is a separate entity** that the page already draws in its own right, so a LEADER renderer must not draw `AssociatedAnnotation` again.

**ACadSharp gap.** `Leader.HasHookline` is a computed getter — it returns whether the vector from the second-to-last to the last vertex is parallel to `HorizontalDirection` — with no setter, even though it carries `[DxfCodeValue(75)]` *(v3.7.1 source, `Entities/Leader.cs:61-72`)*. The file's stored group-75 flag is therefore discarded and re-derived. Harmless for rendering (the geometry is in `Vertices` either way), but do not treat `HasHookline` as file data.

**Rendering approach (both backends).**

- `PathType.StraightLineSegments`: project `Vertices` and call `DrawPolyline(..., closed: false)`. Nothing backend-specific.
- `PathType.Spline`: the vertices are the fit points of a splined leader. Build a transient `Spline { FitPoints = Vertices, Degree = 3 }` and hand it to the existing `SplineRenderer`, which already has a fit-points path — it falls back to *"Spline has fit points but no control points; drawn as a polyline through its fit points"* (`ACadSharp.Image/Rendering/SplineRenderer.cs:64-77`), because ACadSharp 3.7.1's `UpdateFromFitPoints` fills the knot vector but no control points. That is a coarse but honest rendering; alternatively emit a Catmull-Rom→cubic-Bézier chain through the fit points and call `DrawCubicBezier` on surfaces where `SupportsCurves` is true (SVG), which gives the SVG a real `<path>` and costs little.
- Arrowhead: only when `ArrowHeadEnabled`. Size `= Style.ArrowSize * (Style.ScaleFactor > 0 ? Style.ScaleFactor : 1)` in drawing units, converted with `context.ToSurfaceLength`. Direction is `Vertices[0] - Vertices[1]` (the arrow sits at the first vertex, pointing at the thing being annotated). When `Style.LeaderArrow` is null, draw AutoCAD's default *closed filled* arrowhead: an isosceles triangle of length `size` and half-width `size/6` (AutoCAD's built-in ratio), filled with `FillPolygon` using the entity's resolved style. When `LeaderArrow` is a `BlockRecord`, either render the block scaled to `ArrowSize` (an `Insert`-like path) or fall back to the default triangle with a `Notify` — the latter is the cheaper first version.
- `HookLineDirection` and `AnnotationOffset`/`BlockOffset` need no geometry of their own; they only describe where the (separately drawn) annotation sits.

**Recommendation — effort M, no baseline impact.** Straight-line path plus default filled arrowhead first (that covers the overwhelming majority of LEADERs); spline path via `SplineRenderer`; custom arrow blocks deferred behind a notification.

---

## 2. INSERT attributes (ATTRIB)

**Does ACadSharp expose them?** Yes. `Insert.Attributes` is `SeqendCollection<AttributeEntity>` with a public getter, documented as *"Attributes from the block reference. If an attribute should be added in this collection a definition will be added into the block reference as well"*; `Insert.HasAttributes` is *"True if the insert has attribute entities in it"* *(verified in package; XML docs)*. The type chain is `AttributeEntity : AttributeBase : TextEntity : Entity`, and `AttributeEntity` implements `IText` *(verified in package)*. `AttributeBase` adds `AttributeType AttributeType` (`SingleLine = 1, MultiLine = 2, ConstantMultiLine = 4`), `AttributeFlags Flags` (`None = 0, Hidden = 1, Constant = 2, Verify = 4, Preset = 8`), `bool IsLocked`, `MText MText`, `string Tag`, `byte Version`, and an **`override`** of `TextEntity.VerticalAlignment` *(verified in package; `override`, not `new`, confirmed at `Entities/AttributeBase.cs:63-64` against `virtual` at `Entities/TextEntity.cs:135` — v3.7.1 source)*. That last point matters: reading `VerticalAlignment` through a `TextEntity` reference yields the ATTRIB's own value, so `TextRenderer.Draw(ImageRenderContext, ImageStyle, TextEntity, Transform?)` works unchanged on an `AttributeEntity`.

**Are they in world coordinates?** Yes — they need **no** insert transform. The DXF Reference's *Attrib group codes* give ATTRIB the subclass chain `AcDbText` then `AcDbAttribute`, with 10 = *"Text start point (in OCS)"*, 11 = *"Alignment point (in OCS) (optional) … Present only if 72 or 74 group is present and nonzero"*, 40 = text height, 50 = rotation, 72/74 = horizontal/vertical justification *"See TEXT … group codes"*, 210 = extrusion. Those are absolute coordinates in the ATTRIB's own OCS, exactly like a TEXT entity — the insert's translation, rotation and scale are already applied by whatever wrote the file. So: pass `placement: null` to `TextRenderer.Draw`. The existing OCS handling in `TextRenderer.Draw` (`ACadSharp.Image/Rendering/TextRenderer.cs:86-89`) is exactly what ATTRIB needs.

**Which are invisible?**

- **`AttributeFlags.Hidden`** (group 70 bit 1, *"Attribute is invisible (does not appear)"*): skip the attribute.
- **ATTMODE.** `CadHeader.AttributeVisibility` is documented as *"Controls display of attributes. System variable ATTMODE"* and is of type `ACadSharp.Header.AttributeVisibilityMode` with `None = 0, Normal = 1, All = 2` *(verified in package)*, matching the DXF Reference's HEADER entry: *"`$ATTMODE` 70 Attribute visibility: 0 = None; 1 = Normal; 2 = All"* (DXF Reference, *Header Variables*). `None` hides every attribute; `Normal` honours the per-attribute `Hidden` flag; `All` shows every attribute including hidden ones. This is a per-document setting, so it belongs alongside `ImageConfiguration.LayerVisibility` — under `LayerVisibilityMode.All` the renderer already ignores drawing visibility (`ACadSharp.Image/Rendering/EntityVisibilityFilter.cs:38-42`), and ATTMODE should be treated the same way.
- `AttributeFlags.Constant` does **not** hide anything; constant attributes have no ATTRIB at all — they stay as ATTDEFs in the block (see the incidental finding in [section 5](#5-incidental-findings)).

**Rendering approach.** In `EntityRenderDispatcher.DrawBlockContents` (`:393-413`), after the `Explode()` loop, iterate `insert.Attributes` and call `this.Draw(context, attribute, layer, insert.Handle, insert.Block?.Name, parent)` — no `textSource`, no `placement`. The `case TextEntity textEntity:` arm (`:141-143`) then routes it to `TextRenderer`, and `BeginEntity`/`EndEntity` give the SVG the same per-entity group structure as any other text. Filter on `Flags.HasFlag(AttributeFlags.Hidden)` plus the document's ATTMODE. `AttributeBase.MText` is populated for multi-line attributes (`AttributeType.MultiLine`); prefer it over `Value` when non-null so a multi-line attribute goes through the MTEXT path.

**Gotcha when writing the test fixture.** `Insert(BlockRecord block)` already creates one `AttributeEntity` per `block.AttributeDefinitions` and calls `att.ApplyTransform(this.GetTransform())` inside the constructor *(v3.7.1 source, `Entities/Insert.cs:225-246`)*. With C# object-initializer syntax the constructor runs **before** `InsertPoint`/`Rotation`/`XScale` are assigned, so those attributes are transformed by the identity; a later `UpdateAttributes()` sees the tags already present and does nothing (`Insert.cs:411-436`). Set the placement properties first, or position the attributes explicitly, when building a synthetic insert-with-attributes for a test.

**Recommendation — effort S, changes no existing baseline.** No `Samples/` drawing contains an `Insert` at all, let alone one with attributes *(probe)*; the `features` synthetic sample has one `Insert` (`ACadSharp.Image.Tests/SyntheticSamples.cs:118`) with no attributes, so `features.model.01.png/.svg` stay byte-identical unless the sample is extended (which would be a deliberate golden rewrite).

---

## 3. Original-to-clone pairing after `Insert.Explode()`

**Is the order guaranteed in 3.7.1?** Yes, structurally — the pairing in `EntityRenderDispatcher.DrawBlockContents` (`:402-412`) is sound. `Explode()` is a single `foreach` over `this.Block.Entities` that yields exactly one entity per source entity, in order, with no filtering and no fan-out:

```csharp
// v3.7.1 source, src/ACadSharp/Entities/Insert.cs:320-359
public IEnumerable<Entity> Explode()
{
    Transform transform = this.GetTransform();
    foreach (var e in this.Block.Entities)
    {
        Entity c;
        switch (e)
        {
            case Arc arc:   /* … builds a new Arc from transformed end vertices … */ yield return a; continue;
            case Circle circle: c = new Ellipse() { … }; c.MatchProperties(e); break;
            default: c = e.CloneTyped(); break;
        }
        c.ApplyTransform(transform);
        yield return c;
    }
}
```

A probe over a five-entity block (`Line`, `Circle`, `Arc`, `TextEntity`, `AttributeDefinition`) returned exactly five clones in the same positions, with `Circle → Ellipse` being the only type change *(probe)*. `BlockRecord.Entities` is a `CadObjectCollection<Entity>` — an insertion-ordered collection, and the *same* collection object is enumerated by both `insert.Block.Entities.ToList()` and `Explode()`, so index *i* is the same entity on both sides by construction.

**Is a handle-independent pairing possible?** No, not on the clones. `CadObject.Clone()` is `MemberwiseClone()` followed by `clone.Handle = 0; clone.Document = null; clone.Owner = null;` *(v3.7.1 source, `CadObject.cs:140-155`)*, and the probe confirms every exploded clone has `handle=0` *(probe)*. Handle-based pairing is therefore impossible.

**Is there a more robust approach?** Yes, and it is strictly better than pairing: **stop using `Explode()` for the entity types the renderer already special-cases, and drive them from the originals.** The renderer only needs the clones' *properties* (which come from `MatchProperties`/`CloneTyped`) plus the transform; it already ignores the clones' geometry for TEXT and MTEXT and re-derives it from the original plus `insert.GetTransform()` (`TextRenderer.Draw`, `:23-25` and `:72-75`). Two safe refactors:

1. **Iterate `insert.Block.Entities` directly** and, for each entity, either draw it from the original + `GetTransform()` (TEXT, MTEXT — already the case) or draw a `CloneTyped()` + `ApplyTransform(transform)` of just that one entity. This removes the index bookkeeping entirely and removes the dependency on `Explode()`'s internals; it also lets the renderer skip the destructive `MLine.Clone()` (section 1.2) and skip ATTDEFs (section 5). Cost: it re-implements four lines of `Explode()`, and loses the `Arc`/`Circle` conversions — which this renderer does not need, since it tessellates arcs and ellipses itself.
2. **Keep `Explode()` but assert the count.** `originals.Count` vs the number of yielded clones; `Notify` a warning and fall back to un-paired drawing if they ever diverge. Two lines, and it turns a silent misplacement into a visible warning after a package upgrade.

**Does a newer ACadSharp fix `ApplyTransform`?** No, and there is nothing to upgrade to.

- **v3.7.1 (published 2026-08-18) is the newest release** — the GitHub releases feed lists `v3.7.1`, `v3.6.51`, `v3.6.35`, `v3.6.29`, `v3.6.12`, `v3.5.7`, `v3.4.29`, … with no 3.8.x and no 4.x. So "upgrading" today means moving to unreleased `master`.
- `TextEntity.ApplyTransform` assigns `InsertPoint`, `Normal`, `Rotation`, `Height`, `WidthFactor`, `ObliqueAngle` and **never touches `AlignmentPoint`** — identical at `v3.7.1` and at `master` *(v3.7.1 source `Entities/TextEntity.cs:279-284`; main, same file, same assignments)*. Since `TextRenderer.GetTextOrigin` uses `AlignmentPoint` for any non-`Left`/non-`Baseline` text (`TextRenderer.cs:209-214`), the workaround is still required. A probe confirms it: a TEXT with `AlignmentPoint = (5,5,0)` inside an insert at `(10,0,0)` with rotation π/2 and scale 2 comes back with `Insert=(10,0,0)` transformed but `Alignment=(5,5,0)` unchanged *(probe)*.
- `MText.ApplyTransform` computes a `newRotation` local and then **discards it**, assigning only `InsertPoint`, `Normal`, `Height` and `RectangleWidth` — again identical at `v3.7.1` and `master`. `MText.Rotation` is a **get-only** property derived from `AlignmentPoint` (`return new XY(this.AlignmentPoint.X, this.AlignmentPoint.Y).GetAngle();`, *v3.7.1 source `Entities/MText.cs:169-175*), and `AlignmentPoint` is never transformed. A probe confirms: an MTEXT with `AlignmentPoint = (1,0,0)` keeps `rotation = 0.0000` after `ApplyTransform` with a π/2 rotation *(probe)*.
- The one relevant `master` change is `MLine.Clone()` (section 1.2), which does not affect text.

**Cost of upgrading.** Nothing to upgrade to. Moving to `master` would buy only the `MLine.Clone()` vertex fix (not the `Vertex.Segments` fix), would still leave both text-transform gaps, and would put the project on an unreleased commit. Recommendation: **stay on 3.7.1** and keep the renderer-side transform, which is correct regardless of what upstream does.

**Recommendation — effort S, no baseline impact.** Add the count assertion (option 2) now; consider the `Block.Entities`-driven refactor (option 1) when MLINE support lands, since it needs the same change to dodge the destructive clone. Neither alters output for any current sample.

---

## 4. Raster text sizing

### 4.1 Text size scales with `ImageConfiguration.Dpi`; geometry does not

**Confirmation of the problem.** Geometry scale comes from the page fit, not from `Dpi`: `ImageRenderContext.ToSurfaceLength` multiplies by `Scale` (`ACadSharp.Image/Rendering/ImageRenderContext.cs:387-392`), which is computed from the requested width/height. `Dpi` (default `96f`, `ACadSharp.Image/ImageConfiguration.cs:148`) is used in exactly two places: line weights, where it is *intended* (`GetLineWeightPixels`: `millimeters * Dpi / 25.4`, `:360`) because line weights are physical millimetres; and text, where it is not — `RasterDrawingSurface.DrawText` sets `TextOptions.Dpi = this._configuration.Dpi` while passing the CAD text height straight in as a font size (`RasterDrawingSurface.cs:184,205` via `CreateFont`, `:279-282`). The SVG backend has no `Dpi` at all: it converts the same height with a fixed factor, `SvgTextLayout.EmSize(h) = h * 4/3` (`ACadSharp.Image/Rendering/Svg/SvgTextLayout.cs:17-20`), and measures at a pinned `Dpi = 72f` (`:64`).

**Why.** `Font.Size` is documented as *"the size of the font in PT units"*; `TextOptions.Dpi` is *"the DPI (Dots Per Inch) to render/measure the text at. Defaults to 72"*; and `FontMetrics.ScaleFactor` is *"the scale factor that is applied to all glyphs in this face. Calculated as `72 * UnitsPerEm` so that 1pt = 1px"* *(SixLabors.Fonts 2.1.3 XML docs, `~/.nuget/packages/sixlabors.fonts/2.1.3/lib/net6.0/SixLabors.Fonts.xml`)*. In the layout code the whole box is computed in inches (`Vector2 boxLocation = options.Origin / options.Dpi;`, `wrappingLength = options.WrappingLength / options.Dpi`) and one line box is `float lineHeight = metric.UnitsPerEm * scaleY;` with `scaleY = pointSize / metric.ScaleFactor.Y` *(v2.1.3 source, `src/SixLabors.Fonts/TextLayout.cs:196,933,1138,1140`)*. Substituting `ScaleFactor = 72 · UnitsPerEm` gives `lineHeight = pointSize / 72` inches, i.e. **em size in pixels = `Font.Size × Dpi / 72`** — so glyphs grow linearly with `Dpi` while the geometry around them does not.

Measured against the pinned assembly *(probe, Liberation Sans)*: at `Font.Size = 10`, `MeasureBounds("Hg")` returns `11.32 × 8.96` px at `Dpi = 72`, `15.09 × 11.94` at `96`, `23.58 × 18.66` at `150`, `47.16 × 37.31` at `300`.

**The correct fix.** Set `TextOptions.Dpi = 72f` and pass the size in **ems**: `size = height * SvgTextLayout.CapHeightToEm` (that is `height * 4/3`). Because `Font.Size × Dpi / 72` is the only thing that matters, `(size = h, Dpi = 96)` and `(size = 4h/3, Dpi = 72)` are the same rendering — verified exactly: `MeasureBounds("Hg")` at `12pt @ 96dpi` and at `16pt @ 72dpi` both return `bounds=(1.3125, 2.2773) 18.1094 × 14.3281` *(probe)*. Under the new scheme the measurement is `Dpi`-invariant: `height = 10` gives `15.09 × 11.94` px at every `Dpi` in `{72, 96, 150, 300}` *(probe)*.

Three knock-on edits in `RasterDrawingSurface.DrawText`:

- `CreateFont` should take the em size, not the cap height, so it and `SvgTextLayout.EmSize` share one definition of the conversion.
- `halfLeading` becomes `factor * font.Size / 8d` (the `* Dpi / 72d` factor collapses to 1) — numerically the same value as today at `Dpi = 96`.
- `FontResolver.Create`'s 1-point clamp (`ACadSharp.Image/Rendering/FontResolver.cs:77-80`) now bites at `height < 0.75` surface units instead of `height < 1`, i.e. the clamp gets slightly *less* aggressive. Sub-pixel text is degenerate either way.

**Baseline impact.** Arithmetically this is a no-op at the default `Dpi = 96`, but it is **not** bit-identical: `Math.Max(1f, (float)h) * 96f/72f` and `Math.Max(1f, (float)(h * 4d/3d))` differ by one ULP for ~37% of heights `h ≥ 1` (max relative difference `1.34e-7`), and end-to-end `MeasureBounds` differs for most heights by at most `6.1e-4` px *(probe)*. That is three orders of magnitude below a pixel, so the rendered PNGs should be unchanged — but `GoldenAssert.Png` compares pixel-for-pixel (`ACadSharp.Image.Tests/GoldenAssert.cs:26-36`), so **run `FeatureGoldenTests`, `SampleParityTests` and `ViewportParityTests` and be ready to regenerate every PNG baseline that contains text — `features.model.01.png`, `viewport-sheet.paper.01.png` (its `"SHEET 1"` label, `ACadSharp.Image.Tests/SyntheticSamples.cs:60`), `6-57-1119.model.01.png` and `HSK80AHCP16190M_BMG.model.01.png`/`.paper.01.png` — with `ACADSHARP_IMAGE_UPDATE_BASELINES=1`. `Subaru-Logo-Vector-Free-Wrap.model.01.png` is splines only and cannot move.** SVG goldens cannot move: the SVG backend does not read `Dpi`. No test sets a non-default `Dpi` today (the only `Dpi =` in the test tree is `Dpi = 72f` inside a `SixLabors` measuring option, `ACadSharp.Image.Tests/SvgTextLayoutTests.cs:112`), so nothing currently asserts the buggy behaviour.

**Recommendation — effort S.** Make the change and add a regression test that renders the same text at `Dpi = 96` and `Dpi = 300` and asserts the glyph bounding box in pixels is unchanged.

### 4.2 Single-line vertical shift when `LineSpacing ≠ 1`

**How SixLabors distributes the extra leading.** In `LayoutLineHorizontal` *(v2.1.3 source, `src/SixLabors.Fonts/TextLayout.cs:316-348`)*:

```csharp
float lineHeight = textLine.ScaledMaxLineHeight;
float advanceY  = lineHeight * options.LineSpacing;
float offsetY   = (advanceY - lineHeight) * .5F;   // half the extra leading
float yLineAdvance = advanceY - offsetY;
if (isFirstLine) {
    case VerticalAlignment.Center: for (…) offsetY -= …ScaledMaxLineHeight * options.LineSpacing * .5F; break;
    case VerticalAlignment.Bottom: for (…) offsetY -= …ScaledMaxLineHeight * options.LineSpacing;      break;
}
penLocation.Y += offsetY;
```

So the extra leading `lineHeight × (LineSpacing − 1)` is split **half above and half below** every line — which is why even a single line moves. Measured at em `= 13.3333` px *(probe, `MeasureBounds("Hg")` y-origin)*:

| `LineSpacing` | `VerticalAlignment.Top` | `.Bottom` | `.Center` |
| --- | --- | --- | --- |
| 1.0 | 1.8978 | −11.4355 | −4.7689 |
| 1.25 | 3.5644 (`+em/8`) | −13.1022 (`−em/8`) | −4.7689 (unchanged) |
| 2.5 | 11.8978 (`+0.75·em`) | −21.4355 (`−0.75·em`) | −4.7689 (unchanged) |
| 0.5 | −1.4355 (`−0.25·em`) | −8.1022 (`+0.25·em`) | −4.7689 (unchanged) |

Top shifts **down** by half the extra leading, Bottom shifts **up** by half, Center is unaffected. That is exactly the shape of the compensation in `RasterDrawingSurface.DrawText` (`:194-201`): `-halfLeading` for `Hanging`, `+halfLeading` for `Alphabetic`, `0` for `Central`.

**The bug.** The magnitude is only right at spacing factor 1. The code computes `halfLeading = factor * em / 8` (`:195`, with `LineSpacing = factor * 5/4` at `:220`), but the shift SixLabors actually applies is `em × (LineSpacing − 1) / 2 = em × (5·factor − 4) / 8`. The two agree **only** when `factor = 1` (both `em/8`). For an MTEXT whose `LineSpacing` factor is, say, 2, SixLabors shifts by `0.75·em` while the renderer compensates `0.25·em`, leaving a `0.5·em` residual displacement of the whole block. `MText.LineSpacing` is a real DXF value (group 44) that this repo passes straight through (`TextRenderer.cs:59`), so any drawing with non-default MTEXT line spacing is misplaced today.

**Cleanest fix, smallest diff.** Correct the formula:

```csharp
double halfLeading = ((factor * 5d / 4d) - 1d) * emPx / 8d * 4d;   // = emPx * (5*factor - 4) / 8
```

or, written directly from the mechanism, `halfLeading = emPx * (lineSpacing - 1d) / 2d` where `lineSpacing` is the value handed to `TextOptions`. At `factor = 1` this is `emPx/8` — byte-identical to today.

**The structurally cleaner alternative** (and the one that removes the whole class of problem) is to stop asking SixLabors to lay out multiple lines at all: reuse `SvgTextLayout.Wrap` to split and wrap the text (it already exists, is already used by the SVG backend, and is already tested), then draw **each line separately** with `LineSpacing = 1` at an origin advanced by `SvgTextLayout.LineHeight(height, factor)`. That makes the two backends share one line-breaking and one line-advance implementation, removes the `5/4` fudge factor and the half-leading correction entirely, and removes the divergence risk between raster and SVG wrapping. Cost: `DrawText` becomes a loop, and the per-line vertical anchoring has to be derived from `SvgTextLayout.BlockOffset` instead of `TextOptions.VerticalAlignment`.

**Baseline impact.** The formula fix changes nothing at `factor = 1`, and no golden uses a non-unit factor — the synthetic `features` sample's only multi-line text is `new MText { Value = "Line1\\PLine2", … }` with the default spacing (`ACadSharp.Image.Tests/SyntheticSamples.cs:120`), and the sample drawings contain 2 multi-line MTEXTs out of 8 (`Samples/HSK80AHCP16190M_BMG.dwg`) *(probe)*, again at the default factor. The per-line-drawing refactor **would** move multi-line PNG baselines (`features.model.01.png` and `HSK80AHCP16190M_BMG.model.01.png`), because ImageSharp's block layout and a manual per-line advance will not agree to the pixel.

**Recommendation.** Formula fix: **effort S, no baseline change** — do it. Per-line refactor: **effort M, moves multi-line PNG baselines** — worth doing only alongside the section 4.1 change, so both text baselines are regenerated once.

---

## 5. Incidental findings

Two things surfaced while verifying the above; neither was asked for, both are cheap to fix and adjacent to the work.

1. **ATTDEF default values are drawn inside every `Insert`.** `BlockRecord.AttributeDefinitions` is `this.Entities.OfType<AttributeDefinition>()` *(v3.7.1 source, `Tables/BlockRecord.cs:72-76`)* — ATTDEFs live in `Block.Entities`, so `Insert.Explode()` yields them, and a probe confirms an `AttributeDefinition` clone comes back among the exploded entities *(probe)*. `AttributeDefinition : AttributeBase : TextEntity`, so `EntityRenderDispatcher`'s `case TextEntity textEntity:` (`:141-143`) draws its `Value` (the ATTDEF's *default* string, DXF group 1) at the ATTDEF's position, for every insert of that block. AutoCAD does not: a non-`Constant` ATTDEF is replaced by the insert's ATTRIB and is not displayed. Fix: skip `AttributeDefinition` in the explode loop (adding `case AttributeDefinition:` before `case TextEntity:` with a `continue`), and draw `Constant` ATTDEFs only. This pairs naturally with [section 2](#2-insert-attributes-attrib). Effort S; no sample or golden contains an ATTDEF *(probe)*, so no baseline moves.
2. **`MLine.Clone()` destroys the source in 3.7.1.** Detailed in [section 1.2](#12-mline-acdbmline). Verified by probe. Relevant even before MLINE rendering exists, because `Insert.Explode()` clones every block entity — an MLINE inside a block is silently emptied for the rest of the process's lifetime.

---

## 6. Sources

**This repository (working tree at `8ec2894`)**

- `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` — type routing and the `default:` NotImplemented arm (`:153-155`), `DrawSolid` OCS handling (`:239-255`), `DrawBlockContents` explode pairing (`:393-413`), `case TextEntity` (`:141-143`).
- `ACadSharp.Image/Rendering/RasterDrawingSurface.cs` — `DrawText` (`:176-232`), `TextOptions.Dpi` (`:205`), `halfLeading` (`:194-201`), `LineSpacing` (`:220`), `CreateFont` (`:279-282`), `FillPolygon` (`:137-147`).
- `ACadSharp.Image/Rendering/Svg/SvgDrawingSurface.cs` — background `<rect>` gating (`:62-79`), `DrawText` (`:374-436`), `BeginViewport` clip (`:438-453`).
- `ACadSharp.Image/Rendering/Svg/SvgTextLayout.cs` — `CapHeightToEm`/`EmSize` (`:17-20`), `LineHeight` (`:23-24`), `BlockOffset` (`:30-35`), `Wrap` (`:55-83`), measuring `Dpi = 72f` (`:64`).
- `ACadSharp.Image/Rendering/TextRenderer.cs` — MTEXT/TEXT transform rationale (`:22-26`, `:72-76`), `GetTextOrigin` (`:209-214`), `LineSpacing` pass-through (`:59`).
- `ACadSharp.Image/Rendering/FontResolver.cs` — the 1-point clamp (`:77-80`).
- `ACadSharp.Image/Rendering/SplineRenderer.cs` — fit-points fallback (`:64-77`).
- `ACadSharp.Image/Rendering/EntityVisibilityFilter.cs` — visibility precedence (`:26-68`).
- `ACadSharp.Image/Rendering/ImageStyle.cs` — record struct, `EffectiveColor` (`:16-26`).
- `ACadSharp.Image/Rendering/ImageRenderContext.cs` — `ToSurfaceLength`/`Scale` (`:387-392`).
- `ACadSharp.Image/ImageConfiguration.cs` — `Dpi` default (`:148`), `GetLineWeightPixels` (`:360`), `BackgroundColor` (`:210`), `ResolveForegroundColor` (`:538-545`).
- `ACadSharp.Image/ImagePage.cs:95,105` and `ACadSharp.Image/ImageExporter.cs:110` — entity draw order is `block.Entities` order.
- `ACadSharp.Image.Tests/GoldenAssert.cs:26-53` — pixel-exact PNG comparison and text-exact SVG comparison; `ACADSHARP_IMAGE_UPDATE_BASELINES=1` regenerates.
- `ACadSharp.Image.Tests/SyntheticSamples.cs:118,120,121` — the `features` sample's `Insert`, two-line `MText` and Fit-aligned `TextEntity`.
- `ACadSharp.Image.Tests/Baselines/` — the twelve baseline/golden files listed in section 4.1.

**ACadSharp 3.7.1 — pinned package**

- `~/.nuget/packages/acadsharp/3.7.1/lib/net10.0/ACadSharp.dll` (reflected public surface) and `.../ACadSharp.xml` (XML docs) for: `T:ACadSharp.Entities.Face3D`, `T:ACadSharp.Entities.InvisibleEdgeFlags`, `T:ACadSharp.Entities.MLine` and its nested `Vertex`/`Vertex.Segment`, `T:ACadSharp.Objects.MLineStyle` and `MLineStyle.Element`, `T:ACadSharp.Entities.MLineJustification`, `T:ACadSharp.Entities.MLineFlags`, `T:ACadSharp.Objects.MLineStyleFlags`, `T:ACadSharp.Entities.Wipeout`, `T:ACadSharp.Entities.CadWipeoutBase`, `T:ACadSharp.Entities.ClipType`, `T:ACadSharp.Entities.ClipMode`, `T:ACadSharp.Entities.ImageDisplayFlags`, `T:ACadSharp.Entities.Leader`, `T:ACadSharp.Entities.LeaderPathType`, `T:ACadSharp.Entities.LeaderCreationType`, `T:ACadSharp.Entities.HookLineDirection`, `P:ACadSharp.Entities.Insert.Attributes`, `M:ACadSharp.Entities.Insert.Explode`, `M:ACadSharp.Entities.Insert.GetTransform`, `T:ACadSharp.Entities.AttributeEntity`, `T:ACadSharp.Entities.AttributeBase`, `T:ACadSharp.Entities.AttributeDefinition`, `T:ACadSharp.Entities.AttributeFlags`, `T:ACadSharp.Entities.AttributeType`, `P:ACadSharp.Header.CadHeader.AttributeVisibility`, `T:ACadSharp.Header.AttributeVisibilityMode`, `P:ACadSharp.Tables.DimensionStyle.ArrowSize`, `P:ACadSharp.Tables.DimensionStyle.LeaderArrow`, `P:ACadSharp.Tables.DimensionStyle.ScaleFactor`.

**ACadSharp source at tag `v3.7.1`** (https://github.com/DomCR/ACadSharp/blob/v3.7.1/…)

- `src/ACadSharp/Entities/Insert.cs:225-246` (ctor creates and transforms attributes), `:320-359` (`Explode`), `:380-…` (`GetTransform`), `:411-436` (`UpdateAttributes`).
- `src/ACadSharp/Entities/TextEntity.cs:135` (`virtual VerticalAlignment`), `:159-285` (`ApplyTransform`, no `AlignmentPoint`).
- `src/ACadSharp/Entities/MText.cs:169-175` (get-only `Rotation` derived from `AlignmentPoint`), `:233-325` (`ApplyTransform`, discards `newRotation`).
- `src/ACadSharp/Entities/AttributeBase.cs:63-64` (`override VerticalAlignment`).
- `src/ACadSharp/Entities/Face3D.cs:27-75`.
- `src/ACadSharp/Entities/MLine.cs:63-93` (`Style`), `:108-121` (destructive `Clone`).
- `src/ACadSharp/Entities/MLine.Vertex.cs:9-63` (`Position`/`Direction`/`Miter`/`Segments`, destructive `Clone`).
- `src/ACadSharp/Entities/CadWipeoutBase.cs:45-53` (`ClipBoundaryVertices` + doc), `:174-185` (`UVector`/`VVector`/`Size`), `:215-220` (`ApplyTransform`), `:230-244` (`GetBoundingBox`, ignores U/V).
- `src/ACadSharp/Entities/Leader.cs:61-72` (computed `HasHookline`), `:113-128` (`Style`), `:159-169` (`ApplyTransform`).
- `src/ACadSharp/CadObject.cs:140-155` (`Clone` zeroes `Handle`).
- `src/ACadSharp/Tables/BlockRecord.cs:72-76` (`AttributeDefinitions`), `:113-118` (`Entities`), `:243-251`, `:470-486` (`SortEntitiesTable`, `GetSortedEntities`).
- `src/ACadSharp/IO/DXF/DxfStreamReader/DxfSectionReaderBase.cs:231-234`, `:1515-1536` (WIPEOUT/IMAGE read path).
- `src/ACadSharp/IO/DWG/DwgStreamReaders/DwgObjectReader.cs:1217-1233`, `:5608`, `:5655` (WIPEOUT/IMAGE read path).

**ACadSharp `master` (unreleased, read 2026-09-03)** — `src/ACadSharp/Entities/TextEntity.cs`, `MText.cs`, `MLine.cs`, `MLine.Vertex.cs`, `CadWipeoutBase.cs`; `MLine.Clone` fixed, everything else unchanged. Release list from https://api.github.com/repos/DomCR/ACadSharp/releases (latest `v3.7.1`, 2026-08-18).

**Autodesk** — *AutoCAD 2012 DXF Reference*, https://images.autodesk.com/adsk/files/autocad_2012_pdf_dxf-reference_enu.pdf: *3dface group codes* (p. 63-64), *Mline group codes* (p. 104-106), *Wipeout group codes* (p. 155) and *Image group codes* (p. 95-96), *Leader group codes* (p. 98-99), *Attrib group codes* (p. 72-73).

**SixLabors.Fonts 2.1.3**

- `~/.nuget/packages/sixlabors.fonts/2.1.3/lib/net6.0/SixLabors.Fonts.xml` — `P:SixLabors.Fonts.Font.Size`, `P:SixLabors.Fonts.FontMetrics.ScaleFactor`, `P:SixLabors.Fonts.TextOptions.Dpi`, `P:SixLabors.Fonts.TextOptions.LineSpacing`, `P:SixLabors.Fonts.TextOptions.WrappingLength`.
- https://github.com/SixLabors/Fonts/blob/v2.1.3/src/SixLabors.Fonts/TextLayout.cs — `:196` (`Origin / Dpi`), `:316-348` (leading split, `offsetY`), `:933` (`WrappingLength / Dpi`), `:1138-1148` (`lineHeight = UnitsPerEm * pointSize / ScaleFactor`).

**Probes** — throwaway .NET 10 console projects in the session scratchpad, referencing the pinned `ACadSharp 3.7.1` and `SixLabors.Fonts 2.1.3`: (a) entity-type census of the three `Samples/` drawings; (b) `MLine.Clone()` destructiveness; (c) `Insert.Explode()` ordering, clone handles, TEXT `AlignmentPoint` and MTEXT `Rotation` after `ApplyTransform`; (d) DXF and DWG write→read round trip of `MLine`/`Face3D`/`Leader`/`Wipeout`/`Insert`+ATTRIB; (e) `TextMeasurer.MeasureBounds` sweeps for the `Dpi`/em-size equivalence, the `LineSpacing` offsets and the float-rounding comparison.

## 7. Not verified

1. **The `+0.5` half-pixel offset in the wipeout boundary mapping** is *derived* from the DXF Reference's documented default boundary `(-0.5,-0.5) … (size.x-0.5, size.y-0.5)`, not stated outright by Autodesk. Likewise the **sign of `VVector`** (the reference says it "points along the visual left side of the image, starting at the insertion point", which reads as upward from a lower-left insertion point) matters only for asymmetric polygonal boundaries. Both should be checked against a real drawing containing a rotated or polygonal WIPEOUT before the mapping is trusted; no such drawing exists under `Samples/`.
2. **The WIPEOUT read path was not exercised end to end.** ACadSharp 3.7.1's writers drop `Wipeout`, so the round-trip probe could not produce one; the conclusion that both readers support it rests on the reader source cited above, not on a parsed file.
3. **Whether the section 4.1 change leaves the PNG baselines byte-identical.** The measured end-to-end divergence is ≤ `6.1e-4` px, which should not flip an antialiased pixel, but `GoldenAssert.Png` is exact and the only way to know is to run `FeatureGoldenTests` and `SampleParityTests`. No test run was performed for this note.
4. **AutoCAD's exact default arrowhead proportions** (the `size/6` half-width used in section 1.4) come from the conventional closed-filled arrowhead geometry, not from a cited Autodesk statement; the DXF Reference documents `DIMASZ` as the arrow *size* only.
5. **That a 3DFACE plots unfilled.** The DXF Reference documents the corners and the invisible-edge flags but says nothing about fill; "edges only in a 2D/wireframe view" is standard AutoCAD behaviour and is implied by the per-edge invisibility flags, but no Autodesk statement was located.
6. **MLINE cap and joint rendering** (`StartRoundCap`, `EndInnerArcsCap`, `DisplayJoints`, `StartAngle`/`EndAngle`) is described from the flag names and the DXF Reference's group-code list; no primary source was found that specifies the exact cap geometry.
