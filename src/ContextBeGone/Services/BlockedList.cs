using Microsoft.Win32;

namespace ContextBeGone.Services;

/// <summary>
/// The shell's block list for in-process extension handlers:
/// <c>...\CurrentVersion\Shell Extensions\Blocked</c>. A value whose *name* is the CLSID stops the
/// shell from loading that handler. The machine copy needs elevation; the per-user copy does not.
/// </summary>
public static class BlockedList
{
    public static bool IsBlocked(string clsid)
    {
        if (string.IsNullOrWhiteSpace(clsid)) return false;

        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(RegistryPaths.BlockedSubPath);
            if (key is null) continue;
            if (key.GetValueNames().Any(n => string.Equals(n, clsid, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>Adds the CLSID to the block list. Returns the paths actually written.</summary>
    public static List<string> Block(string clsid, string friendlyName)
    {
        var written = new List<string>();

        try
        {
            using var user = Registry.CurrentUser.CreateSubKey(RegistryPaths.BlockedSubPath, writable: true);
            user.SetValue(clsid, friendlyName, RegistryValueKind.String);
            written.Add($@"HKEY_CURRENT_USER\{RegistryPaths.BlockedSubPath}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not write the per-user block list: {ex.Message}", ex);
        }

        // Mirror to HKLM when we are elevated; some handlers are only consulted against the machine list.
        if (Elevation.IsElevated)
        {
            try
            {
                using var machine = Registry.LocalMachine.CreateSubKey(RegistryPaths.BlockedSubPath, writable: true);
                machine.SetValue(clsid, friendlyName, RegistryValueKind.String);
                written.Add($@"HKEY_LOCAL_MACHINE\{RegistryPaths.BlockedSubPath}");
            }
            catch (Exception)
            {
                // Per-user block already applied; the machine mirror is best effort.
            }
        }

        return written;
    }

    /// <summary>Removes the CLSID from both block lists. Returns the paths cleared.</summary>
    public static List<string> Unblock(string clsid)
    {
        var cleared = new List<string>();
        var stubborn = new List<string>();

        foreach (var (root, label) in new[]
                 {
                     (Registry.CurrentUser, "HKEY_CURRENT_USER"),
                     (Registry.LocalMachine, "HKEY_LOCAL_MACHINE"),
                 })
        {
            try
            {
                using var key = root.OpenSubKey(RegistryPaths.BlockedSubPath, writable: true);
                if (key is null) continue;
                var match = key.GetValueNames()
                               .FirstOrDefault(n => string.Equals(n, clsid, StringComparison.OrdinalIgnoreCase));
                if (match is null) continue;

                key.DeleteValue(match, throwOnMissingValue: false);
                cleared.Add($@"{label}\{RegistryPaths.BlockedSubPath}");
            }
            catch (UnauthorizedAccessException)
            {
                stubborn.Add(label);
            }
        }

        if (stubborn.Count > 0 && cleared.Count == 0)
            throw new UnauthorizedAccessException(
                $"The block list entry lives in {string.Join(" and ", stubborn)} and needs administrator rights to remove. " +
                "Use \"Restart as administrator\" and try again.");

        return cleared;
    }
}
