# Remaining Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clear the last known fixable items on `mubeda/svg-support`: the half-pixel rounding of raster viewport composites, single-line TEXT entities on non-default OCS planes, an untested frozen-insert case, and two CLI nits.

**Architecture:** Two contained renderer changes (viewport composite fraction carried into the child context; TEXT origin and direction mapped through `OcsTransform` with MIRRTEXT=0 semantics for planes seen from behind), each test-first, plus tests and doc touch-ups. Only the synthetic viewport baseline may move.

**Tech Stack:** .NET 10, ACadSharp 3.7.1, SixLabors.ImageSharp 3.1.12, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-02-layers-and-svg-design.md` (binding; Task 2 extends section 5.3's OCS bullet).

## Global Constraints

- ACadSharp `3.7.1`; SixLabors.ImageSharp `3.1.12`; no new NuGet dependencies; target frameworks unchanged.
- Coding conventions: `this.` prefix on instance members, explicit types except LINQ lambdas, XML docs on public and internal members, `sealed` classes, file-scoped namespaces, four-space indent, UTF-8 without BOM, LF line endings.
- Baselines: only Task 1 may regenerate a baseline, and only `ACadSharp.Image.Tests/Baselines/viewport-sheet.paper.01.png`, with the scoped command in the task. Every other baseline and golden stays byte-identical (`git status --short ACadSharp.Image.Tests/Baselines` must list only that file in Task 1 and nothing in Tasks 2 and 3).
- Parity tests need the font `DejaVu Sans` (installed).
- `dotnet build ACadSharp.Image.sln -warnaserror` warning-free; full suite green before each commit.
- Never use bare `git stash` / `git stash pop`. Commit messages end with the repository's two trailer lines (see any commit on this branch).

## File Structure

- Modify `ACadSharp.Image/Rendering/RasterDrawingSurface.cs` (`BeginViewport`, `EndViewport`), `ACadSharp.Image/Rendering/TextRenderer.cs`, `ACadSharp.Image.Cli/Program.cs`.
- Modify tests: `RasterDrawingSurfaceTests.cs`, `ViewportParityTests.cs` (probes only if the shifted baseline requires), `TextRendererTests.cs`, `LayerFilteringTests.cs`.
- Modify `README.md`, `docs/superpowers/specs/2026-09-02-layers-and-svg-design.md` (section 5.3).

---

### Task 1: Raster viewport composites keep their sub-pixel position

**Files:**
- Modify: `ACadSharp.Image/Rendering/RasterDrawingSurface.cs` (`BeginViewport` ~216-227, `EndViewport` ~229-239)
- Modify: `ACadSharp.Image.Tests/RasterDrawingSurfaceTests.cs` (append one test)
- Modify (only if a probe fails after regeneration): `ACadSharp.Image.Tests/ViewportParityTests.cs`
- Regenerate: `ACadSharp.Image.Tests/Baselines/viewport-sheet.paper.01.png`

**Interfaces:**
- Consumes: `ViewportSurface(IDrawingSurface Surface, double OffsetX, double BottomY)`; `ImageRenderContext.CreateViewportContext` uses `surface.OffsetX` as `offsetX` and `surface.BottomY` as `surfaceHeight`, so `x = OffsetX + (p.X - OriginX) * Scale` and `y = BottomY - (p.Y - OriginY) * Scale`.
- Produces: no signature change. Behaviour: the child image is pasted at `floor(bounds.X), floor(bounds.Y)` and the fractional parts move into `OffsetX` / `BottomY`, so content keeps its exact page position; the image grows by up to one pixel to hold the fraction.

Background: `EndViewport` pastes the child image at `MathF.Round(bounds.X/Y)`, so every raster viewport sits up to half a pixel from its exact position. The SVG surface is unaffected (it clips in place).

- [ ] **Step 1: Write the failing test**

Append to `ACadSharp.Image.Tests/RasterDrawingSurfaceTests.cs` (the file uses the alias `ImageColor` for `SixLabors.ImageSharp.Color`):

```csharp
    [Fact]
    public void ViewportFractionalPositionIsCarriedIntoTheChildOffsets()
    {
        using Image<Rgba32> canvas = new(20, 20, ImageColor.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);

        // Viewport at (3.6, 2.4): the image is pasted at (3, 2) and the child draws 0.6 / 0.4 px further in.
        ViewportSurface viewport = surface.BeginViewport(new SurfaceRect(3.6, 2.4, 10, 9.3));

        Assert.Equal(0.6, viewport.OffsetX, 9);
        Assert.Equal(2.4 - 2 + 9.3, viewport.BottomY, 9);

        // A vertical line on the child's own X offset must land in page column 3 (covering x 3.1..4.1), not column 4 alone.
        viewport.Surface.DrawLine(new ImageStyle(ImageColor.Red, 1f), new SurfacePoint(viewport.OffsetX, 0), new SurfacePoint(viewport.OffsetX, viewport.BottomY));
        surface.EndViewport(viewport);

        Assert.True(canvas[3, 6].R > canvas[3, 6].G, $"column 3 should carry most of the line, got {canvas[3, 6]}");
        Assert.Equal(new Rgba32(255, 255, 255, 255), canvas[2, 6]);
        Assert.Equal(new Rgba32(255, 255, 255, 255), canvas[6, 6]);
    }
