# Remaining Entities and Raster Text Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the limitations recorded in `docs/research/remaining-rendering-limitations.md`: draw 3DFACE, LEADER, MLINE and WIPEOUT entities and block attributes on both backends, respect the drawing's draw order, make the explode pairing observable, and make raster text independent of `ImageConfiguration.Dpi` with a correct line-spacing compensation.

**Architecture:** Every new entity type becomes one `case` in `EntityRenderDispatcher.Draw` plus one private `Draw<Type>` helper that projects CAD geometry to `SurfacePoint`s and calls the existing backend-neutral `IDrawingSurface` primitives (`DrawPolyline`, `DrawCubicBezier`, `FillPolygon`, `DrawLine`), so both PNG and SVG gain each entity at once. Block attributes reuse the TEXT pipeline. The raster surface sizes text in ems at a fixed 72 dpi like the SVG backend already does.

**Tech Stack:** .NET 8/10, ACadSharp 3.7.1, SixLabors.ImageSharp 3.1.12 / Drawing 2.1.7 / Fonts 2.1.3, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-02-layers-and-svg-design.md` (binding). This plan adds section "4.6 Additional entities (2026-09-03)" and amends 5.3 "Text"; the research note `docs/research/remaining-rendering-limitations.md` is the argument, not the authority. Where the research note and this plan disagree (the WIPEOUT mapping and the MLINE cut parameters), this plan wins; the reasons are given in the tasks.

## Global Constraints

- ACadSharp `3.7.1`; SixLabors packages as pinned; no new NuGet dependencies; target frameworks unchanged.
- Coding conventions: `this.` prefix on instance members, explicit types except LINQ lambdas, XML docs on public and internal members (private helpers get a `<summary>` when their name does not say it all), `sealed` classes, file-scoped namespaces, four-space indent, UTF-8 without BOM, LF line endings.
- PNG baselines and SVG goldens in `ACadSharp.Image.Tests/Baselines/` must stay byte-identical in Tasks 2 to 6 (no sample or synthetic drawing contains the entity types they add). Task 1 and Task 7 may move baselines only as described in those tasks, regenerated with the scoped commands given there, with the cause in the commit body.
- `dotnet build ACadSharp.Image.sln -warnaserror` warning-free; full suite green before each commit (`dotnet test ACadSharp.Image.sln --configuration Release -warnaserror`).
- No reference to any drawing outside `Samples/` in code, tests, comments or commit messages.
- Never use bare `git stash` / `git stash pop`. Commit messages end with the repository's two trailer lines (see any commit on this branch).
- New notifications use the existing `ImageConfiguration.Notify(message, NotificationType[, exception])` and the message shape `[{entity.SubclassMarker}] ...`.
- Tests that need a fixed handle use the reflection helper pattern from `EntityRenderDispatcherTests.WithHandle` (`CadObject.Handle` has an internal setter in 3.7.1).
- In `EntityRenderDispatcherTests.CreateContext` the surface is 100x100 with scale 1 and no offset, so a CAD point `(x, y)` lands at `SurfacePoint(x, 100 - y)`.

## File Structure

- Modify `ACadSharp.Image/ImagePage.cs` (`Add`), `ACadSharp.Image/ImageExporter.cs` (layout page loop): draw order.
- Modify `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs`: new `case` arms and helpers `DrawFace3D`, `DrawLeader`, `DrawMLine`, `DrawWipeout`, `DrawAttributes`, `IsAttributeVisible`; `DrawBlockContents` changes.
- Modify `ACadSharp.Image/Rendering/RasterDrawingSurface.cs` (`DrawText`, `CreateFont`).
- Tests: new `ACadSharp.Image.Tests/ImagePageTests.cs`; extend `EntityRenderDispatcherTests.cs`, `RasterDrawingSurfaceTests.cs`.
- Docs: spec new section 4.6 and 5.3 amendment; `README.md` gets a "Supported entities" section; `docs/research/remaining-rendering-limitations.md` gets a status line per topic.

---

### Task 1: Draw pages in the drawing's sort order

**Files:**
- Modify: `ACadSharp.Image/ImagePage.cs:89-110`
- Modify: `ACadSharp.Image/ImageExporter.cs:110`
- Create: `ACadSharp.Image.Tests/ImagePageTests.cs`
- Modify: `README.md`, spec section 4.6 (new)

**Interfaces:**
- Consumes: `BlockRecord.GetSortedEntities()` (ACadSharp 3.7.1: `IEnumerable<Entity>`, entities ordered by handle, then by the block's `SortEntitiesTable` when present).
- Produces: nothing new; `ImagePage.Entities` order changes.

Background: `ImagePage.Add` and the layout loop in `ImageExporter` enumerate `block.Entities` (file order). AutoCAD draws by handle order overridden by the DRAWORDER table; ACadSharp exposes exactly that as `GetSortedEntities()`. Later entities paint over earlier ones, which is what a WIPEOUT (Task 6) relies on.

- [ ] **Step 1: Write the failing test**

Create `ACadSharp.Image.Tests/ImagePageTests.cs`:

```csharp
using System.Reflection;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class ImagePageTests
{
    private static T WithHandle<T>(T entity, ulong handle)
        where T : CadObject
    {
        typeof(CadObject).GetProperty(nameof(CadObject.Handle))!.SetValue(entity, handle);
        return entity;
    }

    [Fact]
    public void AddOrdersEntitiesByHandleNotByInsertionOrder()
    {
        BlockRecord block = new("ORDER");
        Line later = WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)), 0x20);
        Line earlier = WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)), 0x10);
        block.Entities.Add(later);
        block.Entities.Add(earlier);

        ImagePage page = new();
        page.Add(block, resizeLayout: false);

        Assert.Equal([0x10UL, 0x20UL], page.Entities.Select(e => e.Handle));
    }

    [Fact]
    public void AddWithFilterKeepsTheSortedOrder()
    {
        BlockRecord block = new("ORDER");
        block.Entities.Add(WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)), 0x30));
        block.Entities.Add(WithHandle(new Circle { Center = new XYZ(0, 0, 0), Radius = 1 }, 0x20));
        block.Entities.Add(WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)), 0x10));

        ImagePage page = new();
        page.Add(block, e => e is Line, resizeLayout: false);

        Assert.Equal([0x10UL, 0x30UL], page.Entities.Select(e => e.Handle));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~ImagePageTests"`
Expected: both FAIL (order is 0x20, 0x10 and 0x30, 0x10).

- [ ] **Step 3: Switch the two loops and the exporter to the sorted enumeration**

