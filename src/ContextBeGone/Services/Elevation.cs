using System.Diagnostics;
using System.Security.Principal;

namespace ContextBeGone.Services;

public static class Elevation
{
    private static bool? _cached;

    public static bool IsElevated
    {
        get
        {
            if (_cached is not null) return _cached.Value;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                _cached = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                _cached = false;
            }
            return _cached.Value;
        }
    }

    /// <summary>Relaunches this process with a UAC prompt. Returns false if the user declined.</summary>
    public static bool RestartElevated()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return false;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Exception)
        {
            // Most commonly ERROR_CANCELLED: the user dismissed the UAC prompt.
            return false;
        }
    }
}
