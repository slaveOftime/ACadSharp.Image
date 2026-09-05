# Test Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the coverage gaps the 2026-09-03 Codex review and coverlet run exposed: paper-space viewports end to end, the CLI, feature goldens that contain hatches/ellipses/opacity/inserts/text, generic spline sampling, text placement, and the small public API surface.

**Architecture:** Tests only, plus two testability changes in the CLI (internal entry points, a `TextWriter` parameter) and one addition to the recording test double (`Texts`). Synthetic drawings are built in code (`SyntheticSamples`), not committed as files; their PNG baselines and SVG goldens live next to the sample baselines and use the same `ACADSHARP_IMAGE_UPDATE_BASELINES=1` switch through a shared `GoldenAssert` helper. Nothing in the library's rendering code changes; if a test exposes a defect, stop and record it in the ledger for a ruling rather than fixing it inside the test task.

**Tech Stack:** .NET 10 test project (xUnit, coverlet.collector), ACadSharp 3.7.1 (`DxfWriter`/`DxfReader` for the in-memory round trip), SixLabors.ImageSharp for pixel assertions, System.Xml.Linq for SVG assertions.

**Spec:** `docs/superpowers/specs/2026-09-02-layers-and-svg-design.md` (binding). This plan adds tests for requirements already implemented; it changes no behaviour described there.

## Global Constraints

