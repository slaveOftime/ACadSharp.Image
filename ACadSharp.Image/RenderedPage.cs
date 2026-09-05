namespace ACadSharp.Image;

/// <summary>
/// A rendered page produced by <see cref="ImageExporter.Render(ImageExportFormat)"/>, ready to be saved in its <see cref="Format"/>.
/// </summary>
public abstract class RenderedPage : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RenderedPage"/> class.
    /// </summary>
    /// <param name="name">The name of the page.</param>
    /// <param name="format">The format this page will be saved as.</param>
    protected RenderedPage(string name, ImageExportFormat format)
    {
        this.Name = name;
        this.Format = format;
    }

    /// <summary>
    /// Gets the name of this page (layout name or block name).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the format this page will be saved as.
    /// </summary>
    public ImageExportFormat Format { get; }

    /// <summary>
    /// Saves the page to a file, creating the directory if needed.
    /// </summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using FileStream stream = new(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        this.Save(stream);
    }

    /// <summary>
    /// Writes the page to a stream in its <see cref="Format"/>.
    /// </summary>
    public abstract void Save(Stream stream);

    /// <inheritdoc/>
    public abstract void Dispose();
}
