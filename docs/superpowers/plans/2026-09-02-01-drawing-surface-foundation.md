# Drawing Surface Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put a backend-neutral drawing-surface interface between entity decomposition and ImageSharp, with pixel-identical raster output, an abstract rendered-page API, ACadSharp 3.7.1, and net6.0 dropped.

**Architecture:** `EntityRenderDispatcher`, `SplineRenderer` and `TextRenderer` stop calling ImageSharp and instead call `IDrawingSurface` primitives through a backend-neutral `ImageRenderContext`. `RasterDrawingSurface` reproduces today's ImageSharp calls exactly (same float rounding sequence) so committed baseline PNGs stay byte-identical. `Render()` returns abstract `RenderedPage` objects that know how to save themselves.

**Tech Stack:** .NET 8/10, C# latest, ACadSharp 3.7.1, SixLabors.ImageSharp 3.1.12, SixLabors.ImageSharp.Drawing 2.1.7, SixLabors.Fonts 2.1.3, xUnit 2.9.

**Spec:** `docs/superpowers/specs/2026-09-02-layers-and-svg-design.md` (sections 3, 6, 9). Read the interface appendix (section 9) before starting; every name below comes from it.

## Global Constraints

- ACadSharp `3.7.1`; library targets `net8.0;net10.0`; CLI and tests `net10.0`; no new NuGet packages.
- Raster output after this plan must be byte-identical to the baselines committed in Task 1 for the default configuration.
- Repo style: `this.` on members, explicit types except lambdas, XML docs on public members, `sealed` classes, file-scoped namespaces, rendering internals are `internal` (tests see them through `InternalsVisibleTo`).
- Run all commands from `/work/workspaces/orca/ACadSharp.Image/svg-support`. Never `cd` elsewhere.
- Commit after every task. Commit message style in this repo is a short imperative line (`Add Insert entity support`). Every commit ends with:

```
Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz
```

- Do not commit `.codegraph/`. Commit `docs/` (research note, spec, plans) with Task 1.
- Test command: `dotnet test ACadSharp.Image.sln -c Release --nologo -v q`. Filter one test with `--filter "FullyQualifiedName~<Name>"`.

## File Structure

| File | Responsibility | Task |
| --- | --- | --- |
| `ACadSharp.Image.Tests/SampleParityTests.cs` (create) | Renders `Samples/` with a fixed configuration and compares against committed PNG baselines byte-for-byte | 1 |
| `ACadSharp.Image.Tests/Baselines/*.png` (create) | Baselines generated **before** the refactor | 1 |
| `ACadSharp.Image.Tests/ACadSharp.Image.Tests.csproj` (modify) | Copy baselines to output | 1 |
| `Directory.Packages.props`, `ACadSharp.Image/ACadSharp.Image.csproj`, `.github/workflows/ci.yml`, `.github/workflows/release.yml` (modify) | ACadSharp 3.7.1, drop net6.0, package description | 2 |
| `ACadSharp.Image/Rendering/SurfacePoint.cs` (create) | `SurfacePoint`, `SurfaceRect` | 3 |
| `ACadSharp.Image/Rendering/ImageStyle.cs` (modify) | Full style record with dash pattern and opacity | 3 |
| `ACadSharp.Image/Rendering/SurfaceText.cs` (create) | `SurfaceText`, `SurfaceTextAnchor`, `SurfaceTextBaseline` | 3 |
| `ACadSharp.Image/Rendering/EntityRenderInfo.cs` (create) | `EntityRenderInfo`, `LayerRenderInfo` | 3 |
| `ACadSharp.Image/Rendering/IDrawingSurface.cs` (create) | The interface plus `ViewportSurface` | 3 |
| `ACadSharp.Image/Rendering/CurveTessellation.cs` (create) | Arc and bulge tessellation helpers shared by backends | 3 |
| `ACadSharp.Image/Rendering/RasterDrawingSurface.cs` (create) | ImageSharp implementation | 3 |
| `ACadSharp.Image.Tests/RasterDrawingSurfaceTests.cs` (create) | Pixel tests for the raster surface, tessellation tests | 3 |
| `ACadSharp.Image/Rendering/ImageRenderContext.cs` (modify) | Backend-neutral transform; single-precision mode for raster parity | 4 |
| `ACadSharp.Image/Rendering/ImageStyleResolver.cs` (modify) | Takes the context to resolve stroke width | 4 |
| `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs`, `SplineRenderer.cs`, `TextRenderer.cs`, `ImagePageRenderer.cs` (modify) | Draw through the surface | 4 |
| `ACadSharp.Image.Tests/ImageExporterTests.cs` (modify) | Adapt the two tests that build a context by hand | 4 |
| `ACadSharp.Image/RenderedPage.cs` (create), `RenderedImagePage.cs` (modify), `ImageExporter.cs` (modify) | Abstract rendered page, `Render(format)`, `Save` via page | 5 |
| `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` (modify), `ACadSharp.Image.Tests/RecordingDrawingSurface.cs` (create), `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs` (create) | `BeginEntity`/`EndEntity` with effective layer | 6 |

---

### Task 1: Parity baselines from the pre-refactor renderer

**Files:**
- Create: `ACadSharp.Image.Tests/SampleParityTests.cs`
- Create: `ACadSharp.Image.Tests/Baselines/` (generated PNGs)
- Modify: `ACadSharp.Image.Tests/ACadSharp.Image.Tests.csproj`

**Interfaces:**
- Consumes: today's `ImageExporter.Render()` returning `IReadOnlyList<RenderedImagePage>` with `Canvas`.
- Produces: `SampleParityTests.RenderSample(string fileName, bool paperLayouts)` helper and the baseline files later tasks must keep green.

- [ ] **Step 1: Check the pinned font exists**

Run: `fc-list : family | grep -i "DejaVu Sans"`
Expected: at least one line containing `DejaVu Sans`. If absent, install `dejavu-sans-fonts` (Fedora) before continuing; the baselines depend on it.

- [ ] **Step 2: Write the parity test**

Create `ACadSharp.Image.Tests/SampleParityTests.cs`:

```csharp
using ACadSharp.IO;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ACadSharp.Image.Tests;

/// <summary>
/// Renders the files in <c>Samples/</c> with a fixed configuration and compares the result
/// byte-for-byte with the PNGs in <c>Baselines/</c>. Set the environment variable
/// <c>ACADSHARP_IMAGE_UPDATE_BASELINES=1</c> to rewrite the baselines instead of comparing.
/// </summary>
public sealed class SampleParityTests
{
    private const string FontFamily = "DejaVu Sans";

    public static TheoryData<string, bool> Samples => new()
    {
        { "6-57-1119.dxf", false },
        { "HSK80AHCP16190M_BMG.dwg", false },
        { "HSK80AHCP16190M_BMG.dwg", true },
        { "Subaru Logo Vector Free Wrap.dxf", false },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void SampleRendersMatchBaselines(string fileName, bool paperLayouts)
    {
        Assert.True(SystemFonts.TryGet(FontFamily, out _), $"Font '{FontFamily}' must be installed for parity tests.");

        string repoRoot = FindRepoRoot();
        string samplePath = Path.Combine(repoRoot, "Samples", fileName);
        string baselineDirectory = Path.Combine(repoRoot, "ACadSharp.Image.Tests", "Baselines");
        Directory.CreateDirectory(baselineDirectory);

        bool update = Environment.GetEnvironmentVariable("ACADSHARP_IMAGE_UPDATE_BASELINES") == "1";
        string baseName = Path.GetFileNameWithoutExtension(fileName).Replace(' ', '-') + (paperLayouts ? ".paper" : ".model");

        IReadOnlyList<Image<Rgba32>> rendered = RenderSample(samplePath, paperLayouts);
        try
        {
            for (int i = 0; i < rendered.Count; i++)
            {
                string baselinePath = Path.Combine(baselineDirectory, $"{baseName}.{i + 1:D2}.png");
                if (update)
                {
                    rendered[i].Save(baselinePath, new PngEncoder());
                    continue;
                }

                Assert.True(File.Exists(baselinePath), $"Missing baseline {baselinePath}. Run with ACADSHARP_IMAGE_UPDATE_BASELINES=1 to create it.");
                using Image<Rgba32> baseline = Image.Load<Rgba32>(baselinePath);
                AssertPixelsEqual(baseline, rendered[i], baselinePath);
            }
        }
        finally
        {
            foreach (Image<Rgba32> image in rendered)
            {
                image.Dispose();
            }
        }
    }

    internal static IReadOnlyList<Image<Rgba32>> RenderSample(string samplePath, bool paperLayouts)
    {
        CadDocument document = Path.GetExtension(samplePath).ToLowerInvariant() == ".dwg"
            ? DwgReader.Read(samplePath)
            : DxfReader.Read(samplePath);

        ImageExporter exporter = new();
        exporter.Configuration.Width = 800;
        exporter.Configuration.Height = 500;
        exporter.Configuration.SetPadding(10);
        exporter.Configuration.FontFamilyName = FontFamily;

        if (paperLayouts)
        {
            exporter.AddPaperLayouts(document);
        }
        else
        {
            exporter.AddModelSpace(document);
        }

        List<Image<Rgba32>> images = new();
        foreach (RenderedImagePage page in exporter.Render())
        {
            images.Add(page.Canvas);
        }

        return images;
    }

    internal static void AssertPixelsEqual(Image<Rgba32> expected, Image<Rgba32> actual, string label)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        Rgba32[] expectedPixels = new Rgba32[expected.Width * expected.Height];
        Rgba32[] actualPixels = new Rgba32[actual.Width * actual.Height];
        expected.CopyPixelDataTo(expectedPixels);
        actual.CopyPixelDataTo(actualPixels);

        int firstDifference = -1;
        for (int i = 0; i < expectedPixels.Length; i++)
        {
            if (expectedPixels[i] != actualPixels[i])
            {
                firstDifference = i;
                break;
            }
        }

        Assert.True(firstDifference < 0, $"{label}: first differing pixel at index {firstDifference} (x={firstDifference % expected.Width}, y={firstDifference / expected.Width}); expected {expectedPixels[Math.Max(0, firstDifference)]} actual {actualPixels[Math.Max(0, firstDifference)]}.");
    }

    internal static string FindRepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory, "ACadSharp.Image.sln")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Could not locate the repository root (ACadSharp.Image.sln).");
    }
}
```

Note: the `RenderSample` loop iterates `RenderedImagePage`; Task 5 changes this to `Assert.IsType<RenderedImagePage>(page)` over `RenderedPage`.

- [ ] **Step 3: Generate the baselines with the current renderer**

Run: `ACADSHARP_IMAGE_UPDATE_BASELINES=1 dotnet test ACadSharp.Image.sln -c Release --nologo -v q --filter "FullyQualifiedName~SampleParityTests"`
Expected: PASS, and `ls ACadSharp.Image.Tests/Baselines` shows at least `6-57-1119.model.01.png`, `HSK80AHCP16190M_BMG.model.01.png`, `Subaru-Logo-Vector-Free-Wrap.model.01.png`. The `.paper.` files exist only if the DWG has paper layouts with content; zero pages is acceptable.

- [ ] **Step 4: Verify the comparison passes without the update flag**

Run: `dotnet test ACadSharp.Image.sln -c Release --nologo -v q --filter "FullyQualifiedName~SampleParityTests"`
Expected: PASS (4 test cases).

