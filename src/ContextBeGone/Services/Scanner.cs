using ContextBeGone.Models;
using Microsoft.Win32;

namespace ContextBeGone.Services;

/// <summary>Walks the registry and turns raw keys into <see cref="MenuEntry"/> objects.</summary>
public static class Scanner
{
    public static List<MenuEntry> Scan(Scene scene, bool loadIcons = true)
    {
        var entries = scene switch
        {
            { IsCommandStore: true } => ScanCommandStore(scene),
            { IsShellNew: true } => ScanShellNew(scene),
            { IsPackaged: true } => ScanPackaged(scene),
            { ClassesPath: not null } => ScanClasses(scene, scene.ClassesPath),
            _ => new List<MenuEntry>(),
        };

        if (loadIcons)
            foreach (var entry in entries)
                entry.Icon = ResolveIcon(entry);

        return entries.OrderBy(e => e.Kind)
                      .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                      .ToList();
    }

    private static List<MenuEntry> ScanClasses(Scene scene, string root)
    {
        var result = new List<MenuEntry>();

        AddVerbs(result, scene, $@"{root}\shell");
        AddHandlers(result, scene, $@"{root}\shellex\ContextMenuHandlers", EntryKind.ContextMenuHandler);
        AddHandlers(result, scene, $@"{root}\shellex\DragDropHandlers", EntryKind.DragDropHandler);

        return result;
    }

    private static void AddVerbs(List<MenuEntry> result, Scene scene, string shellPath)
    {
        foreach (var name in RegistryPaths.EnumerateClassesSubKeys(shellPath))
        {
            var path = $@"{shellPath}\{name}";
            using var key = RegistryPaths.OpenMerged(path);
            if (key is null) continue;

            var entry = new MenuEntry
            {
                Scene = scene,
                Kind = EntryKind.StaticVerb,
                KeyName = name,
                ClassesPath = path,
                DisplayPath = $@"HKEY_CLASSES_ROOT\{path}",
                InMachineHive = RegistryPaths.ExistsInMachine(path),
                InUserHive = RegistryPaths.ExistsInUser(path),
            };

            ReadAllValues(key, entry);
            PopulateVerb(key, path, entry);
            ComputeStatus(entry);
            result.Add(entry);
        }
    }

    private static void AddHandlers(List<MenuEntry> result, Scene scene, string containerPath, EntryKind kind)
    {
        foreach (var name in RegistryPaths.EnumerateClassesSubKeys(containerPath))
        {
            var path = $@"{containerPath}\{name}";
            using var key = RegistryPaths.OpenMerged(path);
            if (key is null) continue;

            var entry = new MenuEntry
            {
                Scene = scene,
                Kind = kind,
                KeyName = name,
                ClassesPath = path,
                DisplayPath = $@"HKEY_CLASSES_ROOT\{path}",
                InMachineHive = RegistryPaths.ExistsInMachine(path),
                InUserHive = RegistryPaths.ExistsInUser(path),
            };

            ReadAllValues(key, entry);

            // The CLSID is the key's default value; some registrations name the key after the CLSID instead.
            var clsid = RegistryPaths.ReadString(key, null).Trim();
            if (!RegistryPaths.LooksLikeGuid(clsid) && RegistryPaths.LooksLikeGuid(name.Trim()))
                clsid = name.Trim();

            entry.Clsid = clsid;
            entry.HandlerPath = RegistryPaths.ResolveComServerPath(clsid);

            var friendly = RegistryPaths.ResolveComServerName(clsid);
            entry.DisplayName = friendly.Length > 0 ? $"{name}  ({friendly})" : name;
            entry.CommandMechanism = "IContextMenu (in-process COM)";

            entry.Status = BlockedList.IsBlocked(clsid) ? EntryStatus.Disabled : EntryStatus.Enabled;
            if (entry.Status == EntryStatus.Disabled) entry.DisableMarkers.Add("Shell Extensions\\Blocked");

            result.Add(entry);
        }
    }

