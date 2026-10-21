# AGENTS.md

## Project

ACadSharp.Image renders DXF/DWG documents to raster images and SVG using ACadSharp and Six Labors ImageSharp. The solution contains the library, CLI, and xUnit tests.

## Build and test

```bash
dotnet restore ACadSharp.Image.sln
dotnet build ACadSharp.Image.sln -c Release --no-restore -warnaserror
dotnet test ACadSharp.Image.sln -c Release --no-build
```

CI runs the same commands on Ubuntu and installs `fonts-dejavu-core` for rendering parity. Keep builds warning-free. Golden raster/SVG baselines live in `ACadSharp.Image.Tests/Baselines`; update them only for intentional rendering changes.

## Versions and packages

Package versions are centralized in `Directory.Packages.props`. The library targets `net6.0`, `net8.0`, and `net10.0`. Six Labors 4.x requires .NET 8+, so `net6.0` uses ImageSharp 3.2.0 / Fonts 2.2.0 / Drawing 2.2.0; newer TFMs use ImageSharp 4.1.3 / Fonts 3.2.0 / Drawing 3.1.3. Keep the conditional package groups. The net6.0 audit suppressions in `ACadSharp.Image.csproj` are intentional until net6.0 is dropped or patched 3.x packages exist.

## Versioning and release

There is no version property in the repository. The Git tag is the version source of truth. Merge the release PR and wait for CI, then create the release from `main`:

```bash
gh release create vX.Y.Z --target main --title vX.Y.Z --notes-file <notes.md>
```

Publishing the release triggers `.github/workflows/release.yml`, which publishes both NuGet packages and native AOT CLI archives for `linux-x64`, `win-x64`, and `osx-arm64`. Release notes use `## What's Changed`, PR-linked bullets, and a `**Full Changelog**` compare link.

## License

The project is MIT licensed. The Six Labors community license is `sixlabors.lic` at the repository root and is wired through `Directory.Build.props` via `SixLaborsLicenseFile`. Keep it there. Six Labors' build-time check reads this file; Release builds fail without a valid license, while Debug builds continue. The license file may be committed publicly. Do not remove or replace it without verifying the new key.

## Pull requests

Branch from `main`, open a PR, and merge with a merge commit (`gh pr merge <number> --merge`). Do not push directly to `main`. CI is path-filtered, so docs-only PRs may have no checks.
