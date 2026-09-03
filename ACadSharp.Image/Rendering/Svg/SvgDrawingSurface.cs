using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ACadSharp.IO;
using ImageColor = SixLabors.ImageSharp.Color;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace ACadSharp.Image.Rendering.Svg;

/// <summary>
/// <see cref="IDrawingSurface"/> that builds an SVG document with one <c>&lt;g&gt;</c> per effective layer.
/// </summary>
/// <remarks>
/// Coordinates handed to this surface are already in SVG user units (drawing units, Y down).
/// See the design spec section 5 for the document structure.
/// </remarks>
internal sealed class SvgDrawingSurface : IDrawingSurface
{
    public static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    private readonly ImageConfiguration _configuration;
    private readonly SvgOptions _options;
    private readonly SvgNumberFormatter _numbers;
    private readonly SvgNumberFormatter _styleNumbers;
    private readonly SvgNumberFormatter _angleNumbers;
    private readonly XElement _root;
    private readonly XElement _defs;
    private readonly XElement _defaults;
    private readonly Stack<Container> _containers = new();
    private readonly Stack<(EntityRenderInfo Info, LayerRenderInfo Layer)> _entities = new();
    private readonly HashSet<string> _issuedIds = new(StringComparer.Ordinal);
    private XDocument? _document;
    private int _clipCounter;

    public SvgDrawingSurface(ImageConfiguration configuration, SurfaceRect viewBox, double? sizeWidth, double? sizeHeight, double? strokeUnitsPerMillimeter = null)
    {
        this._configuration = configuration;
        this._options = configuration.Svg;
        this._numbers = new SvgNumberFormatter(this._options.Precision ?? SvgNumberFormatter.AdaptiveDecimals(viewBox.Width, viewBox.Height));
        this._styleNumbers = new SvgNumberFormatter(StyleDecimalsFor(strokeUnitsPerMillimeter));
        // Rotations are degrees whatever the drawing units are, so they get their own fixed precision.
        this._angleNumbers = new SvgNumberFormatter(4);

        this._root = new XElement(Ns + "svg",
            new XAttribute("viewBox", $"{this.N(viewBox.X)} {this.N(viewBox.Y)} {this.N(viewBox.Width)} {this.N(viewBox.Height)}"));
        if (sizeWidth is > 0 && sizeHeight is > 0)
        {
            this._root.Add(new XAttribute("width", this.N(sizeWidth.Value)), new XAttribute("height", this.N(sizeHeight.Value)));
        }

        this._defs = new XElement(Ns + "defs");
        XElement cadRoot = new(Ns + "g", new XAttribute("class", "cad-root"));
        this._defaults = new XElement(Ns + "g",
            new XAttribute("fill", "none"),
            new XAttribute("stroke-linecap", "round"),
            new XAttribute("stroke-linejoin", "round"),
            new XAttribute("font-family", BuildFontStack(configuration.FontFamilyName)));

        Rgba32 background = configuration.BackgroundColor.ToPixel<Rgba32>();
        if (background.A > 0)
        {
            this._defaults.Add(new XElement(Ns + "rect",
                new XAttribute("class", "cad-background"),
                new XAttribute("x", this.N(viewBox.X)),
                new XAttribute("y", this.N(viewBox.Y)),
                new XAttribute("width", this.N(viewBox.Width)),
                new XAttribute("height", this.N(viewBox.Height)),
                new XAttribute("fill", Hex(configuration.BackgroundColor)),
                new XAttribute("stroke", "none")));
        }

        cadRoot.Add(this._defaults);
        this._root.Add(cadRoot);
        this._containers.Push(new Container(this._defaults, "layer"));
    }

    public bool SupportsCurves => true;

    /// <summary>
    /// Decimals used for style scalars such as stroke widths and dash lengths.
    /// </summary>
    internal int StyleDecimals => this._styleNumbers.Decimals;