- ACadSharp `3.7.1`; SixLabors.ImageSharp `3.1.12`; no new NuGet dependencies (spec section 3).
- Target frameworks unchanged: library `net8.0;net10.0`, CLI and tests `net10.0`.
- Coding conventions: `this.` prefix on instance members, explicit types except LINQ lambdas, XML docs on public members, `sealed` classes, file-scoped namespaces, `internal` for rendering internals with `InternalsVisibleTo` for the test project (spec section 3). New files are UTF-8 without BOM, LF line endings, four-space indent (the repository's de facto convention; `.editorconfig` is not followed by the existing files).
- Existing PNG baselines and SVG goldens under `ACadSharp.Image.Tests/Baselines` must not change. New baselines are created once with `ACADSHARP_IMAGE_UPDATE_BASELINES=1` and committed; the commit message states what they cover.
- Parity and golden tests require the font `DejaVu Sans` (`FontFamilyName = "DejaVu Sans"`), which CI installs via `fonts-dejavu-core`.
- Build must stay warning-free: `dotnet build ACadSharp.Image.sln -warnaserror`.
- No library rendering code is modified by this plan. A test that fails against the current renderer is a finding, not a reason to loosen the test or patch the renderer inside the task.
- Never use bare `git stash` / `git stash pop`. Commit messages end with the two trailer lines the repository uses (see any commit on this branch).

## File Structure

- Create `ACadSharp.Image.Tests/GoldenAssert.cs`: shared PNG-baseline and SVG-golden comparison with the update switch (extracted pattern from `SampleParityTests`, which stays as is).
- Create `ACadSharp.Image.Tests/SyntheticSamples.cs`: builders for the in-memory drawings (`ViewportSheet()`, `FeatureBlock()`).
- Create `ACadSharp.Image.Tests/CliTests.cs`, `ViewportParityTests.cs`, `FeatureGoldenTests.cs`, `SplineRendererTests.cs`, `TextRendererTests.cs`, `RenderedPageTests.cs`.
- Modify `ACadSharp.Image.Cli/Program.cs` (visibility of four methods, `TextWriter` parameter), `ACadSharp.Image.Cli/ACadSharp.Image.Cli.csproj` (`InternalsVisibleTo`), `ACadSharp.Image.Tests/ACadSharp.Image.Tests.csproj` (project reference), `ACadSharp.Image.Tests/RecordingDrawingSurface.cs` (`Texts`), `ACadSharp.Image.Tests/ImageConfigurationTests.cs`.
- New baselines: `ACadSharp.Image.Tests/Baselines/viewport-sheet.paper.01.png`, `viewport-sheet.paper.01.svg`, `features.model.01.png`, `features.model.01.svg`.

---

### Task 1: CLI parser, format resolution and layer table tests

**Files:**
- Modify: `ACadSharp.Image.Cli/ACadSharp.Image.Cli.csproj`
- Modify: `ACadSharp.Image.Cli/Program.cs` (methods `WriteLayerTable`, `ResolveFormat`, `ResolveOutputPath`, `ParseArgs`, and the `Main` call site of `WriteLayerTable`)
- Modify: `ACadSharp.Image.Tests/ACadSharp.Image.Tests.csproj`
- Create: `ACadSharp.Image.Tests/CliTests.cs`

**Interfaces:**
- Consumes: `CliOptions` record (`ACadSharp.Image.Cli/CliOptions.cs`), `ImageExportFormatExtensions`.
- Produces: `internal static CliOptions Program.ParseArgs(IReadOnlyList<string> args)`, `internal static ImageExportFormat Program.ResolveFormat(CliOptions options)`, `internal static string Program.ResolveOutputPath(CliOptions options, string inputPath, ImageExportFormat format)`, `internal static void Program.WriteLayerTable(CadDocument document, TextWriter writer)`.

- [ ] **Step 1: Expose the CLI internals to the test project**

In `ACadSharp.Image.Cli/ACadSharp.Image.Cli.csproj` add, inside the `<Project>` element after the existing `<ItemGroup>`:

```xml
	<ItemGroup>
		<InternalsVisibleTo Include="ACadSharp.Image.Tests" />
	</ItemGroup>
```

In `ACadSharp.Image.Tests/ACadSharp.Image.Tests.csproj` change the project reference group to:

```xml
	<ItemGroup>
		<ProjectReference Include="..\ACadSharp.Image\ACadSharp.Image.csproj" />
		<ProjectReference Include="..\ACadSharp.Image.Cli\ACadSharp.Image.Cli.csproj" />
	</ItemGroup>
```

- [ ] **Step 2: Make the four entry points internal and give the layer table a writer**

In `ACadSharp.Image.Cli/Program.cs`:

- `private static void WriteLayerTable(CadDocument document)` becomes `internal static void WriteLayerTable(CadDocument document, TextWriter writer)`. Inside it, replace every `Console.WriteLine(` with `writer.WriteLine(` (three occurrences: the "No layers." line, the header line, the per-layer line).
- In `Main`, change `WriteLayerTable(document);` to `WriteLayerTable(document, Console.Out);`.
- `private static ImageExportFormat ResolveFormat(CliOptions options)` becomes `internal static ...`.
- `private static string ResolveOutputPath(CliOptions options, string inputPath, ImageExportFormat format)` becomes `internal static ...`.
- `private static CliOptions ParseArgs(IReadOnlyList<string> args)` becomes `internal static ...`.

Add `/// <summary>` XML docs to the four now-internal methods in one line each, e.g. `/// <summary>Parses command-line arguments into <see cref="CliOptions"/>; throws <see cref="InvalidOperationException"/> for unknown or invalid arguments.</summary>`.

Run: `dotnet build ACadSharp.Image.sln -warnaserror --nologo -v q`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Write the failing CLI tests**

Create `ACadSharp.Image.Tests/CliTests.cs`:

```csharp
using ACadSharp.Entities;
using ACadSharp.Image.Cli;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class CliTests
{
    [Fact]
    public void ParseArgsAppliesDefaults()
    {
        CliOptions options = Program.ParseArgs(["plan.dxf"]);

        Assert.Equal("plan.dxf", options.InputPath);
        Assert.Null(options.OutputPath);
        Assert.Null(options.Format);
        Assert.Equal(ImageConfiguration.DefaultWidth, options.Width);
        Assert.Equal(ImageConfiguration.DefaultHeight, options.Height);
        Assert.Equal((0, 0, 0, 0), (options.PaddingLeft, options.PaddingTop, options.PaddingRight, options.PaddingBottom));
        Assert.Equal("white", options.BackgroundColor);
        Assert.Equal(90, options.Quality);
        Assert.False(options.ExportPaperLayouts);
        Assert.Empty(options.HideLayers);
        Assert.Empty(options.OnlyLayers);
        Assert.False(options.SvgNoScalingStroke);
        Assert.False(options.SvgNoEntityAttributes);
        Assert.False(options.SvgEmitSize);
        Assert.Equal(string.Empty, options.SvgIdPrefix);
        Assert.Null(options.SvgPrecision);
        Assert.Null(options.LayerVisibility);
        Assert.False(options.ListLayers);
    }

    [Fact]
    public void ParseArgsReadsEveryOption()
    {
        CliOptions options = Program.ParseArgs([
            "plan.dwg", "-o", "out/plan.svg", "-f", "svg", "-w", "640", "-H", "480", "-p", "1,2,3,4", "-b", "#202020", "-q", "75",
            "--paper-layouts", "--hide-layer", "A-DOOR", "--hide-layer", "A-GLAZ", "--only-layer", "A-WALL", "--only-layer", "A-DOOR",
            "--layer-visibility", "Plot", "--list-layers", "--svg-no-scaling-stroke", "--svg-no-entity-attributes", "--svg-size",
            "--svg-id-prefix", "p1-", "--svg-precision", "3",
        ]);

        Assert.Equal("plan.dwg", options.InputPath);
        Assert.Equal("out/plan.svg", options.OutputPath);
        Assert.Equal("svg", options.Format);
        Assert.Equal(640, options.Width);
        Assert.Equal(480, options.Height);
        Assert.Equal((1, 2, 3, 4), (options.PaddingLeft, options.PaddingTop, options.PaddingRight, options.PaddingBottom));
        Assert.Equal("#202020", options.BackgroundColor);
        Assert.Equal(75, options.Quality);
        Assert.True(options.ExportPaperLayouts);
        Assert.Equal(["A-DOOR", "A-GLAZ"], options.HideLayers);
        Assert.Equal(["A-WALL", "A-DOOR"], options.OnlyLayers);
        Assert.Equal(LayerVisibilityMode.Plot, options.LayerVisibility);
        Assert.True(options.ListLayers);
        Assert.True(options.SvgNoScalingStroke);
        Assert.True(options.SvgNoEntityAttributes);
        Assert.True(options.SvgEmitSize);
        Assert.Equal("p1-", options.SvgIdPrefix);
        Assert.Equal(3, options.SvgPrecision);
    }

    [Theory]
    [InlineData("8", 8, 8, 8, 8)]
    [InlineData("4,6", 4, 6, 4, 6)]
    [InlineData("1,2,3,4", 1, 2, 3, 4)]
    public void ParseArgsAcceptsThePaddingForms(string value, int left, int top, int right, int bottom)
    {
        CliOptions options = Program.ParseArgs(["a.dxf", "--padding", value]);

        Assert.Equal((left, top, right, bottom), (options.PaddingLeft, options.PaddingTop, options.PaddingRight, options.PaddingBottom));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--width", "0")]
    [InlineData("--width", "abc")]
    [InlineData("--quality", "101")]
    [InlineData("--padding", "1,2,3")]
    [InlineData("--padding", "-1")]
    [InlineData("--svg-precision", "9")]
    [InlineData("--layer-visibility", "hidden")]
    [InlineData("--layer-visibility", "1")]
    [InlineData("--output")]
    public void ParseArgsRejectsInvalidArguments(params string[] tail)
    {
        List<string> args = ["a.dxf", .. tail];

        Assert.Throws<InvalidOperationException>(() => Program.ParseArgs(args));
    }

    [Fact]
    public void ParseArgsRequiresAnInputFile()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Program.ParseArgs(["--width", "10"]));

        Assert.Contains("input", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LayerVisibilityIsCaseInsensitiveButNotNumeric()
    {
        Assert.Equal(LayerVisibilityMode.Screen, Program.ParseArgs(["a.dxf", "--layer-visibility", "SCREEN"]).LayerVisibility);
        Assert.Equal(LayerVisibilityMode.All, Program.ParseArgs(["a.dxf", "--layer-visibility", " all "]).LayerVisibility);
        Assert.Throws<InvalidOperationException>(() => Program.ParseArgs(["a.dxf", "--layer-visibility", "2"]));
    }

    [Fact]
    public void ResolveFormatPrefersExplicitThenExtensionThenPng()
    {
        Assert.Equal(ImageExportFormat.Svg, Program.ResolveFormat(Program.ParseArgs(["a.dxf", "-f", "svg", "-o", "x.png"])));
        Assert.Equal(ImageExportFormat.Jpeg, Program.ResolveFormat(Program.ParseArgs(["a.dxf", "-o", "x.jpg"])));
        Assert.Equal(ImageExportFormat.Svg, Program.ResolveFormat(Program.ParseArgs(["a.dxf", "-o", "x.SVG"])));
        Assert.Equal(ImageExportFormat.Png, Program.ResolveFormat(Program.ParseArgs(["a.dxf"])));
        Assert.Equal(ImageExportFormat.Png, Program.ResolveFormat(Program.ParseArgs(["a.dxf", "-o", "outdir"])));
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Program.ResolveFormat(Program.ParseArgs(["a.dxf", "-f", "tiff"])));
        Assert.Contains("tiff", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveOutputPathUsesTheFormatExtensionWhenNoOutputIsGiven()
    {
        string input = Path.Combine(Path.GetTempPath(), "drawing.dxf");

        Assert.Equal(Path.ChangeExtension(input, ".svg"), Program.ResolveOutputPath(Program.ParseArgs([input]), input, ImageExportFormat.Svg));
        Assert.Equal(Path.GetFullPath("out.png"), Program.ResolveOutputPath(Program.ParseArgs([input, "-o", "out.png"]), input, ImageExportFormat.Png));
    }

    [Fact]
    public void WriteLayerTableAlignsColumnsAndCountsModelSpaceEntities()
    {
        CadDocument document = new();
        Layer walls = new("A-WALL-INTERIOR") { Color = new ACadSharp.Color(1), LineWeight = LineWeightType.W50 };
        Layer notes = new("N") { Color = ACadSharp.Color.FromTrueColor(0x10, 0x20, 0x30), IsOn = false, PlotFlag = false };
        document.Layers.Add(walls);
        document.Layers.Add(notes);
        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)) { Layer = walls });
        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)) { Layer = walls });

        StringWriter writer = new();
        Program.WriteLayerTable(document, writer);
        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

        // Header, then "0", "A-WALL-INTERIOR" and "N" sorted case-insensitively.
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("Layer            On   Frozen  Plot  Color        Weight   Linetype  Entities", lines[0]);
        Assert.StartsWith("0                yes  no      yes   7", lines[1]);
        Assert.StartsWith("A-WALL-INTERIOR  yes  no      yes   1            W50", lines[2]);
        Assert.EndsWith("  2", lines[2]);
        Assert.StartsWith("N                no   no      no    #102030", lines[3]);
        Assert.EndsWith("  0", lines[3]);
        // The weight column is as wide as its widest value ("Default" is 7 characters), so every row has the same length.
        Assert.Single(lines.Select(l => l.Length).Distinct());
    }

    [Fact]
    public void WriteLayerTableReportsAnEmptyLayerTable()
    {
        CadDocument document = new();
        document.Layers.Remove(Layer.DefaultName);
        StringWriter writer = new();

        Program.WriteLayerTable(document, writer);

        Assert.Equal("No layers.", writer.ToString().Trim());
    }

    [Fact]
    public void MainReturnsOneForAMissingInputFile()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dxf");

        Assert.Equal(1, Program.Main([missing]));
    }

    [Fact]
    public void MainReturnsZeroForHelp()
    {
        Assert.Equal(0, Program.Main([]));
        Assert.Equal(0, Program.Main(["--help"]));
    }
}
```

Notes for the implementer: `Layer.DefaultName` is `"0"`. If `document.Layers.Remove(Layer.DefaultName)` returns false or throws in ACadSharp 3.7.1 (layer 0 may be protected), replace the `WriteLayerTableReportsAnEmptyLayerTable` body with a document whose only layer is `0` and assert the row count is 2 instead; record the substitution in the report. The header string in `WriteLayerTableAlignsColumnsAndCountsModelSpaceEntities` follows the code exactly: name column padded to the longest name (15), `Weight` padded to the longest weight text (`Default` = 7), `Linetype` padded to 8. Run the test first, and if the expected header differs only in spacing, fix the test string to match the actual output and say so in the report; if it differs in content (missing column, wrong count), that is a finding.

- [ ] **Step 4: Run the tests to verify they fail before the CLI changes are wired**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~CliTests"`
Expected: compilation succeeds only if Steps 1 and 2 are done; with them done, all tests should PASS except any whose expected string does not match (see notes). If a test fails for a reason other than spacing, stop and report.

- [ ] **Step 5: Run the whole suite and commit**

Run: `dotnet build ACadSharp.Image.sln -warnaserror --nologo -v q && dotnet test ACadSharp.Image.sln --nologo -v q`
Expected: 0 warnings; all tests pass (167 + the new CLI tests).

```bash
git add ACadSharp.Image.Cli ACadSharp.Image.Tests/ACadSharp.Image.Tests.csproj ACadSharp.Image.Tests/CliTests.cs
git commit -m "Test the CLI parser, format resolution and layer table"
```

---

### Task 2: Paper-space viewport fixture with PNG baseline and SVG golden

**Files:**
- Create: `ACadSharp.Image.Tests/GoldenAssert.cs`
- Create: `ACadSharp.Image.Tests/SyntheticSamples.cs`
- Create: `ACadSharp.Image.Tests/ViewportParityTests.cs`
- Create (generated): `ACadSharp.Image.Tests/Baselines/viewport-sheet.paper.01.png`, `ACadSharp.Image.Tests/Baselines/viewport-sheet.paper.01.svg`

**Interfaces:**
- Consumes: `SampleParityTests.AssertPixelsEqual(Image<Rgba32>, Image<Rgba32>, string)` and `SampleParityTests.FindRepoRoot()` (both `internal static`), `ImageExporter.Add(Layout)`, `SvgDrawingSurface.Ns`.
- Produces: `internal static class GoldenAssert { static void Png(string baseName, Image<Rgba32> actual); static void Svg(string baseName, string actual); static bool Updating { get; } }` and `internal static class SyntheticSamples { static CadDocument ViewportSheet(); }`. Task 3 adds `FeatureBlock()` to `SyntheticSamples`.

- [ ] **Step 1: Write the golden helper**

Create `ACadSharp.Image.Tests/GoldenAssert.cs`:

```csharp
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ACadSharp.Image.Tests;

/// <summary>
/// Compares rendered output with the files under <c>Baselines/</c>: PNGs pixel-for-pixel and SVGs as text. With the
/// environment variable <c>ACADSHARP_IMAGE_UPDATE_BASELINES=1</c> the files are rewritten instead of compared.
/// </summary>
internal static class GoldenAssert
{
    public static bool Updating => Environment.GetEnvironmentVariable("ACADSHARP_IMAGE_UPDATE_BASELINES") == "1";

    private static string BaselineDirectory
    {
        get
        {
            string directory = Path.Combine(SampleParityTests.FindRepoRoot(), "ACadSharp.Image.Tests", "Baselines");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static void Png(string baseName, Image<Rgba32> actual)
    {
        string path = Path.Combine(BaselineDirectory, baseName + ".png");
        if (Updating)
        {
            actual.Save(path, new PngEncoder());
            return;
        }

        Assert.True(File.Exists(path), $"Missing baseline {path}. Run with ACADSHARP_IMAGE_UPDATE_BASELINES=1 to create it.");
        using Image<Rgba32> expected = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
        SampleParityTests.AssertPixelsEqual(expected, actual, path);
    }

    public static void Svg(string baseName, string actual)
    {
        string path = Path.Combine(BaselineDirectory, baseName + ".svg");
        string normalized = actual.Replace("\r\n", "\n");
        Assert.DoesNotContain("Infinity", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", normalized, StringComparison.Ordinal);
        if (Updating)
        {
            File.WriteAllText(path, normalized);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden {path}. Run with ACADSHARP_IMAGE_UPDATE_BASELINES=1 to create it.");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), normalized);
    }
}
```

- [ ] **Step 2: Write the synthetic viewport sheet**

Create `ACadSharp.Image.Tests/SyntheticSamples.cs`:

```csharp
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

/// <summary>
/// Drawings built in code for the golden tests, so the goldens cover the feature list rather than whatever the sample
/// files happen to contain. <see cref="ViewportSheet"/> is round-tripped through the DXF writer and reader so the
/// document arrives the way a file would (owners, handles and table references wired by the reader).
/// </summary>
internal static class SyntheticSamples
{
    /// <summary>
    /// Model space with lines on layers Walls, Hidden and Grid (dashed) and a circle, plus a layout "Sheet"
    /// (297 x 210) holding a viewport at scale 2 that freezes layer Hidden, a frame line and a title.
    /// </summary>
    public static CadDocument ViewportSheet()
    {
        CadDocument document = new();
        document.Header.LineTypeScale = 1d;

        LineType dashed = new("DASHED");
        dashed.AddSegment(new LineType.Segment { Length = 5 });
        dashed.AddSegment(new LineType.Segment { Length = -2.5 });
        document.LineTypes.Add(dashed);

        Layer walls = new("Walls") { Color = new Color(1) };
        Layer hidden = new("Hidden") { Color = new Color(5) };
        Layer grid = new("Grid") { Color = new Color(3), LineType = dashed };
        document.Layers.Add(walls);
        document.Layers.Add(hidden);
        document.Layers.Add(grid);

        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(100, 0, 0)) { Layer = walls });
        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(0, 60, 0)) { Layer = walls });
        document.Entities.Add(new Line(new XYZ(0, 60, 0), new XYZ(100, 60, 0)) { Layer = hidden });
        document.Entities.Add(new Line(new XYZ(0, 30, 0), new XYZ(100, 30, 0)) { Layer = grid });
        document.Entities.Add(new Circle { Center = new XYZ(50, 30, 0), Radius = 20, Layer = walls });

        // The default "Layout1" would render as a second, empty page.
        document.Layouts.Remove("Layout1");
        Layout sheet = new("Sheet") { PaperWidth = 297, PaperHeight = 210 };
        document.Layouts.Add(sheet);

        Viewport viewport = new()
        {
            Center = new XYZ(148.5, 105, 0),
            Width = 200,
            Height = 120,
            ViewCenter = new XY(50, 30),
            ViewHeight = 60,
        };
        viewport.FrozenLayers.Add(hidden);
        sheet.AssociatedBlock.Entities.Add(viewport);
        sheet.AssociatedBlock.Entities.Add(new Line(new XYZ(10, 10, 0), new XYZ(287, 10, 0)) { Layer = walls });
        sheet.AssociatedBlock.Entities.Add(new TextEntity { Value = "SHEET 1", InsertPoint = new XYZ(10, 190, 0), Height = 8, Layer = walls });

        using MemoryStream stream = new();
        using (DxfWriter writer = new(stream, document, binary: false))
        {
            writer.Write();
        }

        stream.Position = 0;
        return DxfReader.Read(stream, null);
    }
}
```

If `DxfWriter` disposing closes the stream before it can be read, write to a temporary file instead (`Path.Combine(Path.GetTempPath(), $"viewport-{Guid.NewGuid():N}.dxf")`, `new DxfWriter(path, document, false)`, then `DxfReader.Read(path)` and `File.Delete(path)`); both constructor and reader overloads exist in ACadSharp 3.7.1. Record which one was used.

- [ ] **Step 3: Write the failing viewport tests**

Create `ACadSharp.Image.Tests/ViewportParityTests.cs`:

```csharp
using System.Xml.Linq;
using ACadSharp.Image.Rendering.Svg;
using ACadSharp.Objects;
using SixLabors.Fonts;
using SixLabors.ImageSharp.PixelFormats;

namespace ACadSharp.Image.Tests;

public sealed class ViewportParityTests
{
    private const string FontFamily = "DejaVu Sans";
    private static readonly XNamespace Ns = SvgDrawingSurface.Ns;

    private static ImageExporter SheetExporter(out CadDocument document)
    {
        document = SyntheticSamples.ViewportSheet();
        ImageExporter exporter = new();
        exporter.Configuration.Width = 800;
        exporter.Configuration.Height = 500;
        exporter.Configuration.SetPadding(10);
        exporter.Configuration.FontFamilyName = FontFamily;
        Layout sheet = document.Layouts.First(l => l.Name == "Sheet");
        exporter.Add(sheet);
        return exporter;
    }

    [Fact]
    public void SheetRoundTripKeepsTheViewport()
    {
        ImageExporter exporter = SheetExporter(out CadDocument document);
        ImagePage page = Assert.Single(exporter.Pages);

        Assert.Equal(2, page.Entities.Count); // frame line and title; the viewport itself is not an entity of the page
        Assert.Single(page.Viewports);
        Assert.Equal(2d, page.Viewports[0].ScaleFactor, 9);
        Assert.Contains(page.Viewports[0].FrozenLayers, l => l.Name == "Hidden");
        Assert.Equal(1, (int)document.Header.PaperSpaceLineTypeScaling); // PSLTSCALE default: dashes at page scale
    }

    [Fact]
    public void SheetPngMatchesBaseline()
    {
        Assert.True(SystemFonts.TryGet(FontFamily, out _), $"Font '{FontFamily}' must be installed for parity tests.");
        ImageExporter exporter = SheetExporter(out _);

        using RenderedImagePage page = Assert.IsType<RenderedImagePage>(Assert.Single(exporter.Render()));

        GoldenAssert.Png("viewport-sheet.paper.01", page.Canvas);

        // PIXEL PROBES: replace these two lines with concrete coordinates after inspecting the baseline (see notes below).
        Rgba32 white = new(255, 255, 255, 255);
        Assert.NotEqual(white, page.Canvas[page.Canvas.Width / 2, page.Canvas.Height / 2]); // placeholder: something is drawn near the centre
    }

    [Fact]
    public void SheetSvgMatchesGoldenAndClipsTheViewport()
    {
        ImageExporter exporter = SheetExporter(out _);

        RenderedSvgPage page = Assert.IsType<RenderedSvgPage>(Assert.Single(exporter.Render(ImageExportFormat.Svg)));
        GoldenAssert.Svg("viewport-sheet.paper.01", page.Content);

        XDocument document = XDocument.Parse(page.Content);
        XElement clip = Assert.Single(document.Descendants(Ns + "clipPath"));
        Assert.Equal("clip-1", (string?)clip.Attribute("id"));
        XElement viewportGroup = Assert.Single(document.Descendants(Ns + "g"), g => (string?)g.Attribute("class") == "cad-viewport");
        Assert.Equal("url(#clip-1)", (string?)viewportGroup.Attribute("clip-path"));

        // Layer groups inside the viewport carry the clip-scoped ids; the frozen layer is absent altogether.
        List<string> layerIds = viewportGroup.Descendants(Ns + "g").Where(g => (string?)g.Attribute("class") == "cad-layer").Select(g => (string)g.Attribute("id")!).ToList();
        Assert.Contains("clip-1-layer-walls", layerIds);
        Assert.Contains("clip-1-layer-grid", layerIds);
        Assert.DoesNotContain(layerIds, id => id.Contains("hidden", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Descendants(), e => (string?)e.Attribute("data-layer") == "Hidden");

        // The grid line is dashed, at page scale (PSLTSCALE 1): 5 and 2.5 drawing units times the fit scale, in pixels.
        XElement gridLine = Assert.Single(viewportGroup.Descendants(Ns + "line"), l => (string?)l.Parent!.Attribute("data-layer") == "Grid");
        string[] dashes = ((string)gridLine.Attribute("stroke-dasharray")!).Split(' ');
        Assert.Equal(2, dashes.Length);
        Assert.Equal(2d, double.Parse(dashes[0], System.Globalization.CultureInfo.InvariantCulture) / double.Parse(dashes[1], System.Globalization.CultureInfo.InvariantCulture), 3);

        // The circle keeps its native form inside the viewport, and the page-level frame line and title sit outside it.
        Assert.Single(viewportGroup.Descendants(Ns + "circle"));
        Assert.Single(document.Descendants(Ns + "text"));
        Assert.DoesNotContain(viewportGroup.Descendants(Ns + "text"), _ => true);
    }
}
```

Notes for the implementer: the pixel probe in `SheetPngMatchesBaseline` is a placeholder. Before committing, open the generated baseline PNG (`Read` tool on the file) and replace the placeholder with two concrete pixel checks: one background pixel just inside the viewport's top edge (where the frozen "Hidden" line would have been drawn, i.e. a few pixels below the top of the red rectangle's side walls) and one non-background pixel on the red bottom wall line. Geometry to compute them: fit = min(780/297, 480/210) = 2.2857 px/unit; the sheet is 678.9 x 480 px, starting at x = 10 + (780 - 678.9)/2 = 60.5 and y = 10; paper (px, py) maps to canvas (60.5 + px * 2.2857, 10 + (210 - py) * 2.2857); model (mx, my) maps to paper (148.5 + (mx - 50) * 2, 105 + (my - 30) * 2). So model (50, 0) (bottom wall) is paper (148.5, 45) = canvas (400, 387); model (50, 58) (just below the frozen top wall) is paper (148.5, 161) = canvas (400, 122). Verify against the image before using them and state the coordinates in the report.

