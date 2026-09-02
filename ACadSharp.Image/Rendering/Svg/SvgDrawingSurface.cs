using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
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
    private readonly XElement _root;
    private readonly XElement _defs;
    private readonly XElement _defaults;
    private readonly Stack<Container> _containers = new();
    private readonly Stack<(EntityRenderInfo Info, LayerRenderInfo Layer)> _entities = new();
    private int _clipCounter;

    public SvgDrawingSurface(ImageConfiguration configuration, SurfaceRect viewBox, double? sizeWidth, double? sizeHeight)
    {
        this._configuration = configuration;
        this._options = configuration.Svg;
        this._numbers = new SvgNumberFormatter(this._options.Precision ?? SvgNumberFormatter.AdaptiveDecimals(viewBox.Width, viewBox.Height));

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

    public XDocument ToDocument()
    {
        XElement clone = new(this._root);
        if (this._defs.HasElements)
        {
            clone.AddFirst(new XElement(this._defs));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), clone);
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
        this.Append(this.Stroked(new XElement(Ns + "line",
            new XAttribute("x1", this.N(start.X)), new XAttribute("y1", this.N(start.Y)),
            new XAttribute("x2", this.N(end.X)), new XAttribute("y2", this.N(end.Y))), style));
    }

    public void DrawPolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, bool closed)
    {
        if (points.Count < 2)
        {
            return;
        }

        XElement element = new(Ns + (closed ? "polygon" : "polyline"), new XAttribute("points", this.Points(points)));
        this.Append(this.Stroked(element, style));
    }

    public void DrawArc(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation, double startAngle, double sweepAngle)
    {
        throw new NotImplementedException("Task 4");
    }

    public void DrawEllipse(ImageStyle style, SurfacePoint center, double radiusX, double radiusY, double rotation)
    {
        throw new NotImplementedException("Task 4");
    }

    public void DrawCubicBezier(ImageStyle style, IReadOnlyList<SurfacePoint> controlPoints, bool closed)
    {
        throw new NotImplementedException("Task 4");
    }

    public void DrawBulgePolyline(ImageStyle style, IReadOnlyList<SurfacePoint> points, IReadOnlyList<double> bulges, bool closed)
    {
        throw new NotImplementedException("Task 4");
    }

    public void FillPolygon(ImageStyle style, IReadOnlyList<SurfacePoint> points)
    {
        if (points.Count < 3)
        {
            return;
        }

        this.Append(this.Filled(new XElement(Ns + "polygon", new XAttribute("points", this.Points(points))), style));
    }

    public void FillPath(ImageStyle style, IReadOnlyList<IReadOnlyList<SurfacePoint>> rings)
    {
        StringBuilder d = new();
        foreach (IReadOnlyList<SurfacePoint> ring in rings)
        {
            if (ring.Count < 3)
            {
                continue;
            }

            d.Append('M').Append(this.N(ring[0].X)).Append(' ').Append(this.N(ring[0].Y));
            for (int i = 1; i < ring.Count; i++)
            {
                d.Append('L').Append(this.N(ring[i].X)).Append(' ').Append(this.N(ring[i].Y));
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
        this.Append(this.Filled(new XElement(Ns + "circle",
            new XAttribute("cx", this.N(center.X)), new XAttribute("cy", this.N(center.Y)), new XAttribute("r", this.N(radius))), style));
    }

    public void DrawText(ImageStyle style, SurfaceText text)
    {
        throw new NotImplementedException("Task 5");
    }

    public ViewportSurface BeginViewport(SurfaceRect bounds)
    {
        throw new NotImplementedException("Task 6");
    }

    public void EndViewport(ViewportSurface viewport)
    {
        throw new NotImplementedException("Task 6");
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
            element.Add(new XAttribute("stroke-width", this.N(style.StrokeWidth)));
        }

        if (style.DashPattern is { Length: > 0 })
        {
            element.Add(new XAttribute("stroke-dasharray", string.Join(" ", style.DashPattern.Select(v => this.N(v)))));
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
            element.Add(new XAttribute("opacity", this.N(Math.Clamp(style.Opacity, 0f, 1f))));
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
            new XAttribute("id", SvgIdSanitizer.Sanitize(this._options.IdPrefix, container.IdKind, name)),
            new XAttribute("class", "cad-layer"),
            new XAttribute("data-layer", name));
        if (layer != null)
        {
            group.Add(new XAttribute("stroke", Hex(layer.Color)), new XAttribute("stroke-width", this.N(layer.StrokeWidth)));
        }

        container.Element.Add(group);
        container.Layers[name] = group;
        return group;
    }

    private string N(double value) => this._numbers.Format(value);

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
        foreach (string candidate in new[] { fontFamilyName, "Arial", "Helvetica", "sans-serif" })
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
