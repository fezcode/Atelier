# Atelier

![Atelier](Assets/banner.png)

`Atelier` is a high-performance, modern image and SVG viewer built with **Avalonia UI** and **C#**. It provides a buttery-smooth viewing experience with native support for modern formats and advanced vector rendering.

## Features
- **Avalonia UI Core**: High-performance, cross-platform XAML-based UI.
- **Hardware Acceleration**: Uses SkiaSharp for GPU-accelerated rendering.
- **SVG Native Support**: Infinite zoom for vector graphics with high fidelity.
- **HEIC/HEIF Support**: View modern smartphone photos directly via Magick.NET.
- **Fluid Navigation**: Seamless directory browsing and centered zooming.
- **Drag & Drop**: Open any supported file or folder instantly.
- **Minimalist Design**: Clean, dark-themed interface focused on your content.

## Controls
- **Mouse Wheel + Ctrl**: Zoom in/out.
- **Mouse Wheel + Shift**: Pan horizontally.
- **Left/Right Arrow**: Previous/Next image in the folder.
- **`F`**: Toggle Fullscreen.
- **Ctrl+O / Ctrl+S**: Open / Save As.
- **Drag & Drop**: Drop any file to view.
- **File → Open File Location**: Reveal the current image in File Explorer.
- **File → Set as Wallpaper**: Fill, Fit, Stretch, Center, Tile or Span. SVG, HEIC, AVIF, WebP and ICO are converted automatically, since Windows only accepts JPG, PNG and BMP.
- **Metadata panel**: Close it with the **✕** in its header, or toggle it from **View → Image Metadata**.

## Releases
You can download the latest pre-built binaries from the [Releases](https://github.com/fezcode/Atelier/releases) page.

## Build Requirements
- **.NET 8.0 SDK**

## Building & Running
1. **Clone the repository**:
   ```bash
   git clone https://github.com/fezcode/Atelier.git
   cd Atelier
   ```

2. **Run the application**:
   ```bash
   dotnet run
   ```

3. **Run the tests** (headless Avalonia — no window is shown):
   ```bash
   dotnet test Tests/Atelier.ZoomTests
   ```

## Packaging
The Windows installer is built with [Forge](https://github.com/fezcode/Forge) from `forge.toml`:

```powershell
.\build-installer.ps1     # publish + bundle -> dist\Atelier-Setup-<version>.exe
```

The version lives in `<Version>` in `Atelier.csproj` — the About dialog reads it back off
the assembly, so keep `[app] version` in `forge.toml` in step with it.
