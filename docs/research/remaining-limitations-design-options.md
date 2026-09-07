# Design options for the remaining rendering limitations

> Consultation note produced by OpenAI Codex on 2026-09-04 at the maintainers' request, lightly edited (repository-relative links). It follows up on [remaining-rendering-limitations.md](remaining-rendering-limitations.md) and proposes how each remaining limitation could be implemented against ACadSharp 3.7.1. Line references point at the branch state on that date and may drift.

## Summary

- Keep `IDrawingSurface` unchanged for MLINE cuts, arrow blocks, multi-line attributes, and tilted hatches; existing primitives are sufficient.
- Introduce one internal, cumulative placement module that maps WCS points, OCS points, and vectors without mutating ACadSharp entities.
- Implement tilted hatches from the original hatch: OCS → WCS → cumulative insert placement.
- Positional original/clone pairing is the only reliable identity in ACadSharp 3.7.1; retain it short-term with type guards.
- Long-term, replace `Insert.Explode()` with original-entity traversal plus type-specific primitive extraction.
- Render custom leader arrows as placed block contents; ACadSharp already follows this model for dimension arrow blocks.
- Render inverted opaque wipeouts as an even-odd full-frame-minus-boundary path.
- Transparent wipeouts require an explicit erase/composite primitive and incompatible changes to SVG’s one-group-per-layer structure.
- Route multi-line attributes through their embedded `MText`, retaining ATTRIB style, visibility, and metadata.
- Method: primary-source research plus a codebase-design review focused on seam depth, locality, and observable rendering behavior.

## 1. MLEDIT cut segments

### Semantics

