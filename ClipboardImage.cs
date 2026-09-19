using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ImageMagick;

namespace Atelier;

/// <summary>
/// Copying the open picture out, and opening one that was copied in.
///
/// Copy deliberately puts three things on the clipboard at once, because "copy an
/// image" means different things to whatever receives it: Paint wants pixels, an
/// Explorer window or an upload box wants the file, and a text field wants the path.
/// Offering all three is what makes Ctrl+C behave the way people expect everywhere
/// they paste it.
///
/// Split like <see cref="WallpaperHelper"/>: the byte-level conversions and the scratch
/// folder live here and are covered by tests, while the Win32 clipboard calls stay thin
/// enough to read in one go.
/// </summary>
public static class ClipboardImage
{
    private const string PasteFilePrefix = "paste-";

    /// <summary>
    /// Where a pasted image is written so the rest of the app can treat it as an
    /// ordinary file. %TEMP% rather than the Fezcode app-data tree: these are scratch,
    /// and unlike a converted wallpaper nothing needs them to survive.
    /// </summary>
    public static string PasteFolder =>
        Path.Combine(Path.GetTempPath(), "Atelier", "Paste");

    // ---- BMP <-> DIB ------------------------------------------------------

    /// <summary>
    /// The 14-byte BITMAPFILEHEADER that a .bmp has and CF_DIB does not. The clipboard
    /// format starts straight at the BITMAPINFOHEADER.
    /// </summary>
    private const int BitmapFileHeaderSize = 14;

    /// <summary>Strips a BMP down to the CF_DIB the clipboard expects.</summary>
    public static byte[] BmpToDib(byte[] bmp)
    {
        if (bmp is null) throw new ArgumentNullException(nameof(bmp));
        if (bmp.Length <= BitmapFileHeaderSize)
            throw new ArgumentException("Too short to be a BMP.", nameof(bmp));

        var dib = new byte[bmp.Length - BitmapFileHeaderSize];
        Array.Copy(bmp, BitmapFileHeaderSize, dib, 0, dib.Length);
        return dib;
    }

