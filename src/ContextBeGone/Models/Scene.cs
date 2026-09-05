namespace ContextBeGone.Models;

/// <summary>
/// A place where the shell looks for context menu registrations: a subkey of HKEY_CLASSES_ROOT
/// (or a special root such as the CommandStore) plus a human label.
/// </summary>
public sealed class Scene
{
    public required string Id { get; init; }

    /// <summary>Label shown in the scene list.</summary>
    public required string Name { get; init; }

    /// <summary>Where the menu appears, in plain words.</summary>
    public required string Description { get; init; }

    /// <summary>Path relative to the Classes root, e.g. <c>Directory\Background</c>. Null for pseudo-scenes.</summary>
    public string? ClassesPath { get; init; }

    /// <summary>Grouping header in the scene list.</summary>
    public required string Group { get; init; }

    /// <summary>Set for the synthetic CommandStore scene.</summary>
    public bool IsCommandStore { get; init; }

    /// <summary>Set for the synthetic "New submenu" scene.</summary>
    public bool IsShellNew { get; init; }

    /// <summary>Set for the pinned "search everywhere" pseudo-scene.</summary>
    public bool IsGlobalSearch { get; init; }

    /// <summary>Set for the synthetic packaged-app (MSIX) scene.</summary>
    public bool IsPackaged { get; init; }

    /// <summary>The file extension this scene was loaded for, when the user added one.</summary>
    public string? SourceExtension { get; init; }

    /// <summary>True for scopes the user added by file type, which can be removed again.</summary>
    public bool IsUserAdded => SourceExtension is not null;

    public override string ToString() => Name;
}
