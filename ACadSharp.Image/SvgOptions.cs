namespace ACadSharp.Image;

/// <summary>
/// Settings that only affect <see cref="ImageExportFormat.Svg"/> output.
/// </summary>
public sealed class SvgOptions
{
    private int? _precision;

    /// <summary>
    /// Gets or sets whether strokes keep a constant on-screen width when the SVG is zoomed.
    /// When true (default) stroke widths are written in pixels with <c>vector-effect="non-scaling-stroke"</c>;
    /// when false they are written in drawing units and scale with the drawing.
    /// </summary>
    public bool NonScalingStroke { get; set; } = true;

    /// <summary>
    /// Gets or sets whether each element carries <c>data-handle</c>, <c>data-type</c>, <c>data-parent</c> and <c>data-block</c> attributes. Default true.
    /// </summary>
    public bool EmitEntityAttributes { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the root element gets <c>width</c>/<c>height</c> attributes from <see cref="ImageConfiguration.Width"/> and <see cref="ImageConfiguration.Height"/>. Default false (responsive).
    /// </summary>
    public bool EmitSize { get; set; }

    /// <summary>
    /// Gets or sets a prefix for every <c>id</c> so several drawings can be inlined in one HTML document. Default empty.
    /// </summary>
    public string IdPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of decimals for coordinates (0..8). Null (default) picks the precision from the viewBox size
    /// so the resolution is one ten-thousandth of the larger side.
    /// </summary>
    public int? Precision
    {
        get => this._precision;
        set => this._precision = value is null or (>= 0 and <= 8)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Precision must be between 0 and 8.");
    }
}
