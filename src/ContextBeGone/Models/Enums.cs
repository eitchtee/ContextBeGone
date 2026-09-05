namespace ContextBeGone.Models;

/// <summary>The registration mechanism that produced a context menu entry.</summary>
public enum EntryKind
{
    /// <summary>A static verb: <c>&lt;scope&gt;\shell\&lt;verb&gt;</c>.</summary>
    StaticVerb,

    /// <summary>An in-process COM IContextMenu handler: <c>&lt;scope&gt;\shellex\ContextMenuHandlers\&lt;name&gt;</c>.</summary>
    ContextMenuHandler,

    /// <summary>An IContextMenu handler invoked on right-drag: <c>&lt;scope&gt;\shellex\DragDropHandlers\&lt;name&gt;</c>.</summary>
    DragDropHandler,

    /// <summary>A shared verb definition under <c>HKLM\...\Explorer\CommandStore\shell</c>.</summary>
    CommandStoreVerb,

    /// <summary>
    /// A packaged (MSIX) shell extension, registered in PackagedCom rather than HKCR. This is how
    /// modern Store/packaged apps add menu items, and why they are invisible to registry-only tools.
    /// </summary>
    PackagedHandler,

    /// <summary>A "New" submenu template: <c>HKCR\.ext[\ProgID]\ShellNew</c>.</summary>
    ShellNew,
}

/// <summary>Visibility state of an entry as the shell would evaluate it.</summary>
public enum EntryStatus
{
    Enabled,

    /// <summary>Hidden by LegacyDisable / ProgrammaticAccessOnly / HideBasedOnVelocityId, or a blocked CLSID.</summary>
    Disabled,

    /// <summary>Present but only shown when SHIFT is held (the <c>Extended</c> value).</summary>
    ShiftOnly,
}

/// <summary>Where a write should land.</summary>
public enum WriteStrategy
{
    /// <summary>
    /// Write into <c>HKCU\Software\Classes\&lt;same relative path&gt;</c>. HKEY_CLASSES_ROOT is a merged
    /// view of HKLM\Software\Classes and HKCU\Software\Classes with HKCU winning, so this injects or
    /// overrides values without touching TrustedInstaller-owned machine keys, needs no elevation, and
    /// is undone by deleting the overlay key.
    /// </summary>
    UserOverlay,

    /// <summary>Write to the key where the entry was actually found. May need elevation and/or key ownership.</summary>
    InPlace,
}
