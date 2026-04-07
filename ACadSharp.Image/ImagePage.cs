using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image;

public sealed class ImagePage
{
    public string Name { get; set; } = "page";

    public Layout Layout { get; set; } = new("default_page");

    public List<Entity> Entities { get; } = new();

    public List<Viewport> Viewports { get; } = new();

    public XY Translation { get; set; } = XY.Zero;

    public void Add(BlockRecord block, bool resizeLayout = true)
    {
        this.Add(block, null, resizeLayout);
    }

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

        this.Layout.PaperUnits = PlotPaperUnits.Pixels;

        if (resizeLayout)
        {
            this.UpdateLayoutSize();
        }
    }

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

        this.Layout.PaperWidth = Math.Max(1d, limits.Max.X);
        this.Layout.PaperHeight = Math.Max(1d, limits.Max.Y);
    }
}
