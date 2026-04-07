using ACadSharp.IO;
using ImageColor = SixLabors.ImageSharp.Color;

namespace ACadSharp.Image;

/// <summary>
/// Configuration settings that control how DWG/DXF files are rendered to images.
/// </summary>
/// <remarks>
/// <para>
/// This class holds all tunable parameters for the image export pipeline, including
/// output dimensions, resolution, colors, line weights, and layer visibility.
/// </para>
/// <para>
/// Properties validate their values on assignment and throw <see cref="ArgumentOutOfRangeException"/>
/// for invalid inputs.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var config = new ImageConfiguration
/// {
///     Width = 1920,
///     Height = 1080,
///     Dpi = 150f,
///     BackgroundColor = ImageColor.Black,
/// };
/// config.HiddenLayers.Add("annotation");
/// </code>
/// </example>
public sealed class ImageConfiguration
{
    /// <summary>
    /// Default output width in pixels.
    /// </summary>
    public const int DefaultWidth = 1600;

    /// <summary>
    /// Default output height in pixels.
    /// </summary>
    public const int DefaultHeight = 900;

    /// <summary>
    /// Default mapping from <see cref="LineWeightType"/> to line weight in millimeters.
    /// </summary>
    /// <remarks>
    /// These values follow standard CAD line weight conventions. Use this as a fallback
    /// when <see cref="LineWeightValues"/> does not contain a mapping for a specific type.
    /// </remarks>
    public static readonly IReadOnlyDictionary<LineWeightType, double> LineWeightDefaultValues =
        new Dictionary<LineWeightType, double>
        {
            { LineWeightType.Default, 0.0 },
            { LineWeightType.W0, 0.001 },
            { LineWeightType.W5, 0.05 },
            { LineWeightType.W9, 0.09 },
            { LineWeightType.W13, 0.13 },
            { LineWeightType.W15, 0.15 },
            { LineWeightType.W18, 0.18 },
            { LineWeightType.W20, 0.20 },
            { LineWeightType.W25, 0.25 },
            { LineWeightType.W30, 0.30 },
            { LineWeightType.W35, 0.35 },
            { LineWeightType.W40, 0.40 },
            { LineWeightType.W50, 0.50 },
            { LineWeightType.W53, 0.53 },
            { LineWeightType.W60, 0.60 },
            { LineWeightType.W70, 0.70 },
            { LineWeightType.W80, 0.80 },
            { LineWeightType.W90, 0.90 },
            { LineWeightType.W100, 1.00 },
            { LineWeightType.W106, 1.06 },
            { LineWeightType.W120, 1.20 },
            { LineWeightType.W140, 1.40 },
            { LineWeightType.W158, 1.58 },
            { LineWeightType.W200, 2.00 },
            { LineWeightType.W211, 2.11 },
        };

    /// <summary>
    /// Event raised when a notification occurs during rendering or export.
    /// </summary>
    /// <remarks>
    /// This event can be used to log warnings, errors, or informational messages
    /// during the rendering process. Common notification types include missing
    /// entity support, invalid geometry, and other rendering issues.
    /// </remarks>
    public event NotificationEventHandler? OnNotification;

    private int _width = DefaultWidth;

    private int _height = DefaultHeight;

    private int _outputQuality = 90;

    /// <summary>
    /// Gets or sets the number of segments used to approximate arcs and circles during polygonal tessellation.
    /// </summary>
    /// <remarks>
    /// Higher values produce smoother curves at the cost of rendering performance.
    /// Default is 256 segments.
    /// </remarks>
    public ushort ArcPrecision { get; set; } = 256;

    /// <summary>
    /// Gets or sets the dots-per-inch resolution used when converting drawing units to pixels.
    /// </summary>
    /// <remarks>
    /// This value affects line weight calculations and text sizing.
    /// Default is 96 DPI.
    /// </remarks>
    public float Dpi { get; set; } = 96f;

    /// <summary>
    /// Gets or sets the diameter in pixels used to render point entities.
    /// </summary>
    /// <remarks>
    /// Default is 4 pixels. Set to a larger value to make points more visible in the output.
    /// </remarks>
    public float DotSizePixels { get; set; } = 4f;