```

- [ ] **Step 2: Run it and confirm it fails**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~ViewportFractionalPositionIsCarried"`
Expected: FAIL on `Assert.Equal(0.6, viewport.OffsetX, 9)` (actual 0).

- [ ] **Step 3: Carry the fraction**

Replace `BeginViewport` and the destination computation in `EndViewport` in `ACadSharp.Image/Rendering/RasterDrawingSurface.cs`:

```csharp
    public ViewportSurface BeginViewport(SurfaceRect bounds)
    {
        // The child image can only be pasted at whole pixels. Its position is floored, the fractional remainder moves
        // into the child's own offsets (so content keeps its exact page position), and the image grows to hold that
        // remainder. The flip origin is the exact height: rounding it used to shift content and drop boundary geometry.
        double left = Math.Floor(bounds.X);
        double top = Math.Floor(bounds.Y);
        double fractionX = bounds.X - left;
        double fractionY = bounds.Y - top;
        int width = Math.Max(1, (int)Math.Ceiling(fractionX + bounds.Width));
        int height = Math.Max(1, (int)Math.Ceiling(fractionY + bounds.Height));
        Image<Rgba32> image = new(width, height, ImageColor.Transparent);
        RasterDrawingSurface child = new(image, this._configuration, ownsCanvas: true);
        ViewportSurface viewport = new(child, fractionX, fractionY + bounds.Height);
        this._viewports[viewport] = (image, new SurfaceRect(left, top, width, height));
        return viewport;
    }
```

and in `EndViewport` replace

```csharp
        ImagePoint destination = new((int)MathF.Round((float)entry.Bounds.X), (int)MathF.Round((float)entry.Bounds.Y));
```

with

```csharp
        // Bounds were floored to whole pixels in BeginViewport.
        ImagePoint destination = new((int)entry.Bounds.X, (int)entry.Bounds.Y);
```

If `_viewports` stores the bounds under a different tuple shape, keep its shape and store the floored rectangle.

- [ ] **Step 4: Run the surface tests, then the full suite**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~RasterDrawingSurfaceTests"`
Expected: PASS (including the existing `ViewportFlipOriginIsTheExactHeightNotTheRoundedImageHeight`, whose bounds start at 0 so nothing changes for it).

Run: `dotnet test ACadSharp.Image.sln --nologo -v q`
Expected: exactly one failure, `ViewportParityTests.SheetPngMatchesBaseline` (its sheet starts at x = 60.5, so the composite moves half a pixel). Any other failure means the change did more than intended: stop and report.

- [ ] **Step 5: Regenerate the sheet baseline and check the probes**

