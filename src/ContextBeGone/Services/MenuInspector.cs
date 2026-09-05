using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ContextBeGone.Models;
using Microsoft.Win32;

namespace ContextBeGone.Services;

/// <summary>Result of inspecting one real file or folder.</summary>
public sealed class InspectionResult
{
    public string Path { get; set; } = string.Empty;
    public string? Error { get; set; }
    public List<InspectedItem> Items { get; set; } = new();

    /// <summary>For a folder: the menu you get right-clicking empty space inside it.</summary>
    public List<InspectedItem> BackgroundItems { get; set; } = new();

    public bool IsFolder { get; set; }
}

public sealed class InspectedItem
{
    public string Text { get; set; } = string.Empty;
    public string Verb { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SourceClsid { get; set; } = string.Empty;
    public int Depth { get; set; }
    public bool IsSeparator { get; set; }
    public bool IsSubmenu { get; set; }

    /// <summary>Only shown when SHIFT is held while right-clicking.</summary>
    public bool ExtendedOnly { get; set; }

    /// <summary>Registry key name of the static verb behind this item, when it could be matched.</summary>
    public string SourceKeyName { get; set; } = string.Empty;

    /// <summary>Classes-relative path of that key, e.g. Directory\Background\shell\WindowsTerminal.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Best term for finding this item back in the main list.</summary>
    public string SearchTerm =>
        SourceKeyName.Length > 0 ? SourceKeyName
        : SourceClsid.Length > 0 ? SourceClsid
        : Verb.Length > 0 && !Verb.StartsWith('{') ? Verb
        : Text;
}

/// <summary>
/// Runs <see cref="ShellMenuProbe"/> in a child process and merges the result with the registry
/// scan, so every item in a real menu can be traced to the thing that produced it.
///
/// The child process matters: probing loads third-party shell extensions in-process, and one that
/// faults would otherwise kill the app.
/// </summary>
public static class MenuInspector
{
    /// <summary>Runs the probe out of process. Returns the parsed result, or a result carrying an error.</summary>
    public static InspectionResult Inspect(string path, TimeSpan timeout)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return new InspectionResult { Path = path, Error = "Cannot locate the running executable." };

        var outputPath = Path.Combine(Path.GetTempPath(), $"cbg-probe-{Guid.NewGuid():N}.json");

        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--probe");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add(outputPath);

            using var process = Process.Start(psi);
            if (process is null)
                return new InspectionResult { Path = path, Error = "Could not start the probe process." };

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                return new InspectionResult
                {
                    Path = path,
                    Error = "A shell extension hung while building the menu, so the probe was stopped.",
                };
            }

            if (!File.Exists(outputPath))
                return new InspectionResult
                {
                    Path = path,
                    Error = $"The probe exited with code {process.ExitCode} without producing a result. " +
                            "That usually means a shell extension crashed while being loaded.",
                };

