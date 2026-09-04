# Remaining Rendering Limitations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Draw, instead of notifying about, the five remaining rendering gaps: multi-line attributes, tilted hatches inside blocks, custom arrowhead blocks, inverted wipeout clips, and MLEDIT cut segments.

**Architecture:** One internal placement module gathers the point/vector/OCS mapping that the dispatcher and the text renderer duplicate today. Block contents keep coming from `Insert.Explode()` with ordinal pairing; the existing `UsesOriginalGeometry` relation is extended so hatches (and, for bounds, wipeouts) are drawn from the original entity in its own OCS and mapped through the insert transform, and a cycle is caught by the scan that already walks the original block graph before `Explode()` runs. Custom arrow blocks are drawn by handing a transient `Insert` of the arrow block to the existing block-content path, so every entity type inside an arrow block gets the transform treatment it already has.

**Tech Stack:** .NET 10, ACadSharp 3.7.1, SixLabors.ImageSharp (raster backend), `System.Xml.Linq` (SVG backend), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-04-remaining-limitations-design.md` (which follows `docs/superpowers/specs/2026-09-02-layers-and-svg-design.md`; both bind).

## Global Constraints

- ACadSharp `3.7.1`; SixLabors packages as pinned; no new NuGet dependencies; target frameworks unchanged.
- Coding conventions: `this.` prefix on instance members, explicit types except LINQ lambdas, XML docs on public and internal members and a `<summary>` on private helpers, `sealed` classes, file-scoped namespaces, four-space indent, UTF-8 without BOM (never add or remove a BOM), LF line endings.
- PNG baselines and SVG goldens in `ACadSharp.Image.Tests/Baselines/` are byte-identical except where a task says otherwise. Task 8 creates `fidelity.model.01.png` and `fidelity.model.01.svg`. No other task may move a baseline; regeneration uses the scoped command the task gives, with the cause in the commit body. Never run the update variable over the whole suite.
- `dotnet build ACadSharp.Image.sln -warnaserror` warning-free; full suite green before each commit (`dotnet test ACadSharp.Image.sln --configuration Release -warnaserror`). The suite stands at 393 tests before Task 1.
- No reference to any drawing outside `Samples/` in code, tests, comments or commit messages.
- Never use bare `git stash` / `git stash pop`. Commit only the files the task names (never `git add -A`). Commit messages end with exactly these two trailer lines:
  ```
  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz
  ```
- Notifications use `ImageConfiguration.Notify(message, NotificationType[, exception])` with the message shape `[{entity.SubclassMarker}] Handle {handle:X}: ...`.
- `EntityRenderDispatcherTests.CreateContext` is `internal static` and gives a 100x100 surface at scale 1 with no offset: CAD `(x, y)` lands at `SurfacePoint(x, 100 - y)`. `RecordingDrawingSurface` records `Polygons`, `Polylines`, `Lines`, `Texts`, `Paths` and `Styles`.

## ACadSharp 3.7.1 facts these tasks depend on (probe-verified 2026-09-04)

- `Insert.GetTransform()` produces `world = R * S * p + (InsertPoint - BasePoint)`. AutoCAD's documented INSERT semantics are `world = InsertPoint + R * S * (p - BasePoint)`. The two agree only when the rotation and scale are identity, so a block with a **non-zero base point** inserted with rotation or scale is placed differently by ACadSharp than by AutoCAD. Measured: base point `(2,3)`, block point `(4,3)`, insert at `(10,10)` rotated 90 degrees, ACadSharp gives `(5,11)` where AutoCAD gives `(10,12)`. This is latent in practice (no sample or private drawing has a non-zero base point on a rotated or scaled insert) but Task 5 must compensate for it explicitly, because it builds an insert on purpose.
- A clone shares these list objects with its source: `MLine.Vertices`, `Leader.Vertices`, `Wipeout.ClipBoundaryVertices`. `LwPolyline.Vertices`, `Spline.ControlPoints`/`FitPoints`/`Knots`, `Polyline2D.Vertices` and `Hatch.Paths` are copied. `Explode()` overwrites the shared MLINE and LEADER lists in place; it never writes the wipeout clip list.
- `Wipeout.ApplyTransform` transforms `UVector` and `VVector` as points, so a translation contaminates them. Measured: `UVector (1,0,0)` became `(10,22,0)` under an insert at `(10,20)` with scale 2/3 and 90 degrees of rotation.
- `new Insert(BlockRecord)` creates one `AttributeEntity` per `AttributeDefinition` in the block, including constant ones.
- Both readers populate `AttributeBase.MText` for multi-line attributes: the DXF reader on the embedded-object marker (group code 101), the DWG reader in `readCommonAttData` for `AttributeType.MultiLine` and `ConstantMultiLine`. The DWG reader only reads `AttributeType` for R2018 and later files, so an older DWG always reports `SingleLine`.
- The renderer does not read `MLine.Vertex.Direction`; MLINE geometry comes from `Position`, `Miter` and `Segments[j].Parameters`.

---

## File Structure

- **Create** `ACadSharp.Image/Rendering/InsertPlacement.cs` — the point, vector and OCS-point mapping used by every task below, plus the planar similarity test Task 5 needs. Nothing else goes in this file.
- **Modify** `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` — the switch arms and per-entity helpers (`DrawHatch`, `DrawLeader`, `DrawWipeout`, `DrawMLine`, `DrawBlockContents`, `UsesOriginalGeometry`, `WipeoutWorldBoundary`).
- **Modify** `ACadSharp.Image/Rendering/TextRenderer.cs` — a `DrawAttribute` entry point for multi-line attributes.
- **Modify** `ACadSharp.Image/Rendering/EntityBounds.cs` — wipeout rings feed framing and viewport culling.
- **Tests** in `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`, `TextRendererTests.cs`, `ImagePageTests.cs`, `SyntheticSamples.cs`, and a new `FidelityGoldenTests.cs`.
- **Docs**: `docs/superpowers/specs/2026-09-02-layers-and-svg-design.md` section 4.6, `README.md` known limitations.

---

### Task 1: Insert placement helpers

**Files:**
- Create: `ACadSharp.Image/Rendering/InsertPlacement.cs`
- Modify: `ACadSharp.Image/Rendering/TextRenderer.cs` (`Place` uses `MapPoint`), `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (`DrawSolid`, `DrawLeader`, `DrawMLine` use `MapPoint`)
- Test: `ACadSharp.Image.Tests/InsertPlacementTests.cs` (create)

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces, all `internal static` on `InsertPlacement`:
  - `XYZ MapPoint(Transform? placement, XYZ point)`
  - `XYZ MapVector(Transform? placement, XYZ vector)`
  - `XYZ MapOcsPoint(Transform? placement, OcsTransform? toWorld, double elevation, XYZ ocsPoint)`
  - `bool TryGetPlanarSimilarity(Transform? placement, out double scale, out double rotation, out bool mirrored)`

- [ ] **Step 1: Write the failing tests**

Create `ACadSharp.Image.Tests/InsertPlacementTests.cs`:

```csharp
using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Tables;
using CSMath;
using Xunit;

namespace ACadSharp.Image.Tests;

public sealed class InsertPlacementTests
{
    [Fact]
    public void MapPointWithoutAPlacementReturnsThePointUnchanged()
    {
        Assert.Equal(new XYZ(3, 4, 5), InsertPlacement.MapPoint(null, new XYZ(3, 4, 5)));
    }

    [Fact]
    public void MapVectorDropsTheTranslation()
    {
        Transform placement = Transform.CreateTranslation(new XYZ(100, 200, 300));

        Assert.Equal(new XYZ(1, 0, 0), InsertPlacement.MapVector(placement, new XYZ(1, 0, 0)));
    }

    [Fact]
    public void MapVectorKeepsTheLinearPart()
    {
        Transform placement = PlacementOf(new XYZ(100, 200, 0), 0d, 2, 3);

        XYZ mapped = InsertPlacement.MapVector(placement, new XYZ(1, 1, 0));

        Assert.Equal(2d, mapped.X, 9);
        Assert.Equal(3d, mapped.Y, 9);
    }

    /// <summary>
    /// A placement built the way production builds one: from a real block reference. Constructing a
    /// <c>Transform</c> directly would depend on an argument order these tests should not be pinning.
    /// </summary>
    private static Transform PlacementOf(XYZ insertPoint, double rotation, double xScale, double yScale)
        => new Insert(new BlockRecord("PLACEMENT"))
        {
            InsertPoint = insertPoint,
            Rotation = rotation,
            XScale = xScale,
            YScale = yScale,
            ZScale = Math.Abs(xScale),
        }.GetTransform();

    [Fact]
    public void MapOcsPointAppliesTheOcsBeforeThePlacement()
    {
        // Normal (0,0,-1) mirrors X going from OCS to world: (4,1) becomes (-4,1); the placement then adds (10,0).
        Transform placement = Transform.CreateTranslation(new XYZ(10, 0, 0));

        XYZ mapped = InsertPlacement.MapOcsPoint(placement, OcsTransform.For(new XYZ(0, 0, -1)), 0d, new XYZ(4, 1, 0));

        Assert.Equal(6d, mapped.X, 9);
        Assert.Equal(1d, mapped.Y, 9);
    }

    [Fact]
    public void MapOcsPointUsesTheElevationForTheOutOfPlaneOffset()
    {
        XYZ mapped = InsertPlacement.MapOcsPoint(null, OcsTransform.For(new XYZ(0, 0, -1)), 7d, new XYZ(1, 2, 0));

        Assert.Equal(-7d, mapped.Z, 9);
    }

    [Fact]
    public void MapOcsPointWithoutAnOcsIsAPlainPointMap()
    {
        Assert.Equal(new XYZ(1, 2, 0), InsertPlacement.MapOcsPoint(null, null, 0d, new XYZ(1, 2, 0)));
    }

    [Fact]
    public void ANullPlacementIsAUnitSimilarity()
    {
        Assert.True(InsertPlacement.TryGetPlanarSimilarity(null, out double scale, out double rotation, out bool mirrored));
        Assert.Equal(1d, scale, 9);
        Assert.Equal(0d, rotation, 9);
        Assert.False(mirrored);
    }

    [Fact]
    public void AUniformlyScaledRotationIsASimilarity()
    {
        Transform placement = PlacementOf(new XYZ(5, 5, 0), Math.PI / 2, 3, 3);

        Assert.True(InsertPlacement.TryGetPlanarSimilarity(placement, out double scale, out double rotation, out bool mirrored));
        Assert.Equal(3d, scale, 9);
        Assert.Equal(Math.PI / 2, rotation, 9);
        Assert.False(mirrored);
    }

    [Fact]
    public void AMirroredPlacementIsASimilarityAndSaysSo()
    {
        Transform placement = PlacementOf(XYZ.Zero, 0d, -2, 2);

        Assert.True(InsertPlacement.TryGetPlanarSimilarity(placement, out double scale, out double rotation, out bool mirrored));
        Assert.Equal(2d, scale, 9);
        Assert.True(mirrored);
    }

    [Fact]
    public void ANonUniformScaleIsNotASimilarity()
    {
        Transform placement = PlacementOf(XYZ.Zero, 0d, 2, 5);

        Assert.False(InsertPlacement.TryGetPlanarSimilarity(placement, out _, out _, out _));
    }

    [Fact]
    public void ANonUniformScaleUnderRotationIsNotASimilarityEvenWhenTheAxesMatchInLength()
    {
        // A 3:1 scale turned 45 degrees leaves both mapped axes the same length but no longer at right angles, so a
        // check that only compared lengths would wrongly call this a similarity.
        Transform placement = PlacementOf(XYZ.Zero, Math.PI / 4, 3, 1);

        Assert.False(InsertPlacement.TryGetPlanarSimilarity(placement, out _, out _, out _));
    }

    [Fact]
    public void APlacementSeenEdgeOnIsNotASimilarity()
    {
        // Rotating a quarter turn about X flattens the Y axis onto Z, so nothing is left in the drawing plane.
        Transform placement = Transform.CreateRotation(XYZ.AxisX, Math.PI / 2);

        Assert.False(InsertPlacement.TryGetPlanarSimilarity(placement, out _, out _, out _));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.sln --configuration Release --filter "FullyQualifiedName~InsertPlacementTests"`