Run: `ACADSHARP_IMAGE_UPDATE_BASELINES=1 dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~ViewportParityTests.SheetPngMatchesBaseline"`
Then: `git status --short ACadSharp.Image.Tests/Baselines` must list only `viewport-sheet.paper.01.png`.

Quantify with Pillow (python3 available) against `git show HEAD:ACadSharp.Image.Tests/Baselines/viewport-sheet.paper.01.png` as in the previous plan: print changed-pixel count and bounding box.

Run the parity test without the variable: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~ViewportParityTests"`. If a pixel probe now fails (the left wall may have moved from column 171 to 172, and the bottom wall row may shift), read the pixels around the old probe in the new baseline, move the probe to the column/row that is now pure red, and explain the move in the report. Do not weaken the assertions (a probe must still demand a red pixel or a white window).

Open the new PNG with the Read tool: same picture as before (closed red rectangle, green dashed line, red circle, title, frame line), shifted by less than a pixel.

- [ ] **Step 6: Build, full suite, commit**

Run: `dotnet build ACadSharp.Image.sln -warnaserror --nologo -v q && dotnet test ACadSharp.Image.sln --nologo -v q`
Expected: 0 warnings; all pass (253 tests).

```bash
git add ACadSharp.Image/Rendering/RasterDrawingSurface.cs ACadSharp.Image.Tests/RasterDrawingSurfaceTests.cs ACadSharp.Image.Tests/ViewportParityTests.cs ACadSharp.Image.Tests/Baselines/viewport-sheet.paper.01.png
git commit -m "Keep the sub-pixel position of raster viewport composites"
```
The commit body states the changed-pixel count and that only the synthetic sheet baseline moved.

---

### Task 2: Single-line TEXT on non-default OCS planes

**Files:**
- Modify: `ACadSharp.Image/Rendering/TextRenderer.cs`
- Modify: `ACadSharp.Image.Tests/TextRendererTests.cs` (append tests)
- Modify: `README.md` (~line 249), `docs/superpowers/specs/2026-09-02-layers-and-svg-design.md` (section 5.3 OCS bullet)

**Interfaces:**
- Consumes: `OcsTransform.IsWorldPlane(XYZ)`, `OcsTransform.For(XYZ)`, `.ToWorldXY(x, y, z)`, `.ToWorld(x, y, z)` (returns `XYZ`), `.Normal`; `RecordingDrawingSurface.Texts`.
- Produces: `internal static (double Rotation, SurfaceTextAnchor Anchor) TextRenderer.ResolvePlacement(double rotation, SurfaceTextAnchor anchor, OcsTransform? toWorld)`.

Background: DXF stores TEXT's insertion and alignment points in the entity's OCS and its rotation in that plane; MTEXT stores its insertion point and X-axis direction in WCS (ACadSharp derives `MText.Rotation` from that direction), so only `TextEntity` needs a transform. When a plane is seen from behind (normal Z below zero, the usual `(0,0,-1)` produced by MIRROR), AutoCAD with `MIRRTEXT = 0` (the default) keeps the glyphs readable and lets the run occupy the mirrored extent: the same baseline, read from the other end. That is what `ResolvePlacement` implements: the projected direction angle plus half a turn, with `Start` and `End` anchors swapped. Planes seen from the front keep their projected angle and anchor.

- [ ] **Step 1: Write the failing tests**

Append to `ACadSharp.Image.Tests/TextRendererTests.cs` (it has `Setup(scale)` returning `(Surface, Context, Dispatcher)` and uses the context mapping `(x, y) -> (x * scale, 100 - y * scale)`):

