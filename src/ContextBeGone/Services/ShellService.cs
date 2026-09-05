using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ContextBeGone.Services;

/// <summary>Explorer lifecycle plus the Windows 11 compact-menu toggle.</summary>
public static class ShellService
{
    /// <summary>
    /// Restarts explorer.exe. Explorer caches the association array, so most changes only show up
    /// after this (or a sign-out).
    /// </summary>
    public static void RestartExplorer()
    {
        Native.NotifyAssociationsChanged();

        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception)
            {
                // A process we cannot touch (another session) is not our problem.
            }
            finally
            {
                process.Dispose();
            }
        }

        // Explorer normally relaunches itself; start it if the shell restart policy is off.
        Thread.Sleep(700);
        if (Process.GetProcessesByName("explorer").Length == 0)
        {
            try
            {
                Process.Start(new ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                { UseShellExecute = true });
            }
            catch (Exception)
            {
                // Nothing more we can do; the user can start it from Task Manager.
            }
        }
    }

    private static string ClassicMenuKeyPath =>
        $@"{RegistryPaths.ClassesSubPath}\CLSID\{RegistryPaths.Windows11MenuClsid}\InprocServer32";

    /// <summary>
    /// True when Windows 11's compact menu is suppressed, i.e. right-click opens the classic menu
    /// directly instead of requiring "Show more options".
    /// </summary>
    public static bool IsClassicMenuForced()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ClassicMenuKeyPath);
        if (key is null) return false;

        // The trick is an InprocServer32 key whose default value is present but empty, so the shell
        // fails to load the command-bar extension and falls back to the classic menu.
        var value = key.GetValue(null, null);
        return value is string text && text.Length == 0;
    }

    /// <summary>Enables or disables the classic-menu-by-default tweak for the current user.</summary>
    public static OperationResult SetClassicMenuForced(bool forced)
    {
        var display = $@"HKEY_CURRENT_USER\{ClassicMenuKeyPath}";
        var result = new OperationResult
        {
            Success = true,
            Summary = forced
                ? "Windows 11 compact menu suppressed — right-click now opens the classic menu"
                : "Windows 11 compact menu restored",
            BackupFile = BackupService.Snapshot("win11-classic-menu",
                $@"HKEY_CURRENT_USER\{RegistryPaths.ClassesSubPath}\CLSID\{RegistryPaths.Windows11MenuClsid}"),
        };

        if (forced)
        {
            using var key = Registry.CurrentUser.CreateSubKey(ClassicMenuKeyPath, writable: true);
            key.SetValue(string.Empty, string.Empty, RegistryValueKind.String);
            result.Operations.Add($"{display}  →  set (Default) = \"\" (REG_SZ)");
        }
        else
        {
            using var clsidParent = Registry.CurrentUser.OpenSubKey(
                $@"{RegistryPaths.ClassesSubPath}\CLSID", writable: true);
            clsidParent?.DeleteSubKeyTree(RegistryPaths.Windows11MenuClsid, throwOnMissingSubKey: false);
            result.Operations.Add(
                $@"deleted HKEY_CURRENT_USER\{RegistryPaths.ClassesSubPath}\CLSID\{RegistryPaths.Windows11MenuClsid}");
        }

        result.Operations.Add("Restart Explorer for this to take effect.");
        BackupService.MarkChanged();
        BackupService.Log(result.Summary);
        return result;
    }

    /// <summary>
    /// When the running Explorer was started. Shell extensions are only re-read when they are
    /// loaded, so a change made after this time is not yet live in the menu you actually see.
    /// </summary>
    public static DateTime? ExplorerStartTime()
    {
        DateTime? earliest = null;
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                if (earliest is null || process.StartTime < earliest) earliest = process.StartTime;
            }
            catch (Exception)
            {
                // Another session's Explorer may deny access; ignore it.
            }
            finally
            {
                process.Dispose();
            }
        }
        return earliest;
    }

    /// <summary>True when we changed something the running Explorer has not picked up.</summary>
    public static bool HasPendingRestart()
    {
        var lastChange = BackupService.LastChangeUtc();
        if (lastChange is null) return false;

        var started = ExplorerStartTime();
        return started is not null && lastChange.Value.ToLocalTime() > started.Value;
    }

    public static void OpenInRegedit(string registryPath)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", writable: true))
                key.SetValue("LastKey", registryPath, RegistryValueKind.String);

            Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // regedit may be blocked by policy; the path is still shown in the UI for copying.
        }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing actionable.
        }
    }
}