Expected: build failure, `InsertPlacement` does not exist.

- [ ] **Step 3: Create the helper**

Create `ACadSharp.Image/Rendering/InsertPlacement.cs`:

```csharp
using CSMath;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Maps geometry through the transform of the block reference that placed it. A placement of <c>null</c> means the
/// entity is at top level and every map is the identity.
/// </summary>
/// <remarks>
/// Points and vectors map differently: a translation moves a point but must not change a direction, so a vector is
/// mapped by transforming its head and tail and subtracting. ACadSharp 3.7.1 gets this wrong in places of its own
/// (<c>Wipeout.ApplyTransform</c> transforms its U and V vectors as points), which is why the renderer maps from the
/// original entity through these helpers instead of trusting a transformed clone.
/// </remarks>
internal static class InsertPlacement
{
    /// <summary>Maps a world point through the placement.</summary>
    /// <param name="placement">The transform of the insert that placed the entity, or null at top level.</param>
    /// <param name="point">The world point.</param>
    /// <returns>The placed world point.</returns>
    internal static XYZ MapPoint(Transform? placement, XYZ point) => placement == null ? point : placement.ApplyTransform(point);

    /// <summary>Maps a world direction through the placement, keeping the linear part and dropping the translation.</summary>
    /// <param name="placement">The transform of the insert that placed the entity, or null at top level.</param>
    /// <param name="vector">The world direction.</param>
    /// <returns>The placed direction, scaled and rotated but not translated.</returns>
    internal static XYZ MapVector(Transform? placement, XYZ vector)
    {
        if (placement == null)
        {
            return vector;
        }

        return placement.ApplyTransform(vector) - placement.ApplyTransform(XYZ.Zero);
    }

    /// <summary>
    /// Maps a point stored in an entity's own object coordinate system: the OCS frame first (with the entity's
    /// elevation as the out-of-plane offset), then the placement.
    /// </summary>
    /// <param name="placement">The transform of the insert that placed the entity, or null at top level.</param>
    /// <param name="toWorld">The entity's OCS frame, or null when it lies in the world plane.</param>
    /// <param name="elevation">The entity's elevation along its own normal.</param>
    /// <param name="ocsPoint">The point in the entity's OCS.</param>
    /// <returns>The placed world point.</returns>
    internal static XYZ MapOcsPoint(Transform? placement, OcsTransform? toWorld, double elevation, XYZ ocsPoint)
    {
        XYZ world = toWorld != null ? toWorld.ToWorld(ocsPoint.X, ocsPoint.Y, elevation) : ocsPoint;
        return MapPoint(placement, world);
    }

    /// <summary>
    /// Whether the placement acts on the drawing plane as a similarity: one uniform scale and a rotation, optionally
    /// with a reflection. Geometry that has to be handed back to ACadSharp as an <c>Insert</c> can only be expressed
    /// when this holds, because an <c>Insert</c> has no way to represent the shear a non-uniform scale composed with
    /// a rotation produces.
    /// </summary>
    /// <param name="placement">The transform to test, or null at top level.</param>
    /// <param name="scale">Receives the uniform scale.</param>
    /// <param name="rotation">Receives the rotation of the mapped X axis, in radians.</param>
    /// <param name="mirrored">Receives whether the mapped Y axis lies clockwise from the mapped X axis.</param>
    /// <returns>True when the placement is a planar similarity.</returns>
    internal static bool TryGetPlanarSimilarity(Transform? placement, out double scale, out double rotation, out bool mirrored)
    {
        XYZ ex = MapVector(placement, XYZ.AxisX);
        XYZ ey = MapVector(placement, XYZ.AxisY);
        XY x = new(ex.X, ex.Y);
        XY y = new(ey.X, ey.Y);
        double lx = x.GetLength();
        double ly = y.GetLength();
        scale = lx;
        rotation = 0d;
        mirrored = false;
        if (lx < 1e-12 || ly < 1e-12 || !double.IsFinite(lx) || !double.IsFinite(ly))
        {
            return false;
        }

        // A similarity keeps both axes the same length and at right angles; the tolerances are relative so a drawing
        // in millimetres and one in metres are judged the same way.
        if (Math.Abs(lx - ly) > 1e-9 * lx || Math.Abs((x.X * y.X) + (x.Y * y.Y)) > 1e-9 * lx * ly)
        {
            return false;
        }

        rotation = Math.Atan2(x.Y, x.X);
        mirrored = (x.X * y.Y) - (x.Y * y.X) < 0d;
        return true;
    }
}
```

- [ ] **Step 4: Route the existing duplicates through the helper**

In `TextRenderer.cs`, delete the private `Apply` helper and replace its three uses inside `Place` so the body reads:

```csharp
        XYZ o = InsertPlacement.MapPoint(placement, origin);
        XYZ dx = InsertPlacement.MapPoint(placement, origin + xAxis) - o;
        XYZ dy = InsertPlacement.MapPoint(placement, origin + yAxis) - o;
```

In `EntityRenderDispatcher.cs`:
- in `DrawSolid`, replace `placement == null ? world : placement.ApplyTransform(world)` with `InsertPlacement.MapPoint(placement, world)`;
- in `DrawLeader`, replace the local `Map` body with `context.ToSurfacePoint(InsertPlacement.MapPoint(placement, p))`;
- in `DrawMLine`, replace `context.ToSurfacePoint(placement == null ? world : placement.ApplyTransform(world))` with `context.ToSurfacePoint(InsertPlacement.MapPoint(placement, world))`.

Do not change any other behaviour in this task.

- [ ] **Step 5: Run the tests and the suite**

Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror`
Expected: the new tests pass, the existing 393 pass, all baselines byte-identical (`git status --short ACadSharp.Image.Tests/Baselines` empty).

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/Rendering/InsertPlacement.cs ACadSharp.Image/Rendering/TextRenderer.cs ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/InsertPlacementTests.cs
git commit -m "Add the insert placement helpers and route the existing maps through them"
```

---

### Task 2: Multi-line attributes

**Files:**
- Modify: `ACadSharp.Image/Rendering/TextRenderer.cs` (add `DrawAttribute`), `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (a switch arm before `case TextEntity`)
- Test: `ACadSharp.Image.Tests/TextRendererTests.cs`
- Docs: spec 4.6, `README.md`

**Interfaces:**
- Consumes: `InsertPlacement` from Task 1 (not required directly, but the file must already exist).
- Produces: `public void DrawAttribute(ImageRenderContext context, ImageStyle style, AttributeBase attribute, Transform? placement)` on `TextRenderer`.

An `AttributeEntity` derives from `TextEntity`, so today a multi-line attribute reaches `case TextEntity` and is drawn from its single-line `Value`. The new arm must come **before** `case MText` and `case TextEntity` in the switch, because `AttributeDefinition` and `AttributeEntity` are both `TextEntity` subclasses and neither is an `MText`.

- [ ] **Step 1: Write the failing tests**

Add to `ACadSharp.Image.Tests/TextRendererTests.cs`:

```csharp
    [Fact]
    public void AMultiLineAttributeIsDrawnFromItsEmbeddedMTextNotItsSingleLineValue()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        AttributeEntity attribute = new()
        {
            Tag = "ROOM",
            Value = "WRONG",
            AttributeType = AttributeType.MultiLine,
            InsertPoint = new XYZ(0, 0, 0),
            Height = 3,
            MText = new MText { Value = "Line1\\PLine2", InsertPoint = new XYZ(10, 20, 0), Height = 4, RectangleWidth = 30 },
        };

        new EntityRenderDispatcher(configuration).Draw(EntityRenderDispatcherTests.CreateContext(surface, configuration), attribute);

        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Contains("Line1", run.Text);
        Assert.Contains("Line2", run.Text);
        Assert.DoesNotContain("WRONG", run.Text);
        Assert.Equal(4d, run.Height, 9);
        Assert.Equal(30d, run.WrappingWidth, 9);
        Assert.Equal(new SurfacePoint(10, 80), run.Origin);
    }

    [Fact]
    public void AMultiLineAttributeKeepsTheAttributeAsTheObservableEntity()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        AttributeEntity attribute = new()
        {
            Tag = "ROOM",
            Value = "WRONG",
            AttributeType = AttributeType.MultiLine,
            MText = new MText { Value = "A", InsertPoint = new XYZ(1, 1, 0), Height = 2 },
        };

        new EntityRenderDispatcher(configuration).Draw(EntityRenderDispatcherTests.CreateContext(surface, configuration), attribute);

        Assert.Equal("ATTRIB", Assert.Single(surface.Entities).ObjectName);
    }

    [Fact]
    public void AMultiLineAttributeWithoutAnEmbeddedMTextFallsBackToItsValueWithAWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        AttributeEntity attribute = new()
        {
            Tag = "ROOM",
            Value = "FALLBACK",
            AttributeType = AttributeType.MultiLine,
            InsertPoint = new XYZ(5, 5, 0),
            Height = 2,
        };

        new EntityRenderDispatcher(configuration).Draw(EntityRenderDispatcherTests.CreateContext(surface, configuration), attribute);

        Assert.Equal("FALLBACK", Assert.Single(surface.Texts).Text);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("multi-line layout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ASingleLineAttributeStillTakesTheTextPath()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        AttributeEntity attribute = new()
        {
            Tag = "ROOM",
            Value = "A-101",
            InsertPoint = new XYZ(2, 2, 0),
            Height = 2,
            MText = new MText { Value = "IGNORED", InsertPoint = new XYZ(50, 50, 0), Height = 9 },
        };

        new EntityRenderDispatcher(configuration).Draw(EntityRenderDispatcherTests.CreateContext(surface, configuration), attribute);

        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal("A-101", run.Text);
        Assert.Equal(2d, run.Height, 9);
    }
