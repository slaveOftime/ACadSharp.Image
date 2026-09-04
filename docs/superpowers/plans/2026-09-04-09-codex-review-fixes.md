# Codex Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the verified findings of the second independent Codex review (range `fc99ba8..0131f28`): viewport draw order and crash-safety, renderer-aware page framing, block-reference composition for OCS solids and leader arrows, non-uniformly scaled block text, MLINE fallback defects, SVG whitespace, stale docs, CLI argument validation, dead code, CI path filters, and the test gaps the review named.

**Architecture:** Each fix is local. `ImagePage` gains an insertion-ordered draw sequence that interleaves viewports and paper entities; `ImagePageRenderer` selects viewport contents itself, in sorted order, with per-entity guards. A small `EntityBounds` helper gives `ComputeFrame` the same wipeout mapping and OCS handling the renderer uses. `DrawBlockContents` extends its "draw from the original through the insert transform" path (already used for TEXT/MTEXT/MLINE) to non-world SOLIDs and LEADERs. `TextRenderer.Placement` and `SurfaceText` carry a horizontal `WidthScale` so both surfaces can stretch glyphs for non-uniform insert scales.

**Tech Stack:** .NET 8/10, ACadSharp 3.7.1, SixLabors.ImageSharp 3.1.12 / Fonts 2.1.3, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-02-layers-and-svg-design.md` (binding; sections 4.6 and 5.3 amended here). Review source: the Codex report archived in the session scratchpad; each task names its finding.

**Findings declined or deferred (recorded for the reviewer):** warning aggregation by block (the library reports per entity by design; consumers aggregate); a split of `EntityRenderDispatcher` into components (behaviour-preserving refactor, deferred to its own plan); avoiding `Insert.Explode()` altogether (the Circle→Ellipse conversion under non-uniform scale is needed); frame computation that ignores filtered block children (a design choice: the frame follows the page, not the filter).

## Global Constraints

- ACadSharp `3.7.1`; SixLabors packages as pinned; no new NuGet dependencies; target frameworks unchanged.
- Coding conventions: `this.` prefix on instance members, explicit types except LINQ lambdas, XML docs on public and internal members and a `<summary>` on private helpers, `sealed` classes, file-scoped namespaces, four-space indent, UTF-8 without BOM (never add or remove a BOM), LF line endings.
- PNG baselines and SVG goldens in `ACadSharp.Image.Tests/Baselines/` are byte-identical except where a task says otherwise: Task 1 may move `viewport-sheet.paper.01.*` only if the paper draw order actually changes (measure and explain); Task 6 regenerates exactly the four SVG goldens that contain `<text` for `xml:space`; Task 7 creates new `entities.model.01.*` files. Regeneration uses the scoped commands given in the task, with the cause in the commit body. Never run the update variable on the whole suite.
- `dotnet build ACadSharp.Image.sln -warnaserror` warning-free; full suite green before each commit (`dotnet test ACadSharp.Image.sln --configuration Release -warnaserror`).
- No reference to any drawing outside `Samples/` in code, tests, comments or commit messages.
- Never use bare `git stash` / `git stash pop`. Commit messages end with the repository's two trailer lines (see any commit on this branch).
- Notifications use `ImageConfiguration.Notify(message, NotificationType[, exception])` with the message shape `[{entity.SubclassMarker}] Handle {handle:X}: ...`.
- `EntityRenderDispatcherTests.CreateContext` gives a 100x100 surface at scale 1 with no offset: CAD `(x, y)` lands at `SurfacePoint(x, 100 - y)`.

## File Structure

- Modify `ACadSharp.Image/ImagePage.cs` (draw sequence, `ComputeFrame` via `EntityBounds`), `ACadSharp.Image/ImageExporter.cs` (single sorted loop), `ACadSharp.Image/Rendering/ImagePageRenderer.cs` (`RenderTo`, `SelectViewportEntities`).
- Create `ACadSharp.Image/Rendering/EntityBounds.cs`.
- Modify `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (`DrawBlockContents`, `DrawSolid`, `DrawLeader`, `DrawMLine`, `WipeoutWorldBoundary`, MLINE subtree cache), `ACadSharp.Image/Rendering/TextRenderer.cs`, `ACadSharp.Image/Rendering/SurfaceText.cs`, `ACadSharp.Image/Rendering/RasterDrawingSurface.cs`, `ACadSharp.Image/Rendering/Svg/SvgDrawingSurface.cs`, `ACadSharp.Image/Rendering/ImageRenderContext.cs`, `ACadSharp.Image/Rendering/ImageStyleResolver.cs`, `ACadSharp.Image/ImageConfiguration.cs`, `ACadSharp.Image.Cli/Program.cs`, `.github/workflows/ci.yml`.
- Tests: `ImagePageTests.cs`, `ImagePageRendererTests.cs`, `EntityRenderDispatcherTests.cs`, `TextRendererTests.cs`, `SvgDrawingSurfaceTests.cs`, `RasterDrawingSurfaceTests.cs`, `CliTests.cs`, `SyntheticSamples.cs`, new `EntityGoldenTests.cs`.
- Docs: spec 4.6 and 5.3, README, plan 07 note.

---

### Task 1: Interleave viewports with paper entities and select viewport contents safely

**Finding:** Important 1. `ImagePageRenderer.RenderTo` draws every viewport before every paper entity, losing DRAWORDER between them; `Viewport.SelectEntities()` enumerates model space in stored order and calls `GetBoundingBox()` unguarded, so one malformed model entity (a bulge between coincident vertices, verified by probe to throw `ArgumentOutOfRangeException`) aborts the whole paper-space export.

**Files:**
- Modify: `ACadSharp.Image/ImagePage.cs` (fields, `AddEntity`, `AddViewport`, new `DrawSequence`)
- Modify: `ACadSharp.Image/ImageExporter.cs:100-125`
- Modify: `ACadSharp.Image/Rendering/ImagePageRenderer.cs` (`RenderTo`, `DrawViewport`, new `SelectViewportEntities`)
- Test: `ACadSharp.Image.Tests/ImagePageTests.cs`, `ACadSharp.Image.Tests/ImagePageRendererTests.cs`

**Interfaces:**
- Produces: `internal IReadOnlyList<CadObject> ImagePage.DrawSequence` (entities and viewports in the order they were added); `internal IEnumerable<Entity> ImagePageRenderer.SelectViewportEntities(Viewport viewport)`.
- Public `ImagePage.Entities` and `ImagePage.Viewports` keep their contents and order.

- [ ] **Step 1: Write the failing tests**

Append to `ImagePageTests`:

```csharp
    [Fact]
    public void DrawSequenceKeepsViewportsAndEntitiesInInsertionOrder()
    {
        ImagePage page = new();
        Line first = new(new XYZ(0, 0, 0), new XYZ(1, 0, 0));
        Viewport viewport = new() { Center = new XYZ(50, 50, 0), Width = 10, Height = 10 };
        Line last = new(new XYZ(0, 0, 0), new XYZ(0, 1, 0));

        page.AddEntity(first);
        page.AddViewport(viewport);
        page.AddEntity(last);

        Assert.Equal([first, viewport, last], page.DrawSequence);
        Assert.Equal([first, last], page.Entities);
        Assert.Equal([viewport], page.Viewports);
    }
```

Append to `ImagePageRendererTests` (follow that file's existing pattern for building a renderer over a `RecordingDrawingSurface`; if it has no such helper, build one there: an `ImagePageRenderer` from an `ImageConfiguration`, a page with `Layout` set, and render through the surface the same way `ImagePageRenderer.Render` does for PNG, or call the internal `RenderTo` through a small internal hook you add and document):

```csharp
    [Fact]
    public void PaperEntitiesAddedBeforeAViewportAreDrawnBeforeIt()
    {
        // A page built by ImageExporter from a layout whose title line sorts before the viewport must draw the line first.
        CadDocument document = new();
        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0)));
        Layout layout = new("Sheet") { PaperWidth = 200, PaperHeight = 100 };
        document.Layouts.Add(layout);
        layout.AssociatedBlock.Entities.Add(new Line(new XYZ(5, 5, 0), new XYZ(50, 5, 0)));
        layout.AssociatedBlock.Entities.Add(new Viewport { Center = new XYZ(100, 50, 0), Width = 50, Height = 50, ViewCenter = new XY(5, 0), ViewHeight = 20 });

        RecordingDrawingSurface surface = new();
        ImageExporter exporter = new();
        exporter.Add(layout);
        RenderThrough(exporter, surface);   // helper: render exporter.Pages[0] onto `surface` via ImagePageRenderer

        int line = surface.Calls.FindIndex(c => c.StartsWith("DrawLine", StringComparison.Ordinal));
        int viewport = surface.Calls.FindIndex(c => c.StartsWith("BeginViewport", StringComparison.Ordinal));
        Assert.True(line >= 0 && viewport >= 0 && line < viewport, $"expected the title line before the viewport, got line at {line}, viewport at {viewport}.");
    }

    [Fact]
    public void MalformedModelEntityDoesNotAbortViewportRendering()
    {
        CadDocument document = new();
        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0)));
        LwPolyline malformed = new();
        malformed.Vertices.Add(new LwPolyline.Vertex(new XY(5, 5)) { Bulge = 1 });
        malformed.Vertices.Add(new LwPolyline.Vertex(new XY(5, 5)));
        malformed.Vertices.Add(new LwPolyline.Vertex(new XY(9, 5)));
        document.Entities.Add(malformed);
        Layout layout = new("Sheet") { PaperWidth = 200, PaperHeight = 100 };
        document.Layouts.Add(layout);
        layout.AssociatedBlock.Entities.Add(new Viewport { Center = new XYZ(100, 50, 0), Width = 50, Height = 50, ViewCenter = new XY(5, 2), ViewHeight = 20 });

        RecordingDrawingSurface surface = new();
        ImageExporter exporter = new();
        List<NotificationEventArgs> notifications = new();
        exporter.Configuration.OnNotification += (_, e) => notifications.Add(e);
        exporter.Add(layout);

        RenderThrough(exporter, surface);

        Assert.Contains(surface.Calls, c => c.StartsWith("DrawLine", StringComparison.Ordinal));
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("bounds", StringComparison.OrdinalIgnoreCase));
    }
```

If `Layout.AssociatedBlock` is not populated for a code-built layout the way these tests assume, build the layout the way `SyntheticSamples.ViewportSheet()` does and say so in the report. The malformed polyline's `GetBoundingBox()` throws `ArgumentOutOfRangeException` on 3.7.1 (verified).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~DrawSequence|FullyQualifiedName~PaperEntitiesAddedBefore|FullyQualifiedName~MalformedModelEntity"`
Expected: FAIL (no `DrawSequence`; viewport drawn first; the malformed polyline throws out of `SelectEntities`).

- [ ] **Step 3: Implement**

`ImagePage`: add `private readonly List<CadObject> _drawSequence = [];` appended to by both `AddEntity` and `AddViewport`, exposed as `internal IReadOnlyList<CadObject> DrawSequence => this._drawSequence;` with an XML summary ("Entities and viewports in the order they were added, which is the order they are drawn in.").

`ImageExporter.Add(Layout)`: replace the two loops with one over `layout.AssociatedBlock.GetSortedEntities()`: a `Viewport` that `RepresentsPaper` is skipped, any other `Viewport` goes to `page.AddViewport`, everything else to `page.AddEntity`. Remove `ShouldIncludeEntity` if it becomes unused (check `Add(BlockRecord)` still filters viewports out; keep the filter there).

`ImagePageRenderer.RenderTo`:

```csharp
        foreach (CadObject item in page.DrawSequence)
        {
            if (item is Viewport viewport)
            {
                this.DrawViewport(context, viewport);
            }
            else if (item is Entity entity)
            {
                this._dispatcher.Draw(context, entity);
            }
        }
```

`DrawViewport`: replace `viewport.SelectEntities()` with `this.SelectViewportEntities(viewport)`:

```csharp
    /// <summary>
    /// The model-space entities a viewport shows, in the drawing's draw order: those whose bounding box lies in or
    /// crosses the view box (what <c>Viewport.SelectEntities</c> does) minus the ones whose bounds ACadSharp cannot
    /// compute, which are skipped with a warning instead of aborting the page.
    /// </summary>
    internal IEnumerable<Entity> SelectViewportEntities(Viewport viewport)
    {
        if (viewport.Document == null)
        {
            this._configuration.Notify($"[{viewport.SubclassMarker}] Handle {viewport.Handle.ToString("X", CultureInfo.InvariantCulture)}: viewport has no document; skipped.", NotificationType.Warning);
            yield break;
        }

        BoundingBox box = viewport.GetModelBoundingBox();
        foreach (Entity entity in viewport.Document.ModelSpace.GetSortedEntities())
        {
            BoundingBox bounds;
            try
            {
                bounds = entity.GetBoundingBox();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                this._configuration.Notify($"[{entity.SubclassMarker}] Handle {entity.Handle.ToString("X", CultureInfo.InvariantCulture)}: bounds could not be computed ({ex.Message}); entity skipped in viewport.", NotificationType.Warning, ex);
                continue;
            }

            if (box.IsIn(bounds, out bool partial) || partial)
            {
                yield return entity;
            }
        }
    }
```

Add `using System.Globalization;` if missing. Keep `DrawViewport`'s own `viewport.GetBoundingBox()` (it cannot throw for a viewport).

- [ ] **Step 4: Run the tests and the suite; measure baselines**

Run the three new tests → PASS. Run the full suite. If `viewport-sheet.paper.01.png`/`.svg` change, confirm with a diff of the SVG that only element order moved (the frame line and title now precede or follow the viewport as the handles dictate), regenerate them with `ACADSHARP_IMAGE_UPDATE_BASELINES=1 dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~ViewportParityTests"`, and list them with the cause in the commit body. Any other baseline change: stop and report.

- [ ] **Step 5: Docs**

Spec 4.6 draw-order bullet: append "Paper-space viewports are interleaved with paper entities in the same sorted order; a viewport's contents are the sorted model-space entities whose bounds lie in or cross its view box, and an entity whose bounds cannot be computed is skipped with a Warning." README "Supported entities" draw-order sentence: add "including paper-space viewports".

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/ImagePage.cs ACadSharp.Image/ImageExporter.cs ACadSharp.Image/Rendering/ImagePageRenderer.cs ACadSharp.Image.Tests/ImagePageTests.cs ACadSharp.Image.Tests/ImagePageRendererTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md ACadSharp.Image.Tests/Baselines
git commit -m "Draw viewports in page order and survive malformed model entities in them"
```

---

### Task 2: Frame pages from renderer-consistent bounds and tolerate unresolved block references

**Findings:** Important 2 (frames use `Entity.GetBoundingBox()`, which ignores the wipeout U/V mapping and OCS solid normals) and Important 7 (an `Insert` whose `Block` is null makes `Explode()` and `GetBoundingBox()` throw `NullReferenceException`, outside every catch filter).

**Files:**
- Create: `ACadSharp.Image/Rendering/EntityBounds.cs`
- Modify: `ACadSharp.Image/ImagePage.cs` (`ComputeFrame`), `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (`DrawWipeout` region extraction, `DrawBlockContents` guard, `HasFiniteGeometry`)
- Test: `ACadSharp.Image.Tests/ImagePageTests.cs`, `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`

**Interfaces:**
- Produces: `internal static class EntityBounds { public static bool TryGet(Entity entity, out BoundingBox bounds); }`; `internal static IReadOnlyList<XYZ> EntityRenderDispatcher.WipeoutWorldBoundary(Wipeout wipeout)` (the mapped region the renderer fills, or empty when nothing would be drawn).

- [ ] **Step 1: Write the failing tests**

Append to `ImagePageTests`:

```csharp
    [Fact]
    public void FrameUsesTheMappedWipeoutRegionNotTheRawPixelVertices()
    {
        // Pixel space rotated 90 degrees: U up, V left. Raw vertices span 1 unit; the mapped region spans 5.
        Wipeout wipeout = new()
        {
            InsertPoint = new XYZ(10, 10, 0),
            UVector = new XYZ(0, 5, 0),
            VVector = new XYZ(-5, 0, 0),
            Size = new XY(1, 1),
            ClippingState = true,
            ClipType = ClipType.Rectangular,
        };
        wipeout.ClipBoundaryVertices.AddRange([new XY(-0.5, -0.5), new XY(0.5, 0.5)]);
        ImagePage page = new();
        page.AddEntity(wipeout);

        PageFrame frame = Assert.NotNull(page.ComputeFrame(null)).Value;

        // Mapped corners: (10,10)+(x+0.5)U+(1-y-0.5)V for the four corners → x in [5,10], y in [10,15].
        Assert.Equal(5d, frame.PaperWidth, 6);
        Assert.Equal(5d, frame.PaperHeight, 6);
    }

    [Fact]
    public void FrameMirrorsAnOcsSolidLikeTheRenderer()
    {
        Solid solid = new() { FirstCorner = new XYZ(0, 0, 0), SecondCorner = new XYZ(10, 0, 0), ThirdCorner = new XYZ(0, 5, 0), FourthCorner = new XYZ(10, 5, 0), Normal = new XYZ(0, 0, -1) };
        ImagePage page = new();
        page.AddEntity(solid);

        PageFrame frame = Assert.NotNull(page.ComputeFrame(null)).Value;

        // A (0,0,-1) normal mirrors X: the solid spans x in [-10, 0].
        Assert.Equal(-10d, frame.Translation.X * -1d, 6);   // adjust to the sign convention PageFrame.Translation uses; see PageFrame.Of
        Assert.Equal(10d, frame.PaperWidth, 6);
        Assert.Equal(5d, frame.PaperHeight, 6);
    }

    [Fact]
    public void FrameSkipsAnInsertWithoutABlock()
    {
        Insert insert = new(new BlockRecord("GONE")) { InsertPoint = new XYZ(1000, 1000, 0) };
        typeof(Insert).GetProperty(nameof(Insert.Block))!.SetValue(insert, null);
        ImagePage page = new();
        page.AddEntity(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0)));
        page.AddEntity(insert);

        PageFrame frame = Assert.NotNull(page.ComputeFrame(null)).Value;

        Assert.Equal(10d, frame.PaperWidth, 6);
    }
```

Read `PageFrame.Of`/`ComputeFrame` to express the translation assertion correctly before running (the comment marks the line to adjust); the width/height assertions are the substance.

Append to `EntityRenderDispatcherTests`:

```csharp
    [Fact]
    public void InsertWithoutABlockIsSkippedWithAWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        Insert insert = new(new BlockRecord("GONE"));
        typeof(Insert).GetProperty(nameof(Insert.Block))!.SetValue(insert, null);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("Draw", StringComparison.Ordinal));
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("no block", StringComparison.OrdinalIgnoreCase));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~Frame|FullyQualifiedName~InsertWithoutABlock"`
Expected: FAIL (raw vertex bounds; OCS ignored; `NullReferenceException`).

- [ ] **Step 3: Implement**

In `EntityRenderDispatcher`, extract the region computation of `DrawWipeout` into

```csharp
    /// <summary>
    /// The world polygon a wipeout masks: its clip boundary (a rectangular pair expanded to four corners) or the whole
    /// image frame when clipping is off, mapped through <see cref="WipeoutPixelToWorld"/>. Empty when the wipeout
    /// would draw nothing (image hidden or an inverted clip).
    /// </summary>
    internal static IReadOnlyList<XYZ> WipeoutWorldBoundary(Wipeout wipeout)
```

and make `DrawWipeout` use it (the notifications for `ClipMode.Inside` and transparent backgrounds stay in `DrawWipeout`).

Create `EntityBounds`:

```csharp
/// <summary>
/// Bounds the renderer would actually draw, for page framing. ACadSharp's <c>GetBoundingBox</c> ignores a wipeout's
/// pixel vectors and a solid's extrusion normal, and throws for some malformed geometry; this helper applies the
/// renderer's own mapping for those and reports failure instead of throwing.
/// </summary>
internal static class EntityBounds
{
    public static bool TryGet(Entity entity, out BoundingBox bounds)
    {
        bounds = default;
        switch (entity)
        {
            case Insert insert when insert.Block == null:
                return false;
            case Wipeout wipeout:
                return TryFromPoints(EntityRenderDispatcher.WipeoutWorldBoundary(wipeout), out bounds);
            case Solid solid when !OcsTransform.IsWorldPlane(solid.Normal):
                OcsTransform toWorld = OcsTransform.For(solid.Normal);
                return TryFromPoints([ToWorld(toWorld, solid.FirstCorner), ToWorld(toWorld, solid.SecondCorner), ToWorld(toWorld, solid.ThirdCorner), ToWorld(toWorld, solid.FourthCorner)], out bounds);
        }

        try
        {
            bounds = entity.GetBoundingBox();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // ACadSharp throws for some malformed geometry (e.g. a bulge between coincident vertices).
            return false;
        }
    }

    private static XYZ ToWorld(OcsTransform toWorld, XYZ corner) => toWorld.ToWorld(corner.X, corner.Y, corner.Z);

    private static bool TryFromPoints(IReadOnlyList<XYZ> points, out BoundingBox bounds)
    {
        bounds = default;
        if (points.Count == 0)
        {
            return false;
        }

        double minX = points.Min(p => p.X), minY = points.Min(p => p.Y), minZ = points.Min(p => p.Z);
        double maxX = points.Max(p => p.X), maxY = points.Max(p => p.Y), maxZ = points.Max(p => p.Z);
        bounds = new BoundingBox(new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
        return true;
    }
}
```

(Split the `double` declarations onto separate lines per the conventions; check `OcsTransform.ToWorld(x, y, elevation)` returns `XYZ` — it does.) `ImagePage.ComputeFrame` replaces its `try { entity.GetBoundingBox() } catch` block with `if (!EntityBounds.TryGet(entity, out BoundingBox boundingBox)) { continue; }` and keeps the non-finite check after it.

`DrawBlockContents`: first statement

```csharp
        if (insert.Block == null)
        {
            this._configuration.Notify($"[{insert.SubclassMarker}] Handle {insert.Handle.ToString("X", CultureInfo.InvariantCulture)}: block reference has no block; skipped.", NotificationType.Warning);
            return;
        }
```

and simplify the later `insert.Block?.` uses accordingly.

- [ ] **Step 4: Run the tests and the suite**

New tests → PASS. Full suite green; `git status --short ACadSharp.Image.Tests/Baselines` empty (no sample contains a wipeout or an OCS solid at page level; the synthetic features solid is on the world plane — if `features.model.01.*` moves, stop and report).

- [ ] **Step 5: Docs**

Spec 4.6: add bullet "Framing: auto-sized pages are framed with `EntityBounds`, which applies the wipeout pixel mapping and solid OCS normals the renderer uses and skips entities whose bounds ACadSharp cannot compute; an `Insert` without a block is skipped with a Warning everywhere." README: in the "Supported entities" paragraph add "A block reference whose block is missing is skipped with a warning."

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityBounds.cs ACadSharp.Image/ImagePage.cs ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/ImagePageTests.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Frame pages from the geometry the renderer draws and skip block references without a block"
```

---

### Task 3: Compose OCS solids and leader arrows with the insert transform; document nested order and thread safety

**Findings:** Important 4 (a block SOLID with a non-world normal gets the insert transform applied to raw OCS corners and the normal applied afterwards, i.e. in the wrong order; a LEADER arrow inside a scaled insert keeps its unscaled size), Important 5 (block interiors below the first nesting level come back from ACadSharp's block clone in handle order, so "stored order" holds only at the first level), Important 6 (the MLINE heal mutates shared lists; concurrent rendering of one document is unsafe, and was already unsafe through `Explode()` itself).

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (`Draw` signature comment, switch arms for `Solid` and `Leader`, `DrawSolid`, `DrawLeader`, `DrawBlockContents` pairing)
- Modify: `ACadSharp.Image/ImageExporter.cs` (`Render` remarks), `README.md`, spec 4.6
- Test: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`

**Interfaces:**
- The private `Draw(..., Entity? source = null, Transform? placement = null)` parameter is renamed from `textSource` to `source` and now carries the original for TEXT, MTEXT, non-world SOLID and LEADER.
- `DrawSolid(ImageRenderContext, ImageStyle, Solid, Transform? placement)` and `DrawLeader(ImageRenderContext, ImageStyle, Leader, Transform? placement)` draw the given entity's geometry in its own coordinates and map every point through `placement` (null at top level).

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void OcsSolidInsideAnInsertAppliesTheNormalBeforeTheInsertTransform()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("PLATE");
        block.Entities.Add(new Solid { FirstCorner = new XYZ(0, 0, 0), SecondCorner = new XYZ(10, 0, 0), ThirdCorner = new XYZ(0, 5, 0), FourthCorner = new XYZ(10, 5, 0), Normal = new XYZ(0, 0, -1) });
        Insert insert = new(block) { InsertPoint = new XYZ(20, 0, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        // Normal (0,0,-1) mirrors X in OCS→world: corners x in [-10,0]; then the insert moves them by +20: x in [10,20].
        IReadOnlyList<SurfacePoint> polygon = Assert.Single(surface.Polygons);
        Assert.Equal(new HashSet<SurfacePoint> { new(20, 100), new(10, 100), new(10, 95), new(20, 95) }, polygon.ToHashSet());
    }

    [Fact]
    public void LeaderArrowInsideAScaledInsertScalesWithIt()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("NOTE");
        block.Entities.Add(new Leader { ArrowHeadEnabled = true, Vertices = { new XYZ(0, 0, 0), new XYZ(10, 0, 0) }, Style = new DimensionStyle("ARROW") { ArrowSize = 3, ScaleFactor = 1 } });
        Insert insert = new(block) { InsertPoint = new XYZ(5, 5, 0), XScale = 2, YScale = 2 };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        IReadOnlyList<SurfacePoint> arrow = Assert.Single(surface.Polygons);
        Assert.Equal(new SurfacePoint(5, 95), arrow[0]);
        // Source-space base at x=3 with half-width 0.5, scaled by 2 and moved by (5,5): x=11, y=5±1.
        Assert.Contains(arrow, p => Math.Abs(p.X - 11) < 1e-9 && Math.Abs(p.Y - 94) < 1e-9);
        Assert.Contains(arrow, p => Math.Abs(p.X - 11) < 1e-9 && Math.Abs(p.Y - 96) < 1e-9);
        Assert.Equal([new SurfacePoint(5, 95), new SurfacePoint(25, 95)], Assert.Single(surface.Polylines));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Expected: FAIL (mirrored corners land at x in [20,30] or the OCS is applied after the move; the arrow base sits at x = 8).

- [ ] **Step 3: Implement**

In `DrawBlockContents`, extend the pairing:

```csharp
                if ((original is TextEntity or MText || original is Solid { } s && !IsWorldPlane(s.Normal) || original is Leader) && original.GetType() == entity.GetType())
                {
                    source = original;
                    entityPlacement = transform;
                }
```

(write it as readable nested conditions rather than one expression if clearer). Switch arms: `case Solid solid: DrawSolid(context, style, source as Solid ?? solid, placement);` and `case Leader leader: this.DrawLeader(context, style, source as Leader ?? leader, placement);`. Rename `textSource` to `source` in the signature and the two text arms, and update the comment above `Draw` to: "source is the original block entity a TEXT, MTEXT, non-world SOLID or LEADER clone came from, whose geometry is used instead of the clone's, and placement is the transform of the insert that placed it; both are null outside a block reference."

`DrawSolid` maps each corner OCS → world → placement: `XYZ world = toWorld != null ? toWorld.ToWorld(corner.X, corner.Y, corner.Z) : corner; return context.ToSurfacePoint(placement == null ? world : placement.ApplyTransform(world));`. For a world-plane solid clone `placement` is null (the clone was transformed by `Explode()`), so existing output is unchanged.

`DrawLeader` computes the path and the arrow in the leader's own coordinates and maps every point with a local `SurfacePoint Map(XYZ p) => context.ToSurfacePoint(placement == null ? p : placement.ApplyTransform(p));`: path points `leader.Vertices.Select(Map)`, Catmull-Rom on the mapped points (affine maps commute with the Catmull-Rom construction), arrow corners built in source space (`tip`, `baseCenter ± half` as today) then mapped. Since a top-level leader has `placement == null`, its output is unchanged.

Docs: spec 4.6 draw-order bullet, replace the stored-order clause with "The contents of a block reference are drawn in the block's stored order at the first nesting level; deeper levels come back from ACadSharp's block clone in handle order (`BlockRecord.Clone()` enumerates `GetSortedEntities()`), so DRAWORDER inside nested blocks is honoured only there." README "Supported entities": same sentence, shorter. Thread safety: `ImageExporter.Render` `<remarks>`: "Rendering temporarily mutates block MLINEs while working around ACadSharp 3.7.1's destructive `MLine.Clone()` and restores them before returning; a `CadDocument` must not be rendered concurrently by two exporters, and `Insert.Explode()` itself is not safe for concurrent use either." README: add the same sentence under the SVG/PNG paragraph or a new "Thread safety" note.

- [ ] **Step 4: Run the tests and the suite**

New tests → PASS; full suite green; baselines unchanged (the features block's solid is world-plane; no sample has a block leader).

- [ ] **Step 5: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image/ImageExporter.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Place block solids and leaders from their originals through the insert transform"
```

---

### Task 4: Carry the horizontal scale of non-uniformly scaled block text

**Finding:** Important 3. `TextRenderer.Place` normalises the transformed reading direction and keeps only the up-vector length, so an insert with `XScale = 2, YScale = 1` draws its text at natural width and wraps MTEXT at the unscaled rectangle width.

**Files:**
- Modify: `ACadSharp.Image/Rendering/TextRenderer.cs` (`Placement`, `Place`, both `Draw`s), `ACadSharp.Image/Rendering/SurfaceText.cs`, `ACadSharp.Image/Rendering/Svg/SvgDrawingSurface.cs` (`DrawText`), `ACadSharp.Image/Rendering/RasterDrawingSurface.cs` (`DrawText`)
- Test: `ACadSharp.Image.Tests/TextRendererTests.cs`, `ACadSharp.Image.Tests/SvgDrawingSurfaceTests.cs`, `ACadSharp.Image.Tests/RasterDrawingSurfaceTests.cs`

**Interfaces:**
- `Placement(XY Origin, XY Direction, bool Mirrored, double Scale, double WidthScale)` where `WidthScale` is the length of the transformed unit reading direction.
- `SurfaceText` gains a trailing positional parameter `double WidthScale = 1d`: the factor glyph advances are stretched by along the baseline relative to `Height` (1 = natural). `WrappingWidth` and `FixedLength` are expressed in surface units of the stretched run.

- [ ] **Step 1: Write the failing tests**

`TextRendererTests` (use that file's existing helpers for drawing an entity inside an insert onto a `RecordingDrawingSurface`; the earlier uniform-scale test is the model):

```csharp
    [Fact]
    public void NonUniformInsertScaleStretchesTextHorizontally()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("LABEL");
        block.Entities.Add(new MText { Value = "Wide", InsertPoint = new XYZ(0, 0, 0), Height = 4, RectangleWidth = 30 });
        Insert insert = new(block) { InsertPoint = new XYZ(10, 10, 0), XScale = 2, YScale = 1 };

        new EntityRenderDispatcher(configuration).Draw(EntityRenderDispatcherTests.CreateContext(surface, configuration), insert);

        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal(4d, run.Height, 9);
        Assert.Equal(2d, run.WidthScale, 9);
        Assert.Equal(60d, run.WrappingWidth, 9);
    }
```

(make `CreateContext` internal static if it is private.) `SvgDrawingSurfaceTests`: draw a `SurfaceText("AB", origin (10, 20), height 4, rotation 0, Start, Alphabetic, -1, 1, -1, WidthScale: 2)` and assert the `<text>` element's `transform` attribute equals `translate(10 20) scale(2 1) translate(-10 -20)`; with rotation π/2 as well assert it starts with `rotate(-90 10 20) ` followed by the same scale triple; with `WidthScale` 1 assert there is no `transform` attribute for rotation 0 (golden safety). `RasterDrawingSurfaceTests`: `DrawnText` gains an optional `double widthScale = 1d`; assert the ink column span of `"H"` at `widthScale: 2` is about twice the span at 1 (within 2 px) while the ink row span is unchanged (within 1 px).

- [ ] **Step 2: Run the tests to verify they fail**

Expected: compile failures for `WidthScale`, then FAIL.

- [ ] **Step 3: Implement**

`Place` returns `new Placement(new XY(o.X, o.Y), direction / length, mirrored, scale, length)`. Both `Draw`s pass `WidthScale: p.WidthScale / p.Scale` to `SurfaceText`, and the MTEXT wrapping width becomes `context.ToSurfaceLength(mtext.RectangleWidth * p.WidthScale)`. `GetFixedLength` already measures transformed points; leave it.

`SvgDrawingSurface.DrawText`: wrap at `text.WrappingWidth / text.WidthScale` (the stretch is applied by the transform), set `textLength` to `text.FixedLength / text.WidthScale`, and build the transform list: rotation part as today, then when `Math.Abs(text.WidthScale - 1d) > 1e-9` append `translate({x} {y}) scale({sx} 1) translate({-x} {-y})` (space-separated, numbers through `this.N`/`this.S`). Validate `WidthScale` in the finiteness guard.

`RasterDrawingSurface.DrawText`: `WrappingLength` divided by `text.WidthScale`; `drawingOptions.Transform = Matrix3x2.CreateScale((float)text.WidthScale, 1f, new Vector2(origin.X, origin.Y)) * Matrix3x2.CreateRotation((float)-text.Rotation, new Vector2(origin.X, origin.Y))` when either differs from identity (scale first, then rotation; check SixLabors' `Matrix3x2` multiplication order gives "scale then rotate" and adjust if the row-vector convention reverses it: the rotated `"H"` at `widthScale: 2` must widen along its own baseline, not along the canvas X axis — add that as a third raster assertion with rotation π/2: the ink ROW span doubles).

- [ ] **Step 4: Run the tests and the suite**

New tests → PASS. Full suite green; all baselines byte-identical (`WidthScale` is 1 everywhere in the samples: uniform scales only).

- [ ] **Step 5: Docs**

Spec 5.3 text bullet: append "**Amended 2026-09-04:** block text under a non-uniform insert scale carries `SurfaceText.WidthScale` (transformed reading-axis length over up-axis length); the SVG emits `translate scale translate` after the rotation and the raster composes the same matrix; wrapping width and fixed length follow the reading axis." README fidelity paragraph: replace the height sentence with "Text height follows the transformed up axis and width the transformed reading axis of the block reference that placed it."

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/Rendering/TextRenderer.cs ACadSharp.Image/Rendering/SurfaceText.cs ACadSharp.Image/Rendering/Svg/SvgDrawingSurface.cs ACadSharp.Image/Rendering/RasterDrawingSurface.cs ACadSharp.Image.Tests/TextRendererTests.cs ACadSharp.Image.Tests/SvgDrawingSurfaceTests.cs ACadSharp.Image.Tests/RasterDrawingSurfaceTests.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Stretch block text along the reading axis under non-uniform insert scales"
```

---

### Task 5: MLINE fallback under signed scale, inherited element linetypes, finite validation, and a block-subtree cache

**Findings:** Minor 2 (extrema chosen before applying a negative `ScaleFactor`, so Top/Bottom anchor the wrong element; an element `LineType` named ByLayer/ByBlock is handed to `LineTypeDashResolver` and comes back solid instead of inheriting; non-finite style offsets or scale reach the fallback), Minor 3 (every insert walks its whole block subtree looking for MLINEs).

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (`DrawMLine`, `DrawBlockContents`, new `_blocksWithMLines` cache), `ACadSharp.Image/Rendering/ImageStyleResolver.cs` (`IsNamed` made `internal static`)
- Test: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void MLineFallbackUnderANegativeScaleAnchorsTheGeometricTopElement()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLine mline = new() { Style = TwoElementStyle(0.5), ScaleFactor = -2, Justification = MLineJustification.Top, Vertices = { VertexAt(0, 10), VertexAt(20, 10) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        // Scaled offsets are -1 (element 0) and +1 (element 1); Top puts the +1 element on the vertex line and element 0 two units below it.
        Assert.Equal([new SurfacePoint(0, 92), new SurfacePoint(20, 92)], surface.Polylines[0]);
        Assert.Equal([new SurfacePoint(0, 90), new SurfacePoint(20, 90)], surface.Polylines[1]);
    }

    [Fact]
    public void MLineElementWithByLayerLinetypeInheritsTheEntityDashes()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLineStyle style = new("DASHED");
        style.AddElement(new MLineStyle.Element { Offset = 0.5, LineType = new LineType(LineType.ByLayerName) });
        style.AddElement(new MLineStyle.Element { Offset = -0.5 });
        LineType dashed = new("DASHED2");
        dashed.AddSegment(new LineType.Segment { Length = 2 });
        dashed.AddSegment(new LineType.Segment { Length = -1 });
        MLine mline = new() { Style = style, LineType = dashed, Vertices = { VertexAt(0, 10, [0.5, 0], [-0.5, 0]), VertexAt(20, 10, [0.5, 0], [-0.5, 0]) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.NotNull(surface.Styles[0].DashPattern);
        Assert.Equal(surface.Styles[1].DashPattern, surface.Styles[0].DashPattern);
    }

    [Fact]
    public void MLineWithANonFiniteScaleIsSkippedWithAWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        MLine mline = new() { Style = TwoElementStyle(0.5), ScaleFactor = double.NaN, Vertices = { VertexAt(0, 10), VertexAt(20, 10) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.Empty(surface.Polylines);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("non-finite", StringComparison.OrdinalIgnoreCase));
    }
```

Check `LineType.Segment`/`AddSegment` names against 3.7.1 (`LineTypeDashResolverTests` builds linetypes already; copy its construction) and adjust.

- [ ] **Step 2: Run the tests to verify they fail**

Expected: FAIL (element 0 on the vertex line; solid element; NaN offsets drawn or exception).

- [ ] **Step 3: Implement**

`DrawMLine`: compute `double[] scaled = elements.Select(e => e.Offset * scale).ToArray();` and take `maxOffset`/`minOffset` and the `outer`/`inner` indices from `scaled`; shift becomes `-max(scaled)` / `-min(scaled)`. Before any drawing, when the fallback is needed for any vertex (or unconditionally, it is cheap): if `!double.IsFinite(scale) || scaled.Any(v => !double.IsFinite(v))` → Warning `"[...] Handle X: multiline style has non-finite offsets or scale; entity skipped."` and return. Element linetype: `LineType? elementType = elements[j].LineType; float[]? dashes = elementType == null || ImageStyleResolver.IsNamed(elementType, LineType.ByLayerName) || ImageStyleResolver.IsNamed(elementType, LineType.ByBlockName) ? style.DashPattern : LineTypeDashResolver.Resolve(...)`. Make `IsNamed` `internal static` with a `<summary>`.

Cache: `private readonly Dictionary<BlockRecord, bool> _blocksWithMLines = new();` on the dispatcher; `private bool BlockSubtreeHasMLines(BlockRecord block, HashSet<BlockRecord> visited)` memoised per block (an MLINE directly in the block, or any nested `Insert.Block` subtree with one). `DrawBlockContents` calls `CollectMLines` only when `BlockSubtreeHasMLines(insert.Block, new HashSet<BlockRecord>())` is true; otherwise `mlineVertices` stays empty and the streaming `Explode()` path is taken. The dispatcher is created per page render, so the cache lives exactly as long as one render.

- [ ] **Step 4: Run the tests and the suite**

New tests → PASS; the existing MLINE tests (including the nested-block one) stay green; full suite green; baselines unchanged.

- [ ] **Step 5: Docs**

Spec 4.6 MLINE bullet: add "justification fallback uses the signed scaled offsets; element linetypes named ByLayer/ByBlock inherit the entity's dashes; a style with non-finite offsets or scale skips the entity with a Warning; block subtrees are scanned for MLINEs once per render and cached."

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image/Rendering/ImageStyleResolver.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Fix MLINE fallback offsets and inherited element linetypes, cache block MLINE scans"
```

---

### Task 6: SVG whitespace, stale docs, CLI positional arguments, dead code, CI paths

**Findings:** Minor 1 (`<text>` collapses the repeated spaces the wrapper preserves), Minor 4 (`Dpi` and `FontFamilyName` XML docs are stale), Minor 5 (extra positional CLI arguments are silently ignored), Minor 6 (unused `ImageStyleResolver.Resolve` and the `ImagePage`-based SVG context overloads), Pass 3 CI verdict (path filters miss the props/solution files; no explicit build step), Pass 1 baseline note (plan 07's constraint was never amended for commit `3c3793c`).

**Files:**
- Modify: `ACadSharp.Image/Rendering/Svg/SvgDrawingSurface.cs` (`DrawText`), `ACadSharp.Image/ImageConfiguration.cs:141-148, 212-220`, `ACadSharp.Image.Cli/Program.cs:228-232`, `ACadSharp.Image/Rendering/ImageStyleResolver.cs` (remove `Resolve`), `ACadSharp.Image/Rendering/ImageRenderContext.cs` (remove `ComputeSvgViewBox(ImagePage, …)`, `ComputeSvgFitScale(ImagePage, …)`, `CreateSvgPageContext(IDrawingSurface, ImagePage, …)`; keep `CreatePageContext(IDrawingSurface, ImagePage, …)`, which tests use), `.github/workflows/ci.yml`, `docs/superpowers/plans/2026-09-03-07-text-fidelity.md:17`
- Test: `ACadSharp.Image.Tests/SvgDrawingSurfaceTests.cs`, `ACadSharp.Image.Tests/CliTests.cs`

- [ ] **Step 1: Write the failing tests**

`SvgDrawingSurfaceTests`: drawing `SurfaceText("A  B", ...)` yields a `<text>` element with attribute `xml:space="preserve"` (check with `XNamespace.Xml + "space"`). `CliTests`: `Program.Run(["a.dxf", "b.dxf"], out, err)` returns 1 and `err` contains `Unexpected argument 'b.dxf'` (mirror the file's existing style for error assertions; the input need not exist because parsing fails first — verify the parse runs before the file check, otherwise create a temp file).

- [ ] **Step 2: Run the tests to verify they fail**

Expected: FAIL.

- [ ] **Step 3: Implement**

`DrawText`: add `new XAttribute(XNamespace.Xml + "space", "preserve")` to every `<text>` element. `ImageConfiguration.Dpi` remarks → "This value affects line weight calculations only; text is sized from the drawing on both backends. Default is 96 DPI." `FontFamilyName` remarks → "If the family is not installed, the first installed family of the fallback chain `FontResolver.Fallbacks` (Liberation Sans, DejaVu Sans, Arial, Helvetica, Noto Sans, Segoe UI) is used, then the first installed family; when no font is installed, raster text is skipped with a warning and SVG text is emitted unwrapped." (Verify the no-font behaviour in `FontResolver`/`RasterDrawingSurface` and word the sentence to match.) `Program.cs`: `if (inputPath != null) { throw new ArgumentException($"Unexpected argument '{current}'."); } inputPath = current;` (use whatever exception type the parser already uses for bad values so `Run` reports it the same way). Remove the dead members and any XML `<see cref>` pointing at them. CI: add `'Directory.Packages.props'`, `'Directory.Build.props'`, `'*.sln'` to both path lists, and a `Build` step `dotnet build ACadSharp.Image.sln --configuration Release --no-restore -warnaserror` before the test step (then `dotnet test ... --no-build`). Plan 07 line 17: append "**Amended 2026-09-03 (final review):** Commit B of the final fix wave regenerated `HSK80AHCP16190M_BMG.model.01.png` and `features.model.01.png` for the 5/3 line spacing, with the cause in its body."

- [ ] **Step 4: Run the tests and the suite; regenerate the text goldens**

The four SVG goldens with `<text` fail on the new attribute. Diff one regenerated golden against the old to confirm the only change is the attribute, then regenerate exactly those with `ACADSHARP_IMAGE_UPDATE_BASELINES=1 dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~SampleParityTests.SampleSvgsMatchGoldens|FullyQualifiedName~FeatureGoldenTests.FeatureSvgMatchesGoldenAndContainsEveryPrimitive|FullyQualifiedName~ViewportParityTests"`; verify with `git diff --stat ACadSharp.Image.Tests/Baselines` that only `.svg` files changed and, for each, `git diff` shows only `xml:space` insertions. PNG baselines must be byte-identical.

- [ ] **Step 5: Docs**

Spec 5.3 text bullet: append "`<text>` carries `xml:space="preserve"` so the whitespace the wrapper keeps is rendered." README fidelity paragraph: "Repeated spaces inside text are preserved in both outputs."

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/Rendering/Svg/SvgDrawingSurface.cs ACadSharp.Image/ImageConfiguration.cs ACadSharp.Image.Cli/Program.cs ACadSharp.Image/Rendering/ImageStyleResolver.cs ACadSharp.Image/Rendering/ImageRenderContext.cs .github/workflows/ci.yml docs/superpowers/plans/2026-09-03-07-text-fidelity.md ACadSharp.Image.Tests/SvgDrawingSurfaceTests.cs ACadSharp.Image.Tests/CliTests.cs ACadSharp.Image.Tests/Baselines README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Preserve text whitespace in SVG, refresh stale docs, reject extra CLI arguments, drop dead overloads"
```

---

### Task 7: Tests the review asked for: DRAWORDER table, real renders of the new entities, tightened weak tests

**Findings:** Pass 3 (no DRAWORDER-table test; plan 08 entities never rendered through a real surface; `ConstantAttributeDefinitionsAreStillDrawn` passes without the explode path; the non-finite MLINE/WIPEOUT export tests pass without the `HasFiniteGeometry` arms).

**Files:**
- Modify: `ACadSharp.Image.Tests/ImagePageTests.cs`, `ACadSharp.Image.Tests/SyntheticSamples.cs` (new `EntityBlock()`), `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`
- Create: `ACadSharp.Image.Tests/EntityGoldenTests.cs`, `ACadSharp.Image.Tests/Baselines/entities.model.01.png`, `ACadSharp.Image.Tests/Baselines/entities.model.01.svg`

- [ ] **Step 1: DRAWORDER table test**

```csharp
    [Fact]
    public void AddHonoursTheDrawOrderTable()
    {
        BlockRecord block = new("ORDER");
        Line low = WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)), 0x10);
        Line high = WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)), 0x20);
        block.Entities.Add(low);
        block.Entities.Add(high);
        block.CreateSortEntitiesTable();
        block.SortEntitiesTable!.Add(low, 0x30);   // the low-handle entity is sorted last

        ImagePage page = new();
        page.Add(block, resizeLayout: false);

        Assert.Equal([0x20UL, 0x10UL], page.Entities.Select(e => e.Handle));
    }