            var json = File.ReadAllText(outputPath);
            return JsonSerializer.Deserialize<InspectionResult>(json)
                   ?? new InspectionResult { Path = path, Error = "The probe produced an unreadable result." };
        }
        catch (Exception ex)
        {
            return new InspectionResult { Path = path, Error = ex.Message };
        }
        finally
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch (IOException) { /* temp file */ }
        }
    }

    /// <summary>The child-process side: probe, attribute, and write JSON. Never throws to the caller.</summary>
    public static void RunProbe(string path, string outputPath)
    {
        var result = new InspectionResult { Path = path, IsFolder = Directory.Exists(path) };

        try
        {
            // Build the menu twice: the ordinary one, and the SHIFT+right-click one. Anything that
            // appears only in the second is an "extended" verb.
            var plain = ShellMenuProbe.ProbeShellMenu(path, extended: false);
            var probed = ShellMenuProbe.ProbeShellMenu(path, extended: true);
            MarkExtendedOnly(probed, plain);

            result.Items.AddRange(
                BuildSection(probed, BuildAttribution(path), StaticVerbIndex(path, background: false)));
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        // A folder has a second, completely separate menu: its background.
        if (result.IsFolder)
        {
            try
            {
                var plainBg = ShellMenuProbe.ProbeBackgroundMenu(path, extended: false);
                var background = ShellMenuProbe.ProbeBackgroundMenu(path, extended: true);
                MarkExtendedOnly(background, plainBg);

                result.BackgroundItems.AddRange(
                    BuildSection(background, BuildAttribution(path, background: true),
                                 StaticVerbIndex(path, background: true)));
            }
            catch (Exception ex)
            {
                result.Error = (result.Error is null ? "" : result.Error + "  ") + "Background menu: " + ex.Message;
            }
        }

        try
        {
            File.WriteAllText(outputPath, JsonSerializer.Serialize(result), new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // Nothing useful left to do; the parent reports the missing file.
        }
    }

    /// <summary>
    /// Turns probed menu items into inspected rows: attributes each one to a COM handler, a
    /// packaged app or a static registry verb, and fills in cascading submenus the shell deferred.
    /// A static cascade (SubCommands / ExtendedSubCommandsKey) reads back empty outside a real menu
    /// loop, so it is expanded straight from the registry instead.
    /// </summary>
    private static List<InspectedItem> BuildSection(List<ProbedItem> probed,
                                                    Dictionary<string, (string Name, string Clsid)> attribution,
                                                    Dictionary<string, MenuEntry> staticVerbs)
    {
        var rows = new List<InspectedItem>();

        for (var i = 0; i < probed.Count; i++)
        {
            var item = probed[i];
            var inspected = new InspectedItem
            {
                Text = item.Text,
                Verb = item.Verb,
                Depth = item.Depth,
                IsSeparator = item.IsSeparator,
                IsSubmenu = item.IsSubmenu,
                ExtendedOnly = item.ExtendedOnly,
            };

            MenuEntry? staticVerb = null;

            if (!item.IsSeparator && attribution.TryGetValue(item.Text, out var owner))
            {
                inspected.Source = owner.Name;
                inspected.SourceClsid = owner.Clsid;
            }
            else if (RegistryPaths.LooksLikeGuid(item.Verb))
            {
                // A GUID where a verb name should be means a packaged (MSIX) IExplorerCommand:
                // the "Edit with Notepad" / "Edit with Notepad++" items live only in PackagedCom.
                inspected.SourceClsid = item.Verb;
                inspected.Source = ResolvePackageName(item.Verb) is { Length: > 0 } package
                    ? $"packaged app: {package}"
                    : "packaged handler";
            }
            else if (!item.IsSeparator && staticVerbs.TryGetValue(item.Text, out staticVerb))
            {
                inspected.Source = "static verb: " + staticVerb.ClassesPath;
                inspected.SourceKeyName = staticVerb.KeyName;
                inspected.SourcePath = staticVerb.ClassesPath ?? string.Empty;
            }

            rows.Add(inspected);

            // The shell reported a submenu but handed us nothing in it. When we know the registry
            // key behind the item, its children are right there.
            var hasProbedChildren = i + 1 < probed.Count && probed[i + 1].Depth > item.Depth;
            if (item.IsSubmenu && !hasProbedChildren && staticVerb is not null)
                rows.AddRange(ExpandStaticCascade(staticVerb, item.Depth + 1, inspected.ExtendedOnly));
        }

        return rows;
    }

    /// <summary>Reads the children of a static cascading menu straight out of the registry.</summary>
    private static IEnumerable<InspectedItem> ExpandStaticCascade(MenuEntry parent, int depth, bool extendedOnly)
    {
        if (parent.Values.TryGetValue("ExtendedSubCommandsKey", out var extendedKey) && extendedKey.Length > 0)
        {
            var subPath = extendedKey + @"\shell";

            foreach (var name in RegistryPaths.EnumerateClassesSubKeys(subPath)
                                              .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                using var key = RegistryPaths.OpenMerged(subPath + "\\" + name);
                if (key is null) continue;

                yield return new InspectedItem
                {
                    Text = LabelOf(key, name),
                    Depth = depth,
                    ExtendedOnly = extendedOnly,
                    Source = "static verb: " + subPath + "\\" + name,
                    SourceKeyName = name,
                    SourcePath = subPath + "\\" + name,
                };
            }

            yield break;
        }

        // SubCommands names verbs that live once in the CommandStore.
        if (!parent.Values.TryGetValue("SubCommands", out var list) || list.Length == 0) yield break;

        foreach (var verb in list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPaths.CommandStorePath + "\\" + verb);

            yield return new InspectedItem
            {
                Text = key is null ? verb : LabelOf(key, verb),
                Depth = depth,
                ExtendedOnly = extendedOnly,
                Source = "CommandStore verb",
                SourceKeyName = verb,
            };
        }
    }

    private static string LabelOf(RegistryKey key, string fallback)
    {
        var raw = RegistryPaths.ReadString(key, "MUIVerb");
        if (raw.Length == 0) raw = RegistryPaths.ReadString(key, null);

        var resolved = Native.ResolveDisplayString(raw).Replace("&", string.Empty).Trim();
        return resolved.Length > 0 ? resolved : fallback;
    }

    /// <summary>
    /// Static verbs for the scopes that apply, indexed by the text they draw. That is how a menu
    /// item with no canonical verb — a cascade parent, for instance — is tied back to its key.
    /// </summary>
    private static Dictionary<string, MenuEntry> StaticVerbIndex(string path, bool background)
    {
        var index = new Dictionary<string, MenuEntry>(StringComparer.CurrentCultureIgnoreCase);

        var scopes = background
            ? new List<string> { @"Directory\Background", "Folder" }
            : ScopesFor(path);

        foreach (var scope in scopes)
        {
            var scene = new Scene
            {
                Id = "idx:" + scope,
                Group = "idx",
                Name = scope,
                ClassesPath = scope,
                Description = scope,
            };

            List<MenuEntry> entries;
            try { entries = Scanner.Scan(scene, loadIcons: false); }
            catch (Exception) { continue; }

            foreach (var entry in entries)
            {
                if (entry.Kind != EntryKind.StaticVerb) continue;
                if (entry.MenuText.Length > 0) index.TryAdd(entry.MenuText, entry);
            }
        }

        return index;
    }

    /// <summary>
    /// Marks the items that only appear with SHIFT held, by consuming the plain menu as a multiset.
    /// A plain HashSet is wrong here: two different handlers can contribute items with identical
    /// text (two "Open in Terminal" entries), and set membership would clear the flag on both.
    /// </summary>
    private static void MarkExtendedOnly(List<ProbedItem> extended, List<ProbedItem> plain)
    {
        var remaining = new Dictionary<(int Depth, string Text), int>();
        foreach (var item in plain)
        {
            if (item.IsSeparator) continue;
            var key = (item.Depth, item.Text);
            remaining[key] = remaining.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        foreach (var item in extended)
        {
            if (item.IsSeparator) continue;

            var key = (item.Depth, item.Text);
            if (remaining.TryGetValue(key, out var left) && left > 0)
            {
                remaining[key] = left - 1;
                item.ExtendedOnly = false;
            }
            else
            {
                item.ExtendedOnly = true;
            }
        }
    }

    /// <summary>Short name of the packaged app that registered a CLSID, if any.</summary>
    private static string ResolvePackageName(string clsid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                RegistryPaths.PackagedClassIndexPath + "\\" + clsid);
            var package = key?.GetSubKeyNames().FirstOrDefault();
            return package?.Split('_').FirstOrDefault() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Maps menu text produced by each COM handler back to that handler.</summary>
    private static Dictionary<string, (string Name, string Clsid)> BuildAttribution(string path, bool background = false)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var handler in HandlersFor(path, background))
        {
            if (handler.Clsid.Length == 0) continue;

            List<string> contributed;
            try
            {
                contributed = ShellMenuProbe.ProbeHandler(path, handler.Clsid);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var text in contributed)
            {
                var clean = text.Replace("&", string.Empty).Trim();
                if (clean.Length > 0) map.TryAdd(clean, (handler.KeyName, handler.Clsid));
            }
        }

        return map;
    }

    /// <summary>The HKCR scopes the shell consults for a given file or folder.</summary>
    private static List<string> ScopesFor(string path)
    {
        var scopes = new List<string> { "AllFilesystemObjects" };

        if (Directory.Exists(path))
        {
            scopes.AddRange(["Directory", "Folder"]);
            return scopes;
        }

        scopes.Add("*");

        var ext = Path.GetExtension(path);
        if (ext.Length > 0)
            foreach (var scene in SceneCatalog.ForExtension(ext))
                if (scene.ClassesPath is not null)
                    scopes.Add(scene.ClassesPath);

        return scopes;
    }

    /// <summary>The COM handlers the shell would consult for this path.</summary>
    private static IEnumerable<MenuEntry> HandlersFor(string path, bool background = false)
    {
        // The background menu draws from an entirely different set of scopes.
        var scopes = background
            ? new List<string> { @"Directory\Background", "Folder" }
            : ScopesFor(path);

        return HandlersInScopes(scopes);
    }

    private static IEnumerable<MenuEntry> HandlersInScopes(IEnumerable<string> scopes)
    {
        foreach (var scope in scopes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var scene = new Scene
            {
                Id = "probe:" + scope,
                Group = "probe",
                Name = scope,
                ClassesPath = scope,
                Description = scope,
            };

            List<MenuEntry> entries;
            try
            {
                entries = Scanner.Scan(scene, loadIcons: false);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var entry in entries)
                if (entry.Kind == EntryKind.ContextMenuHandler && entry.Status != EntryStatus.Disabled)
                    yield return entry;
        }
    }

    /// <summary>Renders an inspection as plain text for the CLI and the log pane.</summary>
    public static string Format(InspectionResult result)
    {
        var text = new StringBuilder();
        text.AppendLine($"Real context menu for: {result.Path}");
        text.AppendLine();

        if (result.Error is not null) text.AppendLine("ERROR: " + result.Error);

        if (result.IsFolder) text.AppendLine("-- RIGHT-CLICKING THE FOLDER --");
        AppendItems(text, result.Items);

        if (result.BackgroundItems.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("-- RIGHT-CLICKING INSIDE IT (background) --");
            AppendItems(text, result.BackgroundItems);
        }

        return text.ToString();
    }

    private static void AppendItems(StringBuilder text, List<InspectedItem> items)
    {
        foreach (var item in items)
        {
            var indent = new string(' ', 2 + item.Depth * 3);
            if (item.IsSeparator) { text.AppendLine(indent + "──────────"); continue; }

            var origin = item.Source.Length > 0
                ? item.Source + (item.SourceClsid.Length > 0 ? "  " + item.SourceClsid : string.Empty)
                : item.Verb.Length > 0
                    ? $"verb: {item.Verb}"
                    : "unattributed";

            var shift = item.ExtendedOnly ? "[SHIFT] " : string.Empty;
            text.AppendLine($"{indent}{shift}{item.Text,-46} {origin}");
        }
    }
}