    /// <summary>
    /// Style scalars are three decimals for pixels and millimetre-scale drawing units. When a drawing unit is much
    /// larger than a millimetre (metres, kilometres) a 0.25 mm stroke is a tiny number of drawing units, so the
    /// precision grows with the unit to keep the width from rounding away to zero.
    /// </summary>
    /// <param name="strokeUnitsPerMillimeter">Drawing units per millimetre for stroke widths, or null for pixel widths.</param>
    /// <returns>The number of decimals for style scalars, 3..8.</returns>
    private static int StyleDecimalsFor(double? strokeUnitsPerMillimeter)
    {
        return strokeUnitsPerMillimeter is double unitsPerMillimeter && unitsPerMillimeter > 0d
            ? Math.Clamp(3 - (int)Math.Floor(Math.Log10(unitsPerMillimeter)), 3, 8)
            : 3;
    }

    /// <summary>
    /// The document being built. The same instance is returned on every call and reflects the drawing done so far.
    /// </summary>
    /// <returns>The SVG document.</returns>
    public XDocument ToDocument()
    {
        if (this._defs.HasElements && this._defs.Parent == null)
        {
            this._root.AddFirst(this._defs);
        }

        return this._document ??= new XDocument(this._root);
    }

    public string ToSvgString()
    {
        StringBuilder builder = new();
        // No XML declaration: XmlWriter over a StringBuilder would declare utf-16, which contradicts the UTF-8 bytes RenderedSvgPage writes,
        // and inline SVG in HTML must not carry a declaration anyway.
        XmlWriterSettings settings = new() { Indent = true, OmitXmlDeclaration = true, NewLineChars = "\n" };
        using (XmlWriter writer = XmlWriter.Create(builder, settings))
        {
            this.ToDocument().Save(writer);
        }

        return builder.ToString();
    }

    public void BeginEntity(EntityRenderInfo info, LayerRenderInfo layer)
    {
        this._entities.Push((info, layer));
    }

    public void EndEntity()
    {
        if (this._entities.Count > 0)
        {
            this._entities.Pop();
        }
    }

    public void DrawLine(ImageStyle style, SurfacePoint start, SurfacePoint end)
    {
        if (!IsFinite(start) || !IsFinite(end))
        {
            this.NotifyNonFinite();
            return;
        }

        this.Append(this.Stroked(new XElement(Ns + "line",
            new XAttribute("x1", this.N(start.X)), new XAttribute("y1", this.N(start.Y)),
            new XAttribute("x2", this.N(end.X)), new XAttribute("y2", this.N(end.Y))), style));
    }

