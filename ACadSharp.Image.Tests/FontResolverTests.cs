using ACadSharp.Image.Rendering;
using SixLabors.Fonts;

namespace ACadSharp.Image.Tests;

/// <summary>Checks that missing font families fall back along the documented chain rather than to an arbitrary face.</summary>
public sealed class FontResolverTests
{
    [Fact]
    public void InstalledFamilyIsUsedAsIs()
    {
        Assert.True(SystemFonts.TryGet("DejaVu Sans", out _), "DejaVu Sans must be installed for this test.");

        Assert.Equal("DejaVu Sans", FontResolver.Resolve("DejaVu Sans").Name);
        Assert.Equal(12f, FontResolver.Create("DejaVu Sans", 12f).Size);
    }

    [Fact]
    public void MissingFamilyFallsBackAlongTheChain()
    {
        FontFamily family = FontResolver.Resolve("No Such Family 4711");

        string[] chain = FontResolver.Fallbacks;
        string? firstInstalled = chain.FirstOrDefault(name => SystemFonts.TryGet(name, out _));
        if (firstInstalled != null)
        {
            Assert.Equal(firstInstalled, family.Name);
        }
        else
        {
            Assert.Equal(SystemFonts.Families.First().Name, family.Name);
        }
    }

    [Fact]
    public void NullOrBlankFamilyUsesTheChain()
    {
        Assert.Equal(FontResolver.Resolve("No Such Family 4711").Name, FontResolver.Resolve(null).Name);
        Assert.Equal(FontResolver.Resolve("No Such Family 4711").Name, FontResolver.Resolve("  ").Name);
    }
}