- [ ] **Step 4: Run the tests and create the baselines**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~ViewportParityTests"`
Expected: `SheetRoundTripKeepsTheViewport` PASS; the two golden tests FAIL with "Missing baseline/golden".

Run: `ACADSHARP_IMAGE_UPDATE_BASELINES=1 dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~ViewportParityTests"`
Expected: PASS, and two new files under `ACadSharp.Image.Tests/Baselines/`.

Inspect `viewport-sheet.paper.01.svg` (it is text) and confirm: a `<clipPath>`, a `cad-viewport` group, layer groups `clip-1-layer-walls` and `clip-1-layer-grid`, no `Hidden`, one `stroke-dasharray`, one `<circle>`, one `<text>`. Inspect the PNG visually with the Read tool: a red rectangle outline missing its top edge, a green dashed line across the middle, a red circle, a frame line near the bottom and a title near the top. If anything is missing, do not commit the baseline: report it as a finding.

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~ViewportParityTests"` (without the variable)
Expected: PASS.

- [ ] **Step 5: Run the whole suite and commit**

Run: `dotnet build ACadSharp.Image.sln -warnaserror --nologo -v q && dotnet test ACadSharp.Image.sln --nologo -v q`
Expected: 0 warnings; all pass; `git status` shows only the new files.

