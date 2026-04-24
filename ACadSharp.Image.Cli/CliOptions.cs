namespace ACadSharp.Image.Cli;

internal sealed record CliOptions(
    string InputPath,
    string? OutputPath,
    string? Format,
    int Width,
    int Height,
    int PaddingLeft,
    int PaddingTop,
    int PaddingRight,
    int PaddingBottom,
    string BackgroundColor,
    int Quality,
    bool ExportPaperLayouts,
    IReadOnlyList<string> HideLayers
);
