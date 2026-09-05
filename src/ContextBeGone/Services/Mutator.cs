using ContextBeGone.Models;
using Microsoft.Win32;

namespace ContextBeGone.Services;

/// <summary>Result of a registry change, with the exact operations performed for the log pane.</summary>
public sealed class OperationResult
{
    public bool Success { get; init; }
    public string Summary { get; init; } = string.Empty;
    public List<string> Operations { get; } = new();
    public string? BackupFile { get; init; }
}

/// <summary>The values a user can edit on a static verb.</summary>
public sealed class VerbEdits
{
    public string? MuiVerb { get; set; }
    public string? Icon { get; set; }
    public string? Command { get; set; }

    /// <summary>"", "Top" or "Bottom".</summary>
    public string? Position { get; set; }

    public bool? Extended { get; set; }
    public bool? NoWorkingDirectory { get; set; }
    public bool? SeparatorBefore { get; set; }
    public bool? SeparatorAfter { get; set; }
    public bool? NeverDefault { get; set; }
    public bool? HasLuaShield { get; set; }
}

/// <summary>Applies enable/disable/edit/delete/create operations to context menu registrations.</summary>
public static class Mutator
{
    // Both are documented as hiding a verb; writing both covers shell paths that only honour one.
    private static readonly string[] HideMarkers = ["LegacyDisable", "ProgrammaticAccessOnly"];

    // ─────────────────────────────────────────────────────────────── enable / disable

    public static OperationResult SetEnabled(MenuEntry entry, bool enabled, WriteStrategy strategy) =>
        entry.Kind switch
        {
            EntryKind.ContextMenuHandler or EntryKind.DragDropHandler or EntryKind.PackagedHandler
                => SetHandlerEnabled(entry, enabled),
            EntryKind.ShellNew => SetShellNewEnabled(entry, enabled),
            _ => SetVerbEnabled(entry, enabled, strategy),
        };

    private static OperationResult SetVerbEnabled(MenuEntry entry, bool enabled, WriteStrategy strategy)
    {
        if (entry.Kind == EntryKind.CommandStoreVerb) strategy = WriteStrategy.InPlace;

        var result = new OperationResult
        {
            Success = true,
            Summary = enabled ? $"Enabled \"{entry.DisplayName}\"" : $"Disabled \"{entry.DisplayName}\"",
            BackupFile = Snapshot(entry, enabled ? "enable" : "disable"),
        };

        if (enabled) EnableVerb(entry, strategy, result);
        else DisableVerb(entry, strategy, result);

        Finish(result);
        return result;
    }

    private static void DisableVerb(MenuEntry entry, WriteStrategy strategy, OperationResult result)
    {
        using var key = OpenForWrite(entry, strategy, create: true);

        foreach (var marker in HideMarkers)
        {
            key.SetValue(marker, string.Empty, RegistryValueKind.String);
            result.Operations.Add($"{key.Name}\\  →  set \"{marker}\" = \"\" (REG_SZ)");
        }

        // Verbs Windows gates behind a feature id are hidden by flipping Show→Hide. Only safe in
        // place: in an overlay the machine key would still carry ShowBasedOnVelocityId.
        if (strategy == WriteStrategy.InPlace && key.GetValue("ShowBasedOnVelocityId") is int velocityId)
        {
            key.SetValue("HideBasedOnVelocityId", velocityId, RegistryValueKind.DWord);
            key.DeleteValue("ShowBasedOnVelocityId", throwOnMissingValue: false);
            result.Operations.Add($"{key.Name}\\  →  renamed ShowBasedOnVelocityId to HideBasedOnVelocityId (0x{velocityId:X})");
        }
    }