```bash
git add ACadSharp.Image.Tests/GoldenAssert.cs ACadSharp.Image.Tests/SyntheticSamples.cs ACadSharp.Image.Tests/ViewportParityTests.cs ACadSharp.Image.Tests/Baselines/viewport-sheet.paper.01.png ACadSharp.Image.Tests/Baselines/viewport-sheet.paper.01.svg
git commit -m "Add a paper-space viewport fixture with PNG baseline and SVG golden"
```

---

### Task 3: Feature goldens for hatch, ellipse, opacity, insert, bulge and text

**Files:**
- Modify: `ACadSharp.Image.Tests/SyntheticSamples.cs` (add `FeatureBlock()`)
- Create: `ACadSharp.Image.Tests/FeatureGoldenTests.cs`
- Create (generated): `ACadSharp.Image.Tests/Baselines/features.model.01.png`, `ACadSharp.Image.Tests/Baselines/features.model.01.svg`

**Interfaces:**
- Consumes: `GoldenAssert.Png/Svg` (Task 2), `SvgDrawingSurface.Ns`.
- Produces: `SyntheticSamples.FeatureBlock()` returning a `BlockRecord`.

- [ ] **Step 1: Add the feature block builder**

Append to `SyntheticSamples` (inside the class, after `ViewportSheet`):

