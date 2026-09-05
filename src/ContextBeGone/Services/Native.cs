using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ContextBeGone.Services;

/// <summary>P/Invoke surface used to resolve shell resource strings, icons and to notify the shell.</summary>
internal static class Native
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHDefExtractIconW(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>Paints the window's title bar dark so it matches the app's palette.</summary>
    public static void EnableDarkTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var enabled = 1;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        }
        catch (Exception)
        {
            // Cosmetic only; older builds simply ignore the attribute.
        }
    }

    /// <summary>Tell the shell that file associations changed so it drops its cached verbs.</summary>
    public static void NotifyAssociationsChanged() =>
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// Resolves an indirect string such as <c>@%SystemRoot%\system32\shell32.dll,-8506</c>.
    /// Non-indirect input is returned unchanged.
    /// </summary>
    public static string ResolveDisplayString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        if (raw[0] != '@') return raw;

        var buffer = new StringBuilder(1024);
        try
        {
            if (SHLoadIndirectString(raw, buffer, buffer.Capacity, IntPtr.Zero) == 0 && buffer.Length > 0)
                return buffer.ToString();
        }
        catch (Exception)
        {
            // Fall through to the raw value; a broken resource reference must not break the scan.
        }
        return raw;
    }

    /// <summary>
    /// Parses an <c>Icon</c> / <c>DefaultIcon</c> spec (<c>"path",index</c>, <c>path,index</c>, <c>path</c>)
    /// and returns a 16px image, or null when it cannot be resolved.
    /// </summary>
    public static ImageSource? LoadIcon(string? iconSpec, int size = 16)
    {
        if (string.IsNullOrWhiteSpace(iconSpec)) return null;

        var spec = iconSpec.Trim();
        var index = 0;

        if (spec.StartsWith('"'))
        {
            var close = spec.IndexOf('"', 1);
            if (close < 0) return null;
            var rest = spec[(close + 1)..].TrimStart();
            spec = spec[1..close];
            if (rest.StartsWith(',')) int.TryParse(rest[1..].Trim(), out index);
        }
        else
        {
            var comma = spec.LastIndexOf(',');
            if (comma > 1 && int.TryParse(spec[(comma + 1)..].Trim(), out var parsed))
            {
                index = parsed;
                spec = spec[..comma];
            }
        }

        spec = Environment.ExpandEnvironmentVariables(spec.Trim().Trim('"'));
        if (spec.Length == 0) return null;

        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            // SHDefExtractIcon treats a negative index as a resource id, matching shell semantics.
            if (SHDefExtractIconW(spec, index, 0, out large, out small, (uint)size) != 0) return null;
            var handle = small != IntPtr.Zero ? small : large;
            if (handle == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero) DestroyIcon(small);
        }
    }
}
