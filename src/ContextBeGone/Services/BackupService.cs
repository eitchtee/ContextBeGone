using System.Diagnostics;
using System.IO;
using System.Text;

namespace ContextBeGone.Services;

/// <summary>
/// Exports a .reg snapshot of a key before it is modified, so any change can be undone by
/// double-clicking a file. Snapshots are taken with reg.exe, which handles every value type.
/// </summary>
public static class BackupService
{
    /// <summary>
    /// Everything the app writes lives here. It sits next to the executable so the whole thing is
    /// portable: copy the folder to a stick and the backups and journal travel with it. If the
    /// executable is somewhere unwritable (Program Files, a read-only share) it falls back to
    /// LocalAppData rather than failing.
    /// </summary>
    public static string BackupRoot { get; } = ResolveDataFolder();

    public static string JournalPath { get; } = Path.Combine(BackupRoot, "journal.log");

    /// <summary>True when the data folder had to fall back out of the executable's folder.</summary>
    public static bool IsPortable { get; private set; }

    private static string ResolveDataFolder()
    {
        var exeFolder = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeFolder))
        {
            var candidate = Path.Combine(exeFolder, "ContextBeGone-Data");
            if (IsWritable(candidate))
            {
                IsPortable = true;
                MigrateLegacyData(candidate);
                return candidate;
            }
        }

        IsPortable = false;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ContextBeGone", "Backups");
    }

    private static bool IsWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Moves backups written by an older, non-portable build so history is not orphaned.</summary>
    private static void MigrateLegacyData(string destination)
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ContextBeGone", "Backups");

            if (!Directory.Exists(legacy) || Path.GetFullPath(legacy) == Path.GetFullPath(destination)) return;

            foreach (var file in Directory.GetFiles(legacy))
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                if (File.Exists(target)) continue;
                File.Move(file, target);
            }

            if (Directory.GetFiles(legacy).Length == 0 && Directory.GetDirectories(legacy).Length == 0)
            {
                Directory.Delete(legacy);

                // Leave nothing behind in LocalAppData once everything has moved.
                var parent = Path.GetDirectoryName(legacy);
                if (parent is not null && Directory.Exists(parent)
                    && Directory.GetFileSystemEntries(parent).Length == 0)
                    Directory.Delete(parent);
            }
        }
        catch (Exception)
        {
            // Migration is a courtesy; a failure must not stop the app from starting.
        }
    }

    /// <summary>
    /// Exports each of <paramref name="registryPaths"/> that exists into one timestamped .reg file.
    /// Returns the file path, or null when nothing existed to back up.
    /// </summary>
    public static string? Snapshot(string label, params string[] registryPaths)
    {
        Directory.CreateDirectory(BackupRoot);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var safeLabel = Sanitize(label);
        var target = Path.Combine(BackupRoot, $"{stamp}_{safeLabel}.reg");

        var chunks = new List<string>();
        foreach (var path in registryPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var exported = ExportKey(path);
            if (exported is not null) chunks.Add(exported);
        }

        if (chunks.Count == 0) return null;

        var body = new StringBuilder();
        body.AppendLine("Windows Registry Editor Version 5.00");
        body.AppendLine();
        body.AppendLine($"; ContextBeGone backup — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        body.AppendLine($"; {label}");
        body.AppendLine("; Double-click this file to restore the keys exactly as they were.");
        body.AppendLine();
        foreach (var chunk in chunks) body.AppendLine(chunk.Trim());

        File.WriteAllText(target, body.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return target;
    }

    /// <summary>Runs <c>reg export</c> and returns the body without the file header, or null if the key is absent.</summary>
    private static string? ExportKey(string registryPath)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"cbg-{Guid.NewGuid():N}.reg");
        try
        {
            var psi = new ProcessStartInfo("reg.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("export");
            psi.ArgumentList.Add(registryPath);
            psi.ArgumentList.Add(temp);
            psi.ArgumentList.Add("/y");

            using var process = Process.Start(psi);
            if (process is null) return null;
            process.WaitForExit(15000);
            if (process.ExitCode != 0 || !File.Exists(temp)) return null;

            var text = File.ReadAllText(temp, Encoding.Unicode);
            var firstBracket = text.IndexOf('[');
            return firstBracket >= 0 ? text[firstBracket..] : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* temp file, ignore */ }
        }
    }

    /// <summary>Timestamp of the last change we made, so we can tell whether Explorer predates it.</summary>
    public static string LastChangePath { get; } = Path.Combine(BackupRoot, "last-change.txt");

    /// <summary>Records that something was changed that Explorer will not pick up until it restarts.</summary>
    public static void MarkChanged()
    {
        try
        {
            Directory.CreateDirectory(BackupRoot);
            File.WriteAllText(LastChangePath, DateTime.UtcNow.ToString("O"));
        }
        catch (IOException)
        {
            // Only affects the reminder banner.
        }
    }

    /// <summary>When we last changed something, or null if we never have.</summary>
    public static DateTime? LastChangeUtc()
    {
        try
        {
            if (!File.Exists(LastChangePath)) return null;
            return DateTime.TryParse(File.ReadAllText(LastChangePath), null,
                       System.Globalization.DateTimeStyles.RoundtripKind, out var stamp)
                   ? stamp
                   : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(BackupRoot);
            File.AppendAllText(JournalPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Journalling is a convenience; never let it break an operation.
        }
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ' ' or '\\' or '*' ? '-' : c);
        var name = new string(chars.ToArray()).Trim('-');
        return name.Length > 60 ? name[..60] : name.Length == 0 ? "change" : name;
    }
}