In `ImagePage.Add(BlockRecord, Func<Entity, bool>?, bool)` replace both `foreach (Entity entity in block.Entities)` with `foreach (Entity entity in block.GetSortedEntities())` and add to the method's `<remarks>`: "Entities are added in the drawing's draw order (handle order, overridden by the block's DRAWORDER table), so later entities paint over earlier ones on both backends."

In `ImageExporter.cs:110` replace `layout.AssociatedBlock.Entities` with `layout.AssociatedBlock.GetSortedEntities()`.

- [ ] **Step 4: Run the new tests, then the whole suite**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~ImagePageTests"` → PASS.
Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror`.

If a PNG baseline or SVG golden fails, the sample's file order differs from its handle order. Then: (a) for each failing golden, diff the SVG text (`git diff --no-index` against a regenerated copy) and confirm the only change is element order inside `<g>` groups (SVG) or overlap pixels (PNG); (b) regenerate exactly the failing baselines with the scoped command, for example `ACADSHARP_IMAGE_UPDATE_BASELINES=1 dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~SampleParityTests.SampleSvgsMatchGoldens"`; (c) list each regenerated file with the cause in the commit body. If a change is not explainable by ordering, stop and report.

- [ ] **Step 5: Docs**

Spec: append a new section after 4.5:

```markdown
### 4.6 Additional entities (2026-09-03)

- Draw order: pages enumerate `BlockRecord.GetSortedEntities()` (handle order, then the DRAWORDER `SortEntitiesTable`), not file order, on both backends.
```

README: after the "Layer visibility" paragraphs (around line 234) add a section:

```markdown
### Supported entities

Lines, arcs, circles, ellipses, polylines (2D, 3D, lightweight, with bulges), splines, points, solids, hatches (solid and pattern), TEXT, MTEXT, dimensions, block references and paper-space viewports. Entities are drawn in the drawing's draw order (handle order overridden by DRAWORDER), so later entities paint over earlier ones.
```

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/ImagePage.cs ACadSharp.Image/ImageExporter.cs ACadSharp.Image.Tests/ImagePageTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Draw page entities in the drawing's sort order"
```

---

### Task 2: Draw 3DFACE edges

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (switch in `Draw`, new `DrawFace3D` next to `DrawSolid`)
- Modify: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`
- Modify: `README.md` ("Supported entities"), spec 4.6

**Interfaces:**
- Consumes: `ACadSharp.Entities.Face3D` (`XYZ FirstCorner/SecondCorner/ThirdCorner/FourthCorner`, `InvisibleEdgeFlags Flags` with `None=0, First=1, Second=2, Third=4, Fourth=8`). Corners are WCS; no OCS step.
- Produces: `private static void DrawFace3D(ImageRenderContext context, ImageStyle style, Face3D face)`.

Background: edge n joins corner n to corner n+1; edge 4 closes corner 4 back to corner 1. A triangle repeats the third corner as the fourth (DXF reference), so its closing edge is edge 4 (flag `Fourth`) and edge 3 is degenerate. A 3DFACE is a wireframe primitive in a plan view: it is stroked, never filled.

- [ ] **Step 1: Write the failing tests**

Append to `EntityRenderDispatcherTests`:

```csharp
    [Fact]
    public void Face3DWithAllEdgesVisibleIsOneClosedPolyline()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Face3D face = new()
        {
            FirstCorner = new XYZ(0, 0, 0),
            SecondCorner = new XYZ(10, 0, 0),
            ThirdCorner = new XYZ(10, 10, 0),
            FourthCorner = new XYZ(0, 10, 0),
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), face);

        Assert.Equal(["DrawPolyline n=4 closed=True"], surface.Calls.Where(c => c.StartsWith("Draw", StringComparison.Ordinal)));
        Assert.Equal(new SurfacePoint(0, 100), surface.Polylines[0][0]);
        Assert.Equal(new SurfacePoint(0, 90), surface.Polylines[0][3]);
        Assert.Empty(surface.Polygons);
    }

    [Fact]
    public void Face3DSkipsInvisibleEdgesAndKeepsTheVisibleRunsJoined()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        // Edges 2 (10,0)->(10,10) and 4 (0,10)->(0,0) hidden: two separate open edges remain.
        Face3D face = new()
        {
            FirstCorner = new XYZ(0, 0, 0),
            SecondCorner = new XYZ(10, 0, 0),
            ThirdCorner = new XYZ(10, 10, 0),
            FourthCorner = new XYZ(0, 10, 0),
            Flags = InvisibleEdgeFlags.Second | InvisibleEdgeFlags.Fourth,
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), face);

        Assert.Equal(2, surface.Polylines.Count);
        Assert.All(surface.Calls.Where(c => c.StartsWith("DrawPolyline", StringComparison.Ordinal)), c => Assert.EndsWith("closed=False", c));
        Assert.Contains(surface.Polylines, p => p.SequenceEqual([new SurfacePoint(10, 90), new SurfacePoint(0, 90)]));
        Assert.Contains(surface.Polylines, p => p.SequenceEqual([new SurfacePoint(0, 100), new SurfacePoint(10, 100)]));
    }

    [Fact]
    public void Face3DWithOneHiddenEdgeIsOneOpenRunOfThreeEdges()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Face3D face = new()
        {
            FirstCorner = new XYZ(0, 0, 0),
            SecondCorner = new XYZ(10, 0, 0),
            ThirdCorner = new XYZ(10, 10, 0),
            FourthCorner = new XYZ(0, 10, 0),
            Flags = InvisibleEdgeFlags.Third,
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), face);

        IReadOnlyList<SurfacePoint> run = Assert.Single(surface.Polylines);
        // Starts after the hidden edge: 4 -> 1 -> 2 -> 3.
        Assert.Equal([new SurfacePoint(0, 90), new SurfacePoint(0, 100), new SurfacePoint(10, 100), new SurfacePoint(10, 90)], run);
    }

    [Fact]
    public void TriangularFace3DDropsTheDegenerateEdge()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Face3D face = new()
        {
            FirstCorner = new XYZ(0, 0, 0),
            SecondCorner = new XYZ(10, 0, 0),
            ThirdCorner = new XYZ(10, 10, 0),
            FourthCorner = new XYZ(10, 10, 0),
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), face);

        Assert.Equal(["DrawPolyline n=3 closed=True"], surface.Calls.Where(c => c.StartsWith("Draw", StringComparison.Ordinal)));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~Face3D"`
Expected: FAIL (no `DrawPolyline` call; a `NotImplemented` notification is raised instead).

- [ ] **Step 3: Implement**

Add `case Face3D face: DrawFace3D(context, style, face); break;` immediately after the `case Solid solid:` arm, and this helper after `DrawSolid`:

```csharp
    /// <summary>
    /// A 3DFACE is stroked edge by edge in plan view: edge n joins corner n to corner n+1 and edge 4 closes the ring;
    /// a triangle repeats its third corner, which makes edge 3 degenerate. Hidden edges (the invisible-edge flags)
    /// split the ring into open runs. Corners are world coordinates, so there is no OCS step.
    /// </summary>
    private static void DrawFace3D(ImageRenderContext context, ImageStyle style, Face3D face)
    {
        bool triangle = face.FourthCorner.Equals(face.ThirdCorner);
        XYZ[] corners = triangle
            ? [face.FirstCorner, face.SecondCorner, face.ThirdCorner]
            : [face.FirstCorner, face.SecondCorner, face.ThirdCorner, face.FourthCorner];
        bool[] hidden = triangle
            ? [face.Flags.HasFlag(InvisibleEdgeFlags.First), face.Flags.HasFlag(InvisibleEdgeFlags.Second), face.Flags.HasFlag(InvisibleEdgeFlags.Fourth)]
            : [face.Flags.HasFlag(InvisibleEdgeFlags.First), face.Flags.HasFlag(InvisibleEdgeFlags.Second), face.Flags.HasFlag(InvisibleEdgeFlags.Third), face.Flags.HasFlag(InvisibleEdgeFlags.Fourth)];

        int count = corners.Length;
        int firstHidden = Array.IndexOf(hidden, true);
        if (firstHidden < 0)
        {
            context.Surface.DrawPolyline(style, corners.Select(context.ToSurfacePoint).ToArray(), true);
            return;
        }

        // Start just after a hidden edge so no visible run wraps around the ring.
        List<SurfacePoint> run = new(count + 1);
        for (int step = 1; step <= count; step++)
        {
            int edge = (firstHidden + step) % count;
            if (hidden[edge])
            {
                Flush();
                continue;
            }

            if (run.Count == 0)
            {
                run.Add(context.ToSurfacePoint(corners[edge]));
            }

            run.Add(context.ToSurfacePoint(corners[(edge + 1) % count]));
        }

        Flush();

        void Flush()
        {
            if (run.Count >= 2)
            {
                context.Surface.DrawPolyline(style, run.ToArray(), false);
            }

            run.Clear();
        }
    }
```

Also extend `HasFiniteGeometry` with `Face3D face => IsFinite(face.FirstCorner) && IsFinite(face.SecondCorner) && IsFinite(face.ThirdCorner) && IsFinite(face.FourthCorner),` before the `_ => true` arm.

- [ ] **Step 4: Run the tests and the suite**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~Face3D"` → PASS.
Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror` → green, `git status --short ACadSharp.Image.Tests/Baselines` empty.

- [ ] **Step 5: Docs**

Spec 4.6, add bullet: "3DFACE (`Face3D`): visible edges stroked as polylines (one closed polygon when no edge is hidden; a triangle repeats its third corner and drops the degenerate edge). Never filled. Corners are WCS." README "Supported entities": add "3D faces (edges, honouring invisible-edge flags)".

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Draw 3DFACE edges on both backends"
```

---

### Task 3: Block attributes, ATTDEF suppression and an explode-count check

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (`DrawBlockContents`, new `DrawAttributes`, `IsAttributeVisible`)
- Modify: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`
- Modify: `README.md`, spec 4.6

**Interfaces:**
- Consumes: `Insert.Attributes` (`SeqendCollection<AttributeEntity>`), `AttributeEntity : AttributeBase : TextEntity` with `AttributeFlags Flags` (`Hidden=1, Constant=2`), `AttributeDefinition : AttributeBase`, `CadHeader.AttributeVisibility` (`AttributeVisibilityMode.None/Normal/All`), `ImageConfiguration.LayerVisibility`.
- Produces: `private void DrawAttributes(ImageRenderContext context, Insert insert, Layer? layer, ResolvedStyle parent)`, `private bool IsAttributeVisible(AttributeEntity attribute, Insert insert)`.

Background: ATTRIB points are absolute coordinates in the attribute's own OCS (DXF: `AcDbText` then `AcDbAttribute`), so the TEXT pipeline renders them with `placement: null`. `Insert.Explode()` also yields the block's ATTDEFs, whose default value is currently drawn for every insert; AutoCAD shows only `Constant` ATTDEFs. The clone/original pairing relies on `Explode()` yielding one clone per block entity; a count mismatch after a package upgrade must become a warning, not silent misplacement. Rule for ATTMODE: like entity invisibility, it is ignored under `LayerVisibilityMode.All`; under `Screen`/`Plot`, `None` hides every attribute, `Normal` hides attributes flagged `Hidden`, `All` shows them all. Multi-line attributes are drawn through the TEXT path from their `Value` (recorded limitation).

- [ ] **Step 1: Write the failing tests**

Append to `EntityRenderDispatcherTests`:

```csharp
    private static Insert InsertWithAttribute(string value, AttributeFlags flags, out BlockRecord block)
    {
        block = new BlockRecord("TAGGED");
        block.Entities.Add(new AttributeDefinition { Tag = "ROOM", Value = "DEFAULT", InsertPoint = new XYZ(1, 1, 0), Height = 2, Flags = flags });
        // Insert(BlockRecord) creates one AttributeEntity per ATTDEF at the identity transform; place it explicitly.
        Insert insert = new(block) { InsertPoint = new XYZ(10, 0, 0) };
        AttributeEntity attribute = Assert.Single(insert.Attributes);
        attribute.Value = value;
        attribute.InsertPoint = new XYZ(15, 5, 0);
        attribute.Flags = flags;
        return insert;
    }

    [Fact]
    public void InsertDrawsItsAttributesAndNotTheDefinitionDefaults()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Insert insert = WithHandle(InsertWithAttribute("A-101", AttributeFlags.None, out _), 0xAB);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        SurfaceText text = Assert.Single(surface.Texts);
        Assert.Equal("A-101", text.Text);
        Assert.Equal(new SurfacePoint(15, 95), text.Origin);
        Assert.DoesNotContain(surface.Texts, t => t.Text == "DEFAULT");
        EntityRenderInfo info = surface.Entities.Single(e => e.EntityType == insert.Attributes.First().ObjectName);
        Assert.Equal(0xABUL, info.ParentHandle);
        Assert.Equal("TAGGED", info.BlockName);
    }

    [Fact]
    public void ConstantAttributeDefinitionsAreStillDrawn()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        BlockRecord block = new("CONST");
        block.Entities.Add(new AttributeDefinition { Tag = "MAKER", Value = "ACME", InsertPoint = new XYZ(1, 1, 0), Height = 2, Flags = AttributeFlags.Constant });
        Insert insert = new(block) { InsertPoint = new XYZ(10, 0, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Contains(surface.Texts, t => t.Text == "ACME");
    }

    [Theory]
    [InlineData(LayerVisibilityMode.All, AttributeVisibilityMode.Normal, true)]
    [InlineData(LayerVisibilityMode.Screen, AttributeVisibilityMode.Normal, false)]
    [InlineData(LayerVisibilityMode.Screen, AttributeVisibilityMode.All, true)]
    [InlineData(LayerVisibilityMode.Screen, AttributeVisibilityMode.None, false)]
    public void HiddenAttributesFollowAttmodeUnlessEverythingIsShown(LayerVisibilityMode layerMode, AttributeVisibilityMode attmode, bool drawn)
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { LayerVisibility = layerMode };
        Insert insert = InsertWithAttribute("SECRET", AttributeFlags.Hidden, out BlockRecord block);
        CadDocument document = new();
        document.Header.AttributeVisibility = attmode;
        document.BlockRecords.Add(block);
        document.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Equal(drawn, surface.Texts.Any(t => t.Text == "SECRET"));
    }

    [Fact]
    public void VisibleAttributeIsDrawnUnderNormalAttmode()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { LayerVisibility = LayerVisibilityMode.Screen };
        Insert insert = InsertWithAttribute("SHOWN", AttributeFlags.None, out BlockRecord block);
        CadDocument document = new();
        document.BlockRecords.Add(block);
        document.Entities.Add(insert);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Contains(surface.Texts, t => t.Text == "SHOWN");
    }
