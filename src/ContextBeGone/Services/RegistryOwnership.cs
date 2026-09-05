using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace ContextBeGone.Services;

/// <summary>
/// Takes ownership of a registry key and grants the Administrators group full control.
///
/// Most built-in Windows verbs live under HKLM\Software\Classes in keys owned by TrustedInstaller,
/// where even an elevated administrator only has read access. Ownership is only needed to *remove*
/// something Windows put there; adding a hide marker never needs it, because the per-user overlay
/// covers that case.
/// </summary>
public static class RegistryOwnership
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    private const uint SE_PRIVILEGE_ENABLED = 0x0002;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) return false;

            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED },
            };

            return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                   && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Makes <paramref name="subKeyPath"/> writable by Administrators.
    /// Throws with a readable message when it cannot be done.
    /// </summary>
    public static void TakeOwnership(RegistryKey hive, string subKeyPath)
    {
        if (!Elevation.IsElevated)
            throw new UnauthorizedAccessException("Taking ownership requires administrator rights. Restart as administrator first.");

        if (!EnablePrivilege("SeTakeOwnershipPrivilege"))
            throw new UnauthorizedAccessException("Could not enable SeTakeOwnershipPrivilege for this process.");
        EnablePrivilege("SeRestorePrivilege");

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        // Step 1: claim ownership. This needs only WriteOwner access.
        using (var forOwner = hive.OpenSubKey(subKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
                                              RegistryRights.TakeOwnership)
                              ?? throw new InvalidOperationException($@"Key not found: {hive.Name}\{subKeyPath}"))
        {
            var security = forOwner.GetAccessControl(AccessControlSections.None);
            security.SetOwner(administrators);
            forOwner.SetAccessControl(security);
        }

        // Step 2: now that we own it, grant ourselves full control.
        using (var forAcl = hive.OpenSubKey(subKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
                                            RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)
                            ?? throw new InvalidOperationException($@"Key not found: {hive.Name}\{subKeyPath}"))
        {
            var security = forAcl.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new RegistryAccessRule(
                administrators,
                RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            forAcl.SetAccessControl(security);
        }

        BackupService.Log($@"took ownership of {hive.Name}\{subKeyPath} and granted Administrators full control");
    }

    /// <summary>Convenience wrapper for a path relative to the Classes root in HKLM.</summary>
    public static void TakeOwnershipOfClassesKey(string classesPath) =>
        TakeOwnership(Registry.LocalMachine, $@"{RegistryPaths.ClassesSubPath}\{classesPath}");
}
