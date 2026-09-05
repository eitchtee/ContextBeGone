using ContextBeGone.Models;
using Microsoft.Win32;

namespace ContextBeGone.Services;

/// <summary>The set of HKEY_CLASSES_ROOT locations the shell consults for classic context menus.</summary>
public static class SceneCatalog
{
    /// <summary>
    /// Fixed scenes, documented under "Predefined Shell Objects" plus the shell namespace CLSIDs
    /// whose menus users normally want to prune.
    /// </summary>
    public static readonly Scene[] Fixed =
    [
        new() { Id = "Everywhere", Group = "Search", Name = "Search everywhere", IsGlobalSearch = true,
                Description = "Scan every scope on the system at once, then filter. Finds an app wherever it registered itself." },

        new() { Id = "AllFiles",   Group = "Files & folders", Name = "All files",             ClassesPath = @"*",                        Description = @"Right-click on any file. HKCR\*" },
        new() { Id = "AllFsObj",   Group = "Files & folders", Name = "All files and folders", ClassesPath = @"AllFilesystemObjects",     Description = @"Any file system object, including drives and shortcuts. HKCR\AllFilesystemObjects" },
        new() { Id = "Folder",     Group = "Files & folders", Name = "Folders (any)",         ClassesPath = @"Folder",                   Description = @"Every folder, including virtual ones such as Control Panel. HKCR\Folder" },
        new() { Id = "Directory",  Group = "Files & folders", Name = "File folders",          ClassesPath = @"Directory",                Description = @"Real folders on disk. HKCR\Directory" },
        new() { Id = "DirBg",      Group = "Files & folders", Name = "Folder background",     ClassesPath = @"Directory\Background",     Description = @"Empty space inside an open folder. HKCR\Directory\Background" },
        new() { Id = "Drive",      Group = "Files & folders", Name = "Drives",                ClassesPath = @"Drive",                    Description = @"Drives in This PC. HKCR\Drive" },
        new() { Id = "Desktop",    Group = "Files & folders", Name = "Desktop background",    ClassesPath = @"DesktopBackground",        Description = @"Empty space on the desktop. HKCR\DesktopBackground" },
        new() { Id = "Unknown",    Group = "Files & folders", Name = "Unknown file types",    ClassesPath = @"Unknown",                  Description = @"Files with no registered association. HKCR\Unknown" },

        new() { Id = "LibFolder",  Group = "Libraries",       Name = "Library folder",        ClassesPath = @"LibraryFolder",            Description = @"A library such as Documents or Pictures. HKCR\LibraryFolder" },
        new() { Id = "LibBg",      Group = "Libraries",       Name = "Library background",    ClassesPath = @"LibraryFolder\background", Description = @"Empty space inside an open library. HKCR\LibraryFolder\background" },
        new() { Id = "UserLib",    Group = "Libraries",       Name = "User library",          ClassesPath = @"UserLibraryFolder",        Description = @"User-created libraries. HKCR\UserLibraryFolder" },

        new() { Id = "Network",    Group = "Network",         Name = "Network",               ClassesPath = @"Network",                  Description = @"The Network node. HKCR\Network" },
        new() { Id = "NetShare",   Group = "Network",         Name = "Network share",         ClassesPath = @"NetShare",                 Description = @"Shared folders on a server. HKCR\NetShare" },
        new() { Id = "NetServer",  Group = "Network",         Name = "Network server",        ClassesPath = @"NetServer",                Description = @"Servers browsed over the network. HKCR\NetServer" },

        new() { Id = "Printers",   Group = "Devices",         Name = "Printers",              ClassesPath = @"Printers",                 Description = @"Printer objects. HKCR\Printers" },
        new() { Id = "AudioCD",    Group = "Devices",         Name = "Audio CD",              ClassesPath = @"AudioCD",                  Description = @"An audio CD in the drive. HKCR\AudioCD" },
        new() { Id = "DVD",        Group = "Devices",         Name = "DVD",                   ClassesPath = @"DVD",                      Description = @"A DVD drive. HKCR\DVD" },

        new() { Id = "lnkfile",    Group = "Common types",    Name = "Shortcuts (.lnk)",      ClassesPath = @"lnkfile",                  Description = @"Shortcut files. HKCR\lnkfile" },
        new() { Id = "exefile",    Group = "Common types",    Name = "Executables (.exe)",    ClassesPath = @"exefile",                  Description = @"Programs. HKCR\exefile" },
        new() { Id = "batfile",    Group = "Common types",    Name = "Batch files (.bat)",    ClassesPath = @"batfile",                  Description = @"Batch scripts. HKCR\batfile" },
        new() { Id = "txtfile",    Group = "Common types",    Name = "Text files (.txt)",     ClassesPath = @"txtfile",                  Description = @"Plain text. HKCR\txtfile" },
        new() { Id = "inifile",    Group = "Common types",    Name = "Configuration (.ini)",  ClassesPath = @"inifile",                  Description = @"INI files. HKCR\inifile" },
        new() { Id = "Msi",        Group = "Common types",    Name = "Installers (.msi)",     ClassesPath = @"Msi.Package",              Description = @"Windows Installer packages. HKCR\Msi.Package" },

        new() { Id = "ThisPC",     Group = "Shell namespace", Name = "This PC",               ClassesPath = @"CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}", Description = "The This PC icon and its window background." },
        new() { Id = "RecycleBin", Group = "Shell namespace", Name = "Recycle Bin",           ClassesPath = @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}", Description = "The Recycle Bin icon." },
        new() { Id = "NetworkNs",  Group = "Shell namespace", Name = "Network (namespace)",   ClassesPath = @"CLSID\{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", Description = "The Network shell folder." },
        new() { Id = "UserFolder", Group = "Shell namespace", Name = "User folder",           ClassesPath = @"CLSID\{59031A47-3F72-44A7-89C5-5595FE6B30EE}", Description = "The user profile folder." },
        new() { Id = "OneDrive",   Group = "Shell namespace", Name = "OneDrive",              ClassesPath = @"CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}", Description = "The OneDrive namespace node." },
        new() { Id = "QuickAcc",   Group = "Shell namespace", Name = "Quick access / Home",   ClassesPath = @"CLSID\{679F85CB-0220-4080-B29B-5540CC05AAB6}", Description = "The Home / Quick access node." },
        new() { Id = "CtrlPanel",  Group = "Shell namespace", Name = "Control Panel",         ClassesPath = @"CLSID\{26EE0668-A00A-44D7-9371-BEB064C98683}", Description = "The Control Panel node." },

        new() { Id = "CommandStore", Group = "Shared", Name = "Command store", IsCommandStore = true,
                Description = @"Reusable verb definitions referenced by SubCommands. HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell" },
        new() { Id = "Packaged", Group = "Shared", Name = "Packaged apps (MSIX)", IsPackaged = true,
                Description = @"Shell extensions shipped inside packaged apps. Registered in PackagedCom, not HKCR, so they are invisible to registry-only tools." },
        new() { Id = "ShellNew", Group = "Shared", Name = "New submenu", IsShellNew = true,
                Description = @"Templates shown under New. HKCR\.ext\ShellNew and HKCR\.ext\<ProgID>\ShellNew" },
    ];