- [ ] **Step 5: Verify the test really compares**

Temporarily change `exporter.Configuration.Width = 800;` to `801` in `RenderSample`, run the same command, expect FAIL on the width assertion, then revert to `800`.

- [ ] **Step 6: Commit**

```bash
git add ACadSharp.Image.Tests/SampleParityTests.cs ACadSharp.Image.Tests/Baselines docs
git commit -m "Add sample parity baselines and design docs

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

---

### Task 2: ACadSharp 3.7.1, drop net6.0, workflows

**Files:**
- Modify: `Directory.Packages.props:7`
- Modify: `ACadSharp.Image/ACadSharp.Image.csproj:4,10,11`
- Modify: `.github/workflows/ci.yml`, `.github/workflows/release.yml` (the `dotnet-version` lists)

- [ ] **Step 1: Bump ACadSharp**

In `Directory.Packages.props` change `<PackageVersion Include="ACadSharp" Version="3.4.24" />` to `Version="3.7.1"`.

- [ ] **Step 2: Drop net6.0 and update package metadata**

In `ACadSharp.Image/ACadSharp.Image.csproj`:
- `<TargetFrameworks Condition="'$(PublishAot)' != 'true'">net6.0;net8.0;net10.0</TargetFrameworks>` becomes `net8.0;net10.0`.
- `<Description>` becomes `Raster and SVG exporter for ACadSharp DXF and DWG documents.`
- `<PackageTags>` becomes `acadsharp;cad;dxf;dwg;imagesharp;svg;rendering`.

- [ ] **Step 3: Remove 6.0.x from both workflows**

In `.github/workflows/ci.yml` and in the `publish-packages` job of `.github/workflows/release.yml`, delete the line `            6.0.x` so each `dotnet-version` block lists only `8.0.x` and `10.0.x` (the `publish-native-cli` job already lists only `10.0.x`).

- [ ] **Step 4: Build and test**

Run: `dotnet build ACadSharp.Image.sln -c Release --nologo -v q 2>&1 | grep -E "error|Warn|warn" ; dotnet test ACadSharp.Image.sln -c Release --nologo -v q`
Expected: no `CodePages ... net6.0` warning anymore, `0 Error(s)`, all tests pass including the parity theory.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props ACadSharp.Image/ACadSharp.Image.csproj .github/workflows/ci.yml .github/workflows/release.yml
git commit -m "Update ACadSharp to 3.7.1 and drop net6.0

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

---

### Task 3: Surface types, interface, tessellation helper, raster surface

**Files:**
- Create: `ACadSharp.Image/Rendering/SurfacePoint.cs`
- Modify: `ACadSharp.Image/Rendering/ImageStyle.cs`
- Create: `ACadSharp.Image/Rendering/SurfaceText.cs`
- Create: `ACadSharp.Image/Rendering/EntityRenderInfo.cs`
- Create: `ACadSharp.Image/Rendering/IDrawingSurface.cs`
- Create: `ACadSharp.Image/Rendering/CurveTessellation.cs`
- Create: `ACadSharp.Image/Rendering/RasterDrawingSurface.cs`
- Test: `ACadSharp.Image.Tests/RasterDrawingSurfaceTests.cs`

**Interfaces:**
- Produces (verbatim from spec section 9): `SurfacePoint`, `SurfaceRect`, `ImageStyle(StrokeColor, StrokeWidth, DashPattern, Opacity)`, `SurfaceText`, `SurfaceTextAnchor`, `SurfaceTextBaseline`, `EntityRenderInfo`, `LayerRenderInfo`, `ViewportSurface`, `IDrawingSurface`, `CurveTessellation.ArcPoints(...)`, `CurveTessellation.BulgeArc(...)`, `RasterDrawingSurface(Image<Rgba32> canvas, ImageConfiguration configuration, bool ownsCanvas)`.

- [ ] **Step 1: Write the failing surface tests**

Create `ACadSharp.Image.Tests/RasterDrawingSurfaceTests.cs`:

```csharp
using ACadSharp.Image.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ACadSharp.Image.Tests;

public sealed class RasterDrawingSurfaceTests
{
    private static readonly Rgba32 White = Color.White.ToPixel<Rgba32>();

    private static readonly Rgba32 Black = Color.Black.ToPixel<Rgba32>();

    [Fact]
    public void DrawLinePaintsPixelsAlongTheLine()
    {
        using Image<Rgba32> canvas = new(20, 20, Color.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);

        surface.DrawLine(new ImageStyle(Color.Black, 2f), new SurfacePoint(2, 10), new SurfacePoint(18, 10));

        Assert.Equal(Black, canvas[10, 10]);
        Assert.Equal(White, canvas[10, 2]);
    }

    [Fact]
    public void DrawPolylineClosedConnectsLastPointToFirst()
    {
        using Image<Rgba32> canvas = new(20, 20, Color.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);
        SurfacePoint[] points = [new(2, 2), new(18, 2), new(18, 18)];

        surface.DrawPolyline(new ImageStyle(Color.Black, 2f), points, closed: true);

        // Closing edge runs from (18,18) back to (2,2): the midpoint (10,10) must be painted.
        Assert.Equal(Black, canvas[10, 10]);
    }

    [Fact]
    public void FillPathUsesEvenOddRuleForHoles()
    {
        using Image<Rgba32> canvas = new(40, 40, Color.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);
        SurfacePoint[] outer = [new(2, 2), new(38, 2), new(38, 38), new(2, 38)];
        SurfacePoint[] hole = [new(15, 15), new(25, 15), new(25, 25), new(15, 25)];

        surface.FillPath(new ImageStyle(Color.Black, 1f), [outer, hole]);

        Assert.Equal(Black, canvas[5, 5]);
        Assert.Equal(White, canvas[20, 20]);
    }

    [Fact]
    public void OpacityBlendsWithBackground()
    {
        using Image<Rgba32> canvas = new(10, 10, Color.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);

        surface.FillPolygon(new ImageStyle(Color.Black, 1f, null, 0.5f), [new(0, 0), new(10, 0), new(10, 10), new(0, 10)]);

        Rgba32 pixel = canvas[5, 5];
        Assert.InRange(pixel.R, 120, 135);
        Assert.Equal(pixel.R, pixel.G);
        Assert.Equal(255, pixel.A);
    }

    [Fact]
    public void DashPatternLeavesGaps()
    {
        using Image<Rgba32> canvas = new(60, 10, Color.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);

        // 10 px dash, 10 px gap.
        surface.DrawLine(new ImageStyle(Color.Black, 2f, [10f, 10f], 1f), new SurfacePoint(0, 5), new SurfacePoint(60, 5));

        Assert.Equal(Black, canvas[5, 5]);
        Assert.Equal(White, canvas[15, 5]);
        Assert.Equal(Black, canvas[25, 5]);
    }

    [Fact]
    public void ViewportDrawsIntoChildAndCompositesAtBounds()
    {
        using Image<Rgba32> canvas = new(40, 40, Color.White);
        using RasterDrawingSurface surface = new(canvas, new ImageConfiguration(), ownsCanvas: false);

        ViewportSurface viewport = surface.BeginViewport(new SurfaceRect(20, 20, 10, 10));
        Assert.Equal(0d, viewport.OffsetX);
        Assert.Equal(10d, viewport.BottomY);

        // Fill the whole child; only the 10x10 region at (20,20) may change on the page.
        viewport.Surface.FillPolygon(new ImageStyle(Color.Black, 1f), [new(0, 0), new(10, 0), new(10, 10), new(0, 10)]);
        surface.EndViewport(viewport);

        Assert.Equal(Black, canvas[25, 25]);
        Assert.Equal(White, canvas[15, 15]);
        Assert.Equal(White, canvas[35, 35]);
    }

    [Fact]
    public void ArcPointsStartAndEndOnTheArc()
    {
        IReadOnlyList<SurfacePoint> points = CurveTessellation.ArcPoints(new SurfacePoint(0, 0), 10, 10, 0, 0, Math.PI / 2, 8);

        Assert.Equal(9, points.Count);
        Assert.Equal(10, points[0].X, 6);
        Assert.Equal(0, points[0].Y, 6);
        Assert.Equal(0, points[^1].X, 6);
        Assert.Equal(10, points[^1].Y, 6);
    }

    [Fact]
    public void BulgeArcOfOneIsASemicircle()
    {
        CurveTessellation.BulgeArc(new SurfacePoint(0, 0), new SurfacePoint(10, 0), 1d, out SurfacePoint center, out double radius, out double startAngle, out double sweep);

        Assert.Equal(5, center.X, 6);
        Assert.Equal(0, center.Y, 6);
        Assert.Equal(5, radius, 6);
        Assert.Equal(-Math.PI, sweep, 6);
        Assert.Equal(Math.PI, Math.Abs(startAngle), 6);
    }