```

If `document.BlockRecords.Add(block)` throws because `document.Entities.Add(insert)` registers the block itself, drop that line and note it in the report.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~Attribute"`
Expected: the first test FAILS because "DEFAULT" is drawn and "A-101" is not; the theory FAILS for the `true` rows; `ConstantAttributeDefinitionsAreStillDrawn` may already pass.

- [ ] **Step 3: Implement**

Replace `DrawBlockContents` with:

```csharp
    private void DrawBlockContents(ImageRenderContext context, Insert insert, Layer? layer, ResolvedStyle parent)
    {
        // The exploded clones carry the block entities' own attributes but no owner or document; ByBlock and
        // layer-0 inheritance, and the header's LTSCALE, come from the insert's resolved style and effective layer.
        // ACadSharp 3.7.1's Explode() yields one clone per block entity, in order. Text geometry comes from the
        // original entity placed through the insert's transform, because the clones' alignment points and MTEXT
        // X axes are never transformed and mirrored inserts hand back world points with a flipped normal.
        Transform transform = insert.GetTransform();
        IReadOnlyList<Entity> originals = insert.Block?.Entities.ToList() ?? (IReadOnlyList<Entity>)Array.Empty<Entity>();
        int index = 0;
        foreach (Entity entity in insert.Explode())
        {
            Entity? original = index < originals.Count ? originals[index] : null;
            index++;
            if (entity is AttributeDefinition definition && !definition.Flags.HasFlag(AttributeFlags.Constant))
            {
                // A non-constant ATTDEF is a template: the insert's ATTRIB carries the value that is actually shown.
                continue;
            }

            NormalizeExplodedClone(entity);
            bool placeText = original is TextEntity or MText && original.GetType() == entity.GetType();
            this.Draw(context, entity, layer, insert.Handle, insert.Block?.Name, parent, placeText ? original : null, placeText ? transform : null);
        }

        if (index != originals.Count)
        {
            this._configuration.Notify(
                $"[{insert.SubclassMarker}] Handle {insert.Handle.ToString("X", CultureInfo.InvariantCulture)}: block '{insert.Block?.Name}' exploded into {index} entities but holds {originals.Count}; text inside it may be misplaced.",
                NotificationType.Warning);
        }

        this.DrawAttributes(context, insert, layer, parent);
    }

    /// <summary>
    /// ATTRIB entities store absolute coordinates in their own OCS (the insert's transform is already applied by
    /// the writer), so they go through the TEXT pipeline with no placement. Multi-line attributes are drawn from
    /// their single-line value.
    /// </summary>
    private void DrawAttributes(ImageRenderContext context, Insert insert, Layer? layer, ResolvedStyle parent)
    {
        foreach (AttributeEntity attribute in insert.Attributes)
        {
            if (this.IsAttributeVisible(attribute, insert))
            {
                this.Draw(context, attribute, layer, insert.Handle, insert.Block?.Name, parent);
            }
        }
    }

    /// <summary>
    /// ATTMODE and the attribute's Hidden flag are drawing-visibility state, ignored under
    /// <see cref="LayerVisibilityMode.All"/> like entity invisibility; otherwise None hides every attribute,
    /// Normal hides the ones flagged Hidden and All shows them all.
    /// </summary>
    private bool IsAttributeVisible(AttributeEntity attribute, Insert insert)
    {
        if (this._configuration.LayerVisibility == LayerVisibilityMode.All)
        {
            return true;
        }

        AttributeVisibilityMode mode = insert.Document?.Header.AttributeVisibility ?? AttributeVisibilityMode.Normal;
        return mode switch
        {
            AttributeVisibilityMode.None => false,
            AttributeVisibilityMode.All => true,
            _ => !attribute.Flags.HasFlag(AttributeFlags.Hidden),
        };
    }
```

Add `using ACadSharp.Header;` for `AttributeVisibilityMode`.

- [ ] **Step 4: Run the tests and the suite**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~Attribute"` → PASS.
Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror` → green; baselines unchanged (the synthetic `features` insert has no attributes and no ATTDEF).

- [ ] **Step 5: Docs**

Spec 4.6 bullets: "Block attributes: `Insert.Attributes` are drawn through the TEXT pipeline with no placement (ATTRIB coordinates are absolute); non-constant ATTDEFs yielded by `Explode()` are skipped, constant ones drawn. ATTMODE (`CadHeader.AttributeVisibility`) and the Hidden flag are ignored under `LayerVisibilityMode.All`, otherwise None hides all, Normal hides Hidden, All shows all. Multi-line attributes are drawn from their single-line value (limitation)." and "Explode pairing: when `Explode()` yields a different number of entities than the block holds, a Warning is raised." README "Supported entities": add "block attributes (ATTRIB; hidden ones follow ATTMODE under `Screen`/`Plot`)". README layer visibility paragraph (line ~234): append "Hidden block attributes and the drawing's ATTMODE are honoured in the same two modes."

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Draw block attributes and skip attribute definition templates"
```

---