```

`CreateSortEntitiesTable()` and `SortEntitiesTable.Add(Entity, ulong sorterHandle)` exist in 3.7.1 (verified). If the sorter semantics turn out to be "sorter handle replaces the entity's handle for ordering" as assumed, the expected order holds; if the probe shows otherwise, adjust the expected order to what `GetSortedEntities()` actually returns and explain in the report (the point is to pin table-driven ordering, not a particular semantic).

- [ ] **Step 2: Synthetic entity block and goldens**

Add `SyntheticSamples.EntityBlock()` returning a `BlockRecord("entities")` with: a `Face3D` quad at (0,0)-(20,15) with `Flags = InvisibleEdgeFlags.Third`; a `Leader` with arrow from (30,0) to (45,10) to (60,10) on a style `ArrowSize = 2`; a splined `Leader` through (70,0),(80,10),(90,0),(100,10); an `MLine` with a two-element fill-on style (offsets ±1, `FillColor` colour 3, element colours 1 and 5) along (0,30)-(40,30)-(40,50), vertex parameters `[1,0]/[-1,0]`; a `Line` from (60,30) to (100,30) on layer "Under" followed (later in the entity list, higher handle if handles are set) by a `Wipeout` covering (70,25)-(90,35) (`InsertPoint (70,25)`, `UVector (20,0,0)`, `VVector (0,10,0)`, `Size (1,1)`, `ClippingState = true`, rectangular `(-0.5,-0.5),(0.5,0.5)`); an `Insert` of a block with an `AttributeDefinition` tag "ROOM" at (0,0) whose insert sits at (60,45) with the attribute value "A-101" placed at (60,45) height 3. Use distinct layers with colours so the SVG groups are easy to assert.

`EntityGoldenTests` mirrors `FeatureGoldenTests`: `EntityExporter()` (800x500, padding 10, font DejaVu Sans), `EntityPngMatchesBaseline` → `GoldenAssert.Png("entities.model.01", ...)`, and `EntitySvgMatchesGoldenAndContainsEveryEntity` asserting: exactly one `<polyline>` from the 3DFACE (open run, 4 points) on its layer; two `<polygon>` fills with `data-type` LEADER (arrows) and one `<path>` with `C` commands; MLINE: one `<polygon>` fill in colour 3 and two `<polyline>`s in colours 1 and 5; WIPEOUT: a `<polygon>` filled `#ffffff` on the wipeout's layer; ATTRIB: a `<text>` "A-101" with `data-parent`; no Warning/NotImplemented notifications. Create the baselines with `ACADSHARP_IMAGE_UPDATE_BASELINES=1 dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~EntityGoldenTests"` and inspect the PNG (open it, describe it in the report). Add a raster occlusion assertion in `EntityPngMatchesBaseline`: a pixel on the "Under" line inside the wipeout rectangle is white, and one outside is not — compute the pixel positions from the exporter's fit (`ImageRenderContext.CreatePageContext(surface, page, configuration)` gives `ToSurfacePoint`; or sample a small window and assert on the darkest pixel).

