using System.Reflection;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class ImagePageTests
{
    private static T WithHandle<T>(T entity, ulong handle)
        where T : CadObject
    {
        typeof(CadObject).GetProperty(nameof(CadObject.Handle))!.SetValue(entity, handle);
        return entity;
    }

    [Fact]
    public void AddOrdersEntitiesByHandleNotByInsertionOrder()
    {
        BlockRecord block = new("ORDER");
        Line later = WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)), 0x20);
        Line earlier = WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)), 0x10);
        block.Entities.Add(later);
        block.Entities.Add(earlier);

        ImagePage page = new();
        page.Add(block, resizeLayout: false);

        Assert.Equal([0x10UL, 0x20UL], page.Entities.Select(e => e.Handle));
    }

    [Fact]
    public void AddHonoursTheDrawOrderTable()
    {
        BlockRecord block = new("ORDER");
        Line low = WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)), 0x10);
        Line high = WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)), 0x20);
        block.Entities.Add(low);
        block.Entities.Add(high);
        block.CreateSortEntitiesTable();
        block.SortEntitiesTable!.Add(low, 0x30); // the low-handle entity is sorted last

        ImagePage page = new();
        page.Add(block, resizeLayout: false);

        Assert.Equal([0x20UL, 0x10UL], page.Entities.Select(e => e.Handle));
    }

    [Fact]
    public void AddWithFilterKeepsTheSortedOrder()
    {
        BlockRecord block = new("ORDER");
        block.Entities.Add(WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)), 0x30));
        block.Entities.Add(WithHandle(new Circle { Center = new XYZ(0, 0, 0), Radius = 1 }, 0x20));
        block.Entities.Add(WithHandle(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)), 0x10));

        ImagePage page = new();
        page.Add(block, e => e is Line, resizeLayout: false);

        Assert.Equal([0x10UL, 0x30UL], page.Entities.Select(e => e.Handle));
    }

    [Fact]
    public void DrawSequenceKeepsViewportsAndEntitiesInInsertionOrder()
    {
        ImagePage page = new();
        Line first = new(new XYZ(0, 0, 0), new XYZ(1, 0, 0));
        Viewport viewport = new() { Center = new XYZ(50, 50, 0), Width = 10, Height = 10 };
        Line last = new(new XYZ(0, 0, 0), new XYZ(0, 1, 0));

        page.AddEntity(first);
        page.AddViewport(viewport);
        page.AddEntity(last);

        Assert.Equal([first, viewport, last], page.DrawSequence);
        Assert.Equal([first, last], page.Entities);
        Assert.Equal([viewport], page.Viewports);
    }

    [Fact]
    public void FrameUsesTheMappedWipeoutRegionNotTheRawPixelVertices()
    {
        // Pixel space rotated 90 degrees: U up, V left. Raw vertices span 1 unit; the mapped region spans 5.
        Wipeout wipeout = new()
        {
            InsertPoint = new XYZ(10, 10, 0),
            UVector = new XYZ(0, 5, 0),
            VVector = new XYZ(-5, 0, 0),
            Size = new XY(1, 1),
            ClippingState = true,
            ClipType = ClipType.Rectangular,
        };
        wipeout.ClipBoundaryVertices.AddRange([new XY(-0.5, -0.5), new XY(0.5, 0.5)]);
        ImagePage page = new();
        page.AddEntity(wipeout);

        PageFrame frame = Assert.NotNull(page.ComputeFrame(null));

        // Mapped corners: (10,10)+(x+0.5)U+(1-y-0.5)V for the four corners → x in [5,10], y in [10,15].
        Assert.Equal(5d, frame.PaperWidth, 6);
        Assert.Equal(5d, frame.PaperHeight, 6);
    }

    [Fact]
    public void FrameMirrorsAnOcsSolidLikeTheRenderer()
    {
        Solid solid = new() { FirstCorner = new XYZ(0, 0, 0), SecondCorner = new XYZ(10, 0, 0), ThirdCorner = new XYZ(0, 5, 0), FourthCorner = new XYZ(10, 5, 0), Normal = new XYZ(0, 0, -1) };
        ImagePage page = new();
        page.AddEntity(solid);

        PageFrame frame = Assert.NotNull(page.ComputeFrame(null));

        // A (0,0,-1) normal mirrors X: the solid spans x in [-10, 0]. Translation is -min (PageFrame.Of / ComputeFrame),
        // so the mirrored solid's translation is 10.
        Assert.Equal(10d, frame.Translation.X, 6);
        Assert.Equal(10d, frame.PaperWidth, 6);
        Assert.Equal(5d, frame.PaperHeight, 6);
    }

    [Fact]
    public void FrameSkipsAnInsertWithoutABlock()
    {
        Insert insert = new(new BlockRecord("GONE")) { InsertPoint = new XYZ(1000, 1000, 0) };
        typeof(Insert).GetProperty(nameof(Insert.Block))!.SetValue(insert, null);
        ImagePage page = new();
        page.AddEntity(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0)));
        page.AddEntity(insert);

        PageFrame frame = Assert.NotNull(page.ComputeFrame(null));

        Assert.Equal(10d, frame.PaperWidth, 6);
    }
}