    private static List<MenuEntry> ScanCommandStore(Scene scene)
    {
        var result = new List<MenuEntry>();
        using var store = Registry.LocalMachine.OpenSubKey(RegistryPaths.CommandStorePath);
        if (store is null) return result;

        foreach (var name in store.GetSubKeyNames())
        {
            using var key = store.OpenSubKey(name);
            if (key is null) continue;

            var entry = new MenuEntry
            {
                Scene = scene,
                Kind = EntryKind.CommandStoreVerb,
                KeyName = name,
                ClassesPath = null,
                DisplayPath = $@"HKEY_LOCAL_MACHINE\{RegistryPaths.CommandStorePath}\{name}",
                InMachineHive = true,
            };

            ReadAllValues(key, entry);
            PopulateVerbCore(key, entry);
            using var command = key.OpenSubKey("command");
            entry.Command = RegistryPaths.ReadString(command, null);
            ComputeStatus(entry);
            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Reads packaged (MSIX) COM classes. A modern app such as Windows Notepad or Notepad++ 8.7+
    /// ships its "Edit with …" command as a packaged IExplorerCommand, registered only under
    /// PackagedCom — nothing appears in HKCR, which is why these items cannot be found by looking
    /// at shell/shellex keys. They are suppressed through the same Blocked CLSID list.
    /// </summary>
    private static List<MenuEntry> ScanPackaged(Scene scene)
    {
        var result = new List<MenuEntry>();

        using var index = Registry.LocalMachine.OpenSubKey(RegistryPaths.PackagedClassIndexPath);
        if (index is null) return result;

        foreach (var clsid in index.GetSubKeyNames())
        {
            using var classKey = index.OpenSubKey(clsid);
            var packages = classKey?.GetSubKeyNames() ?? [];
            var package = packages.FirstOrDefault() ?? string.Empty;

            var dllPath = string.Empty;
            if (package.Length > 0)
            {
                using var detail = Registry.LocalMachine.OpenSubKey(
                    $@"{RegistryPaths.PackagedPackagePath}\{package}\Class\{clsid}");
                dllPath = RegistryPaths.ReadString(detail, "DllPath");
            }

            // "Microsoft.WindowsNotepad_11.2607.14.0_x64__8wekyb3d8bbwe" -> "Microsoft.WindowsNotepad"
            var shortName = package.Split('_').FirstOrDefault() ?? package;

            var entry = new MenuEntry
            {
                Scene = scene,
                Kind = EntryKind.PackagedHandler,
                KeyName = clsid,
                ClassesPath = null,
                DisplayPath = $@"HKEY_LOCAL_MACHINE\{RegistryPaths.PackagedClassIndexPath}\{clsid}",
                InMachineHive = true,
                Clsid = clsid,
                HandlerPath = dllPath,
                DisplayName = shortName.Length > 0
                    ? (dllPath.Length > 0 ? $"{shortName}  —  {dllPath}" : shortName)
                    : clsid,
                CommandMechanism = package.Length > 0
                    ? $"Packaged COM class from {package}"
                    : "Packaged COM class",
            };

            entry.Values["PackageFullName"] = package;
            if (dllPath.Length > 0) entry.Values["DllPath"] = dllPath;
            if (packages.Length > 1) entry.Values["Packages"] = string.Join(" | ", packages);

            entry.Status = BlockedList.IsBlocked(clsid) ? EntryStatus.Disabled : EntryStatus.Enabled;
            if (entry.Status == EntryStatus.Disabled) entry.DisableMarkers.Add("Shell Extensions\\Blocked");

            result.Add(entry);
        }

        return result;
    }

    private static List<MenuEntry> ScanShellNew(Scene scene)
    {
        var result = new List<MenuEntry>();

        foreach (var ext in RegistryPaths.EnumerateClassesSubKeys(string.Empty).Where(n => n.StartsWith('.')))
        {
            foreach (var (path, active) in ShellNewCandidates(ext))
            {
                using var key = RegistryPaths.OpenMerged(path);
                if (key is null) continue;

                var progId = string.Empty;
                using (var extKey = RegistryPaths.OpenMerged(ext))
                    progId = RegistryPaths.ReadString(extKey, null);

                var typeName = string.Empty;
                if (progId.Length > 0)
                {
                    using var progKey = RegistryPaths.OpenMerged(progId);
                    typeName = Native.ResolveDisplayString(RegistryPaths.ReadString(progKey, "FriendlyTypeName"));
                    if (typeName.Length == 0) typeName = RegistryPaths.ReadString(progKey, null);
                }

                var entry = new MenuEntry
                {
                    Scene = scene,
                    Kind = EntryKind.ShellNew,
                    KeyName = ext,
                    ClassesPath = path,
                    DisplayPath = $@"HKEY_CLASSES_ROOT\{path}",
                    InMachineHive = RegistryPaths.ExistsInMachine(path),
                    InUserHive = RegistryPaths.ExistsInUser(path),
                    DisplayName = typeName.Length > 0 ? $"{typeName}  ({ext})" : ext,
                    Status = active ? EntryStatus.Enabled : EntryStatus.Disabled,
                };

                ReadAllValues(key, entry);
                entry.CommandMechanism = DescribeShellNew(entry);
                if (!active) entry.DisableMarkers.Add("key renamed to ShellNew-");

                result.Add(entry);
                break; // one row per extension
            }
        }

        return result;
    }

    /// <summary>ShellNew can sit on the extension key or on its ProgID; "ShellNew-" is the disabled form.</summary>
    private static IEnumerable<(string Path, bool Active)> ShellNewCandidates(string ext)
    {
        yield return ($@"{ext}\ShellNew", true);
        yield return ($@"{ext}\ShellNew-", false);

        using var extKey = RegistryPaths.OpenMerged(ext);
        var progId = RegistryPaths.ReadString(extKey, null);
        if (progId.Length == 0) yield break;

        yield return ($@"{progId}\ShellNew", true);
        yield return ($@"{progId}\ShellNew-", false);
    }

    private static string DescribeShellNew(MenuEntry entry)
    {
        if (entry.Values.ContainsKey("NullFile")) return "Creates an empty file (NullFile)";
        if (entry.Values.TryGetValue("FileName", out var file)) return $"Copies template: {file}";
        if (entry.Values.TryGetValue("Command", out var cmd)) return $"Runs: {cmd}";
        if (entry.Values.ContainsKey("Data")) return "Creates a file from inline binary data (Data)";
        return "ShellNew";
    }

    private static void PopulateVerb(RegistryKey key, string path, MenuEntry entry)
    {
        PopulateVerbCore(key, entry);

        using var command = RegistryPaths.OpenMerged($@"{path}\command");
        entry.Command = RegistryPaths.ReadString(command, null);

        if (entry.Command.Length == 0)
        {
            var delegateExecute = RegistryPaths.ReadString(command, "DelegateExecute");
            if (delegateExecute.Length > 0)
            {
                entry.Clsid = delegateExecute;
                entry.HandlerPath = RegistryPaths.ResolveComServerPath(delegateExecute);
                entry.CommandMechanism = "DelegateExecute (IExecuteCommand)";
            }
        }

        if (entry.Values.TryGetValue("ExplorerCommandHandler", out var explorerCommand) && explorerCommand.Length > 0)
        {
            entry.Clsid = explorerCommand;
            entry.HandlerPath = RegistryPaths.ResolveComServerPath(explorerCommand);
            entry.CommandMechanism = "ExplorerCommandHandler (IExplorerCommand)";
        }

        using (var dropTarget = RegistryPaths.OpenMerged($@"{path}\DropTarget"))
        {
            var clsid = RegistryPaths.ReadString(dropTarget, "CLSID");
            if (clsid.Length > 0)
            {
                entry.Clsid = clsid;
                entry.HandlerPath = RegistryPaths.ResolveComServerPath(clsid);
                entry.CommandMechanism = "DropTarget (IDropTarget)";
            }
        }

        if (entry.CommandMechanism.Length == 0)
            entry.CommandMechanism = entry.Command.Length > 0 ? "command line" : "no command registered";
    }

    private static void PopulateVerbCore(RegistryKey key, MenuEntry entry)
    {
        entry.RawDefaultValue = RegistryPaths.ReadString(key, null);
        entry.RawMuiVerb = RegistryPaths.ReadString(key, "MUIVerb");
        entry.IconSpec = RegistryPaths.ReadString(key, "Icon");
        entry.Position = RegistryPaths.ReadString(key, "Position");
        entry.SubCommands = RegistryPaths.ReadString(key, "SubCommands");
        entry.AppliesTo = RegistryPaths.ReadString(key, "AppliesTo");

        var label = entry.RawMuiVerb.Length > 0 ? entry.RawMuiVerb : entry.RawDefaultValue;
        var resolved = Native.ResolveDisplayString(label).Replace("&", string.Empty).Trim();
        entry.MenuText = resolved;
        entry.DisplayName = resolved.Length > 0 ? $"{resolved}  [{entry.KeyName}]" : entry.KeyName;
    }

    private static void ReadAllValues(RegistryKey key, MenuEntry entry)
    {
        foreach (var name in key.GetValueNames())
        {
            var display = name.Length == 0 ? "(Default)" : name;
            try
            {
                var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                entry.Values[display] = value switch
                {
                    null => string.Empty,
                    string[] multi => string.Join(" | ", multi),
                    byte[] bytes => Convert.ToHexString(bytes),
                    int i => $"0x{i:X8} ({i})",
                    long l => $"0x{l:X16} ({l})",
                    _ => value.ToString() ?? string.Empty,
                };
            }
            catch (Exception)
            {
                entry.Values[display] = "<unreadable>";
            }
        }
    }

    private static void ComputeStatus(MenuEntry entry)
    {
        foreach (var marker in RegistryPaths.DisableValueNames)
            if (entry.Values.ContainsKey(marker))
                entry.DisableMarkers.Add(marker);

        entry.HasUserOverlay = entry.ClassesPath is not null && RegistryPaths.ExistsInUser(entry.ClassesPath);

        entry.Status = entry.DisableMarkers.Count > 0
            ? EntryStatus.Disabled
            : entry.Values.ContainsKey("Extended") ? EntryStatus.ShiftOnly : EntryStatus.Enabled;
    }

    /// <summary>Fills in an entry's icon on demand, for entries scanned with loadIcons: false.</summary>
    public static void EnsureIcon(MenuEntry entry)
    {
        if (entry.Icon is null) entry.Icon = ResolveIcon(entry);
    }

    private static System.Windows.Media.ImageSource? ResolveIcon(MenuEntry entry)
    {
        if (entry.IconSpec.Length > 0)
        {
            var icon = Native.LoadIcon(entry.IconSpec);
            if (icon is not null) return icon;
        }

        if (entry.HandlerPath.Length > 0)
            return Native.LoadIcon(entry.HandlerPath);

        if (entry.Command.Length > 0)
        {
            var exe = ExtractExecutable(entry.Command);
            if (exe.Length > 0) return Native.LoadIcon(exe);
        }

        return null;
    }

    /// <summary>Pulls the program path out of a command line so it can be used as an icon source.</summary>
    private static string ExtractExecutable(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return string.Empty;

        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : string.Empty;
        }

        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }
}
