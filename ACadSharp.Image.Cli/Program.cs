using System.Globalization;
using ACadSharp.IO;

namespace ACadSharp.Image.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Any(IsHelpArgument))
            {
                WriteHelp();
                return 0;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            CliOptions options = ParseArgs(args);
            string inputPath = Path.GetFullPath(options.InputPath);
            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException("Input file was not found.", inputPath);
            }

            ImageExportFormat format = ResolveFormat(options);
            string outputPath = ResolveOutputPath(options, inputPath, format);

            ImageExporter exporter = new();
            Configure(exporter.Configuration, options);
            exporter.Configuration.OnNotification += OnExporterNotification;

            CadDocument document = LoadDocument(inputPath);
            if (options.ExportPaperLayouts)
            {
                exporter.AddPaperLayouts(document);
            }
            else
            {
                exporter.AddModelSpace(document);
            }

            exporter.Save(outputPath, format);

            Console.WriteLine($"Generated {Path.GetFullPath(outputPath)} in {stopwatch.ElapsedMilliseconds}ms");

            return 0;
        }
        catch (Exception ex) when (!IsFatalException(ex))
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
#if DEBUG
            Console.Error.WriteLine(ex.StackTrace);
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
        configuration.OutputQuality = options.Quality;
        configuration.BackgroundColor = ParseColor(options.BackgroundColor);

        foreach (string layer in options.HideLayers)
        {
            configuration.HiddenLayers.Add(layer);
        }
    }

    private static CadDocument LoadDocument(string inputPath)
    {
        return Path.GetExtension(inputPath).ToLowerInvariant() switch
        {
            ".dxf" => DxfReader.Read(inputPath, OnReaderNotification),
            ".dwg" => DwgReader.Read(inputPath, OnReaderNotification),
            _ => throw new InvalidOperationException("Unsupported input format. Use a .dxf or .dwg file."),
        };
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

    private static ImageExportFormat ResolveFormat(CliOptions options)
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

    private static string ResolveOutputPath(CliOptions options, string inputPath, ImageExportFormat format)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            return Path.GetFullPath(options.OutputPath);
        }

        return Path.ChangeExtension(inputPath, format.GetFileExtension());
    }

    private static CliOptions ParseArgs(IReadOnlyList<string> args)
    {
        string? inputPath = null;
        string? outputPath = null;
        string? format = null;
        string backgroundColor = "white";
        int width = ImageConfiguration.DefaultWidth;
        int height = ImageConfiguration.DefaultHeight;
        int quality = 90;
        bool exportPaperLayouts = false;
        List<string> hideLayers = new();

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
                default:
                    throw new InvalidOperationException($"Unknown argument '{current}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new InvalidOperationException("An input .dxf or .dwg file is required.");
        }

        return new CliOptions(inputPath, outputPath, format, width, height, backgroundColor, quality, exportPaperLayouts, hideLayers);
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

    private static string GetRequiredValue(IReadOnlyList<string> args, ref int index, string argumentName)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith('-'))
        {
            throw new InvalidOperationException($"Argument {argumentName} requires a value.");
        }

        index++;
        return args[index];
    }

    private static void OnReaderNotification(object? sender, NotificationEventArgs e)
    {
        if (e.NotificationType is NotificationType.None or NotificationType.Warning or NotificationType.NotImplemented)
        {
            return;
        }

        Console.Error.WriteLine($"reader: {e.Message}");
    }

    private static void OnExporterNotification(object? sender, NotificationEventArgs e)
    {
        Console.Error.WriteLine($"render: {e.Message}");
    }

    private static bool IsHelpArgument(string value) =>
        value.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("-?", StringComparison.OrdinalIgnoreCase);

    private static void WriteHelp()
    {
        Console.WriteLine("""
Usage:
  cad-to-image <input.dxf|input.dwg> [options]

Options:
  -o, --output <path>         Output file or directory path.
  -f, --format <format>       png, bmp, jpg, jpeg, gif, webp.
  -w, --width <pixels>        Output width in pixels. Default: 1600.
  -H, --height <pixels>       Output height in pixels. Default: 900.
  -b, --background <color>    Background color name or hex value. Default: white.
  -q, --quality <1-100>       Output quality for lossy formats. Default: 90.
      --paper-layouts         Export paper layouts instead of model space.
      --hide-layer <name>     Hide entities on the specified layer. Can be used multiple times.
      --help, -h, -?          Show this help text.
""");
    }
}
