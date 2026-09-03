using ACadSharp.Entities;
using ACadSharp.Image.Cli;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

/// <summary>Tests the CLI's argument parsing, format resolution, layer table and entry point through captured writers.</summary>
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
        Layer notes = new("N") { Color = new ACadSharp.Color(0x10, 0x20, 0x30), IsOn = false, PlotFlag = false };
        document.Layers.Add(walls);
        document.Layers.Add(notes);
        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)) { Layer = walls });
        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)) { Layer = walls });

        StringWriter writer = new();
        Program.WriteLayerTable(document, writer);
        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

        // Header, then "0", "A-WALL-INTERIOR" and "N" sorted case-insensitively.
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("Layer            On   Frozen  Plot  Color        Weight   Linetype    Entities", lines[0]);
        Assert.StartsWith("0                yes  no      yes   7", lines[1]);
        Assert.StartsWith("A-WALL-INTERIOR  yes  no      yes   1            W50", lines[2]);
        Assert.EndsWith("  2", lines[2]);
        Assert.StartsWith("N                no   no      no    #102030", lines[3]);
        Assert.EndsWith("  0", lines[3]);
        // Every column except the trailing entity count is padded to a fixed width, and every count here ("7",
        // "2", "0") is a single digit, so all three data rows come out the same length.
        Assert.Single(lines.Skip(1).Select(l => l.Length).Distinct());
    }

    [Fact]
    public void RunReturnsOneAndReportsAMissingInputFileOnTheErrorWriter()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dxf");
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = Program.Run([missing], output, error);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.StartsWith("Error: Input file was not found.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunWritesHelpToTheOutputWriterAndReturnsZero()
    {
        StringWriter output = new();
        StringWriter error = new();

        Assert.Equal(0, Program.Run([], output, error));
        Assert.Equal(0, Program.Run(["--help"], output, error));

        Assert.Contains("Usage:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--list-layers", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void RunRejectsAnUnknownFormatBeforeReadingTheDocument()
    {
        string repoRoot = SampleParityTests.FindRepoRoot();
        string sample = Path.Combine(repoRoot, "Samples", "6-57-1119.dxf");
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = Program.Run([sample, "-f", "tiff"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unsupported output format 'tiff'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunListsLayersOnTheOutputWriterWithoutRendering()
    {
        string repoRoot = SampleParityTests.FindRepoRoot();
        string sample = Path.Combine(repoRoot, "Samples", "6-57-1119.dxf");
        string outputPath = Path.Combine(Path.GetTempPath(), $"not-written-{Guid.NewGuid():N}.png");
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = Program.Run([sample, "--list-layers", "-o", outputPath], output, error);

        Assert.Equal(0, exitCode);
        Assert.StartsWith("Layer", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath));
    }
}