```csharp
    /// <summary>
    /// One block exercising every primitive the goldens from the sample files do not contain: a solid and a pattern
    /// hatch, a full ellipse and an elliptical arc, a translucent line, an insert with layer-0 and ByBlock contents,
    /// a bulged closed polyline, a two-line MText and a Fit-aligned text.
    /// </summary>
    public static BlockRecord FeatureBlock()
    {
        BlockRecord block = new("features");
        Layer hatchLayer = new("Hatch") { Color = new Color(1) };
        Layer curves = new("Curves") { Color = new Color(4) };
        Layer trans = new("Trans") { Color = new Color(6) };
        Layer doors = new("Doors") { Color = new Color(3) };
        Layer anno = new("Anno") { Color = new Color(7) };

        Hatch solid = new() { IsSolid = true, PatternType = HatchPatternType.SolidFill, Pattern = HatchPattern.Solid, Layer = hatchLayer };
        solid.Paths.Add(SquarePath(0, 0, 20));
        block.Entities.Add(solid);

        Hatch pattern = new() { IsSolid = false, PatternType = HatchPatternType.PatternFill, Pattern = new HatchPattern("ANSI31"), Layer = hatchLayer };
        pattern.Pattern.Lines.Add(new HatchPattern.Line { Angle = Math.PI / 4, BasePoint = XY.Zero, Offset = new XY(0, 3.175) });
        pattern.PatternScale = 1;
        pattern.Paths.Add(SquarePath(30, 0, 20));
        block.Entities.Add(pattern);

        block.Entities.Add(new Ellipse { Center = new XYZ(70, 10, 0), MajorAxisEndPoint = new XYZ(10, 0, 0), RadiusRatio = 0.5, Layer = curves });
        block.Entities.Add(new Ellipse { Center = new XYZ(100, 10, 0), MajorAxisEndPoint = new XYZ(10, 0, 0), RadiusRatio = 0.5, StartParameter = 0, EndParameter = Math.PI, Layer = curves });

        block.Entities.Add(new Line(new XYZ(0, 30, 0), new XYZ(120, 30, 0)) { Layer = trans, Transparency = new Transparency(50), LineWeight = LineWeightType.W100 });

        LwPolyline bulged = new() { IsClosed = true, Layer = curves };
        bulged.Vertices.Add(new LwPolyline.Vertex(new XY(0, 40)) { Bulge = 1 });
        bulged.Vertices.Add(new LwPolyline.Vertex(new XY(20, 40)));
        bulged.Vertices.Add(new LwPolyline.Vertex(new XY(20, 55)));
        block.Entities.Add(bulged);

        BlockRecord door = new("DOOR");
        door.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0)) { Layer = new Layer(Layer.DefaultName) });
        door.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(0, 10, 0)) { Color = Color.ByBlock, LineWeight = LineWeightType.ByBlock });
        block.Entities.Add(new Insert(door) { InsertPoint = new XYZ(40, 40, 0), Layer = doors, Color = new Color(5), LineWeight = LineWeightType.W70 });

        block.Entities.Add(new MText { Value = "Line1\\PLine2", InsertPoint = new XYZ(70, 48, 0), Height = 4, Layer = anno });
        block.Entities.Add(new TextEntity { Value = "FIT", InsertPoint = new XYZ(70, 55, 0), AlignmentPoint = new XYZ(110, 55, 0), HorizontalAlignment = TextHorizontalAlignment.Fit, Height = 4, Layer = anno });

        return block;
    }

    private static Hatch.BoundaryPath SquarePath(double x, double y, double size)
    {
        Hatch.BoundaryPath path = new();
        Hatch.BoundaryPath.Polyline polyline = new() { IsClosed = true };
        polyline.Vertices.AddRange([new XYZ(x, y, 0), new XYZ(x + size, y, 0), new XYZ(x + size, y + size, 0), new XYZ(x, y + size, 0)]);
        path.Edges.Add(polyline);
        return path;
    }
```

- [ ] **Step 2: Write the failing feature golden tests**

Create `ACadSharp.Image.Tests/FeatureGoldenTests.cs`:

```csharp
using System.Xml.Linq;
using ACadSharp.Image.Rendering.Svg;
using SixLabors.Fonts;

namespace ACadSharp.Image.Tests;

public sealed class FeatureGoldenTests
{
    private const string FontFamily = "DejaVu Sans";
    private static readonly XNamespace Ns = SvgDrawingSurface.Ns;

    private static ImageExporter FeatureExporter()
    {
        ImageExporter exporter = new();
        exporter.Configuration.Width = 800;
        exporter.Configuration.Height = 500;
        exporter.Configuration.SetPadding(10);
        exporter.Configuration.FontFamilyName = FontFamily;
        exporter.Add(SyntheticSamples.FeatureBlock());
        return exporter;
    }

    [Fact]
    public void FeaturePngMatchesBaseline()
    {
        Assert.True(SystemFonts.TryGet(FontFamily, out _), $"Font '{FontFamily}' must be installed for parity tests.");
        ImageExporter exporter = FeatureExporter();

        using RenderedImagePage page = Assert.IsType<RenderedImagePage>(Assert.Single(exporter.Render()));

        GoldenAssert.Png("features.model.01", page.Canvas);
    }

    [Fact]
    public void FeatureSvgMatchesGoldenAndContainsEveryPrimitive()
    {
        ImageExporter exporter = FeatureExporter();
        List<NotificationEventArgs> notifications = new();
        exporter.Configuration.OnNotification += (_, e) => notifications.Add(e);

        RenderedSvgPage page = Assert.IsType<RenderedSvgPage>(Assert.Single(exporter.Render(ImageExportFormat.Svg)));
        GoldenAssert.Svg("features.model.01", page.Content);

        Assert.DoesNotContain(notifications, n => n.NotificationType is NotificationType.Warning or NotificationType.NotImplemented);
        XDocument document = XDocument.Parse(page.Content);
        List<XElement> paths = document.Descendants(Ns + "path").ToList();

        // Solid hatch: even-odd filled path. Pattern hatch: several plain lines on layer Hatch.
        Assert.Single(paths, p => (string?)p.Attribute("fill-rule") == "evenodd");
        XElement hatchGroup = Assert.Single(document.Descendants(Ns + "g"), g => (string?)g.Attribute("data-layer") == "Hatch");
        Assert.True(hatchGroup.Elements(Ns + "line").Count() >= 5);

        // Full ellipse and elliptical arc (an A command with rx 10 ry 5).
        Assert.Single(document.Descendants(Ns + "ellipse"));
        Assert.Contains(paths, p => ((string?)p.Attribute("d") ?? string.Empty).Contains("A10 5", StringComparison.Ordinal));

        // Translucent line.
        XElement translucent = Assert.Single(document.Descendants(Ns + "line"), l => (string?)l.Parent!.Attribute("data-layer") == "Trans");
        Assert.Equal("0.5", (string?)translucent.Attribute("opacity"));

        // Bulged closed polyline: a path with an arc command that closes.
        Assert.Contains(paths, p => ((string?)p.Attribute("d") ?? string.Empty) is string d && d.Contains('A') && d.EndsWith('Z') && !d.Contains("A10 5", StringComparison.Ordinal));

        // Insert: two nested lines tagged with the block, in the Doors group, the ByBlock one in the insert's colour (5 = blue).
        List<XElement> doorLines = document.Descendants(Ns + "line").Where(l => (string?)l.Attribute("data-block") == "DOOR").ToList();
        Assert.Equal(2, doorLines.Count);
        Assert.All(doorLines, l => Assert.Equal("Doors", (string?)l.Parent!.Attribute("data-layer")));
        Assert.Contains(doorLines, l => (string?)l.Attribute("stroke") == "#0000ff");
        Assert.All(doorLines, l => Assert.Null(l.Attribute("data-handle")));

        // Text: MText as two tspans, Fit text with textLength.
        List<XElement> texts = document.Descendants(Ns + "text").ToList();
        Assert.Equal(2, texts.Count);
        Assert.Contains(texts, t => t.Elements(Ns + "tspan").Select(s => s.Value).SequenceEqual(["Line1", "Line2"]));
        XElement fit = Assert.Single(texts, t => t.Value == "FIT");
        Assert.Equal("40", (string?)fit.Attribute("textLength"));
        Assert.Equal("middle", (string?)fit.Attribute("text-anchor"));
    }
}
```

Notes for the implementer: the SVG `d` strings are formatted with adaptive precision; if `"A10 5"` does not match because of the exact formatting (e.g. `A10 5 0 0 1`), read the golden, find the elliptical arc path and adjust the substring to the smallest distinctive form (`"A10 5"` should hold since rx = 10 and ry = 5 are integers). If the notification assertion fails because the pattern hatch or the Fit text raises a warning, report the warning text: that is a finding, not a test bug.

- [ ] **Step 3: Run, create the baselines, inspect, rerun**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~FeatureGoldenTests"`
Expected: both FAIL with "Missing baseline/golden".

Run: `ACADSHARP_IMAGE_UPDATE_BASELINES=1 dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~FeatureGoldenTests"`
Expected: PASS, two new files.

Inspect the PNG with the Read tool: a red filled square and a red diagonally hatched square along the bottom, a cyan ellipse and half-ellipse to their right, a translucent magenta horizontal line, a cyan D-shaped closed polyline, a green/blue L-shaped door symbol, two lines of text and the word FIT stretched. Confirm the SVG assertions pass without the update variable:

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~FeatureGoldenTests"`
Expected: PASS.

- [ ] **Step 4: Run the whole suite and commit**

Run: `dotnet build ACadSharp.Image.sln -warnaserror --nologo -v q && dotnet test ACadSharp.Image.sln --nologo -v q`
Expected: 0 warnings; all pass.

```bash
git add ACadSharp.Image.Tests/SyntheticSamples.cs ACadSharp.Image.Tests/FeatureGoldenTests.cs ACadSharp.Image.Tests/Baselines/features.model.01.png ACadSharp.Image.Tests/Baselines/features.model.01.svg
git commit -m "Add feature goldens covering hatches, ellipses, opacity, inserts and text"
```

