# How it works

Where Windows registers context menu entries, how each kind is disabled, and how the app checks its
own results against the menu Explorer really draws. Written against Windows 11 25H2 (build 26200).

For what the app is and how to get it, see the [README](../README.md).

## How Windows builds the classic context menu

There are six mechanisms. Five live under `HKEY_CLASSES_ROOT`. The sixth does not, which is why it
defeats registry-only tools.

### 1. Static verbs

```
HKCR\<scope>\shell\<verb>
    (Default)   = display text, or the key name is used
    MUIVerb     = display text, often an indirect string: @shell32.dll,-8506
    Icon        = "path",index
    Position    = Top | Bottom
    Extended    = ""      → only shown with SHIFT held
    SubCommands = "a;b;c" → cascading submenu built from the CommandStore
    AppliesTo   = AQS query, e.g. System.ItemName:"report"
    HasLUAShield, NeverDefault, NoWorkingDirectory, SeparatorBefore/After,
    MultiSelectModel, AttributeMask/AttributeValue, SuppressionPolicy
  \command
    (Default)   = the command line   (%1 item, %V folder/background, %* all selected)
```

Instead of a plain `command`, a verb may be executed through COM: `DelegateExecute`
(IExecuteCommand), `ExplorerCommandHandler` (IExplorerCommand), or a `DropTarget\CLSID` subkey
(IDropTarget). The app detects and reports each.

### 2. COM shortcut-menu handlers

```
HKCR\<scope>\shellex\ContextMenuHandlers\<name>
    (Default) = {CLSID}      → HKCR\CLSID\{..}\InprocServer32 = the DLL
```

These build their menu items in code (`IContextMenu`), so their text is not in the registry at all.
7-Zip, OneDrive, PowerToys and Notepad++ all work this way. They can only be turned on or off.

### 3. Drag-and-drop handlers

`HKCR\<scope>\shellex\DragDropHandlers\<name>`, the same shape but for right-button drag.

### 4. Cascading submenus