    [Fact]
    public void PositiveBulgeBendsTowardPositiveYInSurfaceSpace()
    {
        // Drawing-space CCW arc from (0,0) to (10,0) passes below the chord; below is +Y on a Y-down surface.
        CurveTessellation.BulgeArc(new SurfacePoint(0, 0), new SurfacePoint(10, 0), 0.5d, out SurfacePoint center, out double radius, out double startAngle, out double sweep);

        Assert.Equal(5, center.X, 6);
        Assert.Equal(-3.75, center.Y, 6);
        Assert.Equal(6.25, radius, 6);
        Assert.True(sweep < 0);

        IReadOnlyList<SurfacePoint> points = CurveTessellation.ArcPoints(center, radius, radius, 0, startAngle, sweep, 2);
        Assert.Equal(5, points[1].X, 6);
        Assert.Equal(2.5, points[1].Y, 6);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test ACadSharp.Image.sln -c Release --nologo -v q --filter "FullyQualifiedName~RasterDrawingSurfaceTests"`
Expected: build errors (`RasterDrawingSurface`, `SurfacePoint` not found).

- [ ] **Step 3: Add the value types**

Create `ACadSharp.Image/Rendering/SurfacePoint.cs`:

```csharp
namespace ACadSharp.Image.Rendering;

/// <summary>
/// A point in surface coordinates: pixels for the raster backend, drawing units for SVG. Y grows downward.
/// </summary>
internal readonly record struct SurfacePoint(double X, double Y);

/// <summary>
/// An axis-aligned rectangle in surface coordinates. <see cref="Y"/> is the top edge.
/// </summary>
internal readonly record struct SurfaceRect(double X, double Y, double Width, double Height);
```

Replace `ACadSharp.Image/Rendering/ImageStyle.cs` with:

```csharp
using ImageColor = SixLabors.ImageSharp.Color;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Resolved visual style for one entity. Widths and dash lengths are in surface units.
/// </summary>
/// <param name="StrokeColor">Stroke and fill colour.</param>
/// <param name="StrokeWidth">Stroke width in surface units.</param>
/// <param name="DashPattern">Alternating dash and gap lengths in surface units, or <see langword="null"/> for a solid stroke.</param>
/// <param name="Opacity">Opacity from 0 (invisible) to 1 (opaque).</param>
internal readonly record struct ImageStyle(ImageColor StrokeColor, float StrokeWidth, float[]? DashPattern, float Opacity)
{
    public ImageStyle(ImageColor strokeColor, float strokeWidth)
        : this(strokeColor, strokeWidth, null, 1f)
    {
    }

    /// <summary>
    /// Gets the stroke colour with <see cref="Opacity"/> applied to its alpha channel.
    /// </summary>
    public ImageColor EffectiveColor => this.Opacity >= 1f ? this.StrokeColor : this.StrokeColor.WithAlpha(this.Opacity);
}
```

Create `ACadSharp.Image/Rendering/SurfaceText.cs`:

```csharp
namespace ACadSharp.Image.Rendering;

internal enum SurfaceTextAnchor
{
    Start,
    Middle,
    End,
}

internal enum SurfaceTextBaseline
{
    Alphabetic,
    Central,
    Hanging,
}

/// <summary>
/// Everything a backend needs to place a text run.
/// </summary>
/// <param name="Text">Text with CAD control codes already expanded; may contain newlines.</param>
/// <param name="Origin">Anchor point in surface units.</param>
/// <param name="Height">Text height (font size) in surface units.</param>
/// <param name="Rotation">Rotation in radians, counter-clockwise in drawing space. Backends negate it because surface Y points down.</param>
/// <param name="Anchor">Horizontal anchoring relative to <paramref name="Origin"/>.</param>
/// <param name="Baseline">Vertical anchoring relative to <paramref name="Origin"/>.</param>
/// <param name="WrappingWidth">Wrap width in surface units; zero or negative disables wrapping.</param>
/// <param name="LineSpacingFactor">Line spacing multiplier; 1.0 is single spacing.</param>
/// <param name="FixedLength">Total advance the text must occupy in surface units; zero or negative means natural width.</param>
internal sealed record SurfaceText(
    string Text,
    SurfacePoint Origin,
    double Height,
    double Rotation,
    SurfaceTextAnchor Anchor,
    SurfaceTextBaseline Baseline,
    double WrappingWidth,
    double LineSpacingFactor,
    double FixedLength);
```

Create `ACadSharp.Image/Rendering/EntityRenderInfo.cs`:

```csharp
using ImageColor = SixLabors.ImageSharp.Color;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Identifies the entity currently being drawn so structured backends can group and tag their output.
/// </summary>
/// <param name="LayerName">Effective layer name (entities on layer "0" inside a block inherit the insert's layer).</param>
/// <param name="EntityType">DXF object name, e.g. <c>LINE</c>.</param>
/// <param name="Handle">Entity handle.</param>
/// <param name="ParentHandle">Handle of the owning <c>Insert</c> or <c>Dimension</c> when drawing nested content.</param>
/// <param name="BlockName">Block name when drawing nested content of an <c>Insert</c>.</param>
internal sealed record EntityRenderInfo(string LayerName, string EntityType, ulong Handle, ulong? ParentHandle, string? BlockName);

/// <summary>
/// Layer defaults a structured backend may hoist onto a group element.
/// </summary>
internal sealed record LayerRenderInfo(string LayerName, ImageColor Color, float StrokeWidth);
```

Create `ACadSharp.Image/Rendering/IDrawingSurface.cs`:

```csharp
namespace ACadSharp.Image.Rendering;

/// <summary>
/// Result of opening a viewport: the surface to draw into and where its origin sits relative to the parent.
/// </summary>
/// <param name="Surface">Surface that receives the viewport contents.</param>
/// <param name="OffsetX">X of the viewport's left edge in <paramref name="Surface"/> units.</param>
/// <param name="BottomY">Y of the viewport's bottom edge in <paramref name="Surface"/> units.</param>
internal readonly record struct ViewportSurface(IDrawingSurface Surface, double OffsetX, double BottomY);

/// <summary>
/// Backend-neutral drawing primitives. Coordinates are surface units with Y growing downward.
/// </summary>
internal interface IDrawingSurface : IDisposable
{
    /// <summary>
    /// True when the backend draws arcs, ellipses and bulges natively; false when it wants tessellated polylines.
    /// </summary>
    bool SupportsCurves { get; }

    void BeginEntity(EntityRenderInfo info, LayerRenderInfo layer);

    void EndEntity();

    void DrawLine(ImageStyle style, SurfacePoint start, SurfacePoint end);

    void DrawPolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, bool closed);

    /// <summary>
    /// Draws an elliptical arc. Angles are radians in surface space (already sign-adjusted for the Y flip); a positive sweep turns clockwise on screen.
    /// </summary>
    void DrawArc(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation, double startAngle, double sweepAngle);

    void DrawEllipse(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation);

    /// <summary>
    /// Draws a chain of cubic Bezier segments given 3n+1 control points.
    /// </summary>
    void DrawCubicBezier(ImageStyle style, IReadOnlyList<SurfacePoint> controlPoints, bool closed);

    /// <summary>
    /// Draws a polyline whose segments may be circular arcs. <paramref name="bulges"/>[i] applies to the segment from points[i] to points[i+1]; 0 is straight.
    /// </summary>
    void DrawBulgePolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, IReadOnlyList<double> bulges, bool closed);

    void FillPolygon(ImageStyle style, IReadOnlyList<SurfacePoint> points);

    /// <summary>
    /// Fills several rings with the even-odd rule.
    /// </summary>
    void FillPath(ImageStyle style, IReadOnlyList<IReadOnlyList<SurfacePoint>> rings);

    void FillCircle(ImageStyle style, SurfacePoint center, double radius);

    void DrawText(ImageStyle style, SurfaceText text);

    /// <summary>
    /// Opens a clipped viewport region. <paramref name="bounds"/> is the viewport rectangle in this surface's units.
    /// </summary>
    ViewportSurface BeginViewport(SurfaceRect bounds);

    void EndViewport(ViewportSurface viewport);
}
```

- [ ] **Step 4: Add the tessellation helper**

Create `ACadSharp.Image/Rendering/CurveTessellation.cs`:

```csharp
namespace ACadSharp.Image.Rendering;

/// <summary>
/// Geometry helpers shared by backends that need arcs as points or bulges as arcs.
/// </summary>
internal static class CurveTessellation
{
    /// <summary>
    /// Samples an elliptical arc into <paramref name="segments"/> + 1 points.
    /// </summary>
    /// <param name="center">Centre in surface units.</param>
    /// <param name="radiusX">Semi-axis along the rotated X axis.</param>
    /// <param name="radiusY">Semi-axis along the rotated Y axis.</param>
    /// <param name="rotation">Rotation of the X axis in radians (surface space).</param>
    /// <param name="startAngle">Start parameter in radians (surface space).</param>
    /// <param name="sweepAngle">Signed sweep in radians (surface space).</param>
    /// <param name="segments">Number of straight segments, at least 1.</param>
    public static IReadOnlyList<SurfacePoint> ArcPoints(SurfacePoint center, double radiusX, double radiusY, double rotation, double startAngle, double sweepAngle, int segments)
    {
        segments = Math.Max(1, segments);
        double cos = Math.Cos(rotation);
        double sin = Math.Sin(rotation);
        SurfacePoint[] points = new SurfacePoint[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            double angle = startAngle + (sweepAngle * i / segments);
            double x = radiusX * Math.Cos(angle);
            double y = radiusY * Math.Sin(angle);
            points[i] = new SurfacePoint(
                center.X + (x * cos) - (y * sin),
                center.Y + (x * sin) + (y * cos));
        }

        return points;
    }

    /// <summary>
    /// Converts a polyline bulge into arc parameters in surface space.
    /// </summary>
    /// <remarks>
    /// Bulge is tan(theta/4) where theta is the included angle. A positive bulge is a counter-clockwise arc in the drawing
    /// and still looks counter-clockwise on screen after the Y flip; but in surface coordinates (Y down) a visually
    /// counter-clockwise turn is a decreasing angle, so a positive bulge yields a negative sweep here.
    /// </remarks>
    public static void BulgeArc(SurfacePoint start, SurfacePoint end, double bulge, out SurfacePoint center, out double radius, out double startAngle, out double sweepAngle)
    {
        double chordX = end.X - start.X;
        double chordY = end.Y - start.Y;
        double chord = Math.Sqrt((chordX * chordX) + (chordY * chordY));
        double theta = 4d * Math.Atan(Math.Abs(bulge));
        radius = chord / (2d * Math.Sin(theta / 2d));

        // Distance from the chord midpoint to the centre, along the chord normal.
        double sagitta = radius * Math.Cos(theta / 2d);
        double midX = (start.X + end.X) / 2d;
        double midY = (start.Y + end.Y) / 2d;
        double normalX = -chordY / chord;
        double normalY = chordX / chord;

        // The arc bulges toward +normal for a positive bulge, so the centre sits on the -normal side.
        double side = bulge > 0 ? -1d : 1d;
        center = new SurfacePoint(midX + (side * sagitta * normalX), midY + (side * sagitta * normalY));
        startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        sweepAngle = bulge > 0 ? -theta : theta;
    }

    /// <summary>
    /// Number of straight segments to use for an arc of <paramref name="sweepAngle"/> radians when a full turn uses <paramref name="fullCircleSegments"/>.
    /// </summary>
    public static int SegmentsForSweep(double sweepAngle, int fullCircleSegments)
    {
        return Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepAngle) / (2d * Math.PI) * Math.Max(4, fullCircleSegments)));
    }
}
```

- [ ] **Step 5: Add the raster surface**

Create `ACadSharp.Image/Rendering/RasterDrawingSurface.cs`. The text code is moved verbatim from today's `TextRenderer` (font creation, options, rotation transform) so glyph output stays identical.

```csharp
using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using ImageColor = SixLabors.ImageSharp.Color;
using ImagePoint = SixLabors.ImageSharp.Point;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// <see cref="IDrawingSurface"/> backed by an ImageSharp <see cref="Image{Rgba32}"/>.
/// </summary>
/// <remarks>
/// Every primitive maps onto the same ImageSharp.Drawing call the pre-abstraction renderer used, so output is pixel-identical.
/// Curves are not supported natively; callers tessellate them (<see cref="SupportsCurves"/> is false).
/// </remarks>
internal sealed class RasterDrawingSurface : IDrawingSurface
{
    private readonly ImageConfiguration _configuration;
    private readonly bool _ownsCanvas;
    private readonly Dictionary<ViewportSurface, (Image<Rgba32> Image, SurfaceRect Bounds)> _viewports = new();

    public RasterDrawingSurface(Image<Rgba32> canvas, ImageConfiguration configuration, bool ownsCanvas)
    {
        this.Canvas = canvas;
        this._configuration = configuration;
        this._ownsCanvas = ownsCanvas;
    }

    public Image<Rgba32> Canvas { get; }

    public bool SupportsCurves => false;

    public void BeginEntity(EntityRenderInfo info, LayerRenderInfo layer)
    {
    }

    public void EndEntity()
    {
    }

    public void DrawLine(ImageStyle style, SurfacePoint start, SurfacePoint end)
    {
        Pen pen = CreatePen(style);
        this.Canvas.Mutate(x => x.DrawLine(pen, ToPointF(start), ToPointF(end)));
    }