---

### Task 4: Spline sampling and text placement unit tests

**Files:**
- Modify: `ACadSharp.Image.Tests/RecordingDrawingSurface.cs` (add `Texts`)
- Create: `ACadSharp.Image.Tests/SplineRendererTests.cs`
- Create: `ACadSharp.Image.Tests/TextRendererTests.cs`

**Interfaces:**
- Consumes: `SplineRenderer.EvaluateSplinePoint(int, IReadOnlyList<double>, IReadOnlyList<XYZ>, IReadOnlyList<double>, double)` (internal static), `EntityRenderDispatcher.Draw(ImageRenderContext, Entity)`, `RecordingDrawingSurface.Polylines`, `SurfaceText`.
- Produces: `RecordingDrawingSurface.Texts` (`List<SurfaceText>`).

- [ ] **Step 1: Record text runs in the test double**

In `ACadSharp.Image.Tests/RecordingDrawingSurface.cs`, after the `FillPaths` property add:

```csharp
    /// <summary>Every text run handed to DrawText, in order.</summary>
    public List<SurfaceText> Texts { get; } = new();
```

and in `DrawText`, after the `Calls.Add(...)` line, add `this.Texts.Add(text);`.

- [ ] **Step 2: Write the failing spline tests**

Create `ACadSharp.Image.Tests/SplineRendererTests.cs`:

```csharp
using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Objects;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class SplineRendererTests
{
    private static ImageRenderContext Context(IDrawingSurface surface, ImageConfiguration configuration)
    {
        Layout layout = new("t") { PaperWidth = 100, PaperHeight = 100 };
        return new ImageRenderContext(surface, configuration, layout, 100, 100, 0, 0, 1d, 0, 0, singlePrecision: false, lineTypeScale: 1d);
    }

    // Degree 3, 6 control points, clamped uniform knots: not Bezier-form (interior knots have multiplicity 1).
    private static Spline ClampedUniformCubic()
    {
        Spline spline = new() { Degree = 3 };
        spline.Knots.AddRange([0d, 0d, 0d, 0d, 1d, 2d, 3d, 3d, 3d, 3d]);
        spline.ControlPoints.AddRange([new XYZ(0, 0, 0), new XYZ(1, 3, 0), new XYZ(3, 4, 0), new XYZ(5, 1, 0), new XYZ(7, 3, 0), new XYZ(9, 0, 0)]);
        return spline;
    }

    [Fact]
    public void NonBezierSplineIsSampledOnSurfacesWithoutCurves()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new() { ArcPrecision = 16 };
        EntityRenderDispatcher dispatcher = new(configuration);
        Spline spline = ClampedUniformCubic();

        dispatcher.Draw(Context(surface, configuration), spline);

        // 3 knot spans x 16 = 48 steps -> 49 points (ArcPrecision 16 is below that floor).
        IReadOnlyList<SurfacePoint> points = Assert.Single(surface.Polylines);
        Assert.Equal(49, points.Count);
        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("DrawCubicBezier", StringComparison.Ordinal));

        // Endpoints are the clamped control points; the midpoint is the de Boor evaluation at t = 1.5 (Y flipped by the context).
        Assert.Equal(0d, points[0].X, 9);
        Assert.Equal(100d, points[0].Y, 9);
        Assert.Equal(9d, points[^1].X, 9);
        XY mid = SplineRenderer.EvaluateSplinePoint(3, spline.Knots, spline.ControlPoints, spline.Weights, 1.5);
        Assert.Equal(mid.X, points[24].X, 9);
        Assert.Equal(100d - mid.Y, points[24].Y, 9);
    }

    [Fact]
    public void RationalSplineIsSampledEvenOnCurveSurfaces()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = true };
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);
        Spline spline = ClampedUniformCubic();
        spline.Weights.AddRange([1d, 2d, 1d, 2d, 1d, 1d]);

        dispatcher.Draw(Context(surface, configuration), spline);

        // Bezier conversion refuses rational splines, so the curve-capable surface still receives a polyline.
        Assert.Single(surface.Polylines);
        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("DrawCubicBezier", StringComparison.Ordinal));
        // Weighting pulls the curve toward the heavier control points: the midpoint moves compared with the unweighted spline.
        XY weighted = SplineRenderer.EvaluateSplinePoint(3, spline.Knots, spline.ControlPoints, spline.Weights, 1.5);
        XY unweighted = SplineRenderer.EvaluateSplinePoint(3, spline.Knots, spline.ControlPoints, [], 1.5);
        Assert.NotEqual(unweighted.Y, weighted.Y);
    }

    [Fact]
    public void QuadraticSplineIsSampled()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = true };
        ImageConfiguration configuration = new() { ArcPrecision = 8 };
        EntityRenderDispatcher dispatcher = new(configuration);
        Spline spline = new() { Degree = 2 };
        spline.Knots.AddRange([0d, 0d, 0d, 1d, 2d, 2d, 2d]);
        spline.ControlPoints.AddRange([new XYZ(0, 0, 0), new XYZ(2, 4, 0), new XYZ(4, 0, 0), new XYZ(6, 4, 0)]);

        dispatcher.Draw(Context(surface, configuration), spline);

        IReadOnlyList<SurfacePoint> points = Assert.Single(surface.Polylines);
        Assert.Equal(33, points.Count); // 2 spans x 16 = 32 steps
        Assert.Equal(6d, points[^1].X, 9);
    }

    [Fact]
    public void BezierFormSplineStaysNativeOnCurveSurfaces()
    {
        RecordingDrawingSurface surface = new() { SupportsCurves = true };
        ImageConfiguration configuration = new();
        EntityRenderDispatcher dispatcher = new(configuration);

        dispatcher.Draw(Context(surface, configuration), ClampedUniformCubic());

        // Knot insertion turns the clamped cubic into 3 Bezier segments: 10 control points.
        Assert.Contains(surface.Calls, c => c.StartsWith("DrawCubicBezier n=10", StringComparison.Ordinal));
        Assert.Empty(surface.Polylines);
    }

    [Fact]
    public void InconsistentSplineWarnsAndDrawsNothing()
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        List<NotificationEventArgs> notifications = new();
        configuration.OnNotification += (_, e) => notifications.Add(e);
        EntityRenderDispatcher dispatcher = new(configuration);
        Spline spline = new() { Degree = 3 };
        spline.Knots.AddRange([0d, 0d, 1d, 1d]); // wrong knot count for 4 control points of degree 3
        spline.ControlPoints.AddRange([new XYZ(0, 0, 0), new XYZ(1, 1, 0), new XYZ(2, 0, 0), new XYZ(3, 1, 0)]);

        dispatcher.Draw(Context(surface, configuration), spline);

        Assert.Empty(surface.Polylines);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Warning && n.Message.Contains("spline", StringComparison.OrdinalIgnoreCase));
    }
}
```

Notes: `InconsistentSplineWarnsAndDrawsNothing` reaches the renderer's last resort, ACadSharp's `TryPolygonalVertexes`. If ACadSharp manages to produce points for that malformed spline and the renderer draws a polyline, replace the two assertions with `Assert.True(surface.Polylines.Count <= 1)` plus a comment naming the ACadSharp behaviour, and report it.

- [ ] **Step 3: Write the failing text placement tests**

Create `ACadSharp.Image.Tests/TextRendererTests.cs`:

```csharp
using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Objects;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class TextRendererTests
{
    private static (RecordingDrawingSurface Surface, ImageRenderContext Context, EntityRenderDispatcher Dispatcher) Setup(double scale = 1d)
    {
        RecordingDrawingSurface surface = new();
        ImageConfiguration configuration = new();
        Layout layout = new("t") { PaperWidth = 100, PaperHeight = 100 };
        ImageRenderContext context = new(surface, configuration, layout, 100, 100, 0, 0, scale, 0, 0, singlePrecision: false, lineTypeScale: scale);
        return (surface, context, new EntityRenderDispatcher(configuration));
    }

    [Fact]
    public void FitTextIsCentredBetweenInsertAndAlignmentPointsWithAFixedLength()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup(scale: 2d);
        TextEntity text = new() { Value = "FIT", InsertPoint = new XYZ(10, 20, 0), AlignmentPoint = new XYZ(40, 20, 0), HorizontalAlignment = TextHorizontalAlignment.Fit, Height = 5 };

        dispatcher.Draw(context, text);

        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal(SurfaceTextAnchor.Middle, run.Anchor);
        Assert.Equal(SurfaceTextBaseline.Alphabetic, run.Baseline);
        Assert.Equal(60d, run.FixedLength, 9);           // 30 drawing units x scale 2
        Assert.Equal(80d, run.Origin.X, 9);              // origin is the alignment point for anything but Left/Baseline
        Assert.Equal(100d - 40d, run.Origin.Y, 9);
        Assert.Equal(10d, run.Height, 9);
    }

    [Fact]
    public void AlignedTextWithCoincidentPointsHasNoFixedLength()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();
        TextEntity text = new() { Value = "A", InsertPoint = new XYZ(1, 1, 0), AlignmentPoint = new XYZ(1, 1, 0), HorizontalAlignment = TextHorizontalAlignment.Aligned, Height = 2 };

        dispatcher.Draw(context, text);

        Assert.Equal(-1d, Assert.Single(surface.Texts).FixedLength);
    }

    [Theory]
    [InlineData(TextHorizontalAlignment.Left, TextVerticalAlignmentType.Baseline, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, 1d)]
    [InlineData(TextHorizontalAlignment.Center, TextVerticalAlignmentType.Baseline, SurfaceTextAnchor.Middle, SurfaceTextBaseline.Alphabetic, 9d)]
    [InlineData(TextHorizontalAlignment.Right, TextVerticalAlignmentType.Top, SurfaceTextAnchor.End, SurfaceTextBaseline.Hanging, 9d)]
    [InlineData(TextHorizontalAlignment.Middle, TextVerticalAlignmentType.Middle, SurfaceTextAnchor.Middle, SurfaceTextBaseline.Central, 9d)]
    [InlineData(TextHorizontalAlignment.Left, TextVerticalAlignmentType.Bottom, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, 9d)]
    public void TextAlignmentMapsToAnchorBaselineAndOrigin(TextHorizontalAlignment horizontal, TextVerticalAlignmentType vertical, SurfaceTextAnchor anchor, SurfaceTextBaseline baseline, double expectedOriginX)
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();
        TextEntity text = new() { Value = "T", InsertPoint = new XYZ(1, 0, 0), AlignmentPoint = new XYZ(9, 0, 0), HorizontalAlignment = horizontal, VerticalAlignment = vertical, Height = 2 };

        dispatcher.Draw(context, text);

        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal(anchor, run.Anchor);
        Assert.Equal(baseline, run.Baseline);
        Assert.Equal(expectedOriginX, run.Origin.X, 9);
    }

    [Theory]
    [InlineData(AttachmentPointType.TopLeft, SurfaceTextAnchor.Start, SurfaceTextBaseline.Hanging)]
    [InlineData(AttachmentPointType.TopCenter, SurfaceTextAnchor.Middle, SurfaceTextBaseline.Hanging)]
    [InlineData(AttachmentPointType.MiddleRight, SurfaceTextAnchor.End, SurfaceTextBaseline.Central)]
    [InlineData(AttachmentPointType.BottomCenter, SurfaceTextAnchor.Middle, SurfaceTextBaseline.Alphabetic)]
    [InlineData(AttachmentPointType.BottomLeft, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic)]
    public void MTextAttachmentMapsToAnchorAndBaseline(AttachmentPointType attachment, SurfaceTextAnchor anchor, SurfaceTextBaseline baseline)
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();
        MText text = new() { Value = "M", InsertPoint = new XYZ(5, 5, 0), Height = 3, AttachmentPoint = attachment, RectangleWidth = 40, LineSpacing = 1.5 };

        dispatcher.Draw(context, text);

        SurfaceText run = Assert.Single(surface.Texts);
        Assert.Equal(anchor, run.Anchor);
        Assert.Equal(baseline, run.Baseline);
        Assert.Equal(40d, run.WrappingWidth, 9);
        Assert.Equal(1.5d, run.LineSpacingFactor, 9);
        Assert.Equal(-1d, run.FixedLength);
    }

    [Fact]
    public void MTextWithoutRectangleWidthDoesNotWrap()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();

        dispatcher.Draw(context, new MText { Value = "M", InsertPoint = new XYZ(5, 5, 0), Height = 3, RectangleWidth = 0 });

        Assert.Equal(-1d, Assert.Single(surface.Texts).WrappingWidth);
    }

    [Fact]
    public void ControlCodesAreExpandedAndParagraphsBecomeLines()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();

        dispatcher.Draw(context, new TextEntity { Value = "%%c20 %%d %%p1", InsertPoint = new XYZ(0, 0, 0), Height = 2 });
        dispatcher.Draw(context, new MText { Value = "First\\PSecond", InsertPoint = new XYZ(0, 0, 0), Height = 2 });

        Assert.Equal(2, surface.Texts.Count);
        Assert.Equal("Ø20 ° ±1", surface.Texts[0].Text);
        Assert.Equal("First\nSecond", surface.Texts[1].Text);
    }

    [Fact]
    public void BlankTextDrawsNothing()
    {
        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();

        dispatcher.Draw(context, new TextEntity { Value = "   ", InsertPoint = new XYZ(0, 0, 0), Height = 2 });
        dispatcher.Draw(context, new MText { Value = string.Empty, InsertPoint = new XYZ(0, 0, 0), Height = 2 });

        Assert.Empty(surface.Texts);
        Assert.Equal(4, surface.Calls.Count); // two Begin/End pairs, no DrawText
    }
}
```

Notes: `MText.PlainText` in ACadSharp 3.7.1 already turns `\P` into a newline, and `NormalizeText` leaves newlines alone, so `"First\nSecond"` is the expected value. In the alignment theory the `Left/Bottom` row expects the alignment point (X 9) because only `Left` + `Baseline` uses the insert point. If ACadSharp's `TextEntity` setter for `HorizontalAlignment` also moves the origin or the enum lacks a member named exactly as written, report it rather than guessing.

- [ ] **Step 4: Run the new tests**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~SplineRendererTests|FullyQualifiedName~TextRendererTests"`
Expected: all PASS (these test existing behaviour; a failure is a finding to report, with the actual values).

- [ ] **Step 5: Run the whole suite and commit**

Run: `dotnet build ACadSharp.Image.sln -warnaserror --nologo -v q && dotnet test ACadSharp.Image.sln --nologo -v q`
Expected: 0 warnings; all pass.

```bash
git add ACadSharp.Image.Tests/RecordingDrawingSurface.cs ACadSharp.Image.Tests/SplineRendererTests.cs ACadSharp.Image.Tests/TextRendererTests.cs
git commit -m "Test spline sampling and text placement through the dispatcher"
```

---

### Task 5: Small public API and unit conversion tests

**Files:**
- Modify: `ACadSharp.Image.Tests/ImageConfigurationTests.cs` (append tests)
- Create: `ACadSharp.Image.Tests/RenderedPageTests.cs`

**Interfaces:**
- Consumes: `ImageConfiguration.HideLayers/IncludeLayers/SetLineWeight/RemoveLineWeight/ClearLineWeights/GetLineWeightMillimeters`, `RenderedImagePage(string name, Image<Rgba32> canvas, ImageExportFormat format, int quality)` (check the constructor signature in `RenderedImagePage.cs` before writing; adjust the call if it differs and report), `RenderedPage.Save(string)`, `ImageRenderContext.UnitsPerMillimeter(UnitsType)` (internal static), `ImageExportFormatExtensions.GetFileExtension/TryParse/TryParseFileExtension`, `ImagePage.Add(BlockRecord, Func<Entity, bool>?, bool)`.

- [ ] **Step 1: Append configuration tests**

Append to the class in `ACadSharp.Image.Tests/ImageConfigurationTests.cs` (before its closing brace):

