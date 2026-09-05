using System.Diagnostics;
using ContextBeGone.Models;

namespace ContextBeGone.Services;

/// <summary>Progress of a full-registry sweep.</summary>
public readonly record struct ScanProgress(int Done, int Total, string Label);

/// <summary>
/// Scans every scope in one pass so a single term can be matched against the whole system —
/// "notepad" finds the Notepad++ handler, the Applications\notepad.exe verbs, and every verb
/// whose command line invokes notepad, wherever they are registered.
///
/// The sweep is cached for the life of the window because it touches tens of thousands of keys;
/// Rescan drops the cache.
/// </summary>
public static class SearchService
{
    private static List<MenuEntry>? _cache;
    private static TimeSpan _lastDuration;

    public static bool HasCache => _cache is not null;
    public static TimeSpan LastDuration => _lastDuration;
    public static int CachedCount => _cache?.Count ?? 0;

    public static void Invalidate() => _cache = null;

    /// <summary>Every scope worth scanning, fixed plus discovered.</summary>
    public static List<Scene> AllScenes() =>
        SceneCatalog.Fixed
                    .Where(s => !s.IsGlobalSearch)
                    .Concat(SceneCatalog.DiscoverSystemFileAssociations())
                    .Concat(SceneCatalog.DiscoverProgIds())
                    .ToList();

    /// <summary>
    /// Scans everything, or returns the cached result. Icons are skipped here — at several thousand
    /// entries that would dominate the cost — and are filled in for matches only.
    /// </summary>
    public static List<MenuEntry> ScanEverything(IProgress<ScanProgress>? progress, CancellationToken token)
    {
        if (_cache is not null) return _cache;

        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new ScanProgress(0, 0, "enumerating the registry…"));

        var scenes = AllScenes();
        var all = new List<MenuEntry>(8192);

        for (var i = 0; i < scenes.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            // Reporting every scope would flood the dispatcher; a sample is enough to show life.
            if (i % 25 == 0 || i == scenes.Count - 1)
                progress?.Report(new ScanProgress(i + 1, scenes.Count, scenes[i].Name));

            try
            {
                all.AddRange(Scanner.Scan(scenes[i], loadIcons: false));
            }
            catch (Exception)
            {
                // A single unreadable scope must not abort the sweep.
            }
        }

        stopwatch.Stop();
        _lastDuration = stopwatch.Elapsed;
        _cache = all;
        BackupService.Log($"full sweep: {all.Count} entries from {scenes.Count} scopes in {stopwatch.ElapsedMilliseconds} ms");
        return all;
    }

    /// <summary>
    /// Ranks matches so the most direct ones come first: name, then key, then what it runs,
    /// then where it lives. Every term must match somewhere (AND), so "notepad zip" narrows.
    /// </summary>
    public static List<MenuEntry> Filter(IEnumerable<MenuEntry> entries, string query, int iconBudget = 400)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return new List<MenuEntry>();

        var matches = new List<(MenuEntry Entry, int Score)>();

        foreach (var entry in entries)
        {
            var total = 0;
            var matchedAll = true;

            foreach (var term in terms)
            {
                var score = Score(entry, term);
                if (score == 0) { matchedAll = false; break; }
                total += score;
            }

            if (matchedAll) matches.Add((entry, total));
        }

        var ordered = matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(m => m.Entry)
            .ToList();

        foreach (var entry in ordered.Take(iconBudget)) Scanner.EnsureIcon(entry);

        return ordered;
    }

    private static int Score(MenuEntry entry, string term)
    {
        if (Has(entry.DisplayName, term)) return 100;
        if (Has(entry.KeyName, term)) return 80;
        if (Has(entry.Command, term)) return 60;
        if (Has(entry.HandlerPath, term)) return 50;
        if (Has(entry.Scene.Name, term)) return 30;
        if (Has(entry.ClassesPath, term)) return 20;
        if (Has(entry.Clsid, term)) return 20;
        if (Has(entry.CommandMechanism, term)) return 10;
        if (Has(entry.SubCommands, term)) return 10;
        return 0;
    }

    private static bool Has(string? haystack, string term) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(term, StringComparison.OrdinalIgnoreCase);
}
