using System.Runtime.InteropServices;

namespace VirtualTacticalSituationDisplay;

internal static class WindowsAppIdentity
{
    public const string AppUserModelId = "VirtualTacticalSituationDisplay.Desktop";

    public static void SetForCurrentProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