    public void DrawPolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, bool closed)
    {
        if (points.Count < 2)
        {
            return;
        }

        PointF[] pixels = new PointF[closed ? points.Count + 1 : points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            pixels[i] = ToPointF(points[i]);
        }

        if (closed)
        {
            pixels[^1] = pixels[0];
        }

        Pen pen = CreatePen(style);
        this.Canvas.Mutate(x => x.DrawLine(pen, pixels));
    }

    public void DrawArc(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation, double startAngle, double sweepAngle)
    {
        int segments = CurveTessellation.SegmentsForSweep(sweepAngle, this._configuration.ArcPrecision);
        this.DrawPolyline(style, CurveTessellation.ArcPoints(center, radiusX, radiusY, rotation, startAngle, sweepAngle, segments), closed: false);
    }

    public void DrawEllipse(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation)
    {
        this.DrawPolyline(style, CurveTessellation.ArcPoints(center, radiusX, radiusY, rotation, 0d, 2d * Math.PI, this._configuration.ArcPrecision), closed: true);
    }

    public void DrawCubicBezier(ImageStyle style, IReadOnlyList<SurfacePoint> controlPoints, bool closed)
    {
        if (controlPoints.Count < 4)
        {
            return;
        }

        PathBuilder builder = new();
        for (int index = 0; index + 3 < controlPoints.Count; index += 3)
        {
            builder.AddCubicBezier(
                ToPointF(controlPoints[index]),
                ToPointF(controlPoints[index + 1]),
                ToPointF(controlPoints[index + 2]),
                ToPointF(controlPoints[index + 3]));
        }

        IPath path = builder.Build();
        Pen pen = CreatePen(style);
        this.Canvas.Mutate(x => x.Draw(pen, path));
    }

    public void DrawBulgePolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, IReadOnlyList<double> bulges, bool closed)
    {
        if (points.Count < 2)
        {
            return;
        }

        List<SurfacePoint> flattened = new(points.Count * 4) { points[0] };
        int segmentCount = closed ? points.Count : points.Count - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            SurfacePoint start = points[i];
            SurfacePoint end = points[(i + 1) % points.Count];
            double bulge = i < bulges.Count ? bulges[i] : 0d;
            if (Math.Abs(bulge) < 1e-12 || start == end)
            {
                flattened.Add(end);
                continue;
            }

            CurveTessellation.BulgeArc(start, end, bulge, out SurfacePoint center, out double radius, out double startAngle, out double sweep);
            IReadOnlyList<SurfacePoint> arc = CurveTessellation.ArcPoints(center, radius, radius, 0d, startAngle, sweep, CurveTessellation.SegmentsForSweep(sweep, this._configuration.ArcPrecision));
            for (int j = 1; j < arc.Count; j++)
            {
                flattened.Add(arc[j]);
            }
        }

        this.DrawPolyline(style, flattened, closed: false);
    }

    public void FillPolygon(ImageStyle style, IReadOnlyList<SurfacePoint> points)
    {
        if (points.Count < 3)
        {
            return;
        }

        PointF[] pixels = points.Select(ToPointF).ToArray();
        ImageColor color = style.EffectiveColor;
        this.Canvas.Mutate(x => x.FillPolygon(color, pixels));
    }

    public void FillPath(ImageStyle style, IReadOnlyList<IReadOnlyList<SurfacePoint>> rings)
    {
        IPath[] polygons = rings
            .Where(ring => ring.Count >= 3)
            .Select(ring => (IPath)new Polygon(new LinearLineSegment(ring.Select(ToPointF).ToArray())))
            .ToArray();
        if (polygons.Length == 0)
        {
            return;
        }

        IPath shape = polygons.Length == 1 ? polygons[0] : new ComplexPolygon(polygons);
        ImageColor color = style.EffectiveColor;
        DrawingOptions options = new()
        {
            ShapeOptions = { IntersectionRule = IntersectionRule.EvenOdd },
        };
        this.Canvas.Mutate(x => x.Fill(options, color, shape));
    }

    public void FillCircle(ImageStyle style, SurfacePoint center, double radius)
    {
        PointF pixel = ToPointF(center);
        ImageColor color = style.EffectiveColor;
        this.Canvas.Mutate(x => x.Fill(color, new EllipsePolygon(pixel.X, pixel.Y, (float)radius)));
    }

    public void DrawText(ImageStyle style, SurfaceText text)
    {
        if (string.IsNullOrWhiteSpace(text.Text))
        {
            return;
        }

        PointF origin = ToPointF(text.Origin);
        Font font = this.CreateFont(text.Height);
        TextOptions options = new(font)
        {
            Dpi = this._configuration.Dpi,
            Origin = origin,
            HorizontalAlignment = text.Anchor switch
            {
                SurfaceTextAnchor.Middle => HorizontalAlignment.Center,
                SurfaceTextAnchor.End => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left,
            },
            VerticalAlignment = text.Baseline switch
            {
                SurfaceTextBaseline.Hanging => VerticalAlignment.Top,
                SurfaceTextBaseline.Central => VerticalAlignment.Center,
                _ => VerticalAlignment.Bottom,
            },
            WrappingLength = text.WrappingWidth > 0 ? (float)text.WrappingWidth : -1,
            LineSpacing = (float)text.LineSpacingFactor,
        };

        IPathCollection glyphs = TextBuilder.GenerateGlyphs(text.Text, options);
        DrawingOptions drawingOptions = new();
        if (Math.Abs(text.Rotation) > double.Epsilon)
        {
            drawingOptions.Transform = Matrix3x2.CreateRotation((float)-text.Rotation, new Vector2(origin.X, origin.Y));
        }

        ImageColor color = style.EffectiveColor;
        this.Canvas.Mutate(x => x.Fill(drawingOptions, color, glyphs));
    }

    public ViewportSurface BeginViewport(SurfaceRect bounds)
    {
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        Image<Rgba32> image = new(width, height, ImageColor.Transparent);
        RasterDrawingSurface child = new(image, this._configuration, ownsCanvas: true);
        ViewportSurface viewport = new(child, 0d, height);
        this._viewports[viewport] = (image, bounds);
        return viewport;
    }

    public void EndViewport(ViewportSurface viewport)
    {
        if (!this._viewports.Remove(viewport, out (Image<Rgba32> Image, SurfaceRect Bounds) entry))
        {
            throw new InvalidOperationException("EndViewport was called for a viewport this surface did not begin.");
        }

        ImagePoint destination = new((int)MathF.Round((float)entry.Bounds.X), (int)MathF.Round((float)entry.Bounds.Y));
        this.Canvas.Mutate(x => x.DrawImage(entry.Image, destination, 1f));
        viewport.Surface.Dispose();
    }

    public void Dispose()
    {
        foreach ((Image<Rgba32> image, _) in this._viewports.Values)
        {
            image.Dispose();
        }

        this._viewports.Clear();
        if (this._ownsCanvas)
        {
            this.Canvas.Dispose();
        }
    }

    private Font CreateFont(double height)
    {
        float size = Math.Max(1f, (float)height);
        if (SystemFonts.TryGet(this._configuration.FontFamilyName, out FontFamily family))
        {
            return family.CreateFont(size);
        }

        return SystemFonts.Families.First().CreateFont(size);
    }

    private static Pen CreatePen(ImageStyle style)
    {
        ImageColor color = style.EffectiveColor;
        if (style.DashPattern is not { Length: > 0 })
        {
            return new SolidPen(color, style.StrokeWidth);
        }

        // ImageSharp.Drawing pattern values are multiples of the stroke width.
        float width = Math.Max(0.01f, style.StrokeWidth);
        float[] pattern = new float[style.DashPattern.Length];
        for (int i = 0; i < pattern.Length; i++)
        {
            pattern[i] = Math.Max(0.001f, style.DashPattern[i] / width);
        }

        return new PatternPen(color, style.StrokeWidth, pattern);
    }

    private static PointF ToPointF(SurfacePoint point)
    {
        return new PointF((float)point.X, (float)point.Y);
    }
}
```

Parity notes for whoever touches this later: `DrawLine(Color, float, PointF[])` in ImageSharp.Drawing is a wrapper over `DrawLine(new SolidPen(color, width), points)`, so building the pen ourselves is pixel-identical. `EffectiveColor` returns the colour unchanged when opacity is 1.

- [ ] **Step 6: Run the surface tests**

Run: `dotnet test ACadSharp.Image.sln -c Release --nologo -v q --filter "FullyQualifiedName~RasterDrawingSurfaceTests"`
Expected: PASS (9 tests). If `OpacityBlendsWithBackground` lands outside 120..135, print the pixel and widen by at most 5 either side; ImageSharp blends 50% black over white to about 127 or 128.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test ACadSharp.Image.sln -c Release --nologo -v q`
Expected: PASS. Nothing in the render pipeline uses the surface yet, so parity is unaffected.

- [ ] **Step 8: Commit**

```bash
git add ACadSharp.Image/Rendering ACadSharp.Image.Tests/RasterDrawingSurfaceTests.cs
git commit -m "Add drawing surface abstraction and ImageSharp implementation

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

---

### Task 4: Route the render pipeline through the surface (pixel parity)

**Files:**
- Modify: `ACadSharp.Image/Rendering/ImageRenderContext.cs` (rewrite)
- Modify: `ACadSharp.Image/Rendering/ImageStyleResolver.cs`
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs`
- Modify: `ACadSharp.Image/Rendering/SplineRenderer.cs`
- Modify: `ACadSharp.Image/Rendering/TextRenderer.cs`
- Modify: `ACadSharp.Image/Rendering/ImagePageRenderer.cs`
- Modify: `ACadSharp.Image.Tests/ImageExporterTests.cs:60-84,130-176` (the two tests that build a context)

**Interfaces:**
- Consumes: `IDrawingSurface`, `RasterDrawingSurface`, `SurfacePoint`, `ImageStyle`, `SurfaceText` from Task 3.
- Produces: `ImageRenderContext` with `Surface`, `Scale`, `SurfaceWidth`, `SurfaceHeight`, `OffsetX`, `OffsetY`, `OriginX`, `OriginY`, `LineTypeScale`, `Viewport`, `Parent`, `SinglePrecision`, `ToSurfacePoint(XY)`, `ToSurfacePoint(XYZ)`, `ToSurfaceLength(double)`, `ToStrokeWidth(LineWeightType)`, static `CreatePageContext(IDrawingSurface, ImagePage, ImageConfiguration)` and `CreateViewportContext(ImageRenderContext parent, Viewport viewport, ViewportSurface surface, BoundingBox modelBounds, double scale)`. `ImageStyleResolver.Resolve(Entity, ImageRenderContext)`. `ImagePageRenderer.Render(ImagePage)` still returns `RenderedImagePage` until Task 5.

**Why single precision matters:** today's `ToPixelPoint` computes `float x = OffsetX + (float)((point.X - OriginX) * PixelsPerUnit)` with `OffsetX` and `PixelsPerUnit` as `float`. The refactored context stores doubles but, when `SinglePrecision` is true, performs the exact same float operations in the same order so the raster backend receives bit-identical coordinates. The SVG backend (plan 2) uses `SinglePrecision = false`.

- [ ] **Step 1: Rewrite `ImageRenderContext`**

Replace `ACadSharp.Image/Rendering/ImageRenderContext.cs` with:

