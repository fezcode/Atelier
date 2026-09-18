using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Atelier;

/// <summary>
/// Reads the file order out of the live Explorer window showing a folder, so Next/Prev
/// walk the pictures the way they are actually laid out on screen.
///
/// Explorer's per-folder view state is not readable from the filesystem, and mirroring
/// it by re-sorting ourselves does not work: a pictures folder is typically sorted by
/// PKEY_ItemDate, the shell's composite "Date" column, whose value is date-taken for
/// some files and date-modified for others. Rather than reimplement that, this asks the
/// view for its items in view order -- which also gets grouping, and any search or
/// filter the window has applied, for free.
///
/// The chain is IShellWindows -> IServiceProvider -> IShellBrowser -> IShellView ->
/// IFolderView2. Every link is allowed to fail: no window open on that folder, Explorer
/// restarting, a shell that hands back something unexpected. All of it means the same
/// thing to the caller -- null, use your own order.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ExplorerOrder
{
    /// <summary>
    /// The full paths Explorer is showing for <paramref name="folder"/>, in the order its
    /// window has them, or null when nothing could be read. Never throws.
    /// </summary>
    internal static List<string>? TryGetViewOrder(string folder)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(folder)) return null;

        object? shellWindows = null;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_ShellWindows, throwOnError: false);
            if (type == null) return null;

            shellWindows = Activator.CreateInstance(type);
            if (shellWindows is not IShellWindows windows) return null;

            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                var items = TryReadWindow(windows, i, folder);
                if (items != null) return items;
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Release(shellWindows);
        }
    }

    /// <summary>One window's worth of the walk; null unless it is showing <paramref name="folder"/>.</summary>
    private static List<string>? TryReadWindow(IShellWindows windows, int index, string folder)
    {
        object? win = null, browserObj = null, viewObj = null;
        try
        {
            win = windows.Item(index);
            if (win is not IServiceProvider provider) return null;

            var service = SID_STopLevelBrowser;
            var browserIid = IID_IShellBrowser;
            if (provider.QueryService(ref service, ref browserIid, out browserObj) != 0 ||
                browserObj is not IShellBrowser browser)
                return null;

            if (browser.QueryActiveShellView(out viewObj) != 0 || viewObj is not IFolderView2 view)
                return null;

            if (!PathsMatch(FolderOf(view), folder)) return null;

            return ItemsInViewOrder(view);
        }
        catch
        {
            return null;
        }
        finally
        {
            // Explorer owns these; hand them back rather than waiting for a GC.
            Release(viewObj);
            Release(browserObj);
            Release(win);
        }
    }

    private static List<string>? ItemsInViewOrder(IFolderView2 view)
    {
        if (view.ItemCount(SVGIO_ALLVIEW | SVGIO_FLAG_VIEWORDER, out int count) != 0 || count <= 0)
            return null;

        var paths = new List<string>(count);
        var itemIid = IID_IShellItem;
        for (int i = 0; i < count; i++)
        {
            object? itemObj = null;
            try
            {
                if (view.GetItem(i, ref itemIid, out itemObj) != 0 || itemObj is not IShellItem item)
                    continue;
                if (item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr name) != 0 || name == IntPtr.Zero)
                    continue;   // a library, a search result, anything without a path on disk
                try
                {
                    var path = Marshal.PtrToStringUni(name);
                    if (!string.IsNullOrEmpty(path)) paths.Add(path!);
                }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            catch { /* one unreadable row must not lose the other 286 */ }
            finally { Release(itemObj); }
        }
        return paths.Count > 0 ? paths : null;
    }

    /// <summary>The folder a view is showing: IFolderView2 -> IPersistFolder2 -> pidl -> path.</summary>
    private static string? FolderOf(IFolderView2 view)
    {
        try
        {
            var iid = IID_IPersistFolder2;
            if (view.GetFolder(ref iid, out IntPtr ppv) != 0 || ppv == IntPtr.Zero) return null;
            try
            {
                if (Marshal.GetObjectForIUnknown(ppv) is not IPersistFolder2 persist) return null;
                if (persist.GetCurFolder(out IntPtr pidl) != 0 || pidl == IntPtr.Zero) return null;
                try
                {
                    var buffer = new StringBuilder(260);
                    return SHGetPathFromIDListW(pidl, buffer) ? buffer.ToString() : null;
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
            finally { Marshal.Release(ppv); }
        }
        catch { return null; }
    }

    private static bool PathsMatch(string? a, string? b) =>
        a != null && b != null &&
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar),
                      StringComparison.OrdinalIgnoreCase);

    private static void Release(object? com)
    {
        try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); }
        catch { }
    }

    private const uint SVGIO_ALLVIEW = 0x00000002;
    private const uint SVGIO_FLAG_VIEWORDER = 0x80000000;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    private static readonly Guid CLSID_ShellWindows   = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser    = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IID_IPersistFolder2  = new("1AC3D9F0-175C-11D1-95BE-00609797EA4F");
    private static readonly Guid IID_IShellItem       = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;
}

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
    int Count { get; }

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object Item([MarshalAs(UnmanagedType.Struct)] object index);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid guidService, ref Guid riid,
                     [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
}