    /// <summary>
    /// Puts a BITMAPFILEHEADER back on a CF_DIB so an ordinary decoder can read it.
    ///
    /// The pixel offset has to account for the colour table, whose size the header only
    /// implies: a palettised DIB puts entries between the header and the pixels, and
    /// pointing at the wrong offset yields a picture of noise rather than a failure.
    /// </summary>
    public static byte[] DibToBmp(byte[] dib)
    {
        if (dib is null) throw new ArgumentNullException(nameof(dib));
        if (dib.Length < 40) throw new ArgumentException("Too short to be a DIB.", nameof(dib));

        int headerSize = BitConverter.ToInt32(dib, 0);
        short bitCount = BitConverter.ToInt16(dib, 14);
        int coloursUsed = BitConverter.ToInt32(dib, 32);
        int compression = BitConverter.ToInt32(dib, 16);

        int paletteEntries = coloursUsed != 0
            ? coloursUsed
            : bitCount <= 8 ? 1 << bitCount : 0;
        int paletteBytes = paletteEntries * 4;

        // BI_BITFIELDS (3) adds three colour masks ahead of the pixels.
        if (compression == 3) paletteBytes += 12;

        int pixelOffset = BitmapFileHeaderSize + headerSize + paletteBytes;

        var bmp = new byte[BitmapFileHeaderSize + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(pixelOffset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, BitmapFileHeaderSize);
        return bmp;
    }

    // ---- The paste scratch folder ----------------------------------------

    /// <summary>
    /// A path for an image pasted at <paramref name="at"/>, guaranteed not to be taken.
    /// Timestamped so the folder stays legible if anyone opens it.
    /// </summary>
    public static string NewPastePath(string folder, DateTime at)
    {
        string stem = $"{PasteFilePrefix}{at:yyyy-MM-dd-HHmmss}";
        string candidate = Path.Combine(folder, stem + ".png");

        // Two pastes inside the same second are entirely possible.
        for (int n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(folder, $"{stem}-{n}.png");

        return candidate;
    }

    /// <summary>
    /// Clears out pasted images older than <paramref name="maxAge"/>.
    ///
    /// Run at startup rather than at exit: an app that is killed never gets to clean up
    /// after itself, and these would otherwise pile up unseen for as long as Atelier
    /// stays installed. Only Atelier's own leftovers are touched, and a file that will
    /// not delete -- most likely the one currently open -- is skipped rather than
    /// allowed to take startup down with it.
    /// </summary>
    public static void SweepPasteFolder(string folder, DateTime nowUtc, TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(folder)) return;

            foreach (var file in Directory.EnumerateFiles(folder, PasteFilePrefix + "*.png"))
            {
                try
                {
                    if (nowUtc - File.GetLastWriteTimeUtc(file) > maxAge)
                        File.Delete(file);
                }
                catch (Exception)
                {
                    // Locked, in use, or gone already. Never worth failing startup over.
                }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Writes an image to the scratch folder and hands back its path.</summary>
    public static string WritePaste(byte[] imageBytes)
    {
        Directory.CreateDirectory(PasteFolder);
        string path = NewPastePath(PasteFolder, DateTime.Now);

        using (var image = new MagickImage(imageBytes))
            image.Write(path, MagickFormat.Png);

        return path;
    }

    // ---- The clipboard itself --------------------------------------------

    /// <summary>
    /// Puts the picture on the clipboard as pixels, as a file and as a path, in one
    /// go. Returns false if the clipboard could not be opened -- another process can
    /// hold it briefly, and there is nothing to do about that but say so.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool Copy(string path)
    {
        if (!File.Exists(path)) return false;

        try
        {
            byte[] bmp;
            using (var image = new MagickImage(path))
            {
                // Paint and friends expect a plain 24-bit DIB; alpha in a CF_DIB is
                // read inconsistently, so it is flattened onto white first.
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
                using var ms = new MemoryStream();
                image.Write(ms, MagickFormat.Bmp);
                bmp = ms.ToArray();
            }

            byte[] png;
            using (var image = new MagickImage(path))
            {
                using var ms = new MemoryStream();
                image.Write(ms, MagickFormat.Png);
                png = ms.ToArray();
            }

            return WindowsClipboard.SetImageAndFile(BmpToDib(bmp), png, path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The image on the clipboard, as a path on disk.
    ///
    /// A copied file is preferred over copied pixels when both are present: it is the
    /// original rather than a re-encode, and it keeps its name and metadata. Only when
    /// there is no file do the raw pixels get written to the scratch folder.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? Paste(Func<string, bool> isSupported)
    {
        try
        {
            var files = WindowsClipboard.GetFiles();
            var supported = files.FirstOrDefault(f => isSupported(f) && File.Exists(f));
            if (supported != null) return supported;

            var image = WindowsClipboard.GetImageBytes();
            return image == null ? null : WritePaste(image);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// The Win32 clipboard, reduced to the two things Atelier needs.
///
/// Done by hand rather than through Avalonia's clipboard because a useful copy has to
/// publish several formats in a single session -- open, empty, then set each one -- and
/// the cross-platform abstraction has no notion of CF_DIB or CF_HDROP.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsClipboard
{
    private const uint CF_BITMAP_DIB = 8;      // CF_DIB
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;

    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, char[]? lpszFile, uint cch);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct DROPFILES
    {
        public uint pFiles;
        public int pt_x;
        public int pt_y;
        [MarshalAs(UnmanagedType.Bool)] public bool fNC;
        [MarshalAs(UnmanagedType.Bool)] public bool fWide;
    }

    /// <summary>
    /// Publishes the picture in every form a paste target might ask for. The handles
    /// are deliberately not freed on success: SetClipboardData transfers ownership to
    /// the system, and freeing them afterwards is a use-after-free the receiving app
    /// pays for, not this one.
    /// </summary>
    public static bool SetImageAndFile(byte[] dib, byte[] png, string path)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;

        try
        {
            EmptyClipboard();

            Publish(CF_BITMAP_DIB, dib);

            uint pngFormat = RegisterClipboardFormatW("PNG");
            if (pngFormat != 0) Publish(pngFormat, png);

            PublishDropFiles(path);
            PublishText(path);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void Publish(uint format, byte[] bytes)
    {
        IntPtr handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)bytes.Length);
        if (handle == IntPtr.Zero) return;

        IntPtr target = GlobalLock(handle);
        if (target == IntPtr.Zero) { GlobalFree(handle); return; }

        try { Marshal.Copy(bytes, 0, target, bytes.Length); }
        finally { GlobalUnlock(handle); }

        if (SetClipboardData(format, handle) == IntPtr.Zero) GlobalFree(handle);
    }

    /// <summary>CF_HDROP: a DROPFILES header, then a double-null-terminated path list.</summary>
    private static void PublishDropFiles(string path)
    {
        var header = new DROPFILES
        {
            pFiles = (uint)Marshal.SizeOf<DROPFILES>(),
            fWide = true,
        };

        int headerSize = Marshal.SizeOf<DROPFILES>();
        byte[] paths = System.Text.Encoding.Unicode.GetBytes(path + '\0' + '\0');
        var payload = new byte[headerSize + paths.Length];

        IntPtr scratch = Marshal.AllocHGlobal(headerSize);
        try
        {
            Marshal.StructureToPtr(header, scratch, false);
            Marshal.Copy(scratch, payload, 0, headerSize);
        }
        finally
        {
            Marshal.FreeHGlobal(scratch);
        }

        paths.CopyTo(payload, headerSize);
        Publish(CF_HDROP, payload);
    }

    private static void PublishText(string text) =>
        Publish(CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(text + '\0'));

    /// <summary>The files on the clipboard, or an empty list when there are none.</summary>
    public static IReadOnlyList<string> GetFiles()
    {
        if (!IsClipboardFormatAvailable(CF_HDROP)) return Array.Empty<string>();
        if (!OpenClipboard(IntPtr.Zero)) return Array.Empty<string>();

        try
        {
            IntPtr drop = GetClipboardData(CF_HDROP);
            if (drop == IntPtr.Zero) return Array.Empty<string>();

            IntPtr locked = GlobalLock(drop);
            if (locked == IntPtr.Zero) return Array.Empty<string>();

            try
            {
                uint count = DragQueryFileW(locked, 0xFFFFFFFF, null, 0);
                var files = new List<string>((int)count);

                for (uint i = 0; i < count; i++)
                {
                    uint length = DragQueryFileW(locked, i, null, 0);
                    var buffer = new char[length + 1];
                    if (DragQueryFileW(locked, i, buffer, length + 1) > 0)
                        files.Add(new string(buffer, 0, (int)length));
                }

                return files;
            }
            finally
            {
                GlobalUnlock(drop);
            }
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// The pixels on the clipboard as PNG bytes, or null when there is no image.
    /// A publisher's own PNG is preferred over CF_DIB: it keeps transparency, which
    /// a DIB round trip does not.
    /// </summary>
    public static byte[]? GetImageBytes()
    {
        uint pngFormat = RegisterClipboardFormatW("PNG");

        if (pngFormat != 0 && IsClipboardFormatAvailable(pngFormat))
        {
            var png = Read(pngFormat);
            if (png != null) return png;
        }

        if (!IsClipboardFormatAvailable(CF_BITMAP_DIB)) return null;

        var dib = Read(CF_BITMAP_DIB);
        return dib == null ? null : ClipboardImage.DibToBmp(dib);
    }

    private static byte[]? Read(uint format)
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;

        try
        {
            IntPtr handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) return null;

            IntPtr locked = GlobalLock(handle);
            if (locked == IntPtr.Zero) return null;

            try
            {
                int size = (int)GlobalSize(handle);
                if (size <= 0) return null;

                var bytes = new byte[size];
                Marshal.Copy(locked, bytes, 0, size);
                return bytes;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }
}