```csharp
using ACadSharp.Entities;
using ACadSharp.Objects;
using CSMath;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Maps drawing coordinates onto an <see cref="IDrawingSurface"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>x = OffsetX + (p.X - OriginX) * Scale</c> and <c>y = SurfaceHeight - OffsetY - (p.Y - OriginY) * Scale</c>.
/// </para>
/// <para>
/// When <see cref="SinglePrecision"/> is true the arithmetic is performed in <see cref="float"/> in the same order
/// the original raster renderer used, so raster output stays pixel-identical.
/// </para>
/// </remarks>
internal sealed class ImageRenderContext
{
    public ImageRenderContext(
        IDrawingSurface surface,
        ImageConfiguration configuration,
        Layout layout,
        double surfaceWidth,
        double surfaceHeight,
        double originX,
        double originY,
        double scale,
        double offsetX,
        double offsetY,
        bool singlePrecision,
        double lineTypeScale,
        Viewport? viewport = null,
        ImageRenderContext? parent = null)
    {
        this.Surface = surface;
        this.Configuration = configuration;
        this.Layout = layout;
        this.SurfaceWidth = surfaceWidth;
        this.SurfaceHeight = surfaceHeight;
        this.OriginX = originX;
        this.OriginY = originY;
        this.Scale = scale;
        this.OffsetX = offsetX;
        this.OffsetY = offsetY;
        this.SinglePrecision = singlePrecision;
        this.LineTypeScale = lineTypeScale;
        this.Viewport = viewport;
        this.Parent = parent;
    }

    public IDrawingSurface Surface { get; }

    public ImageConfiguration Configuration { get; }

    public Layout Layout { get; }

    public double SurfaceWidth { get; }

    public double SurfaceHeight { get; }

    public double OriginX { get; }

    public double OriginY { get; }

    /// <summary>Surface units per drawing unit.</summary>
    public double Scale { get; }

    public double OffsetX { get; }

    public double OffsetY { get; }

    /// <summary>True for the raster backend: reproduces the original float arithmetic exactly.</summary>
    public bool SinglePrecision { get; }

    /// <summary>Surface units per linetype unit; differs from <see cref="Scale"/> inside viewports with paper-space linetype scaling.</summary>
    public double LineTypeScale { get; }

    /// <summary>Viewport whose contents are being drawn, or null for page-level content.</summary>
    public Viewport? Viewport { get; }

    public ImageRenderContext? Parent { get; }

    public static ImageRenderContext CreatePageContext(IDrawingSurface surface, ImagePage page, ImageConfiguration configuration)
    {
        int drawableWidth = configuration.Width - configuration.PaddingLeft - configuration.PaddingRight;
        int drawableHeight = configuration.Height - configuration.PaddingTop - configuration.PaddingBottom;
        if (drawableWidth <= 0 || drawableHeight <= 0)
        {
            throw new InvalidOperationException("Padding must leave at least one drawable pixel in both dimensions.");
        }

        Layout layout = page.Layout ?? new Layout("default_page");
        double pageWidth = Math.Max(1d, layout.PaperWidth);
        double pageHeight = Math.Max(1d, layout.PaperHeight);
        float pixelsPerUnit = Math.Min(
            drawableWidth / (float)pageWidth,
            drawableHeight / (float)pageHeight);

        float scaledWidth = (float)pageWidth * pixelsPerUnit;
        float scaledHeight = (float)pageHeight * pixelsPerUnit;
        float offsetX = configuration.PaddingLeft + ((drawableWidth - scaledWidth) / 2f);
        float offsetY = configuration.PaddingBottom + ((drawableHeight - scaledHeight) / 2f);

        double originX = -page.Translation.X - layout.UnprintableMargin.Left;
        double originY = -page.Translation.Y - layout.UnprintableMargin.Bottom;

        return new ImageRenderContext(
            surface,
            configuration,
            layout,
            configuration.Width,
            configuration.Height,
            originX,
            originY,
            pixelsPerUnit,
            offsetX,
            offsetY,
            singlePrecision: true,
            lineTypeScale: pixelsPerUnit);
    }

    public static ImageRenderContext CreateViewportContext(ImageRenderContext parent, Viewport viewport, ViewportSurface surface, BoundingBox modelBounds, double scale)
    {
        return new ImageRenderContext(
            surface.Surface,
            parent.Configuration,
            parent.Layout,
            surfaceWidth: 0d,
            surfaceHeight: surface.BottomY,
            originX: modelBounds.Min.X,
            originY: modelBounds.Min.Y,
            scale: scale,
            offsetX: surface.OffsetX,
            offsetY: 0d,
            singlePrecision: parent.SinglePrecision,
            lineTypeScale: scale,
            viewport: viewport,
            parent: parent);
    }

    public SurfacePoint ToSurfacePoint(XY point)
    {
        if (this.SinglePrecision)
        {
            float x = (float)this.OffsetX + (float)((point.X - this.OriginX) * (float)this.Scale);
            float y = (float)this.SurfaceHeight - (float)this.OffsetY - (float)((point.Y - this.OriginY) * (float)this.Scale);
            return new SurfacePoint(x, y);
        }

        return new SurfacePoint(
            this.OffsetX + ((point.X - this.OriginX) * this.Scale),
            this.SurfaceHeight - this.OffsetY - ((point.Y - this.OriginY) * this.Scale));
    }

    public SurfacePoint ToSurfacePoint(XYZ point)
    {
        return this.ToSurfacePoint(point.Convert<XY>());
    }

    public double ToSurfaceLength(double value)
    {
        return this.SinglePrecision
            ? (float)value * (float)this.Scale
            : value * this.Scale;
    }

    /// <summary>
    /// Stroke width in surface units for a line weight. Raster: pixels from the configuration table.
    /// </summary>
    public float ToStrokeWidth(LineWeightType lineWeight)
    {
        return this.Configuration.GetLineWeightPixels(lineWeight);
    }
}
```

`(float)((point.X - this.OriginX) * (float)this.Scale)` is the same expression as before: `double * float` promotes the float to double, exactly as `(point.X - OriginX) * PixelsPerUnit` did, and `Scale` holds the float value widened (it was constructed from `pixelsPerUnit`, a float).

- [ ] **Step 2: Update `ImageStyleResolver`**

Replace the `Resolve` method in `ACadSharp.Image/Rendering/ImageStyleResolver.cs`:

```csharp
    /// <summary>
    /// Resolves the visual style for a CAD entity in the given context.
    /// </summary>
    public ImageStyle Resolve(Entity entity, ImageRenderContext context)
    {
        return new ImageStyle(
            entity.GetActiveColor().ToImageColor(),
            context.ToStrokeWidth(entity.GetActiveLineWeightType()));
    }
```

Remove the now-unused `_configuration` field and constructor parameter only if nothing else uses them; otherwise leave them.

- [ ] **Step 3: Rewrite `TextRenderer` as a `SurfaceText` builder**

Replace `ACadSharp.Image/Rendering/TextRenderer.cs` with:

```csharp
using ACadSharp.Entities;
using CSMath;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Converts <see cref="MText"/> and <see cref="TextEntity"/> into <see cref="SurfaceText"/> runs and hands them to the surface.
/// </summary>
internal sealed class TextRenderer
{
    public void Draw(ImageRenderContext context, ImageStyle style, MText mtext)
    {
        string text = NormalizeText(mtext.PlainText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        SurfaceText run = new(
            text,
            context.ToSurfacePoint(mtext.InsertPoint),
            context.ToSurfaceLength(mtext.Height),
            mtext.Rotation,
            GetAnchor(mtext.AttachmentPoint),
            GetBaseline(mtext.AttachmentPoint),
            mtext.RectangleWidth > 0 ? context.ToSurfaceLength(mtext.RectangleWidth) : -1d,
            mtext.LineSpacing,
            FixedLength: -1d);

        context.Surface.DrawText(style, run);
    }

    public void Draw(ImageRenderContext context, ImageStyle style, TextEntity textEntity)
    {
        string text = NormalizeText(textEntity.Value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        SurfaceText run = new(
            text,
            context.ToSurfacePoint(GetTextOrigin(textEntity)),
            context.ToSurfaceLength(textEntity.Height),
            textEntity.Rotation,
            GetAnchor(textEntity.HorizontalAlignment),
            GetBaseline(textEntity.VerticalAlignment),
            WrappingWidth: -1d,
            LineSpacingFactor: 1d,
            GetFixedLength(context, textEntity));

        context.Surface.DrawText(style, run);
    }

    private static double GetFixedLength(ImageRenderContext context, TextEntity textEntity)
    {
        if (textEntity.HorizontalAlignment is not (TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Fit))
        {
            return -1d;
        }

        double dx = textEntity.AlignmentPoint.X - textEntity.InsertPoint.X;
        double dy = textEntity.AlignmentPoint.Y - textEntity.InsertPoint.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        return length > 0 ? context.ToSurfaceLength(length) : -1d;
    }

    private static XYZ GetTextOrigin(TextEntity textEntity)
    {
        return textEntity.HorizontalAlignment == TextHorizontalAlignment.Left && textEntity.VerticalAlignment == TextVerticalAlignmentType.Baseline
            ? textEntity.InsertPoint
            : textEntity.AlignmentPoint;
    }

    private static SurfaceTextAnchor GetAnchor(AttachmentPointType attachment)
    {
        return attachment switch
        {
            AttachmentPointType.TopCenter or AttachmentPointType.MiddleCenter or AttachmentPointType.BottomCenter => SurfaceTextAnchor.Middle,
            AttachmentPointType.TopRight or AttachmentPointType.MiddleRight or AttachmentPointType.BottomRight => SurfaceTextAnchor.End,
            _ => SurfaceTextAnchor.Start,
        };
    }

    private static SurfaceTextBaseline GetBaseline(AttachmentPointType attachment)
    {
        return attachment switch
        {
            AttachmentPointType.TopLeft or AttachmentPointType.TopCenter or AttachmentPointType.TopRight => SurfaceTextBaseline.Hanging,
            AttachmentPointType.MiddleLeft or AttachmentPointType.MiddleCenter or AttachmentPointType.MiddleRight => SurfaceTextBaseline.Central,
            _ => SurfaceTextBaseline.Alphabetic,
        };
    }

    private static SurfaceTextAnchor GetAnchor(TextHorizontalAlignment alignment)
    {
        return alignment switch
        {
            TextHorizontalAlignment.Center or TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Middle or TextHorizontalAlignment.Fit => SurfaceTextAnchor.Middle,
            TextHorizontalAlignment.Right => SurfaceTextAnchor.End,
            _ => SurfaceTextAnchor.Start,
        };
    }

    private static SurfaceTextBaseline GetBaseline(TextVerticalAlignmentType alignment)
    {
        return alignment switch
        {
            TextVerticalAlignmentType.Middle => SurfaceTextBaseline.Central,
            TextVerticalAlignmentType.Top => SurfaceTextBaseline.Hanging,
            _ => SurfaceTextBaseline.Alphabetic,
        };
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("%%C", "Ø", StringComparison.OrdinalIgnoreCase)
            .Replace("%%D", "°", StringComparison.OrdinalIgnoreCase)
            .Replace("%%P", "±", StringComparison.OrdinalIgnoreCase)
            .Replace("\\P", "\n", StringComparison.OrdinalIgnoreCase);
    }
}
```

