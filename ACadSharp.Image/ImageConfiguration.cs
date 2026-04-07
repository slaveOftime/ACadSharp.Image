using ACadSharp.IO;
using ImageColor = SixLabors.ImageSharp.Color;

namespace ACadSharp.Image;

public sealed class ImageConfiguration
{
    public const int DefaultWidth = 1600;

    public const int DefaultHeight = 900;

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

    public event NotificationEventHandler? OnNotification;

    private int _width = DefaultWidth;

    private int _height = DefaultHeight;

    private int _outputQuality = 90;

    public ushort ArcPrecision { get; set; } = 256;

    public float Dpi { get; set; } = 96f;

    public float DotSizePixels { get; set; } = 4f;

    public float LineWeightScale { get; set; } = 1f;

    public ImageColor BackgroundColor { get; set; } = ImageColor.White;

    public string FontFamilyName { get; set; } = "Arial";

    /// <summary>
    /// Gets the set of layer names that should be hidden during export.
    /// Layer names are case-insensitive.
    /// </summary>
    public HashSet<string> HiddenLayers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int OutputQuality
    {
        get => this._outputQuality;
        set => this._outputQuality = value is >= 1 and <= 100
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Quality must be between 1 and 100.");
    }

    public int Width
    {
        get => this._width;
        set => this._width = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Width must be greater than zero.");
    }

    public int Height
    {
        get => this._height;
        set => this._height = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Height must be greater than zero.");
    }

    public Dictionary<LineWeightType, double> LineWeightValues { get; } = new();

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