### Task 4: Draw LEADER paths and arrowheads

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (switch, new `DrawLeader`, `CatmullRomToBezier`)
- Modify: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`
- Modify: `README.md`, spec 4.6

**Interfaces:**
- Consumes: `Leader` (`List<XYZ> Vertices` in WCS, `bool ArrowHeadEnabled`, `LeaderPathType PathType` (`StraightLineSegments=0, Spline=1`), `DimensionStyle Style` never null with `double ArrowSize` (default 0.18), `double ScaleFactor` (default 1), `BlockRecord? LeaderArrow`), `IDrawingSurface.DrawCubicBezier(style, controlPoints, closed)` where `controlPoints` is `1 + 3n` points (both surfaces implement it; the raster tessellates).
- Produces: `private void DrawLeader(ImageRenderContext context, ImageStyle style, Leader leader)`, `internal static SurfacePoint[] CatmullRomToBezier(IReadOnlyList<SurfacePoint> points)`.

Background: the hookline is already the last stored vertex, and the annotation is a separate entity drawn on its own, so a leader is just its path plus an optional arrowhead at the first vertex pointing away from the second. AutoCAD's default closed filled arrowhead is an isosceles triangle of length DIMASZ×DIMSCALE and base width one third of that. Splined leaders use their vertices as fit points; a uniform Catmull-Rom spline through them, converted to cubic Béziers, gives the SVG a real `<path>` and the raster a smooth tessellation without a warning.

- [ ] **Step 1: Write the failing tests**

Append to `EntityRenderDispatcherTests`:

```csharp
    [Fact]
    public void StraightLeaderIsOneOpenPolylineWithoutArrow()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Leader leader = new() { Vertices = { new XYZ(0, 0, 0), new XYZ(10, 10, 0), new XYZ(20, 10, 0) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.Equal(["DrawPolyline n=3 closed=False"], surface.Calls.Where(c => c.StartsWith("Draw", StringComparison.Ordinal) || c.StartsWith("Fill", StringComparison.Ordinal)));
        Assert.Equal([new SurfacePoint(0, 100), new SurfacePoint(10, 90), new SurfacePoint(20, 90)], surface.Polylines[0]);
    }

    [Fact]
    public void LeaderArrowheadIsAFilledTriangleAtTheFirstVertex()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        // A fresh style: DimensionStyle.Default may be shared, and tests run in parallel.
        Leader leader = new() { ArrowHeadEnabled = true, Vertices = { new XYZ(0, 0, 0), new XYZ(30, 0, 0) }, Style = new DimensionStyle("ARROW") { ArrowSize = 6, ScaleFactor = 2 } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        IReadOnlyList<SurfacePoint> arrow = Assert.Single(surface.Polygons);
        Assert.Equal(3, arrow.Count);
        Assert.Equal(new SurfacePoint(0, 100), arrow[0]);
        // Length 6 * 2 = 12 along +X (away from the second vertex), half-width 12 / 6 = 2.
        Assert.Contains(arrow, p => Math.Abs(p.X - 12) < 1e-9 && Math.Abs(p.Y - 98) < 1e-9);
        Assert.Contains(arrow, p => Math.Abs(p.X - 12) < 1e-9 && Math.Abs(p.Y - 102) < 1e-9);
    }

    [Fact]
    public void SplinedLeaderIsACubicBezierChainThroughItsVertices()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Leader leader = new() { PathType = LeaderPathType.Spline, Vertices = { new XYZ(0, 0, 0), new XYZ(10, 10, 0), new XYZ(20, 0, 0), new XYZ(30, 10, 0) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.Equal(["DrawCubicBezier n=10 closed=False"], surface.Calls.Where(c => c.StartsWith("Draw", StringComparison.Ordinal)));
    }

    [Fact]
    public void CatmullRomControlPointsInterpolateTheInputPoints()
    {
        SurfacePoint[] points = [new(0, 0), new(10, 10), new(20, 0)];

        SurfacePoint[] controls = EntityRenderDispatcher.CatmullRomToBezier(points);

        Assert.Equal(7, controls.Length);
        Assert.Equal(points[0], controls[0]);
        Assert.Equal(points[1], controls[3]);
        Assert.Equal(points[2], controls[6]);
        // Interior tangent at (10,10) is (P2 - P0) / 6 = (20, 0) / 6.
        Assert.Equal(new SurfacePoint(10 - 20d / 6d, 10), controls[2]);
        Assert.Equal(new SurfacePoint(10 + 20d / 6d, 10), controls[4]);
    }

    [Fact]
    public void LeaderWithCustomArrowBlockFallsBackToTheDefaultArrowWithANotification()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        Leader leader = new() { ArrowHeadEnabled = true, Vertices = { new XYZ(0, 0, 0), new XYZ(30, 0, 0) }, Style = new DimensionStyle("DOTTED") { LeaderArrow = new BlockRecord("_DOT") } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), leader);

        Assert.Single(surface.Polygons);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.NotImplemented && n.Message.Contains("_DOT", StringComparison.Ordinal));
    }
```

Check the property names on `NotificationEventArgs` (`Message`, `NotificationType`) against `ACadSharp.Image/NotificationEventArgs.cs` and adjust the assertion if they differ.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~Leader|FullyQualifiedName~CatmullRom"`
Expected: FAIL (compile error for `CatmullRomToBezier`, then no draw calls).

- [ ] **Step 3: Implement**

Add `case Leader leader: this.DrawLeader(context, style, leader); break;` after the `Dimension` arm, and:

```csharp
    /// <summary>
    /// A leader is its stored path (the hookline is already the last vertex; the annotation is a separate entity)
    /// plus, when enabled, AutoCAD's default closed filled arrowhead at the first vertex: an isosceles triangle
    /// DIMASZ x DIMSCALE long and a third of that wide. A splined leader runs a Catmull-Rom curve through its
    /// vertices. Custom arrowhead blocks fall back to the default triangle with a notification.
    /// </summary>
    private void DrawLeader(ImageRenderContext context, ImageStyle style, Leader leader)
    {
        if (leader.Vertices.Count < 2)
        {
            return;
        }

        SurfacePoint[] points = leader.Vertices.Select(context.ToSurfacePoint).ToArray();
        if (leader.PathType == LeaderPathType.Spline && points.Length > 2)
        {
            context.Surface.DrawCubicBezier(style, CatmullRomToBezier(points), false);
        }
        else
        {
            context.Surface.DrawPolyline(style, points, false);
        }

        if (!leader.ArrowHeadEnabled)
        {
            return;
        }

        if (leader.Style.LeaderArrow != null)
        {
            this._configuration.Notify($"[{leader.SubclassMarker}] Arrowhead block '{leader.Style.LeaderArrow.Name}' is not rendered; the default closed arrow is drawn instead.", NotificationType.NotImplemented);
        }

        double size = leader.Style.ArrowSize * (leader.Style.ScaleFactor > 0d ? leader.Style.ScaleFactor : 1d);
        XY tip = leader.Vertices[0].Convert<XY>();
        XY direction = tip - leader.Vertices[1].Convert<XY>();
        double length = direction.GetLength();
        if (size <= 0d || length <= 0d)
        {
            return;
        }

        direction /= length;
        XY baseCenter = tip - (direction * size);
        XY half = new XY(-direction.Y, direction.X) * (size / 6d);
        context.Surface.FillPolygon(style, [context.ToSurfacePoint(tip), context.ToSurfacePoint(baseCenter + half), context.ToSurfacePoint(baseCenter - half)]);
    }

    /// <summary>
    /// Control points (1 + 3n) of the cubic Bézier chain equivalent to a uniform Catmull-Rom spline through
    /// <paramref name="points"/>, with the end tangents clamped by repeating the end points.
    /// </summary>
    internal static SurfacePoint[] CatmullRomToBezier(IReadOnlyList<SurfacePoint> points)
    {
        int segments = points.Count - 1;
        SurfacePoint[] controls = new SurfacePoint[(segments * 3) + 1];
        controls[0] = points[0];
        for (int i = 0; i < segments; i++)
        {
            SurfacePoint previous = points[Math.Max(i - 1, 0)];
            SurfacePoint start = points[i];
            SurfacePoint end = points[i + 1];
            SurfacePoint next = points[Math.Min(i + 2, points.Count - 1)];
            controls[(3 * i) + 1] = new SurfacePoint(start.X + ((end.X - previous.X) / 6d), start.Y + ((end.Y - previous.Y) / 6d));
            controls[(3 * i) + 2] = new SurfacePoint(end.X - ((next.X - start.X) / 6d), end.Y - ((next.Y - start.Y) / 6d));
            controls[(3 * i) + 3] = end;
        }

        return controls;
    }
```

If `XY` lacks an operator used above (`-`, `*`, `/`, `GetLength`), use the equivalent CSMath method (`XY.Subtract`, `Multiply`, `Normalize`) and say so in the report. Extend `HasFiniteGeometry` with `Leader leader => leader.Vertices.All(IsFinite),`.

- [ ] **Step 4: Run the tests and the suite**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~Leader|FullyQualifiedName~CatmullRom"` → PASS.
Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror` → green; baselines unchanged.

- [ ] **Step 5: Docs**

Spec 4.6 bullet: "LEADER (`Leader`): polyline through `Vertices` (WCS; the hookline is the last vertex), or a Catmull-Rom cubic Bézier chain for `PathType.Spline`; default closed filled arrowhead (length `ArrowSize x ScaleFactor`, base width one third) at the first vertex when `ArrowHeadEnabled`; custom arrow blocks fall back to it with a NotImplemented notification; the associated annotation is never drawn by the leader." README: add "leaders (straight and splined, with the default arrowhead)".

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Draw leaders with their default arrowhead"
```