The mapping tables are the same as before; the raster surface maps `Start/Middle/End` back to `Left/Center/Right` and `Hanging/Central/Alphabetic` back to `Top/Center/Bottom`, so the `TextOptions` the surface builds are identical to the old ones. (CSMath's `XYZ` exposes no distance helper in 3.7.1, hence the explicit arithmetic.)

- [ ] **Step 4: Update `SplineRenderer` to draw through the surface**

In `ACadSharp.Image/Rendering/SplineRenderer.cs`:

Change the class header to `internal sealed class SplineRenderer(ImageConfiguration configuration)` (unchanged) and remove the ImageSharp `using` lines (`SixLabors.ImageSharp`, `SixLabors.ImageSharp.Drawing`, `SixLabors.ImageSharp.Drawing.Processing`, `SixLabors.ImageSharp.Processing`).

Replace the body of `Draw`:

```csharp
    public bool Draw(ImageRenderContext context, ImageStyle style, Spline spline)
    {
        if (this.DrawBezierSpline(context, style, spline))
        {
            return true;
        }

        XY[] sampledVertices = this.SampleSpline(spline);
        if (sampledVertices.Length > 1)
        {
            SurfacePoint[] points = new SurfacePoint[sampledVertices.Length];
            for (int i = 0; i < sampledVertices.Length; i++)
            {
                points[i] = context.ToSurfacePoint(sampledVertices[i]);
            }

            context.Surface.DrawPolyline(style, points, ShouldClosePoints(points, spline.IsClosed || spline.IsPeriodic));
            return true;
        }

        if (spline.TryPolygonalVertexes(this._configuration.ArcPrecision, out List<XYZ>? polygonalPoints) && polygonalPoints.Count > 1)
        {
            SurfacePoint[] points = new SurfacePoint[polygonalPoints.Count];
            for (int i = 0; i < polygonalPoints.Count; i++)
            {
                points[i] = context.ToSurfacePoint(polygonalPoints[i].Convert<XY>());
            }

            context.Surface.DrawPolyline(style, points, ShouldClosePoints(points, spline.IsClosed || spline.IsPeriodic));
            return true;
        }

        this._configuration.Notify($"[{spline.SubclassMarker}] Could not approximate spline geometry.", NotificationType.Warning);
        return false;
    }
```

Replace `DrawBezierSpline`:

```csharp
    private bool DrawBezierSpline(ImageRenderContext context, ImageStyle style, Spline spline)
    {
        if (!TryGetBezierSegments(spline, out int segmentCount))
        {
            return false;
        }

        IReadOnlyList<XYZ> controlPoints = spline.ControlPoints;
        SurfacePoint[] points = new SurfacePoint[(segmentCount * 3) + 1];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = context.ToSurfacePoint(controlPoints[i]);
        }

        context.Surface.DrawCubicBezier(style, points, spline.IsClosed || spline.IsPeriodic);
        return true;
    }
```

Make `TryGetBezierSegments` `internal static` (plan 2 reuses it). Replace `ClosePoints`, `ShouldClose(IReadOnlyList<PointF>)` and `Distance(PointF, PointF)` with:

```csharp
    internal static bool ShouldClosePoints(IReadOnlyList<SurfacePoint> points, bool close)
    {
        if (!close || points.Count < 3)
        {
            return false;
        }

        float totalLength = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            totalLength += Distance(points[i - 1], points[i]);
        }

        float averageSegmentLength = totalLength / (points.Count - 1);
        float closingLength = Distance(points[^1], points[0]);
        return closingLength <= averageSegmentLength * 3f;
    }

    private static float Distance(SurfacePoint a, SurfacePoint b)
    {
        float dx = (float)a.X - (float)b.X;
        float dy = (float)a.Y - (float)b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
```

The float arithmetic mirrors the old `PointF` version, so the closing decision is unchanged.

- [ ] **Step 5: Update `EntityRenderDispatcher`**

In `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` remove the four `SixLabors.*` usings and rewrite the drawing methods. Full replacement of the class body below the constructor (keep the class doc comment and constructor):

```csharp
    public void Draw(ImageRenderContext context, Entity entity)
    {
        ImageStyle style = this._styleResolver.Resolve(entity, context);

        switch (entity)
        {
            case Arc arc:
                this.DrawPolyline(context, style, arc.PolygonalVertexes(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), false);
                break;
            case Circle circle:
                this.DrawPolyline(context, style, circle.PolygonalVertexes(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), true);
                break;
            case Ellipse ellipse:
                this.DrawPolyline(context, style, ellipse.PolygonalVertexes(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), true);
                break;
            case Line line:
                context.Surface.DrawLine(style, context.ToSurfacePoint(line.StartPoint), context.ToSurfacePoint(line.EndPoint));
                break;
            case Dimension dimension:
                this.DrawDimension(context, dimension);
                break;
            case Solid solid:
                this.DrawSolid(context, style, solid);
                break;
            case ACadSharp.Entities.Point point:
                this.DrawPoint(context, style, point);
                break;
            case IPolyline polyline:
                this.DrawPolyline(context, style, polyline.GetPoints<XYZ>(this._configuration.ArcPrecision).Select(v => v.Convert<XY>()), polyline.IsClosed);
                break;
            case Spline spline:
                this._splineRenderer.Draw(context, style, spline);
                break;
            case MText mtext:
                this._textRenderer.Draw(context, style, mtext);
                break;
            case TextEntity textEntity:
                this._textRenderer.Draw(context, style, textEntity);
                break;
            case IText text:
                this._configuration.Notify($"[{entity.SubclassMarker}] Text rendering is not implemented yet.", NotificationType.NotImplemented);
                break;
            case Insert insert:
                this.DrawBlockContents(context, insert);
                break;
            default:
                this._configuration.Notify($"[{entity.SubclassMarker}] Drawing not implemented.", NotificationType.NotImplemented);
                break;
        }
    }

    private void DrawPoint(ImageRenderContext context, ImageStyle style, ACadSharp.Entities.Point point)
    {
        float radius = Math.Max(1f, this._configuration.DotSizePixels / 2f);
        context.Surface.FillCircle(style, context.ToSurfacePoint(point.Location), radius);
    }

    private void DrawDimension(ImageRenderContext context, Dimension dimension)
    {
        BlockRecord? block = dimension.Block;
        if (block == null)
        {
            dimension.UpdateBlock();
            block = dimension.Block;
        }

        if (block == null)
        {
            this._configuration.Notify($"[{dimension.SubclassMarker}] Dimension block is not available.", NotificationType.Warning);
            return;
        }

        foreach (Entity entity in block.Entities)
        {
            if (entity is ACadSharp.Entities.Point)
            {
                continue;
            }

            this.Draw(context, entity);
        }
    }

    private static void DrawSolid(ImageRenderContext context, ImageStyle style, Solid solid)
    {
        SurfacePoint[] points =
        [
            context.ToSurfacePoint(solid.FirstCorner),
            context.ToSurfacePoint(solid.SecondCorner),
            context.ToSurfacePoint(solid.ThirdCorner),
            context.ToSurfacePoint(solid.FourthCorner),
        ];

        context.Surface.FillPolygon(style, points);
    }

    private void DrawPolyline(ImageRenderContext context, ImageStyle style, IEnumerable<XY> vertices, bool close)
    {
        SurfacePoint[] points = vertices.Select(context.ToSurfacePoint).ToArray();
        if (points.Length < 2)
        {
            return;
        }

        context.Surface.DrawPolyline(style, points, SplineRenderer.ShouldClosePoints(points, close));
    }

    private void DrawBlockContents(ImageRenderContext context, Insert insert)
    {
        foreach (Entity entity in insert.Explode())
        {
            this.Draw(context, entity);
        }
    }
```

Delete the old `ShouldClose` and `Distance` helpers from the dispatcher (they now live in `SplineRenderer`). `DrawSolid` becomes `static` because it no longer touches instance state; change the call site to `DrawSolid(context, style, solid)`. The `DrawPoint` radius is the same `Math.Max(1f, DotSizePixels / 2f)` as before; note `FillCircle` takes a double and casts back to float inside the raster surface, which is lossless.

- [ ] **Step 6: Update `ImagePageRenderer`**

Replace the `Render` and `DrawViewport` methods in `ACadSharp.Image/Rendering/ImagePageRenderer.cs` (keep the `using` for `SixLabors.ImageSharp` and `Rgba32`; remove `SixLabors.ImageSharp.Processing`, `ImageColor` and `ImagePoint` aliases if unused):

```csharp
    public RenderedImagePage Render(ImagePage page)
    {
        Image<Rgba32> image = new(this._configuration.Width, this._configuration.Height, this._configuration.BackgroundColor);
        using RasterDrawingSurface surface = new(image, this._configuration, ownsCanvas: false);
        this.RenderTo(surface, page);
        return new RenderedImagePage(page.Name, image);
    }

    internal void RenderTo(IDrawingSurface surface, ImagePage page)
    {
        ImageRenderContext context = ImageRenderContext.CreatePageContext(surface, page, this._configuration);

        foreach (Viewport viewport in page.Viewports)
        {
            this.DrawViewport(context, viewport);
        }

        foreach (Entity entity in page.Entities)
        {
            this._dispatcher.Draw(context, entity);
        }
    }

    private void DrawViewport(ImageRenderContext pageContext, Viewport viewport)
    {
        BoundingBox viewportBounds = viewport.GetBoundingBox();
        double viewportWidth = Math.Max(1, (int)Math.Ceiling(pageContext.ToSurfaceLength(viewportBounds.LengthX)));
        double viewportHeight = Math.Max(1, (int)Math.Ceiling(pageContext.ToSurfaceLength(viewportBounds.LengthY)));
        BoundingBox modelBounds = viewport.GetModelBoundingBox();

        SurfacePoint topLeft = pageContext.ToSurfacePoint(new XY(viewportBounds.Min.X, viewportBounds.Max.Y));
        ViewportSurface viewportSurface = pageContext.Surface.BeginViewport(new SurfaceRect(topLeft.X, topLeft.Y, viewportWidth, viewportHeight));

        double scale = pageContext.SinglePrecision
            ? (float)pageContext.Scale * (float)viewport.ScaleFactor
            : pageContext.Scale * viewport.ScaleFactor;
        ImageRenderContext viewportContext = ImageRenderContext.CreateViewportContext(pageContext, viewport, viewportSurface, modelBounds, scale);

        foreach (Entity entity in viewport.SelectEntities())
        {
            this._dispatcher.Draw(viewportContext, entity);
        }

        pageContext.Surface.EndViewport(viewportSurface);
    }
```

`ToSurfaceLength` in single-precision mode returns `(float)value * (float)Scale`, the same as the old `ToPixelLength`, and the ceiling/int conversion is unchanged. The viewport image size in `RasterDrawingSurface.BeginViewport` is `Ceiling(bounds.Width)` of an already-integral value, so it is the same integer.

- [ ] **Step 7: Fix the two tests that construct a context directly**

In `ACadSharp.Image.Tests/ImageExporterTests.cs`, `PageContextUsesConfiguredPadding` becomes:

```csharp
        using Image<Rgba32> canvas = new(configuration.Width, configuration.Height);
        using RasterDrawingSurface surface = new(canvas, configuration, ownsCanvas: false);
        ImageRenderContext context = ImageRenderContext.CreatePageContext(surface, page, configuration);

        Assert.Equal(5d, context.Scale);
        Assert.Equal(10d, context.OffsetX);
        Assert.Equal(20d, context.OffsetY);
```

and in `RenderClosedPeriodicSplineDoesNotDrawSpokeToOrigin` replace the context construction with:

```csharp
        using RasterDrawingSurface surface = new(canvas, configuration, ownsCanvas: false);
        ImageRenderContext context = new(surface, configuration, page.Layout, 100, 100, -5, -5, 10f, 0, 0, singlePrecision: true, lineTypeScale: 10f);
```

- [ ] **Step 8: Build, then run the full suite including parity**

Run: `dotnet build ACadSharp.Image.sln -c Release --nologo -v q 2>&1 | grep -E " error " ; dotnet test ACadSharp.Image.sln -c Release --nologo -v q`
Expected: 0 errors, all tests PASS, including the 4 `SampleParityTests` cases.

If a parity case fails, the message names the first differing pixel. Diagnose in this order: (1) a `float`/`double` ordering difference in `ToSurfacePoint` or `ToSurfaceLength`; (2) `DrawPolyline` closing decision (`ShouldClosePoints` must receive the original `close` request and the un-closed points); (3) text options (compare each `TextOptions` property to the old `TextRenderer`); (4) viewport destination rounding (`MathF.Round((float)bounds.X)`). Do not update the baselines to make the test pass.

- [ ] **Step 9: Commit**

```bash
git add ACadSharp.Image/Rendering ACadSharp.Image.Tests/ImageExporterTests.cs
git commit -m "Render entities through the drawing surface

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

---

### Task 5: `RenderedPage` abstraction and `Render(format)`

**Files:**
- Create: `ACadSharp.Image/RenderedPage.cs`
- Modify: `ACadSharp.Image/RenderedImagePage.cs`
- Modify: `ACadSharp.Image/ImageExporter.cs:144-272` (`Render`, `Save`, `SaveInternal`, `SavePage`)
- Modify: `ACadSharp.Image/Rendering/ImagePageRenderer.cs` (`Render` signature)
- Modify: `ACadSharp.Image.Tests/ImageExporterTests.cs`, `ACadSharp.Image.Tests/SampleParityTests.cs` (call sites of `Render()`)

**Interfaces:**
- Produces: `public abstract class RenderedPage : IDisposable { string Name; ImageExportFormat Format; abstract void Save(string path); abstract void Save(Stream stream); }`, `RenderedImagePage(string name, Image<Rgba32> canvas, ImageExportFormat format, int quality)`, `ImageExporter.Render(ImageExportFormat format = ImageExportFormat.Png) : IReadOnlyList<RenderedPage>`, `ImagePageRenderer.Render(ImagePage page, ImageExportFormat format) : RenderedPage`.

- [ ] **Step 1: Write the failing tests**

Append to `ACadSharp.Image.Tests/ImageExporterTests.cs`:

```csharp
    [Fact]
    public void RenderReturnsRasterPagesCarryingTheRequestedFormat()
    {
        BlockRecord block = new("format-block");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)));

        ImageExporter exporter = new();
        exporter.Add(block);

        using RenderedPage page = Assert.Single(exporter.Render(ImageExportFormat.Jpeg));

        RenderedImagePage raster = Assert.IsType<RenderedImagePage>(page);
        Assert.Equal(ImageExportFormat.Jpeg, raster.Format);
        Assert.Equal("format-block", raster.Name);
    }

    [Fact]
    public void RenderedPageSavesToStreamInItsFormat()
    {
        BlockRecord block = new("stream-block");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)));

        ImageExporter exporter = new();
        exporter.Configuration.Width = 32;
        exporter.Configuration.Height = 32;
        exporter.Add(block);

        using RenderedPage page = Assert.Single(exporter.Render(ImageExportFormat.Png));
        using MemoryStream stream = new();
        page.Save(stream);

        byte[] bytes = stream.ToArray();
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }
```

Also update the existing call sites in the same file: every `using RenderedImagePage page = Assert.Single(exporter.Render());` becomes `using RenderedImagePage page = Assert.IsType<RenderedImagePage>(Assert.Single(exporter.Render()));`. In `SampleParityTests.RenderSample` change the loop to:

```csharp
        foreach (RenderedPage page in exporter.Render())
        {
            images.Add(Assert.IsType<RenderedImagePage>(page).Canvas);
        }