    public void DrawPolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, bool closed)
    {
        if (this.FinitePoints(points, 2) is not List<SurfacePoint> finite)
        {
            return;
        }

        XElement element = new(Ns + (closed ? "polygon" : "polyline"), new XAttribute("points", this.Points(finite)));
        this.Append(this.Stroked(element, style));
    }

    public void DrawArc(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation, double startAngle, double sweepAngle)
    {
        if (!IsFinite(center) || !IsFinite(radiusX) || !IsFinite(radiusY) || !IsFinite(rotation) || !IsFinite(startAngle) || !IsFinite(sweepAngle))
        {
            this.NotifyNonFinite();
            return;
        }

        if (Math.Abs(sweepAngle) >= (2d * Math.PI) - 1e-9)
        {
            this.DrawEllipse(style, center, radiusX, radiusY, rotation);
            return;
        }

        SurfacePoint start = PointOnEllipse(center, radiusX, radiusY, rotation, startAngle);
        SurfacePoint end = PointOnEllipse(center, radiusX, radiusY, rotation, startAngle + sweepAngle);
        int largeArc = Math.Abs(sweepAngle) > Math.PI ? 1 : 0;
        int sweepFlag = sweepAngle > 0 ? 1 : 0;
        string d = $"M{this.N(start.X)} {this.N(start.Y)}A{this.N(Math.Abs(radiusX))} {this.N(Math.Abs(radiusY))} {this.A(rotation * 180d / Math.PI)} {largeArc} {sweepFlag} {this.N(end.X)} {this.N(end.Y)}";
        this.Append(this.Stroked(new XElement(Ns + "path", new XAttribute("d", d)), style));
    }

    public void DrawEllipse(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation)
    {
        if (!IsFinite(center) || !IsFinite(radiusX) || !IsFinite(radiusY) || !IsFinite(rotation))
        {
            this.NotifyNonFinite();
            return;
        }

        // A NaN radius compares false against everything, so the finiteness is part of the discriminator.
        if (double.IsFinite(radiusX) && double.IsFinite(radiusY) && Math.Abs(radiusX - radiusY) < 1e-9)
        {
            this.Append(this.Stroked(new XElement(Ns + "circle",
                new XAttribute("cx", this.N(center.X)), new XAttribute("cy", this.N(center.Y)), new XAttribute("r", this.N(Math.Abs(radiusX)))), style));
            return;
        }

        XElement ellipse = new(Ns + "ellipse",
            new XAttribute("cx", this.N(center.X)), new XAttribute("cy", this.N(center.Y)),
            new XAttribute("rx", this.N(Math.Abs(radiusX))), new XAttribute("ry", this.N(Math.Abs(radiusY))));
        if (Math.Abs(rotation) > 1e-12)
        {
            ellipse.Add(new XAttribute("transform", $"rotate({this.A(rotation * 180d / Math.PI)} {this.N(center.X)} {this.N(center.Y)})"));
        }

        this.Append(this.Stroked(ellipse, style));
    }

    public void DrawCubicBezier(ImageStyle style, IReadOnlyList<SurfacePoint> controlPoints, bool closed)
    {
        if (this.FinitePoints(controlPoints, 4) is not List<SurfacePoint> finite)
        {
            return;
        }

        StringBuilder d = new();
        d.Append('M').Append(this.N(finite[0].X)).Append(' ').Append(this.N(finite[0].Y));
        for (int i = 1; i + 2 < finite.Count; i += 3)
        {
            d.Append('C');
            for (int j = 0; j < 3; j++)
            {
                if (j > 0)
                {
                    d.Append(' ');
                }

                d.Append(this.N(finite[i + j].X)).Append(' ').Append(this.N(finite[i + j].Y));
            }
        }

        if (closed)
        {
            d.Append('Z');
        }

        this.Append(this.Stroked(new XElement(Ns + "path", new XAttribute("d", d.ToString())), style));
    }

    public void DrawBulgePolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, IReadOnlyList<double> bulges, bool closed)
    {
        // The bulges are indexed by vertex, so dropping a non-finite point has to drop its bulge with it.
        List<SurfacePoint> vertices = new(points.Count);
        List<double> vertexBulges = new(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            if (!IsFinite(points[i]))
            {
                continue;
            }

            vertices.Add(points[i]);
            vertexBulges.Add(i < bulges.Count ? bulges[i] : 0d);
        }

        if (vertices.Count != points.Count)
        {
            this.NotifyNonFinite();
        }

        if (vertices.Count < 2)
        {
            return;
        }

        StringBuilder d = new();
        d.Append('M').Append(this.N(vertices[0].X)).Append(' ').Append(this.N(vertices[0].Y));
        int segmentCount = closed ? vertices.Count : vertices.Count - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            SurfacePoint start = vertices[i];
            SurfacePoint end = vertices[(i + 1) % vertices.Count];
            double bulge = vertexBulges[i];
            if (!IsFinite(bulge) || Math.Abs(bulge) < 1e-12 || start == end)
            {
                d.Append('L').Append(this.N(end.X)).Append(' ').Append(this.N(end.Y));
                continue;
            }

            CurveTessellation.BulgeArc(start, end, bulge, out _, out double radius, out _, out double sweep);
            if (!IsFinite(radius) || !IsFinite(sweep))
            {
                this.NotifyNonFinite();
                d.Append('L').Append(this.N(end.X)).Append(' ').Append(this.N(end.Y));
                continue;
            }

            int largeArc = Math.Abs(bulge) > 1d ? 1 : 0;
            int sweepFlag = sweep > 0 ? 1 : 0;
            d.Append('A').Append(this.N(radius)).Append(' ').Append(this.N(radius)).Append(" 0 ").Append(largeArc).Append(' ').Append(sweepFlag).Append(' ')
                .Append(this.N(end.X)).Append(' ').Append(this.N(end.Y));
        }

        if (closed)
        {
            d.Append('Z');
        }

        this.Append(this.Stroked(new XElement(Ns + "path", new XAttribute("d", d.ToString())), style));
    }

    private static SurfacePoint PointOnEllipse(SurfacePoint center, double radiusX, double radiusY, double rotation, double angle)
    {
        double x = radiusX * Math.Cos(angle);
        double y = radiusY * Math.Sin(angle);
        double cos = Math.Cos(rotation);
        double sin = Math.Sin(rotation);
        return new SurfacePoint(center.X + (x * cos) - (y * sin), center.Y + (x * sin) + (y * cos));
    }

    public void FillPolygon(ImageStyle style, IReadOnlyList<SurfacePoint> points)
    {
        if (this.FinitePoints(points, 3) is not List<SurfacePoint> finite)
        {
            return;
        }

        this.Append(this.Filled(new XElement(Ns + "polygon", new XAttribute("points", this.Points(finite))), style));
    }

    public void FillPath(ImageStyle style, IReadOnlyList<IReadOnlyList<SurfacePoint>> rings)
    {
        StringBuilder d = new();
        foreach (IReadOnlyList<SurfacePoint> ring in rings)
        {
            if (this.FinitePoints(ring, 3) is not List<SurfacePoint> finite)
            {
                continue;
            }

            d.Append('M').Append(this.N(finite[0].X)).Append(' ').Append(this.N(finite[0].Y));
            for (int i = 1; i < finite.Count; i++)
            {
                d.Append('L').Append(this.N(finite[i].X)).Append(' ').Append(this.N(finite[i].Y));
            }

            d.Append('Z');
        }

        if (d.Length == 0)
        {
            return;
        }

        XElement path = new(Ns + "path", new XAttribute("fill-rule", "evenodd"), new XAttribute("d", d.ToString()));
        this.Append(this.Filled(path, style));
    }

    public void FillCircle(ImageStyle style, SurfacePoint center, double radius)
    {
        if (!IsFinite(center) || !IsFinite(radius))
        {
            this.NotifyNonFinite();
            return;
        }

        this.Append(this.Filled(new XElement(Ns + "circle",
            new XAttribute("cx", this.N(center.X)), new XAttribute("cy", this.N(center.Y)), new XAttribute("r", this.N(Math.Abs(radius)))), style));
    }

    public void DrawText(ImageStyle style, SurfaceText text)
    {
        if (string.IsNullOrWhiteSpace(text.Text))
        {
            return;
        }

        if (!IsFinite(text.Origin) || !IsFinite(text.Height) || !IsFinite(text.Rotation) || !IsFinite(text.FixedLength) || !IsFinite(text.LineSpacingFactor))
        {
            this.NotifyNonFinite();
            return;
        }

        XElement element = new(Ns + "text",
            new XAttribute("x", this.N(text.Origin.X)),
            new XAttribute("y", this.N(text.Origin.Y)),
            new XAttribute("font-size", this.N(text.Height)));

        if (text.Anchor != SurfaceTextAnchor.Start)
        {
            element.Add(new XAttribute("text-anchor", text.Anchor == SurfaceTextAnchor.Middle ? "middle" : "end"));
        }

        if (text.Baseline != SurfaceTextBaseline.Alphabetic)
        {
            element.Add(new XAttribute("dominant-baseline", text.Baseline == SurfaceTextBaseline.Central ? "central" : "hanging"));
        }

        if (Math.Abs(text.Rotation) > 1e-12)
        {
            element.Add(new XAttribute("transform", $"rotate({this.A(-text.Rotation * 180d / Math.PI)} {this.N(text.Origin.X)} {this.N(text.Origin.Y)})"));
        }

        if (text.FixedLength > 0)
        {
            element.Add(new XAttribute("textLength", this.N(text.FixedLength)), new XAttribute("lengthAdjust", "spacingAndGlyphs"));
        }

        string[] lines = text.Text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 1)
        {
            element.Add(lines[0]);
        }
        else
        {
            double lineHeight = text.Height * (text.LineSpacingFactor <= 0 ? 1d : text.LineSpacingFactor) * 5d / 3d;
            for (int i = 0; i < lines.Length; i++)
            {
                XElement span = new(Ns + "tspan", new XAttribute("x", this.N(text.Origin.X)), lines[i]);
                if (i > 0)
                {
                    span.Add(new XAttribute("dy", this.N(lineHeight)));
                }

                element.Add(span);
            }
        }

        this.Append(this.Filled(element, style));
    }

    public ViewportSurface BeginViewport(SurfaceRect bounds)
    {
        this._clipCounter++;
        string clipId = this.UniqueId(SvgIdSanitizer.Sanitize(this._options.IdPrefix, "clip", this._clipCounter.ToString(CultureInfo.InvariantCulture)));
        this._defs.Add(new XElement(Ns + "clipPath",
            new XAttribute("id", clipId),
            new XAttribute("clipPathUnits", "userSpaceOnUse"),
            new XElement(Ns + "rect",
                new XAttribute("x", this.N(bounds.X)), new XAttribute("y", this.N(bounds.Y)),
                new XAttribute("width", this.N(bounds.Width)), new XAttribute("height", this.N(bounds.Height)))));

        XElement group = new(Ns + "g", new XAttribute("class", "cad-viewport"), new XAttribute("clip-path", $"url(#{clipId})"));
        this._containers.Peek().Element.Add(group);
        this._containers.Push(new Container(group, $"clip-{this._clipCounter.ToString(CultureInfo.InvariantCulture)}-layer"));
        return new ViewportSurface(this, bounds.X, bounds.Y + bounds.Height);
    }

    public void EndViewport(ViewportSurface viewport)
    {
        if (!ReferenceEquals(viewport.Surface, this))
        {
            throw new InvalidOperationException("EndViewport was called with a viewport from another surface.");
        }

        if (this._containers.Count <= 1)
        {
            throw new InvalidOperationException("EndViewport was called without a matching BeginViewport.");
        }

        this._containers.Pop();
    }

    public void Dispose()
    {
    }

    // ---- element helpers -------------------------------------------------

    private XElement Stroked(XElement element, ImageStyle style)
    {
        (LayerRenderInfo? layer, _) = this.CurrentLayer();
        string stroke = Hex(style.StrokeColor);
        if (layer == null || !string.Equals(stroke, Hex(layer.Color), StringComparison.Ordinal))
        {
            element.Add(new XAttribute("stroke", stroke));
        }

        if (layer == null || Math.Abs(style.StrokeWidth - layer.StrokeWidth) > 1e-6f)
        {
            element.Add(new XAttribute("stroke-width", this.S(style.StrokeWidth)));
        }

        if (style.DashPattern is { Length: > 0 })
        {
            element.Add(new XAttribute("stroke-dasharray", string.Join(" ", style.DashPattern.Select(v => this.S(v)))));
        }

        if (this._options.NonScalingStroke)
        {
            element.Add(new XAttribute("vector-effect", "non-scaling-stroke"));
        }

        this.AddCommon(element, style);
        return element;
    }

    private XElement Filled(XElement element, ImageStyle style)
    {
        element.Add(new XAttribute("fill", Hex(style.StrokeColor)), new XAttribute("stroke", "none"));
        this.AddCommon(element, style);
        return element;
    }

    private void AddCommon(XElement element, ImageStyle style)
    {
        if (style.Opacity < 1f)
        {
            element.Add(new XAttribute("opacity", this.S(Math.Clamp(style.Opacity, 0f, 1f))));
        }

        if (this._options.EmitEntityAttributes && this._entities.Count > 0)
        {
            (EntityRenderInfo info, _) = this._entities.Peek();
            if (info.Handle != 0)
            {
                // Exploded block contents are transient clones with handle 0; a "0" handle would be meaningless to consumers.
                element.Add(new XAttribute("data-handle", info.Handle.ToString("X", CultureInfo.InvariantCulture)));
            }

            element.Add(new XAttribute("data-type", info.EntityType));
            if (info.ParentHandle is ulong parent)
            {
                element.Add(new XAttribute("data-parent", parent.ToString("X", CultureInfo.InvariantCulture)));
            }

            if (!string.IsNullOrEmpty(info.BlockName))
            {
                element.Add(new XAttribute("data-block", info.BlockName));
            }
        }
    }

    private void Append(XElement element)
    {
        this.CurrentLayerGroup().Add(element);
    }

    private (LayerRenderInfo? Layer, string Name) CurrentLayer()
    {
        if (this._entities.Count == 0)
        {
            return (null, "0");
        }

        (EntityRenderInfo info, LayerRenderInfo layer) = this._entities.Peek();
        return (layer, info.LayerName);
    }

    private XElement CurrentLayerGroup()
    {
        (LayerRenderInfo? layer, string name) = this.CurrentLayer();
        Container container = this._containers.Peek();
        if (container.Layers.TryGetValue(name, out XElement? group))
        {
            return group;
        }

        group = new XElement(Ns + "g",
            new XAttribute("id", this.UniqueId(SvgIdSanitizer.Sanitize(this._options.IdPrefix, container.IdKind, name))),
            new XAttribute("class", "cad-layer"),
            new XAttribute("data-layer", name));
        if (layer != null)
        {
            group.Add(new XAttribute("stroke", Hex(layer.Color)), new XAttribute("stroke-width", this.S(layer.StrokeWidth)));
        }

        container.Element.Add(group);
        container.Layers[name] = group;
        return group;
    }

    private string N(double value) => this._numbers.Format(value);

    private string S(double value) => this._styleNumbers.Format(value);

    private string A(double degrees) => this._angleNumbers.Format(degrees);

    private static bool IsFinite(SurfacePoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    private static bool IsFinite(double v) => double.IsFinite(v);

    /// <summary>
    /// Reports geometry dropped because it carried NaN or infinity. The dispatcher filters the known cases; this is the
    /// backstop that keeps such values out of the markup, where they would make the SVG invalid.
    /// </summary>
    private void NotifyNonFinite()
    {
        string handle = this._entities.Count > 0
            ? this._entities.Peek().Info.Handle.ToString("X", CultureInfo.InvariantCulture)
            : "?";
        this._configuration.Notify($"SVG: non-finite geometry skipped for entity {handle}", NotificationType.Warning);
    }

    /// <summary>
    /// Copies the points that carry no NaN or infinity, notifying when any is dropped.
    /// </summary>
    /// <param name="points">Points to filter.</param>
    /// <param name="minimum">Number of points the shape needs.</param>
    /// <returns>The surviving points, or null when fewer than <paramref name="minimum"/> remain.</returns>
    private List<SurfacePoint>? FinitePoints(IReadOnlyList<SurfacePoint> points, int minimum)
    {
        List<SurfacePoint> finite = new(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            if (IsFinite(points[i]))
            {
                finite.Add(points[i]);
            }
        }

        if (finite.Count != points.Count)
        {
            this.NotifyNonFinite();
        }

        return finite.Count >= minimum ? finite : null;
    }

    /// <summary>
    /// Makes an id unique within the document by appending <c>-2</c>, <c>-3</c>, ... Two different names can sanitise
    /// to the same id ("A WALL" and "A-WALL" both become "layer-a-wall") and duplicate ids are invalid markup.
    /// </summary>
    /// <param name="candidate">The sanitised id to issue.</param>
    /// <returns>An id no other element in this document carries.</returns>
    private string UniqueId(string candidate)
    {
        if (this._issuedIds.Add(candidate))
        {
            return candidate;
        }

        int suffix = 2;
        string id;
        do
        {
            id = $"{candidate}-{suffix.ToString(CultureInfo.InvariantCulture)}";
            suffix++;
        }
        while (!this._issuedIds.Add(id));

        return id;
    }

    private string Points(IReadOnlyList<SurfacePoint> points)
    {
        StringBuilder builder = new(points.Count * 12);
        for (int i = 0; i < points.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(this.N(points[i].X)).Append(' ').Append(this.N(points[i].Y));
        }

        return builder.ToString();
    }

    internal static string Hex(ImageColor color)
    {
        Rgba32 pixel = color.ToPixel<Rgba32>();
        return $"#{pixel.R:x2}{pixel.G:x2}{pixel.B:x2}";
    }

    private static string BuildFontStack(string fontFamilyName)
    {
        List<string> families = new();
        string[] candidates = { fontFamilyName, "Arial", "Helvetica", "sans-serif" };
        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !families.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                families.Add(candidate);
            }
        }

        return string.Join(", ", families.Select(f => f.Contains(' ') ? $"'{f}'" : f));
    }

    /// <summary>
    /// A page or viewport group that owns its own set of layer groups. <see cref="IdKind"/> keeps layer ids unique
    /// across containers ("layer" at page level, "clip-N-layer" inside viewport N).
    /// </summary>
    private sealed class Container
    {
        public Container(XElement element, string idKind)
        {
            this.Element = element;
            this.IdKind = idKind;
        }

        public XElement Element { get; }

        public string IdKind { get; }

        public Dictionary<string, XElement> Layers { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