```

If `RecordingDrawingSurface` does not expose the entity records under the name `Entities`, use whatever member it records `BeginEntity` calls under and assert the `ObjectName` of the single record; check the surface's definition in `ACadSharp.Image.Tests/` before writing that assertion and adjust the test rather than the surface.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.sln --configuration Release --filter "FullyQualifiedName~TextRendererTests"`
Expected: the first three fail (the single-line value is drawn, no warning is raised); the fourth passes already.

- [ ] **Step 3: Add `DrawAttribute` to `TextRenderer`**

Add to `TextRenderer.cs`, after the `MText` `Draw` overload:

```csharp
    /// <summary>
    /// Draws an attribute whose layout comes from an embedded <see cref="MText"/>. AutoCAD stores a multi-line
    /// attribute's real layout there, and leaves the single-line <c>Value</c> as a flattened copy, so the embedded
    /// object is the authority for everything geometric: the text, its rectangle width, height, rotation and
    /// attachment point. The attribute itself stays the observable entity, so layer, colour, handle and parent
    /// metadata are unchanged.
    /// </summary>
    /// <param name="context">The context that maps drawing units onto the surface.</param>
    /// <param name="style">The resolved style for the attribute.</param>
    /// <param name="attribute">The multi-line attribute or attribute definition.</param>
    /// <param name="placement">The transform of the insert that placed the entity, or null at top level.</param>
    /// <remarks>
    /// When the embedded object is missing the single-line value is drawn instead, with a warning: ACadSharp 3.7.1's
    /// DWG reader only reads the attribute type for R2018 and later files, so an older drawing reports every
    /// attribute as single-line and never populates the embedded object.
    /// </remarks>
    public void DrawAttribute(ImageRenderContext context, ImageStyle style, AttributeBase attribute, Transform? placement)
    {
        if (attribute.MText == null)
        {
            context.Configuration.Notify(
                $"[{attribute.SubclassMarker}] Handle {attribute.Handle.ToString("X", CultureInfo.InvariantCulture)}: multi-line layout is not available; the single-line value was drawn.",
                NotificationType.Warning);
            this.Draw(context, style, (TextEntity)attribute, placement);
            return;
        }

        this.Draw(context, style, attribute.MText, placement);
    }
```

Add `using System.Globalization;` to the file's usings if it is not already there.

- [ ] **Step 4: Add the switch arm**

In `EntityRenderDispatcher.Draw`, immediately **before** `case MText mtext:`, add:

```csharp
                case AttributeBase attribute when attribute.AttributeType is AttributeType.MultiLine or AttributeType.ConstantMultiLine:
                    this._textRenderer.DrawAttribute(context, style, attribute, placement);
                    break;
```

- [ ] **Step 5: Run the tests and the suite**

Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror`
Expected: all pass; baselines byte-identical (no sample has a multi-line attribute).

- [ ] **Step 6: Docs**

In spec section 4.6, replace the sentence that says multi-line attributes are drawn from their single-line value with:

```
A multi-line attribute (`AttributeType.MultiLine` or `ConstantMultiLine`) is laid out from its embedded `MText` — text, rectangle width, height, rotation and attachment point — while the attribute stays the observable entity, so its layer, colour, handle and parent metadata are unchanged. When the embedded object is missing, which is what ACadSharp reports for a pre-2018 DWG, the single-line value is drawn with a Warning.
```

In `README.md`, remove "Multi-line attributes are drawn from their single-line value." from the known limitations.

- [ ] **Step 7: Commit**

```bash
git add ACadSharp.Image/Rendering/TextRenderer.cs ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/TextRendererTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Lay out multi-line attributes from their embedded MText"
```

---

### Task 3: Draw hatches from the original entity in its own OCS

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (`UsesOriginalGeometry`, `DrawHatch`, the `Hatch` switch arm, delete `NormalizeExplodedClone`)
- Test: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`
- Docs: spec 4.6, `README.md`

**Interfaces:**
- Consumes: `InsertPlacement.MapOcsPoint` from Task 1.
- Produces: `private void DrawHatch(ImageRenderContext context, ImageStyle style, Hatch hatch, Transform? placement)`.

A hatch clone inside a block is wrong twice over: `Hatch.ApplyTransform` transforms the raw OCS boundary as if it were world data and never folds in the elevation, and `NormalizeExplodedClone` then forces any non-world normal to `+Z` to hide it. Drawing from the original in its own OCS and mapping through the placement fixes both, and `NormalizeExplodedClone` becomes dead.

- [ ] **Step 1: Write the failing tests**

Add to `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`:

```csharp
    [Fact]
    public void ATiltedHatchInsideAnInsertIsMappedThroughItsOwnOcsThenTheInsertTransform()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("TILT");
        Hatch hatch = new() { IsSolid = true, Normal = new XYZ(0, 0, -1), Elevation = 0d };
        hatch.Paths.Add(SquarePath(0, 0, 10, 10));
        block.Entities.Add(hatch);
        Insert insert = new(block) { InsertPoint = new XYZ(20, 0, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        // Normal (0,0,-1) mirrors X going OCS to world, so the square spans x in [-10,0]; the insert then adds 20.
        IReadOnlyList<SurfacePoint> ring = Assert.Single(Assert.Single(surface.Paths));
        Assert.Equal(10d, ring.Min(p => p.X), 6);
        Assert.Equal(20d, ring.Max(p => p.X), 6);
    }

    [Fact]
    public void ATiltedHatchAtTopLevelIsUnchanged()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Hatch hatch = new() { IsSolid = true, Normal = new XYZ(0, 0, -1), Elevation = 0d };
        hatch.Paths.Add(SquarePath(0, 0, 10, 10));

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), hatch);

        IReadOnlyList<SurfacePoint> ring = Assert.Single(Assert.Single(surface.Paths));
        Assert.Equal(-10d, ring.Min(p => p.X), 6);
        Assert.Equal(0d, ring.Max(p => p.X), 6);
    }

    [Fact]
    public void AHatchInsideAMirroredInsertKeepsItsExtent()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("MIRROR");
        Hatch hatch = new() { IsSolid = true, Normal = XYZ.AxisZ, Elevation = 0d };
        hatch.Paths.Add(SquarePath(0, 0, 10, 10));
        block.Entities.Add(hatch);
        Insert insert = new(block) { InsertPoint = new XYZ(50, 0, 0), XScale = -1 };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        IReadOnlyList<SurfacePoint> ring = Assert.Single(Assert.Single(surface.Paths));
        Assert.Equal(40d, ring.Min(p => p.X), 6);
        Assert.Equal(50d, ring.Max(p => p.X), 6);
    }

    [Fact]
    public void ATiltedHatchWithAnElevationIsPlacedAlongItsOwnNormal()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Hatch hatch = new() { IsSolid = true, Normal = new XYZ(0, 1, 0), Elevation = 5d };
        hatch.Paths.Add(SquarePath(0, 0, 10, 10));

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), hatch);

        // Normal (0,1,0): the OCS X axis is world +X and the OCS Y axis is world +Z, so the square's Y collapses and
        // the elevation carries it to y = -5 in world (the arbitrary-axis frame's third axis).
        IReadOnlyList<SurfacePoint> ring = Assert.Single(Assert.Single(surface.Paths));
        Assert.All(ring, p => Assert.Equal(105d, p.Y, 6));
    }
```

`SquarePath` is a private helper in the test file; if it does not exist there, add it beside the other helpers:

```csharp
    private static Hatch.BoundaryPath SquarePath(double x0, double y0, double x1, double y1)
    {
        Hatch.BoundaryPath path = new();
        path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(x0, y0), End = new XY(x1, y0) });
        path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(x1, y0), End = new XY(x1, y1) });
        path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(x1, y1), End = new XY(x0, y1) });
        path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(x0, y1), End = new XY(x0, y0) });
        return path;
    }
```