    private static void EnableVerb(MenuEntry entry, WriteStrategy strategy, OperationResult result)
    {
        var path = entry.ClassesPath;
        var stubborn = new List<string>();

        foreach (var (hiveKey, label) in EnumerateWritableCopies(entry, strategy))
        {
            using var key = hiveKey;
            if (key is null) continue;

            foreach (var marker in RegistryPaths.DisableValueNames)
            {
                if (key.GetValue(marker) is null) continue;

                if (marker == "HideBasedOnVelocityId" && key.GetValue(marker) is int velocityId)
                {
                    key.SetValue("ShowBasedOnVelocityId", velocityId, RegistryValueKind.DWord);
                    result.Operations.Add($"{label}  →  restored ShowBasedOnVelocityId (0x{velocityId:X})");
                }

                key.DeleteValue(marker, throwOnMissingValue: false);
                result.Operations.Add($"{label}  →  deleted \"{marker}\"");
            }
        }

        // If we created a bare overlay purely to hide the verb, remove it so nothing is left behind.
        if (path is not null) TryPruneEmptyOverlay(path, result);

        // Re-read the merged view: a marker still visible means it lives in a key we could not write.
        if (path is not null)
        {
            using var merged = RegistryPaths.OpenMerged(path);
            var remaining = RegistryPaths.DisableValueNames
                                         .Where(m => merged?.GetValue(m) is not null)
                                         .ToList();
            if (remaining.Count > 0)
                stubborn.Add($"{string.Join(", ", remaining)} still present in {RegistryPaths.MachineDisplayPath(path)}");
        }

        if (stubborn.Count > 0)
            result.Operations.Add(
                "NOT FULLY APPLIED: " + string.Join("; ", stubborn) +
                ". That key is owned by the system — use \"Restart as administrator\", switch the write mode to " +
                "\"In place\", and if it still fails use \"Take ownership\".");
    }

    private static OperationResult SetHandlerEnabled(MenuEntry entry, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(entry.Clsid))
            return new OperationResult { Success = false, Summary = "This handler has no CLSID, so it cannot be blocked." };

        var result = new OperationResult
        {
            Success = true,
            Summary = enabled ? $"Unblocked handler \"{entry.KeyName}\"" : $"Blocked handler \"{entry.KeyName}\"",
            BackupFile = BackupService.Snapshot(
                $"blocked-{entry.KeyName}",
                $@"HKEY_CURRENT_USER\{RegistryPaths.BlockedSubPath}",
                $@"HKEY_LOCAL_MACHINE\{RegistryPaths.BlockedSubPath}"),
        };

        if (enabled)
        {
            foreach (var path in BlockedList.Unblock(entry.Clsid))
                result.Operations.Add($"{path}  →  deleted value \"{entry.Clsid}\"");
        }
        else
        {
            var name = $"{entry.KeyName} (blocked by ContextBeGone)";
            foreach (var path in BlockedList.Block(entry.Clsid, name))
                result.Operations.Add($"{path}  →  set \"{entry.Clsid}\" = \"{name}\"");

            if (!Elevation.IsElevated)
                result.Operations.Add(
                    "Only the per-user block list was written. If the handler survives, restart as administrator " +
                    "so the machine-wide list is written too.");
        }

