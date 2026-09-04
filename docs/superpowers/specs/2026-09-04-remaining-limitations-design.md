# Remaining rendering limitations: design (2026-09-04)

Revised 2026-09-04 against the branch after plan 09 (signatures, the existing pairing relation and the cycle pre-check are quoted from the code as it now stands).

Follow-up to the layers-and-SVG design (`2026-09-02-layers-and-svg-design.md`, whose global constraints, interface appendix and notification rules apply unchanged) and to the research notes `docs/research/remaining-rendering-limitations.md` and `docs/research/remaining-limitations-design-options.md` (the Codex consultation this design argues from). Line references below describe the branch after plan 09; plan 10 executes this design.

## 1. Goal

Draw, instead of notifying about, the five remaining gaps listed in the README: MLEDIT cut segments, custom arrowhead blocks, inverted wipeout clips, multi-line attributes, and hatches on a tilted plane inside blocks. Harden the block-content pairing that several of these rely on so a mismatch is caught per entity rather than by a count after the fact.

## 2. Non-goals

- Transparent or translucent wipeouts (would need an erase primitive and break the one-group-per-layer SVG structure; the existing Warning stays).
- Exact cross-layer painter order in SVG (layer grouping keeps precedence over draw order, as documented in 5.2 of the base design).
- An `Explode()`-free block traversal. Block contents keep coming from `Insert.Explode()`; originals are used for geometry only where a clone is known to be wrong.
- MLINE fill cuts (group 42, `AreaFillParameters`): a NotImplemented notification replaces the current stroke-cut warning for them.
- Upstream ACadSharp fixes. Everything here works on 3.7.1 as shipped.

## 3. Facts this design rests on (verified 2026-09-04)

- Both ACadSharp 3.7.1 readers populate `AttributeBase.MText` for multi-line attributes: the DXF reader on the embedded-object marker (code 101), the DWG reader in `readCommonAttData` for `AttributeType.MultiLine`/`ConstantMultiLine` (the type byte exists only in R2018+ files, so older DWGs always read as single-line).
- `DrawWipeout` and `WipeoutWorldBoundary` return on `ClipMode.Inside` before consulting `ClippingState`, so a wipeout whose clipping is switched off but whose stored mode is inverted is skipped today.
- `Hatch.ApplyTransform` transforms the raw OCS boundary as if it were world data and never folds the original elevation in, so `NormalizeExplodedClone` (resetting any non-world normal to +Z) is only right for a world-plane hatch that a mirror flipped to -Z.
- `Insert.Explode()` is one-to-one and ordered in 3.7.1; clones carry no handle, document or owner, so ordinal position is the only original/clone identity available. `Circle` explodes to `Ellipse`; every other type keeps its type.
- Custom arrow blocks have the tip at the block base point and the body along local -X (real-world example: a square plus a line from (-1,0) to (0,0)). AutoCAD scales the block by `ArrowSize x ScaleFactor` and rotates local +X to the outward direction at the tip; ACadSharp's own `Dimension.dimensionArrow` does the same.
- MLINE `Segment.Parameters`: `p[0]` offset along `Miter` (already multiplied by `ScaleFactor` in stored data), `p[1]` distance from that intersection to the element's actual start, `p[2..]` alternating break/resume positions. The only real-world sample with three values has `p[2]` equal to the segment length (a run to the end, no visible cut), so odd counts are normal and a break at or beyond the segment end means "no cut". Whether `p[2..]` are absolute positions or relative lengths cannot be settled from the available data (see 4.5).
- `DrawBlockContents` has no draw-time recursion guard, but the per-block scan it already runs before exploding (`ScanBlockSubtree`, reached through `BlockSubtreeNeedsHeal`) walks the original block graph and reports whether a cycle cut the walk short. That truncation flag is the cycle signal this design builds on. A draw-time guard keyed on `BlockRecord` identity could not work anyway: nested inserts reached during `Explode()` hold deep-cloned block records, a different key every time, and `Insert.Explode()` deep-clones the whole block graph before any drawing happens, so a genuine cycle overflows the stack inside ACadSharp before a draw-time guard would ever run. The guard has to be a pre-check on the original graph. `BlockRecord.Name` survives cloning and is the usable key where a name-level check is needed.
- Clone list sharing (probed): `MLine.Vertices`, `Leader.Vertices` and `Wipeout.ClipBoundaryVertices` are shared between a clone and its source; `LwPolyline.Vertices`, the `Spline` lists, `Polyline2D.Vertices` and `Hatch.Paths` are copied. `Explode()` overwrites the shared MLINE and LEADER lists in place, which is why the renderer snapshots and heals both. The wipeout clip list is shared but never written by `ApplyTransform`, so drawing wipeouts from the original needs no heal; its `UVector`/`VVector` are transformed as points, which is the actual defect to work around.

## 4. Design

### 4.1 Shared: insert placement helpers

