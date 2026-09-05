using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ContextBeGone.Services;

/// <summary>One item as the shell would actually draw it.</summary>
public sealed class ProbedItem
{
    public required string Text { get; init; }
    public required int Depth { get; init; }
    public string Verb { get; set; } = string.Empty;
    public bool IsSeparator { get; init; }
    public bool IsSubmenu { get; init; }

    /// <summary>True when the item only appears if SHIFT is held while right-clicking.</summary>
    public bool ExtendedOnly { get; set; }

    /// <summary>Name of the COM handler that contributed this item, when it could be attributed.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// Asks the shell what a context menu really contains, instead of inferring it from the registry.
///
/// This is the only way to see items that COM handlers draw in code — "Move to OneDrive" is
/// produced by OneDrive's FileSyncEx handler and exists nowhere in the registry as text. Items are
/// attributed by instantiating each registered handler on its own and asking what it adds.
///
/// Shell extensions are third-party code loaded in-process, so a faulty one can take the process
/// down. Callers run this in a child process (<c>--probe</c>) for exactly that reason.
/// </summary>
public static class ShellMenuProbe
{
    // ─────────────────────────────────────── interop

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
                                           ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
                                        ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                                    uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(IntPtr pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved,
                                           [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pszName, uint cchMax);
    }

    /// <summary>
    /// IContextMenu2 exists so the host can forward menu messages to the handler. Static cascading
    /// menus (SubCommands / ExtendedSubCommandsKey) are empty until the shell receives
    /// WM_INITMENUPOPUP for them, which is why a submenu looks blank if you never send it.
    /// </summary>
    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(IntPtr pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved,
                                           [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    /// <summary>IContextMenu3 is the preferred sink; the shell's own cascade objects implement it.</summary>
    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(IntPtr pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved,
                                           [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [ComImport, Guid("000214E8-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellExtInit
    {
        [PreserveSig] int Initialize(IntPtr pidlFolder, IDataObject? pdtobj, IntPtr hkeyProgID);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl,
                                                 uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IntPtr ppshf);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
                                               ref Guid riid, out IntPtr ppv);

    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuItemInfoW")]
    private static extern bool GetMenuItemInfo(IntPtr hMenu, uint item, bool fByPosition, ref MENUITEMINFO lpmii);

    [StructLayout(LayoutKind.Sequential)]
    private struct MENUITEMINFO
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    private const uint MIIM_STATE = 0x01, MIIM_ID = 0x02, MIIM_SUBMENU = 0x04,
                       MIIM_FTYPE = 0x100, MIIM_STRING = 0x40;
    private const uint MFT_SEPARATOR = 0x800;
    private const uint CMF_NORMAL = 0x00;
    private const uint CMF_CANRENAME = 0x10;
    private const uint CMF_ITEMMENU = 0x80;
    private const uint CMF_EXTENDEDVERBS = 0x100;

    /// <summary>
    /// Tells the shell we are prepared to build cascading menus synchronously. Without it a static
    /// cascade (SubCommands / ExtendedSubCommandsKey) is deferred and reads back empty, because we
    /// are not running a real menu loop that would fill it in on demand.
    /// </summary>
    private const uint CMF_SYNCCASCADEMENU = 0x1000;

    /// <summary>
    /// What Explorer itself passes for a menu on a selected item. Passing bare CMF_NORMAL is not
    /// faithful: without CMF_ITEMMENU several handlers (PowerToys, Defender) behave as though they
    /// were being asked for a background menu, and without CMF_CANRENAME the Rename verb is omitted.
    /// </summary>
    private const uint ItemMenuFlags = CMF_NORMAL | CMF_ITEMMENU | CMF_CANRENAME | CMF_SYNCCASCADEMENU;
    private const uint GCS_VERBW = 0x04;
    private const uint WM_INITMENUPOPUP = 0x0117;
    private const uint CLSCTX_INPROC_SERVER = 0x1;

    private const uint IdFirst = 1, IdLast = 0x7FFF;

    private static Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    private static Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");
    private static Guid IID_IShellExtInit = new("000214E8-0000-0000-C000-000000000046");
    private static Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    // ─────────────────────────────────────── public API

    /// <summary>
    /// Enumerates the real context menu for a file or folder path.
    /// <paramref name="extended"/> asks for the SHIFT+right-click menu, which is what the shell
    /// builds when CMF_EXTENDEDVERBS is passed; without it you get the ordinary menu.
    /// </summary>
    public static List<ProbedItem> ProbeShellMenu(string path, bool extended = true)
    {
        var items = new List<ProbedItem>();

        var hr = SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _);
        if (hr != 0 || pidl == IntPtr.Zero)
            throw new InvalidOperationException($"Cannot resolve '{path}' in the shell namespace (0x{hr:X8}).");

        IntPtr parentPtr = IntPtr.Zero, menuPtr = IntPtr.Zero;
        var hmenu = IntPtr.Zero;

        try
        {
            hr = SHBindToParent(pidl, ref IID_IShellFolder, out parentPtr, out var childPidl);
            if (hr != 0) throw new InvalidOperationException($"SHBindToParent failed (0x{hr:X8}).");

            var parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
            // Some handlers (Defender, PowerToys) return nothing when asked with a null owner
            // window, so give them a real HWND — that is what Explorer does.
            hr = parent.GetUIObjectOf(GetDesktopWindow(), 1, [childPidl], ref IID_IContextMenu, IntPtr.Zero, out menuPtr);
            if (hr != 0 || menuPtr == IntPtr.Zero)
                throw new InvalidOperationException($"The shell provided no context menu (0x{hr:X8}).");

            var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);

            hmenu = CreatePopupMenu();
            var flags = extended ? ItemMenuFlags | CMF_EXTENDEDVERBS : ItemMenuFlags;
            hr = contextMenu.QueryContextMenu(hmenu, 0, IdFirst, IdLast, flags);
            if (hr < 0) throw new InvalidOperationException($"QueryContextMenu failed (0x{hr:X8}).");

            ReadMenu(hmenu, contextMenu, items, depth: 0, contextMenu);
        }
        finally
        {
            if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
            CoTaskMemFree(pidl);
        }

        return items;
    }

    /// <summary>
    /// Enumerates the menu for the *background* of a folder — right-clicking empty space inside it.
    /// That menu does not come from the parent folder's GetUIObjectOf; it comes from the folder's
    /// own view object, which is why it carries a different set of verbs (Directory\Background).
    /// </summary>
    public static List<ProbedItem> ProbeBackgroundMenu(string path, bool extended = true)
    {
        var items = new List<ProbedItem>();

        var hr = SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _);
        if (hr != 0 || pidl == IntPtr.Zero)
            throw new InvalidOperationException($"Cannot resolve '{path}' in the shell namespace (0x{hr:X8}).");

        IntPtr desktopPtr = IntPtr.Zero, folderPtr = IntPtr.Zero, menuPtr = IntPtr.Zero;
        var hmenu = IntPtr.Zero;

        try
        {
            if (SHGetDesktopFolder(out desktopPtr) != 0 || desktopPtr == IntPtr.Zero)
                throw new InvalidOperationException("SHGetDesktopFolder failed.");

            var desktop = (IShellFolder)Marshal.GetObjectForIUnknown(desktopPtr);
            hr = desktop.BindToObject(pidl, IntPtr.Zero, ref IID_IShellFolder, out folderPtr);
            if (hr != 0 || folderPtr == IntPtr.Zero)
                throw new InvalidOperationException($"Cannot bind to the folder (0x{hr:X8}). Is it a folder?");

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            hr = folder.CreateViewObject(GetDesktopWindow(), ref IID_IContextMenu, out menuPtr);
            if (hr != 0 || menuPtr == IntPtr.Zero)
                throw new InvalidOperationException($"The folder provided no background menu (0x{hr:X8}).");

            var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);

            hmenu = CreatePopupMenu();
            // A background menu is not an item menu, so CMF_ITEMMENU/CMF_CANRENAME do not apply.
            var flags = CMF_NORMAL | CMF_SYNCCASCADEMENU | (extended ? CMF_EXTENDEDVERBS : 0);
            hr = contextMenu.QueryContextMenu(hmenu, 0, IdFirst, IdLast, flags);
            if (hr < 0) throw new InvalidOperationException($"QueryContextMenu failed (0x{hr:X8}).");

            ReadMenu(hmenu, contextMenu, items, depth: 0, contextMenu);
        }
        finally
        {
            if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            if (desktopPtr != IntPtr.Zero) Marshal.Release(desktopPtr);
            CoTaskMemFree(pidl);
        }

        return items;
    }

    /// <summary>
    /// Asks one registered handler, in isolation, what it would add to the menu for this item.
    /// That is what maps a menu entry with no registry text back to the extension that draws it.
    /// </summary>
    public static List<string> ProbeHandler(string path, string clsid)
    {
        var results = new List<string>();
        if (!Guid.TryParse(clsid, out var guid)) return results;

        var hr = SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _);
        if (hr != 0 || pidl == IntPtr.Zero) return results;

        IntPtr parentPtr = IntPtr.Zero, dataPtr = IntPtr.Zero, extPtr = IntPtr.Zero;
        var hmenu = IntPtr.Zero;

        try
        {
            if (SHBindToParent(pidl, ref IID_IShellFolder, out parentPtr, out var childPidl) != 0) return results;
            var parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);

            if (parent.GetUIObjectOf(IntPtr.Zero, 1, [childPidl], ref IID_IDataObject, IntPtr.Zero, out dataPtr) != 0)
                return results;
            var dataObject = (IDataObject)Marshal.GetObjectForIUnknown(dataPtr);

            if (CoCreateInstance(ref guid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref IID_IUnknown, out extPtr) != 0
                || extPtr == IntPtr.Zero)
                return results;

            var unknown = Marshal.GetObjectForIUnknown(extPtr);
            if (unknown is not IShellExtInit init || unknown is not IContextMenu handlerMenu) return results;

            if (init.Initialize(IntPtr.Zero, dataObject, IntPtr.Zero) < 0) return results;

            hmenu = CreatePopupMenu();
            if (handlerMenu.QueryContextMenu(hmenu, 0, IdFirst, IdLast, ItemMenuFlags | CMF_EXTENDEDVERBS) < 0)
                return results;

            var items = new List<ProbedItem>();
            ReadMenu(hmenu, null, items, depth: 0, handlerMenu);
            results.AddRange(items.Where(i => !i.IsSeparator).Select(i => i.Text));
        }
        catch (Exception)
        {
            // A handler that refuses to initialise simply contributes nothing we can attribute.
        }
        finally
        {
            if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
            if (extPtr != IntPtr.Zero) Marshal.Release(extPtr);
            if (dataPtr != IntPtr.Zero) Marshal.Release(dataPtr);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
            CoTaskMemFree(pidl);
        }