```csharp
    [Fact]
    public void MirroredPlaneTextKeepsReadableGlyphsAndOccupiesTheMirroredExtent()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();
        TextEntity text = new() { Value = "M", InsertPoint = new XYZ(10, 20, 0), Height = 2, Normal = new XYZ(0, 0, -1) };

        dispatcher.Draw(context, text);

        SurfaceText run = Assert.Single(surface.Texts);
        // (0,0,-1) mirrors X: the origin lands at x = -10, and the run is anchored at its end so it extends toward -x.
        Assert.Equal(-10d, run.Origin.X, 9);
        Assert.Equal(100d - 20d, run.Origin.Y, 9);
        Assert.Equal(SurfaceTextAnchor.End, run.Anchor);
        Assert.Equal(1d, Math.Cos(run.Rotation), 9);   // upright, MIRRTEXT = 0 semantics
        Assert.Equal(0d, Math.Sin(run.Rotation), 9);
    }

    [Fact]
    public void MirroredPlaneRotationIsNegatedAndRightAlignmentBecomesStart()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();
        TextEntity text = new() { Value = "R", InsertPoint = new XYZ(1, 1, 0), AlignmentPoint = new XYZ(4, 1, 0), HorizontalAlignment = TextHorizontalAlignment.Right, Rotation = 0.5, Height = 2, Normal = new XYZ(0, 0, -1) };

        dispatcher.Draw(context, text);

        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal(-4d, run.Origin.X, 9);
        Assert.Equal(SurfaceTextAnchor.Start, run.Anchor);
        Assert.Equal(Math.Cos(0.5), Math.Cos(run.Rotation), 9);
        Assert.Equal(-Math.Sin(0.5), Math.Sin(run.Rotation), 9);
    }

    [Fact]
    public void FrontFacingTiltedPlaneProjectsOriginAndDirection()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();
        // Normal tilted toward +Y: the arbitrary axis algorithm makes the OCS X axis point along world -X.
        TextEntity text = new() { Value = "T", InsertPoint = new XYZ(1, 0, 0), Height = 2, Normal = new XYZ(0, 0.6, 0.8) };

        dispatcher.Draw(context, text);

        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal(-1d, run.Origin.X, 9);
        Assert.Equal(SurfaceTextAnchor.Start, run.Anchor);          // seen from the front: anchor unchanged
        Assert.Equal(-1d, Math.Cos(run.Rotation), 9);               // direction (1,0) in OCS is world -X
        Assert.Equal(0d, Math.Sin(run.Rotation), 9);
    }

    [Fact]
    public void MiddleAnchorAndFixedLengthSurviveMirroring()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup(scale: 2d);
        TextEntity text = new() { Value = "F", InsertPoint = new XYZ(0, 0, 0), AlignmentPoint = new XYZ(30, 0, 0), HorizontalAlignment = TextHorizontalAlignment.Fit, Height = 5, Normal = new XYZ(0, 0, -1) };

        dispatcher.Draw(context, text);

        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal(SurfaceTextAnchor.Middle, run.Anchor);
        Assert.Equal(60d, run.FixedLength, 9);
        Assert.Equal(-60d, run.Origin.X, 9);
    }

    [Fact]
    public void ResolvePlacementLeavesWorldPlaneTextAlone()
    {
        (double rotation, SurfaceTextAnchor anchor) = TextRenderer.ResolvePlacement(0.7, SurfaceTextAnchor.End, null);

        Assert.Equal(0.7, rotation);
        Assert.Equal(SurfaceTextAnchor.End, anchor);
    }
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~TextRendererTests"`
Expected: `ResolvePlacementLeavesWorldPlaneTextAlone` fails to compile (no `ResolvePlacement`); comment it out temporarily if needed to see the other four FAIL (origin X 10 instead of -10, etc.), then restore it.

- [ ] **Step 3: Implement**

In `ACadSharp.Image/Rendering/TextRenderer.cs`, change the `TextEntity` overload of `Draw` to:

```csharp
    public void Draw(ImageRenderContext context, ImageStyle style, TextEntity textEntity)
    {
        string text = NormalizeText(textEntity.Value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // TEXT stores its points and rotation in its own OCS (MTEXT does not: its insertion point and X axis are WCS).
        OcsTransform? toWorld = OcsTransform.IsWorldPlane(textEntity.Normal) ? null : OcsTransform.For(textEntity.Normal);
        XYZ origin = GetTextOrigin(textEntity);
        SurfacePoint surfaceOrigin = toWorld == null
            ? context.ToSurfacePoint(origin)
            : context.ToSurfacePoint(toWorld.ToWorldXY(origin.X, origin.Y, origin.Z));
        (double rotation, SurfaceTextAnchor anchor) = ResolvePlacement(textEntity.Rotation, GetAnchor(textEntity.HorizontalAlignment), toWorld);

        SurfaceText run = new(
            text,
            surfaceOrigin,
            context.ToSurfaceLength(textEntity.Height),
            rotation,
            anchor,
            GetBaseline(textEntity.VerticalAlignment),
            WrappingWidth: -1d,
            LineSpacingFactor: 1d,
            GetFixedLength(context, textEntity));
        context.Surface.DrawText(style, run);
    }

    /// <summary>
    /// Maps a TEXT entity's in-plane rotation and anchor onto the page.
    /// </summary>
    /// <param name="rotation">Rotation in the entity's OCS, radians.</param>
    /// <param name="anchor">Anchor derived from the horizontal alignment.</param>
    /// <param name="toWorld">The OCS frame, or null for the world plane.</param>
    /// <returns>The rotation to draw with (radians, drawing convention) and the anchor to use.</returns>
    /// <remarks>
    /// The OCS X direction rotated by <paramref name="rotation"/> is projected onto world XY. A plane seen from the
    /// front keeps that direction. A plane seen from behind (normal Z below zero, what MIRROR produces) would show the
    /// glyphs mirrored; AutoCAD with <c>MIRRTEXT = 0</c>, its default, keeps them readable and lets the run occupy the
    /// mirrored extent instead, which is the same baseline read from the other end: half a turn added to the projected
    /// direction, and <see cref="SurfaceTextAnchor.Start"/> and <see cref="SurfaceTextAnchor.End"/> swapped.
    /// </remarks>
    internal static (double Rotation, SurfaceTextAnchor Anchor) ResolvePlacement(double rotation, SurfaceTextAnchor anchor, OcsTransform? toWorld)
    {
        if (toWorld == null)
        {
            return (rotation, anchor);
        }

        XYZ direction = toWorld.ToWorld(Math.Cos(rotation), Math.Sin(rotation), 0d);
        double projected = Math.Atan2(direction.Y, direction.X);
        if (toWorld.Normal.Z >= 0d)
        {
            return (projected, anchor);
        }

        SurfaceTextAnchor flipped = anchor switch
        {
            SurfaceTextAnchor.Start => SurfaceTextAnchor.End,
            SurfaceTextAnchor.End => SurfaceTextAnchor.Start,
            _ => anchor,
        };
        return (projected + Math.PI, flipped);
    }
```

`GetTextOrigin` stays as it is (it picks the OCS point; the transform is applied afterwards). Add `using CSMath;` if the file lacks it (it already uses `XYZ`).

- [ ] **Step 4: Run the text tests and the full suite**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~TextRendererTests"`
Expected: PASS (all, including the pre-existing ones, which use the default normal).

Run: `dotnet test ACadSharp.Image.sln --nologo -v q`
Expected: all pass; no baseline changes. A baseline change would mean a sample contains TEXT with a non-default normal: stop and report rather than regenerate.

- [ ] **Step 5: Document**

`README.md`: replace the sentence "Text and dimension entities are not transformed (their normals are ignored), which is a known limitation." with "Single-line TEXT on another plane is placed the way AutoCAD shows it with `MIRRTEXT = 0`: readable glyphs occupying the mirrored extent. MTEXT and dimension geometry are already world coordinates in DXF and need no transform."

Spec section 5.3, at the end of the amended OCS bullet, replace "`TextEntity`/`MText` normals remain ignored." with "`TextEntity` points are OCS too (remaining fix, 2026-09-03): the origin goes through `OcsTransform`, and `TextRenderer.ResolvePlacement` projects the rotation, adding half a turn and swapping Start/End anchors for planes seen from behind (`MIRRTEXT = 0` semantics). `MText` insertion point and X axis are WCS in DXF and are used as stored."