`SubCommands` names verbs stored once in
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell` (242 of them on a stock
25H2 box). Alternatively `ExtendedSubCommandsKey` points at a private `shell` subtree.

### 5. Packaged (MSIX) shell extensions

Modern packaged apps register context menu commands outside HKCR entirely, under
`HKLM\SOFTWARE\Classes\PackagedCom\ClassIndex\{CLSID}\<PackageFullName>`, with the implementing DLL
at `...\PackagedCom\Package\<pkg>\Class\{CLSID}`. Windows 11's *Edit in Notepad* and Notepad++ 8.7's
*Edit with Notepad++* are both of this kind, which is why looking at `shell` and `shellex` keys
never finds them. They are suppressed with the same Blocked CLSID list as ordinary handlers.

### 6. The New submenu

`HKCR\.ext\ShellNew` or `HKCR\.ext\<ProgID>\ShellNew`, with `NullFile`, `FileName`, `Data` or
`Command`.

### Scopes the shell consults

| Key | Applies to |
|---|---|
| `*` | every file |
| `AllFilesystemObjects` | files, folders, drives, shortcuts |
| `Folder` | every folder, including virtual ones |
| `Directory` | real folders on disk |
| `Directory\Background` | empty space inside an open folder |
| `Drive` | drives in This PC |
| `DesktopBackground` | empty space on the desktop |
| `LibraryFolder`, `LibraryFolder\background`, `UserLibraryFolder` | libraries |
| `Network`, `NetShare`, `NetServer`, `Printers`, `AudioCD`, `DVD` | namespace objects |
| `Unknown` | files with no association |
| `<ProgID>` (`txtfile`, `exefile`, `lnkfile`, …) | one file type |
| `SystemFileAssociations\<ext or perceived type>` | text, image, audio, video, … |
| `CLSID\{...}` | This PC, Recycle Bin, Network, Control Panel, OneDrive, Home |

## How entries are disabled

| Kind | Mechanism |
|---|---|
| Static verb | Add `LegacyDisable` and `ProgrammaticAccessOnly` (empty `REG_SZ`). Both are documented as hiding a verb; writing both covers shell paths that only honour one. |
| Windows-gated verb | Windows hides its own verbs by renaming `ShowBasedOnVelocityId` to `HideBasedOnVelocityId`. In *In place* mode the app does the same, preserving the feature id. |
| COM handler | Write the CLSID as a value *name* under `…\CurrentVersion\Shell Extensions\Blocked`. |
| New submenu | Rename `ShellNew` to `ShellNew-`, which is the shell's own convention. |

### The HKCU overlay

`HKEY_CLASSES_ROOT` is not a real hive. It is a merged view of `HKLM\Software\Classes` and
`HKCU\Software\Classes`, and where a key exists in both, the per-user values win.

Most built-in Windows verbs live in HKLM keys owned by `TrustedInstaller`, where even an elevated
administrator only has read access. So instead of fighting the ACL, the app writes the hide marker
to `HKCU\Software\Classes\<same relative path>`. The merged view then shows that marker alongside
the machine key's own values, and the verb disappears.

Verified on this machine:

```
HKLM\...\Directory\shell\cmd     →  (Default), Extended, HideBasedOnVelocityId, NoWorkingDirectory
HKCU\...\Directory\shell\cmd     →  LegacyDisable
HKCR\Directory\shell\cmd         →  all of the above, and the HKLM \command subkey still visible
```

So the default mode needs no administrator rights, never modifies a system key, and is undone
completely by deleting one per-user key. The app also prunes the empty scaffolding it created on the
way in.

*In place* mode exists for the one thing the overlay cannot do: removing a value or key that Windows
itself placed in HKLM. That needs elevation, and sometimes `Take ownership`.

## Searching every scope at once

Selecting **Search everywhere**, pinned at the top of the scope list, sweeps every place a menu can
be registered in one pass, then ranks matches against your term. Searching `notepad` returns the
Notepad++ COM handler, `Applications\notepad.exe`, and every ProgID whose `edit` or `open` verb
invokes notepad, which comes to 52 entries across 30 scopes on this machine.

Measured on Windows 11 25H2:

```
scopes          : 1511  (enumerated in  390 ms)
entries indexed : 2672  (swept in     ~1000 ms)
matches         :   52  (ranked in        4 ms)
total           : ~1400 ms
```

The sweep covers the fixed scopes, `SystemFileAssociations\*`, every ProgID with a menu container
(extension keys included), `Applications\*`, the packaged handlers and the shell-namespace CLSIDs.
It skips the COM registration trees such as `Interface`, `TypeLib` and `AppID`, which never carry
menus.

Results are cached for the life of the window, so repeat searches are instant. **Rescan** drops the
cache. Ranking is name → key → command → handler DLL → scope → path, and multiple words narrow the
result, since all of them must match. Icons are resolved lazily for matches only, which is what
keeps the sweep under a second.

Each row has a **Where it appears** column, so an entry in `Directory` is distinguishable from the
same one in `Directory\Background`. Everything is editable straight from the results.

## Inspecting a real menu

Registry scanning cannot explain every item you see, for two reasons: COM handlers build their items
in code, and packaged apps register outside HKCR. **Inspect a real item** asks the shell itself,
through `IContextMenu::QueryContextMenu`, what a given file or folder's menu actually contains, then
traces every row back to its source:

- static verbs report their canonical verb via `GetCommandString`, which is the registry key name;
- COM handlers are identified by instantiating each registered handler on its own and asking what it
  contributes, then matching the text;
- a GUID where a verb name should be means a packaged handler, resolved to its package.

On this machine that produces, for a `.txt` file:

```
Mover para o OneDrive       FileSyncEx                              {CB3D0F55-BC2C-4C1A-85ED-23ED75B5106B}
Editar com o Notepad++      packaged app: NotepadPlusPlus           {E6950302-61F0-4FEB-97DB-855E30D4A991}
Editar no Bloco de Notas    packaged app: Microsoft.WindowsNotepad  {CA6CC9F1-867A-481E-951E-A28C5E4F01EA}
7-Zip -> Extrair aqui       7-Zip                                   {23170F69-40C1-278A-1000-000100020000}
```

Any of them can be switched off from that window. Probing loads third-party shell extensions
in-process, so it runs in a child process. A faulty extension cannot take the app down, and a hung
one is killed after a timeout.

### A folder has two menus

Right-clicking a folder and right-clicking the empty space inside it produce different menus from
different scopes (`Directory` against `Directory\Background`), and they are obtained differently.
The first comes from the parent's `GetUIObjectOf`, the second from the folder's own
`CreateViewObject`. The inspector probes both and offers a selector.

### Cascading submenus

A static cascade (`SubCommands` or `ExtendedSubCommandsKey`) is filled in lazily by the shell when
the submenu is opened, so outside a real menu loop it reads back empty. The inspector handles that
in two ways. It forwards `WM_INITMENUPOPUP` through `IContextMenu2` and `IContextMenu3`, which
populates handler-owned cascades such as New and Send to. Where the shell still defers, it expands
the children straight from the registry, since `ExtendedSubCommandsKey` and `SubCommands` say
exactly where they live.

Those children are editable entries in their own right, so `<scope>\ContextMenus\<name>\shell\*` is
a scanned scope as well, listed under "Cascading submenus" and reachable from search.

### Finding an item back in the main list

Every inspected row carries the identity of whatever produced it: a registry key name for a static
verb, a CLSID for a handler. **Find in main list** closes the inspector, switches to the global
search, looks that identity up and selects the row, with the editor already populated. Static verbs
are matched to their key by the text they draw, which is how a cascade parent with no canonical verb
still resolves to `Directory\Background\shell\WindowsTerminal`.

### The SHIFT menu

The menu is built twice, once with `CMF_EXTENDEDVERBS` and once without. Anything present only in
the first is an extended verb, marked with an arrow and toggleable with the "Include SHIFT+right-click
items" checkbox. The flags used are the ones Explorer itself passes for a selected item
(`CMF_ITEMMENU | CMF_CANRENAME`, plus a real owner window). With a bare `CMF_NORMAL` and a null
HWND, several handlers behave differently and Rename disappears.

Extended-only detection consumes the plain menu as a multiset rather than a set. Two handlers can
contribute items with identical text, such as two "Open in Terminal" entries where one is packaged
and one is a static verb, and set membership would wrongly clear the SHIFT flag on both.

The simulated plain menu was checked against the menu Explorer actually draws, read out of the live
popup via MSAA (`IAccessible`), for the same file:

```
real (Menu key)      : 20 items
simulated plain      : 20 items   -> identical, no differences
real (Shift+F10)     : 22 items
extra under SHIFT    : File Locksmith, PowerRename  -> exactly the two marked
```

One thing to watch when testing this yourself: opening the menu with Shift+F10 holds Shift, so
Explorer builds the extended menu. Use the Menu key for the plain one.

### Changes are not live until Explorer restarts

Explorer reads the Blocked list when it loads a handler, then keeps the DLL and its association
cache for the life of the process. A block written while it is running therefore has no visible
effect, even though a fresh process such as the probe honours it immediately.

The app records the time of every change and compares it against Explorer's start time. When a
change is not yet live, a banner appears with a **Restart Explorer now** button. An Explorer restart
is enough on its own, with no reboot or sign-out, including for packaged handlers. That was verified
by blocking one, re-probing, and unblocking.

## What the app does

- Universal search across roughly 1,500 scopes in about 1.4 seconds, described above.
- Scans 211 scopes and reports every entry with its resolved display name, icon, status, hive and
  what it actually runs. Indirect strings like `@dll,-id` are resolved, so localized names show
  correctly.
- Enables or disables an entry with one checkbox. Select several rows and press Space, or use the
  right-click menu, to toggle them as a block: a mixed selection gets hidden, an all-hidden one gets
  restored. Toggling updates rows in place, so a long list never scrolls back to the top.
- Inspects a real file or folder to show the menu the shell actually builds and what produces each
  item, described above.
- Edits a static verb: display name, icon, command line, `Position`, `Extended`, separators,
  `NeverDefault`, `NoWorkingDirectory`, `HasLUAShield`.
- Creates a new verb in any scope, written to HKCU with no elevation.
- Deletes an entry, with an explicit choice about the machine copy.
- Takes ownership of a TrustedInstaller key, behind a confirmation.
- Toggles the Windows 11 classic menu by creating
  `HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32` with an empty
  default value, so right-click opens the full menu without going through "Show more options".
- Restarts Explorer, since it caches the association array. The button is in the toolbar, and a
  banner surfaces it automatically whenever a change is not yet live.

### Adding a file type, and creating entries

**Add type** loads the scopes for one file extension. Asking for `.png` pulls in `pngfile`,
`SystemFileAssociations\.png`, `SystemFileAssociations\image` and so on, so you can edit menus that
apply only to that type. The scopes are appended under "File type .png", and the app jumps to the
first one that actually contains entries.

**+ New entry** creates a verb in the selected scope. It only works on a real registry scope, so it
is disabled, with a tooltip saying why, while Search everywhere, Command store, New submenu or
Packaged apps is selected.

### Portable data

Everything the app writes goes into `ContextBeGone-Data`, next to the executable: backups, the
journal, and the pending-change marker. Copy that folder to a stick and the history travels with it.
If the executable's folder is not writable, as in Program Files or a read-only share, it falls back
to `%LOCALAPPDATA%\ContextBeGone\Backups` rather than failing, and data written by an older build is
migrated on first run. The Backups button's tooltip shows which location is in use.

### Safety

Every change is preceded by a `reg export` of the affected keys into
`<data folder>\<timestamp>_<action>.reg`. Double-click it to restore. `journal.log` in the same
folder records every operation, and the app prints the exact registry writes it made in the *Last
operation* pane, so you can always see what was touched.

### Command line

```
ContextBeGone.exe --report [file]                              # dump every scope and entry
ContextBeGone.exe --search <term> [file]                       # ranked sweep + timings
ContextBeGone.exe --inspect <path> [file]                      # real menu + attribution
ContextBeGone.exe --inspect-ui <path>                          # open the inspector window
ContextBeGone.exe --probe <path> <json>                        # internal: used as a child process
ContextBeGone.exe --search notepad                             # example
ContextBeGone.exe --toggle <classesPath> <key> <on|off> [inplace] [file]
ContextBeGone.exe --toggle Directory cmd off                   # example
```

## Known limits

- COM handlers cannot be renamed or given a different icon, because their text lives in the DLL
  rather than the registry.
- Re-enabling something Windows shipped disabled needs *In place* mode plus elevation, and possibly
  ownership. The app says so explicitly instead of silently doing nothing.
- Windows 11's compact menu, the icon bar, is a separate surface. This app targets the classic menu,
  plus the toggle that makes the classic menu the default.

## Sources

- [Creating Shortcut Menu Handlers, Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/shell/context-menu-handlers)
- [Creating Shell Extension Handlers (Predefined Shell Objects), Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/shell/handlers)
- [Registering Shell Extension Handlers, Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/shell/reg-shell-exts)
- [The Windows Context Menu, enderman.ch](https://enderman.ch/blog/the-windows-context-menu)
- [ContextMenuManager registry operations, DeepWiki](https://deepwiki.com/BluePointLilac/ContextMenuManager/6-registry-operations)
- [Restore the classic context menu in Windows 11, 4sysops](https://4sysops.com/archives/restore-classic-context-menu-in-windows-11-explorer-using-group-policy-or-powershell/)