Check the exact `Hatch.BoundaryPath` edge API against ACadSharp 3.7.1 before relying on it, and adjust the helper (not the assertions) if the member names differ. The fourth test's expected value depends on the arbitrary-axis frame for normal `(0,1,0)`: run it first and, if the frame puts the elevation on the other side, flip the sign in the assertion and say so in your report — the point is that the elevation reaches the output, not which sign the frame gives it.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.sln --configuration Release --filter "FullyQualifiedName~EntityRenderDispatcherTests"`
Expected: the first test fails (the clone's normal was forced to `+Z`, so the square lands at x in [20,30]); the others pass or fail as the current code dictates. Record which.

- [ ] **Step 3: Extend the pairing relation**

In `UsesOriginalGeometry`, add `Hatch` to the always-original list so the body reads:

```csharp
        if (original is TextEntity or MText or Leader or Hatch)
        {
            return true;
        }
```

- [ ] **Step 4: Give `DrawHatch` a placement**

Change the signature to `private void DrawHatch(ImageRenderContext context, ImageStyle style, Hatch hatch, Transform? placement)` and replace the local `ToSurface` with:

```csharp
        // Boundary paths and exploded pattern lines are OCS data; the OCS frame and the entity's own elevation are
        // applied here and the insert transform after them, because ACadSharp 3.7.1's Hatch.ApplyTransform maps the
        // raw OCS boundary as if it were world data and never folds the elevation in, so a clone from a block cannot
        // be trusted for a hatch on a tilted plane.
        OcsTransform? toWorld = IsWorldPlane(hatch.Normal) ? null : OcsTransform.For(hatch.Normal);
        SurfacePoint ToSurface(XYZ point) => context.ToSurfacePoint(InsertPlacement.MapOcsPoint(placement, toWorld, hatch.Elevation, point));
```

Update the switch arm to `case Hatch hatch: this.DrawHatch(context, style, source as Hatch ?? hatch, placement); break;`.

- [ ] **Step 5: Delete the dead normalisation**

Delete the `NormalizeExplodedClone` method and its single call in `DrawBlockContents`. It existed only to hide the clone's wrong normal for hatches, which no longer reach the drawing path.

- [ ] **Step 6: Run the tests and the suite**

Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror`
Expected: all pass. Baselines must be byte-identical: the features sample's hatch is world-plane, and a world-plane hatch inside a mirrored insert is now drawn from the original through the placement instead of from a normalised clone, which produces the same points. If any baseline moves, STOP and report BLOCKED with the diff rather than regenerating.

- [ ] **Step 7: Docs**

In spec 4.6, replace the hatch clause that records the tilted-plane limitation with:

```
A hatch is drawn from the original block entity in its own OCS (normal and elevation) and mapped through the insert transform, so a hatch on a tilted plane inside a block is placed correctly; the exploded clone's boundary is never used, because ACadSharp 3.7.1 transforms it as if it were world data.
```

In `README.md`, remove "A hatch on a tilted plane inside a block is still wrong" from the known limitations.

- [ ] **Step 8: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Draw hatches from the original entity in its own OCS through the insert transform"
```

---

### Task 4: Refuse to explode a circular block graph

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (`DrawBlockContents`, `ScanBlockSubtree` result use)
- Test: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`

**Interfaces:**
- Consumes: the existing `private (bool NeedsHeal, bool Truncated) ScanBlockSubtree(BlockRecord? block, HashSet<BlockRecord> visited)` and `private bool BlockSubtreeNeedsHeal(BlockRecord? block, HashSet<BlockRecord> visited)`.
- Produces: `private bool BlockGraphIsCircular(BlockRecord block)` used by `DrawBlockContents`, and the guard Task 5 relies on for an arrow block that leads back to itself.

A block that contains an insert of itself makes `Insert.Explode()` deep-clone the graph until the stack overflows, inside ACadSharp, before the renderer draws anything. A guard at draw time cannot help: nested inserts hold deep-cloned block records, so identity is a different key at every level, and the overflow happens first. The scan that already walks the **original** graph before `Explode()` is the only place that can see it, and it already reports truncation on a cycle.

- [ ] **Step 1: Write the failing test**

Add to `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`:

```csharp
    [Fact]
    public void ABlockThatReferencesItselfIsSkippedWithAWarningInsteadOfOverflowing()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        CadDocument document = new();
        BlockRecord outer = new("OUTER");
        BlockRecord inner = new("INNER");
        document.BlockRecords.Add(outer);
        document.BlockRecords.Add(inner);
        outer.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)));
        outer.Entities.Add(new Insert(inner));
        inner.Entities.Add(new Insert(outer));
        Insert insert = new(outer);
        document.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("references itself", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(surface.Lines);
    }

    [Fact]
    public void AnOrdinaryNestedBlockStillDraws()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord outer = new("OUTER");
        BlockRecord inner = new("INNER");
        inner.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0)));
        outer.Entities.Add(new Insert(inner) { InsertPoint = new XYZ(0, 5, 0) });
        Insert insert = new(outer) { InsertPoint = new XYZ(2, 3, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Single(surface.Lines);
    }
```

If constructing that cycle throws inside ACadSharp before the assertion runs, build the second insert with its block assigned after both blocks exist, using the same technique the existing block tests use, and record what you had to do in your report.

- [ ] **Step 2: Run the tests to verify the first one fails**

Run: `dotnet test ACadSharp.Image.sln --configuration Release --filter "FullyQualifiedName~ABlockThatReferencesItself"`
Expected: the test process dies with a stack overflow, or the test fails with no warning. Either counts as RED; record which you saw. Run this single test on its own so a stack overflow does not take the rest of the suite with it.

- [ ] **Step 3: Add the guard**

Add beside `BlockSubtreeNeedsHeal`:

```csharp
    /// <summary>
    /// Whether the block's own graph contains a cycle, which makes it impossible to explode.
    /// </summary>
    /// <param name="block">The block a reference points at.</param>
    /// <returns>True when walking the block's nested references reaches the block again.</returns>
    /// <remarks>
    /// This has to be answered before <c>Insert.Explode()</c> is called, not while drawing: exploding deep-clones the
    /// whole block graph, so a cycle overflows the stack inside ACadSharp before the renderer sees a single entity,
    /// and a draw-time guard keyed on the block record cannot recognise a nested level anyway, because the inserts
    /// reached down there hold deep-cloned records with a different identity at every level.
    /// </remarks>
    private bool BlockGraphIsCircular(BlockRecord block) => this.ScanBlockSubtree(block, new HashSet<BlockRecord>()).Truncated;
```

In `DrawBlockContents`, immediately after the null-block guard, add:

```csharp
        if (this.BlockGraphIsCircular(insert.Block))
        {
            this._configuration.Notify($"[{insert.SubclassMarker}] Handle {insert.Handle.ToString("X", CultureInfo.InvariantCulture)}: block '{insert.Block.Name}' references itself; skipped.", NotificationType.Warning);
            return;
        }
```

- [ ] **Step 4: Run the tests and the suite**

Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror`
Expected: all pass, baselines byte-identical.

- [ ] **Step 5: Docs**

In spec 4.6, add to the block-contents bullet:

```
A block whose graph references itself is skipped with a Warning before it is exploded, because `Insert.Explode()` deep-clones the whole graph and would exhaust the stack first.
```

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Skip a circular block graph before exploding it"
```

---

### Task 5: Custom arrowhead blocks

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (`DrawLeader`, a new `DrawArrowBlock`)
- Test: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`
- Docs: spec 4.6, `README.md`

**Interfaces:**
- Consumes: `InsertPlacement.TryGetPlanarSimilarity` (Task 1), `BlockGraphIsCircular` (Task 4), the existing `DrawBlockContents`.
- Produces: `private bool DrawArrowBlock(ImageRenderContext context, Layer? layer, ResolvedStyle parent, Leader leader, BlockRecord arrow, XY tip, XY direction, double size, double z, Transform? placement)` returning whether the block was drawn.

Most entity types ignore the `placement` argument entirely: a `Line` inside a block is drawn from the clone's own transformed points, not from the original. So an arrow block cannot be drawn by walking its entities with a placement — it has to go through the same `Insert.Explode()` path every other block uses. The task therefore builds a transient `Insert` of the arrow block whose own transform is the composition of the arrow placement and any outer placement, and hands it to `DrawBlockContents`.

Two ACadSharp facts constrain the construction:
- `Insert.GetTransform()` yields `world = R * S * p + (InsertPoint - BasePoint)`, not AutoCAD's `InsertPoint + R * S * (p - BasePoint)`. The insertion point must therefore be computed as `InsertPoint = wantedOrigin - L(BasePoint) + BasePoint`, where `L` is the linear part being requested.
- An `Insert` can only express a rotation and per-axis scales, so a composed transform that is not a planar similarity cannot be represented. In that case the default triangle is drawn with a Warning.

- [ ] **Step 1: Write the failing tests**

Add to `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`:

```csharp
    private static BlockRecord ArrowBlock(string name = "ARROW", double baseX = 0, double baseY = 0)
    {
        BlockRecord block = new(name);
        block.BlockEntity.BasePoint = new XYZ(baseX, baseY, 0);
        // A unit arrow: the tip sits at the base point and the body runs back along local -X.
        block.Entities.Add(new Line(new XYZ(baseX - 1, baseY, 0), new XYZ(baseX, baseY, 0)));
        block.Entities.Add(new Solid
        {
            FirstCorner = new XYZ(baseX - 1, baseY - 0.25, 0),
            SecondCorner = new XYZ(baseX, baseY, 0),
            ThirdCorner = new XYZ(baseX - 1, baseY + 0.25, 0),
            FourthCorner = new XYZ(baseX, baseY, 0),
        });
        return block;
    }

    [Fact]
    public void ALeaderWithACustomArrowBlockDrawsTheBlockAndNotifiesNothing()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        document.BlockRecords.Add(arrow);
        Leader leader = new()
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(10, 10, 0), new XYZ(30, 10, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        };
        document.Entities.Add(leader);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.DoesNotContain(notifications, n => n.NotificationType == NotificationType.NotImplemented);
        // The block's own line, scaled by 2 and pointing back along -X from the tip at (10,10).
        Assert.Contains(surface.Lines, l => l.Start == new SurfacePoint(8, 90) && l.End == new SurfacePoint(10, 90));
        // The block's solid, not the built-in triangle.
        Assert.Single(surface.Polygons);
    }

    [Fact]
    public void ACustomArrowRotatesToTheOutwardLeaderDirection()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        document.BlockRecords.Add(arrow);
        // The leader runs downward from the tip, so the arrow's local +X must point up.
        Leader leader = new()
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(10, 50, 0), new XYZ(10, 20, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        };
        document.Entities.Add(leader);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.Contains(surface.Lines, l => l.Start == new SurfacePoint(10, 52) && l.End == new SurfacePoint(10, 50));
    }

    [Fact]
    public void ACustomArrowHonoursANonZeroBlockBasePoint()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock("ARROWB", baseX: 5, baseY: 7);
        document.BlockRecords.Add(arrow);
        Leader leader = new()
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(10, 10, 0), new XYZ(30, 10, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        };
        document.Entities.Add(leader);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        // The base point is the arrow's tip, so it must land on the leader's first vertex exactly as in the
        // zero-base-point case: the block's line still runs from (8,10) to (10,10) in world.
        Assert.Contains(surface.Lines, l => l.Start == new SurfacePoint(8, 90) && l.End == new SurfacePoint(10, 90));
    }

    [Fact]
    public void ACustomArrowInsideAScaledInsertScalesWithIt()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        document.BlockRecords.Add(arrow);
        BlockRecord note = new("NOTE");
        document.BlockRecords.Add(note);
        note.Entities.Add(new Leader
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(0, 0, 0), new XYZ(10, 0, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        });
        Insert insert = new(note) { InsertPoint = new XYZ(10, 10, 0), XScale = 3, YScale = 3, ZScale = 3 };
        document.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        // Arrow size 2 scaled by 3 is 6: the block's line runs from (4,10) to (10,10) in world.
        Assert.Contains(surface.Lines, l => l.Start == new SurfacePoint(4, 90) && l.End == new SurfacePoint(10, 90));
    }

    [Fact]
    public void ACustomArrowUnderANonUniformInsertFallsBackToTheDefaultTriangleWithAWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        document.BlockRecords.Add(arrow);
        BlockRecord note = new("NOTE");
        document.BlockRecords.Add(note);
        note.Entities.Add(new Leader
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(0, 0, 0), new XYZ(10, 0, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        });
        Insert insert = new(note) { InsertPoint = new XYZ(10, 10, 0), XScale = 3, YScale = 1, ZScale = 1, Rotation = Math.PI / 4 };
        document.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Contains(notifications, n => n.Message.Contains("cannot be placed", StringComparison.OrdinalIgnoreCase));
        Assert.Single(surface.Polygons);
    }

    [Fact]
    public void ARecursiveArrowBlockFallsBackToTheDefaultTriangle()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        CadDocument document = new();
        BlockRecord arrow = ArrowBlock();
        document.BlockRecords.Add(arrow);
        arrow.Entities.Add(new Insert(arrow));
        Leader leader = new()
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(10, 10, 0), new XYZ(30, 10, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        };
        document.Entities.Add(leader);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.Contains(notifications, n => n.Message.Contains("references itself", StringComparison.OrdinalIgnoreCase));
        Assert.Single(surface.Polygons);
    }

    [Fact]
    public void AnEmptyArrowBlockDrawsNothingExtraAndWarnsOnce()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        CadDocument document = new();
        BlockRecord arrow = new("EMPTY");
        document.BlockRecords.Add(arrow);
        Leader leader = new()
        {
            ArrowHeadEnabled = true,
            Vertices = { new XYZ(10, 10, 0), new XYZ(30, 10, 0) },
            Style = new DimensionStyle("A") { ArrowSize = 2, ScaleFactor = 1, LeaderArrow = arrow },
        };
        document.Entities.Add(leader);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.Contains(notifications, n => n.Message.Contains("is empty", StringComparison.OrdinalIgnoreCase));
        Assert.Single(surface.Polygons);
    }
```

`surface.Lines` records `DrawLine` calls; if the recording surface exposes them under a different member or shape, adjust the assertions to that shape, not the surface.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.sln --configuration Release --filter "FullyQualifiedName~CustomArrow|FullyQualifiedName~ArrowBlock"`
Expected: FAIL — today every one of these draws the default triangle and raises a NotImplemented notification.

- [ ] **Step 3: Implement the arrow block**

Add to `EntityRenderDispatcher.cs`:

```csharp
    /// <summary>
    /// Draws a custom arrowhead block at a leader's tip: the block's base point goes to the tip, its local +X axis
    /// turns to point outward along <paramref name="direction"/>, and it is scaled by <paramref name="size"/>, all
    /// composed with the placement of the block reference that placed the leader.
    /// </summary>
    /// <param name="context">The context that maps drawing units onto the surface.</param>
    /// <param name="layer">The leader's effective layer, which the arrow's layer-0 entities inherit.</param>
    /// <param name="parent">The leader's resolved style, which the arrow's ByBlock entities inherit.</param>
    /// <param name="leader">The leader the arrow belongs to, for notifications.</param>
    /// <param name="arrow">The arrow block.</param>
    /// <param name="tip">The leader's first vertex, in the leader's own coordinates.</param>
    /// <param name="direction">The outward unit direction at the tip, in the leader's own coordinates.</param>
    /// <param name="size">The arrow size, already multiplied by the dimension style's overall scale.</param>
    /// <param name="z">The tip's own Z, so a leader off the world plane keeps its arrow attached to its line.</param>
    /// <param name="placement">The transform of the insert that placed the leader, or null at top level.</param>
    /// <returns>True when the block was drawn; false when the caller should fall back to the default triangle.</returns>
    /// <remarks>
    /// The block is drawn by handing a transient <c>Insert</c> of it to the ordinary block-content path, rather than
    /// by walking its entities with a transform: most entity types are drawn from their own stored points and ignore
    /// a placement, so only <c>Insert.Explode()</c> transforms an arbitrary block's contents correctly.
    /// <para>
    /// Two ACadSharp 3.7.1 behaviours shape the construction. An <c>Insert</c> cannot represent shear, so a composed
    /// transform that is not a planar similarity has no equivalent insert and the caller falls back. And
    /// <c>Insert.GetTransform()</c> computes <c>R * S * p + (InsertPoint - BasePoint)</c>, where AutoCAD specifies
    /// <c>InsertPoint + R * S * (p - BasePoint)</c>; the two agree only when the rotation and scale are identity, so
    /// the insertion point below is compensated to produce AutoCAD's placement. A package upgrade that corrects this
    /// will break <c>ACustomArrowHonoursANonZeroBlockBasePoint</c>, which is the intended tripwire.
    /// </para>
    /// </remarks>
    private bool DrawArrowBlock(ImageRenderContext context, Layer? layer, ResolvedStyle parent, Leader leader, BlockRecord arrow, XY tip, XY direction, double size, double z, Transform? placement)
    {
        string handle = leader.Handle.ToString("X", CultureInfo.InvariantCulture);
        if (arrow.Entities.Count == 0)
        {
            this._configuration.Notify($"[{leader.SubclassMarker}] Handle {handle}: arrowhead block '{arrow.Name}' is empty; the default closed arrow is drawn instead.", NotificationType.Warning);
            return false;
        }

        if (this.BlockGraphIsCircular(arrow))
        {
            this._configuration.Notify($"[{leader.SubclassMarker}] Handle {handle}: arrowhead block '{arrow.Name}' references itself; the default closed arrow is drawn instead.", NotificationType.Warning);
            return false;
        }

        // The map the arrow block's own coordinates must go through: base point to the tip, local +X onto the
        // outward direction, scaled by the arrow size, and then the outer placement.
        XYZ basePoint = arrow.BlockEntity.BasePoint;
        XY across = new(-direction.Y, direction.X);
        XYZ Arrow(XYZ p)
        {
            XY local = new(p.X - basePoint.X, p.Y - basePoint.Y);
            XY placed = tip + (direction * (local.X * size)) + (across * (local.Y * size));
            return InsertPlacement.MapPoint(placement, new XYZ(placed.X, placed.Y, z + ((p.Z - basePoint.Z) * size)));
        }

        XYZ origin = Arrow(basePoint);
        XYZ ex = Arrow(basePoint + XYZ.AxisX) - origin;
        XYZ ey = Arrow(basePoint + XYZ.AxisY) - origin;
        XYZ ez = Arrow(basePoint + XYZ.AxisZ) - origin;
        double scale = new XY(ex.X, ex.Y).GetLength();
        double across2 = new XY(ey.X, ey.Y).GetLength();
        if (!double.IsFinite(scale) || scale < 1e-12 || Math.Abs(scale - across2) > 1e-9 * scale)
        {
            this._configuration.Notify($"[{leader.SubclassMarker}] Handle {handle}: arrowhead block '{arrow.Name}' cannot be placed under a non-uniform transform; the default closed arrow is drawn instead.", NotificationType.Warning);
            return false;
        }

        bool mirrored = (ex.X * ey.Y) - (ex.Y * ey.X) < 0d;
        double rotation = Math.Atan2(ex.Y, ex.X);
        // A reflection is expressed as a negative X scale, which turns the mapped X axis around, so the rotation is
        // taken half a turn further to bring it back.
        Insert transient = new(arrow)
        {
            Rotation = mirrored ? rotation + Math.PI : rotation,
            XScale = mirrored ? -scale : scale,
            YScale = scale,
            ZScale = scale,
        };
        transient.Attributes.Clear();

        // ACadSharp translates by InsertPoint - BasePoint, so the insertion point has to carry the base point back.
        XYZ linearBase = (ex * basePoint.X) + (ey * basePoint.Y) + (ez * basePoint.Z);
        transient.InsertPoint = origin - linearBase + basePoint;
        this.DrawBlockContents(context, transient, layer, parent);
        return true;
    }
```

In `DrawLeader`, replace the block that notifies about a custom arrow with a call to it. The method needs the leader's layer and resolved style to inherit ByBlock and layer 0, so change its signature to `private void DrawLeader(ImageRenderContext context, ImageStyle style, ResolvedStyle resolved, Layer? layer, Leader leader, Transform? placement)` and update the switch arm to `case Leader leader: this.DrawLeader(context, style, resolved, layer, source as Leader ?? leader, placement); break;`. Then replace:

```csharp
        if (leader.Style.LeaderArrow != null)
        {
            this._configuration.Notify($"[{leader.SubclassMarker}] Arrowhead block '{leader.Style.LeaderArrow.Name}' is not rendered; the default closed arrow is drawn instead.", NotificationType.NotImplemented);
        }

        direction /= length;
```

with:

```csharp
        direction /= length;
        double tipZ = leader.Vertices[0].Z;
        if (leader.Style.LeaderArrow != null
            && this.DrawArrowBlock(context, layer, resolved, leader, leader.Style.LeaderArrow, tip, direction, size, tipZ, placement))
        {
            return;
        }
```

and delete the now-duplicated `double z = leader.Vertices[0].Z;` line further down, using `tipZ` in the three arrow corners instead.

- [ ] **Step 4: Run the tests and the suite**

Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror`
Expected: all pass; baselines byte-identical (no sample uses a custom arrow block).

- [ ] **Step 5: Docs**

In spec 4.6, replace the leader clause about custom arrowheads with:

```
A custom arrowhead block (`DimensionStyle.LeaderArrow`) is drawn as a placed block: its base point goes to the leader tip, its local +X axis turns to the outward direction at the tip, and it is scaled by `ArrowSize * ScaleFactor` composed with the placement of any block reference around the leader. A composed transform that is not a planar similarity has no equivalent `Insert`, and an empty or self-referencing arrow block cannot be drawn; each falls back to the default closed triangle with a Warning.
```

In `README.md`, remove custom arrowhead blocks from the known limitations and add a line under a "Caveats" or equivalent existing heading: "A custom arrowhead inside a non-uniformly scaled block reference falls back to the default triangle."

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Draw custom arrowhead blocks at leader tips"
```

---

### Task 6: Inverted wipeout clips and clipping state

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (`WipeoutWorldBoundary` becomes `WipeoutWorldRings`, `DrawWipeout`, `UsesOriginalGeometry`), `ACadSharp.Image/Rendering/EntityBounds.cs` (the wipeout arm)
- Test: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`, `ACadSharp.Image.Tests/ImagePageTests.cs`
- Docs: spec 4.6, `README.md`

**Interfaces:**
- Consumes: `InsertPlacement.MapPoint` and `MapVector` (Task 1).
- Produces: `internal static IReadOnlyList<IReadOnlyList<XYZ>> WipeoutWorldRings(Wipeout wipeout, Transform? placement)`, replacing `internal static IReadOnlyList<XYZ> WipeoutWorldBoundary(Wipeout wipeout)`. It has three consumers that must all be updated: `DrawWipeout`, `EntityBounds.TryGet`, and `ImagePageRenderer.SelectViewportEntities` through `EntityBounds`.

Three behaviours change. An inverted clip (`ClipMode.Inside`) masks the image frame minus the boundary instead of being skipped. Clipping that is switched off ignores the clip mode entirely, where today the inverted-mode check runs first and skips the entity. And a wipeout inside a block is mapped from the original, because `Wipeout.ApplyTransform` transforms the U and V vectors as points.

- [ ] **Step 1: Write the failing tests**

Add to `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`:

```csharp
    [Fact]
    public void AnInvertedWipeoutMasksTheFrameMinusItsBoundary()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Wipeout wipeout = UnitWipeout();
        wipeout.ClipMode = ClipMode.Inside;

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), wipeout);

        IReadOnlyList<IReadOnlyList<SurfacePoint>> rings = Assert.Single(surface.Paths);
        Assert.Equal(2, rings.Count);
        Assert.Empty(surface.Polygons);
    }

    [Fact]
    public void AWipeoutWithClippingOffFillsTheWholeFrameEvenWhenItsModeIsInverted()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Wipeout wipeout = UnitWipeout();
        wipeout.ClipMode = ClipMode.Inside;
        wipeout.ClippingState = false;

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), wipeout);

        Assert.Single(surface.Polygons);
        Assert.Empty(surface.Paths);
    }

    [Fact]
    public void AnOrdinaryWipeoutStillFillsOnePolygon()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), UnitWipeout());

        Assert.Single(surface.Polygons);
        Assert.Empty(surface.Paths);
    }

    [Fact]
    public void AWipeoutInsideAnInsertIsMappedFromTheOriginalSoItsUAndVStayDirections()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("MASK");
        block.Entities.Add(UnitWipeout());
        Insert insert = new(block) { InsertPoint = new XYZ(50, 0, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        IReadOnlyList<SurfacePoint> polygon = Assert.Single(surface.Polygons);
        // UnitWipeout covers x in [0,1]; the insert moves it to [50,51]. A U vector contaminated by the translation
        // would stretch it across the page instead.
        Assert.Equal(50d, polygon.Min(p => p.X), 6);
        Assert.Equal(51d, polygon.Max(p => p.X), 6);
    }

    [Fact]
    public void AHiddenWipeoutDrawsNothing()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Wipeout wipeout = UnitWipeout();
        wipeout.Flags = 0;

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), wipeout);

        Assert.Empty(surface.Polygons);
        Assert.Empty(surface.Paths);
    }
```

And in `ACadSharp.Image.Tests/ImagePageTests.cs`:

```csharp
    [Fact]
    public void TheFrameOfAnInvertedWipeoutIsItsWholeImageFootprint()
    {
        ImagePage page = new();
        Wipeout wipeout = new()
        {
            InsertPoint = new XYZ(0, 0, 0),
            UVector = new XYZ(20, 0, 0),
            VVector = new XYZ(0, 10, 0),
            Size = new XY(1, 1),
            ClippingState = true,
            ClipType = ClipType.Rectangular,
            ClipMode = ClipMode.Inside,
        };
        wipeout.ClipBoundaryVertices.Add(new XY(-0.25, -0.25));
        wipeout.ClipBoundaryVertices.Add(new XY(0.25, 0.25));
        page.AddEntity(wipeout);

        BoundingBox frame = page.ComputeFrame(null)!.Value;

        Assert.Equal(20d, frame.Max.X - frame.Min.X, 6);
        Assert.Equal(10d, frame.Max.Y - frame.Min.Y, 6);
    }
```

Match `ComputeFrame`'s real signature and return type when writing that last test; read `ImagePage.ComputeFrame` first and shape the call and the assertion to what it returns.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.sln --configuration Release --filter "FullyQualifiedName~Wipeout"`
Expected: the inverted, clipping-off and in-block tests fail; the ordinary and hidden ones pass.

- [ ] **Step 3: Replace the boundary helper with rings**

Replace `WipeoutWorldBoundary` with:

```csharp
    /// <summary>
    /// The world rings a wipeout masks: none when the image is hidden, one when it masks a single region, and two —
    /// the whole image frame and the boundary inside it — for an inverted clip, which masks everything except the
    /// boundary. Clipping that is switched off masks the whole frame whatever the clip mode says.
    /// </summary>
    /// <param name="wipeout">The wipeout entity.</param>
    /// <param name="placement">The transform of the insert that placed it, or null at top level.</param>
    /// <returns>Zero, one or two rings of world points.</returns>
    /// <remarks>
    /// The insertion point is mapped as a point and the U and V vectors as directions, from the original entity:
    /// ACadSharp 3.7.1's <c>Wipeout.ApplyTransform</c> maps U and V as points, so a translated clone's vectors carry
    /// the translation and the mask is stretched across the drawing.
    /// </remarks>
    internal static IReadOnlyList<IReadOnlyList<XYZ>> WipeoutWorldRings(Wipeout wipeout, Transform? placement)
    {
        if (!wipeout.Flags.HasFlag(ImageDisplayFlags.ShowImage))
        {
            return [];
        }

        List<XY> frame =
        [
            new XY(-0.5, -0.5),
            new XY(wipeout.Size.X - 0.5, -0.5),
            new XY(wipeout.Size.X - 0.5, wipeout.Size.Y - 0.5),
            new XY(-0.5, wipeout.Size.Y - 0.5),
        ];

        if (!wipeout.ClippingState || wipeout.ClipBoundaryVertices.Count < 2)
        {
            return [Map(frame)];
        }

        List<XY> boundary;
        if (wipeout.ClipType == ClipType.Rectangular || wipeout.ClipBoundaryVertices.Count == 2)
        {
            XY a = wipeout.ClipBoundaryVertices[0];
            XY b = wipeout.ClipBoundaryVertices[1];
            boundary = [a, new XY(b.X, a.Y), b, new XY(a.X, b.Y)];
        }
        else
        {
            boundary = wipeout.ClipBoundaryVertices.ToList();
        }

        return wipeout.ClipMode == ClipMode.Inside
            ? [Map(frame), Map(boundary)]
            : [Map(boundary)];

        IReadOnlyList<XYZ> Map(IEnumerable<XY> pixels) => pixels.Select(p => WipeoutPixelToWorld(wipeout, p, placement)).ToList();
    }
```

Change `WipeoutPixelToWorld` to map through the placement:

```csharp
    /// <summary>
    /// Maps an image-space boundary vertex to world coordinates. Pixel (0,0) is the top-left pixel and Y grows
    /// downwards; <c>UVector</c> runs along the visual bottom and <c>VVector</c> up the visual left side, each one
    /// pixel long. The documented default boundary (-0.5,-0.5)..(Size-0.5) therefore covers exactly the image. The
    /// insertion point is mapped as a point and the two vectors as directions.
    /// </summary>
    internal static XYZ WipeoutPixelToWorld(CadWipeoutBase image, XY pixel, Transform? placement)
    {
        XYZ insertPoint = InsertPlacement.MapPoint(placement, image.InsertPoint);
        XYZ u = InsertPlacement.MapVector(placement, image.UVector);
        XYZ v = InsertPlacement.MapVector(placement, image.VVector);
        return insertPoint + (u * (pixel.X + 0.5)) + (v * (image.Size.Y - pixel.Y - 0.5));
    }
```

- [ ] **Step 4: Draw the rings**

Replace `DrawWipeout`'s body after the opaque-background check with:

```csharp
        IReadOnlyList<IReadOnlyList<XYZ>> rings = WipeoutWorldRings(wipeout, placement);
        if (rings.Count == 0)
        {
            return;
        }

        ImageStyle maskStyle = style with { StrokeColor = background, Opacity = 1f, DashPattern = null };
        if (rings.Count == 1)
        {
            context.Surface.FillPolygon(maskStyle, rings[0].Select(context.ToSurfacePoint).ToArray());
            return;
        }

        // An inverted clip masks everything except the boundary, which is the frame with the boundary as a hole: an
        // even-odd fill over both rings.
        context.Surface.FillPath(maskStyle, rings.Select(ring => (IReadOnlyList<SurfacePoint>)ring.Select(context.ToSurfacePoint).ToArray()).ToList());
```

Delete the `ClipMode.Inside` early return and its NotImplemented notification, and delete the comment above the `ShowImage` check that pointed at the old helper. Change the signature to `private void DrawWipeout(ImageRenderContext context, ImageStyle style, Wipeout wipeout, Transform? placement)`, update the switch arm to `case Wipeout wipeout: this.DrawWipeout(context, style, source as Wipeout ?? wipeout, placement); break;`, and add `Wipeout` to the `UsesOriginalGeometry` always-original list so the body reads:

```csharp
        if (original is TextEntity or MText or Leader or Hatch or Wipeout)
        {
            return true;
        }
```

- [ ] **Step 5: Update the bounds**

In `EntityBounds.cs`, replace the wipeout arm so it bounds by every ring point:

```csharp
            case Wipeout wipeout:
                return TryFromPoints(EntityRenderDispatcher.WipeoutWorldRings(wipeout, null).SelectMany(ring => ring), out bounds);
```

Keep whatever the surrounding method's exact shape is — read it first — and keep the `error` handling unchanged.

- [ ] **Step 6: Run the tests and the suite**

Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror`
Expected: all pass. The features sample's wipeout is an ordinary outside clip, so baselines stay byte-identical. If any moves, STOP and report BLOCKED with the diff.

- [ ] **Step 7: Docs**

In spec 4.6, replace the wipeout clause with:

```
A wipeout masks its clip boundary, or the whole image frame when clipping is off, whatever its clip mode says. An inverted clip (`ClipMode.Inside`) masks the frame minus the boundary as a single even-odd path. Its geometry is taken from the original entity, with the insertion point mapped as a point and the U and V vectors as directions. Framing and viewport culling use the same rings, so an inverted wipeout is bounded by its whole footprint. A wipeout still needs an opaque background to mask, and is skipped with a Warning otherwise.
```

In `README.md`, remove inverted wipeout clips from the known limitations.

- [ ] **Step 8: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image/Rendering/EntityBounds.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs ACadSharp.Image.Tests/ImagePageTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Mask inverted wipeout clips and ignore the clip mode when clipping is off"
```

---

### Task 7: MLEDIT cut segments

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (`DrawMLine`, the MLINE arm of `HasFiniteGeometry`)
- Test: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`
- Docs: spec 4.6, `README.md`

**Interfaces:**
- Consumes: `InsertPlacement.MapPoint` (Task 1).
- Produces: `internal static IReadOnlyList<(double Start, double End)> VisibleRuns(IReadOnlyList<double> parameters, double length)`, the interval arithmetic, exposed for its own tests.

An MLINE element's parameters are, per the DXF reference: `p[0]` the offset from the vertex along the miter, `p[1]` the distance from that intersection to the element's actual start, and `p[2..]` the positions where the element breaks and resumes, alternating. An odd count ends hidden. A break at or past the segment's end means no cut at all, which is what the one real-world sample contains.

**This interpretation is not confirmed.** The DXF prose reads as absolute positions; ezdxf's comments describe relative dash and gap lengths, and neither ezdxf nor LibreDWG implements cuts. The only real sample has three parameters whose third equals the segment length, which both readings render identically. Implement the absolute reading, keep the limitation note in the README saying so, and do not present it as verified.

- [ ] **Step 1: Write the failing tests**

Add to `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`:

```csharp
    [Theory]
    // No cut values: one run covering the whole element.
    [InlineData(new double[] { 0.5, 0 }, 10d, new double[] { 0, 10 })]
    // A single break at the end is not a cut.
    [InlineData(new double[] { 0.5, 0, 10 }, 10d, new double[] { 0, 10 })]
    // Break at 4, resume at 6.
    [InlineData(new double[] { 0.5, 0, 4, 6 }, 10d, new double[] { 0, 4, 6, 10 })]
    // Odd count: the element ends hidden after the last value.
    [InlineData(new double[] { 0.5, 0, 4 }, 10d, new double[] { 0, 4 })]
    // Two cuts.
    [InlineData(new double[] { 0.5, 0, 2, 3, 7, 8 }, 10d, new double[] { 0, 2, 3, 7, 8, 10 })]
    // Values past the end are clamped.
    [InlineData(new double[] { 0.5, 0, 4, 99 }, 10d, new double[] { 0, 4 })]
    public void VisibleRunsFollowTheAbsoluteCutPositions(double[] parameters, double length, double[] expected)
    {
        IReadOnlyList<(double Start, double End)> runs = EntityRenderDispatcher.VisibleRuns(parameters, length);

        Assert.Equal(expected.Length / 2, runs.Count);
        for (int i = 0; i < runs.Count; i++)
        {
            Assert.Equal(expected[2 * i], runs[i].Start, 9);
            Assert.Equal(expected[(2 * i) + 1], runs[i].End, 9);
        }
    }

    [Fact]
    public void VisibleRunsStopAtANonFiniteOrDecreasingValue()
    {
        Assert.Equal([(0d, 4d)], EntityRenderDispatcher.VisibleRuns([0.5, 0, 4, double.NaN, 8], 10d));
        Assert.Equal([(0d, 4d)], EntityRenderDispatcher.VisibleRuns([0.5, 0, 4, 3], 10d));
    }

    [Fact]
    public void AnMLineWithACutDrawsTwoRunsForThatElement()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLineStyle style = new("CUT");
        style.AddElement(new MLineStyle.Element { Offset = 0 });
        MLine mline = new()
        {
            Style = style,
            Vertices =
            {
                VertexAt(0, 10, [0, 0, 4, 6]),
                VertexAt(20, 10, [0, 0, 4, 6]),
            },
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.Equal(2, surface.Polylines.Count);
        Assert.Equal([new SurfacePoint(0, 90), new SurfacePoint(4, 90)], surface.Polylines[0]);
        Assert.Equal([new SurfacePoint(6, 90), new SurfacePoint(20, 90)], surface.Polylines[1]);
    }

    [Fact]
    public void AnMLineWithoutCutsStillDrawsOnePolylinePerElement()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLine mline = new()
        {
            Style = TwoElementStyle(0.5),
            Vertices = { VertexAt(0, 10), VertexAt(20, 10) },
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.Equal(2, surface.Polylines.Count);
    }

    [Fact]
    public void ACutMLineInsideAScaledInsertScalesItsRuns()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLineStyle style = new("CUT");
        style.AddElement(new MLineStyle.Element { Offset = 0 });
        BlockRecord block = new("WALL");
        block.Entities.Add(new MLine
        {
            Style = style,
            Vertices = { VertexAt(0, 0, [0, 0, 4, 6]), VertexAt(20, 0, [0, 0, 4, 6]) },
        });
        Insert insert = new(block) { InsertPoint = new XYZ(0, 10, 0), XScale = 2, YScale = 2, ZScale = 2 };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Equal(2, surface.Polylines.Count);
        Assert.Equal([new SurfacePoint(0, 90), new SurfacePoint(8, 90)], surface.Polylines[0]);
        Assert.Equal([new SurfacePoint(12, 90), new SurfacePoint(40, 90)], surface.Polylines[1]);
    }

    [Fact]
    public void AnMLineWithAreaFillCutsNotifiesThatFillCutsAreNotDrawn()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        MLine mline = new() { Style = TwoElementStyle(0.5), Vertices = { VertexAt(0, 10), VertexAt(20, 10) } };
        mline.Vertices[0].Segments[0].AreaFillParameters.Add(2);
        mline.Vertices[0].Segments[0].AreaFillParameters.Add(5);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.Contains(notifications, n => n.NotificationType == NotificationType.NotImplemented && n.Message.Contains("fill cuts", StringComparison.OrdinalIgnoreCase));
    }