        Finish(result);
        return result;
    }

    private static OperationResult SetShellNewEnabled(MenuEntry entry, bool enabled)
    {
        var path = entry.ClassesPath
                   ?? throw new InvalidOperationException("ShellNew entry without a path.");

        var parent = path[..path.LastIndexOf('\\')];
        var from = path[(path.LastIndexOf('\\') + 1)..];
        var to = enabled ? "ShellNew" : "ShellNew-";

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return new OperationResult { Success = true, Summary = "Already in the requested state." };

        var result = new OperationResult
        {
            Success = true,
            Summary = enabled ? $"Enabled New entry for {entry.KeyName}" : $"Disabled New entry for {entry.KeyName}",
            BackupFile = Snapshot(entry, enabled ? "shellnew-enable" : "shellnew-disable"),
        };

        // ShellNew is toggled by renaming the key; the registry has no rename, so copy then delete.
        var (root, rootLabel) = RegistryPaths.ExistsInUser(path)
            ? (Registry.CurrentUser, "HKEY_CURRENT_USER")
            : (Registry.LocalMachine, "HKEY_LOCAL_MACHINE");

        var basePath = $@"{RegistryPaths.ClassesSubPath}\{parent}";
        using var parentKey = root.OpenSubKey(basePath, writable: true)
            ?? throw new UnauthorizedAccessException($@"Cannot open {rootLabel}\{basePath} for writing.");

        using (var source = parentKey.OpenSubKey(from))
        using (var destination = parentKey.CreateSubKey(to, writable: true))
        {
            if (source is null) throw new InvalidOperationException($"{from} disappeared.");
            CopyKey(source, destination);
        }

        parentKey.DeleteSubKeyTree(from, throwOnMissingSubKey: false);
        result.Operations.Add($@"{rootLabel}\{basePath}\  →  renamed ""{from}"" to ""{to}""");

        Finish(result);
        return result;
    }

    // ─────────────────────────────────────────────────────────────── editing

    public static OperationResult ApplyEdits(MenuEntry entry, VerbEdits edits, WriteStrategy strategy)
    {
        if (entry.Kind is EntryKind.ContextMenuHandler or EntryKind.DragDropHandler or EntryKind.PackagedHandler)
            return new OperationResult
            {
                Success = false,
                Summary = "COM handlers build their own menu items in code — there is nothing in the registry to edit. " +
                          "You can only enable or disable them.",
            };

        if (entry.Kind == EntryKind.CommandStoreVerb) strategy = WriteStrategy.InPlace;

        var result = new OperationResult
        {
            Success = true,
            Summary = $"Updated \"{entry.KeyName}\"",
            BackupFile = Snapshot(entry, "edit"),
        };

        using var key = OpenForWrite(entry, strategy, create: true);

        SetOrDelete(key, "MUIVerb", edits.MuiVerb, result);
        SetOrDelete(key, "Icon", edits.Icon, result);
        SetOrDelete(key, "Position", NormalisePosition(edits.Position), result);

        SetFlag(key, "Extended", edits.Extended, result);
        SetFlag(key, "NoWorkingDirectory", edits.NoWorkingDirectory, result);
        SetFlag(key, "SeparatorBefore", edits.SeparatorBefore, result);
        SetFlag(key, "SeparatorAfter", edits.SeparatorAfter, result);
        SetFlag(key, "NeverDefault", edits.NeverDefault, result);
        SetFlag(key, "HasLUAShield", edits.HasLuaShield, result);

        if (edits.Command is not null && edits.Command != entry.Command)
        {
            using var command = key.CreateSubKey("command", writable: true);
            if (edits.Command.Length == 0)
            {
                command.DeleteValue(string.Empty, throwOnMissingValue: false);
                result.Operations.Add($"{key.Name}\\command  →  cleared the default value");
            }
            else
            {
                // REG_EXPAND_SZ so %SystemRoot% style paths keep working.
                var kind = edits.Command.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String;
                command.SetValue(string.Empty, edits.Command, kind);
                result.Operations.Add($"{key.Name}\\command  →  set (Default) = \"{edits.Command}\" ({kind})");
            }
        }

        if (result.Operations.Count == 0) result.Operations.Add("No changes — the values already matched.");

        Finish(result);
        return result;
    }

    // ─────────────────────────────────────────────────────────────── create / delete

    /// <summary>Creates a new static verb under the given scene, in HKCU\Software\Classes.</summary>
    public static OperationResult CreateVerb(Scene scene, string keyName, string label, string command, string icon,
                                             string position, bool extended)
    {
        if (scene.ClassesPath is null)
            return new OperationResult { Success = false, Summary = "New entries can only be added to a registry scope." };

        keyName = keyName.Trim();
        if (keyName.Length == 0 || keyName.Contains('\\'))
            return new OperationResult { Success = false, Summary = "The key name must be non-empty and must not contain a backslash." };

        var path = $@"{scene.ClassesPath}\shell\{keyName}";
        if (RegistryPaths.OpenMerged(path) is { } existing)
        {
            existing.Dispose();
            return new OperationResult { Success = false, Summary = $"A verb named \"{keyName}\" already exists here." };
        }

        var result = new OperationResult
        {
            Success = true,
            Summary = $"Created \"{keyName}\" under {scene.Name}",
            BackupFile = null,
        };

        using var key = RegistryPaths.CreateUser(path);
        result.Operations.Add($"created {key.Name}");

        if (label.Length > 0)
        {
            key.SetValue("MUIVerb", label, RegistryValueKind.String);
            result.Operations.Add($"  set MUIVerb = \"{label}\"");
        }
        if (icon.Length > 0)
        {
            key.SetValue("Icon", icon, RegistryValueKind.String);
            result.Operations.Add($"  set Icon = \"{icon}\"");
        }
        var pos = NormalisePosition(position);
        if (!string.IsNullOrEmpty(pos))
        {
            key.SetValue("Position", pos, RegistryValueKind.String);
            result.Operations.Add($"  set Position = \"{pos}\"");
        }
        if (extended)
        {
            key.SetValue("Extended", string.Empty, RegistryValueKind.String);
            result.Operations.Add("  set Extended = \"\"");
        }

        using var commandKey = key.CreateSubKey("command", writable: true);
        var kind = command.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String;
        commandKey.SetValue(string.Empty, command, kind);
        result.Operations.Add($"  set command\\(Default) = \"{command}\" ({kind})");

        Finish(result);
        return result;
    }

    /// <summary>Deletes the entry's key. Removes the user overlay first; the machine copy needs rights.</summary>
    public static OperationResult Delete(MenuEntry entry, bool includeMachineCopy)
    {
        var path = entry.ClassesPath;
        if (path is null)
            return new OperationResult { Success = false, Summary = "This entry has no deletable Classes key." };

        var result = new OperationResult
        {
            Success = true,
            Summary = $"Deleted \"{entry.DisplayName}\"",
            BackupFile = Snapshot(entry, "delete"),
        };

        var parent = path[..path.LastIndexOf('\\')];
        var leaf = path[(path.LastIndexOf('\\') + 1)..];

        if (RegistryPaths.ExistsInUser(path))
        {
            using var userParent = Registry.CurrentUser.OpenSubKey($@"{RegistryPaths.ClassesSubPath}\{parent}", writable: true);
            userParent?.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
            result.Operations.Add($"deleted {RegistryPaths.UserDisplayPath(path)}");
        }

        if (includeMachineCopy && RegistryPaths.ExistsInMachine(path))
        {
            using var machineParent = Registry.LocalMachine.OpenSubKey($@"{RegistryPaths.ClassesSubPath}\{parent}", writable: true)
                ?? throw new UnauthorizedAccessException(
                    $"Cannot open {RegistryPaths.MachineDisplayPath(parent)} for writing. Restart as administrator, " +
                    "and use \"Take ownership\" if the key belongs to TrustedInstaller.");
            machineParent.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
            result.Operations.Add($"deleted {RegistryPaths.MachineDisplayPath(path)}");
        }
        else if (RegistryPaths.ExistsInMachine(path))
        {
            result.Operations.Add(
                $"left {RegistryPaths.MachineDisplayPath(path)} in place — the machine copy was not selected for deletion, " +
                "so the entry will still appear. Disable it instead, or re-run with the machine copy included.");
        }

        Finish(result);
        return result;
    }

    /// <summary>Removes any per-user overlay, restoring the entry to exactly what Windows shipped.</summary>
    public static OperationResult RemoveOverlay(MenuEntry entry)
    {
        var path = entry.ClassesPath;
        if (path is null || !RegistryPaths.ExistsInUser(path))
            return new OperationResult { Success = true, Summary = "There is no per-user overlay for this entry." };

        var result = new OperationResult
        {
            Success = true,
            Summary = $"Removed the per-user overlay for \"{entry.KeyName}\"",
            BackupFile = BackupService.Snapshot($"overlay-{entry.KeyName}", RegistryPaths.UserDisplayPath(path)),
        };

        var parent = path[..path.LastIndexOf('\\')];
        var leaf = path[(path.LastIndexOf('\\') + 1)..];

        using var userParent = Registry.CurrentUser.OpenSubKey($@"{RegistryPaths.ClassesSubPath}\{parent}", writable: true);
        userParent?.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
        result.Operations.Add($"deleted {RegistryPaths.UserDisplayPath(path)}");

        Finish(result);
        return result;
    }

    // ─────────────────────────────────────────────────────────────── helpers

    /// <summary>Opens the key that a write should target, per the chosen strategy.</summary>
    private static RegistryKey OpenForWrite(MenuEntry entry, WriteStrategy strategy, bool create)
    {
        if (entry.Kind == EntryKind.CommandStoreVerb)
        {
            var storePath = $@"{RegistryPaths.CommandStorePath}\{entry.KeyName}";
            return Registry.LocalMachine.OpenSubKey(storePath, writable: true)
                ?? throw new UnauthorizedAccessException(
                    $@"Cannot write HKEY_LOCAL_MACHINE\{storePath}. Restart as administrator, then use ""Take ownership"" if needed.");
        }

        var path = entry.ClassesPath
                   ?? throw new InvalidOperationException("This entry has no Classes path.");

        if (strategy == WriteStrategy.UserOverlay)
            return RegistryPaths.CreateUser(path);

        // In place: prefer the hive the entry actually came from.
        if (entry.InUserHive)
        {
            var user = RegistryPaths.OpenUser(path, writable: true);
            if (user is not null) return user;
        }

        var machine = Registry.LocalMachine.OpenSubKey($@"{RegistryPaths.ClassesSubPath}\{path}", writable: true);
        if (machine is not null) return machine;

        if (!create) throw new UnauthorizedAccessException($"Cannot open {RegistryPaths.MachineDisplayPath(path)} for writing.");

        throw new UnauthorizedAccessException(
            $"Cannot write {RegistryPaths.MachineDisplayPath(path)}. It is most likely owned by TrustedInstaller. " +
            "Restart as administrator and use \"Take ownership\", or switch the write mode back to \"User overlay\".");
    }

    /// <summary>Yields every writable copy of the entry's key across hives, for value removal.</summary>
    private static IEnumerable<(RegistryKey? Key, string Label)> EnumerateWritableCopies(MenuEntry entry, WriteStrategy strategy)
    {
        if (entry.Kind == EntryKind.CommandStoreVerb)
        {
            var storePath = $@"{RegistryPaths.CommandStorePath}\{entry.KeyName}";
            yield return (Registry.LocalMachine.OpenSubKey(storePath, writable: true),
                          $@"HKEY_LOCAL_MACHINE\{storePath}");
            yield break;
        }

        var path = entry.ClassesPath;
        if (path is null) yield break;

        yield return (RegistryPaths.OpenUser(path, writable: true), RegistryPaths.UserDisplayPath(path));

        if (strategy == WriteStrategy.InPlace)
        {
            RegistryKey? machine = null;
            try
            {
                machine = Registry.LocalMachine.OpenSubKey($@"{RegistryPaths.ClassesSubPath}\{path}", writable: true);
            }
            catch (Exception)
            {
                // No write access to the machine copy; the caller reports what is left over.
            }
            yield return (machine, RegistryPaths.MachineDisplayPath(path));
        }
    }

    /// <summary>
    /// Deletes an overlay key that no longer holds anything of ours, then walks back up deleting the
    /// empty scaffolding it left (…\Classes\*\shell, …\Classes\*), so enabling an entry restores the
    /// registry to exactly what it looked like before.
    /// </summary>
    private static void TryPruneEmptyOverlay(string path, OperationResult result)
    {
        try
        {
            var current = path;

            while (current.Contains('\\'))
            {
                using (var user = RegistryPaths.OpenUser(current))
                {
                    if (user is null) break;
                    if (user.ValueCount > 0 || user.SubKeyCount > 0) break;
                }

                var parent = current[..current.LastIndexOf('\\')];
                var leaf = current[(current.LastIndexOf('\\') + 1)..];

                using (var userParent = Registry.CurrentUser.OpenSubKey(
                           $@"{RegistryPaths.ClassesSubPath}\{parent}", writable: true))
                {
                    if (userParent is null) break;
                    userParent.DeleteSubKey(leaf, throwOnMissingSubKey: false);
                }

                result.Operations.Add($"removed the now-empty overlay {RegistryPaths.UserDisplayPath(current)}");
                current = parent;
            }
        }
        catch (Exception)
        {
            // Leaving an empty overlay behind is harmless.
        }
    }

    private static void SetOrDelete(RegistryKey key, string name, string? value, OperationResult result)
    {
        if (value is null) return;

        var current = key.GetValue(name)?.ToString();
        if (value.Length == 0)
        {
            if (current is null) return;
            key.DeleteValue(name, throwOnMissingValue: false);
            result.Operations.Add($"{key.Name}\\  →  deleted \"{name}\"");
            return;
        }

        if (current == value) return;
        var kind = value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String;
        key.SetValue(name, value, kind);
        result.Operations.Add($"{key.Name}\\  →  set \"{name}\" = \"{value}\" ({kind})");
    }

    private static void SetFlag(RegistryKey key, string name, bool? wanted, OperationResult result)
    {
        if (wanted is null) return;

        var present = key.GetValue(name) is not null;
        if (wanted.Value == present) return;

        if (wanted.Value)
        {
            key.SetValue(name, string.Empty, RegistryValueKind.String);
            result.Operations.Add($"{key.Name}\\  →  set \"{name}\" = \"\" (REG_SZ)");
        }
        else
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            result.Operations.Add($"{key.Name}\\  →  deleted \"{name}\"");
        }
    }

    private static string? NormalisePosition(string? position)
    {
        if (position is null) return null;
        return position.Trim().ToLowerInvariant() switch
        {
            "top" => "Top",
            "bottom" => "Bottom",
            _ => string.Empty,
        };
    }

    private static string? Snapshot(MenuEntry entry, string action)
    {
        var paths = new List<string>();
        if (entry.ClassesPath is not null)
        {
            paths.Add(RegistryPaths.UserDisplayPath(entry.ClassesPath));
            paths.Add(RegistryPaths.MachineDisplayPath(entry.ClassesPath));
        }
        if (entry.Kind == EntryKind.CommandStoreVerb)
            paths.Add($@"HKEY_LOCAL_MACHINE\{RegistryPaths.CommandStorePath}\{entry.KeyName}");

        return BackupService.Snapshot($"{action}-{entry.KeyName}", paths.ToArray());
    }

    private static void CopyKey(RegistryKey source, RegistryKey destination)
    {
        foreach (var name in source.GetValueNames())
        {
            var value = source.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null) continue;
            destination.SetValue(name, value, source.GetValueKind(name));
        }

        foreach (var name in source.GetSubKeyNames())
        {
            using var childSource = source.OpenSubKey(name);
            if (childSource is null) continue;
            using var childDestination = destination.CreateSubKey(name, writable: true);
            CopyKey(childSource, childDestination);
        }
    }

    private static void Finish(OperationResult result)
    {
        Native.NotifyAssociationsChanged();
        BackupService.MarkChanged();
        BackupService.Log(result.Summary + (result.BackupFile is null ? "" : $"  [backup: {result.BackupFile}]"));
        foreach (var op in result.Operations) BackupService.Log("    " + op);
    }
}
