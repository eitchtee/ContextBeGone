using System.Windows;
using ContextBeGone.Services;

namespace ContextBeGone.Views;

public partial class AddFileTypeWindow : Window
{
    /// <summary>The normalised extension, e.g. ".png". Only set when the dialog is accepted.</summary>
    public string Extension { get; private set; } = string.Empty;

    public AddFileTypeWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ExtensionBox.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.EnableDarkTitleBar(this);
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var extension = Settings.NormaliseExtension(ExtensionBox.Text);
        if (extension is null)
        {
            MessageBox.Show(
                "That does not look like a file extension. Try something like .png",
                "Add a file type", MessageBoxButton.OK, MessageBoxImage.Warning);
            ExtensionBox.Focus();
            ExtensionBox.SelectAll();
            return;
        }

        Extension = extension;
        DialogResult = true;
    }
}
