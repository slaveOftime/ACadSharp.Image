using System.Collections.ObjectModel;
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
    private readonly List<Entity> _entities = [];

    private readonly List<Viewport> _viewports = [];

    private readonly ReadOnlyCollection<Entity> _readOnlyEntities;

    private readonly ReadOnlyCollection<Viewport> _readOnlyViewports;

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
    public IReadOnlyList<Entity> Entities => this._readOnlyEntities;

    /// <summary>
    /// Gets the collection of viewports to be rendered on this page.
    /// </summary>
    public IReadOnlyList<Viewport> Viewports => this._readOnlyViewports;

    /// <summary>
    /// Gets or sets the document the page content came from, when known. Used for header settings such as units and linetype scale.
    /// </summary>
    public CadDocument? Document { get; set; }

    /// <summary>
    /// Gets or sets the translation offset applied to the page content.
    /// </summary>
    public XY Translation { get; set; } = XY.Zero;

    /// <summary>
    /// Gets the paper units used for this page (pixels).
    /// </summary>
    internal PlotPaperUnits PaperUnits => PlotPaperUnits.Pixels;

    /// <summary>
    /// Gets a value indicating whether the page size came from <see cref="UpdateLayoutSize()"/> rather than from a layout's paper size.
    /// Auto-sized pages are re-framed on the visible entities at render time.
    /// </summary>
    internal bool AutoSized { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ImagePage"/> class.
    /// </summary>
    public ImagePage()
    {
        this._readOnlyEntities = this._entities.AsReadOnly();
        this._readOnlyViewports = this._viewports.AsReadOnly();
    }

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
                    this.AddEntity(entity);
                }
            }
        }
        else
        {
            foreach (Entity entity in block.Entities)
            {
                this.AddEntity(entity);
            }
        }

        if (resizeLayout)
        {
            this.UpdateLayoutSize();
        }
    }

    /// <summary>
    /// Adds a single entity to this page.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    public void AddEntity(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        this._entities.Add(entity);
    }

    /// <summary>
    /// Adds a single viewport to this page.
    /// </summary>
    /// <param name="viewport">The viewport to add.</param>
    public void AddViewport(Viewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        this._viewports.Add(viewport);
    }

    /// <summary>
    /// Updates the layout size based on the bounding box of all entities on this page.
    /// </summary>
    public void UpdateLayoutSize()
    {
        this.UpdateLayoutSize(null);
    }

    /// <summary>
    /// Updates the layout size based on the bounding box of the entities the predicate accepts.
    /// </summary>
    /// <param name="include">Predicate selecting the entities to frame, or null to frame every entity.</param>
    /// <remarks>
    /// When no selected entity has finite bounds, <see cref="Translation"/> and the layout size are left unchanged.
    /// </remarks>
    public void UpdateLayoutSize(Func<Entity, bool>? include)
    {
        if (this._entities.Count == 0)
        {
            return;
        }

        bool hasValidBounds = false;
        double minX = 0d;
        double minY = 0d;
        double minZ = 0d;
        double maxX = 0d;
        double maxY = 0d;
        double maxZ = 0d;

        foreach (Entity entity in this._entities)
        {
            if (include != null && !include(entity))
            {
                continue;
            }

            BoundingBox boundingBox = entity.GetBoundingBox();
            // NaN and infinity both occur in the wild (Samples/6-57-1119.dxf has an ARC with an infinite radius)
            // and either would poison the page size.
            if (!double.IsFinite(boundingBox.Min.X) || !double.IsFinite(boundingBox.Min.Y) || !double.IsFinite(boundingBox.Min.Z) ||
                !double.IsFinite(boundingBox.Max.X) || !double.IsFinite(boundingBox.Max.Y) || !double.IsFinite(boundingBox.Max.Z))
            {
                continue;
            }

            if (!hasValidBounds)
            {
                minX = boundingBox.Min.X;
                minY = boundingBox.Min.Y;
                minZ = boundingBox.Min.Z;
                maxX = boundingBox.Max.X;
                maxY = boundingBox.Max.Y;
                maxZ = boundingBox.Max.Z;
                hasValidBounds = true;
                continue;
            }

            minX = Math.Min(minX, boundingBox.Min.X);
            minY = Math.Min(minY, boundingBox.Min.Y);
            minZ = Math.Min(minZ, boundingBox.Min.Z);
            maxX = Math.Max(maxX, boundingBox.Max.X);
            maxY = Math.Max(maxY, boundingBox.Max.Y);
            maxZ = Math.Max(maxZ, boundingBox.Max.Z);
        }

        if (!hasValidBounds)
        {
            return;
        }

        BoundingBox limits = new(minX, minY, minZ, maxX, maxY, maxZ);
        this.Translation = -(XY)limits.Min;
        limits = limits.Move(-limits.Min);

        this.Layout ??= new Layout("default_page");
        this.Layout.PaperWidth = Math.Max(1d, limits.Max.X);
        this.Layout.PaperHeight = Math.Max(1d, limits.Max.Y);
        this.AutoSized = true;
    }
}