```

`VertexAt(x, y, params double[][] segments)` already exists in the test file; the two-argument form gives a vertex with the default segments. Check its exact signature and pass the cut parameters the way it expects.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.sln --configuration Release --filter "FullyQualifiedName~VisibleRuns|FullyQualifiedName~Cut"`
Expected: build failure (`VisibleRuns` does not exist), then the MLINE tests fail because elements are drawn continuous with a warning.

- [ ] **Step 3: Implement the interval arithmetic**

Add to `EntityRenderDispatcher.cs`:

```csharp
    /// <summary>
    /// The visible runs of one MLINE element, as distances from the element's own start. DXF group 41 stores, after
    /// the miter offset and the element's start offset, the positions at which the element breaks and resumes,
    /// alternating; an odd count leaves the element hidden to its end. Values are clamped to the element's length,
    /// and the list is cut short at the first value that is not finite or not greater than the one before it.
    /// </summary>
    /// <param name="parameters">The element's stored parameters, starting with the miter offset.</param>
    /// <param name="length">The element's length between this vertex and the next.</param>
    /// <returns>The visible runs, in order; a single full-length run when there are no usable cut positions.</returns>
    /// <remarks>
    /// Reading these as absolute positions is the literal sense of the DXF reference. ezdxf's model comments read the
    /// same array as relative dash and gap lengths, and neither ezdxf nor LibreDWG draws cuts at all, so no
    /// implementation settles it; the two readings agree only on a single cut. This is the interpretation the
    /// renderer implements and the README records it as unconfirmed.
    /// </remarks>
    internal static IReadOnlyList<(double Start, double End)> VisibleRuns(IReadOnlyList<double> parameters, double length)
    {
        if (!double.IsFinite(length) || length <= 0d)
        {
            return [];
        }

        List<double> breaks = new();
        double previous = 0d;
        for (int i = 2; i < parameters.Count; i++)
        {
            double value = parameters[i];
            if (!double.IsFinite(value) || value <= previous)
            {
                break;
            }

            if (value >= length)
            {
                break;
            }

            breaks.Add(value);
            previous = value;
        }

        if (breaks.Count == 0)
        {
            return [(0d, length)];
        }

        List<(double Start, double End)> runs = new();
        double start = 0d;
        for (int i = 0; i < breaks.Count; i += 2)
        {
            runs.Add((start, breaks[i]));
            start = i + 1 < breaks.Count ? breaks[i + 1] : double.NaN;
            if (double.IsNaN(start))
            {
                return runs;
            }
        }

        runs.Add((start, length));
        return runs;
    }
```

