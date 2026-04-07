using System.Text;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ACadSharp.Image.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace ACadSharp.Image;

/// <summary>
/// Exports CAD drawings to raster images in various formats.
/// </summary>
/// <remarks>
/// The <see cref="ImageExporter"/> is the main entry point for exporting CAD content to images.
/// Use <see cref="AddModelSpace"/> or <see cref="AddPaperLayouts"/> to add content, then call
/// <see cref="Save(string, ImageExportFormat)"/> or <see cref="Close(ImageExportFormat)"/> to render and save.
/// </remarks>
/// <example>
/// <code>
/// var exporter = new ImageExporter("output.png");
/// exporter.AddModelSpace(document);
/// exporter.Close();
/// </code>
/// </example>
public sealed class ImageExporter
{
    /// <summary>
    /// Gets the configuration for this exporter.
    /// </summary>
    public ImageConfiguration Configuration { get; } = new();

    /// <summary>
    /// Gets the collection of pages that have been added to this exporter.
    /// </summary>
    public IList<ImagePage> Pages { get; } = new List<ImagePage>();

    private readonly string? _outputPath;

    /// <summary>
    /// Creates a new instance of <see cref="ImageExporter"/> without an output path.
    /// Use <see cref="Save(string, ImageExportFormat)"/> to specify the output location.
    /// </summary>
    public ImageExporter()
    {
    }

    /// <summary>
    /// Creates a new instance of <see cref="ImageExporter"/> with a predefined output path.
    /// </summary>
    /// <param name="outputPath">The file path where the image will be saved.</param>
    public ImageExporter(string outputPath)
    {
        this._outputPath = outputPath;
    }

    /// <summary>
    /// Adds the model space from the specified document to the exporter.
    /// </summary>
    /// <param name="document">The CAD document containing the model space.</param>
    public void AddModelSpace(CadDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        this.Add(document.ModelSpace);
    }

    /// <summary>
    /// Adds all paper layouts from the specified document to the exporter.
    /// </summary>
    /// <param name="document">The CAD document containing the layouts.</param>
    public void AddPaperLayouts(CadDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        this.Add(document.Layouts);
    }

    /// <summary>
    /// Adds a collection of layouts to the exporter.
    /// Only paper space layouts are added; model space layouts are skipped.
    /// </summary>
    /// <param name="layouts">The layouts to add.</param>
    public void Add(IEnumerable<Layout> layouts)
    {
        ArgumentNullException.ThrowIfNull(layouts);

        foreach (Layout layout in layouts)
        {
            if (!layout.IsPaperSpace)
            {
                continue;
            }

            this.Add(layout);
        }
    }

    /// <summary>
    /// Adds a single layout to the exporter.
    /// </summary>
    /// <param name="layout">The layout to add.</param>
    public void Add(Layout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        ImagePage page = new()
        {
            Layout = layout,
            Name = sanitizeFileName(layout.Name),
        };

        foreach (Entity entity in layout.AssociatedBlock.Entities)
        {
            if (this.shouldIncludeEntity(entity))
            {
                page.Entities.Add(entity);
            }
        }

        foreach (Viewport viewport in layout.Viewports)
        {
            if (viewport.RepresentsPaper)
            {
                continue;
            }

            page.Viewports.Add(viewport);
        }

        this.Pages.Add(page);
    }

    /// <summary>
    /// Adds a block record to the exporter as a single page.
    /// </summary>
    /// <param name="block">The block record to add.</param>
    public void Add(BlockRecord block)
    {
        ArgumentNullException.ThrowIfNull(block);

        ImagePage page = new()
        {
            Name = sanitizeFileName(block.Name),
        };

        page.Add(block, this.shouldIncludeEntity);
        this.Pages.Add(page);
    }

    private bool shouldIncludeEntity(Entity entity)
    {
        if (entity is Viewport)
        {
            return false;
        }

        return !this.isHiddenLayer(entity);
    }