```

- [ ] **Step 2: Run to confirm compile failure**

Run: `dotnet test ACadSharp.Image.sln -c Release --nologo -v q --filter "FullyQualifiedName~ImageExporterTests"`
Expected: build error, `RenderedPage` not found.

- [ ] **Step 3: Add `RenderedPage`**

Create `ACadSharp.Image/RenderedPage.cs`:

```csharp
namespace ACadSharp.Image;

/// <summary>
/// A rendered page produced by <see cref="ImageExporter.Render(ImageExportFormat)"/>, ready to be saved in its <see cref="Format"/>.
/// </summary>
public abstract class RenderedPage : IDisposable
{
    protected RenderedPage(string name, ImageExportFormat format)
    {
        this.Name = name;
        this.Format = format;
    }

    /// <summary>
    /// Gets the name of this page (layout name or block name).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the format this page will be saved as.
    /// </summary>
    public ImageExportFormat Format { get; }

    /// <summary>
    /// Saves the page to a file, creating the directory if needed.
    /// </summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using FileStream stream = new(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        this.Save(stream);
    }

    /// <summary>
    /// Writes the page to a stream in its <see cref="Format"/>.
    /// </summary>
    public abstract void Save(Stream stream);

    /// <inheritdoc/>
    public abstract void Dispose();
}
```

- [ ] **Step 4: Make `RenderedImagePage` a `RenderedPage`**

Replace `ACadSharp.Image/RenderedImagePage.cs` with:

```csharp
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace ACadSharp.Image;

/// <summary>
/// A page rendered to a raster canvas.
/// </summary>
/// <remarks>
/// Owns the underlying <see cref="SixLabors.ImageSharp.Image{Rgba32}"/>; dispose the page to release it.
/// </remarks>
public sealed class RenderedImagePage : RenderedPage
{
    private readonly int _quality;

    /// <summary>
    /// Initializes a new instance of the <see cref="RenderedImagePage"/> class.
    /// </summary>
    /// <param name="name">Page name.</param>
    /// <param name="canvas">Rendered canvas; ownership transfers to the page.</param>
    /// <param name="format">Raster format used by <see cref="RenderedPage.Save(Stream)"/>. Must not be <see cref="ImageExportFormat.Svg"/>.</param>
    /// <param name="quality">Quality 1..100 for lossy formats.</param>
    public RenderedImagePage(string name, SixLabors.ImageSharp.Image<Rgba32> canvas, ImageExportFormat format = ImageExportFormat.Png, int quality = 90)
        : base(name, format)
    {
        this.Canvas = canvas;
        this._quality = quality;
    }

    /// <summary>
    /// Gets the rendered image canvas (32-bit RGBA).
    /// </summary>
    public SixLabors.ImageSharp.Image<Rgba32> Canvas { get; }

    /// <inheritdoc/>
    public override void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        switch (this.Format)
        {
            case ImageExportFormat.Bmp:
                this.Canvas.Save(stream, new BmpEncoder());
                break;
            case ImageExportFormat.Jpeg:
                this.Canvas.Save(stream, new JpegEncoder { Quality = this._quality });
                break;
            case ImageExportFormat.Gif:
                this.Canvas.Save(stream, new GifEncoder());
                break;
            case ImageExportFormat.Webp:
                this.Canvas.Save(stream, new WebpEncoder { Quality = this._quality });
                break;
            default:
                this.Canvas.Save(stream, new PngEncoder());
                break;
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        this.Canvas.Dispose();
    }
}
```

(The `<param name="format">` remark about `Svg` becomes true in plan 2; leave it.)

- [ ] **Step 5: Update `ImagePageRenderer.Render`**

```csharp
    public RenderedPage Render(ImagePage page, ImageExportFormat format)
    {
        Image<Rgba32> image = new(this._configuration.Width, this._configuration.Height, this._configuration.BackgroundColor);
        using RasterDrawingSurface surface = new(image, this._configuration, ownsCanvas: false);
        this.RenderTo(surface, page);
        return new RenderedImagePage(page.Name, image, format, this._configuration.OutputQuality);
    }
```

- [ ] **Step 6: Update `ImageExporter`**

Replace `Render`, `Save`, `SaveInternal` and delete `SavePage` and the five `SixLabors.ImageSharp.Formats.*` usings plus `using SixLabors.ImageSharp;` in `ACadSharp.Image/ImageExporter.cs`:

```csharp
    /// <summary>
    /// Renders all added pages without saving to disk.
    /// </summary>
    /// <param name="format">Output format the pages will be saved as. Defaults to PNG.</param>
    /// <returns>Rendered pages; dispose each when finished.</returns>
    public IReadOnlyList<RenderedPage> Render(ImageExportFormat format = ImageExportFormat.Png)
    {
        ImagePageRenderer renderer = new(this.Configuration);
        RenderedPage[] pages = new RenderedPage[this._pages.Count];
        for (int i = 0; i < this._pages.Count; i++)
        {
            pages[i] = renderer.Render(this._pages[i], format);
        }

        return pages;
    }

    /// <summary>
    /// Renders all added pages and saves the output to the specified path.
    /// </summary>
    /// <param name="outputPath">A file path when there is one page, or a directory when there are several.</param>
    /// <param name="format">The output format. Defaults to PNG.</param>
    public void Save(string outputPath, ImageExportFormat format = ImageExportFormat.Png)
    {
        IReadOnlyList<RenderedPage> pages = this.Render(format);

        try
        {
            if (pages.Count == 0)
            {
                throw new InvalidOperationException("There are no pages to export.");
            }

            string fullPath = Path.GetFullPath(outputPath);
            string? extension = Path.GetExtension(fullPath);

            if (pages.Count == 1 && !string.IsNullOrWhiteSpace(extension))
            {
                pages[0].Save(fullPath);
                return;
            }

            string directory = string.IsNullOrWhiteSpace(extension)
                ? fullPath
                : Path.GetDirectoryName(fullPath)!;

            string prefix = string.IsNullOrWhiteSpace(extension)
                ? "page"
                : Path.GetFileNameWithoutExtension(fullPath);

            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Save(Path.Combine(directory, $"{prefix}-{i + 1:D2}-{pages[i].Name}{format.GetFileExtension()}"));
            }
        }
        finally
        {
            foreach (RenderedPage page in pages)
            {
                page.Dispose();
            }
        }
    }
```

Update the class XML summary from "Exports CAD drawings to raster images in various formats." to "Exports CAD drawings to raster images or SVG." and the `<remarks>` reference `Save(string, ImageExportFormat)` stays valid.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test ACadSharp.Image.sln -c Release --nologo -v q`
Expected: PASS, including `SaveMultiplePagesUsesIndexedOutputNames`, the two new tests, and parity.

- [ ] **Step 8: Commit**

