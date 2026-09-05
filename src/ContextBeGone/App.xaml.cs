using System.Windows;
using System.Windows.Threading;
using ContextBeGone.Services;

namespace ContextBeGone;

public partial class App : Application
{
    /// <summary>Set by <c>--inspect-ui &lt;path&gt;</c>: open straight into the inspector for this item.</summary>
    public static string? InspectOnStartup { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless mode: dump everything the scanner finds, then exit without showing a window.
        if (e.Args.Length > 0 && e.Args[0] is "--report" or "/report")
        {
            var output = e.Args.Length > 1
                ? e.Args[1]
                : System.IO.Path.Combine(Environment.CurrentDirectory, "context-menu-report.txt");

            SelfTest.WriteReport(output);
            Shutdown();
            return;
        }

        // --probe <path> <outputJson>   (child process: loads third-party shell extensions)
        if (e.Args.Length >= 3 && e.Args[0] is "--probe" or "/probe")
        {
            Services.MenuInspector.RunProbe(e.Args[1], e.Args[2]);
            Shutdown();
            return;
        }

        // --inspect <path> [outputFile]
        if (e.Args.Length >= 2 && e.Args[0] is "--inspect" or "/inspect")
        {
            var output = e.Args.Length > 2
                ? e.Args[2]
                : System.IO.Path.Combine(Environment.CurrentDirectory, "context-menu-inspect.txt");

            var inspection = Services.MenuInspector.Inspect(e.Args[1], TimeSpan.FromSeconds(45));
            System.IO.File.WriteAllText(output, Services.MenuInspector.Format(inspection));
            Shutdown();
            return;
        }

        // --inspect-ui <path>: open the window with the inspector already showing that item
        if (e.Args.Length >= 2 && e.Args[0] is "--inspect-ui" or "/inspect-ui")
        {
            InspectOnStartup = e.Args[1];
        }

        // --search <term> [outputFile]
        if (e.Args.Length >= 2 && e.Args[0] is "--search" or "/search")
        {
            var output = e.Args.Length > 2
                ? e.Args[2]
                : System.IO.Path.Combine(Environment.CurrentDirectory, "context-menu-search.txt");

            SelfTest.Search(e.Args[1], output);
            Shutdown();
            return;
        }

        // --toggle <classesPath> <keyName> <on|off> [inplace] [outputFile]
        if (e.Args.Length >= 4 && e.Args[0] is "--toggle" or "/toggle")
        {
            var inPlace = e.Args.Any(a => a.Equals("inplace", StringComparison.OrdinalIgnoreCase));
            var output = e.Args.Length > 4 && !e.Args[4].Equals("inplace", StringComparison.OrdinalIgnoreCase)
                ? e.Args[4]
                : System.IO.Path.Combine(Environment.CurrentDirectory, "context-menu-toggle.txt");

            SelfTest.Toggle(e.Args[1], e.Args[2],
                            enable: e.Args[3].Equals("on", StringComparison.OrdinalIgnoreCase),
                            inPlace, output);
            Shutdown();
            return;
        }

        // Several tooltips here are full sentences; five seconds is not enough to read them.
        System.Windows.Controls.ToolTipService.ShowDurationProperty.OverrideMetadata(
            typeof(DependencyObject), new FrameworkPropertyMetadata(30000));

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        BackupService.Log("UNHANDLED: " + e.Exception);
        MessageBox.Show(
            e.Exception.Message + "\n\nNothing was left half-applied; check the log in the Backups folder.",
            "ContextBeGone", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
