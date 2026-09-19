using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Atelier;

/// <summary>
/// Moves a file to the Windows Recycle Bin.
///
/// Atelier never deletes permanently. Del sits right next to the arrow keys that walk
/// a folder, so a mis-tap has to stay recoverable -- and the bin, unlike a confirmation
/// dialog, is an undo the user can reach for minutes later rather than a click they
/// learn to dismiss without reading.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;

    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>
    /// No Pack is set on purpose. The widely copied <c>Pack = 1</c> version of this
    /// struct misaligns the pointer fields on x64 and SHFileOperation then reads
    /// rubbish for the path.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>
    /// Sends one file to the Recycle Bin. Returns false if the shell refused or the
    /// user cancelled -- a locked file, a denied permission, a network path with no bin.
    /// Callers keep the picture open on false rather than pretending it went.
    /// </summary>
    public static bool Send(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // The shell reads a double-null-terminated list here, not a plain string:
            // one terminator ends the path, the second ends the list. C# supplies only
            // the first, so the extra '\0' is load-bearing.
            pFrom = path + '\0' + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };

        try
        {
            int result = SHFileOperationW(ref op);
            return result == 0 && !op.fAnyOperationsAborted;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
