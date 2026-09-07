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
/// <param name="WidthScale">Factor glyph advances are stretched by along the baseline relative to <paramref name="Height"/>; 1 is natural width. <paramref name="WrappingWidth"/> and <paramref name="FixedLength"/> are expressed in surface units of the stretched run.</param>
internal sealed record SurfaceText(
    string Text,
    SurfacePoint Origin,
    double Height,
    double Rotation,
    SurfaceTextAnchor Anchor,
    SurfaceTextBaseline Baseline,
    double WrappingWidth,
    double LineSpacingFactor,
    double FixedLength,
    double WidthScale = 1d);
