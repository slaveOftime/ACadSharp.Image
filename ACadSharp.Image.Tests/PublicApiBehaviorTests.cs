using System.Collections.Generic;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;

namespace ACadSharp.Image.Tests;

public sealed class PublicApiBehaviorTests
{
    [Fact]
    public void PagesCollectionIsReadOnly()
    {
        ImageExporter exporter = new();
        IList<ImagePage> pages = Assert.IsAssignableFrom<IList<ImagePage>>(exporter.Pages);

        Assert.True(pages.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => pages.Add(new ImagePage()));
    }

    [Fact]
    public void PageCollectionsAreReadOnly()
    {
        ImagePage page = new();
        page.AddEntity(new Line(new XYZ(0, 0, 0), new XYZ(1, 1, 0)));
        page.AddViewport(new Viewport());

        IList<Entity> entities = Assert.IsAssignableFrom<IList<Entity>>(page.Entities);
        IList<Viewport> viewports = Assert.IsAssignableFrom<IList<Viewport>>(page.Viewports);

        Assert.True(entities.IsReadOnly);
        Assert.True(viewports.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => entities.Add(new Line(new XYZ(1, 1, 0), new XYZ(2, 2, 0))));
        Assert.Throws<NotSupportedException>(() => viewports.Add(new Viewport()));
    }

    [Fact]
    public void SaveMultiplePagesUsesIndexedOutputNames()
    {
        string outputDirectory = Path.Combine(Path.GetTempPath(), $"acadsharp-image-tests-{Guid.NewGuid():N}");

        try
        {
            ImageExporter exporter = new();

            BlockRecord first = new("First");
            first.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(5, 5, 0)));

            BlockRecord second = new("Second");
            second.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)));

            exporter.Add(first);
            exporter.Add(second);
            exporter.Save(outputDirectory, ImageExportFormat.Png);

            Assert.True(File.Exists(Path.Combine(outputDirectory, "page-01-First.png")));
            Assert.True(File.Exists(Path.Combine(outputDirectory, "page-02-Second.png")));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }
}
