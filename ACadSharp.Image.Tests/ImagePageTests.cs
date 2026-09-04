using System.Reflection;
using ACadSharp;
using ACadSharp.Entities;
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
}