        return results;
    }

    // ─────────────────────────────────────── menu walking

    private static void ReadMenu(IntPtr hmenu, IContextMenu? contextMenu, List<ProbedItem> items, int depth,
                                 object? messageSink = null)
    {
        if (depth > 3) return;

        var count = GetMenuItemCount(hmenu);
        for (var i = 0; i < count; i++)
        {
            var buffer = Marshal.AllocHGlobal(1024 * 2);
            try
            {
                var info = new MENUITEMINFO
                {
                    cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                    fMask = MIIM_STRING | MIIM_ID | MIIM_SUBMENU | MIIM_FTYPE | MIIM_STATE,
                    dwTypeData = buffer,
                    cch = 1024,
                };

                if (!GetMenuItemInfo(hmenu, (uint)i, true, ref info)) continue;

                if ((info.fType & MFT_SEPARATOR) != 0)
                {
                    items.Add(new ProbedItem { Text = "──────────", Depth = depth, IsSeparator = true });
                    continue;
                }

                var text = Marshal.PtrToStringUni(buffer, (int)info.cch) ?? string.Empty;
                text = text.Replace("&", string.Empty).Trim();

                var item = new ProbedItem
                {
                    Text = text,
                    Depth = depth,
                    IsSubmenu = info.hSubMenu != IntPtr.Zero,
                };

                if (contextMenu is not null && info.wID >= IdFirst && info.hSubMenu == IntPtr.Zero)
                    item.Verb = GetVerb(contextMenu, info.wID - IdFirst);

                items.Add(item);

                if (info.hSubMenu != IntPtr.Zero)
                {
                    PopulateSubmenu(messageSink, info.hSubMenu, i);
                    ReadMenu(info.hSubMenu, contextMenu, items, depth + 1, messageSink);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// Tells the handler the submenu is about to be shown, which is when the shell fills in a
    /// static cascading menu. Without this the submenu reads back empty.
    /// </summary>
    private static void PopulateSubmenu(object? sink, IntPtr hSubMenu, int position)
    {
        if (sink is null) return;

        var lParam = (IntPtr)(position & 0xFFFF);
        try
        {
            // Prefer IContextMenu3; the shell's static-cascade objects implement that one.
            if (sink is IContextMenu3 cm3)
            {
                cm3.HandleMenuMsg2(WM_INITMENUPOPUP, hSubMenu, lParam, out _);
                if (GetMenuItemCount(hSubMenu) > 0) return;
            }

            if (sink is IContextMenu2 cm2) cm2.HandleMenuMsg(WM_INITMENUPOPUP, hSubMenu, lParam);
        }
        catch (Exception)
        {
            // Handlers are free to not support this; the submenu simply stays as-is.
        }
    }

    /// <summary>
    /// The canonical verb for a command id. For a static verb this is the registry key name, which
    /// is exactly what is needed to find the entry again in the main list.
    /// </summary>
    private static string GetVerb(IContextMenu menu, uint offset)
    {
        var buffer = new byte[512];
        try
        {
            if (menu.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buffer, 255) != 0)
                return string.Empty;

            var text = Encoding.Unicode.GetString(buffer);
            var end = text.IndexOf('\0');
            return end >= 0 ? text[..end] : text.TrimEnd('\0');
        }
        catch (Exception)
        {
            // Handlers are free to not implement GetCommandString.
            return string.Empty;
        }
    }
}
