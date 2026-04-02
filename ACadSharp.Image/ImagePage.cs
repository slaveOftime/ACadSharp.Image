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
        ArgumentNullException.ThrowIfNull(block);

        this.Entities.AddRange(block.Entities);
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

        BoundingBox limits = BoundingBox.Merge(this.Entities.Select(entity => entity.GetBoundingBox()));
        this.Translation = -(XY)limits.Min;
        limits = limits.Move(-limits.Min);

        this.Layout.PaperWidth = Math.Max(1d, limits.Max.X);
        this.Layout.PaperHeight = Math.Max(1d, limits.Max.Y);
    }
}
