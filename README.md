# ACadSharp.Image

[![NuGet downloads](https://img.shields.io/nuget/dt/ACadSharp.Image?logo=nuget&label=downloads)](https://www.nuget.org/packages/ACadSharp.Image)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-6.0%20%7C%208.0%20%7C%2010.0-512bd4)](https://dotnet.microsoft.com/download)
[![CI](https://github.com/slaveoftime/ACadSharp.Image/actions/workflows/ci.yml/badge.svg)](https://github.com/slaveoftime/ACadSharp.Image/actions)

**High-performance DXF/DWG to image renderer for .NET**, built on [ACadSharp](https://github.com/DomCR/ACadSharp) and [ImageSharp](https://github.com/SixLabors/ImageSharp).

Transform CAD drawings into raster images for **previews**, **CI/CD pipelines**, **web applications**, **documentation**, and **automated workflows** — with zero AutoCAD dependency.

![Rendered sample](Samples/HSK80AHCP16190M_BMG.webp)

---

## ✨ Features

- 🎨 **Multi-format export** — PNG, BMP, JPEG, GIF, and WebP support
- 📐 **Full CAD support** — Render DXF and DWG files with ACadSharp
- 🖼️ **Customizable output** — Control width, height, padding, background color, and quality
- 📊 **Space support** — Model space, paper layouts, and viewports
- 🎭 **Layer filtering** — Hide specific layers with `--hide-layer` option
- ⚡ **CLI tool** — Cross-platform command-line interface for automation
- 🔧 **Library API** — Full .NET integration with intuitive fluent-style configuration
- 🚀 **Native AOT** - Publish as standalone native binaries with zero .NET runtime requirement
- 📚 **Fully documented** — Complete XML IntelliSense support

---

## 📦 Installation

### NuGet Package

```bash
dotnet add package ACadSharp.Image
```

### CLI as Global Tool

```bash
dotnet tool install --global ACadSharp.Image.Cli
```

**Update to latest version:**

```bash
dotnet tool update --global ACadSharp.Image.Cli
```

---

## 🚀 Quick Start

### Library Usage

Render a DWG file with custom settings:

```csharp
using ACadSharp.IO;
using ACadSharp.Image;
using SixLabors.ImageSharp;

// Load CAD document
var document = DwgReader.Read("part.dwg");

// Configure and export
var exporter = new ImageExporter("output.webp");
exporter.Configuration.Width = 2000;
exporter.Configuration.Height = 1400;
exporter.Configuration.SetPadding(24, 12);
exporter.Configuration.BackgroundColor = Color.Parse("#ffffff");
exporter.Configuration.OutputQuality = 90;

// Optional: hide specific layers
exporter.Configuration.HideLayer("DIMENSIONS");
exporter.Configuration.HideLayer("ANNOTATIONS");

exporter.AddModelSpace(document);
exporter.Save("./output-directory/filename.webp", ImageExportFormat.Webp);
```

**Multi-page export:**

```csharp
var exporter = new ImageExporter();
exporter.AddPaperLayouts(document);
exporter.Save("./output-directory/filename.png", ImageExportFormat.Png);
```

### CLI Usage

**Basic rendering:**

```bash
cad-to-image "drawing.dxf" --format webp --width 1400 --height 1400 --quality 85
```

**Custom background & dimensions:**

```bash
cad-to-image "part.dwg" --format png --width 1800 --height 1200 --background "#0c0c0c"
```

**Add padding around the drawing:**

```bash
cad-to-image "part.dwg" --format png --padding 24
cad-to-image "part.dwg" --format png --padding 24,12
cad-to-image "part.dwg" --format png --padding 24,12,40,20
```

**Hide multiple layers:**

```bash
cad-to-image "complex.dxf" --hide-layer "DIMENSIONS" --hide-layer "ANNOTATIONS" --hide-layer "BORDER"
```

**Export paper layouts:**

```bash
cad-to-image "multi-sheet.dwg" --paper-layouts --output ./sheets/
```

---

## 📖 CLI Reference

```
Usage:
  cad-to-image <input.dxf|input.dwg> [options]

Options:
  -o, --output <path>         Output file or directory path.
  -f, --format <format>       png, bmp, jpg, jpeg, gif, webp.
  -w, --width <pixels>        Output width in pixels. Default: 1600.
  -H, --height <pixels>       Output height in pixels. Default: 900.
  -p, --padding <value>       Padding in pixels: <all>, <x,y>, or <left,top,right,bottom>.
  -b, --background <color>    Background color name or hex value. Default: white.
  -q, --quality <1-100>       Output quality for lossy formats. Default: 90.
      --paper-layouts         Export paper layouts instead of model space.
      --hide-layer <name>     Hide entities on the specified layer. Can be used multiple times.
      --help, -h, -?          Show this help text.
```

---

## 🏗️ Architecture

```
ACadSharp.Image/
├── ImageExporter.cs          # Main public API
├── ImageConfiguration.cs     # Fluent configuration
├── ImagePage.cs              # Page representation
├── RenderedImagePage.cs      # Rendered output
└── Rendering/
    ├── ImagePageRenderer.cs      # Page-level rendering
    ├── EntityRenderDispatcher.cs # Entity routing & primitive drawing
    ├── SplineRenderer.cs         # Spline path generation and sampling
    ├── TextRenderer.cs           # Text and MText rendering
    ├── ImageRenderContext.cs     # Coordinate transforms
    └── ImageStyleResolver.cs     # Color & line weight resolution
```

The library follows a clean architecture pattern:
- **ImageExporter** - Public API for adding CAD content
- **ImagePage** - Represents individual renderable pages
- **Rendering pipeline** - Transforms CAD entities to pixel coordinates and draws them
- **Configuration** - Fluent, extensible settings for customization

---

## 💡 Advanced Usage

### Layer Filtering

Control visibility of specific layers programmatically:

```csharp
var exporter = new ImageExporter();

// Hide multiple layers (case-insensitive)
exporter.Configuration.HideLayer("0");
exporter.Configuration.HideLayer("DEFPOINTS");
exporter.Configuration.HideLayer("ANNO_TEXT");

exporter.AddModelSpace(document);
```

### Custom Line Weights

Override default line weight values:

```csharp
exporter.Configuration.SetLineWeight(LineWeightType.W25, 0.30);
exporter.Configuration.LineWeightScale = 1.5f; // Scale all weights
```

### Text & Font Configuration

Customize text rendering:

```csharp
exporter.Configuration.FontFamilyName = "Consolas";
exporter.Configuration.ArcPrecision = 512; // Higher = smoother arcs
```

---

## 🛠️ Development

### Prerequisites

- [.NET 6.0 SDK](https://dotnet.microsoft.com/download) or later
- Any IDE with C# support (VS 2022, VS Code, Rider)

### Build & Test

```bash
# Clone and build
git clone https://github.com/slaveoftime/ACadSharp.Image.git
cd ACadSharp.Image
dotnet restore
dotnet build
dotnet format --verify-no-changes

# Run tests
dotnet test
```

### Measure Render Performance

Use the repeatable sample-render benchmark script:

```powershell
powershell -ExecutionPolicy Bypass -File .\artifacts\measure-render.ps1
powershell -ExecutionPolicy Bypass -File .\artifacts\measure-render.ps1 -Iterations 10
```

### Run Examples

```bash
dotnet run --project ./ACadSharp.Image.Cli/ACadSharp.Image.Cli.csproj -- "./Samples/6-57-1119.dxf" --width 300 --height 200 --hide-layer OPTIONAL_DIMENSIONS

dotnet run --project ./ACadSharp.Image.Cli/ACadSharp.Image.Cli.csproj -- "./Samples/HSK80AHCP16190M_BMG.dwg" --format webp --width 1200 --height 760

dotnet run --project ./ACadSharp.Image.Cli/ACadSharp.Image.Cli.csproj -- "./Samples/Subaru Logo Vector Free Wrap.dxf" --format webp --width 1200 --height 700 --background "#a0a7ae"
```

### Build NuGet Package

```bash
dotnet pack ./ACadSharp.Image/ACadSharp.Image.csproj -c Release
dotnet pack ./ACadSharp.Image.Cli/ACadSharp.Image.Cli.csproj -c Release
dotnet tool install -g --add-source ./ACadSharp.Image.Cli/bin/Release ACadSharp.Image.Cli
```

### Publish Native Binary (AOT)

Zero-dependency standalone executables:

```bash
# Windows x64
dotnet publish ./ACadSharp.Image.Cli/ -c Release -r win-x64 --self-contained -p:PublishAot=true

# Linux x64
dotnet publish ./ACadSharp.Image.Cli/ -c Release -r linux-x64 --self-contained -p:PublishAot=true

# macOS ARM64
dotnet publish ./ACadSharp.Image.Cli/ -c Release -r osx-arm64 --self-contained -p:PublishAot=true
```

---

## 🔄 Migration Notes

Recent modernization work includes intentional API tightening:

- `ImageExporter.Pages` is now a read-only collection view.
- `ImagePage.Entities` and `ImagePage.Viewports` are now read-only collection views.
- Add content through `ImageExporter.Add(...)`, `ImagePage.Add(...)`, `ImagePage.AddEntity(...)`, and `ImagePage.AddViewport(...)`.
- `ImageConfiguration.HiddenLayers` is now read-only; use `HideLayer`, `HideLayers`, `ShowLayer`, and `ClearHiddenLayers`.
- `ImageConfiguration.LineWeightValues` is now read-only; use `SetLineWeight`, `RemoveLineWeight`, and `ClearLineWeights`.

These changes preserve the rendering behavior while making mutation points explicit and easier to maintain.

---

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

---

## 📄 License

This project is released under the [MIT License](LICENSE).

---

## 🌟 Support

If you find this project helpful, please consider giving it a ⭐️ on GitHub! It helps others discover the project.

**Questions or issues?** [Open an issue](https://github.com/slaveoftime/ACadSharp.Image/issues) or start a [Discussion](https://github.com/slaveoftime/ACadSharp.Image/discussions).
