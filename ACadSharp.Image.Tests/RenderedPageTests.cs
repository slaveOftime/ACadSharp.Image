using ACadSharp.Entities;
using ACadSharp.Image.Rendering;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ACadSharp.Image.Tests;

public sealed class RenderedPageTests
{
    [Theory]
    [InlineData(ImageExportFormat.Png, new byte[] { 0x89, 0x50, 0x4E, 0x47 }, null)]
    [InlineData(ImageExportFormat.Bmp, new byte[] { 0x42, 0x4D }, null)]
    [InlineData(ImageExportFormat.Jpeg, new byte[] { 0xFF, 0xD8, 0xFF }, null)]
    [InlineData(ImageExportFormat.Gif, new byte[] { 0x47, 0x49, 0x46, 0x38 }, null)]
    [InlineData(ImageExportFormat.Webp, new byte[] { 0x52, 0x49, 0x46, 0x46 }, "WEBP")]
    public void RasterPagesEncodeInTheirFormat(ImageExportFormat format, byte[] signature, string? fourcc)
    {
        using Image<Rgba32> canvas = new(8, 8, SixLabors.ImageSharp.Color.White);
        using RenderedImagePage page = new("p", canvas, format, 80);
        using MemoryStream stream = new();

        page.Save(stream);

        byte[] bytes = stream.ToArray();
        Assert.True(bytes.Length > signature.Length);
        Assert.Equal(signature, bytes.Take(signature.Length).ToArray());
        if (fourcc is not null)
        {
            Assert.Equal(fourcc, System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        }
    }

    [Fact]
    public void SaveToPathCreatesTheDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"acad-image-{Guid.NewGuid():N}", "nested");
        string path = Path.Combine(directory, "page.svg");
        try
        {
            using RenderedSvgPage page = new("p", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");

            page.Save(path);

            Assert.True(File.Exists(path));
            byte[] bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "SVG files must be written without a BOM.");
            Assert.StartsWith("<svg", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(directory)!))
            {
                Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(ImageExportFormat.Png, ".png")]
    [InlineData(ImageExportFormat.Bmp, ".bmp")]
    [InlineData(ImageExportFormat.Jpeg, ".jpg")]
    [InlineData(ImageExportFormat.Gif, ".gif")]
    [InlineData(ImageExportFormat.Webp, ".webp")]
    [InlineData(ImageExportFormat.Svg, ".svg")]
    public void EveryFormatHasAnExtensionAndRoundTripsThroughParsing(ImageExportFormat format, string extension)
    {
        Assert.Equal(extension, format.GetFileExtension());
        Assert.True(ImageExportFormatExtensions.TryParseFileExtension(extension, out ImageExportFormat fromExtension));
        Assert.Equal(format, fromExtension);
        Assert.True(ImageExportFormatExtensions.TryParse(extension.TrimStart('.').ToUpperInvariant(), out ImageExportFormat fromName));
        Assert.Equal(format, fromName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tiff")]
    [InlineData(".tiff")]
    public void UnknownFormatsDoNotParse(string? value)
    {
        Assert.False(ImageExportFormatExtensions.TryParse(value, out _));
        Assert.False(ImageExportFormatExtensions.TryParseFileExtension(value, out _));
    }

    [Theory]
    [InlineData(UnitsType.Millimeters, 1d)]
    [InlineData(UnitsType.Centimeters, 0.1d)]
    [InlineData(UnitsType.Decimeters, 0.01d)]
    [InlineData(UnitsType.Meters, 0.001d)]
    [InlineData(UnitsType.Kilometers, 0.000001d)]
    [InlineData(UnitsType.Microns, 1000d)]
    [InlineData(UnitsType.Inches, 1d / 25.4d)]
    [InlineData(UnitsType.Feet, 1d / 304.8d)]
    [InlineData(UnitsType.Yards, 1d / 914.4d)]
    [InlineData(UnitsType.Miles, 1d / 1609344d)]
    [InlineData(UnitsType.Unitless, 1d)]
    [InlineData(UnitsType.Parsecs, 1d)]
    public void DrawingUnitsPerMillimetreCoverTheHeaderUnits(UnitsType units, double expected)
    {
        Assert.Equal(expected, ImageRenderContext.UnitsPerMillimeter(units), 12);
    }

    [Fact]
    public void PageEntityFilterIsAppliedAtAddTime()
    {
        BlockRecord block = new("filtered");
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1, 0, 0)) { Layer = new Layer("Keep") });
        block.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(0, 1, 0)) { Layer = new Layer("Drop") });
        ImagePage page = new();

        page.Add(block, e => e.Layer.Name == "Keep");

        Assert.Single(page.Entities);
        Assert.Equal("Keep", page.Entities[0].Layer.Name);
        Assert.True(page.AutoSized);
        Assert.Equal(1d, page.Layout!.PaperWidth); // extents 1 x 0 are clamped to at least 1 unit
        Assert.Equal(1d, page.Layout.PaperHeight);
    }
}
