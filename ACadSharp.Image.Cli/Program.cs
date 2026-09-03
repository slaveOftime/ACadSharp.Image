using System.Globalization;
using ACadSharp.IO;

namespace ACadSharp.Image.Cli;

internal static class Program
{
    /// <summary>Entry point: runs the tool against the console.</summary>
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>
    /// Runs the tool with explicit writers so the output can be captured; <see cref="Main"/> passes the console.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="output">Receives help, the layer table and the success line.</param>
    /// <param name="error">Receives reader and renderer notifications and the error line.</param>
    /// <returns>0 on success, 1 on any handled error.</returns>
    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Length == 0 || args.Any(IsHelpArgument))
            {
                WriteHelp(output);
                return 0;
            }

            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            CliOptions options = ParseArgs(args);
            string inputPath = Path.GetFullPath(options.InputPath);
            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException("Input file was not found.", inputPath);
            }

            // The format is resolved before the document is read, so a bad --format fails fast instead of
            // after a long DWG parse.
            ImageExportFormat format = ResolveFormat(options);

            CadDocument document = LoadDocument(inputPath, error);
            if (options.ListLayers)
            {
                WriteLayerTable(document, output);
                return 0;
            }

            string outputPath = ResolveOutputPath(options, inputPath, format);

            ImageExporter exporter = new();
            Configure(exporter.Configuration, options);
            exporter.Configuration.OnNotification += (_, e) => error.WriteLine($"render: {e.Message}");

            if (options.ExportPaperLayouts)
            {
                exporter.AddPaperLayouts(document);
            }
            else
            {
                exporter.AddModelSpace(document);
            }

            exporter.Save(outputPath, format);

            output.WriteLine($"Generated {Path.GetFullPath(outputPath)} in {stopwatch.ElapsedMilliseconds}ms");

            return 0;
        }
        catch (Exception ex) when (!IsFatalException(ex))
        {
            error.WriteLine($"Error: {ex.Message}");
#if DEBUG
            error.WriteLine(ex.StackTrace);
#endif
            return 1;
        }
    }

    /// <summary>
    /// Determines if an exception is fatal and should not be caught.
    /// </summary>
    private static bool IsFatalException(Exception ex)
    {
        return ex is OutOfMemoryException
            or StackOverflowException
            or ThreadAbortException
            or AccessViolationException;
    }

    private static void Configure(ImageConfiguration configuration, CliOptions options)
    {
        configuration.Width = options.Width;
        configuration.Height = options.Height;
        configuration.SetPadding(options.PaddingLeft, options.PaddingTop, options.PaddingRight, options.PaddingBottom);
        configuration.OutputQuality = options.Quality;
        configuration.BackgroundColor = ParseColor(options.BackgroundColor);

        foreach (string layer in options.HideLayers)
        {
            configuration.HideLayer(layer);
        }

        configuration.Svg.NonScalingStroke = !options.SvgNoScalingStroke;
        configuration.Svg.EmitEntityAttributes = !options.SvgNoEntityAttributes;
        configuration.Svg.EmitSize = options.SvgEmitSize;
        configuration.Svg.IdPrefix = options.SvgIdPrefix;
        configuration.Svg.Precision = options.SvgPrecision;

        foreach (string layer in options.OnlyLayers)
        {
            configuration.IncludeLayer(layer);
        }

        if (options.LayerVisibility is not null)
        {
            configuration.LayerVisibility = options.LayerVisibility.Value;
        }
    }

    private static CadDocument LoadDocument(string inputPath, TextWriter error)
    {
        return Path.GetExtension(inputPath).ToLowerInvariant() switch
        {
            ".dxf" => DxfReader.Read(inputPath, (_, e) => OnReaderNotification(e, error)),
            ".dwg" => DwgReader.Read(inputPath, (_, e) => OnReaderNotification(e, error)),
            _ => throw new InvalidOperationException("Unsupported input format. Use a .dxf or .dwg file."),
        };
    }

    /// <summary>Writes a human-readable layer table for <paramref name="document"/> to <paramref name="writer"/>.</summary>
    internal static void WriteLayerTable(CadDocument document, TextWriter writer)
    {
        // ACadSharp always keeps layer "0", so the table is never empty.
        List<ACadSharp.Tables.Layer> layers = document.Layers.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();

        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (ACadSharp.Entities.Entity entity in document.ModelSpace.Entities)
        {
            string name = entity.Layer?.Name ?? "0";
            counts[name] = counts.TryGetValue(name, out int count) ? count + 1 : 1;
        }

        int nameWidth = Math.Max(5, layers.Max(l => l.Name.Length));
        int lineTypeWidth = Math.Max(8, layers.Max(l => (l.LineType?.Name ?? "-").Length));
        int weightWidth = Math.Max(6, layers.Max(l => l.LineWeight.ToString().Length));

        writer.WriteLine($"{"Layer".PadRight(nameWidth)}  On   Frozen  Plot  Color        {"Weight".PadRight(weightWidth)}  {"Linetype".PadRight(lineTypeWidth)}  Entities");
        foreach (ACadSharp.Tables.Layer layer in layers)
        {
            string color = layer.Color.IsTrueColor
                ? $"#{layer.Color.R:x2}{layer.Color.G:x2}{layer.Color.B:x2}"
                : layer.Color.Index.ToString(CultureInfo.InvariantCulture);
            counts.TryGetValue(layer.Name, out int count);
            writer.WriteLine(
                $"{layer.Name.PadRight(nameWidth)}  {(layer.IsOn ? "yes" : "no ")}  {(layer.Flags.HasFlag(ACadSharp.Tables.LayerFlags.Frozen) ? "yes   " : "no    ")}  {(layer.PlotFlag ? "yes " : "no  ")}  {color.PadRight(11)}  {layer.LineWeight.ToString().PadRight(weightWidth)}  {(layer.LineType?.Name ?? "-").PadRight(lineTypeWidth)}  {count}");
        }
    }

    private static SixLabors.ImageSharp.Color ParseColor(string value)
    {
        try
        {
            return SixLabors.ImageSharp.Color.Parse(value);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Invalid background color '{value}'. Use a named color or hex value like #ffffff.", ex);
        }
    }

    /// <summary>Resolves the export format from an explicit <c>--format</c>, else the output path's extension, else <see cref="ImageExportFormat.Png"/>.</summary>
    internal static ImageExportFormat ResolveFormat(CliOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Format))
        {
            if (ImageExportFormatExtensions.TryParse(options.Format, out ImageExportFormat explicitFormat))
            {
                return explicitFormat;
            }

            throw new InvalidOperationException($"Unsupported output format '{options.Format}'.");
        }

        if (ImageExportFormatExtensions.TryParseFileExtension(Path.GetExtension(options.OutputPath), out ImageExportFormat fromExtension))
        {
            return fromExtension;
        }

        return ImageExportFormat.Png;
    }

    /// <summary>Resolves the output path from an explicit <c>--output</c>, else the input path with the format's extension.</summary>
    internal static string ResolveOutputPath(CliOptions options, string inputPath, ImageExportFormat format)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            return Path.GetFullPath(options.OutputPath);
        }

        return Path.ChangeExtension(inputPath, format.GetFileExtension());
    }

    /// <summary>Parses command-line arguments into <see cref="CliOptions"/>; throws <see cref="InvalidOperationException"/> for unknown or invalid arguments.</summary>
    internal static CliOptions ParseArgs(IReadOnlyList<string> args)
    {
        string? inputPath = null;
        string? outputPath = null;
        string? format = null;
        string backgroundColor = "white";
        int width = ImageConfiguration.DefaultWidth;
        int height = ImageConfiguration.DefaultHeight;
        int paddingLeft = 0;
        int paddingTop = 0;
        int paddingRight = 0;
        int paddingBottom = 0;
        int quality = 90;
        bool exportPaperLayouts = false;
        List<string> hideLayers = new();
        bool svgNoScalingStroke = false;
        bool svgNoEntityAttributes = false;
        bool svgEmitSize = false;
        string svgIdPrefix = string.Empty;
        int? svgPrecision = null;
        LayerVisibilityMode? layerVisibility = null;
        List<string> onlyLayers = new();
        bool listLayers = false;

        for (int i = 0; i < args.Count; i++)
        {
            string current = args[i];
            if (!current.StartsWith('-'))
            {
                inputPath ??= current;
                continue;
            }

            switch (current)
            {
                case "--output":
                case "-o":
                    outputPath = GetRequiredValue(args, ref i, current);
                    break;
                case "--width":
                case "-w":
                    width = ParsePositiveInt(GetRequiredValue(args, ref i, current), current);
                    break;
                case "--height":
                case "-H":
                    height = ParsePositiveInt(GetRequiredValue(args, ref i, current), current);
                    break;
                case "--padding":
                case "-p":
                    (paddingLeft, paddingTop, paddingRight, paddingBottom) = ParsePadding(GetRequiredValue(args, ref i, current), current);
                    break;
                case "--background":
                case "-b":
                    backgroundColor = GetRequiredValue(args, ref i, current);
                    break;
                case "--quality":
                case "-q":
                    quality = ParseQuality(GetRequiredValue(args, ref i, current), current);
                    break;
                case "--format":
                case "-f":
                    format = GetRequiredValue(args, ref i, current);
                    break;
                case "--paper-layouts":
                    exportPaperLayouts = true;
                    break;
                case "--hide-layer":
                    hideLayers.Add(GetRequiredValue(args, ref i, current));
                    break;
                case "--svg-no-scaling-stroke":
                    svgNoScalingStroke = true;
                    break;
                case "--svg-no-entity-attributes":
                    svgNoEntityAttributes = true;
                    break;
                case "--svg-size":
                    svgEmitSize = true;
                    break;
                case "--svg-id-prefix":
                    svgIdPrefix = GetRequiredValue(args, ref i, current);
                    break;
                case "--svg-precision":
                    svgPrecision = ParseRange(GetRequiredValue(args, ref i, current), current, 0, 8);
                    break;
                case "--layer-visibility":
                    layerVisibility = ParseLayerVisibility(GetRequiredValue(args, ref i, current));
                    break;
                case "--only-layer":
                    onlyLayers.Add(GetRequiredValue(args, ref i, current));
                    break;
                case "--list-layers":
                    listLayers = true;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown argument '{current}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new InvalidOperationException("An input .dxf or .dwg file is required.");
        }

        return new CliOptions(inputPath, outputPath, format, width, height, paddingLeft, paddingTop, paddingRight, paddingBottom, backgroundColor, quality, exportPaperLayouts, hideLayers, svgNoScalingStroke, svgNoEntityAttributes, svgEmitSize, svgIdPrefix, svgPrecision, layerVisibility, onlyLayers, listLayers);
    }

    private static LayerVisibilityMode ParseLayerVisibility(string? value)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "all":
                return LayerVisibilityMode.All;
            case "screen":
                return LayerVisibilityMode.Screen;
            case "plot":
                return LayerVisibilityMode.Plot;
            default:
                throw new InvalidOperationException($"Invalid --layer-visibility '{value}'. Use all, screen or plot.");
        }
    }

    private static int ParseRange(string value, string argumentName, int min, int max)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= min && parsed <= max)
        {
            return parsed;
        }

        throw new InvalidOperationException($"Argument {argumentName} must be between {min} and {max}.");
    }

    private static int ParsePositiveInt(string value, string argumentName)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
        {
            return parsed;
        }

        throw new InvalidOperationException($"Argument {argumentName} must be a positive integer.");
    }

    private static int ParseQuality(string value, string argumentName)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 1 and <= 100)
        {
            return parsed;
        }

        throw new InvalidOperationException($"Argument {argumentName} must be between 1 and 100.");
    }

    private static (int Left, int Top, int Right, int Bottom) ParsePadding(string value, string argumentName)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        int[] parsed = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            parsed[i] = ParseNonNegativeInt(parts[i], argumentName);
        }

        return parsed.Length switch
        {
            1 => (parsed[0], parsed[0], parsed[0], parsed[0]),
            2 => (parsed[0], parsed[1], parsed[0], parsed[1]),
            4 => (parsed[0], parsed[1], parsed[2], parsed[3]),
            _ => throw new InvalidOperationException($"Argument {argumentName} must contain 1, 2, or 4 comma-separated integers."),
        };
    }

    private static int ParseNonNegativeInt(string value, string argumentName)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0)
        {
            return parsed;
        }

        throw new InvalidOperationException($"Argument {argumentName} values must be zero or greater.");
    }

    private static string GetRequiredValue(IReadOnlyList<string> args, ref int index, string argumentName)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith('-'))
        {
            throw new InvalidOperationException($"Argument {argumentName} requires a value.");
        }

        index++;
        return args[index];
    }

    private static void OnReaderNotification(NotificationEventArgs e, TextWriter error)
    {
        if (e.NotificationType is NotificationType.None or NotificationType.Warning or NotificationType.NotImplemented)
        {
            return;
        }

        error.WriteLine($"reader: {e.Message}");
    }

    private static bool IsHelpArgument(string value) =>
        value.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("-?", StringComparison.OrdinalIgnoreCase);

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("""
Usage:
  cad-to-image <input.dxf|input.dwg> [options]

Options:
  -o, --output <path>         Output file or directory path.
  -f, --format <format>       png, bmp, jpg, jpeg, gif, webp, svg.
  -w, --width <pixels>        Output width in pixels. Default: 1600.
  -H, --height <pixels>       Output height in pixels. Default: 900.
  -p, --padding <value>       Padding in pixels: <all>, <x,y>, or <left,top,right,bottom>.
  -b, --background <color>    Background color name or hex value. Default: white.
  -q, --quality <1-100>       Output quality for lossy formats. Default: 90.
      --paper-layouts         Export paper layouts instead of model space.
      --hide-layer <name>     Hide entities on the specified layer. Can be used multiple times.
      --only-layer <name>     Render only the specified layer(s). Can be used multiple times.
      --layer-visibility <m>  all (default), screen (honour off/frozen), or plot (also honour non-plottable).
      --list-layers           Print the drawing's layers and exit without rendering.
      --svg-no-scaling-stroke Write SVG stroke widths in drawing units instead of constant pixels.
      --svg-no-entity-attributes
                              Omit data-handle/data-type/data-parent/data-block attributes from SVG.
      --svg-size              Emit width/height on the SVG root from --width/--height.
      --svg-id-prefix <text>  Prefix for SVG ids so several drawings can share one page.
      --svg-precision <0-8>   Decimal places for SVG coordinates. Default: adaptive.
      --help, -h, -?          Show this help text.
""");
    }
}
