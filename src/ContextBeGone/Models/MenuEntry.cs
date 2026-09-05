using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ContextBeGone.Models;

/// <summary>One discovered context menu registration.</summary>
public sealed class MenuEntry : INotifyPropertyChanged
{
    public required Scene Scene { get; init; }
    public required EntryKind Kind { get; init; }

    /// <summary>The registry key name, e.g. <c>Windows.Copy</c> or <c>7-Zip</c>.</summary>
    public required string KeyName { get; init; }

    /// <summary>
    /// Path of this entry's key relative to the Classes root, e.g.
    /// <c>Directory\Background\shell\Personalize</c>. Null for CommandStore entries.
    /// </summary>
    public string? ClassesPath { get; init; }

    /// <summary>Fully qualified path of the key that actually holds the definition (for display).</summary>
    public required string DisplayPath { get; init; }

    /// <summary>True when the definition exists under HKLM\Software\Classes.</summary>
    public bool InMachineHive { get; set; }

    /// <summary>True when the definition exists under HKCU\Software\Classes.</summary>
    public bool InUserHive { get; set; }

    /// <summary>
    /// The text the shell actually draws for this entry, with no "[key]" suffix. Used to match a
    /// real menu item back to the registry key that produced it.
    /// </summary>
    public string MenuText { get; set; } = string.Empty;

    public string RawMuiVerb { get; set; } = string.Empty;
    public string RawDefaultValue { get; set; } = string.Empty;

    /// <summary>Command line for static verbs (from the <c>command</c> subkey).</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>How the verb is executed when there is no plain command line.</summary>
    public string CommandMechanism { get; set; } = string.Empty;

    public string IconSpec { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;

    /// <summary>CLSID for COM handlers, or the DelegateExecute/ExplorerCommandHandler GUID of a verb.</summary>
    public string Clsid { get; set; } = string.Empty;

    /// <summary>Resolved InprocServer32/LocalServer32 path behind <see cref="Clsid"/>.</summary>
    public string HandlerPath { get; set; } = string.Empty;

    /// <summary>Semicolon-separated verbs of a static cascading menu, when present.</summary>
    public string SubCommands { get; set; } = string.Empty;

    /// <summary>AppliesTo AQS expression, when present.</summary>
    public string AppliesTo { get; set; } = string.Empty;

    /// <summary>Every value found directly on the key, for the advanced view.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names of the values responsible for a Disabled state.</summary>
    public List<string> DisableMarkers { get; } = new();

    /// <summary>True when the key already carries a real HKCU overlay written by this app (or by hand).</summary>
    public bool HasUserOverlay { get; set; }

    private string _displayName = string.Empty;
    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    private EntryStatus _status;
    public EntryStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(IsEnabled));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>Bound to the checkbox column. Shift-only entries count as enabled.</summary>
    public bool IsEnabled => Status != EntryStatus.Disabled;

    public string StatusText => Status switch
    {
        EntryStatus.Enabled => "Enabled",
        EntryStatus.ShiftOnly => "Shift-only",
        _ => "Disabled",
    };

    public string KindText => Kind switch
    {
        EntryKind.StaticVerb => "Static verb",
        EntryKind.ContextMenuHandler => "COM handler",
        EntryKind.DragDropHandler => "Drag-drop handler",
        EntryKind.CommandStoreVerb => "CommandStore verb",
        EntryKind.ShellNew => "New submenu",
        EntryKind.PackagedHandler => "Packaged (MSIX)",
        _ => Kind.ToString(),
    };

    public string HiveText =>
        (InMachineHive, InUserHive) switch
        {
            (true, true) => "HKLM + HKCU",
            (true, false) => "HKLM",
            (false, true) => "HKCU",
            _ => "—",
        };

    /// <summary>One-line summary of what the entry runs.</summary>
    public string Target =>
        !string.IsNullOrEmpty(Command) ? Command
        : !string.IsNullOrEmpty(HandlerPath) ? HandlerPath
        : !string.IsNullOrEmpty(Clsid) ? Clsid
        : CommandMechanism;

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    /// <summary>
    /// Copies the freshly-scanned state of the same key onto this instance and raises change
    /// notifications. Updating in place rather than replacing the object in the collection is what
    /// keeps the list from scrolling back to the top after a toggle.
    /// </summary>
    public void AdoptStateFrom(MenuEntry fresh)
    {
        InMachineHive = fresh.InMachineHive;
        InUserHive = fresh.InUserHive;
        HasUserOverlay = fresh.HasUserOverlay;

        MenuText = fresh.MenuText;
        RawMuiVerb = fresh.RawMuiVerb;
        RawDefaultValue = fresh.RawDefaultValue;
        Command = fresh.Command;
        CommandMechanism = fresh.CommandMechanism;
        IconSpec = fresh.IconSpec;
        Position = fresh.Position;
        Clsid = fresh.Clsid;
        HandlerPath = fresh.HandlerPath;
        SubCommands = fresh.SubCommands;
        AppliesTo = fresh.AppliesTo;

        Values.Clear();
        foreach (var pair in fresh.Values) Values[pair.Key] = pair.Value;

        DisableMarkers.Clear();
        DisableMarkers.AddRange(fresh.DisableMarkers);

        DisplayName = fresh.DisplayName;
        Status = fresh.Status;
        if (fresh.Icon is not null) Icon = fresh.Icon;

        OnPropertyChanged(nameof(HiveText));
        OnPropertyChanged(nameof(Target));
    }

    /// <summary>Also what screen readers and UI automation report for the row.</summary>
    public override string ToString() => $"{DisplayName} — {StatusText}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