    /// <summary>
    /// Scenes discovered at runtime under <c>HKCR\SystemFileAssociations</c> — perceived types
    /// (text, image, audio, video, document, compressed) and per-extension overrides.
    /// </summary>
    public static IEnumerable<Scene> DiscoverSystemFileAssociations()
    {
        var names = RegistryPaths.EnumerateClassesSubKeys(@"SystemFileAssociations")
                                 .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            var path = $@"SystemFileAssociations\{name}";
            if (!HasMenuContainers(path)) continue;

            yield return new Scene
            {
                Id = "sfa:" + name,
                Group = "System file associations",
                Name = name,
                ClassesPath = path,
                Description = $@"Applies to every file the shell maps to {name}. HKCR\{path}",
            };
        }
    }

    /// <summary>
    /// Top-level HKCR keys that are not ProgIDs, or that are already covered by <see cref="Fixed"/>.
    /// Skipping them keeps the full sweep from walking COM registration trees with no menus in them.
    /// </summary>
    private static readonly HashSet<string> NotProgIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLSID", "WOW6432Node", "Interface", "TypeLib", "Record", "AppID", "Component Categories",
        "Installer", "Local Settings", "MIME", "PROTOCOLS", "Extensions", "Applications",
        "SystemFileAssociations", "Unknown", "Directory", "Folder", "Drive", "DesktopBackground",
        "LibraryFolder", "UserLibraryFolder", "Network", "NetShare", "NetServer", "Printers",
        "AudioCD", "DVD", "*", "AllFilesystemObjects", "Launcher.SystemSettings",
    };

    /// <summary>
    /// Every remaining place a menu can be registered: ProgIDs (txtfile, Notepad++_file, …),
    /// per-application keys under <c>Applications</c> (where "Open with" verbs live), and the
    /// shell namespace CLSIDs beyond the handful listed in <see cref="Fixed"/>.
    /// Used by the "search everywhere" sweep.
    /// </summary>
    public static IEnumerable<Scene> DiscoverProgIds()
    {
        foreach (var name in RegistryPaths.EnumerateClassesSubKeys(string.Empty))
        {
            // Extension keys are included too: a handful (.copilot, .loop, .rustdesk, …) carry
            // their own shell subkey rather than delegating to a ProgID.
            if (NotProgIds.Contains(name) || !HasMenuContainers(name)) continue;

            yield return new Scene
            {
                Id = "progid:" + name,
                Group = "File types and programs",
                Name = name,
                ClassesPath = name,
                Description = $@"HKCR\{name}",
            };
        }

        foreach (var name in RegistryPaths.EnumerateClassesSubKeys("Applications"))
        {
            var path = $@"Applications\{name}";
            if (!HasMenuContainers(path)) continue;

            yield return new Scene
            {
                Id = "app:" + name,
                Group = "Applications (Open with)",
                Name = name,
                ClassesPath = path,
                Description = $@"Verbs offered when opening a file with {name}. HKCR\{path}",
            };
        }

        // Cascading submenus defined by ExtendedSubCommandsKey live in their own tree, e.g.
        // Directory\ContextMenus\WindowsTerminal\shell\*. Those children are editable entries in
        // their own right, so they need to be reachable and searchable too.
        foreach (var root in RegistryPaths.EnumerateClassesSubKeys(string.Empty)
                                          .Concat([@"Directory\Background", @"LibraryFolder\Background"]))
        {
            var menusPath = root + @"\ContextMenus";
            foreach (var name in RegistryPaths.EnumerateClassesSubKeys(menusPath))
            {
                var path = menusPath + "\\" + name;
                if (!HasMenuContainers(path)) continue;

                yield return new Scene
                {
                    Id = "cascade:" + path,
                    Group = "Cascading submenus",
                    Name = name + "  (" + root + ")",
                    ClassesPath = path,
                    Description = "Items inside a cascading submenu referenced by ExtendedSubCommandsKey. HKCR\\" + path,
                };
            }
        }

        var known = Fixed.Where(s => s.ClassesPath?.StartsWith(@"CLSID\", StringComparison.OrdinalIgnoreCase) == true)
                         .Select(s => s.ClassesPath!)
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in RegistryPaths.EnumerateClassesSubKeys("CLSID"))
        {
            var path = $@"CLSID\{name}";
            if (known.Contains(path) || !HasMenuContainers(path)) continue;

            yield return new Scene
            {
                Id = "clsid:" + name,
                Group = "Shell namespace (discovered)",
                Name = RegistryPaths.ResolveComServerName(name) is { Length: > 0 } friendly ? $"{friendly}  {name}" : name,
                ClassesPath = path,
                Description = $@"HKCR\{path}",
            };
        }
    }

    /// <summary>Builds the scene list for one file extension: its ProgIDs plus SystemFileAssociations.</summary>
    public static IEnumerable<Scene> ForExtension(string extension)
    {
        var ext = extension.Trim();
        if (ext.Length == 0) yield break;
        if (!ext.StartsWith('.')) ext = "." + ext;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in ProgIdCandidates(ext))
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) continue;
            using var probe = Registry.ClassesRoot.OpenSubKey(path);
            if (probe is null) continue;

            yield return new Scene
            {
                Id = "ext:" + ext + ":" + path,
                Group = $"File type {ext}",
                Name = path,
                ClassesPath = path,
                SourceExtension = ext,
                Description = $@"HKCR\{path}",
            };
        }
    }

    private static IEnumerable<string> ProgIdCandidates(string ext)
    {
        // The extension key itself can carry a shell subkey.
        yield return ext;

        using (var extKey = Registry.ClassesRoot.OpenSubKey(ext))
        {
            if (extKey?.GetValue(null) is string progId && progId.Length > 0)
                yield return progId;

            using var openWith = extKey?.OpenSubKey("OpenWithProgids");
            if (openWith is not null)
                foreach (var name in openWith.GetValueNames().Where(n => n.Length > 0))
                    yield return name;

            if (extKey?.GetValue("PerceivedType") is string perceived && perceived.Length > 0)
                yield return $@"SystemFileAssociations\{perceived}";
        }

        yield return $@"SystemFileAssociations\{ext}";

        // The user's explicit file-association choice also contributes verbs.
        using var choice = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice");
        if (choice?.GetValue("ProgId") is string userProgId && userProgId.Length > 0)
            yield return userProgId;
    }

    private static bool HasMenuContainers(string classesPath)
    {
        foreach (var container in RegistryPaths.MenuContainers)
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"{classesPath}\{container}");
            if (key is not null && key.SubKeyCount > 0) return true;
        }
        return false;
    }
}
