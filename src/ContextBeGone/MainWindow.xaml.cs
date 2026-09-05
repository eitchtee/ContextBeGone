using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ContextBeGone.Models;
using ContextBeGone.Services;
using Microsoft.Win32;

namespace ContextBeGone;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Scene> _scenes = new();
    private readonly ObservableCollection<MenuEntry> _entries = new();
    private MenuEntry? _selected;

    private WriteStrategy Strategy =>
        WriteModeBox.SelectedIndex == 1 ? WriteStrategy.InPlace : WriteStrategy.UserOverlay;

    public MainWindow()
    {
        InitializeComponent();

        var sceneView = new CollectionViewSource { Source = _scenes };
        sceneView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Scene.Group)));
        sceneView.View.Filter = SceneFilter;
        SceneList.ItemsSource = sceneView.View;

        var entryView = new CollectionViewSource { Source = _entries };
        entryView.View.Filter = EntryFilter;
        EntryList.ItemsSource = entryView.View;

        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.EnableDarkTitleBar(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ElevationText.Text = Elevation.IsElevated ? "Running elevated" : "Running as standard user";
        ElevateButton.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        ClassicMenuBox.IsChecked = ShellService.IsClassicMenuForced();

        BackupsButton.ToolTip =
            "Every change is preceded by a .reg export you can double-click to undo.\n\n" +
            BackupService.BackupRoot +
            (BackupService.IsPortable
                ? "\n\nStored next to the executable, so the app is portable."
                : "\n\nThe executable's folder is not writable, so this fell back to LocalAppData.");

        LoadScenes();
        foreach (var extension in Settings.FileTypes()) AddFileType(extension, remember: false);

        UpdateSceneCount();
        if (SceneList.Items.Count > 0) SceneList.SelectedIndex = 0;

        // Explorer only re-reads shell extensions when it loads them, so a change made while it is
        // running is invisible in the real menu until it restarts. Keep that state on screen.
        UpdateRestartBanner();
        var banner = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        banner.Tick += (_, _) => UpdateRestartBanner();
        banner.Start();

        if (App.InspectOnStartup is { Length: > 0 } startupPath)
            Dispatcher.BeginInvoke(() => ShowInspector(startupPath));
    }

    private void ShowInspector(string path)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        StatusText.Text = "Asking the shell to build the menu for " + path + " …";
        try
        {
            var result = MenuInspector.Inspect(path, TimeSpan.FromSeconds(45));
            BackupService.Log($"inspected {path}: {result.Items.Count} items, error={result.Error ?? "none"}");
            StatusText.Text = result.Error is null
                ? $"Inspected {path}: {result.Items.Count(i => !i.IsSeparator)} menu items."
                : "Inspection problem: " + result.Error;

            var window = new Views.InspectWindow(result) { Owner = this };
            Mouse.OverrideCursor = null;
            window.ShowDialog();

            if (window.SearchRequest is { Length: > 0 } term) JumpToSearch(term);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // ─────────────────────────────────────────────────────────── scenes

    private void LoadScenes()
    {
        var previous = (SceneList.SelectedItem as Scene)?.Id;
        var extras = _scenes.Where(s => s.Id.StartsWith("ext:", StringComparison.Ordinal)).ToList();

        _scenes.Clear();
        foreach (var scene in SceneCatalog.Fixed) _scenes.Add(scene);
        foreach (var scene in SceneCatalog.DiscoverSystemFileAssociations()) _scenes.Add(scene);
        foreach (var scene in extras) _scenes.Add(scene);

        if (previous is not null)
        {
            var match = _scenes.FirstOrDefault(s => s.Id == previous);
            if (match is not null) SceneList.SelectedItem = match;
        }
    }

    private void OnSceneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SceneList.SelectedItem is not Scene scene) return;

        RemoveTypeItem.IsEnabled = scene.IsUserAdded;
        RemoveTypeItem.Header = scene.IsUserAdded
            ? $"Stop listing {scene.SourceExtension}"
            : "Remove this file type  (only for types you added)";

        FilterHint.Text = scene.IsGlobalSearch
            ? "Search every scope — a program name, a command, a CLSID…"
            : "Filter by name, key, command, CLSID or DLL…";

        // Only a real registry scope can host a new verb; say so on the button rather than
        // failing quietly when it is pressed.
        var canHostEntries = scene.ClassesPath is not null;
        NewEntryButton.IsEnabled = canHostEntries;
        NewEntryButton.ToolTip = canHostEntries
            ? $"Create a new context menu entry under {scene.Name} (written to HKCU, no admin needed)."
            : $"\"{scene.Name}\" is not a registry scope, so a new entry cannot be added here. " +
              "Pick something like \"File folders\" or \"All files\" first.";

        if (scene.IsGlobalSearch) _ = LoadEverythingAsync();
        else LoadEntries(scene);
    }

    // ─────────────────────────────────────────────────────────── search everywhere

    private CancellationTokenSource? _sweep;

    /// <summary>
    /// Sweeps every scope on a background thread, then shows the results. The sweep is cached, so
    /// coming back to this scene is instant until Rescan drops it.
    /// </summary>
    private async Task LoadEverythingAsync()
    {
        _sweep?.Cancel();
        _sweep = new CancellationTokenSource();
        var token = _sweep.Token;

        _entries.Clear();
        ShowDetails(null);

        var cached = SearchService.HasCache;
        if (!cached) StatusText.Text = "Scanning every scope on the system…";

        var progress = new Progress<ScanProgress>(p =>
            StatusText.Text = p.Total == 0
                ? $"Search everywhere: {p.Label}"
                : $"Search everywhere: scanning {p.Done}/{p.Total} scopes — {p.Label}");

        List<MenuEntry> all;
        try
        {
            all = await Task.Run(() => SearchService.ScanEverything(progress, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Sweep failed: " + ex.Message;
            return;
        }

        if (token.IsCancellationRequested || SceneList.SelectedItem is not Scene { IsGlobalSearch: true }) return;

        StatusText.Text =
            $"Search everywhere: {all.Count} entries from {SearchService.AllScenes().Count} scopes" +
            (cached ? " (cached)" : $" in {SearchService.LastDuration.TotalSeconds:0.0}s") +
            ".  Type a term above — try a program name like \"notepad\".";

        ApplyGlobalSearch();
        FilterBox.Focus();
    }

    /// <summary>Ranks the swept entries against the filter box and shows the matches.</summary>
    private void ApplyGlobalSearch()
    {
        if (!SearchService.HasCache) return;

        var query = FilterBox.Text.Trim();
        _entries.Clear();

        if (query.Length == 0)
        {
            StatusText.Text = $"Search everywhere: {SearchService.CachedCount} entries indexed. Type a term to search.";
            return;
        }

        var matches = SearchService.Filter(SearchService.ScanEverything(null, CancellationToken.None), query);
        foreach (var entry in matches) _entries.Add(entry);

        var scopes = matches.Select(m => m.Scene.Name).Distinct().Count();
        StatusText.Text = matches.Count == 0
            ? $"No entry anywhere matches \"{query}\"."
            : $"\"{query}\": {matches.Count} entries across {scopes} scopes, best match first.";
    }

    private bool IsGlobalSearchActive => SceneList.SelectedItem is Scene { IsGlobalSearch: true };

    private void LoadEntries(Scene scene)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _entries.Clear();
            foreach (var entry in Scanner.Scan(scene)) _entries.Add(entry);

            var disabled = _entries.Count(x => x.Status == EntryStatus.Disabled);
            StatusText.Text = $"{scene.Name}: {_entries.Count} entries ({disabled} disabled).  {scene.Description}";
            ShowDetails(null);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        SearchService.Invalidate();
        LoadScenes();

        if (SceneList.SelectedItem is Scene scene)
        {
            if (scene.IsGlobalSearch) _ = LoadEverythingAsync();
            else LoadEntries(scene);
        }

        ClassicMenuBox.IsChecked = ShellService.IsClassicMenuForced();
    }

    /// <summary>
    /// Selects a scene and actually brings it into view. The scroll has to wait for layout: the
    /// items were only just added, so the container for the new scene does not exist yet and
    /// ScrollIntoView would silently do nothing.
    /// </summary>
    private void SelectScene(Scene scene)
    {
        SceneList.SelectedItem = scene;
        Dispatcher.BeginInvoke(() =>
        {
            SceneList.UpdateLayout();
            SceneList.ScrollIntoView(scene);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static bool HasEntries(Scene scene)
    {
        try { return Scanner.Scan(scene, loadIcons: false).Count > 0; }
        catch (Exception) { return false; }
    }

    // ─────────────────────────────────────────────────────────── scope search

    /// <summary>Filters the scope list itself, by anything visible on the row.</summary>
    private bool SceneFilter(object item)
    {
        if (item is not Scene scene) return false;

        var needle = SceneSearchBox.Text.Trim();
        if (needle.Length == 0) return true;

        foreach (var term in needle.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var hit = Contains(scene.Name, term)
                      || Contains(scene.Description, term)
                      || Contains(scene.Group, term)
                      || Contains(scene.ClassesPath, term);
            if (!hit) return false;
        }

        return true;
    }

    private void OnSceneSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;

        CollectionViewSource.GetDefaultView(SceneList.ItemsSource)?.Refresh();
        UpdateSceneCount();
    }

    private void UpdateSceneCount()
    {
        var shown = SceneList.Items.Count;
        var total = _scenes.Count;

        SceneCountText.Text = shown == total
            ? $"{total} scopes. Every place a menu can appear."
            : $"{shown} of {total} scopes match.";
    }

    // ─────────────────────────────────────────────────────────── file types

    /// <summary>
    /// Adding a file type is purely a view setting: it loads the scopes that apply to that type so
    /// they can be inspected. Nothing is written to the registry, and the list is remembered in the
    /// portable data folder so it survives a restart.
    /// </summary>
    private void OnAddFileType(object sender, RoutedEventArgs e)
    {
        var dialog = new Views.AddFileTypeWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        AddFileType(dialog.Extension, remember: true);
    }

    private void AddFileType(string extension, bool remember)
    {
        var addedScenes = new List<Scene>();

        foreach (var scene in SceneCatalog.ForExtension(extension))
        {
            if (_scenes.Any(s => s.Id == scene.Id)) continue;
            _scenes.Add(scene);
            addedScenes.Add(scene);
        }

        if (remember)
        {
            var types = Settings.FileTypes();
            if (!types.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                types.Add(extension);
                Settings.SaveFileTypes(types);
            }
        }

        if (addedScenes.Count == 0)
        {
            var existing = _scenes.FirstOrDefault(
                x => string.Equals(x.SourceExtension, extension, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                SelectScene(existing);
                StatusText.Text = $"{extension} was already listed — selected it.";
            }
            else
            {
                StatusText.Text = $"Nothing is registered for {extension}, so it has no scopes of its own.";
            }
            return;
        }

        // A new group at the bottom is easy to miss, and the extension key itself usually has no
        // verbs, so land on the first scope that actually contains something.
        SceneSearchBox.Clear();
        CollectionViewSource.GetDefaultView(SceneList.ItemsSource)?.Refresh();

        var landing = addedScenes.FirstOrDefault(HasEntries) ?? addedScenes[0];
        SelectScene(landing);
        UpdateSceneCount();

        StatusText.Text = $"Added {addedScenes.Count} scope(s) for {extension}, under \"File type {extension}\". " +
                          "Nothing on your system was changed; right-click to remove it again.";
    }

    /// <summary>Removes a file type the user added. Only ever affects what is listed.</summary>
    private void OnRemoveFileType(object sender, RoutedEventArgs e)
    {
        if (SceneList.SelectedItem is not Scene { SourceExtension: { Length: > 0 } extension })
        {
            StatusText.Text = "Right-click one of the scopes under a \"File type\" group to remove it.";
            return;
        }

        var doomed = _scenes.Where(
            s => string.Equals(s.SourceExtension, extension, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var scene in doomed) _scenes.Remove(scene);

        var types = Settings.FileTypes();
        types.RemoveAll(t => string.Equals(t, extension, StringComparison.OrdinalIgnoreCase));
        Settings.SaveFileTypes(types);

        UpdateSceneCount();
        StatusText.Text = $"Stopped listing {extension} ({doomed.Count} scopes removed from the list). " +
                          "Nothing on your system was changed.";

        if (SceneList.SelectedItem is null && SceneList.Items.Count > 0) SceneList.SelectedIndex = 0;
    }

    // ─────────────────────────────────────────────────────────── entry list

    private bool EntryFilter(object item)
    {
        if (item is not MenuEntry entry) return false;
        if (HideDisabledBox.IsChecked == true && entry.Status == EntryStatus.Disabled) return false;

        // In global search the ranking already applied the query; the view must not filter again.
        if (IsGlobalSearchActive) return true;

        var needle = FilterBox.Text.Trim();
        if (needle.Length == 0) return true;

        return Contains(entry.DisplayName, needle)
               || Contains(entry.KeyName, needle)
               || Contains(entry.Command, needle)
               || Contains(entry.Clsid, needle)
               || Contains(entry.HandlerPath, needle)
               || Contains(entry.ClassesPath, needle);
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        if (IsGlobalSearchActive) ApplyGlobalSearch();
        CollectionViewSource.GetDefaultView(EntryList.ItemsSource)?.Refresh();
    }

    private void OnEntrySelected(object sender, SelectionChangedEventArgs e)
    {
        ShowDetails(EntryList.SelectedItem as MenuEntry);
    }

    // ─────────────────────────────────────────────────────────── details pane

    private void ShowDetails(MenuEntry? entry)
    {
        _selected = entry;

        NavActions.IsEnabled = entry is not null;
        DangerHeader.Visibility = DangerActions.Visibility =
            entry is not null ? Visibility.Visible : Visibility.Collapsed;

        if (entry is null)
        {
            DetailTitle.Text = "Select an entry";
            DetailPath.Text = string.Empty;
            DetailMechanism.Text = "Pick a row on the left to see how it is registered and what you can change.";
            EditPanel.Visibility = Visibility.Collapsed;
            HandlerNote.Visibility = Visibility.Collapsed;
            ValueList.ItemsSource = null;
            return;
        }

        DetailTitle.Text = entry.DisplayName;
        DetailPath.Text = entry.DisplayPath;
        DetailMechanism.Text = DescribeMechanism(entry);
        ValueList.ItemsSource = entry.Values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).ToList();

        var editable = entry.Kind is EntryKind.StaticVerb or EntryKind.CommandStoreVerb;
        EditPanel.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        HandlerNote.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;

        if (!editable)
        {
            HandlerNote.Text = entry.Kind == EntryKind.ShellNew
                ? "New-submenu templates are toggled by renaming the ShellNew key. There is nothing else to edit here."
                : "This item is drawn by a COM handler (IContextMenu) whose menu text lives in code, not in the registry. "
                  + "It can only be enabled or disabled, which is done through the shell's Blocked CLSID list.";
            return;
        }

        LoadEditor(entry);
    }

    private void LoadEditor(MenuEntry entry)
    {
        EditMuiVerb.Text = entry.RawMuiVerb.Length > 0 ? entry.RawMuiVerb : entry.RawDefaultValue;
        EditMuiVerbHint.Text = entry.RawMuiVerb.StartsWith('@') || entry.RawDefaultValue.StartsWith('@')
            ? $"Currently an indirect resource string that resolves to \"{Native.ResolveDisplayString(EditMuiVerb.Text)}\". "
              + "Replacing it with plain text overrides the localised name."
            : string.Empty;

        EditIcon.Text = entry.IconSpec;
        EditCommand.Text = entry.Command;

        EditPosition.SelectedIndex = entry.Position.ToLowerInvariant() switch
        {
            "top" => 1,
            "bottom" => 2,
            _ => 0,
        };

        EditExtended.IsChecked = entry.Values.ContainsKey("Extended");
        EditSeparatorBefore.IsChecked = entry.Values.ContainsKey("SeparatorBefore");
        EditSeparatorAfter.IsChecked = entry.Values.ContainsKey("SeparatorAfter");
        EditNeverDefault.IsChecked = entry.Values.ContainsKey("NeverDefault");
        EditNoWorkingDirectory.IsChecked = entry.Values.ContainsKey("NoWorkingDirectory");
        EditLuaShield.IsChecked = entry.Values.ContainsKey("HasLUAShield");
    }

    private static string DescribeMechanism(MenuEntry entry)
    {
        var lines = new List<string> { $"{entry.KindText} — {entry.CommandMechanism}" };

        if (entry.Clsid.Length > 0) lines.Add($"CLSID: {entry.Clsid}");
        if (entry.HandlerPath.Length > 0) lines.Add($"Server: {entry.HandlerPath}");
        if (entry.SubCommands.Length > 0) lines.Add($"Cascading submenu (SubCommands): {entry.SubCommands}");
        if (entry.AppliesTo.Length > 0) lines.Add($"Shown only when AQS matches: {entry.AppliesTo}");
        if (entry.DisableMarkers.Count > 0) lines.Add($"Hidden by: {string.Join(", ", entry.DisableMarkers)}");
        if (entry.HasUserOverlay) lines.Add("A per-user overlay exists for this key in HKCU\\Software\\Classes.");
        if (entry.InMachineHive && !Elevation.IsElevated)
            lines.Add("Defined in HKLM. Hiding it works without admin via the per-user overlay; removing it does not.");

        return string.Join("\n", lines);
    }

    // ─────────────────────────────────────────────────────────── actions

    private void OnToggleEntry(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;
        if (box.DataContext is not MenuEntry entry) return;

        // Drive the model from the intent, not from the checkbox's own state.
        var wantEnabled = box.IsChecked == true;

        try
        {
            var result = Mutator.SetEnabled(entry, wantEnabled, Strategy);
            Report(result);
            if (!result.Success)
            {
                box.IsChecked = entry.IsEnabled;
                return;
            }

            RefreshEntryState(entry);
            box.IsChecked = entry.IsEnabled;
        }
        catch (Exception ex)
        {
            box.IsChecked = entry.IsEnabled;
            Fail(ex);
        }
    }

    /// <summary>
    /// Space or Enter toggles every selected row. This is a preview handler on purpose:
    /// ListViewItem consumes Space for its own selection handling, so a bubbling handler never
    /// sees it.
    /// </summary>
    private void OnEntryListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter)) return;

        // Let the filter box keep normal typing.
        if (Keyboard.FocusedElement is TextBox) return;
        ToggleSelected();
        e.Handled = true;
    }

    private void OnEnableSelected(object sender, RoutedEventArgs e) => ApplyToSelected(enable: true);

    private void OnDisableSelected(object sender, RoutedEventArgs e) => ApplyToSelected(enable: false);

    /// <summary>
    /// Flips the selection as a block: if anything in it is still showing, the whole selection is
    /// hidden; otherwise the whole selection comes back. Toggling each row independently would make
    /// a mixed selection unpredictable.
    /// </summary>
    private void ToggleSelected()
    {
        var selected = EntryList.SelectedItems.OfType<MenuEntry>().ToList();
        if (selected.Count == 0) return;

        ApplyToSelected(enable: selected.All(x => x.Status == EntryStatus.Disabled));
    }

    private void ApplyToSelected(bool enable)
    {
        var selected = EntryList.SelectedItems.OfType<MenuEntry>().ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Select one or more rows first.";
            return;
        }

        var changed = 0;
        var skipped = 0;
        var failures = new List<string>();
        var log = new List<string>();

        Mouse.OverrideCursor = selected.Count > 5 ? Cursors.Wait : null;
        try
        {
            foreach (var entry in selected)
            {
                if (entry.IsEnabled == enable) { skipped++; continue; }

                try
                {
                    var result = Mutator.SetEnabled(entry, enable, Strategy);
                    if (result.Success)
                    {
                        changed++;
                        log.Add(result.Summary);
                    }
                    else
                    {
                        failures.Add($"{entry.KeyName}: {result.Summary}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{entry.KeyName}: {ex.Message}");
                }
            }

            RefreshEntriesInPlace(selected);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        var verb = enable ? "Enabled" : "Disabled";
        StatusText.Text =
            $"{verb} {changed} of {selected.Count} selected" +
            (skipped > 0 ? $", {skipped} already {(enable ? "enabled" : "disabled")}" : string.Empty) +
            (failures.Count > 0 ? $", {failures.Count} failed" : string.Empty) +
            ".  Restart Explorer to see it.";

        var lines = new List<string> { $"{verb} {changed} entries" };
        lines.AddRange(log.Take(40));
        if (log.Count > 40) lines.Add($"… and {log.Count - 40} more");
        if (failures.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("FAILED:");
            lines.AddRange(failures);
        }
        OperationLog.Text = string.Join(Environment.NewLine, lines);
    }

    private void OnSaveEdits(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var edits = new VerbEdits
        {
            MuiVerb = EditMuiVerb.Text,
            Icon = EditIcon.Text,
            Command = EditCommand.Text,
            Position = (EditPosition.SelectedItem as ComboBoxItem)?.Content as string,
            Extended = EditExtended.IsChecked == true,
            SeparatorBefore = EditSeparatorBefore.IsChecked == true,
            SeparatorAfter = EditSeparatorAfter.IsChecked == true,
            NeverDefault = EditNeverDefault.IsChecked == true,
            NoWorkingDirectory = EditNoWorkingDirectory.IsChecked == true,
            HasLuaShield = EditLuaShield.IsChecked == true,
        };

        // The MUIVerb box shows the default value when there is no MUIVerb; do not silently move it.
        if (_selected.RawMuiVerb.Length == 0 && edits.MuiVerb == _selected.RawDefaultValue) edits.MuiVerb = null;

        try
        {
            Report(Mutator.ApplyEdits(_selected, edits, Strategy));
            RefreshEntryState(_selected);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void OnRevertEdits(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) LoadEditor(_selected);
    }

    private void OnRemoveOverlay(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            Report(Mutator.RemoveOverlay(_selected));
            RefreshEntryState(_selected);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void OnDeleteEntry(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var hasMachineCopy = _selected.InMachineHive;
        var message = $"Delete \"{_selected.DisplayName}\"?\n\n{_selected.DisplayPath}\n\n"
                      + "A .reg backup is written first, so this can be undone.\n\n"
                      + (hasMachineCopy
                          ? "This entry is defined in HKLM. Choose Yes to delete the machine copy too (needs "
                            + "administrator rights and possibly key ownership), No to delete only any per-user "
                            + "overlay, or Cancel to stop.\n\nDisabling is safer than deleting."
                          : "Only a per-user key will be removed.");

        var buttons = hasMachineCopy ? MessageBoxButton.YesNoCancel : MessageBoxButton.OKCancel;
        var answer = MessageBox.Show(message, "Delete entry", buttons, MessageBoxImage.Warning);

        if (answer is MessageBoxResult.Cancel or MessageBoxResult.None) return;
        var includeMachine = answer == MessageBoxResult.Yes;

        try
        {
            Report(Mutator.Delete(_selected, includeMachine));
            if (SceneList.SelectedItem is Scene scene) LoadEntries(scene);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void OnTakeOwnership(object sender, RoutedEventArgs e)
    {
        if (_selected?.ClassesPath is null)
        {
            StatusText.Text = "This entry has no machine key to take ownership of.";
            return;
        }

        var answer = MessageBox.Show(
            $"Take ownership of\n\n{RegistryPaths.MachineDisplayPath(_selected.ClassesPath)}\n\n"
            + "This transfers the key from TrustedInstaller to the Administrators group and grants full control. "
            + "It is a permanent change to the key's permissions, and Windows Update may recreate the key later.\n\n"
            + "You only need this to REMOVE something Windows placed there. Hiding an entry never requires it.\n\n"
            + "Continue?",
            "Take ownership", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        try
        {
            RegistryOwnership.TakeOwnershipOfClassesKey(_selected.ClassesPath);
            StatusText.Text = "Ownership taken. Switch the write mode to \"In place\" and retry the operation.";
            OperationLog.Text = $"Took ownership of {RegistryPaths.MachineDisplayPath(_selected.ClassesPath)}";
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void OnCreateEntry(object sender, RoutedEventArgs e)
    {
        if (SceneList.SelectedItem is not Scene scene || scene.ClassesPath is null)
        {
            MessageBox.Show(
                "Pick a registry scope on the left first — \"File folders\" or \"All files\", for example.\n\n" +
                "The current selection is not a registry scope, so there is nowhere to put a new entry. " +
                "Search everywhere, Command store, New submenu and Packaged apps are all in that category.",
                "New entry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Views.NewEntryWindow(scene) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            Report(Mutator.CreateVerb(scene, dialog.KeyName, dialog.Label, dialog.Command,
                                      dialog.IconSpec, dialog.Position, dialog.Extended));
            LoadEntries(scene);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    // ─────────────────────────────────────────────────────────── toolbar

    /// <summary>Shows the banner only while a change we made is not yet live in the running Explorer.</summary>
    private void UpdateRestartBanner()
    {
        try
        {
            if (!ShellService.HasPendingRestart())
            {
                RestartBanner.Visibility = Visibility.Collapsed;
                return;
            }

            var started = ShellService.ExplorerStartTime();
            var changed = BackupService.LastChangeUtc()?.ToLocalTime();

            RestartBannerText.Text =
                "Your changes are saved but not visible yet: Explorer has been running since " +
                $"{started:HH:mm} and last loaded its shell extensions then, while the newest change was made at " +
                $"{changed:HH:mm}. Restart Explorer to apply it.";

            RestartBanner.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            RestartBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void OnRestartExplorer(object sender, RoutedEventArgs e)
    {
        RestartExplorerButton.IsEnabled = false;
        StatusText.Text = "Restarting Explorer…";
        try
        {
            ShellService.RestartExplorer();
            StatusText.Text = "Explorer restarted. Right-click something to see the result.";
            UpdateRestartBanner();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            RestartExplorerButton.IsEnabled = true;
        }
    }

    private void OnToggleClassicMenu(object sender, RoutedEventArgs e)
    {
        try
        {
            Report(ShellService.SetClassicMenuForced(ClassicMenuBox.IsChecked == true));
        }
        catch (Exception ex)
        {
            ClassicMenuBox.IsChecked = ShellService.IsClassicMenuForced();
            Fail(ex);
        }
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (Elevation.RestartElevated()) Application.Current.Shutdown();
        else StatusText.Text = "Elevation was declined.";
    }

    /// <summary>
    /// Asks the shell for a real item's menu. This is the only way to see items that COM or
    /// packaged handlers draw in code, which is why registry-only tools cannot find them.
    /// </summary>
    private void OnInspect(object sender, RoutedEventArgs e)
    {
        var choice = MessageBox.Show(
            "Inspect a file's menu?\n\nYes = pick a file,  No = pick a folder,  Cancel = stop.",
            "Inspect a real item", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        string? path = null;
        if (choice == MessageBoxResult.Yes)
        {
            var dialog = new OpenFileDialog { Title = "Pick any file", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) path = dialog.FileName;
        }
        else if (choice == MessageBoxResult.No)
        {
            var dialog = new OpenFolderDialog { Title = "Pick any folder" };
            if (dialog.ShowDialog(this) == true) path = dialog.FolderName;
        }

        if (string.IsNullOrEmpty(path)) return;
        ShowInspector(path);
    }

    /// <summary>
    /// Switches to the global search and looks the term up, so an item found in the inspector can
    /// be edited straight away.
    /// </summary>
    private async void JumpToSearch(string term)
    {
        var everywhere = _scenes.FirstOrDefault(s => s.IsGlobalSearch);
        if (everywhere is null) return;

        if (!ReferenceEquals(SceneList.SelectedItem, everywhere))
        {
            SceneList.SelectedItem = everywhere;
            await LoadEverythingAsync();
        }

        FilterBox.Text = term;
        ApplyGlobalSearch();

        if (_entries.Count > 0)
        {
            EntryList.SelectedItem = _entries[0];
            EntryList.ScrollIntoView(_entries[0]);
            EntryList.Focus();
        }
        else
        {
            StatusText.Text = $"\"{term}\" was not found in the registry scan — it is probably drawn " +
                              "in code by a handler rather than registered as a verb.";
        }
    }

    private void OnOpenBackups(object sender, RoutedEventArgs e) => ShellService.OpenFolder(BackupService.BackupRoot);

    private void OnWriteModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        StatusText.Text = Strategy == WriteStrategy.UserOverlay
            ? "Writes go to HKCU\\Software\\Classes. HKEY_CLASSES_ROOT merges that over HKLM, so system entries can be overridden without being touched."
            : "Writes go to the original key. Removing values Windows placed in HKLM needs administrator rights, and sometimes key ownership.";
    }

    private void OnOpenRegedit(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) ShellService.OpenInRegedit(_selected.DisplayPath);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            Clipboard.SetText(_selected.DisplayPath);
            StatusText.Text = "Path copied.";
        }
        catch (Exception)
        {
            StatusText.Text = "The clipboard was busy; try again.";
        }
    }

    private void OnBrowseIcon(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pick an icon source",
            Filter = "Icon sources (*.ico;*.exe;*.dll)|*.ico;*.exe;*.dll|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true) EditIcon.Text = dialog.FileName;
    }

    // ─────────────────────────────────────────────────────────── plumbing

    /// <summary>
    /// Re-reads entries from the registry and updates the existing objects in place.
    ///
    /// The objects are deliberately not replaced in the collection: swapping items makes the
    /// ListView regenerate its containers and jump back to the top, which is intolerable after
    /// toggling something halfway down a long list. Updating in place also keeps the cached global
    /// sweep correct for free, since it holds the very same instances.
    /// </summary>
    private void RefreshEntriesInPlace(IEnumerable<MenuEntry> entries)
    {
        var vanished = new List<MenuEntry>();

        // One scan per scope, not per entry — bulk toggles would otherwise rescan hundreds of times.
        foreach (var group in entries.GroupBy(e => e.Scene))
        {
            Dictionary<(EntryKind, string), MenuEntry> fresh;
            try
            {
                fresh = Scanner.Scan(group.Key, loadIcons: false)
                               .GroupBy(x => (x.Kind, x.KeyName.ToLowerInvariant()))
                               .ToDictionary(g => g.Key, g => g.First());
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var entry in group)
            {
                if (fresh.TryGetValue((entry.Kind, entry.KeyName.ToLowerInvariant()), out var updated))
                    entry.AdoptStateFrom(updated);
                else
                    vanished.Add(entry);
            }
        }

        foreach (var gone in vanished) _entries.Remove(gone);

        if (_selected is not null && vanished.Contains(_selected)) ShowDetails(null);
        else if (_selected is not null) ShowDetails(_selected);
    }

    private void RefreshEntryState(MenuEntry entry) => RefreshEntriesInPlace([entry]);

    private void Report(OperationResult result)
    {
        StatusText.Text = result.Success
            ? result.Summary + "  —  restart Explorer to see it."
            : result.Summary;

        var lines = new List<string> { result.Summary, string.Empty };
        lines.AddRange(result.Operations);
        if (result.BackupFile is not null)
        {
            lines.Add(string.Empty);
            lines.Add("Backup: " + result.BackupFile);
        }
        OperationLog.Text = string.Join(Environment.NewLine, lines);
    }

    private void Fail(Exception ex)
    {
        StatusText.Text = "Failed: " + ex.Message;
        OperationLog.Text = ex.ToString();
        BackupService.Log("ERROR: " + ex);
    }
}