    /// <summary>
    /// Gets or sets a multiplier applied to all line weights.
    /// </summary>
    /// <remarks>
    /// Values greater than 1.0 thicken lines; values less than 1.0 thin them.
    /// Default is 1.0 (no scaling).
    /// </remarks>
    public float LineWeightScale { get; set; } = 1f;

    /// <summary>
    /// Gets or sets the background color of the rendered image.
    /// </summary>
    /// <remarks>
    /// Default is <see cref="ImageColor.White"/>. Use <see cref="ImageColor.Transparent"/> for
    /// formats that support transparency (e.g., PNG).
    /// </remarks>
    public ImageColor BackgroundColor { get; set; } = ImageColor.White;

    /// <summary>
    /// Gets or sets the font family name used for rendering text entities.
    /// </summary>
    /// <remarks>
    /// The font must be available on the system. If the specified font is not found,
    /// the system's default font family is used as a fallback.
    /// Default is "Arial".
    /// </remarks>
    public string FontFamilyName { get; set; } = "Arial";

    /// <summary>
    /// Gets the set of layer names that should be hidden during export.
    /// Layer names are case-insensitive.
    /// </summary>
    public HashSet<string> HiddenLayers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the JPEG output quality as a percentage.
    /// </summary>
    /// <value>A value between 1 and 100, where 100 is the highest quality.</value>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is outside the range of 1 to 100.
    /// </exception>
    /// <remarks>
    /// Default is 90. This property only affects JPEG output.
    /// </remarks>
    public int OutputQuality
    {
        get => this._outputQuality;
        set => this._outputQuality = value is >= 1 and <= 100
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Quality must be between 1 and 100.");
    }

    /// <summary>
    /// Gets or sets the output image width in pixels.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is less than or equal to zero.
    /// </exception>
    /// <remarks>
    /// Default is <see cref="DefaultWidth"/> (1600 pixels).
    /// </remarks>
    public int Width
    {
        get => this._width;
        set => this._width = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Width must be greater than zero.");
    }

    /// <summary>
    /// Gets or sets the output image height in pixels.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is less than or equal to zero.
    /// </exception>
    /// <remarks>
    /// Default is <see cref="DefaultHeight"/> (900 pixels).
    /// </remarks>
    public int Height
    {
        get => this._height;
        set => this._height = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Height must be greater than zero.");
    }

    /// <summary>
    /// Gets a dictionary of custom line weight values in millimeters, keyed by <see cref="LineWeightType"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Values in this dictionary take precedence over <see cref="LineWeightDefaultValues"/>.
    /// Add entries here to override the default line weights for specific line weight types.
    /// </para>
    /// <para>
    /// If a line weight type is not found in either dictionary, a minimum of 1 pixel
    /// (or <see cref="LineWeightScale"/>, whichever is greater) is used.
    /// </para>
    /// </remarks>
    public Dictionary<LineWeightType, double> LineWeightValues { get; } = new();

    /// <summary>
    /// Converts a <see cref="LineWeightType"/> to its equivalent width in pixels.
    /// </summary>
    /// <param name="lineWeight">The line weight type to convert.</param>
    /// <returns>
    /// The line weight in pixels, computed from the configured millimeter value,
    /// <see cref="Dpi"/>, and <see cref="LineWeightScale"/>. Returns at least 1 pixel.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The conversion formula is: <c>millimeters * DPI / 25.4 * LineWeightScale</c>.
    /// </para>
    /// <para>
    /// If the line weight maps to zero or is not found, the result is clamped to
    /// <c>Math.Max(1, LineWeightScale)</c>.
    /// </para>
    /// </remarks>
    public float GetLineWeightPixels(LineWeightType lineWeight)
    {
        double millimeters = this.LineWeightValues.TryGetValue(lineWeight, out double configured)
            ? configured
            : LineWeightDefaultValues.TryGetValue(lineWeight, out double fallback)
                ? fallback
                : 0d;

        if (millimeters <= 0d)
        {
            return Math.Max(1f, this.LineWeightScale);
        }

        float pixels = (float)(millimeters * this.Dpi / 25.4d);
        return Math.Max(1f, pixels * this.LineWeightScale);
    }

    internal void Notify(string message, NotificationType notificationType, Exception? ex = null)
    {
        this.OnNotification?.Invoke(this, new NotificationEventArgs(message, notificationType, ex));
    }
}