One internal static class, `InsertPlacement` (`ACadSharp.Image/Rendering/InsertPlacement.cs`), gathers the maths that today lives in `TextRenderer.Place`/`Orient` and the per-type helpers of the dispatcher:

- `MapPoint(Transform? placement, XYZ point)`: identity when null.
- `MapVector(Transform? placement, XYZ vector)`: `MapPoint(v) - MapPoint(0)`, so translations never leak into direction vectors (the wipeout U/V trap).
- `MapOcsPoint(Transform? placement, XYZ normal, double elevation, XY point)`: OCS to world through `OcsTransform.For(normal)` plus elevation, then `MapPoint`.
- `Compose(Transform? outer, Insert inner)`: the placement of `inner`'s contents seen from outside `outer`.
- `IsSimilarity(Transform, out double scale)`: true when the linear part is a rotation (possibly with a reflection) times one uniform scale; used by 4.3.

Existing callers are moved onto these helpers where they duplicate them, without changing behaviour or goldens: `TextRenderer.Place`/`Orient` (whose `Placement` record is `(XY Origin, XY Direction, bool Mirrored, double Scale, double WidthScale)`), `DrawMLine`, and the placement paths of `DrawSolid(ImageRenderContext, ImageStyle, Solid, Transform?)` and `DrawLeader(ImageRenderContext, ImageStyle, Leader, Transform?)`.

### 4.2 Block content pairing and recursion

`DrawBlockContents` keeps `Explode()` and ordinal pairing. The compatibility relation it needs already exists as `UsesOriginalGeometry(Entity? original, Entity clone)`, which today requires an identical runtime type and admits TEXT, MTEXT, LEADER and non-world SOLID: extend that one relation with `Hatch` and `Wipeout` rather than adding a second. Add the `Circle` original with `Ellipse` clone case as an explicitly allowed conversion, and make a type mismatch notify once for that entity (`Warning`, "block entity {i} is a {A} but its exploded clone is a {B}; drawn from the clone") instead of returning a silent false, so a wrong original is never applied and the mismatch is visible. The existing count mismatch warning stays as the package-upgrade tripwire.

Cycles are caught before `Explode()`, not during drawing. `ScanBlockSubtree` already walks the original block graph and returns a truncation flag when a cycle cut the walk short; `DrawBlockContents` treats a truncated scan as "this block graph is circular", notifies (`Warning`, "block {name} references itself; skipped") and returns without exploding. That is the only point at which a cycle can be stopped, because `Insert.Explode()` deep-clones the block graph and would overflow the stack first. The same pre-check covers an arrow block whose contents lead back to the same arrow block (4.3).

`NormalizeExplodedClone` is removed: hatches are drawn from the original (4.6), so the clone's normal no longer matters.

### 4.3 Custom arrowhead blocks

When `Style.LeaderArrow` is set and `ArrowHeadEnabled`, `DrawLeader` draws the block instead of the default triangle and the NotImplemented notification goes away.

- Tip = first vertex; outward direction = `tip - vertices[1]` for straight leaders, the tangent of the first Bezier segment for spline leaders (from the existing Catmull-Rom conversion).
- Arrow transform = translate(tip) x rotate(local +X onto the outward direction) x scale(`ArrowSize x ScaleFactor`) x translate(-block base point), composed with the leader's own placement when it sits inside a block.
- If the composed transform is a similarity (4.1), a transient `Insert` is built for the arrow block (insert point, rotation, uniform scale, normal from the transform; `Attributes` cleared) and handed to `DrawBlockContents`, so text, MLINE healing, hatches and nested blocks inside the arrow block get every existing rule. The transient insert is never added to a document. If it is not a similarity (a non-uniformly scaled outer insert), the default triangle is drawn and a Warning explains why.
- Layer 0 and ByBlock inside the arrow block resolve against the leader, exactly as block children resolve against their insert today.
- The leader line is drawn first, the arrow after it, so a filled arrow covers the line end.

### 4.4 Inverted wipeout clips and clipping state

`WipeoutWorldBoundary(Wipeout)` becomes `WipeoutWorldRings(Wipeout, Transform? placement)`, returning zero, one or two world rings. It has three consumers today, all of which must be updated together: `DrawWipeout`, `EntityBounds.TryGet` (which bounds a wipeout by the region it actually draws), and `ImagePageRenderer.SelectViewportEntities` through `EntityBounds`. The rings are:

- image hidden (`ShowImage` off): none;
- clipping off (`ClippingState == false`), whatever `ClipMode` says: the full image frame;
- clipping on, `ClipMode.Outside`: the clip boundary (a rectangular pair expanded to four corners);
- clipping on, `ClipMode.Inside`: the full frame and the boundary.

