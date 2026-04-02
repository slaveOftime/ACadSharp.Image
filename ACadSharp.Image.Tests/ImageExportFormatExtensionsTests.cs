namespace ACadSharp.Image.Tests;

public sealed class ImageExportFormatExtensionsTests
{
    [Theory]
    [InlineData("png", ImageExportFormat.Png)]
    [InlineData("bmp", ImageExportFormat.Bmp)]
    [InlineData("jpg", ImageExportFormat.Jpeg)]
    [InlineData("jpeg", ImageExportFormat.Jpeg)]
    [InlineData("gif", ImageExportFormat.Gif)]
    [InlineData("webp", ImageExportFormat.Webp)]
    public void TryParseRecognizesSupportedFormats(string value, ImageExportFormat expected)
    {
        bool parsed = ImageExportFormatExtensions.TryParse(value, out ImageExportFormat actual);

        Assert.True(parsed);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(".png", ImageExportFormat.Png)]
    [InlineData(".jpg", ImageExportFormat.Jpeg)]
    [InlineData(".webp", ImageExportFormat.Webp)]
    public void TryParseFileExtensionRecognizesSupportedExtensions(string extension, ImageExportFormat expected)
    {
        bool parsed = ImageExportFormatExtensions.TryParseFileExtension(extension, out ImageExportFormat actual);

        Assert.True(parsed);
        Assert.Equal(expected, actual);
    }
}
