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

public sealed class ImageExporter
{
    public ImageConfiguration Configuration { get; } = new();

    private readonly ImageDocument _document = new();
    private readonly string? _outputPath;

    public ImageExporter()
    {
    }

    public ImageExporter(string outputPath)
    {
        this._outputPath = outputPath;
    }

    public void AddModelSpace(CadDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        this.Add(document.ModelSpace);
    }

    public void AddPaperLayouts(CadDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        this.Add(document.Layouts);
    }

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
            if (entity is Viewport)
            {
                continue;
            }

            if (this.isHiddenLayer(entity))
            {
                continue;
            }

            page.Entities.Add(entity);
        }

        foreach (Viewport viewport in layout.Viewports)
        {
            if (viewport.RepresentsPaper)
            {
                continue;
            }

            page.Viewports.Add(viewport);
        }

        this._document.Pages.Add(page);
    }

    public void Add(BlockRecord block)
    {
        ArgumentNullException.ThrowIfNull(block);

        ImagePage page = new()
        {
            Name = sanitizeFileName(block.Name),
        };

        page.Add(block, entity => !this.isHiddenLayer(entity));
        this._document.Pages.Add(page);
    }

    /// <summary>
    /// Gets a page for testing purposes.
    /// </summary>
    internal ImagePage TestGetPage(int index)
    {
        return this._document.Pages[index];
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

    public IReadOnlyList<RenderedImagePage> Render()
    {
        ImagePageRenderer renderer = new(this.Configuration);
        return this._document.Pages.Select(renderer.Render).ToArray();
    }

    public void Save(string outputPath, ImageExportFormat format = ImageExportFormat.Png)
    {
        this.saveInternal(outputPath, format);
    }

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
                string pagePath = Path.Combine(directory, $"{prefix}-{i + 1:D2}-{pages[i].Name}{getExtension(format)}");
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

    private static string getExtension(ImageExportFormat format)
    {
        return format switch
        {
            ImageExportFormat.Bmp => ".bmp",
            ImageExportFormat.Jpeg => ".jpg",
            ImageExportFormat.Gif => ".gif",
            ImageExportFormat.Webp => ".webp",
            _ => ".png",
        };
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