- [ ] **Step 4: Draw the runs**

`DrawMLine` already builds `lines[j][i]`, the surface point of element `j` at vertex `i`, and then draws one polyline per element. Keep all of that, and additionally keep the **world** point each of those came from, because the stored cut positions are distances in drawing units and must be measured against a drawing-unit segment length, never against a surface length.

In the loop that fills `lines`, declare `XYZ[][] world = new XYZ[elements.Length][];` beside it, allocate `world[j] = new XYZ[vertices.Count];` with `lines[j]`, and record the placed world point before projecting it:

```csharp
                XYZ placed = InsertPlacement.MapPoint(placement, vertex.Position + (vertex.Miter * along));
                world[j][i] = placed;
                lines[j][i] = context.ToSurfacePoint(placed);
```

Then add, immediately before the existing element-drawing loop, a per-element decision: an element with no real cut keeps its single `DrawPolyline` call, and only a cut element is drawn as separate runs.

Replace the existing element-drawing loop with:

```csharp
        for (int j = 0; j < elements.Length; j++)
        {
            // An element linetype named ByLayer/ByBlock is not itself a drawable pattern: it means the element
            // inherits the entity's own resolved dashes, same as a null element linetype, rather than being handed
            // to the resolver, which would otherwise treat the placeholder name as an unknown (solid) linetype.
            LineType? elementType = elements[j].LineType;
            float[]? dashes = elementType == null
                || ImageStyleResolver.IsNamed(elementType, LineType.ByLayerName)
                || ImageStyleResolver.IsNamed(elementType, LineType.ByBlockName)
                ? style.DashPattern
                : LineTypeDashResolver.Resolve(elementType, resolved.Header, resolved.LineTypeScale, context, style.StrokeWidth);
            ImageStyle elementStyle = style with { StrokeColor = ElementColor(elements[j].Color), DashPattern = dashes };

            // An uncut element stays one polyline: drawing it as a chain of separate lines would restart a dashed
            // linetype's phase at every vertex and would move every existing golden.
            if (!HasCut(j))
            {
                context.Surface.DrawPolyline(elementStyle, lines[j], closed);
                continue;
            }

            int lastVertex = closed ? vertices.Count : vertices.Count - 1;
            for (int i = 0; i < lastVertex; i++)
            {
                int next = (i + 1) % vertices.Count;
                SurfacePoint from = lines[j][i];
                SurfacePoint to = lines[j][next];
                foreach ((double t0, double t1) in RunFractions(j, i, next))
                {
                    SurfacePoint a = new(from.X + ((to.X - from.X) * t0), from.Y + ((to.Y - from.Y) * t0));
                    SurfacePoint b = new(from.X + ((to.X - from.X) * t1), from.Y + ((to.Y - from.Y) * t1));
                    context.Surface.DrawLine(elementStyle, a, b);
                }
            }
        }

        // The visible runs of one segment, as fractions of its length. The stored cut positions are distances in
        // drawing units, so the segment they are measured against has to be the world one; the fractions are then
        // applied to the already-projected surface points, which is exact because the projection is affine.
        IReadOnlyList<(double Start, double End)> RunFractions(int element, int from, int to)
        {
            double segmentLength = (world[element][to] - world[element][from]).GetLength();
            if (segmentLength <= 0d || !double.IsFinite(segmentLength))
            {
                return [];
            }

            IReadOnlyList<double> parameters = element < vertices[from].Segments.Count ? vertices[from].Segments[element].Parameters : [];
            return VisibleRuns(parameters, segmentLength).Select(run => (run.Start / segmentLength, run.End / segmentLength)).ToList();
        }

        // Whether any segment of this element is broken, i.e. yields anything other than one run covering the whole
        // segment. An unbroken element keeps its single polyline.
        bool HasCut(int element)
        {
            int lastVertex = closed ? vertices.Count : vertices.Count - 1;
            for (int i = 0; i < lastVertex; i++)
            {
                IReadOnlyList<(double Start, double End)> runs = RunFractions(element, i, (i + 1) % vertices.Count);
                if (runs.Count != 1 || runs[0].Start > 1e-12 || runs[0].End < 1d - 1e-12)
                {
                    return true;
                }
            }

            return false;
        }
```

