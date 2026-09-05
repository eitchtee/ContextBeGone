# ContextBeGone

Take control of the Windows right-click menu.

ContextBeGone finds every classic context menu entry on your system — including the ones no
registry-only tool can see — and lets you disable, edit, create or remove them. Nothing changes
without a backup you can undo by double-clicking.

Built for Windows 11, works on Windows 10.

---

## Download

Grab **`ContextBeGone.exe`** from the [latest release](../../releases/latest) and run it.

That is the whole thing: one file, no installer, no prerequisites. It keeps backups in a
`ContextBeGone-Data` folder next to itself, so putting it on a USB stick works as you would expect.

---

## What it does

**Finds everything.** Static verbs, COM handlers, drag-and-drop handlers, cascading submenus, the
New submenu, and packaged (MSIX) shell extensions — around 2,700 entries across 1,500 scopes on a
typical machine.

**Searches the whole system.** Type `notepad` and get every entry that mentions it, wherever it is
registered, in about a second.

**Inspects a real menu.** Point it at any file or folder and it asks the shell what that menu
actually contains, then traces every item back to whatever produces it — a registry key, a COM
handler, or a packaged app. This is how you find the entry you can see but cannot locate.

**Edits properly.** Display name, icon, command line, position, separators, SHIFT-only visibility.
Or create a new entry from scratch.

**Undoes anything.** Every change is preceded by a `.reg` export. Double-click it to put things back.

---

## How it stays safe

Most built-in Windows entries live in registry keys owned by `TrustedInstaller`, where even an
administrator has read-only access. Rather than fight that, ContextBeGone writes to
`HKEY_CURRENT_USER\Software\Classes` instead.

`HKEY_CLASSES_ROOT` is a merged view of the machine and per-user hives with the per-user side
winning, so a change made there overrides a system entry **without modifying it**. The default mode
therefore needs no administrator rights, never touches a system key, and is undone by deleting one
per-user key.

An *in place* mode covers the one thing the overlay cannot do — removing something Windows itself
put there — and asks for elevation when it needs it.

Explorer only reads shell extensions when it loads them, so changes are invisible until it restarts.
The app notices when that is the case and offers a button.

For the full mechanism — every registration type, every scope, and how each is disabled — see
[docs/how-it-works.md](docs/how-it-works.md).

---

## Build from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```
git clone https://github.com/eitchtee/ContextBeGone.git
cd ContextBeGone/src/ContextBeGone
dotnet run
```

The single-file build used for releases:

```
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o out
```

---

## Command line

For scripting, or for looking around without the UI.

```
ContextBeGone.exe --report [file]                    Dump every scope and entry
ContextBeGone.exe --search <term> [file]             Ranked search across everything
ContextBeGone.exe --inspect <path> [file]            Real menu for a file or folder
ContextBeGone.exe --inspect-ui <path>                Open the inspector on that item
ContextBeGone.exe --toggle <scope> <key> <on|off>    Enable or disable one entry
```

---

## Licence

MIT — see [LICENSE](LICENSE).
