using ContextBeGone.Models;
using Microsoft.Win32;

namespace ContextBeGone.Services;

/// <summary>
/// Helpers for working with the HKEY_CLASSES_ROOT merged view.
///
/// HKCR is not a real hive. Reading it returns the union of HKLM\Software\Classes and
/// HKCU\Software\Classes, and where a key exists in both, the per-user values win. That is what
/// makes the "user overlay" write strategy work: adding LegacyDisable under
/// HKCU\Software\Classes\Directory\shell\Foo hides a verb whose real definition lives in a
/// TrustedInstaller-owned machine key, without modifying that key and without elevation.
/// </summary>
public static class RegistryPaths
{
    public const string ClassesSubPath = @"Software\Classes";

    public const string CommandStorePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell";

    public const string BlockedSubPath =
        @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    /// <summary>Where packaged (MSIX) COM classes are indexed, including shell extensions.</summary>
    public const string PackagedClassIndexPath =
        @"SOFTWARE\Classes\PackagedCom\ClassIndex";

    public const string PackagedPackagePath =
        @"SOFTWARE\Classes\PackagedCom\Package";

    public const string ApprovedSubPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";

    /// <summary>CLSID of the Windows 11 command-bar shell extension; suppressing it restores the classic menu.</summary>
    public const string Windows11MenuClsid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    /// <summary>Subkeys of a scope key in which menu registrations live.</summary>
    public static readonly string[] MenuContainers =
    [
        "shell",
        @"shellex\ContextMenuHandlers",
        @"shellex\DragDropHandlers",
    ];

    /// <summary>Values whose mere presence hides a static verb from the menu.</summary>
    public static readonly string[] DisableValueNames =
    [
        "LegacyDisable",
        "ProgrammaticAccessOnly",
        "HideBasedOnVelocityId",
    ];

    /// <summary>Union of the subkey names of <paramref name="classesPath"/> across both real hives.</summary>
    public static IEnumerable<string> EnumerateClassesSubKeys(string classesPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var key = Registry.ClassesRoot.OpenSubKey(classesPath))
        {
            if (key is not null)
                foreach (var n in key.GetSubKeyNames()) names.Add(n);
        }
        return names;
    }

    /// <summary>Opens the merged HKCR view of a path.</summary>
    public static RegistryKey? OpenMerged(string classesPath, bool writable = false) =>
        Registry.ClassesRoot.OpenSubKey(classesPath, writable);

    /// <summary>Opens the machine-hive copy of a Classes path (HKLM\Software\Classes\...).</summary>
    public static RegistryKey? OpenMachine(string classesPath, bool writable = false) =>
        Registry.LocalMachine.OpenSubKey($@"{ClassesSubPath}\{classesPath}", writable);

    /// <summary>Opens the per-user copy of a Classes path (HKCU\Software\Classes\...).</summary>
    public static RegistryKey? OpenUser(string classesPath, bool writable = false) =>
        Registry.CurrentUser.OpenSubKey($@"{ClassesSubPath}\{classesPath}", writable);

    /// <summary>Creates (or opens) the per-user overlay key for a Classes path.</summary>
    public static RegistryKey CreateUser(string classesPath) =>
        Registry.CurrentUser.CreateSubKey($@"{ClassesSubPath}\{classesPath}", writable: true);

    public static bool ExistsInMachine(string classesPath)
    {
        using var key = OpenMachine(classesPath);
        return key is not null;
    }

    public static bool ExistsInUser(string classesPath)
    {
        using var key = OpenUser(classesPath);
        return key is not null;
    }

    /// <summary>Registry-editor style path, for display and for .reg export.</summary>
    public static string ToDisplayPath(WriteStrategy strategy, string classesPath) => strategy switch
    {
        WriteStrategy.UserOverlay => $@"HKEY_CURRENT_USER\{ClassesSubPath}\{classesPath}",
        _ => $@"HKEY_CLASSES_ROOT\{classesPath}",
    };

    public static string MachineDisplayPath(string classesPath) =>
        $@"HKEY_LOCAL_MACHINE\{ClassesSubPath}\{classesPath}";

    public static string UserDisplayPath(string classesPath) =>
        $@"HKEY_CURRENT_USER\{ClassesSubPath}\{classesPath}";

    /// <summary>Reads a string value from the merged view, returning "" when absent.</summary>
    public static string ReadString(RegistryKey? key, string? name)
    {
        try
        {
            return key?.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString()
                   ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Resolves the server path registered for a CLSID, checking both the 64-bit and 32-bit views.</summary>
    public static string ResolveComServerPath(string clsid)
    {
        if (string.IsNullOrWhiteSpace(clsid)) return string.Empty;

        foreach (var server in new[] { "InprocServer32", "LocalServer32", "InprocHandler32" })
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\{server}");
            var path = ReadString(key, null);
            if (path.Length > 0) return Environment.ExpandEnvironmentVariables(path.Trim('"'));

            using var wow = Registry.ClassesRoot.OpenSubKey($@"WOW6432Node\CLSID\{clsid}\{server}");
            var wowPath = ReadString(wow, null);
            if (wowPath.Length > 0) return Environment.ExpandEnvironmentVariables(wowPath.Trim('"'));
        }

        return string.Empty;
    }

    /// <summary>Friendly name registered for a CLSID, if any.</summary>
    public static string ResolveComServerName(string clsid)
    {
        if (string.IsNullOrWhiteSpace(clsid)) return string.Empty;
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}");
        return Native.ResolveDisplayString(ReadString(key, null));
    }

    public static bool LooksLikeGuid(string value) =>
        value.Length == 38 && value[0] == '{' && value[^1] == '}' && Guid.TryParse(value, out _);
}
