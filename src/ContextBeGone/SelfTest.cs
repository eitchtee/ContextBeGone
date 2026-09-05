using System.IO;
using System.Text;
using ContextBeGone.Models;
using ContextBeGone.Services;

namespace ContextBeGone;

/// <summary>
/// Headless report used by <c>ContextBeGone.exe --report [outputFile]</c>. It scans every known
/// scene and writes what was found, which doubles as a way to verify the scanner without the UI.
/// </summary>
public static class SelfTest
{
    public static void WriteReport(string outputPath)
    {
        var text = new StringBuilder();
        text.AppendLine($"ContextBeGone scan report — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Elevated: {Elevation.IsElevated}");
        text.AppendLine($"Windows 11 classic menu forced: {ShellService.IsClassicMenuForced()}");
        text.AppendLine();

        var scenes = SceneCatalog.Fixed.Where(s => !s.IsGlobalSearch)
                                       .Concat(SceneCatalog.DiscoverSystemFileAssociations()).ToList();
        var total = 0;

        foreach (var scene in scenes)
        {
            List<MenuEntry> entries;
            try
            {
                entries = Scanner.Scan(scene, loadIcons: false);
            }
            catch (Exception ex)
            {
                text.AppendLine($"## {scene.Name}  — SCAN FAILED: {ex.Message}");
                text.AppendLine();
                continue;
            }

            total += entries.Count;
            text.AppendLine($"## {scene.Name}  [{scene.ClassesPath ?? scene.Id}]  — {entries.Count} entries");

            foreach (var entry in entries)
            {
                text.AppendLine($"   {entry.StatusText,-10} {entry.KindText,-18} {entry.HiveText,-11} {entry.DisplayName}");
                if (entry.Target.Length > 0)
                    text.AppendLine($"              → {Truncate(entry.Target, 150)}");
                if (entry.DisableMarkers.Count > 0)
                    text.AppendLine($"              hidden by: {string.Join(", ", entry.DisableMarkers)}");
            }

            text.AppendLine();
        }

        text.AppendLine($"TOTAL: {total} entries across {scenes.Count} scenes.");
        File.WriteAllText(outputPath, text.ToString(), new UTF8Encoding(false));
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// <c>--toggle &lt;classesPath&gt; &lt;keyName&gt; &lt;on|off&gt; [inplace]</c>, e.g.
    /// <c>--toggle Directory\shell CbgTest off</c>. Writes the outcome to <paramref name="outputPath"/>.
    /// </summary>
    public static void Toggle(string classesPath, string keyName, bool enable, bool inPlace, string outputPath)
    {
        var text = new StringBuilder();
        try
        {
            var scene = new Scene
            {
                Id = "cli", Group = "cli", Name = classesPath,
                Description = "invoked from the command line", ClassesPath = TrimContainer(classesPath),
            };

            var entry = Scanner.Scan(scene, loadIcons: false).FirstOrDefault(
                x => string.Equals(x.KeyName, keyName, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                text.AppendLine($"NOT FOUND: {keyName} under {classesPath}");
            }
            else
            {
                text.AppendLine($"before: {entry.StatusText} ({entry.HiveText})");
                var result = Mutator.SetEnabled(entry, enable,
                    inPlace ? WriteStrategy.InPlace : WriteStrategy.UserOverlay);

                text.AppendLine(result.Success ? "OK: " + result.Summary : "FAILED: " + result.Summary);
                foreach (var op in result.Operations) text.AppendLine("  " + op);

                var after = Scanner.Scan(scene, loadIcons: false).FirstOrDefault(
                    x => string.Equals(x.KeyName, keyName, StringComparison.OrdinalIgnoreCase));
                text.AppendLine($"after: {after?.StatusText ?? "(gone)"} ({after?.HiveText})");
            }
        }
        catch (Exception ex)
        {
            text.AppendLine("EXCEPTION: " + ex);
        }

        File.WriteAllText(outputPath, text.ToString(), new UTF8Encoding(false));
    }

    /// <summary><c>--search &lt;term&gt;</c>: sweeps every scope and prints ranked matches with timings.</summary>
    public static void Search(string query, string outputPath)
    {
        var text = new StringBuilder();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var scenes = SearchService.AllScenes();
        var enumerated = sw.Elapsed;

        var all = SearchService.ScanEverything(null, CancellationToken.None);
        var swept = sw.Elapsed;

        var matches = SearchService.Filter(all, query, iconBudget: 0);
        sw.Stop();

        text.AppendLine($"query           : {query}");
        text.AppendLine($"scopes          : {scenes.Count}  (enumerated in {enumerated.TotalMilliseconds:0} ms)");
        text.AppendLine($"entries indexed : {all.Count}  (swept in {(swept - enumerated).TotalMilliseconds:0} ms)");
        text.AppendLine($"matches         : {matches.Count}  (ranked in {(sw.Elapsed - swept).TotalMilliseconds:0} ms)");
        text.AppendLine($"total           : {sw.Elapsed.TotalMilliseconds:0} ms");
        text.AppendLine();

        foreach (var m in matches.Take(40))
        {
            text.AppendLine($"{m.StatusText,-10} {m.Scene.Name,-32} {m.KindText,-18} {m.DisplayName}");
            if (m.Target.Length > 0) text.AppendLine($"           -> {Truncate(m.Target, 140)}");
        }

        if (matches.Count > 40) text.AppendLine($"... and {matches.Count - 40} more");

        File.WriteAllText(outputPath, text.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Accepts either "Directory" or "Directory\shell"; the scanner appends the containers itself.</summary>
    private static string TrimContainer(string classesPath) =>
        classesPath.EndsWith(@"\shell", StringComparison.OrdinalIgnoreCase)
            ? classesPath[..^6]
            : classesPath;
}
