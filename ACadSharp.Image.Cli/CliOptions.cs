namespace ACadSharp.Image.Cli;

internal sealed record CliOptions(
    string InputPath,
    string? OutputPath,
    string? Format,
    int Width,
    int Height,
    string BackgroundColor,
    int Quality,
    bool ExportPaperLayouts
);
