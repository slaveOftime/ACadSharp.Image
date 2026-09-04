using System.Text;
using System.Collections.ObjectModel;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ACadSharp.Image.Rendering;

namespace ACadSharp.Image;

/// <summary>
/// Exports CAD drawings to raster images or SVG.
/// </summary>
/// <remarks>
/// The <see cref="ImageExporter"/> is the main entry point for exporting CAD content to images.
/// Use <see cref="AddModelSpace"/> or <see cref="AddPaperLayouts"/> to add content, then call
/// <see cref="Save(string, ImageExportFormat)"/>
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
    private static readonly HashSet<char> s_invalidFileNameCharacters = Path.GetInvalidFileNameChars().ToHashSet();

    private readonly List<ImagePage> _pages = [];

    private readonly ReadOnlyCollection<ImagePage> _readOnlyPages;

    /// <summary>
    /// Gets the configuration for this exporter.
    /// </summary>
    public ImageConfiguration Configuration { get; } = new();

    /// <summary>
    /// Gets the collection of pages that have been added to this exporter.
    /// </summary>
    public IReadOnlyList<ImagePage> Pages => this._readOnlyPages;

    /// <summary>
    /// Creates a new instance of <see cref="ImageExporter"/> without an output path.
    /// Use <see cref="Save(string, ImageExportFormat)"/> to specify the output location.
    /// </summary>
    public ImageExporter()
    {
        this._readOnlyPages = this._pages.AsReadOnly();
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
    /// <remarks>
    /// Layer filters and visibility settings are applied when rendering, so all entities are kept on the page.
    /// Paper entities and viewports are taken from one sorted pass over the layout's block, so the page keeps
    /// the drawing's draw order between them.
    /// </remarks>
    public void Add(Layout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        ImagePage page = new()
        {
            Layout = layout,
            Name = SanitizeFileName(layout.Name),
            Document = layout.Document,
        };

        foreach (Entity entity in layout.AssociatedBlock.GetSortedEntities())
        {
            if (entity is Viewport viewport)
            {
                // The paper viewport is the sheet itself, not a window onto model space.
                if (!viewport.RepresentsPaper)
                {
                    page.AddViewport(viewport);
                }

                continue;
            }

            page.AddEntity(entity);
        }

        this._pages.Add(page);
    }

    /// <summary>
    /// Adds a block record to the exporter as a single page.
    /// </summary>
    /// <param name="block">The block record to add.</param>
    /// <remarks>
    /// Layer filters and visibility settings are applied when rendering, so all entities are kept on the page.
    /// </remarks>
    public void Add(BlockRecord block)
    {
        ArgumentNullException.ThrowIfNull(block);

        ImagePage page = new()
        {
            Name = SanitizeFileName(block.Name),
            Document = block.Document,
        };

        page.Add(block, ShouldIncludeEntity);
        this._pages.Add(page);
    }

    /// <summary>
    /// Viewports are added through <see cref="ImagePage.AddViewport"/>, never as page entities.
    /// </summary>
    /// <param name="entity">The entity being considered.</param>
    /// <returns>True when the entity belongs on the page.</returns>
    private static bool ShouldIncludeEntity(Entity entity) => entity is not Viewport;

    /// <summary>
    /// Renders all added pages without saving to disk.
    /// </summary>
    /// <param name="format">Output format the pages will be saved as. Defaults to PNG.</param>
    /// <returns>Rendered pages; dispose each when finished.</returns>
    public IReadOnlyList<RenderedPage> Render(ImageExportFormat format = ImageExportFormat.Png)
    {
        ImagePageRenderer renderer = new(this.Configuration);
        RenderedPage[] pages = new RenderedPage[this._pages.Count];
        for (int i = 0; i < this._pages.Count; i++)
        {
            pages[i] = renderer.Render(this._pages[i], format);
        }

        return pages;
    }

    /// <summary>
    /// Renders all added pages and saves the output to the specified path.
    /// </summary>
    /// <param name="outputPath">A file path when there is one page, or a directory when there are several.</param>
    /// <param name="format">The output format. Defaults to PNG.</param>
    public void Save(string outputPath, ImageExportFormat format = ImageExportFormat.Png)
    {
        IReadOnlyList<RenderedPage> pages = this.Render(format);

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
                pages[0].Save(fullPath);
                return;
            }

            string directory = string.IsNullOrWhiteSpace(extension)
                ? fullPath
                : Path.GetDirectoryName(fullPath)!;

            string prefix = string.IsNullOrWhiteSpace(extension)
                ? "page"
                : Path.GetFileNameWithoutExtension(fullPath);

            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Save(Path.Combine(directory, $"{prefix}-{i + 1:D2}-{pages[i].Name}{format.GetFileExtension()}"));
            }
        }
        finally
        {
            foreach (RenderedPage page in pages)
            {
                page.Dispose();
            }
        }
    }

    private static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "page";
        }

        StringBuilder builder = new(value.Length);

        foreach (char c in value)
        {
            builder.Append(s_invalidFileNameCharacters.Contains(c) ? '_' : c);
        }

        return builder.ToString();
    }
}