- [ ] **Step 3: Tighten the weak tests**

`ConstantAttributeDefinitionsAreStillDrawn`: after constructing the insert, `insert.Attributes.Clear()` so the constant value can only come from the explode path (keep the "exactly once" assertion); the separate no-ATTRIB test then duplicates it — delete the duplicate. `FilledMLineWithANonFiniteVertexIsSkippedWithoutKillingTheExport` and the wipeout counterpart: assert the Warning message contains "geometry contains non-finite values; entity skipped" (the dispatcher's `HasFiniteGeometry` message) and that no notification contains "Raster:" (the surface-level fallback must not be what saved the export).

- [ ] **Step 4: Run the suite**

Full suite green; `git status --short ACadSharp.Image.Tests/Baselines` shows only the two new `entities.model.01.*` files.

- [ ] **Step 5: Commit**

```bash
git add ACadSharp.Image.Tests
git commit -m "Pin DRAWORDER tables, render the new entities through both backends, tighten two tests"
```

---

## Self-review

- Coverage: I1 → T1; I2, I7 → T2; I4, I5, I6 → T3; I3 → T4; Minor 2, 3 → T5; Minor 1, 4, 5, 6 (dead code) + CI + plan-07 note → T6; Pass 3 gaps → T7. Declined/deferred items are listed in the header.
- Type consistency: `EntityBounds.TryGet` (T2) is consumed by `ImagePage.ComputeFrame` (T2); `WipeoutWorldBoundary` (T2) is consumed by `EntityBounds` and `DrawWipeout`; the `source`/`placement` parameters (T3) are the ones T4 relies on for text; `SurfaceText.WidthScale` (T4) defaults to 1 so T5–T7 callers need no change; `ImageStyleResolver.IsNamed` (T5) is the existing private helper made internal.
- Baselines: T1 (viewport sheet, measured), T6 (four text SVGs, attribute-only), T7 (new files) are the only tasks allowed to touch `Baselines/`.
