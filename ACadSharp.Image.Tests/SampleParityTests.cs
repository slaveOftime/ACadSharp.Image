using ACadSharp.IO;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ACadSharp.Image.Tests;

/// <summary>
/// Renders the files in <c>Samples/</c> with a fixed configuration and compares the result
/// byte-for-byte with the PNGs in <c>Baselines/</c>. Set the environment variable
/// <c>ACADSHARP_IMAGE_UPDATE_BASELINES=1</c> to rewrite the baselines instead of comparing.
/// </summary>
public sealed class SampleParityTests
{
    private const string FontFamily = "DejaVu Sans";

    public static TheoryData<string, bool> Samples => new()
    {
        { "6-57-1119.dxf", false },
        { "HSK80AHCP16190M_BMG.dwg", false },
        { "HSK80AHCP16190M_BMG.dwg", true },
        { "Subaru Logo Vector Free Wrap.dxf", false },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void SampleRendersMatchBaselines(string fileName, bool paperLayouts)
    {
        Assert.True(SystemFonts.TryGet(FontFamily, out _), $"Font '{FontFamily}' must be installed for parity tests.");

        string repoRoot = FindRepoRoot();
        string samplePath = Path.Combine(repoRoot, "Samples", fileName);
        string baselineDirectory = Path.Combine(repoRoot, "ACadSharp.Image.Tests", "Baselines");
        Directory.CreateDirectory(baselineDirectory);

        bool update = Environment.GetEnvironmentVariable("ACADSHARP_IMAGE_UPDATE_BASELINES") == "1";
        string baseName = Path.GetFileNameWithoutExtension(fileName).Replace(' ', '-') + (paperLayouts ? ".paper" : ".model");

        IReadOnlyList<Image<Rgba32>> rendered = RenderSample(samplePath, paperLayouts);
        try
        {
            if (!update)
            {
                int baselineCount = Directory.GetFiles(baselineDirectory, $"{baseName}.*.png").Length;
                Assert.True(baselineCount == rendered.Count, $"Expected {baselineCount} baseline pages for {baseName}, renderer produced {rendered.Count}.");
            }

            for (int i = 0; i < rendered.Count; i++)
            {
                string baselinePath = Path.Combine(baselineDirectory, $"{baseName}.{i + 1:D2}.png");
                if (update)
                {
                    rendered[i].Save(baselinePath, new PngEncoder());
                    continue;
                }

                Assert.True(File.Exists(baselinePath), $"Missing baseline {baselinePath}. Run with ACADSHARP_IMAGE_UPDATE_BASELINES=1 to create it.");
                using Image<Rgba32> baseline = SixLabors.ImageSharp.Image.Load<Rgba32>(baselinePath);
                AssertPixelsEqual(baseline, rendered[i], baselinePath);
            }
        }
        finally
        {
            foreach (Image<Rgba32> image in rendered)
            {
                image.Dispose();
            }
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void SampleSvgsMatchGoldens(string fileName, bool paperLayouts)
    {
        string repoRoot = FindRepoRoot();
        string samplePath = Path.Combine(repoRoot, "Samples", fileName);
        string baselineDirectory = Path.Combine(repoRoot, "ACadSharp.Image.Tests", "Baselines");
        bool update = Environment.GetEnvironmentVariable("ACADSHARP_IMAGE_UPDATE_BASELINES") == "1";
        string baseName = Path.GetFileNameWithoutExtension(fileName).Replace(' ', '-') + (paperLayouts ? ".paper" : ".model");

        CadDocument document = Path.GetExtension(samplePath).ToLowerInvariant() == ".dwg" ? DwgReader.Read(samplePath) : DxfReader.Read(samplePath);
        ImageExporter exporter = new();
        exporter.Configuration.Width = 800;
        exporter.Configuration.Height = 500;
        exporter.Configuration.SetPadding(10);
        exporter.Configuration.FontFamilyName = FontFamily;
        if (paperLayouts)
        {
            exporter.AddPaperLayouts(document);
        }
        else
        {
            exporter.AddModelSpace(document);
        }

        IReadOnlyList<RenderedPage> pages = exporter.Render(ImageExportFormat.Svg);
        if (!update)
        {
            int baselineCount = Directory.GetFiles(baselineDirectory, $"{baseName}.*.svg").Length;
            Assert.True(baselineCount == pages.Count, $"Expected {baselineCount} golden SVG pages for {baseName}, renderer produced {pages.Count}.");
        }

        for (int i = 0; i < pages.Count; i++)
        {
            string goldenPath = Path.Combine(baselineDirectory, $"{baseName}.{i + 1:D2}.svg");
            string actual = Assert.IsType<RenderedSvgPage>(pages[i]).Content.Replace("\r\n", "\n");
            if (update)
            {
                File.WriteAllText(goldenPath, actual);
                continue;
            }

            Assert.True(File.Exists(goldenPath), $"Missing golden {goldenPath}. Run with ACADSHARP_IMAGE_UPDATE_BASELINES=1 to create it.");
            string expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");
            Assert.Equal(expected, actual);
        }
    }

    internal static IReadOnlyList<Image<Rgba32>> RenderSample(string samplePath, bool paperLayouts)
    {
        CadDocument document = Path.GetExtension(samplePath).ToLowerInvariant() == ".dwg"
            ? DwgReader.Read(samplePath)
            : DxfReader.Read(samplePath);

        ImageExporter exporter = new();
        exporter.Configuration.Width = 800;
        exporter.Configuration.Height = 500;
        exporter.Configuration.SetPadding(10);
        exporter.Configuration.FontFamilyName = FontFamily;

        if (paperLayouts)
        {
            exporter.AddPaperLayouts(document);
        }
        else
        {
            exporter.AddModelSpace(document);
        }

        List<Image<Rgba32>> images = new();
        foreach (RenderedPage page in exporter.Render())
        {
            images.Add(Assert.IsType<RenderedImagePage>(page).Canvas);
        }

        return images;
    }

    internal static void AssertPixelsEqual(Image<Rgba32> expected, Image<Rgba32> actual, string label)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        Rgba32[] expectedPixels = new Rgba32[expected.Width * expected.Height];
        Rgba32[] actualPixels = new Rgba32[actual.Width * actual.Height];
        expected.CopyPixelDataTo(expectedPixels);
        actual.CopyPixelDataTo(actualPixels);

        int firstDifference = -1;
        for (int i = 0; i < expectedPixels.Length; i++)
        {
            if (expectedPixels[i] != actualPixels[i])
            {
                firstDifference = i;
                break;
            }
        }

        Assert.True(firstDifference < 0, $"{label}: first differing pixel at index {firstDifference} (x={firstDifference % expected.Width}, y={firstDifference / expected.Width}); expected {expectedPixels[Math.Max(0, firstDifference)]} actual {actualPixels[Math.Max(0, firstDifference)]}.");
    }

    internal static string FindRepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory, "ACadSharp.Image.sln")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Could not locate the repository root (ACadSharp.Image.sln).");
    }
}