```csharp
    [Fact]
    public void HideLayersAddsSeveralNamesCaseInsensitively()
    {
        ImageConfiguration configuration = new();

        configuration.HideLayers(["A-DOOR", "a-door", "A-GLAZ"]);

        Assert.Equal(2, configuration.HiddenLayers.Count);
        Assert.Contains("A-DOOR", configuration.HiddenLayers);
        Assert.Contains("a-glaz", configuration.HiddenLayers);
        Assert.True(configuration.ShowLayer("A-Door"));
        Assert.False(configuration.ShowLayer("A-Door"));
        Assert.Throws<ArgumentException>(() => configuration.HideLayers([" "]));
    }

    [Fact]
    public void IncludedLayersBehavesAsAReadOnlySet()
    {
        ImageConfiguration configuration = new();
        configuration.IncludeLayers(["Walls", "Doors"]);
        IReadOnlySet<string> included = configuration.IncludedLayers;

        Assert.Equal(2, included.Count);
        Assert.True(included.Contains("walls"));
        Assert.True(included.IsSubsetOf(["WALLS", "DOORS", "Glazing"]));
        Assert.True(included.IsProperSubsetOf(["WALLS", "DOORS", "Glazing"]));
        Assert.True(included.IsSupersetOf(["doors"]));
        Assert.True(included.IsProperSupersetOf(["doors"]));
        Assert.True(included.Overlaps(["Doors", "Roof"]));
        Assert.True(included.SetEquals(["DOORS", "WALLS"]));
        Assert.Equal(2, included.Count());
        Assert.True(configuration.ExcludeLayer("WALLS"));
        Assert.False(included.Contains("Walls"));
    }

    [Fact]
    public void LineWeightOverridesValidateAndFallBackToDefaults()
    {
        ImageConfiguration configuration = new();
        double defaultW50 = configuration.GetLineWeightMillimeters(ACadSharp.LineWeightType.W50);

        configuration.SetLineWeight(ACadSharp.LineWeightType.W50, 1.25);
        Assert.Equal(1.25, configuration.GetLineWeightMillimeters(ACadSharp.LineWeightType.W50));
        Assert.Throws<ArgumentOutOfRangeException>(() => configuration.SetLineWeight(ACadSharp.LineWeightType.W50, -0.1));
        Assert.True(configuration.RemoveLineWeight(ACadSharp.LineWeightType.W50));
        Assert.False(configuration.RemoveLineWeight(ACadSharp.LineWeightType.W50));
        Assert.Equal(defaultW50, configuration.GetLineWeightMillimeters(ACadSharp.LineWeightType.W50));
        Assert.Equal(0d, configuration.GetLineWeightMillimeters(ACadSharp.LineWeightType.ByLayer));
        configuration.SetLineWeight(ACadSharp.LineWeightType.Default, 0d);
        Assert.Equal(Math.Max(1f, configuration.LineWeightScale), configuration.GetLineWeightPixels(ACadSharp.LineWeightType.Default));
        configuration.ClearLineWeights();
        Assert.Equal(defaultW50, configuration.GetLineWeightMillimeters(ACadSharp.LineWeightType.W50));
    }
```

If `HideLayers([" "])` does not throw `ArgumentException` in the current implementation (check `HideLayer` in `ImageConfiguration.cs`), replace that line with whatever the code actually does for blank names, and state it in the report.

- [ ] **Step 2: Write the rendered-page, format and unit tests**

Create `ACadSharp.Image.Tests/RenderedPageTests.cs`:

```csharp
using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ACadSharp.Image.Tests;

public sealed class RenderedPageTests
{
    [Theory]
    [InlineData(ImageExportFormat.Png, new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(ImageExportFormat.Bmp, new byte[] { 0x42, 0x4D })]
    [InlineData(ImageExportFormat.Jpeg, new byte[] { 0xFF, 0xD8, 0xFF })]
    [InlineData(ImageExportFormat.Gif, new byte[] { 0x47, 0x49, 0x46, 0x38 })]
    [InlineData(ImageExportFormat.Webp, new byte[] { 0x52, 0x49, 0x46, 0x46 })]
    public void RasterPagesEncodeInTheirFormat(ImageExportFormat format, byte[] signature)
    {
        using Image<Rgba32> canvas = new(8, 8, Color.White);
        using RenderedImagePage page = new("p", canvas, format, 80);
        using MemoryStream stream = new();

        page.Save(stream);

        byte[] bytes = stream.ToArray();
        Assert.True(bytes.Length > signature.Length);
        Assert.Equal(signature, bytes.Take(signature.Length).ToArray());
    }

    [Fact]
    public void SaveToPathCreatesTheDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"acad-image-{Guid.NewGuid():N}", "nested");
        string path = Path.Combine(directory, "page.svg");
        try
        {
            using RenderedSvgPage page = new("p", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");

            page.Save(path);

            Assert.True(File.Exists(path));
            byte[] bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "SVG files must be written without a BOM.");
            Assert.StartsWith("<svg", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(directory)!))
            {
                Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(ImageExportFormat.Png, ".png")]
    [InlineData(ImageExportFormat.Bmp, ".bmp")]
    [InlineData(ImageExportFormat.Jpeg, ".jpg")]
    [InlineData(ImageExportFormat.Gif, ".gif")]
    [InlineData(ImageExportFormat.Webp, ".webp")]
    [InlineData(ImageExportFormat.Svg, ".svg")]
    public void EveryFormatHasAnExtensionAndRoundTripsThroughParsing(ImageExportFormat format, string extension)
    {
        Assert.Equal(extension, format.GetFileExtension());
        Assert.True(ImageExportFormatExtensions.TryParseFileExtension(extension, out ImageExportFormat fromExtension));
        Assert.Equal(format, fromExtension);
        Assert.True(ImageExportFormatExtensions.TryParse(extension.TrimStart('.').ToUpperInvariant(), out ImageExportFormat fromName));
        Assert.Equal(format, fromName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tiff")]
    [InlineData(".tiff")]
    public void UnknownFormatsDoNotParse(string? value)
    {
        Assert.False(ImageExportFormatExtensions.TryParse(value, out _));
        Assert.False(ImageExportFormatExtensions.TryParseFileExtension(value, out _));
    }

    [Theory]
    [InlineData(UnitsType.Millimeters, 1d)]
    [InlineData(UnitsType.Centimeters, 0.1d)]
    [InlineData(UnitsType.Decimeters, 0.01d)]
    [InlineData(UnitsType.Meters, 0.001d)]
    [InlineData(UnitsType.Kilometers, 0.000001d)]
    [InlineData(UnitsType.Microns, 1000d)]
    [InlineData(UnitsType.Inches, 1d / 25.4d)]
    [InlineData(UnitsType.Feet, 1d / 304.8d)]
    [InlineData(UnitsType.Yards, 1d / 914.4d)]
    [InlineData(UnitsType.Miles, 1d / 1609344d)]
    [InlineData(UnitsType.Unitless, 1d)]
    [InlineData(UnitsType.Parsecs, 1d)]
    public void DrawingUnitsPerMillimetreCoverTheHeaderUnits(UnitsType units, double expected)
    {
        Assert.Equal(expected, ImageRenderContext.UnitsPerMillimeter(units), 12);
    }

    [Fact]
    public void PageEntityFilterIsAppliedAtAddTime()
    {
        BlockRecord block = new("filtered");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)) { Layer = new Layer("Keep") });
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)) { Layer = new Layer("Drop") });
        ImagePage page = new();

        page.Add(block, e => e.Layer.Name == "Keep");

        Assert.Single(page.Entities);
        Assert.Equal("Keep", page.Entities[0].Layer.Name);
        Assert.True(page.AutoSized);
        Assert.Equal(1d, page.Layout!.PaperWidth); // extents 1 x 0 are clamped to at least 1 unit
        Assert.Equal(1d, page.Layout.PaperHeight);
    }
}
```

Notes: if `UnitsType.Parsecs` does not exist in ACadSharp 3.7.1, use any member of `UnitsType` that is not in the switch (`UnitsType.Angstroms` or `UnitsType.Nanometers`), and if the `RenderedImagePage` constructor takes different parameters, adapt the call. Report both substitutions.

- [ ] **Step 3: Run the new tests**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~RenderedPageTests|FullyQualifiedName~ImageConfigurationTests"`
Expected: PASS.

- [ ] **Step 4: Run the whole suite, check coverage, commit**

Run: `dotnet build ACadSharp.Image.sln -warnaserror --nologo -v q && dotnet test ACadSharp.Image.sln --nologo -v q`
Expected: 0 warnings; all pass.

Run: `dotnet test ACadSharp.Image.Tests --nologo -v q --collect:"XPlat Code Coverage" --results-directory /tmp/claude-1000/-work-workspaces-orca-ACadSharp-Image-svg-support/f63cfd24-08c1-4c72-b680-352d15a25a0a/scratchpad/coverage-after`
Expected: a `coverage.cobertura.xml`; report its `line-rate` and `branch-rate` attributes from the root element (before this plan: 85.8% lines, 76.4% branches).

```bash
git add ACadSharp.Image.Tests/ImageConfigurationTests.cs ACadSharp.Image.Tests/RenderedPageTests.cs
git commit -m "Test the configuration set API, rendered page encoders, formats and units"
```