---

### Task 5: Draw MLINE elements and survive ACadSharp's destructive MLINE clone

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (switch, new `DrawMLine`, `DrawBlockContents` vertex preservation)
- Modify: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`
- Modify: `README.md`, spec 4.6

**Interfaces:**
- Consumes: `MLine` (`List<MLine.Vertex> Vertices`, `MLineFlags Flags` with `Closed=2, NoStartCaps=4, NoEndCaps=8`, `MLineJustification Justification` (`Top=0, Zero=1, Bottom=2`), `double ScaleFactor`, `MLineStyle Style` never null); `MLine.Vertex` (`XYZ Position`, `XYZ Miter`, `List<Vertex.Segment> Segments`); `Vertex.Segment.Parameters` (`List<double>`); `MLineStyle` (`IEnumerable<Element> Elements`, `Color FillColor`, `MLineStyleFlags Flags` with `FillOn=1, StartSquareCap=16, EndSquareCap=256`, `AddElement(Element)`); `MLineStyle.Element` (`double Offset`, `Color Color`, `LineType? LineType`). `LineTypeDashResolver.Resolve(LineType?, CadHeader?, double, ImageRenderContext, float)`, `ColorExtensions.ToImageColor(CadColor, ImageColor)`, `ResolvedStyle.Header`, `ResolvedStyle.LineTypeScale`. The private `Draw(...)` already has a `Transform? placement` parameter.
- Produces: `private void DrawMLine(ImageRenderContext context, ImageStyle style, ResolvedStyle resolved, MLine mline, Transform? placement)`.

Background: the offsets are baked into each vertex: element j's line passes through `Position + Parameters[0] * Miter` of `Segments[j]` (DXF group 41; ezdxf's renderer also uses only that first value and treats the stored geometry as final, so justification and scale must not be re-applied). The further group-41 values describe cuts made by MLEDIT; this plan ignores them (the elements stay continuous) and raises one Warning per entity when any are present. Ruling against the research note, which suggested honouring them: the DXF reference's wording is ambiguous about whether they are cumulative, the reference implementation ignores them, and a wrong gap is worse than a missing one. When a vertex has no parameters for an element, the offset is computed from the style (`Offset * ScaleFactor` plus the justification shift `-max(Offset)` for Top, `0` for Zero, `-min(Offset)` for Bottom, all times `ScaleFactor`) with one Warning. Vertices are WCS. In 3.7.1 `MLine.Clone()` empties the source's shared vertex list, so `Insert.Explode()` destroys every MLINE inside a block: `DrawBlockContents` snapshots the vertex lists first, hands the snapshot to the clone (drawn through the insert transform) and restores the originals afterwards.

- [ ] **Step 1: Write the failing tests**

Append to `EntityRenderDispatcherTests`:

```csharp
    private static MLineStyle TwoElementStyle(double outer, MLineStyleFlags flags = MLineStyleFlags.None)
    {
        MLineStyle style = new("PLAN") { Flags = flags, FillColor = new ACadSharp.Color(3) };
        style.AddElement(new MLineStyle.Element { Offset = outer, Color = new ACadSharp.Color(1) });
        style.AddElement(new MLineStyle.Element { Offset = -outer, Color = ACadSharp.Color.ByLayer });
        return style;
    }

    private static MLine.Vertex VertexAt(double x, double y, params double[][] parameters)
    {
        MLine.Vertex vertex = new() { Position = new XYZ(x, y, 0), Direction = new XYZ(1, 0, 0), Miter = new XYZ(0, 1, 0) };
        foreach (double[] segment in parameters)
        {
            MLine.Vertex.Segment element = new();
            element.Parameters.AddRange(segment);
            vertex.Segments.Add(element);
        }

        return vertex;
    }

    [Fact]
    public void MLineDrawsOnePolylinePerStyleElementAtTheStoredOffsets()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLine mline = new() { Style = TwoElementStyle(0.5), Vertices = { VertexAt(0, 10, [0.5, 0], [-0.5, 0]), VertexAt(20, 10, [0.5, 0], [-0.5, 0]) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.Equal(2, surface.Polylines.Count);
        Assert.Equal([new SurfacePoint(0, 89.5), new SurfacePoint(20, 89.5)], surface.Polylines[0]);
        Assert.Equal([new SurfacePoint(0, 90.5), new SurfacePoint(20, 90.5)], surface.Polylines[1]);
        Assert.All(surface.Calls.Where(c => c.StartsWith("DrawPolyline", StringComparison.Ordinal)), c => Assert.EndsWith("closed=False", c));
        // Element colour 1 (red) is used for the first element; ByLayer falls back to the entity's resolved colour.
        Assert.Equal(SixLabors.ImageSharp.Color.Red.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgba32>(), surface.Styles[0].StrokeColor.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgba32>());
    }

    [Fact]
    public void MLineWithoutVertexParametersFallsBackToStyleOffsetsAndJustification()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        MLine mline = new() { Style = TwoElementStyle(0.5), ScaleFactor = 2, Justification = MLineJustification.Top, Vertices = { VertexAt(0, 10), VertexAt(20, 10) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        // Top justification puts the +0.5 element on the vertex line; the other lies 2 * 1.0 below it.
        Assert.Equal([new SurfacePoint(0, 90), new SurfacePoint(20, 90)], surface.Polylines[0]);
        Assert.Equal([new SurfacePoint(0, 92), new SurfacePoint(20, 92)], surface.Polylines[1]);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning);
    }

    [Fact]
    public void ClosedMLineClosesEveryElementAndFillsBetweenTheOuterOnes()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLine mline = new()
        {
            Style = TwoElementStyle(1, MLineStyleFlags.FillOn),
            Flags = MLineFlags.Closed,
            Vertices = { VertexAt(0, 0, [1, 0], [-1, 0]), VertexAt(20, 0, [1, 0], [-1, 0]), VertexAt(20, 20, [1, 0], [-1, 0]) },
        };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.All(surface.Calls.Where(c => c.StartsWith("DrawPolyline", StringComparison.Ordinal)), c => Assert.EndsWith("closed=True", c));
        IReadOnlyList<SurfacePoint> fill = Assert.Single(surface.Polygons);
        Assert.Equal(6, fill.Count);
        Assert.Equal("FillPolygon n=6", surface.Calls.First(c => c.StartsWith("Fill", StringComparison.Ordinal) || c.StartsWith("DrawPolyline", StringComparison.Ordinal)));
    }

    [Fact]
    public void MLineCutParametersAreIgnoredWithAWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        MLine mline = new() { Style = TwoElementStyle(0.5), Vertices = { VertexAt(0, 10, [0.5, 0, 4, 6], [-0.5, 0]), VertexAt(20, 10, [0.5, 0], [-0.5, 0]) } };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), mline);

        Assert.Equal(2, surface.Polylines.Count);
        Assert.Single(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("cut", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MLineInsideABlockIsDrawnThroughTheInsertAndKeepsItsVertices()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        MLine mline = new() { Style = TwoElementStyle(0.5), Vertices = { VertexAt(0, 0, [0.5, 0], [-0.5, 0]), VertexAt(10, 0, [0.5, 0], [-0.5, 0]) } };
        BlockRecord block = new("WALL");
        block.Entities.Add(mline);
        Insert insert = new(block) { InsertPoint = new XYZ(5, 20, 0) };

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), insert);

        Assert.Equal(2, mline.Vertices.Count);
        Assert.Equal(2, surface.Polylines.Count);
        Assert.Equal([new SurfacePoint(5, 79.5), new SurfacePoint(15, 79.5)], surface.Polylines[0]);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~MLine"`
Expected: FAIL (no polylines; the block test also finds `mline.Vertices.Count == 0` after the explode).

- [ ] **Step 3: Implement**

Add `case MLine mline: this.DrawMLine(context, style, resolved, mline, placement); break;` before the `default:` arm, and:

```csharp
    /// <summary>
    /// The geometry stored in an MLINE's vertices is final: element j passes through
    /// <c>Position + Segments[j].Parameters[0] * Miter</c> at every vertex (DXF group 41), with justification and
    /// scale already applied by the writer. Vertices without parameters fall back to the style offsets with the
    /// justification shift, with a warning. Cuts made by MLEDIT (further group-41 values) are ignored with a
    /// warning; the elements stay continuous. Each element takes the style element's colour and linetype, falling
    /// back to the entity's own; a fill-on style fills the ring between the two outermost elements first. Square
    /// caps join the outermost elements at an open end unless the entity suppresses them; round and inner-arc
    /// caps and joints are not drawn.
    /// </summary>
    private void DrawMLine(ImageRenderContext context, ImageStyle style, ResolvedStyle resolved, MLine mline, Transform? placement)
    {
        IReadOnlyList<MLine.Vertex> vertices = mline.Vertices;
        MLineStyle.Element[] elements = mline.Style.Elements.ToArray();
        if (vertices.Count < 2 || elements.Length == 0)
        {
            return;
        }

        bool closed = mline.Flags.HasFlag(MLineFlags.Closed);
        double scale = mline.ScaleFactor == 0d ? 1d : mline.ScaleFactor;
        double maxOffset = elements.Max(e => e.Offset);
        double minOffset = elements.Min(e => e.Offset);
        double shift = mline.Justification switch
        {
            MLineJustification.Top => -maxOffset * scale,
            MLineJustification.Bottom => -minOffset * scale,
            _ => 0d,
        };

        bool fallback = false;
        bool cuts = false;
        SurfacePoint[][] lines = new SurfacePoint[elements.Length][];
        for (int j = 0; j < elements.Length; j++)
        {
            lines[j] = new SurfacePoint[vertices.Count];
            for (int i = 0; i < vertices.Count; i++)
            {
                MLine.Vertex vertex = vertices[i];
                double along;
                if (j < vertex.Segments.Count && vertex.Segments[j].Parameters.Count > 0)
                {
                    along = vertex.Segments[j].Parameters[0];
                    cuts |= vertex.Segments[j].Parameters.Count > 2;
                }
                else
                {
                    along = (elements[j].Offset * scale) + shift;
                    fallback = true;
                }

                XYZ world = vertex.Position + (vertex.Miter * along);
                lines[j][i] = context.ToSurfacePoint(placement == null ? world : placement.ApplyTransform(world));
            }
        }

        string handle = mline.Handle.ToString("X", CultureInfo.InvariantCulture);
        if (fallback)
        {
            this._configuration.Notify($"[{mline.SubclassMarker}] Handle {handle}: vertex parameters are missing; element offsets were computed from the style.", NotificationType.Warning);
        }

        if (cuts)
        {
            this._configuration.Notify($"[{mline.SubclassMarker}] Handle {handle}: cut segments are not rendered; elements are drawn continuous.", NotificationType.Warning);
        }

        ImageColor foreground = context.Configuration.ResolveForegroundColor();
        int outer = Array.FindIndex(elements, e => e.Offset == maxOffset);
        int inner = Array.FindIndex(elements, e => e.Offset == minOffset);
        if (mline.Style.Flags.HasFlag(MLineStyleFlags.FillOn) && outer != inner)
        {
            ImageStyle fill = style with { StrokeColor = ElementColor(mline.Style.FillColor), DashPattern = null };
            context.Surface.FillPolygon(fill, [.. lines[outer], .. lines[inner].Reverse()]);
        }

        for (int j = 0; j < elements.Length; j++)
        {
            float[]? dashes = elements[j].LineType == null
                ? style.DashPattern
                : LineTypeDashResolver.Resolve(elements[j].LineType, resolved.Header, resolved.LineTypeScale, context, style.StrokeWidth);
            ImageStyle elementStyle = style with { StrokeColor = ElementColor(elements[j].Color), DashPattern = dashes };
            context.Surface.DrawPolyline(elementStyle, lines[j], closed);
        }

        if (!closed && outer != inner)
        {
            if (mline.Style.Flags.HasFlag(MLineStyleFlags.StartSquareCap) && !mline.Flags.HasFlag(MLineFlags.NoStartCaps))
            {
                context.Surface.DrawLine(style, lines[outer][0], lines[inner][0]);
            }

            if (mline.Style.Flags.HasFlag(MLineStyleFlags.EndSquareCap) && !mline.Flags.HasFlag(MLineFlags.NoEndCaps))
            {
                context.Surface.DrawLine(style, lines[outer][^1], lines[inner][^1]);
            }
        }

        ImageColor ElementColor(ACadSharp.Color color) => color.IsByLayer || color.IsByBlock ? style.StrokeColor : color.ToImageColor(foreground);
    }
```

Then in `DrawBlockContents` (from Task 3), wrap the explode loop:

```csharp
        // ACadSharp 3.7.1's MLine.Clone() clears the vertex list it shares with its source, so Explode() would
        // empty every MLINE in the block for the rest of the document's life. The lists are captured first, lent to
        // the clone (drawn through the insert transform, since the empty list was what ApplyTransform saw) and
        // restored afterwards.
        Dictionary<MLine, List<MLine.Vertex>> mlineVertices = originals.OfType<MLine>().ToDictionary(m => m, m => new List<MLine.Vertex>(m.Vertices));
        int index = 0;
        try
        {
            foreach (Entity entity in insert.Explode())
            {
                Entity? original = index < originals.Count ? originals[index] : null;
                index++;
                if (entity is AttributeDefinition definition && !definition.Flags.HasFlag(AttributeFlags.Constant))
                {
                    continue;
                }

                NormalizeExplodedClone(entity);
                Transform? entityPlacement = null;
                Entity? source = null;
                if (original is TextEntity or MText && original.GetType() == entity.GetType())
                {
                    source = original;
                    entityPlacement = transform;
                }
                else if (entity is MLine clone && original is MLine sourceMLine && mlineVertices.TryGetValue(sourceMLine, out List<MLine.Vertex>? vertices))
                {
                    clone.Vertices = vertices;
                    entityPlacement = transform;
                }

                this.Draw(context, entity, layer, insert.Handle, insert.Block?.Name, parent, source, entityPlacement);
            }
        }
        finally
        {
            foreach (KeyValuePair<MLine, List<MLine.Vertex>> pair in mlineVertices)
            {
                pair.Key.Vertices = pair.Value;
            }
        }
```

The explode-count warning and the `this.DrawAttributes(...)` call from Task 3 stay in the method, after the `finally` block. Update the comment on the private `Draw` so `placement` reads: "placement is the transform of the insert that placed a block TEXT, MTEXT or MLINE; null outside a block reference." If `XYZ` lacks `+`/`*` operators, use `XYZ.Add`/`Multiply` equivalents and report it.

- [ ] **Step 4: Run the tests and the suite**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~MLine"` → PASS.
Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror` → green; baselines unchanged.

- [ ] **Step 5: Docs**

Spec 4.6 bullet: "MLINE (`MLine`): one polyline per style element through `Position + Parameters[0] x Miter` (stored geometry is final; justification and scale are not re-applied), style-element colour and linetype falling back to the entity's, fill between the outermost elements when the style has FillOn, square caps only; vertices without parameters fall back to style offsets with a Warning; MLEDIT cuts are ignored with a Warning. Inside blocks the vertex lists are captured before `Explode()` and restored after it because `MLine.Clone()` in 3.7.1 empties the source." README: add "multilines (element offsets, fill, square caps; cuts are not rendered)".

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Draw multilines and shield block MLINEs from the destructive clone"
```

---

