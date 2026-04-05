using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Atelier;

[SupportedOSPlatform("windows")]
public static class FileAssociationHelper
{
    private const string ProgIdPrefix = "Atelier";
    private const string AppName = "Atelier Image Viewer";

    public static readonly (string Extension, string Description)[] SupportedTypes =
    {
        (".png", "PNG Image"),
        (".jpg", "JPEG Image"),
        (".jpeg", "JPEG Image"),
        (".ico", "Icon File"),
        (".heic", "HEIC Image"),
        (".heif", "HEIF Image"),
        (".avif", "AVIF Image"),
    };

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNF_IDLIST = 0x0000;

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static void RegisterFileAssociations(IEnumerable<string> extensions)
    {
        if (!IsWindows) return;

        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath)) return;

        var classesRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes", true);
        if (classesRoot == null) return;

        var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);

        foreach (var (ext, description) in SupportedTypes)
        {
            var progId = $"{ProgIdPrefix}{ext.TrimStart('.').ToUpperInvariant()}";

            if (extSet.Contains(ext))
            {
                // Register
                using (var progIdKey = classesRoot.CreateSubKey(progId))
                {
                    progIdKey.SetValue("", $"{AppName} - {description}");
                    using var iconKey = progIdKey.CreateSubKey("DefaultIcon");
                    iconKey.SetValue("", $"\"{exePath}\",0");

                    using var commandKey = progIdKey.CreateSubKey(@"shell\open\command");
                    commandKey.SetValue("", $"\"{exePath}\" \"%1\"");
                }

                using (var extKey = classesRoot.CreateSubKey(ext))
                {
                    using var openWithKey = extKey.CreateSubKey("OpenWithProgids");
                    openWithKey.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
                }
            }
            else
            {
                // Unregister
                using (var extKey = classesRoot.OpenSubKey(ext, true))
                {
                    using var openWithKey = extKey?.OpenSubKey("OpenWithProgids", true);
                    openWithKey?.DeleteValue(progId, false);
                }
                try { classesRoot.DeleteSubKeyTree(progId, false); } catch { }
            }
        }

        classesRoot.Dispose();

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    public static HashSet<string> GetRegisteredExtensions()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!IsWindows) return result;

        var classesRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes", false);
        if (classesRoot == null) return result;

        foreach (var (ext, _) in SupportedTypes)
        {
            var progId = $"{ProgIdPrefix}{ext.TrimStart('.').ToUpperInvariant()}";
            using var key = classesRoot.OpenSubKey(progId, false);
            if (key != null)
                result.Add(ext);
        }

        classesRoot.Dispose();
        return result;
    }
}
