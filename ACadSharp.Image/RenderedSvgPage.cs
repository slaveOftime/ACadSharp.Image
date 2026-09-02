using System.Text;

namespace ACadSharp.Image;

/// <summary>
/// A page rendered to SVG markup.
/// </summary>
public sealed class RenderedSvgPage : RenderedPage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RenderedSvgPage"/> class.
    /// </summary>
    /// <param name="name">Page name.</param>
    /// <param name="content">SVG markup for the page.</param>
    public RenderedSvgPage(string name, string content)
        : base(name, ImageExportFormat.Svg)
    {
        this.Content = content;
    }

    /// <summary>
    /// Gets the SVG markup as text. It carries no XML declaration so it can be inlined in HTML; <see cref="Save(Stream)"/> writes UTF-8 without a BOM.
    /// </summary>
    public string Content { get; }

    /// <inheritdoc/>
    public override void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] bytes = new UTF8Encoding(false).GetBytes(this.Content);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
    }
}