### Task 6: Draw WIPEOUT as an opaque background polygon

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (switch, new `DrawWipeout`, `internal static XYZ WipeoutPixelToWorld(...)`)
- Modify: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`
- Modify: `README.md`, spec 4.6

**Interfaces:**
- Consumes: `Wipeout : CadWipeoutBase` (`List<XY> ClipBoundaryVertices`, `ClipType ClipType` (`Rectangular=1, Polygonal=2`), `bool ClippingState`, `ClipMode ClipMode` (`Outside=0, Inside=1`), `XYZ InsertPoint`, `XYZ UVector`, `XYZ VVector`, `XY Size`, `ImageDisplayFlags Flags` with `ShowImage=1`), `ImageConfiguration.BackgroundColor`.
- Produces: `private void DrawWipeout(ImageRenderContext context, ImageStyle style, Wipeout wipeout)`, `internal static XYZ WipeoutPixelToWorld(CadWipeoutBase image, XY pixel)`.

Background: the boundary is in pixel space with its origin at the top-left corner of the image and Y pointing down; U runs along the visual bottom, V along the visual left side, both one pixel long. The mapping (as implemented by ezdxf's `boundary_path_wcs`, which also writes wipeouts this way) is `world = InsertPoint + (p.X + 0.5) * U + (Size.Y - p.Y - 0.5) * V`. Ruling against the research note, which had no Y flip: the flip is what makes the documented default boundary `(-0.5,-0.5) .. (Size.X-0.5, Size.Y-0.5)` cover exactly the image extent with the top-left pixel at the top. On the raster backend a wipeout paints the background colour at full opacity over everything drawn before it (Task 1 makes that order the drawing's). In SVG every entity sits inside its layer's `<g>`, so a wipeout masks only content in its own layer group and in groups written earlier; layer grouping takes precedence over draw order by design (the same holds for hatches and solids) and is not to be changed. A transparent background cannot occlude, so the wipeout is skipped with a Warning; `ClipMode.Inside` (everything outside the boundary is masked) is skipped with a NotImplemented notification. The frame is never drawn (AutoCAD's WIPEOUTFRAME=0 plot behaviour; 3.7.1 exposes no header variable for it).

- [ ] **Step 1: Write the failing tests**

Append to `EntityRenderDispatcherTests`:

```csharp
    private static Wipeout UnitWipeout()
    {
        // Insert (10,10), one-pixel image whose pixel spans 5 x 5 drawing units.
        return new Wipeout
        {
            InsertPoint = new XYZ(10, 10, 0),
            UVector = new XYZ(5, 0, 0),
            VVector = new XYZ(0, 5, 0),
            Size = new XY(1, 1),
            Flags = ImageDisplayFlags.ShowImage | ImageDisplayFlags.UseClippingBoundary,
            ClippingState = true,
        };
    }

    [Fact]
    public void WipeoutPixelMappingFlipsYAndCentresPixels()
    {
        Wipeout wipeout = UnitWipeout();

        Assert.Equal(new XYZ(10, 15, 0), EntityRenderDispatcher.WipeoutPixelToWorld(wipeout, new XY(-0.5, -0.5)));
        Assert.Equal(new XYZ(15, 10, 0), EntityRenderDispatcher.WipeoutPixelToWorld(wipeout, new XY(0.5, 0.5)));
    }

    [Fact]
    public void RectangularWipeoutFillsTheBackgroundColourOpaquely()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { BackgroundColor = SixLabors.ImageSharp.Color.White };
        Wipeout wipeout = UnitWipeout();
        wipeout.ClipType = ClipType.Rectangular;
        wipeout.ClipBoundaryVertices.AddRange([new XY(-0.5, -0.5), new XY(0.5, 0.5)]);
        wipeout.Transparency = new Transparency(50);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), wipeout);

        IReadOnlyList<SurfacePoint> polygon = Assert.Single(surface.Polygons);
        Assert.Equal(4, polygon.Count);
        Assert.Equal(new HashSet<SurfacePoint> { new(10, 90), new(15, 90), new(15, 85), new(10, 85) }, polygon.ToHashSet());
        ImageStyle style = Assert.Single(surface.Styles);
        Assert.Equal(SixLabors.ImageSharp.Color.White.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgba32>(), style.StrokeColor.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgba32>());
        Assert.Equal(1f, style.Opacity);
        Assert.Empty(surface.Polylines);
    }

    [Fact]
    public void PolygonalWipeoutUsesItsVertices()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Wipeout wipeout = UnitWipeout();
        wipeout.ClipType = ClipType.Polygonal;
        wipeout.ClipBoundaryVertices.AddRange([new XY(-0.5, -0.5), new XY(0.5, -0.5), new XY(0, 0.5)]);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), wipeout);

        Assert.Equal([new SurfacePoint(10, 85), new SurfacePoint(15, 85), new SurfacePoint(12.5, 90)], Assert.Single(surface.Polygons));
    }

    [Fact]
    public void WipeoutWithoutClippingFillsTheWholeImageFrame()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Wipeout wipeout = UnitWipeout();
        wipeout.ClippingState = false;

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), wipeout);

        Assert.Equal(new HashSet<SurfacePoint> { new(10, 90), new(15, 90), new(15, 85), new(10, 85) }, Assert.Single(surface.Polygons).ToHashSet());
    }

    [Fact]
    public void WipeoutOnTransparentBackgroundIsSkippedWithAWarning()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { BackgroundColor = SixLabors.ImageSharp.Color.Transparent };
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);

        new EntityRenderDispatcher(configuration).Draw(CreateContext(surface, configuration), UnitWipeout());

        Assert.Empty(surface.Polygons);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning);
    }

    [Fact]
    public void InvertedAndHiddenWipeoutsDrawNothing()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        Wipeout inverted = UnitWipeout();
        inverted.ClipMode = ClipMode.Inside;
        Wipeout hidden = UnitWipeout();
        hidden.Flags = ImageDisplayFlags.None;
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(CreateContext(surface, configuration), inverted);
        dispatcher.Draw(CreateContext(surface, configuration), hidden);

        Assert.Empty(surface.Polygons);
        Assert.Single(notifications, n => n.NotificationType == NotificationType.NotImplemented);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~Wipeout"`
Expected: FAIL (compile error for `WipeoutPixelToWorld`, then no polygons).

- [ ] **Step 3: Implement**

Add `case Wipeout wipeout: this.DrawWipeout(context, style, wipeout); break;` before the `default:` arm, and:

```csharp
    /// <summary>
    /// A wipeout masks whatever was drawn before it: its clip boundary (or the whole image frame when clipping is
    /// off) is filled with the page background at full opacity, so the page must be drawn in the drawing's order.
    /// The frame is never stroked. An inverted clip (everything outside the boundary masked) and a transparent
    /// background cannot be honoured and are skipped with a notification.
    /// </summary>
    private void DrawWipeout(ImageRenderContext context, ImageStyle style, Wipeout wipeout)
    {
        if (!wipeout.Flags.HasFlag(ImageDisplayFlags.ShowImage))
        {
            return;
        }

        string handle = wipeout.Handle.ToString("X", CultureInfo.InvariantCulture);
        if (wipeout.ClipMode == ClipMode.Inside)
        {
            this._configuration.Notify($"[{wipeout.SubclassMarker}] Handle {handle}: inverted clip boundaries are not rendered.", NotificationType.NotImplemented);
            return;
        }

        ImageColor background = this._configuration.BackgroundColor;
        if (background.ToPixel<Rgba32>().A == 0)
        {
            this._configuration.Notify($"[{wipeout.SubclassMarker}] Handle {handle}: a wipeout cannot mask on a transparent background; skipped.", NotificationType.Warning);
            return;
        }

        List<XY> pixels;
        if (wipeout.ClippingState && wipeout.ClipBoundaryVertices.Count >= 2)
        {
            if (wipeout.ClipType == ClipType.Rectangular || wipeout.ClipBoundaryVertices.Count == 2)
            {
                XY a = wipeout.ClipBoundaryVertices[0];
                XY b = wipeout.ClipBoundaryVertices[1];
                pixels = [a, new XY(b.X, a.Y), b, new XY(a.X, b.Y)];
            }
            else
            {
                pixels = wipeout.ClipBoundaryVertices.ToList();
            }
        }
        else
        {
            pixels = [new XY(-0.5, -0.5), new XY(wipeout.Size.X - 0.5, -0.5), new XY(wipeout.Size.X - 0.5, wipeout.Size.Y - 0.5), new XY(-0.5, wipeout.Size.Y - 0.5)];
        }

        SurfacePoint[] points = pixels.Select(p => context.ToSurfacePoint(WipeoutPixelToWorld(wipeout, p))).ToArray();
        context.Surface.FillPolygon(style with { StrokeColor = background, Opacity = 1f, DashPattern = null }, points);
    }

    /// <summary>
    /// Maps an image-space boundary vertex to world coordinates. Pixel (0,0) is the top-left pixel and Y grows
    /// downwards; <c>UVector</c> runs along the visual bottom and <c>VVector</c> up the visual left side, each one
    /// pixel long. The documented default boundary (-0.5,-0.5)..(Size-0.5) therefore covers exactly the image.
    /// </summary>
    internal static XYZ WipeoutPixelToWorld(CadWipeoutBase image, XY pixel)
        => image.InsertPoint + (image.UVector * (pixel.X + 0.5)) + (image.VVector * (image.Size.Y - pixel.Y - 0.5));
```

Add `using SixLabors.ImageSharp.PixelFormats;` if `Rgba32` is not already in scope.

- [ ] **Step 4: Run the tests and the suite**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~Wipeout"` → PASS.
Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror` → green; baselines unchanged.

- [ ] **Step 5: Docs**

Spec 4.6 bullet: "WIPEOUT (`Wipeout`): the clip boundary (rectangular pair expanded to four corners; polygonal as listed; the full frame when `ClippingState` is off) is mapped from pixel space with `InsertPoint + (x+0.5)U + (Size.Y-y-0.5)V` and filled with `BackgroundColor` at opacity 1; no frame; `ClipMode.Inside` is NotImplemented; a transparent background skips the wipeout with a Warning. In SVG the mask covers only its own layer group and earlier groups, because layer grouping takes precedence over draw order." README: add "wipeouts (masked with the background colour; needs an opaque `BackgroundColor`; in SVG the mask stays within layer-group order)".

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md
git commit -m "Mask wipeouts with the background colour"
```

---

### Task 7: Size raster text in ems at 72 dpi and fix the line-spacing compensation

**Files:**
- Create: `ACadSharp.Image/Rendering/TextMetrics.cs`
- Modify: `ACadSharp.Image/Rendering/RasterDrawingSurface.cs:176-232, 279-282`, `ACadSharp.Image/Rendering/Svg/SvgTextLayout.cs:17-20`
- Modify: `ACadSharp.Image.Tests/RasterDrawingSurfaceTests.cs`
- Modify: `README.md`, spec 5.3, `docs/research/remaining-rendering-limitations.md`