    private bool isHiddenLayer(Entity entity)
    {
        if (this.Configuration.HiddenLayers.Count == 0)
        {
            return false;
        }

        string? layerName = entity.Layer?.Name;
        if (string.IsNullOrEmpty(layerName))
        {
            return false;
        }

        return this.Configuration.HiddenLayers.Contains(layerName);
    }

    /// <summary>
    /// Renders all added pages to image format without saving to disk.
    /// </summary>
    /// <returns>A list of rendered image pages.</returns>
    /// <remarks>
    /// The returned pages must be disposed after use to free resources.
    /// This method is useful for custom processing or testing without file I/O.
    /// </remarks>
    public IReadOnlyList<RenderedImagePage> Render()
    {
        ImagePageRenderer renderer = new(this.Configuration);
        return this.Pages.Select(renderer.Render).ToArray();
    }

    /// <summary>
    /// Renders all added pages and saves the output to the specified path.
    /// </summary>
    /// <param name="outputPath">The file path to save the image to.</param>
    /// <param name="format">The image format to use. Defaults to PNG.</param>
    public void Save(string outputPath, ImageExportFormat format = ImageExportFormat.Png)
    {
        this.saveInternal(outputPath, format);
    }

    /// <summary>
    /// Renders all added pages and saves the output to the path specified in the constructor.
    /// </summary>
    /// <param name="format">The image format to use. Defaults to PNG.</param>
    /// <exception cref="InvalidOperationException">Thrown when the exporter was not created with an output path.</exception>
    public void Close(ImageExportFormat format = ImageExportFormat.Png)
    {
        if (string.IsNullOrWhiteSpace(this._outputPath))
        {
            throw new InvalidOperationException("The exporter was not created with an output path.");
        }

        this.saveInternal(this._outputPath, format);
    }

    private void saveInternal(string outputPath, ImageExportFormat format)
    {
        IReadOnlyList<RenderedImagePage> pages = this.Render();

        try
        {
            if (pages.Count == 0)
            {
                throw new InvalidOperationException("There are no pages to export.");
            }

            string fullPath = Path.GetFullPath(outputPath);
            string? extension = Path.GetExtension(fullPath);

            if (pages.Count == 1 && !string.IsNullOrWhiteSpace(extension))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                this.savePage(pages[0], fullPath, format);
                return;
            }

            string directory = string.IsNullOrWhiteSpace(extension)
                ? fullPath
                : Path.GetDirectoryName(fullPath)!;

            string prefix = string.IsNullOrWhiteSpace(extension)
                ? "page"
                : Path.GetFileNameWithoutExtension(fullPath);

            Directory.CreateDirectory(directory);

            for (int i = 0; i < pages.Count; i++)
            {
                string pagePath = Path.Combine(directory, $"{prefix}-{i + 1:D2}-{pages[i].Name}{format.GetFileExtension()}");
                this.savePage(pages[i], pagePath, format);
            }
        }
        finally
        {
            foreach (RenderedImagePage page in pages)
            {
                page.Dispose();
            }
        }
    }

    private void savePage(RenderedImagePage page, string path, ImageExportFormat format)
    {
        switch (format)
        {
            case ImageExportFormat.Bmp:
                page.Canvas.Save(path, new BmpEncoder());
                break;
            case ImageExportFormat.Jpeg:
                page.Canvas.Save(path, new JpegEncoder { Quality = this.Configuration.OutputQuality });
                break;
            case ImageExportFormat.Gif:
                page.Canvas.Save(path, new GifEncoder());
                break;
            case ImageExportFormat.Webp:
                page.Canvas.Save(path, new WebpEncoder { Quality = this.Configuration.OutputQuality });
                break;
            case ImageExportFormat.Png:
            default:
                page.Canvas.Save(path, new PngEncoder());
                break;
        }
    }

    private static string sanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "page";
        }

        StringBuilder builder = new(value.Length);
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();

        foreach (char c in value)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }

        return builder.ToString();
    }
}
