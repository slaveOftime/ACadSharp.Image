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

        IReadOnlyList<string> chain = FontResolver.Fallbacks;
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
    public void TryResolveAgreesWithResolveWhileFontsAreInstalled()
    {
        // The false branch needs a machine without a single installed family, which SystemFonts cannot be made to
        // report here, so only the resolving side is covered.
        Assert.True(FontResolver.TryResolve("DejaVu Sans", out FontFamily configured));
        Assert.Equal("DejaVu Sans", configured.Name);

        Assert.True(FontResolver.TryResolve("No Such Family 4711", out FontFamily fallback));
        Assert.Equal(FontResolver.Resolve("No Such Family 4711").Name, fallback.Name);
    }

    [Fact]
    public void NullOrBlankFamilyUsesTheChain()
    {
        Assert.Equal(FontResolver.Resolve("No Such Family 4711").Name, FontResolver.Resolve(null).Name);
        Assert.Equal(FontResolver.Resolve("No Such Family 4711").Name, FontResolver.Resolve("  ").Name);
    }
}