```bash
git add ACadSharp.Image ACadSharp.Image.Tests
git commit -m "Add RenderedPage abstraction and format-aware Render

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

---

### Task 6: Entity boundaries with effective layer

**Files:**
- Modify: `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs`
- Create: `ACadSharp.Image.Tests/RecordingDrawingSurface.cs`
- Create: `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`

**Interfaces:**
- Consumes: `IDrawingSurface.BeginEntity(EntityRenderInfo, LayerRenderInfo)`, `EndEntity()`.
- Produces: `EntityRenderDispatcher.Draw(ImageRenderContext context, Entity entity)` (unchanged public shape) now wraps every drawn entity in `BeginEntity`/`EndEntity` and passes nested-entity context; `internal static string GetEffectiveLayerName(Entity entity, string? parentLayerName)`. Plans 2 and 3 rely on `EntityRenderInfo.LayerName` being the effective layer.

- [ ] **Step 1: Write the recording surface and the failing tests**

Create `ACadSharp.Image.Tests/RecordingDrawingSurface.cs`:

```csharp
using ACadSharp.Image.Rendering;

namespace ACadSharp.Image.Tests;

/// <summary>
/// Test double that records surface calls as strings and entity boundaries as infos.
/// </summary>
internal sealed class RecordingDrawingSurface : IDrawingSurface
{
    public List<string> Calls { get; } = new();

    public List<EntityRenderInfo> Entities { get; } = new();

    public List<LayerRenderInfo> Layers { get; } = new();

    public int Depth { get; private set; }

    public bool SupportsCurves { get; init; }

    public void BeginEntity(EntityRenderInfo info, LayerRenderInfo layer)
    {
        this.Depth++;
        this.Entities.Add(info);
        this.Layers.Add(layer);
        this.Calls.Add($"BeginEntity {info.EntityType} layer={info.LayerName} parent={info.ParentHandle?.ToString("X") ?? "-"} block={info.BlockName ?? "-"}");
    }

    public void EndEntity()
    {
        this.Depth--;
        this.Calls.Add("EndEntity");
    }

    public void DrawLine(ImageStyle style, SurfacePoint start, SurfacePoint end) => this.Calls.Add($"DrawLine {start} {end} w={style.StrokeWidth}");

    public void DrawPolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, bool closed) => this.Calls.Add($"DrawPolyline n={points.Count} closed={closed}");

    public void DrawArc(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation, double startAngle, double sweepAngle) => this.Calls.Add($"DrawArc {center} rx={radiusX} ry={radiusY} start={startAngle} sweep={sweepAngle}");

    public void DrawEllipse(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation) => this.Calls.Add($"DrawEllipse {center} rx={radiusX} ry={radiusY}");

    public void DrawCubicBezier(ImageStyle style, IReadOnlyList<SurfacePoint> controlPoints, bool closed) => this.Calls.Add($"DrawCubicBezier n={controlPoints.Count} closed={closed}");

    public void DrawBulgePolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, IReadOnlyList<double> bulges, bool closed) => this.Calls.Add($"DrawBulgePolyline n={points.Count} closed={closed}");

    public void FillPolygon(ImageStyle style, IReadOnlyList<SurfacePoint> points) => this.Calls.Add($"FillPolygon n={points.Count}");

    public void FillPath(ImageStyle style, IReadOnlyList<IReadOnlyList<SurfacePoint>> rings) => this.Calls.Add($"FillPath rings={rings.Count}");

    public void FillCircle(ImageStyle style, SurfacePoint center, double radius) => this.Calls.Add($"FillCircle {center} r={radius}");

    public void DrawText(ImageStyle style, SurfaceText text) => this.Calls.Add($"DrawText '{text.Text}' anchor={text.Anchor} baseline={text.Baseline}");

    public ViewportSurface BeginViewport(SurfaceRect bounds)
    {
        this.Calls.Add($"BeginViewport {bounds}");
        return new ViewportSurface(this, bounds.X, bounds.Y + bounds.Height);
    }

    public void EndViewport(ViewportSurface viewport) => this.Calls.Add("EndViewport");

    public void Dispose()
    {
    }
}
```

Create `ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs`:

```csharp
using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class EntityRenderDispatcherTests
{
    private static ImageRenderContext CreateContext(RecordingDrawingSurface surface, ImageConfiguration configuration)
    {
        Layout layout = new("test") { PaperWidth = 100, PaperHeight = 100 };
        return new ImageRenderContext(surface, configuration, layout, 100, 100, 0, 0, 1d, 0, 0, singlePrecision: false, lineTypeScale: 1d);
    }

    [Fact]
    public void DrawWrapsEntityInBeginAndEnd()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        Line line = new(new XYZ(0, 0, 0), new XYZ(10, 0, 0)) { Layer = new Layer("Walls"), Handle = 0x1F3 };

        dispatcher.Draw(CreateContext(surface, configuration), line);

        Assert.Equal(3, surface.Calls.Count);
        Assert.StartsWith("BeginEntity LINE layer=Walls parent=- block=-", surface.Calls[0]);
        Assert.StartsWith("DrawLine", surface.Calls[1]);
        Assert.Equal("EndEntity", surface.Calls[2]);
        Assert.Equal(0, surface.Depth);
        Assert.Equal(0x1F3UL, surface.Entities[0].Handle);
    }

    [Fact]
    public void NestedEntityOnLayerZeroInheritsInsertLayer()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        BlockRecord block = new("DOOR");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)) { Layer = new Layer(Layer.DefaultName) });
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)) { Layer = new Layer("Hardware") });
        Insert insert = new(block) { Layer = new Layer("Doors"), Handle = 0xAB };

        dispatcher.Draw(CreateContext(surface, configuration), insert);

        // Outer insert, then two nested entities.
        Assert.Equal(3, surface.Entities.Count);
        Assert.Equal("Doors", surface.Entities[0].LayerName);
        Assert.Equal("Doors", surface.Entities[1].LayerName);
        Assert.Equal(0xABUL, surface.Entities[1].ParentHandle);
        Assert.Equal("DOOR", surface.Entities[1].BlockName);
        Assert.Equal("Hardware", surface.Entities[2].LayerName);
        Assert.Equal(0, surface.Depth);
    }

    [Fact]
    public void LayerInfoCarriesLayerColourAndWidth()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        Layer layer = new("Red") { Color = new ACadSharp.Color(1), LineWeight = LineWeightType.W50 };
        Line line = new(new XYZ(0, 0, 0), new XYZ(10, 0, 0)) { Layer = layer };

        dispatcher.Draw(CreateContext(surface, configuration), line);

        LayerRenderInfo info = Assert.Single(surface.Layers);
        Assert.Equal("Red", info.LayerName);
        Assert.Equal(SixLabors.ImageSharp.Color.FromRgb(255, 0, 0), info.Color);
        Assert.Equal(configuration.GetLineWeightPixels(LineWeightType.W50), info.StrokeWidth);
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test ACadSharp.Image.sln -c Release --nologo -v q --filter "FullyQualifiedName~EntityRenderDispatcherTests"`
Expected: FAIL (`Calls.Count` is 1, no `BeginEntity`).

- [ ] **Step 3: Implement entity boundaries in the dispatcher**

In `ACadSharp.Image/Rendering/EntityRenderDispatcher.cs` add `using ACadSharp.Image.Extensions;` (for `ToImageColor`) and change `Draw` into a thin public entry that calls a private recursive method carrying the parent state:

```csharp
    public void Draw(ImageRenderContext context, Entity entity)
    {
        this.Draw(context, entity, parentLayerName: null, parentHandle: null, blockName: null);
    }

    private void Draw(ImageRenderContext context, Entity entity, string? parentLayerName, ulong? parentHandle, string? blockName)
    {
        ImageStyle style = this._styleResolver.Resolve(entity, context);
        string layerName = GetEffectiveLayerName(entity, parentLayerName);
        EntityRenderInfo info = new(layerName, entity.ObjectName, entity.Handle, parentHandle, blockName);
        LayerRenderInfo layerInfo = CreateLayerInfo(entity.Layer, layerName, context);

        context.Surface.BeginEntity(info, layerInfo);
        try
        {
            switch (entity)
            {
                // ... every existing case unchanged, except the two recursive ones:
                case Dimension dimension:
                    this.DrawDimension(context, dimension, layerName);
                    break;
                case Insert insert:
                    this.DrawBlockContents(context, insert, layerName);
                    break;
                // ...
            }
        }
        finally
        {
            context.Surface.EndEntity();
        }
    }

    /// <summary>
    /// Entities on layer "0" inside a block take the layer of the insert that placed them.
    /// </summary>
    internal static string GetEffectiveLayerName(Entity entity, string? parentLayerName)
    {
        string? own = entity.Layer?.Name;
        if (string.IsNullOrEmpty(own))
        {
            return parentLayerName ?? Layer.DefaultName;
        }

        if (parentLayerName != null && string.Equals(own, Layer.DefaultName, StringComparison.Ordinal))
        {
            return parentLayerName;
        }

        return own;
    }

    private static LayerRenderInfo CreateLayerInfo(Layer? layer, string layerName, ImageRenderContext context)
    {
        if (layer == null)
        {
            return new LayerRenderInfo(layerName, SixLabors.ImageSharp.Color.Black, context.ToStrokeWidth(LineWeightType.Default));
        }

        return new LayerRenderInfo(layerName, layer.Color.ToImageColor(), context.ToStrokeWidth(layer.LineWeight));
    }
```

Then update the two recursive helpers so nested entities receive the parent information:

```csharp
    private void DrawDimension(ImageRenderContext context, Dimension dimension, string layerName)
    {
        // ... block lookup unchanged ...
        foreach (Entity entity in block.Entities)
        {
            if (entity is ACadSharp.Entities.Point)
            {
                continue;
            }

            this.Draw(context, entity, layerName, dimension.Handle, blockName: null);
        }
    }

    private void DrawBlockContents(ImageRenderContext context, Insert insert, string layerName)
    {
        foreach (Entity entity in insert.Explode())
        {
            this.Draw(context, entity, layerName, insert.Handle, insert.Block?.Name);
        }
    }
```

- [ ] **Step 4: Run the dispatcher tests, then the whole suite**

Run: `dotnet test ACadSharp.Image.sln -c Release --nologo -v q`
Expected: PASS. Raster is unaffected because `RasterDrawingSurface.BeginEntity/EndEntity` are no-ops, so parity must still be green.

- [ ] **Step 5: Commit**

```bash
git add ACadSharp.Image/Rendering/EntityRenderDispatcher.cs ACadSharp.Image.Tests/RecordingDrawingSurface.cs ACadSharp.Image.Tests/EntityRenderDispatcherTests.cs
git commit -m "Emit entity boundaries with effective layer to the surface

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016RGgSinQSUz4d89FRLxhMz"
```

---

## Self-review checklist (run before handing over)

- Spec coverage for this plan: section 3 (ACadSharp 3.7.1, net6.0 drop, no new deps, parity) → Tasks 1, 2, 4; section 6 (`RenderedPage`, `Render(format)`) → Task 5; section 9 types → Task 3; effective layer rule (section 4.2) → Task 6.
- Every type and member name used in Tasks 4 to 6 (`SurfacePoint`, `ImageStyle.EffectiveColor`, `IDrawingSurface.*`, `ViewportSurface`, `ImageRenderContext.ToSurfacePoint/ToSurfaceLength/ToStrokeWidth/Scale/SinglePrecision`, `SplineRenderer.ShouldClosePoints`, `SplineRenderer.TryGetBezierSegments`, `ImagePageRenderer.RenderTo`, `RenderedPage`, `RenderedImagePage(name, canvas, format, quality)`) is defined in an earlier task of this plan or in spec section 9.
- Parity is checked by `SampleParityTests` after Tasks 2, 3, 4, 5 and 6.