/// <remarks>
/// Only QueryActiveShellView is ever called. The members above it are declared solely to
/// hold their vtable slots, so their signatures are placeholders -- but their COUNT and
/// ORDER are load-bearing. The first two belong to IOleWindow, which IShellBrowser derives
/// from.
/// </remarks>
[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    void GetWindow(out IntPtr phwnd);
    void ContextSensitiveHelp(bool fEnterMode);
    void InsertMenusSB();
    void SetMenuSB();
    void RemoveMenusSB();
    void SetStatusTextSB();
    void EnableModelessSB();
    void TranslateAcceleratorSB();
    void BrowseObject();
    void GetViewStateStream();
    void GetControlWindow();
    void SendControlMsg();
    [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object ppshv);
    void OnViewWindowActive();
    void SetToolbarItems();
}

[ComImport, Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFolder2
{
    void GetClassID(out Guid pClassID);
    void Initialize(IntPtr pidl);
    [PreserveSig] int GetCurFolder(out IntPtr ppidl);
}

/// <remarks>
/// Same rule as <see cref="IShellBrowser"/>: placeholder members hold vtable slots and must
/// not be reordered. The first fourteen are IFolderView's, which IFolderView2 derives from;
/// GetFolder, ItemCount, GetItem and GetSortColumnCount are the live ones.
/// </remarks>
[ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView2
{
    // --- IFolderView ---
    void GetCurrentViewMode(out uint pViewMode);
    void SetCurrentViewMode(uint viewMode);
    [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
    void Item(int iItemIndex, out IntPtr ppidl);
    [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
    void Items(uint uFlags, ref Guid riid, out IntPtr ppv);
    void GetSelectionMarkedItem(out int piItem);
    void GetFocusedItem(out int piItem);
    void GetItemPosition(IntPtr pidl, out int ppt);
    void GetSpacing(out int ppt);
    void GetDefaultSpacing(out int ppt);
    void GetAutoArrange();
    void SelectItem(int iItem, uint dwFlags);
    void SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
    // --- IFolderView2 ---
    void SetGroupBy(ref PROPERTYKEY key, bool fAscending);
    void GetGroupBy(out PROPERTYKEY pkey, out bool pfAscending);
    void SetViewProperty();
    void GetViewProperty();
    void SetTileViewProperties();
    void SetExtendedTileViewProperties();
    void SetText(int iType, [MarshalAs(UnmanagedType.LPWStr)] string pwszText);
    void SetCurrentFolderFlags(uint dwMask, uint dwFlags);
    void GetCurrentFolderFlags(out uint pdwFlags);
    [PreserveSig] int GetSortColumnCount(out int pcColumns);
    void SetSortColumns(IntPtr rgSortColumns, int cColumns);
    [PreserveSig] int GetSortColumns(IntPtr rgSortColumns, int cColumns);
    [PreserveSig] int GetItem(int iItem, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    void GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(IShellItem psi, uint hint, out int piOrder);
}
