# Text Fidelity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make SVG text match the raster (and the CAD intent) on real drawings: decode `\U+XXXX` escapes, resolve a sensible fallback font, size SVG text like the raster, anchor multi-line MTEXT by its block, and wrap MTEXT at its rectangle width.

**Architecture:** Three small tasks. Text normalisation gains unicode-escape decoding (both backends). A shared `FontResolver` gives the raster a deterministic fallback chain and lets the SVG surface measure text with the same font family. `SvgDrawingSurface.DrawText` converts the CAD height to an em size with the factor the raster already applies implicitly, offsets multi-line blocks for the Central and Alphabetic baselines, and wraps at `WrappingWidth` with measured advances. Raster output is unchanged except the fallback font on machines without the configured family.

**Tech Stack:** .NET 10, ACadSharp 3.7.1, SixLabors.ImageSharp 3.1.12 / Drawing 2.1.7 / Fonts 2.1.3 (`TextMeasurer`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-02-layers-and-svg-design.md` (binding; section 5.3 "Text" bullet amended by Task 3).

## Global Constraints

- ACadSharp `3.7.1`; SixLabors packages as pinned; no new NuGet dependencies; target frameworks unchanged.
- Coding conventions: `this.` prefix on instance members, explicit types except LINQ lambdas, XML docs on public and internal members, `sealed` classes, file-scoped namespaces, four-space indent, UTF-8 without BOM, LF line endings.
- PNG baselines must stay byte-identical in every task (the parity tests pin `DejaVu Sans`, which is installed, so the fallback chain never engages there). SVG goldens: Tasks 1 and 2 must not change any; Task 3 regenerates exactly the goldens that contain `<text` (`6-57-1119.model.01.svg`, `HSK80AHCP16190M_BMG.model.01.svg`, `features.model.01.svg`, `viewport-sheet.paper.01.svg`) with the scoped commands given there, and nothing else. **Amended 2026-09-03 (final review):** Commit B of the final fix wave regenerated `HSK80AHCP16190M_BMG.model.01.png` and `features.model.01.png` for the 5/3 line spacing, with the cause in its body.
- `dotnet build ACadSharp.Image.sln -warnaserror` warning-free; full suite green before each commit.
- No reference to any drawing outside `Samples/` in code, tests, comments or commit messages.
- Never use bare `git stash` / `git stash pop`. Commit messages end with the repository's two trailer lines (see any commit on this branch).

## File Structure

- Modify `ACadSharp.Image/Rendering/TextRenderer.cs` (`NormalizeText`, MTEXT plain text via decoded value).
- Create `ACadSharp.Image/Rendering/FontResolver.cs`; modify `ACadSharp.Image/Rendering/RasterDrawingSurface.cs` (`CreateFont`).
- Create `ACadSharp.Image/Rendering/Svg/SvgTextLayout.cs` (em conversion, block offset, wrapping); modify `ACadSharp.Image/Rendering/Svg/SvgDrawingSurface.cs` (`DrawText`).
- Tests: `TextRendererTests.cs`, new `FontResolverTests.cs`, `SvgDrawingSurfaceTests.cs`, `SvgExportTests.cs`.
- Docs: spec section 5.3, `README.md`.

---

### Task 1: Decode `\U+XXXX` escapes and `%%` codes in TEXT and MTEXT

**Files:**
- Modify: `ACadSharp.Image/Rendering/TextRenderer.cs`
- Modify: `ACadSharp.Image.Tests/TextRendererTests.cs`

**Interfaces:**
- Produces: `internal static string TextRenderer.NormalizeText(string? value)` (was private) and `internal static string TextRenderer.PlainTextOf(MText mtext)`.

Background: DXF text may contain `\U+00B4` (a Unicode code point, here the acute accent). ACadSharp 3.7.1's `MText.PlainText` strips the backslash and leaves `U+00B4` in the output; `TextEntity.Value` keeps the escape verbatim. Both backends currently print the literal. `%%U` and `%%O` toggle underline/overline and must disappear; `%%%` is a literal percent sign.

- [ ] **Step 1: Write the failing tests**

Append to `ACadSharp.Image.Tests/TextRendererTests.cs`:

```csharp
    [Fact]
    public void UnicodeEscapesAndPercentCodesAreDecoded()
    {
        Assert.Equal("Ø 50 ´", TextRenderer.NormalizeText("\\U+00D8 50 \\u+00b4"));
        Assert.Equal("Ø ° ± % under", TextRenderer.NormalizeText("%%c %%d %%p %%% %%uunder%%u"));
        Assert.Equal("A\nB", TextRenderer.NormalizeText("A\\PB"));
        Assert.Equal("U+12", TextRenderer.NormalizeText("U+12"));            // not an escape without the backslash
        Assert.Equal("\\U+12G4", TextRenderer.NormalizeText("\\U+12G4"));    // not four hex digits: left alone
    }

    [Fact]
    public void MTextEscapesAreDecodedBeforeFormattingIsStripped()
    {
        MText mtext = new() { Value = "\\C10;\\fArial|b0|i0|;\\H1.5;A\\P\\U+00B4" };

        Assert.Equal("A\n´", TextRenderer.PlainTextOf(mtext));

        (RecordingDrawingSurface surface, ImageRenderContext context, EntityRenderDispatcher dispatcher) = Setup();
        dispatcher.Draw(context, new MText { Value = "\\U+00D8\\P\\U+2205", InsertPoint = new XYZ(0, 0, 0), Height = 2 });
        Assert.Equal("Ø\n∅", Assert.Single(surface.Texts).Text);
    }
```

- [ ] **Step 2: Run and confirm they fail**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~TextRendererTests"`
Expected: the two new tests fail (compile error for the private/missing methods, then wrong strings once made internal).

- [ ] **Step 3: Implement**

In `TextRenderer.cs`:

```csharp
    private static readonly Regex UnicodeEscape = new(@"\\[Uu]\+([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    /// <summary>
    /// The MTEXT's text with formatting stripped. Unicode escapes are decoded before ACadSharp strips the formatting,
    /// because its <c>PlainText</c> drops the backslash of <c>\U+XXXX</c> and would leave the literal code behind.
    /// </summary>
    internal static string PlainTextOf(MText mtext)
    {
        string value = mtext.Value ?? string.Empty;
        string decoded = UnicodeEscape.Replace(value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        return ReferenceEquals(decoded, value) || decoded == value ? mtext.PlainText : new MText { Value = decoded }.PlainText;
    }

    /// <summary>
    /// Applies the DXF text codes: <c>\U+XXXX</c> code points, <c>%%C</c> diameter, <c>%%D</c> degree, <c>%%P</c>
    /// plus-minus, <c>%%%</c> percent, the <c>%%U</c>/<c>%%O</c> underline and overline toggles (dropped), and
    /// <c>\P</c> paragraph breaks.
    /// </summary>
    internal static string NormalizeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string text = UnicodeEscape.Replace(value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        return text
            .Replace("%%%", "\u0001", StringComparison.Ordinal)
            .Replace("%%C", "Ø", StringComparison.OrdinalIgnoreCase)
            .Replace("%%D", "°", StringComparison.OrdinalIgnoreCase)
            .Replace("%%P", "±", StringComparison.OrdinalIgnoreCase)
            .Replace("%%U", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("%%O", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\u0001", "%", StringComparison.Ordinal)
            .Replace("\\P", "\n", StringComparison.OrdinalIgnoreCase);
    }
```

Add `using System.Text.RegularExpressions;`. In the MTEXT `Draw` overload replace `NormalizeText(mtext.PlainText)` with `NormalizeText(PlainTextOf(mtext))`. (The `\u0001` placeholder never survives: it is replaced back before returning; `SvgXmlText.Clean` would drop it otherwise.)

- [ ] **Step 4: Run tests, full suite, commit**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~TextRendererTests"` → PASS.
Run: `dotnet build ACadSharp.Image.sln -warnaserror --nologo -v q && dotnet test ACadSharp.Image.sln --nologo -v q` → all pass; `git status --short ACadSharp.Image.Tests/Baselines` empty (if a golden changes because a sample contains such an escape, stop and report which; do not regenerate in this task).

```bash
git add ACadSharp.Image/Rendering/TextRenderer.cs ACadSharp.Image.Tests/TextRendererTests.cs
git commit -m "Decode Unicode escapes and percent codes in text"
```

---

### Task 2: Deterministic fallback font

**Files:**
- Create: `ACadSharp.Image/Rendering/FontResolver.cs`
- Modify: `ACadSharp.Image/Rendering/RasterDrawingSurface.cs` (`CreateFont`)
- Create: `ACadSharp.Image.Tests/FontResolverTests.cs`

**Interfaces:**
- Produces: `internal static class FontResolver { static FontFamily Resolve(string? familyName); static Font Create(string? familyName, float size); static readonly string[] Fallbacks; }`.

Background: when the configured family (default `Arial`) is not installed, the raster takes `SystemFonts.Families.First()`, whatever sorts first on the machine (a decorative face on this one). The SVG font stack is `Arial, Helvetica, sans-serif`, which fontconfig maps to Liberation Sans; the raster should follow the same intent.

- [ ] **Step 1: Write the failing test**

Create `ACadSharp.Image.Tests/FontResolverTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run and confirm it fails to compile**

Run: `dotnet build ACadSharp.Image.Tests --nologo -v q` → CS0103/CS0246 for `FontResolver`.

- [ ] **Step 3: Implement**

Create `ACadSharp.Image/Rendering/FontResolver.cs`:

```csharp
using SixLabors.Fonts;

namespace ACadSharp.Image.Rendering;

/// <summary>
/// Resolves the configured font family to an installed one. When the configured family is missing, the fallback chain
/// mirrors the SVG font stack (<c>Arial, Helvetica, sans-serif</c>): metric-compatible Liberation Sans first, then the
/// common Linux and Windows sans faces, and only then the first installed family.
/// </summary>
internal static class FontResolver
{
    /// <summary>Families tried, in order, when the configured one is not installed.</summary>
    public static readonly string[] Fallbacks = ["Liberation Sans", "DejaVu Sans", "Arial", "Helvetica", "Noto Sans", "Segoe UI"];

    /// <summary>
    /// Finds the installed family for a configured name.
    /// </summary>
    /// <param name="familyName">The configured family, or null/blank for the fallback chain.</param>
    /// <returns>The configured family when installed, otherwise the first installed fallback, otherwise the first installed family.</returns>
    public static FontFamily Resolve(string? familyName)
    {
        if (!string.IsNullOrWhiteSpace(familyName) && SystemFonts.TryGet(familyName, out FontFamily configured))
        {
            return configured;
        }

        foreach (string fallback in Fallbacks)
        {
            if (SystemFonts.TryGet(fallback, out FontFamily family))
            {
                return family;
            }
        }

        return SystemFonts.Families.First();
    }

    /// <summary>
    /// Creates a font of the given size from the resolved family.
    /// </summary>
    /// <param name="familyName">The configured family.</param>
    /// <param name="size">Font size in points.</param>
    /// <returns>The font.</returns>
    public static Font Create(string? familyName, float size)
    {
        return Resolve(familyName).CreateFont(Math.Max(1f, size));
    }
}
```

In `RasterDrawingSurface.CreateFont` replace the body with `return FontResolver.Create(this._configuration.FontFamilyName, (float)height);` and keep the method (or inline it at its call sites and delete it; either is fine, say which).

- [ ] **Step 4: Run tests, full suite, commit**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~FontResolverTests"` → PASS.
Run: `dotnet build ACadSharp.Image.sln -warnaserror --nologo -v q && dotnet test ACadSharp.Image.sln --nologo -v q` → all pass; no baseline change (the parity tests pin DejaVu Sans; the feature and viewport tests too).

```bash
git add ACadSharp.Image/Rendering/FontResolver.cs ACadSharp.Image/Rendering/RasterDrawingSurface.cs ACadSharp.Image.Tests/FontResolverTests.cs
git commit -m "Resolve missing font families along a deterministic fallback chain"
```

---

### Task 3: SVG text sized, anchored and wrapped like the raster

**Files:**
- Create: `ACadSharp.Image/Rendering/Svg/SvgTextLayout.cs`
- Modify: `ACadSharp.Image/Rendering/Svg/SvgDrawingSurface.cs` (`DrawText`)
- Modify: `ACadSharp.Image.Tests/SvgDrawingSurfaceTests.cs`, `ACadSharp.Image.Tests/SvgExportTests.cs` (one assertion each, see below)
- Modify: `docs/superpowers/specs/2026-09-02-layers-and-svg-design.md` (5.3 Text bullet), `README.md`
- Regenerate: the four SVG goldens that contain text (see Global Constraints)

**Interfaces:**
- Consumes: `FontResolver.Create(string?, float)` (Task 2), `SixLabors.Fonts.TextMeasurer.MeasureAdvance(string, TextOptions)`.
- Produces: `internal static class SvgTextLayout { const double CapHeightToEm = 4d / 3d; static double EmSize(double capHeight); static double LineHeight(double capHeight, double lineSpacingFactor); static double BlockOffset(int lineCount, double lineHeight, SurfaceTextBaseline baseline); static IReadOnlyList<string> Wrap(string text, double wrappingWidth, double emSize, string? fontFamily); }`.

Background (measured): `SurfaceText.Height` is the CAD text height, which is the cap height. The raster creates a font of that size in points and renders at `ImageConfiguration.Dpi` (96 by default), so its em size is `Height × 96/72`, giving a cap height close to the CAD height. The SVG wrote `font-size = Height`, so its glyphs were 25% smaller than the raster's. Multi-line MTEXT: the raster (like AutoCAD) positions the whole block by the attachment point; the SVG put the first line's baseline there. Wrapping: the raster wraps at `WrappingWidth`; SVG has no automatic wrapping, so wrapped labels came out on one line.

- [ ] **Step 1: Write the failing tests**

Append to `ACadSharp.Image.Tests/SvgDrawingSurfaceTests.cs`:

```csharp
    [Fact]
    public void FontSizeIsTheEmForTheCadCapHeight()
    {
        using SvgDrawingSurface surface = CreateSurface();
        surface.BeginEntity(Entity("Anno", "TEXT"), Layer("Anno"));
        surface.DrawText(new ImageStyle(Color.Black, 1f), new SurfaceText("H", new SurfacePoint(0, 0), 3, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, -1, 1, 0));
        surface.EndEntity();

        XElement text = Assert.Single(surface.ToDocument().Descendants(Ns + "text"));
        Assert.Equal("4", (string?)text.Attribute("font-size"));   // 3 × 4/3
    }

    [Fact]
    public void MultiLineBlocksAreAnchoredByTheirBaseline()
    {
        using SvgDrawingSurface surface = CreateSurface();
        ImageStyle style = new(Color.Black, 1f);
        surface.BeginEntity(Entity("Anno", "MTEXT"), Layer("Anno"));
        surface.DrawText(style, new SurfaceText("a\nb\nc", new SurfacePoint(10, 50), 3, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Central, -1, 1, 0));
        surface.DrawText(style, new SurfaceText("a\nb", new SurfacePoint(10, 50), 3, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Alphabetic, -1, 1, 0));
        surface.DrawText(style, new SurfaceText("a\nb", new SurfacePoint(10, 50), 3, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Hanging, -1, 1, 0));
        surface.EndEntity();

        List<XElement> texts = surface.ToDocument().Descendants(Ns + "text").ToList();
        // Line height is 5/3 of the cap height: 5. Central: three lines, first line one line height above the origin.
        Assert.Equal("45", (string?)texts[0].Attribute("y"));
        // Alphabetic (bottom): two lines, first line one line height above.
        Assert.Equal("45", (string?)texts[1].Attribute("y"));
        // Hanging (top): first line at the origin.
        Assert.Equal("50", (string?)texts[2].Attribute("y"));
        Assert.Equal(["a", "b", "c"], texts[0].Elements(Ns + "tspan").Select(t => t.Value).ToArray());
        Assert.Equal("5", (string?)texts[0].Elements(Ns + "tspan").ElementAt(1).Attribute("dy"));
    }

    [Fact]
    public void TextIsWrappedAtTheWrappingWidth()
    {
        using SvgDrawingSurface surface = CreateSurface(c => c.FontFamilyName = "DejaVu Sans");
        surface.BeginEntity(Entity("Anno", "MTEXT"), Layer("Anno"));
        // Width 14 at cap height 3 (em 4) fits roughly five to six characters of DejaVu Sans per line.
        surface.DrawText(new ImageStyle(Color.Black, 1f), new SurfaceText("alpha beta gamma delta", new SurfacePoint(0, 0), 3, 0, SurfaceTextAnchor.Start, SurfaceTextBaseline.Hanging, 14, 1, 0));
        surface.EndEntity();

        XElement text = Assert.Single(surface.ToDocument().Descendants(Ns + "text"));
        string[] lines = text.Elements(Ns + "tspan").Select(t => t.Value).ToArray();
        Assert.Equal(["alpha", "beta", "gamma", "delta"], lines);
    }

    [Fact]
    public void WrapKeepsExplicitBreaksAndLongWords()
    {
        IReadOnlyList<string> lines = SvgTextLayout.Wrap("one two\nthree fourfivesixseven", 8, 4, "DejaVu Sans");

        Assert.Equal("one", lines[0]);
        Assert.Equal("two", lines[1]);
        Assert.Equal("three", lines[2]);
        Assert.Equal("fourfivesixseven", lines[3]);   // a single word wider than the width stays on its own line
        Assert.Equal(["x"], SvgTextLayout.Wrap("x", -1, 4, "DejaVu Sans"));
    }
```

In `ACadSharp.Image.Tests/SvgExportTests.cs`, find the assertion on `font-size` if one exists (search for `font-size`) and update it to the new value (height × 4/3); if none exists, add nothing.

- [ ] **Step 2: Run and confirm they fail**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~SvgDrawingSurfaceTests"` → the new tests fail (compile error for `SvgTextLayout`, then font-size "3", y "50", one tspan).

- [ ] **Step 3: Implement the layout helper**

Create `ACadSharp.Image/Rendering/Svg/SvgTextLayout.cs`:

```csharp
using SixLabors.Fonts;

namespace ACadSharp.Image.Rendering.Svg;

/// <summary>
/// Text metrics for the SVG backend, chosen to match the raster backend and the CAD intent.
/// </summary>
/// <remarks>
/// <c>SurfaceText.Height</c> is the CAD text height, which is the cap height. The raster backend creates a font of that
/// size in points and renders at 96 dpi, so its em size is 4/3 of the cap height, and common sans faces have a cap
/// height of about 0.72 em; the SVG uses the same factor so both outputs agree. Line spacing follows AutoCAD: 5/3 of
/// the text height per line at spacing factor 1.
/// </remarks>
internal static class SvgTextLayout
{
    /// <summary>Em size per unit of cap height.</summary>
    public const double CapHeightToEm = 4d / 3d;

    /// <summary>Font size (em) for a CAD text height.</summary>
    public static double EmSize(double capHeight) => capHeight * CapHeightToEm;

    /// <summary>Distance between consecutive baselines.</summary>
    public static double LineHeight(double capHeight, double lineSpacingFactor) =>
        capHeight * (lineSpacingFactor <= 0 ? 1d : lineSpacingFactor) * 5d / 3d;

    /// <summary>
    /// Offset of the first line's baseline from the anchor, in surface units (negative is up), so the whole block hangs
    /// from, is centred on, or stands on the anchor the way the CAD attachment point says.
    /// </summary>
    public static double BlockOffset(int lineCount, double lineHeight, SurfaceTextBaseline baseline) => baseline switch
    {
        SurfaceTextBaseline.Central => -(lineCount - 1) * lineHeight / 2d,
        SurfaceTextBaseline.Alphabetic => -(lineCount - 1) * lineHeight,
        _ => 0d,
    };

    /// <summary>
    /// Splits text into lines: explicit line breaks always break; when <paramref name="wrappingWidth"/> is positive,
    /// words are added greedily while the measured advance fits. A single word wider than the width stays alone.
    /// </summary>
    /// <param name="text">Text with <c>\n</c> for explicit breaks.</param>
    /// <param name="wrappingWidth">Available width in surface units, or a non-positive value for no wrapping.</param>
    /// <param name="emSize">Font size in surface units.</param>
    /// <param name="fontFamily">Configured family, resolved through <see cref="FontResolver"/> for measuring.</param>
    /// <returns>The lines, never empty.</returns>
    public static IReadOnlyList<string> Wrap(string text, double wrappingWidth, double emSize, string? fontFamily)
    {
        string[] paragraphs = text.Replace("\r\n", "\n").Split('\n');
        if (wrappingWidth <= 0d || emSize <= 0d)
        {
            return paragraphs;
        }

        // Points at 72 dpi are surface units, so the measured advance is directly comparable with the width.
        TextOptions options = new(FontResolver.Create(fontFamily, (float)emSize)) { Dpi = 72f };
        List<string> lines = new();
        foreach (string paragraph in paragraphs)
        {
            string[] words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            string current = words[0];
            for (int i = 1; i < words.Length; i++)
            {
                string candidate = current + " " + words[i];
                if (TextMeasurer.MeasureAdvance(candidate, options).Width <= wrappingWidth)
                {
                    current = candidate;
                }
                else
                {
                    lines.Add(current);
                    current = words[i];
                }
            }

            lines.Add(current);
        }

        return lines;
    }
}
```

`FontResolver` lives in `ACadSharp.Image.Rendering`; add `using ACadSharp.Image.Rendering;` if the namespace differs.

- [ ] **Step 4: Use it in `SvgDrawingSurface.DrawText`**

Replace the element construction and the lines block:

```csharp
        double emSize = SvgTextLayout.EmSize(text.Height);
        IReadOnlyList<string> lines = SvgTextLayout.Wrap(SvgXmlText.Clean(text.Text), text.WrappingWidth, emSize, this._configuration.FontFamilyName);
        double lineHeight = SvgTextLayout.LineHeight(text.Height, text.LineSpacingFactor);
        double firstLineY = text.Origin.Y + SvgTextLayout.BlockOffset(lines.Count, lineHeight, text.Baseline);

        XElement element = new(Ns + "text",
            new XAttribute("x", this.N(text.Origin.X)),
            new XAttribute("y", this.N(firstLineY)),
            new XAttribute("font-size", this.N(emSize)));
```

Keep the anchor, baseline, rotation (still about `text.Origin`, so a shifted block rotates around its anchor) and `textLength` attributes as they are. Then:

```csharp
        if (lines.Count == 1)
        {
            element.Add(lines[0]);
        }
        else
        {
            for (int i = 0; i < lines.Count; i++)
            {
                XElement span = new(Ns + "tspan", new XAttribute("x", this.N(text.Origin.X)), lines[i]);
                if (i > 0)
                {
                    span.Add(new XAttribute("dy", this.N(lineHeight)));
                }

                element.Add(span);
            }
        }
```

- [ ] **Step 5: Run the surface tests, then the suite; regenerate the text goldens**

Run: `dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~SvgDrawingSurfaceTests"` → PASS (fix any pre-existing assertion in that file that hard-coded the old `font-size` or `y`, and say so).
Run: `dotnet test ACadSharp.Image.sln --nologo -v q` → expected failures: exactly the SVG golden comparisons for `6-57-1119.dxf`, `HSK80AHCP16190M_BMG.dwg` (model), `FeatureGoldenTests.FeatureSvgMatchesGoldenAndContainsEveryPrimitive`, `ViewportParityTests.SheetSvgMatchesGoldenAndClipsTheViewport`, plus possibly `SvgExportTests` assertions on `font-size`. No PNG may fail. Anything else: stop and report.

Regenerate only the text goldens:
```bash
ACADSHARP_IMAGE_UPDATE_BASELINES=1 dotnet test ACadSharp.Image.Tests --nologo --filter "FullyQualifiedName~SampleParityTests.SampleSvgsMatchGoldens|FullyQualifiedName~FeatureGoldenTests.FeatureSvgMatchesGolden|FullyQualifiedName~ViewportParityTests.SheetSvgMatchesGolden"
git status --short ACadSharp.Image.Tests/Baselines
```
The status must list only the four text goldens (the Subaru golden has no text and must come out identical; if it changes, stop and report). Inspect `git diff --stat` of the goldens and confirm the changes are `font-size` values, `y` values on multi-line texts, and added `tspan`s, nothing else. Run the suite again without the variable → all pass.

- [ ] **Step 6: Document**

Spec section 5.3, Text bullet: append "**Amended 2026-09-03 (text fidelity):** `font-size` is 4/3 of the CAD text height (the cap height), matching the raster backend, which renders points at 96 dpi; multi-line blocks are offset so the whole block hangs from, is centred on, or stands on the anchor (Hanging/Central/Alphabetic); MTEXT with a rectangle width is wrapped greedily with advances measured by SixLabors.Fonts through `FontResolver`, so lines break where the raster breaks them. `\U+XXXX` escapes and `%%` codes are decoded for both backends."
README, after the SVG fidelity sentence: "SVG text is sized and wrapped to match the PNG output; glyph shapes still depend on the viewer's fonts."

- [ ] **Step 7: Build, full suite, commit**

Run: `dotnet build ACadSharp.Image.sln -warnaserror --nologo -v q && dotnet test ACadSharp.Image.sln --nologo -v q` → 0 warnings; all pass.

```bash
git add ACadSharp.Image/Rendering/Svg/SvgTextLayout.cs ACadSharp.Image/Rendering/Svg/SvgDrawingSurface.cs ACadSharp.Image.Tests/SvgDrawingSurfaceTests.cs ACadSharp.Image.Tests/SvgExportTests.cs ACadSharp.Image.Tests/Baselines/*.svg docs/superpowers/specs/2026-09-02-layers-and-svg-design.md README.md
git commit -m "Size, anchor and wrap SVG text like the raster backend"
```
The commit body lists the four regenerated goldens and the cause.