**Interfaces:**
- Consumes: `FontResolver.Create(string?, float size)`, `RasterDrawingSurfaceTests.DrawnText(value, baseline, lineSpacingFactor, rotation)` and `InkBandStarts(image)` helpers.
- Produces: `internal static class TextMetrics` in `ACadSharp.Image/Rendering/TextMetrics.cs` with `public const double CapHeightToEm = 4d / 3d;` and `public static double EmSize(double capHeight) => capHeight * CapHeightToEm;`. `SvgTextLayout.CapHeightToEm` and `SvgTextLayout.EmSize` become forwarders to it (`public const double CapHeightToEm = TextMetrics.CapHeightToEm;`, `public static double EmSize(double capHeight) => TextMetrics.EmSize(capHeight);`) so the SVG code and its tests do not change. The raster must not reference the `Svg` namespace.

Background: SixLabors draws glyphs at `Font.Size x Dpi / 72` pixels, so passing the CAD height with `Dpi = configuration.Dpi` makes text grow with `Dpi` while geometry does not. `(size = h, Dpi = 96)` and `(size = 4h/3, Dpi = 72)` render identically, so the fix is `Dpi = 72f` with the size in ems, shared with the SVG backend through the new `TextMetrics.EmSize`. Separately, SixLabors splits the extra leading `em x (LineSpacing - 1)` half above and half below each line; the current compensation `factor x em / 8` equals that only when `factor = 1`. The correct value is `em x (LineSpacing - 1) / 2`.

- [ ] **Step 1: Write the failing tests**

Append to `RasterDrawingSurfaceTests`:

```csharp
    [Fact]
    public void TextSizeDoesNotDependOnTheConfiguredDpi()
    {
        int[] at96 = InkColumnBounds(DrawnText("Hg", SurfaceTextBaseline.Alphabetic, 1d, 0d, dpi: 96f));
        int[] at300 = InkColumnBounds(DrawnText("Hg", SurfaceTextBaseline.Alphabetic, 1d, 0d, dpi: 300f));

        Assert.True(Math.Abs(at96[0] - at300[0]) <= 1 && Math.Abs(at96[1] - at300[1]) <= 1, $"ink columns {at96[0]}..{at96[1]} at 96 dpi but {at300[0]}..{at300[1]} at 300 dpi.");
    }

    [Fact]
    public void HangingTextStaysOnItsAnchorForAnyLineSpacingFactor()
    {
        using Image<Rgba32> single = DrawnText("H", SurfaceTextBaseline.Hanging, 1d, 0d);
        using Image<Rgba32> spaced = DrawnText("H\nH", SurfaceTextBaseline.Hanging, 2d, 0d);

        int[] one = InkBandStarts(single);
        int[] two = InkBandStarts(spaced);

        Assert.Equal(Assert.Single(one), two[0]);
        double distance = two[1] - two[0];
        Assert.True(Math.Abs(distance - 100d / 3d) <= 1d, $"expected the lines about {100d / 3d:F1} px apart (2 x 5/3 of the text height), got {distance}.");
    }

    /// <summary>First and last canvas column holding a pixel darker than mid grey.</summary>
    private static int[] InkColumnBounds(Image<Rgba32> canvas)
    {
        int first = -1;
        int last = -1;
        for (int x = 0; x < canvas.Width; x++)
        {
            bool inked = false;
            for (int y = 0; y < canvas.Height && !inked; y++)
            {
                inked = canvas[x, y].R < 128;
            }

            if (inked)
            {
                if (first < 0)
                {
                    first = x;
                }

                last = x;
            }
        }

        return [first, last];
    }
```

Change the existing `DrawnText` helper to take `float dpi = 96f` as a fifth optional parameter and pass `new ImageConfiguration { Dpi = dpi }` to the surface.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~RasterDrawingSurfaceTests"`
Expected: `TextSizeDoesNotDependOnTheConfiguredDpi` FAILS (about three times wider at 300 dpi); `HangingTextStaysOnItsAnchorForAnyLineSpacingFactor` FAILS (first band moves by about 6 px). Record both RED values in the report.

- [ ] **Step 3: Implement**

In `RasterDrawingSurface.DrawText` replace the font creation, comment, `halfLeading` and `TextOptions.Dpi`:

```csharp
        // The font size is the em, 4/3 of the CAD text height, laid out at 72 dpi so one point is one pixel: text
        // then scales with the page like the geometry does and not with ImageConfiguration.Dpi, which only sizes
        // line weights. The SVG backend uses the same em through TextMetrics.EmSize.
        Font font = this.CreateFont(TextMetrics.EmSize(text.Height));

        // ImageSharp advances the baseline by one em per line; AutoCAD and the SVG backend space lines at 5/3 of
        // the text height, that is 5/4 em, so the spacing factor carries the 5/4. ImageSharp then splits the extra
        // (LineSpacing - 1) em of leading evenly above and below every line, which would displace even a single
        // line, so the origin is pulled back by that half-leading on whichever end the alignment anchors: up for
        // Hanging, which anchors the top, down for Alphabetic, which anchors the bottom, and not at all for
        // Central. The offset rides on the layout origin, so the rotation below turns it with the glyphs.
        double factor = text.LineSpacingFactor <= 0d ? 1d : text.LineSpacingFactor;
        float lineSpacing = (float)factor * 5f / 4f;
        double halfLeading = font.Size * (lineSpacing - 1d) / 2d;
```

Set `Dpi = 72f,` and `LineSpacing = lineSpacing,` in the `TextOptions` initializer. Rename `CreateFont(double height)` to `CreateFont(double emSize)` with a `<summary>`: "Font at the given em size in points; at 72 dpi one point is one pixel."

- [ ] **Step 4: Run the tests and the suite; handle baselines**

Run: `dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~RasterDrawingSurfaceTests"` → PASS (all, including the existing spacing and anchor tests).
Run: `dotnet test ACadSharp.Image.sln --configuration Release -warnaserror`.

The change is arithmetically a no-op at 96 dpi but not bit-exact (one-ULP float differences). If a PNG baseline fails: with Pillow (or an xUnit scratch assertion) count the differing pixels and the maximum channel delta between the baseline and the new render; the diff must be confined to text pixels, at most 0.5% of the canvas and small in magnitude. Then regenerate exactly the failing PNGs with the scoped commands (`ACADSHARP_IMAGE_UPDATE_BASELINES=1 dotnet test ACadSharp.Image.Tests --filter "FullyQualifiedName~FeatureGoldenTests.FeaturePngMatchesBaseline"`, `...~SampleParityTests.SampleRendersMatchBaselines`, `...~ViewportParityTests`), list them with pixel counts in the commit body, and confirm no `.svg` golden changed (`git status --short ACadSharp.Image.Tests/Baselines/*.svg` empty). If a non-text pixel changes, stop and report.

- [ ] **Step 5: Docs**

Spec 5.3 "Text" bullet: append "**Amended 2026-09-03 (remaining fixes):** the raster backend lays text out at a fixed 72 dpi with the em size (`TextMetrics.EmSize`, shared with the SVG backend), so text no longer scales with `ImageConfiguration.Dpi`; the single-line compensation is `em x (LineSpacing - 1) / 2`, exact for every line-spacing factor." README line ~250: replace "a non-default `Dpi` scales raster text but not SVG text" (or the equivalent sentence) with "`ImageConfiguration.Dpi` affects only line weights; text is sized from the drawing on both backends." In `docs/research/remaining-rendering-limitations.md`, add under each topic heading (1.1 to 1.4, 2, 3, 4.1, 4.2 and the incidental ATTDEF finding) one line `**Status (2026-09-03):** implemented in plan 08 (docs/superpowers/plans/2026-09-03-08-remaining-entities.md)`, with the deviations noted for 1.2 (cuts ignored, like ezdxf) and 1.3 (Y-flipped mapping).

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image/Rendering/TextMetrics.cs ACadSharp.Image/Rendering/RasterDrawingSurface.cs ACadSharp.Image/Rendering/Svg/SvgTextLayout.cs ACadSharp.Image.Tests/RasterDrawingSurfaceTests.cs README.md docs/superpowers/specs/2026-09-02-layers-and-svg-design.md docs/research/remaining-rendering-limitations.md ACadSharp.Image.Tests/Baselines
git commit -m "Size raster text in ems at 72 dpi and fix the leading compensation"
```

---

## Self-review

- Coverage against the research note: 1.1 → Task 2; 1.2 → Task 5 (cuts ignored, documented deviation); 1.3 → Task 6 (Y-flipped mapping, documented deviation) and Task 1 (draw order); 1.4 → Task 4; 2 → Task 3; 3 → Task 3 (count warning; the `Block.Entities` refactor is not taken because `Explode()`'s Circle→Ellipse conversion under non-uniform scale is needed, verified by probe); 4.1 and 4.2 → Task 7; incidental ATTDEF → Task 3; incidental MLINE clone → Task 5.
- Type consistency: `Draw(..., Entity? textSource, Transform? placement)` keeps its signature; Task 5 reuses `placement`. `CatmullRomToBezier` and `WipeoutPixelToWorld` are `internal static` so the tests reach them through the existing `InternalsVisibleTo`.
- Baselines: Tasks 2 to 6 cannot move any; Task 1 and Task 7 have explicit measure-then-regenerate steps.