For every MLINE vertex and style element, ACadSharp exposes DXF group 41 as `MLine.Vertex.Segment.Parameters`; group 42 is `AreaFillParameters`. The types are present but carry no interpretation helper in [MLine.Vertex.cs](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/MLine.Vertex.cs#L9-L63).

The [Autodesk MLINE reference](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-DXF/files/GUID-590E8AE3-C6D9-4641-8485-D7B3693E432C.htm) defines:

- `p[0]`: distance from the reference vertex along `Miter` to the element-path intersection.
- `p[1]`: distance along the element path from that intersection to the element’s actual start.
- `p[2]`: distance from the actual start to the first break.
- Further values “continue to list the start and stop points” of the element.

The last phrase is genuinely ambiguous. The literal reading makes `p[2..]` monotonically increasing positions from the actual element start, alternating cut-start/cut-stop. The existing synthetic values `[offset, 0, 4, 6]` naturally mean a cut from distance 4 to 6.

ezdxf documents the same array as `[miter-offset, line-start-offset, dash, gap, dash, …]`, suggesting relative alternating lengths, but also states that it does not create line-break features; its current renderer uses only `offset[0]` and draws continuous elements. See [ezdxf’s model comments](https://github.com/mozman/ezdxf/blob/master/src/ezdxf/entities/mline.py#L140-L185) and [renderer](https://github.com/mozman/ezdxf/blob/master/src/ezdxf/render/mline.py#L177-L205). LibreDWG only preserves the raw `segparms`/`areafillparms` arrays and therefore does not resolve the ambiguity ([schema](https://github.com/LibreDWG/libredwg/blob/master/src/dwg.spec#L2967-L3004)). I found no public ODA passage that settles it.

### Options

1. **Absolute cut positions, matching the literal DXF prose.**

   For segment `i → i+1`, element `j`:

   - `A = vertex[i].Position + p[0] × vertex[i].Miter`
   - `D = normalize(vertex[i].Direction)`
   - `S = A + p[1] × D`
   - `E` is the next vertex’s element-path intersection.
   - Interpret `(p[2], p[3]), (p[4], p[5]), …` as cut-start/cut-stop distances from `S`.
   - Clamp and sort valid pairs to the signed span `S → E`, merge overlaps, and emit the complementary visible intervals.
   - Construct endpoints in the original block coordinate system, then apply `placement` to each endpoint. This correctly handles non-uniform and mirrored inserts without trying to scale the stored distances separately.
   - Coalesce adjacent visible intervals across vertices where possible so linetype phase is not unnecessarily restarted.

   Both backends receive `DrawLine`/`DrawPolyline`; SVG emits `<line>` or `<polyline>`, raster uses ImageSharp strokes. Fill remains before strokes. Transparency, per-layer grouping, viewport compositing, and entity draw order are unchanged.

2. **Relative dash/gap lengths, matching ezdxf’s comments.**

   Begin at `S`, alternately consume visible and hidden lengths from `p[2..]`. The primitive output is identical to option 1, but drawings whose parameters are absolute positions render differently as soon as there is more than one cut.

3. **Extend the interval engine to group 42 fill cuts.**

   Group 42 uses analogous parameterization. A complete MLEDIT implementation would split the filled band into per-segment polygons and subtract the group-42 gaps before stroking. This is materially larger than the stated group-41 limitation; inferring fill gaps from stroke gaps is incorrect.

A mask-based solution is inappropriate: painting a background-colored cut after the MLINE would also hide unrelated entities drawn earlier.

### Recommendation

Use option 1, but make an AutoCAD-authored fixture the acceptance oracle before freezing the interpretation. Effort **M** including malformed-data handling; **S** once the absolute interpretation is confirmed.

Also extend [`HasFiniteGeometry`](../../ACadSharp.Image/Rendering/EntityRenderDispatcher.cs:385): it currently validates only `Parameters[0]` and does not validate `Direction`; the new path consumes every parameter.

Tests:

- Replace `MLineCutParametersAreIgnoredWithAWarning` with `[0.5, 0, 4, 6]` expecting visible runs `0–4` and `6–end`.
- Multiple and overlapping cuts, odd parameter count, descending/non-finite values, closed final-to-first segment.
- A rotated, mirrored, non-uniformly scaled block MLINE proving endpoints go through `placement`.
- Dashed element around a cut, documenting whether dash phase restarts.
- Separate group-42 warning/test until fill cuts are implemented.

Existing no-cut MLINEs should remain primitive-for-primitive identical, so PNG/SVG baseline risk is **low**. New feature goldens will intentionally contain multiple SVG strokes.

No upstream change is required to read the cuts. The useful upstream changes are an authoritative `MLine` interval/virtual-entity helper and correct deep cloning of both `MLine.Vertices` and every vertex’s `Segments`/parameter lists.

## 2. Custom arrowhead blocks

### Semantics

AutoCAD treats a custom arrowhead as a block inserted at the normal arrow location. Its X/Y scale is the arrow size multiplied by the overall dimension scale; its block insertion point affects placement. For a horizontal dimension, Autodesk documents zero rotation at the right end and 180° at the left end. Annotative blocks are not valid arrowheads. See [About Customizing Arrowheads](https://help.autodesk.com/cloudhelp/2019/ENU/AutoCAD-Core/files/GUID-5D1F8D41-86EC-481F-ACA0-B169F0B91D00.htm) and [DIMLDRBLK](https://help.autodesk.com/cloudhelp/2019/ENU/AutoCAD-LT/files/GUID-B4374832-C2B4-4555-900C-693625AC58DE.htm).

ACadSharp exposes the resolved block as `DimensionStyle.LeaderArrow`, DXF handle 341, and exposes `ArrowSize`/`ScaleFactor` in [DimensionStyle.cs](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Tables/DimensionStyle.cs#L138-L176). `Leader.Style` and its WCS `Vertices` are in [Leader.cs](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Leader.cs#L102-L158).

The transform convention is corroborated by ACadSharp’s own dimension implementation: `dimensionArrow` maps the block base point to the tip, uses `ArrowSize × ScaleFactor`, and rotates local +X to the supplied arrow direction ([Dimension.cs](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Dimension.cs#L702-L730)).

Consequently, dimensions do not have the same dispatcher-level gap when their anonymous picture block exists or is generated: the picture contains a correctly scaled/rotated `Insert`, and [`DrawDimension`](../../ACadSharp.Image/Rendering/EntityRenderDispatcher.cs:225) recursively renders it. `DIMBLK`/`DIMBLK1`/`DIMBLK2` need separate work only if ACadSharp fails to generate the anonymous dimension block or ignores a particular override.

For a straight leader, the outward arrow direction is `tip - vertices[1]`. For a spline leader, AutoCAD visually aligns the arrow to the endpoint tangent; using the first chord is only an approximation. With the current Catmull–Rom conversion, derive the tangent from the first Bézier control pair.

### Options

1. **Render the arrow `BlockRecord` through a placement-aware block helper.**

   Build:

   - `tip = leader.Vertices[0]`
   - `direction = normalized outward endpoint tangent`
   - `scale = Style.ArrowSize × effective Style.ScaleFactor`
   - `arrowPlacement(local) = tip + R(direction) × scale × (local - block.BasePoint)`

   Compose that with any outer leader `placement`, rather than decomposing it back into an ACadSharp `Insert`. Traverse the arrow block’s stored entities in order, with layer-0 and ByBlock properties inherited from the leader. Draw the leader line first and arrow contents second, so filled arrow geometry covers the line endpoint.

   Both backends use existing primitives. SVG arrow children retain vector paths but enter their effective per-layer `<g>` groups; raster draws them directly. Transparent backgrounds have no special meaning—ordinary entity opacity applies.

2. **Construct a synthetic ACadSharp `Insert` and call `DrawBlockContents`.**

   This is smaller initially and matches `Dimension.dimensionArrow`, but imports every existing `Explode()` problem: destructive MLINE cloning, transformed text defects, hatch normalization, nested `BlockRecord.Clone()` reordering, and possible attribute creation from the `Insert(BlockRecord)` constructor.

3. **Compile blocks into reusable drawing commands/SVG symbols.**

   Cache a backend-neutral primitive display list. SVG can emit a `<symbol>`/`<use>` or replay the commands; raster replays them. This helps drawings with thousands of identical arrows but complicates ByBlock styling, per-layer grouping, entity metadata, nested inserts, and recursion detection.

### Recommendation

Use option 1. It is a useful deep module rather than arrow-specific recursion, and it becomes groundwork for option 5(c). Effort **M**.

Add a recursion guard keyed by active `BlockRecord` references plus a configurable depth cap. A block containing a leader that points back to the same arrow block must warn and fall back to the default arrow rather than recurse forever.

Tests:

- Custom block containing a line and a filled circle; assert that no fallback triangle or NotImplemented notification remains.
- Non-zero block base point, four leader directions, and `ArrowSize × ScaleFactor`.
- Layer 0/ByBlock inheritance and a nonzero child layer.
- Custom arrow on a leader inside rotated, mirrored, and non-uniformly scaled inserts.
- Spline leader endpoint tangent.
- Recursive arrow block and malformed/empty block.
- PNG and SVG synthetic goldens.

Default leaders remain unchanged. Existing drawings with custom arrows intentionally change, so overall baseline risk is **low**, but their new goldens are substantial.

No upstream change is necessary. Helpful upstream additions would be a public arrow-block placement helper equivalent to protected `Dimension.dimensionArrow`, and a `Leader.GetActiveDimensionStyle()` equivalent if leader DSTYLE overrides are not already folded into `Leader.Style`.

## 3. Inverted wipeout clips

### Semantics

WIPEOUT uses the raster-image geometry model: WCS insertion point, WCS single-pixel U/V vectors, pixel-space boundary vertices, image size, clipping state, and rectangular or polygonal clip type. Autodesk documents the default pixel boundary as `(-0.5,-0.5)` to `(size.x-0.5,size.y-0.5)` in the [WIPEOUT DXF reference](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-DXF/files/GUID-2229F9C4-3C80-4C67-9EDA-45ED684808DC.htm).

The published WIPEOUT table does not document group 290, so the serialization reference itself is incomplete here. AutoCAD’s [IMAGECLIP command documentation](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-9D652E1A-29F8-49BC-ABCC-37B9F1C7A1D0.htm) resolves the display semantics: normal clipping hides the outside; inverted clipping hides the inside. Turning clipping off displays the full image.

ACadSharp maps group 290 to `CadWipeoutBase.ClipMode`, with `Outside` and `Inside`, and exposes the remaining fields in [CadWipeoutBase.cs](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/CadWipeoutBase.cs#L45-L53). `Inside` means the inside is clipped away, so the wipeout paints the full image frame minus the active boundary.

Two related issues matter:

- Clip mode must be ignored when `ClippingState == false`; the current early return in [`DrawWipeout`](../../ACadSharp.Image/Rendering/EntityRenderDispatcher.cs:674) skips an inverted wipeout even when clipping is off.
- ACadSharp’s `ApplyTransform` applies point transforms to `UVector` and `VVector`, so translations contaminate both vectors ([CadWipeoutBase.cs](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/CadWipeoutBase.cs#L215-L220)). Wipeouts in block references should therefore be mapped from the original entity and then passed through outer placement.

### Options

1. **Opaque-background even-odd fill using the existing interface.**

   When clipping is active:

   - Build the complete four-corner image footprint.
   - Build the active rectangular/polygonal boundary.
   - For normal mode, `FillPolygon(boundary)`.
   - For inverted mode, `FillPath([fullFrame, boundary])`.

   When clipping is off, fill the full frame regardless of `ClipMode`.

   [`FillPath`](../../ACadSharp.Image/Rendering/IDrawingSurface.cs:66) already gives the needed even-odd rule: SVG emits one `<path fill-rule="evenodd">`; raster uses an ImageSharp `ComplexPolygon` with `IntersectionRule.EvenOdd`. Autodesk requires clipping vertices to remain within the image, so the inner-ring assumption is valid for conforming files.

   Use the current opaque background color. On transparent/translucent backgrounds, retain the explicit warning and skip: a color fill cannot mean “reveal what was beneath the CAD drawing.”

2. **Add `IDrawingSurface.ErasePath(rings)` with even-odd semantics.**

   This models a wipeout directly as removal of earlier paint:

   - Raster: rasterize the region into an antialiased mask and replace those destination pixels with the surface’s base value—configured background at page level, transparent in a viewport child, transparent for a transparent page.
   - SVG: leave the background as an immutable bottom sibling; wrap all earlier paint in the current container in a `<g mask="…">`, use a user-space mask to punch out the region, then begin a new paint segment for later entities.

   This handles transparent output and nested viewports correctly. It also supports both normal and inverted wipeouts, future IMAGE/XCLIP work, and MTEXT background masks.

   The price is structural: after every erase, later entities require new layer groups. A single `<g>` per logical layer and strict global painter order cannot both be preserved.

3. **Retain every primitive in a display list and resolve compositing at finalization.**

   This permits exact chronological layers, masks, and clips, but converts both adapters into retained-mode renderers. It is an architectural **L** change with broad memory and golden consequences.

`BeginClip`/`EndClip` is not the right interface: a clip constrains future paint; a wipeout removes prior paint.

### Recommendation

For the stated limitation, use option 1: effort **S**, low risk, no `IDrawingSurface` change. Treat transparent wipeouts as a separate explicit capability; if required, choose option 2 rather than a misleading `WipeoutColor`.

Update [`WipeoutWorldBoundary`](../../ACadSharp.Image/Rendering/EntityRenderDispatcher.cs:707) and [`EntityBounds.TryGet`](../../ACadSharp.Image/Rendering/EntityBounds.cs:19) together. An inverted wipeout’s bounds are its full image footprint, not an empty set or the inner polygon; otherwise page framing and viewport culling can discard it before rendering.

Tests:

- Inverted polygon produces two even-odd rings.
- Inverted rectangular pair expands correctly.
- `ClippingState=false` fills the full frame even with `ClipMode.Inside`.
- A line before the wipeout is hidden; a line after it remains visible.
- Rotated/skewed U/V vectors and an outer block placement.
- Active viewport compositing.
- Existing transparent-background warning.
- If `ErasePath` is later added: transparent page, nested viewport, multiple successive wipeouts, and cross-layer chronological behavior.

The current SVG design gives layer grouping precedence over painter order. Under option 1, a wipeout can still cover a later entity appended to an older layer group. Exact AutoCAD behavior requires option 2 or 3 and repeated chronological layer groups. That should be documented, not hidden in the wipeout helper.

Upstream should fix `CadWipeoutBase.ApplyTransform` to transform U/V as vectors, transform or retain the pixel boundary consistently, expose the correct full-frame world polygon, and correct `GetBoundingBox()`.

## 4. Multi-line attributes

### Semantics

The [ATTRIB DXF reference](https://help.autodesk.com/cloudhelp/2019/ENU/AutoCAD-DXF/files/GUID-7DD8B495-C3F8-48CD-A766-14F9D7D0DD9B.htm) includes an `AcDbXrecord`/`AcDbMText` representation. Its MText flag distinguishes multiline attributes and constant multiline definitions; the embedded section contains the text chunks, text style, WCS X-axis, width, height, and rotation.

ObjectARX describes the embedded `AcDbMText` as the actual representation used by a multiline attribute ([`getMTextAttribute`](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-__MEMBERTYPE_Methods_AcDbAttribute.html)). Consequently, the single-line `AttributeBase.Value` is not authoritative for layout.

ACadSharp models this as `AttributeBase.AttributeType` and `AttributeBase.MText` in [AttributeBase.cs](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/AttributeBase.cs#L9-L30). `AttributeEntity` still derives through `TextEntity`, which explains why the current switch reaches the single-line TEXT path.

A top-level ATTRIB’s TEXT/MTEXT coordinates already include its own insert’s placement. It must not receive `insert.GetTransform()` again. For an ATTRIB owned by an insert nested inside another block, however, the outer cumulative placement still applies. That distinction should be represented explicitly rather than by a nullable transform whose meaning changes by call site.

### Options

1. **Route the embedded `MText` while retaining ATTRIB ownership.**

   Before the generic `TextEntity` case, detect `AttributeBase` with `MultiLine` or `ConstantMultiLine`:

   - Resolve color, layer, transparency, visibility, `EntityRenderInfo`, and parent handle from the ATTRIB/ATTDEF.
   - Pass `attribute.MText` as layout geometry to `TextRenderer.Draw(MText, placement)`.
   - For a top-level `Insert.Attributes` entry, placement is identity.
   - For a nested insert’s attribute, placement is the transform of outer block references only.
   - For a constant multiline ATTDEF in block-local coordinates, use the full block placement.

   SVG automatically emits `<text>`/`<tspan>`; raster uses the existing multiline/wrapping glyph path. Draw order remains the current order: attributes are drawn after block contents and in `Insert.Attributes` order. Transparent backgrounds need no special behavior.

2. **Add a `TextRenderer.DrawAttribute` adapter.**

   It accepts the owner attribute, embedded MText, and placement, hiding the ownership/layout split. This slightly deepens `TextRenderer` and prevents the dispatcher from knowing which MText fields are authoritative.

3. **Synthesize MText when `MText == null`.**

   Copy `Value`, point, height, rotation, and style into a temporary MText. This loses the true rectangle width, attachment point, direction, and embedded formatting. It is acceptable only as a warning-producing fallback.

### Recommendation

Use option 2 internally, implemented with option 1’s semantics. Effort **S**, low risk.

The observable entity remains ATTRIB—important for SVG `data-type`, `data-handle`, parent insert metadata, and layer grouping—while the embedded MText supplies only layout geometry.

Tests:

- `AttributeType.MultiLine`, `Value="WRONG"`, embedded `MText.Value="Line1\\PLine2"`; assert two lines and prove `Value` is ignored.
- Rectangle width/wrapping, attachment point, line spacing, rotation, and text style.
- Top-level inserted attribute proving no double placement.
- Nested insert proving exactly the outer placement is applied.
- Mirrored outer insert and a constant multiline ATTDEF.
- Hidden/ATTMODE filtering remains unchanged.
- Missing embedded MText warns and uses the documented fallback.
- SVG `<tspan>` and raster golden.

Existing single-line attributes remain byte-identical. Only drawings already containing multiline attributes change.

For complete upstream safety, ACadSharp needs to deep-clone `AttributeBase.MText`, transform it with the owning attribute, and correct `MText.ApplyTransform`. `AttributeDefinition` also inherits the broken `TextEntity.ApplyTransform`; its embedded MText needs explicit treatment.

## 5. Tilted hatches in blocks and the explode pairing

### Semantics

HATCH elevation and normal define an OCS plane, and its boundary vertices are OCS data. Autodesk states this explicitly in the [HATCH entity reference](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-C6C71CED-CE0F-4184-82A5-07AD6241F15B.htm) and for every boundary edge type in [Boundary Path Data](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-DC5215D6-E73F-4DFF-8BE9-01CA9610FAEE.htm). The OCS-to-WCS frame follows AutoCAD’s arbitrary-axis algorithm ([OCS overview](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-D99F1509-E4E4-47A3-8691-92EA07DC88F5.htm)).

ACadSharp exposes `Hatch.Elevation`, `Normal`, `Paths`, and `Pattern` in [Hatch.cs](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Hatch.cs#L18-L68). Its `ApplyTransform` transforms raw boundary edges directly, separately transforms the normal, never incorporates the original OCS elevation into each boundary point, and reduces the transformed pattern to one angle and scale ([Hatch.cs](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Hatch.cs#L145-L170), [BoundaryPath.cs](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Hatch.BoundaryPath.cs#L90-L109)). That representation cannot preserve a general affine transform of a tilted pattern.

Therefore [`NormalizeExplodedClone`](../../ACadSharp.Image/Rendering/EntityRenderDispatcher.cs:917) is correct only for a world-plane hatch whose insert changes the normal to `-Z`, notably a mirror. For an originally tilted hatch, resetting the clone to `+Z` hides the missing original OCS transformation.

`Insert.Explode()` is structurally one-to-one and ordered in 3.7.1 ([Insert.cs](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Insert.cs#L302-L340)). Clones cannot carry identity: `CadObject.Clone()` clears the handle, document, and owner. Thus ordinal position is the only identity available after `Explode()`; geometry/type matching is unsafe because duplicates are legal and Circle becomes Ellipse.

One correction to the stated premise: the current working tree’s [`DrawBlockContents`](../../ACadSharp.Image/Rendering/EntityRenderDispatcher.cs:744) uses the original only for exact-type `TextEntity`/`MText`; it gives `placement` to a healed MLine, but `Solid` and `Leader` still render from exploded clones. Designs that assume all five types already use original geometry would miss current defects in non-world SOLID and scaled leader arrowheads.

### Options (a) original+transform, (b) clone normalisation, (c) Explode()-free composition; ApplyTransform trust table

#### (a) Original hatch + OCS + insert transform

Pair the hatch clone to its original by ordinal, but ignore the clone’s geometry:

1. Generate original boundary points or original `ExplodePattern()` segments in hatch OCS.
2. Map each endpoint with `OcsTransform.For(original.Normal)` and `original.Elevation`.
3. Apply the cumulative insert `placement`.
4. Project through `ImageRenderContext`.
5. Emit `FillPath` for solid hatches or `DrawLine` for pattern segments.

Pattern expansion occurs before placement, so non-uniform scaling, mirroring, and tilted projection are represented by transformed endpoints rather than forced back into one pattern angle/scale. Both backends receive the same primitives; SVG remains vector. Entity/layer metadata and draw order come from the exploded clone/source pair exactly as today.

This is the smallest correct change. It still relies on the 3.7.1 ordinal guarantee.

#### (b) Repair the clone in `NormalizeExplodedClone`

A safe narrow normalization is possible only when the **original** hatch normal was already `+Z`: after a mirror, treat its transformed boundary coordinates as world data and reset the clone normal.

A genuinely tilted hatch cannot be repaired from the clone alone. The information required to distinguish “raw original OCS, then insert-transformed” from “fully transformed WCS” has already been conflated. Rebuilding its boundary and pattern from the original would merely reimplement option (a) while adding mutable transient state.

Recommendation within this option: make normalization conditional on the original normal and warn/fall back for any source/clone mismatch. Do not retain the current unconditional “any non-world normal becomes +Z” rule.

#### (c) `Explode()`-free composition

Traverse `insert.Block.Entities` directly in stored order and carry a cumulative placement:

`worldPoint = outerPlacement(innerInsert.GetTransform()(sourcePoint))`

The internal placement module should expose only a few operations:

- map WCS point;
- map vector by transforming `origin + vector` and subtracting transformed origin;
- map OCS point using original normal/elevation, then placement;
- compose an inner insert.

That interface hides matrix order and all point-versus-vector traps.

Two implementation variants are viable:

- **All-source rendering:** every dispatcher helper extracts source geometry and applies placement before producing surface primitives.
- **Hybrid transform-one:** clone and call `ApplyTransform` only for types proven safe for the renderer; special-case all others.

To retain Circle→Ellipse under non-uniform scale without `Explode()`:

- Map the circle center and its two orthogonal radius axes through OCS and placement.
- Form the projected 2×2 axis matrix.
- Use its singular values and left singular vectors as ellipse radii and rotation.
- Emit `DrawEllipse` for a full circle. SVG keeps a native `<ellipse>`; raster tessellates because `SupportsCurves` is false.
- Tessellate partial arcs initially, or derive the SVD-induced phase and reflected sweep separately.

Do not create an `Ellipse` and call ACadSharp’s `Ellipse.ApplyTransform`; that method itself is not reliable.

An Explode-free traversal also removes:

- positional pairing;
- the MLINE snapshot/heal mutation;
- `Insert.Clone()`’s deep block clone;
- nested `BlockRecord.Clone()` reordering through `GetSortedEntities()` ([BlockRecord.cs](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Tables/BlockRecord.cs#L363-L385));
- loss of source handles/documents/owners.

It may nevertheless change SVG output: using original entities would expose their handles where exploded clones currently omit `data-handle`. Preserve the present metadata contract deliberately, or introduce `data-source-handle` as a separately reviewed change.

#### ApplyTransform trust table

Here, “safe” means safe for a copied entity under an arbitrary nested-insert affine transform and for the primitives this renderer emits—not merely that the method changes some fields.

| Entity | Trust | Assessment |
|---|---:|---|
| [`Line`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Line.cs#L71-L76) | Yes | Maps both WCS endpoints and normal. |
| [`Arc`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Arc.cs#L168-L201) | Similarity only | Retains `Arc`; non-uniform scale requires an elliptical arc. |
| [`Circle`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Circle.cs#L68-L80) | Similarity only | Retains one radius. `Insert.Explode()` avoids this with Circle→Ellipse. |
| [`Ellipse`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Ellipse.cs#L122-L146) | No | Transforms a perpendicular direction with the point transform and does not remap partial-ellipse parameters/reflection. |
| [`LwPolyline`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/LwPolyLine.cs#L109-L124) | No, generally | Re-expresses vertices but leaves bulges, widths, thickness, and elevation semantics incomplete; non-uniform scale turns circular bulges into ellipses. |
| [`Polyline2D`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/PolyLine.cs#L121-L144) | No, generally | Same base transform problem as above; straight centerlines under planar similarity transforms are usable. |
| [`Polyline3D`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/PolyLine.cs#L121-L144) | Conditional | Inherits the same OCS re-expression despite 3D vertex semantics. Common +Z insert transforms work; direct mapping of sampled WCS vertices is safer. |
| [`Spline`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Spline.cs#L190-L205) | Geometry-only | Control/fit points are affine-mapped, so this renderer’s locus is usable. Start/end tangents are transformed as points and are not trustworthy. |
| [`Hatch`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Hatch.cs#L145-L170) | No | Raw OCS paths are transformed without the original OCS/elevation; pattern state cannot represent general affine scaling. |
| [`Solid`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Solid.cs#L93-L99) | World-plane only | Corners are raw OCS values but are transformed as WCS; normal is not updated. |
| [`Face3D`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Face3D.cs#L63-L69) | Yes | All four WCS corners are mapped. |
| [`Point`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Point.cs#L61-L65) | Dot only | Location/normal are sufficient for the current dot primitive; point-display rotation is not transformed. |
| [`Dimension`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Dimension.cs#L242-L264) | No | Maps only the main definition point and sometimes text midpoint; subtype points and anonymous picture geometry are not generally transformed. |
| [`Insert`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Insert.cs#L241-L291) | Conditional | Attempts to decompose the result into normal/rotation/scales. Nested non-uniform scales plus rotations can introduce shear, which `Insert` cannot represent; attributes inherit text defects. |
| [`MLine`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/MLine.cs#L95-L121) | No | Vertex direction/miter/parameters and scale are unsafe under general placement; cloning is destructive in 3.7.1. |
| [`Leader`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/Leader.cs#L149-L165) | Line geometry only | Vertices and horizontal direction are mapped, but offsets and generated arrow size/shape do not inherit the affine placement. |
| [`Wipeout`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/CadWipeoutBase.cs#L215-L220) | No | U/V vectors are transformed as points, adding translation. |
| [`TextEntity`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/TextEntity.cs#L149-L268) | No | `AlignmentPoint` is untouched. |
| [`MText`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/MText.cs#L217-L306) | No | Computes then discards rotation; `AlignmentPoint` remains unchanged; non-uniform scaling is intentionally unsupported. |
| [`AttributeDefinition`](https://github.com/DomCR/ACadSharp/blob/v3.7.1/src/ACadSharp/Entities/AttributeDefinition.cs#L9-L44) | No | Inherits `TextEntity.ApplyTransform`; the embedded MText also needs independent clone/placement handling. |

### Recommendation

Implement option (a) now. Effort **M**, baseline risk **low**: top-level hatches and the existing mirrored world-plane regression should remain unchanged; only tilted hatches in blocks intentionally move.

Harden ordinal pairing at the same time:

- Pairing by `Explode()` position is already the only handle-free scheme available in 3.7.1.
- Check a per-type compatibility relation before using original geometry: exact type for text/hatch, plus explicitly allowed conversions such as Circle→Ellipse.
- On mismatch, warn immediately and avoid applying the wrong original—not merely report a count mismatch after everything has drawn.
- Keep the final count warning as a package-upgrade tripwire.

Tests:

- Solid and patterned hatch with a genuinely tilted normal, non-zero elevation, and a translated/rotated insert.
- Same under mirror and non-uniform scale.
- Two nested inserts proving transform composition.
- Existing mirrored +Z hatch remains unchanged.
- Pattern endpoints are generated in original OCS and placed afterward.
- Source hatch and block remain unchanged after drawing.
- Duplicate adjacent entities and a Circle→Ellipse between source-dependent types, proving pairing is ordinal rather than geometric.

Treat option (c), preferably hybrid transform-one, as a separate **L** migration. It is architecturally cleaner but has high SVG-golden and moderate PNG-baseline risk because it changes clone metadata, nested order, and curve representation.

## Sequencing and shared infrastructure

1. **Multi-line attributes first**: isolated **S**, immediate coverage, no surface changes.
2. **Placement module + tilted hatch**: establish the cumulative point/vector/OCS seam and harden ordinal pairing.
3. **Custom leader arrows**: reuse placement and placed-block traversal; also correct arrow sizing inside outer inserts.
4. **Inverted wipeout with existing `FillPath`**: small once original-wipeout placement and bounds are available.
5. **MLEDIT cuts last**: first obtain one AutoCAD-authored multi-cut fixture to settle absolute versus relative interpretation.
6. **Separate architectural work:** Explode-free traversal and, independently, transparent `ErasePath` compositing.

The highest-leverage shared module is placement, not clipping. It serves items 1, 2, 3, 4, and 5 while leaving the backend interface small. `ErasePath` earns a real surface seam only if transparent wipeouts or future XCLIP/IMAGE masking are in scope.

Current-code complications to account for:

- SVG’s single layer group means exact cross-layer painter order is already impossible.
- `WipeoutWorldBoundary` is also used for bounds/culling.
- MLINE finiteness checks currently cover too little data.
- `DrawAttributes` occurs after all block contents.
- `BlockRecord.Clone()` materializes sorted rather than stored entity order.
- Current `DrawBlockContents` does not actually use originals for Solid or Leader.
- A spline leader’s arrow direction should come from its endpoint tangent.
- An Explode-free traversal must apply only outer placement to nested `Insert.Attributes`; applying the nested insert’s own transform again is a double transform.

## Upstream (ACadSharp) changes that would unlock more

- Correct `Hatch.ApplyTransform`, or expose `GetWorldBoundaryPoints()` and `ExplodePatternWorld()` that explicitly consume the original OCS/elevation.
- Add `Insert.ExplodeWithSources()` returning `(Source, Result)` pairs, or a public one-entity transform operation that preserves source identity.
- Deep-clone `MLine.Vertices`, vertex `Segments`, and both parameter lists without mutating the source.
- Fix `TextEntity.AlignmentPoint`, `MText.AlignmentPoint`/rotation, and embedded `AttributeBase.MText` cloning/transformation.
- Transform wipeout U/V as vectors and provide correct world bounds.
- Make Circle/Arc/Ellipse affine transforms representation-aware, including Circle→Ellipse and reflected partial-ellipse parameters.
- Transform spline tangents as vectors.
- Preserve stored block entity order in `BlockRecord.Clone()` while cloning the sort table separately.
- Expose the dimension arrow-block placement helper for leaders, and expose effective leader style overrides directly.