Delete the `cuts` local and the loop that sets it, and delete the "cut segments are not rendered" notification.

Replace the `cuts` warning with a fill-cut notification, raised once per entity when any vertex segment has a non-empty `AreaFillParameters`:

```csharp
        if (vertices.Any(v => v.Segments.Any(s => s.AreaFillParameters.Count > 0)))
        {
            this._configuration.Notify($"[{mline.SubclassMarker}] Handle {handle}: fill cuts are not drawn; the filled band is continuous.", NotificationType.NotImplemented);
        }
```

Extend the MLINE arm of `HasFiniteGeometry` so every parameter and both `Miter` and `Position` are validated, not only `Parameters[0]`. Read that method first and add the checks in its existing style.

- [ ] **Step 5: Run the tests and the suite**

Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror`
Expected: all pass; baselines byte-identical, because no sample MLINE carries cut positions and the no-cut path still emits one polyline per element. If any baseline moves, STOP and report BLOCKED with the diff — a moved baseline means the no-cut path changed shape, which this task forbids.

- [ ] **Step 6: Docs**

In spec 4.6, replace the MLINE cut clause with:

```
Cut positions (DXF group 41 beyond the first two values) are drawn: they are read as absolute distances from the element's start, alternating break and resume, with an odd count leaving the element hidden to its end and a break at or past the segment's end meaning no cut. An element with no usable cut positions is still drawn as a single polyline, so its linetype phase is unbroken. Fill cuts (group 42) are notified, not drawn.
```

In `README.md`, replace the MLEDIT limitation with: "MLINE cut segments are drawn from DXF group 41 read as absolute positions. The DXF reference reads that way, but ezdxf reads the same values as relative dash and gap lengths and no implementation settles it, so a drawing with more than one cut per element may differ from AutoCAD. Fill cuts are not drawn."

- [ ] **Step 7: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Draw MLINE cut segments from their stored positions"
```

