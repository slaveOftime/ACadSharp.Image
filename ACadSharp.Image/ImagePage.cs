using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image;

/// <summary>
/// Represents a single exportable page containing CAD entities and layout metadata.
/// </summary>
public sealed class ImagePage
{
    /// <summary>
    /// Gets or sets the name of this page.
    /// </summary>
    public string Name { get; set; } = "page";

    /// <summary>
    /// Gets or sets the layout associated with this page.
    /// </summary>
    public Layout? Layout { get; set; }

    /// <summary>
    /// Gets the collection of entities to be rendered on this page.
    /// </summary>
    public List<Entity> Entities { get; } = new();

    /// <summary>
    /// Gets the collection of viewports to be rendered on this page.
    /// </summary>
    public List<Viewport> Viewports { get; } = new();

    /// <summary>
    /// Gets or sets the translation offset applied to the page content.
    /// </summary>
    public XY Translation { get; set; } = XY.Zero;

    /// <summary>
    /// Gets the paper units used for this page (pixels).
    /// </summary>
    internal PlotPaperUnits PaperUnits => PlotPaperUnits.Pixels;

    /// <summary>
    /// Adds entities from a block record to this page.
    /// </summary>
    /// <param name="block">The block record to add entities from.</param>
    /// <param name="resizeLayout">Whether to automatically calculate layout bounds. Defaults to true.</param>
    public void Add(BlockRecord block, bool resizeLayout = true)
    {
        this.Add(block, null, resizeLayout);
    }

    /// <summary>
    /// Adds entities from a block record to this page with optional filtering.
    /// </summary>
    /// <param name="block">The block record to add entities from.</param>
    /// <param name="entityFilter">Optional predicate to filter entities. Return true to include the entity.</param>
    /// <param name="resizeLayout">Whether to automatically calculate layout bounds. Defaults to true.</param>
    public void Add(BlockRecord block, Func<Entity, bool>? entityFilter, bool resizeLayout = true)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (entityFilter != null)
        {
            foreach (Entity entity in block.Entities)
            {
                if (entityFilter(entity))
                {
                    this.Entities.Add(entity);
                }
            }
        }
        else
        {
            this.Entities.AddRange(block.Entities);
        }

        if (resizeLayout)
        {
            this.UpdateLayoutSize();
        }
    }

    /// <summary>
    /// Updates the layout size based on the bounding box of all entities on this page.
    /// </summary>
    public void UpdateLayoutSize()
    {
        if (this.Entities.Count == 0)
        {
            return;
        }

        // Filter out invalid bounding boxes (those with NaN values)
        var validBoxes = this.Entities
            .Select(entity => entity.GetBoundingBox())
            .Where(bbox => !double.IsNaN(bbox.Min.X) && !double.IsNaN(bbox.Min.Y) &&
                          !double.IsNaN(bbox.Max.X) && !double.IsNaN(bbox.Max.Y))
            .ToList();

        if (validBoxes.Count == 0)
        {
            return;
        }

        BoundingBox limits = BoundingBox.Merge(validBoxes);
        this.Translation = -(XY)limits.Min;
        limits = limits.Move(-limits.Min);

        this.Layout ??= new Layout("default_page");
        this.Layout.PaperWidth = Math.Max(1d, limits.Max.X);
        this.Layout.PaperHeight = Math.Max(1d, limits.Max.Y);
    }
}