`DrawWipeout` fills one ring with `FillPolygon` and two rings with `FillPath` (even-odd), both with the opaque background colour as today; the NotImplemented notification for inverted clips is removed. The insert point is mapped as a point and U/V as vectors through `placement`, so wipeouts inside blocks are drawn from the original rather than from the clone whose U/V `ApplyTransform` contaminated. `EntityBounds.TryGet(Entity, out BoundingBox, out Exception?)` bounds a wipeout by all ring points, so an inverted wipeout frames and culls by its full footprint in both the page framer and the viewport culler.

### 4.5 MLEDIT cut segments

Per segment `i -> i+1` and element `j`, the visible runs are computed from the element's parameters:

- `A = vertex[i].Position + p[0] x Miter`, `D = normalize(Direction)`, `S = A + p[1] x D`, `E` = the same construction at vertex `i+1` (for a closed MLINE the last segment wraps to vertex 0).
- `p[2], p[3], ...` are absolute distances from `S`: visible from `S` to `p[2]`, hidden to `p[3]`, visible to `p[4]`, and so on; an odd count ends hidden after the last value. Values are clamped to `[0, |E - S|]`, non-increasing or non-finite values end the list at that point with a Warning; a first break at or beyond the segment length means no cut.
- Endpoints are built in block space and mapped through `placement`, so mirrored and non-uniform inserts hold.
- Each visible run is one `DrawLine`/`DrawPolyline` with the element's resolved style. No-cut elements produce exactly the primitives they do today (goldens unchanged).

The absolute interpretation is the literal reading of the DXF reference. ezdxf's comments describe relative dash/gap lengths and neither ezdxf nor LibreDWG implements cuts, so this stays flagged in README as an interpretation to confirm against an AutoCAD-authored multi-cut fixture. The `HasFiniteGeometry` arm for MLINE validates every parameter and `Direction`/`Miter`, not only `p[0]`.

### 4.6 Tilted hatches inside blocks

`DrawHatch(ImageRenderContext, ImageStyle, Hatch)` gains a `Transform? placement` parameter and, for block children, is called with the original hatch and the block placement instead of the exploded clone: boundary points and `ExplodePattern()` segments are produced in the hatch's own OCS, mapped with `InsertPlacement.MapOcsPoint(placement, hatch.Normal, hatch.Elevation, p)` and then projected. Pattern expansion happens before placement, so non-uniform scale and mirroring show up in the transformed endpoints instead of being squeezed back into one angle and scale. Top-level hatches take the same path with a null placement, so their output is unchanged.

### 4.7 Multi-line attributes

`EntityRenderDispatcher` recognises an `AttributeBase` whose `AttributeType` is `MultiLine` or `ConstantMultiLine` before the generic `TextEntity` arm and calls `TextRenderer.DrawAttribute(context, style, attribute, placement)`, which lays out `attribute.MText` (value, rectangle width, height, attachment point, direction, rotation, line spacing, style) and emits it with the attribute's own layer, colour, transparency, visibility, handle and parent metadata: the observable entity stays ATTRIB (SVG `data-type`, `data-handle`, layer group). Placement is null for a top-level insert's attributes (their coordinates already include the insert), the outer placement only for a nested insert's attributes, and the full block placement for a constant multi-line ATTDEF drawn from block space. When `MText` is null (pre-2018 DWG or malformed file) the single-line path draws `Value` and a Warning says the multi-line layout was unavailable. Single-line attributes are byte-identical to today.

## 5. Notifications

All messages keep the `[{SubclassMarker}] Handle {handle:X}: ...` shape. Removed: leader custom-arrow NotImplemented, wipeout inverted-clip NotImplemented, MLINE stroke-cut Warning, attribute multi-line Warning, the "hatch on a tilted plane" limitation. Added: pairing mismatch Warning, block recursion Warning, non-similarity arrow Warning, MLINE malformed-parameter Warning, MLINE fill-cut NotImplemented, attribute missing-MText Warning.

## 6. Tests and goldens

- Unit tests per item in `EntityRenderDispatcherTests`/`TextRendererTests` following the existing `CreateContext` conventions (100x100 surface, CAD `(x, y)` at `SurfacePoint(x, 100 - y)`), covering the cases listed in the Codex note for each item, plus: pairing mismatch drawn from the clone with one warning, self-referencing block terminates with one warning, arrow inside a non-uniform insert falls back with a warning.
- New synthetic sample `SyntheticSamples.FidelityBlock()` with a custom-arrow leader, an inverted wipeout, a cut MLINE, a tilted hatch inside a block and a multi-line attribute, exercised by `EntityGoldenTests` as `fidelity.model.01.{png,svg}` (created once, then byte-identical).
- Existing baselines stay byte-identical except where a task names the golden and the cause.
- The comparison run against the private drawings (never named in the repository) must show no remaining NotImplemented notifications for these five items and parity within the current 99.8-100% band.

## 7. Documentation

README "Known limitations" drops the five items and gains: MLEDIT interpretation flagged as unconfirmed; wipeouts on transparent backgrounds; fill cuts; non-similarity arrow fallback. Spec 4.6 of the base design gets a pointer to this document.