- [ ] **Step 6: Build, full suite, commit**

Run: `dotnet build ACadSharp.Image.sln -warnaserror --nologo -v q && dotnet test ACadSharp.Image.sln --nologo -v q`
Expected: 0 warnings; all pass (258 tests).

```bash
git add ACadSharp.Image/Rendering/TextRenderer.cs ACadSharp.Image.Tests/TextRendererTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Place single-line TEXT on non-default OCS planes"
```

---

### Task 3: Frozen-insert inheritance test and CLI nits

**Files:**
- Modify: `ACadSharp.Image.Tests/LayerFilteringTests.cs` (append one test)
- Modify: `ACadSharp.Image.Cli/Program.cs` (`Main` XML doc, `var stopwatch`)

**Interfaces:**
- Consumes: `LayerFilteringTests.Setup(Action<ImageConfiguration>?, Viewport?)` returning `(Surface, Dispatcher, Context)`, `Drawn(surface)` counting `DrawLine` calls, `RecordingDrawingSurface.Entities`.

- [ ] **Step 1: Write the test**

Append to `ACadSharp.Image.Tests/LayerFilteringTests.cs`:

```csharp
    [Fact]
    public void FrozenInsertLayerHidesItsContentsAndVisibleInsertShowsLayerZeroContents()
    {
        Layer frozen = new("Doors") { Flags = LayerFlags.Frozen };
        Layer visible = new("Windows");
        Layer frozenOwn = new("Hardware") { Flags = LayerFlags.Frozen };

        static BlockRecord Symbol(Layer own)
        {
            BlockRecord block = new(Guid.NewGuid().ToString("N"));
            block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)) { Layer = new Layer(Layer.DefaultName) });
            block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)) { Layer = own });
            return block;
        }

        (RecordingDrawingSurface surface, EntityRenderDispatcher dispatcher, ImageRenderContext context) = Setup(c => c.LayerVisibility = LayerVisibilityMode.Screen);

        // Insert on a frozen layer: nothing inside is drawn, not even the entity on its own visible layer.
        dispatcher.Draw(context, new Insert(Symbol(visible)) { Layer = frozen });
        Assert.Equal(0, Drawn(surface));

        // Insert on a visible layer: the layer-0 line inherits that layer and is drawn; the line on its own frozen layer is not.
        dispatcher.Draw(context, new Insert(Symbol(frozenOwn)) { Layer = visible });
        Assert.Equal(1, Drawn(surface));
        Assert.Equal("Windows", surface.Entities.Last(e => e.EntityType == "LINE").LayerName);
    }
```

If `LayerFlags` needs a `using ACadSharp.Tables;` it is already imported (the file uses `Layer`); if `Flags` is not settable, use `IsOn = false` on the frozen layers instead and say so in the report (Screen mode honours both).

- [ ] **Step 2: Run it**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~FrozenInsertLayerHidesItsContents"`
Expected: PASS (this describes existing behaviour). If it fails, report the actual counts; do not change the renderer.

- [ ] **Step 3: CLI nits**

In `ACadSharp.Image.Cli/Program.cs`: add `/// <summary>Entry point: runs the tool against the console.</summary>` above `Main`, and change `var stopwatch = System.Diagnostics.Stopwatch.StartNew();` to `System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();`.

- [ ] **Step 4: Build, full suite, commit**

Run: `dotnet build ACadSharp.Image.sln -warnaserror --nologo -v q && dotnet test ACadSharp.Image.sln --nologo -v q`
Expected: 0 warnings; all pass (259 tests); no baseline changes.

```bash
git add ACadSharp.Image.Tests/LayerFilteringTests.cs ACadSharp.Image.Cli/Program.cs
git commit -m "Test frozen inserts with layer-0 contents and tidy the CLI entry point"
```
