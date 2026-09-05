using System.Windows;
using ContextBeGone.Services;

namespace ContextBeGone.Views;

/// <summary>A row in the inspector, flattened for display.</summary>
public sealed class InspectedRow
{
    public required InspectedItem Item { get; init; }

    public string Display => new string(' ', Item.Depth * 4) + (Item.IsSeparator ? "──────────" : Item.Text);

    public string Origin => Item.Source.Length > 0
        ? Item.Source
        : Item.Verb.Length > 0 ? "static verb" : string.Empty;

    public string Handle => Item.SourceClsid.Length > 0 ? Item.SourceClsid : Item.Verb;

    /// <summary>Only items backed by a COM or packaged CLSID can be switched off from here.</summary>
    public bool CanBlock => Item.SourceClsid.Length > 0;

    public string ShiftMark => Item.ExtendedOnly ? "⇧" : string.Empty;

    /// <summary>Also what screen readers and UI automation report for the row.</summary>
    public override string ToString() =>
        Item.IsSeparator ? "separator" : $"{Item.Text} — {Origin}";
}

public partial class InspectWindow : Window
{
    private readonly InspectionResult _result;
    private List<InspectedRow> _rows = new();

    /// <summary>Set when the user asks to locate the selected item back in the main list.</summary>
    public string? SearchRequest { get; private set; }

    public InspectWindow(InspectionResult result)
    {
        InitializeComponent();
        _result = result;

        PathText.Text = result.Path;

        if (result.Error is not null)
        {
            ErrorText.Text = result.Error;
            ErrorText.Visibility = Visibility.Visible;
        }

        // A folder has two independent menus; let the user switch between them.
        MenuModeBox.Visibility = result.BackgroundItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ApplyShiftFilter();
        ItemList.SelectionChanged += (_, _) => UpdateButton();
        UpdateButton();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.EnableDarkTitleBar(this);
    }

    private void OnToggleShift(object sender, RoutedEventArgs e) => ApplyShiftFilter();

    private void OnMenuModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded || _rows.Count > 0) ApplyShiftFilter();
    }

    /// <summary>Switches between the plain right-click menu and the SHIFT one.</summary>
    private void ApplyShiftFilter()
    {
        var background = MenuModeBox.SelectedIndex == 1 && _result.BackgroundItems.Count > 0;
        var source = background ? _result.BackgroundItems : _result.Items;

        _rows = source.Select(i => new InspectedRow { Item = i }).ToList();

        var shiftOnly = _rows.Count(r => r.Item.ExtendedOnly);
        ShiftCountText.Text = shiftOnly == 0
            ? "Nothing here is SHIFT-only."
            : $"{shiftOnly} of these appear only with SHIFT held.";

        var includeShift = ShowShiftBox.IsChecked == true;
        ItemList.ItemsSource = includeShift ? _rows : _rows.Where(r => !r.Item.ExtendedOnly).ToList();

        var where = background ? "inside the folder" : _result.IsFolder ? "on the folder" : "on the file";
        PathText.Text = $"{_result.Path}   —   right-click {where}" +
                        (includeShift ? ", SHIFT held" : ", no SHIFT");
    }

    private void UpdateButton()
    {
        var row = ItemList.SelectedItem as InspectedRow;
        DisableButton.IsEnabled = row is { CanBlock: true };
        FindButton.IsEnabled = row is not null && !row.Item.IsSeparator;
    }

    /// <summary>Hands the selected item's best identifier back to the main window to search for.</summary>
    private void OnFindInList(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is not InspectedRow row || row.Item.IsSeparator) return;

        SearchRequest = row.Item.SearchTerm;
        DialogResult = true;
        Close();
    }

    private void OnDisableHandler(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is not InspectedRow row || !row.CanBlock) return;

        var clsid = row.Item.SourceClsid;
        var answer = MessageBox.Show(
            $"Block the handler behind \"{row.Item.Text}\"?\n\n{row.Item.Source}\n{clsid}\n\n" +
            "The CLSID is added to the shell's Blocked list, which stops the handler loading. " +
            "A handler often draws several menu items, so all of them will disappear together.\n\n" +
            "A .reg backup is written first, and the entry can be re-enabled from the " +
            "\"Packaged apps (MSIX)\" or handler list.",
            "Block handler", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        try
        {
            BackupService.Snapshot($"blocked-{clsid}",
                $@"HKEY_CURRENT_USER\{RegistryPaths.BlockedSubPath}",
                $@"HKEY_LOCAL_MACHINE\{RegistryPaths.BlockedSubPath}");

            var written = BlockedList.Block(clsid, $"{row.Item.Text} (blocked by ContextBeGone)");
            Native.NotifyAssociationsChanged();
            BackupService.Log($"blocked {clsid} for menu item \"{row.Item.Text}\"");

            ResultText.Text = $"Blocked. Written to: {string.Join(", ", written)}. Restart Explorer to see it.";
        }
        catch (Exception ex)
        {
            ResultText.Text = "Failed: " + ex.Message;
        }
    }

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(MenuInspector.Format(_result));
            ResultText.Text = "Report copied to the clipboard.";
        }
        catch (Exception)
        {
            ResultText.Text = "The clipboard was busy; try again.";
        }
    }
}
