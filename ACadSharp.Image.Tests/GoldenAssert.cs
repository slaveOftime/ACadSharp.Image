using ACadSharp.Image.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ACadSharp.Image.Tests;

/// <summary>
/// Compares rendered output with the files under <c>Baselines/</c>: PNGs pixel-for-pixel and SVGs as text. With the
/// environment variable <c>ACADSHARP_IMAGE_UPDATE_BASELINES=1</c> the files are rewritten instead of compared.
/// </summary>
internal static class GoldenAssert
{
    public static bool Updating => Environment.GetEnvironmentVariable("ACADSHARP_IMAGE_UPDATE_BASELINES") == "1";

    private static string BaselineDirectory
    {
        get
        {
            string directory = Path.Combine(SampleParityTests.FindRepoRoot(), "ACadSharp.Image.Tests", "Baselines");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static void Png(string baseName, Image<Rgba32> actual)
    {
        string path = Path.Combine(BaselineDirectory, baseName + ".png");
        if (Updating)
        {
            actual.Save(path, new PngEncoder());
            return;
        }

        Assert.True(File.Exists(path), $"Missing baseline {path}. Run with ACADSHARP_IMAGE_UPDATE_BASELINES=1 to create it.");
        using Image<Rgba32> expected = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
        SampleParityTests.AssertPixelsEqual(expected, actual, path);
    }

    public static void Svg(string baseName, string actual)
    {
        string path = Path.Combine(BaselineDirectory, baseName + ".svg");
        string normalized = actual.Replace("\r\n", "\n");
        Assert.DoesNotContain("Infinity", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", normalized, StringComparison.Ordinal);
        if (Updating)
        {
            File.WriteAllText(path, normalized);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden {path}. Run with ACADSHARP_IMAGE_UPDATE_BASELINES=1 to create it.");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), normalized);
    }

    /// <summary>
    /// The darkest (lowest R+G+B) pixel in a small window around <paramref name="point"/>, so an occlusion assertion
    /// survives anti-aliasing and rounding of the fitted coordinates without depending on one exact pixel. Shared by
    /// <c>EntityGoldenTests</c> and <c>FidelityGoldenTests</c>.
    /// </summary>
    internal static Rgba32 DarkestPixelNear(Image<Rgba32> canvas, SurfacePoint point, int radius = 2)
    {
        int centerX = (int)Math.Round(point.X);
        int centerY = (int)Math.Round(point.Y);
        Rgba32 darkest = SixLabors.ImageSharp.Color.White.ToPixel<Rgba32>();
        int darkestLuma = int.MaxValue;
        for (int y = Math.Max(0, centerY - radius); y <= Math.Min(canvas.Height - 1, centerY + radius); y++)
        {
            for (int x = Math.Max(0, centerX - radius); x <= Math.Min(canvas.Width - 1, centerX + radius); x++)
            {
                Rgba32 pixel = canvas[x, y];
                int luma = pixel.R + pixel.G + pixel.B;
                if (luma < darkestLuma)
                {
                    darkestLuma = luma;
                    darkest = pixel;
                }
            }
        }

        return darkest;
    }
}