---

### Task 8: A golden that exercises all five

**Files:**
- Modify: `ACadSharp.Image.Tests/SyntheticSamples.cs` (add `FidelityBlock()`)
- Create: `ACadSharp.Image.Tests/FidelityGoldenTests.cs`, `ACadSharp.Image.Tests/Baselines/fidelity.model.01.png`, `ACadSharp.Image.Tests/Baselines/fidelity.model.01.svg`
- Docs: `README.md`

**Interfaces:**
- Consumes: everything from Tasks 2 to 7.
- Produces: `internal static BlockRecord FidelityBlock()` on `SyntheticSamples`.

- [ ] **Step 1: Add the synthetic block**

Add to `SyntheticSamples.cs`, following the shape of the existing `EntityBlock()` (its layers, its `WithHandle` numbering and its `MLineVertex` helper):

```csharp
    /// <summary>
    /// A block exercising every feature the remaining-limitations work added: a multi-line attribute, a hatch on a
    /// tilted plane inside a block, a leader with a custom arrowhead block, an inverted wipeout over a line, and an
    /// MLINE with a cut in both of its elements. Handles increase in draw order, so the wipeout follows the line it
    /// masks.
    /// </summary>
    /// <returns>The block, with every entity on its own named layer.</returns>
    public static BlockRecord FidelityBlock()
    {
        BlockRecord block = new("fidelity");
        Layer roomsLayer = new("Rooms") { Color = new Color(9) };
        Layer hatchLayer = new("Tilted") { Color = new Color(3) };
        Layer leaderLayer = new("Leader") { Color = new Color(4) };
        Layer underLayer = new("Under") { Color = new Color(1) };
        Layer coverLayer = new("Cover") { Color = new Color(8) };
        Layer wallLayer = new("Wall") { Color = new Color(6) };

        // Multi-line attribute: the single-line value must never appear in the output.
        BlockRecord label = new("LABEL");
        label.Entities.Add(new AttributeDefinition { Tag = "ROOM", Value = "FLAT", Layer = roomsLayer });
        Insert labelInsert = WithHandle(new Insert(label) { InsertPoint = new XYZ(10, 80, 0), Layer = roomsLayer }, 0x10);
        labelInsert.Attributes.Clear();
        labelInsert.Attributes.Add(WithHandle(new AttributeEntity
        {
            Tag = "ROOM",
            Value = "FLAT",
            AttributeType = AttributeType.MultiLine,
            InsertPoint = new XYZ(10, 80, 0),
            Height = 4,
            Layer = roomsLayer,
            MText = new MText { Value = "Room 1\\PLevel 2", InsertPoint = new XYZ(10, 80, 0), Height = 4, RectangleWidth = 40 },
        }, 0x11));
        block.Entities.Add(labelInsert);

        // Tilted hatch inside a block: normal (0,0,-1) mirrors X on the way to world.
        BlockRecord tilted = new("TILTED");
        Hatch hatch = new() { IsSolid = true, Normal = new XYZ(0, 0, -1), Elevation = 0d, Layer = hatchLayer };
        hatch.Paths.Add(SquarePath(0, 0, 20, 15));
        tilted.Entities.Add(hatch);
        block.Entities.Add(WithHandle(new Insert(tilted) { InsertPoint = new XYZ(80, 70, 0), Layer = hatchLayer }, 0x12));

        // Custom arrowhead block: tip at the base point, body back along local -X.
        BlockRecord arrow = new("FIDELITY_ARROW");
        arrow.Entities.Add(new Line(new XYZ(-1, 0, 0), new XYZ(0, 0, 0)));
        arrow.Entities.Add(new Solid
        {
            FirstCorner = new XYZ(-1, -0.25, 0),
            SecondCorner = new XYZ(0, 0, 0),
            ThirdCorner = new XYZ(-1, 0.25, 0),
            FourthCorner = new XYZ(0, 0, 0),
        });
        block.Entities.Add(WithHandle(new Leader
        {
            ArrowHeadEnabled = true,
            Style = new DimensionStyle("FIDELITY") { ArrowSize = 4, ScaleFactor = 1, LeaderArrow = arrow },
            Layer = leaderLayer,
            Vertices = { new XYZ(10, 40, 0), new XYZ(35, 55, 0), new XYZ(55, 55, 0) },
        }, 0x13));

        // Inverted wipeout over a line: only the middle band of the line survives.
        block.Entities.Add(WithHandle(new Line(new XYZ(60, 20, 0), new XYZ(110, 20, 0)) { Layer = underLayer }, 0x14));
        Wipeout wipeout = WithHandle(new Wipeout
        {
            InsertPoint = new XYZ(60, 10, 0),
            UVector = new XYZ(50, 0, 0),
            VVector = new XYZ(0, 20, 0),
            Size = new XY(1, 1),
            ClippingState = true,
            ClipType = ClipType.Rectangular,
            ClipMode = ClipMode.Inside,
            Layer = coverLayer,
        }, 0x15);
        wipeout.ClipBoundaryVertices.Add(new XY(-0.2, -0.5));
        wipeout.ClipBoundaryVertices.Add(new XY(0.2, 0.5));
        block.Entities.Add(wipeout);

        // Cut MLINE: both elements break between 20 and 30 along their own length.
        MLineStyle wallStyle = new("FIDELITY_WALL");
        wallStyle.AddElement(new MLineStyle.Element { Offset = 1 });
        wallStyle.AddElement(new MLineStyle.Element { Offset = -1 });
        block.Entities.Add(WithHandle(new MLine
        {
            Style = wallStyle,
            Layer = wallLayer,
            Vertices =
            {
                MLineVertex(new XYZ(10, 10, 0), new XYZ(1, 0, 0), new XYZ(0, 1, 0), [1, 0, 20, 30], [-1, 0, 20, 30]),
                MLineVertex(new XYZ(60, 10, 0), new XYZ(1, 0, 0), new XYZ(0, 1, 0), [1, 0, 20, 30], [-1, 0, 20, 30]),
            },
        }, 0x16));

        return block;
    }
```

`SquarePath` is the helper Task 3 added to the dispatcher tests; move it to `SyntheticSamples` as an `internal static` helper with a `<summary>` and have the dispatcher tests call it there, rather than writing a second copy. If `MLineVertex`'s existing signature does not take the parameter arrays as shown, match whatever it takes and keep the values.

- [ ] **Step 2: Write the golden tests**

Create `ACadSharp.Image.Tests/FidelityGoldenTests.cs` mirroring `EntityGoldenTests`: an exporter factory (800 by 500, padding 10, DejaVu Sans), `FidelityPngMatchesBaseline` calling `GoldenAssert.Png("fidelity.model.01", ...)`, and `FidelitySvgMatchesGoldenAndContainsEveryFeature` calling `GoldenAssert.Svg("fidelity.model.01", ...)` and then asserting, scoped to the relevant `data-layer` group each time:

- the attribute is one `<text>` with two `<tspan>` children whose values are `Room 1` and `Level 2`, and `data-type="ATTRIB"`;
- the tilted hatch is one `<path>` whose points lie in the x range the OCS mirror plus the insert produces (compute it and assert the min and max x within 1e-6);
- the arrow is at least one `<polygon>` with `data-type="SOLID"` inside the leader's layer group, and no `<polygon>` matching the default triangle's three-point shape;
- the inverted wipeout is one `<path>` with two rings filled `#ffffff`;
- the MLINE contributes four `<line>` or `<polyline>` elements (two runs for each of two elements);
- no notification of type `NotImplemented` is raised, and the only `Warning` is none.

Add a raster occlusion assertion to the PNG test in the style `EntityGoldenTests` uses: a pixel on the `Under` line inside the wipeout's masked region is white, and one inside the boundary hole is not, with both positions derived from the exporter's fit rather than hard-coded.

- [ ] **Step 3: Create the baselines**

Run: `ACADSHARP_IMAGE_UPDATE_BASELINES=1 dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~FidelityGoldenTests"`
Then run `git status --short ACadSharp.Image.Tests/Baselines` and confirm only `fidelity.model.01.png` and `fidelity.model.01.svg` appear. Open the PNG with the Read tool and describe it in your report, feature by feature, confirming each of the five is visible and looks right — a passing byte comparison against a file you just generated proves nothing on its own.

- [ ] **Step 4: Run the suite**

Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror`
Expected: all pass; no baseline other than the two new files changed.

- [ ] **Step 5: Docs**

In `README.md`, make sure the known limitations section now lists only what genuinely remains: MLINE fill cuts, the unconfirmed cut interpretation, a custom arrowhead under a non-uniform block reference, wipeouts on a translucent background, and exact cross-layer painter order in SVG.

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image.Tests/SyntheticSamples.cs ACadSharp.Image.Tests/FidelityGoldenTests.cs ACadSharp.Image.Tests/Baselines/fidelity.model.01.png ACadSharp.Image.Tests/Baselines/fidelity.model.01.svg README.md
git commit -m "Add a golden that exercises every newly drawn feature"
```

---

## Self-Review

**Spec coverage.** Spec 4.1 placement helpers → Task 1. Spec 4.2 pairing → Task 3 (hatch) and Task 6 (wipeout) extend the one existing relation; spec 4.2 cycles → Task 4. Spec 4.3 arrow blocks → Task 5. Spec 4.4 wipeout rings and clipping state → Task 6. Spec 4.5 MLEDIT cuts → Task 7. Spec 4.6 tilted hatches → Task 3. Spec 4.7 multi-line attributes → Task 2. Spec 5 notification changes → each task removes or adds its own. Spec 6 tests and goldens → each task plus Task 8. Spec 7 documentation → each task plus Task 8.

Two spec items are deliberately **not** implemented, matching the spec's own non-goals: the `Circle` to `Ellipse` pairing conversion (the relation requires identical runtime types, and no task needs a circle's original geometry), and the per-entity pairing mismatch warning (no task in this plan makes a mismatch more likely, and the existing count warning still catches a package change). If a reviewer raises either, they belong to a follow-up, not here.

**Type consistency.** `InsertPlacement.MapPoint`, `MapVector`, `MapOcsPoint` and `TryGetPlanarSimilarity` are defined in Task 1 and used with those exact names in Tasks 3, 5, 6 and 7. `WipeoutWorldRings(Wipeout, Transform?)` replaces `WipeoutWorldBoundary(Wipeout)` in Task 6 and all three of its consumers are named there. `DrawHatch` gains its `Transform?` parameter in Task 3, `DrawWipeout` in Task 6, and `DrawLeader` gains `ResolvedStyle` and `Layer?` in Task 5; each task updates its own switch arm. `BlockGraphIsCircular` is defined in Task 4 and used in Task 5. `VisibleRuns` is defined and used in Task 7 only.

**Ordering.** Task 1 must come first (everything uses it). Task 4 must precede Task 5 (the arrow guard uses it). Tasks 2, 3, 6 and 7 are independent of each other. Task 8 must come last.
