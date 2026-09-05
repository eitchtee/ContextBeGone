using System.Windows;
using System.Windows.Controls;
using ContextBeGone.Models;
using ContextBeGone.Services;

namespace ContextBeGone.Views;

public partial class NewEntryWindow : Window
{
    public string KeyName { get; private set; } = string.Empty;
    public string Label { get; private set; } = string.Empty;
    public string Command { get; private set; } = string.Empty;
    public string IconSpec { get; private set; } = string.Empty;
    public string Position { get; private set; } = string.Empty;
    public bool Extended { get; private set; }

    public NewEntryWindow(Scene scene)
    {
        InitializeComponent();
        ScopeText.Text = $"The entry will appear in: {scene.Name} — {scene.Description}";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.EnableDarkTitleBar(this);
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        KeyName = KeyNameBox.Text.Trim();
        Label = LabelBox.Text.Trim();
        Command = CommandBox.Text.Trim();
        IconSpec = IconBox.Text.Trim();
        Position = (PositionBox.SelectedItem as ComboBoxItem)?.Content as string ?? string.Empty;
        Extended = ExtendedBox.IsChecked == true;

        if (KeyName.Length == 0 || KeyName.Contains('\\'))
        {
            MessageBox.Show("Give the entry a key name without backslashes.", "New entry",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Command.Length == 0)
        {
            MessageBox.Show("A command is required, otherwise the menu item would do nothing.", "New entry",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
