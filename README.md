# ContextBeGone

An editor for the classic Windows right-click menu. It finds every context menu entry registered on
your system, including the ones registry-only tools miss, and lets you disable, edit, create or
remove them. Every change is backed up to a `.reg` file first, so anything can be put back by
double-clicking.

Built for Windows 11. Works on Windows 10.

## Download

Get `ContextBeGone.exe` from the [latest release](../../releases/latest) and run it. It is a single
file, with no installer and no runtime to set up first. Backups go into a `ContextBeGone-Data`
folder beside the executable, so it runs fine from a USB stick.

## What it does

It scans every place the shell looks for menu entries: static verbs, COM handlers, drag-and-drop
handlers, cascading submenus, the New submenu, and packaged (MSIX) shell extensions. On a typical
machine that comes to roughly 2,700 entries across 1,500 scopes.

Search covers all of them at once. Type `notepad` and you get every entry that mentions it, wherever
it happens to be registered, in about a second.

The inspector is the part you will probably use most. Point it at a real file or folder and it asks
the shell what that menu actually contains, then traces each row back to the registry key, COM
handler or packaged app that put it there. That is usually the quickest way to track down an entry
you can see in the menu but cannot find in the registry.

Editing covers the display name, icon, command line, position, separators and SHIFT-only
visibility. You can also create entries from scratch.

## How it stays safe

Most built-in Windows entries live in registry keys owned by `TrustedInstaller`, where even an
administrator only has read access. Rather than fight that, ContextBeGone writes to
`HKEY_CURRENT_USER\Software\Classes` instead.

`HKEY_CLASSES_ROOT` is a merged view of the machine and per-user hives, and where a key exists in
both, the per-user side wins. A change written there overrides a system entry without modifying it.
So the default mode needs no administrator rights, leaves system keys untouched, and is undone by
deleting a single per-user key.

There is also an in place mode, for the one thing an overlay cannot do: removing something Windows
itself put there. It asks for elevation when it needs it.

Explorer only reads shell extensions at the moment it loads them, so a change stays invisible until
it restarts. The app compares its own change times against Explorer's start time and offers a
restart button when the two are out of step.

The full mechanism, covering every registration type and scope and how each one is disabled, is in
[docs/how-it-works.md](docs/how-it-works.md).

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

## Command line

For scripting, or for looking around without the UI.

```
ContextBeGone.exe --report [file]                    Dump every scope and entry
ContextBeGone.exe --search <term> [file]             Ranked search across everything
ContextBeGone.exe --inspect <path> [file]            Real menu for a file or folder
ContextBeGone.exe --inspect-ui <path>                Open the inspector on that item
ContextBeGone.exe --toggle <scope> <key> <on|off>    Enable or disable one entry
```

## Licence

MIT. See [LICENSE](LICENSE).
