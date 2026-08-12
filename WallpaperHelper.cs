using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ImageMagick;
using Microsoft.Win32;

namespace Atelier;

/// <summary>
/// Sets the Windows desktop background.
///
/// Split deliberately: <see cref="PrepareImage"/> holds every decision worth
/// testing and touches nothing but the filesystem, while <see cref="Apply"/> --
/// the part that actually changes the user's desktop -- stays as thin as it can be.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WallpaperHelper
{
    public enum WallpaperFit
    {
        Fill,
        Fit,
        Stretch,
        Center,
        Tile,
        /// <summary>One image stretched across every monitor. No-op on a single display.</summary>
        Span,
    }

    /// <summary>
    /// The only formats Windows will accept as a desktop background. Everything
    /// else Atelier can open -- SVG, HEIC, AVIF, WebP, ICO, GIF -- is converted first.
    /// </summary>
    private static readonly string[] NativeFormats = { ".bmp", ".jpg", ".jpeg", ".png" };

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Where converted wallpapers are kept. Not %TEMP%: Windows re-reads this path
    /// when the desktop refreshes, and a cleaner deleting it would break the background.
    /// </summary>
    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Atelier", "Wallpaper");

    /// <summary>
    /// Maps a fit onto the two HKCU\Control Panel\Desktop values the shell reads.
    /// </summary>
    public static (string Style, string Tile) StyleFor(WallpaperFit fit) => fit switch
    {
        WallpaperFit.Fill => ("10", "0"),
        WallpaperFit.Fit => ("6", "0"),
        WallpaperFit.Stretch => ("2", "0"),
        WallpaperFit.Center => ("0", "0"),
        WallpaperFit.Tile => ("0", "1"),
        WallpaperFit.Span => ("22", "0"),
        _ => ("10", "0"),
    };

    /// <summary>
    /// Returns a path Windows can use as a background, converting the image first
    /// if its format is not one Windows accepts.
    /// </summary>
    /// <param name="screenWidth">Target width for rasterising vectors. 0 to use the SVG's own size.</param>
    /// <param name="screenHeight">Target height for rasterising vectors.</param>
    /// <param name="outputDirectory">
    /// Where converted files are written. Defaults to <see cref="CacheDirectory"/>; tests
    /// pass a temp folder so pruning cannot delete a wallpaper the user actually has set.
    /// </param>
    public static string PrepareImage(string sourcePath, int screenWidth = 0, int screenHeight = 0,
        string? outputDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("No image is open.", nameof(sourcePath));

        var full = Path.GetFullPath(sourcePath);
        if (!File.Exists(full))
            throw new FileNotFoundException("The image no longer exists.", full);

        // Invariant casing: under a Turkish locale ToLower() maps 'I' to dotless 'i',
        // so ".PNG" would miss this list and every uppercase file would be converted.
        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (NativeFormats.Contains(ext))
        {
            // Handed to Windows as-is, so Settings > Personalization shows the real file.
            return full;
        }

        var outDir = outputDirectory ?? CacheDirectory;
        Directory.CreateDirectory(outDir);

        // A fresh name every time. Windows caches the desktop background by path, so
        // writing over one filename can leave the desktop showing the previous image.
        var target = Path.Combine(outDir, $"wallpaper-{Guid.NewGuid():N}.png");

        if (ext == ".svg")
        {
            // Rasterise at screen size. Read at its nominal size instead and the
            // result is soft once Windows scales it up to fill the desktop.
            var settings = new MagickReadSettings
            {
                Format = MagickFormat.Svg,
                BackgroundColor = MagickColors.Transparent,
            };
            if (screenWidth > 0 && screenHeight > 0)
            {
                settings.Width = (uint)screenWidth;
                settings.Height = (uint)screenHeight;
            }

            using var svg = new MagickImage(full, settings);
            svg.Write(target, MagickFormat.Png);
        }
        else
        {
            // ICO and GIF hold several frames. Take the largest rather than the first --
            // an icon's first frame is often 16x16, which makes a miserable wallpaper.
            using var frames = new MagickImageCollection(full);
            var largest = frames.OrderByDescending(f => (long)f.Width * f.Height).First();
            largest.Write(target, MagickFormat.Png);
        }

        Prune(outDir, target);
        return target;
    }

    /// <summary>
    /// Points the desktop at <paramref name="imagePath"/>. The file must already be
    /// in a format Windows accepts -- run it through <see cref="PrepareImage"/> first.
    /// </summary>
    public static void Apply(string imagePath, WallpaperFit fit)
    {
        if (!IsWindows)
            throw new PlatformNotSupportedException("Setting the wallpaper requires Windows.");

        var (style, tile) = StyleFor(fit);

        using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true))
        {
            if (key == null)
                throw new InvalidOperationException(@"Cannot open HKCU\Control Panel\Desktop.");

            // These must be in place BEFORE SPI_SETDESKWALLPAPER: the shell reads them
            // while applying the bitmap, so writing them afterwards changes nothing
            // until the next sign-in.
            key.SetValue("WallpaperStyle", style, RegistryValueKind.String);
            key.SetValue("TileWallpaper", tile, RegistryValueKind.String);
        }

        if (!SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, imagePath,
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Drops every previously converted wallpaper except the one just written.</summary>
    private static void Prune(string directory, string keep)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "wallpaper-*.png"))
        {
            if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Still mapped by the shell. The next call clears it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
