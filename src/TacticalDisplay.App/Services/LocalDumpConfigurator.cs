using Microsoft.Win32;
using System.IO;

namespace TacticalDisplay.App.Services;

public static class LocalDumpConfigurator
{
    private const string LogSource = "LocalDumps";
    private const string DumpKeyPath = @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\TacticalDisplay.App.exe";

    public static void EnsureLocalDumpsConfigured()
    {
        try
        {
            var dumpFolder = Path.Combine(DataSourceDebugLog.CurrentLogDirectoryPath, "crashdumps");
            Directory.CreateDirectory(dumpFolder);

            using var key = Registry.CurrentUser.CreateSubKey(DumpKeyPath, writable: true);
            if (key is null)
            {
                DataSourceDebugLog.Important(LogSource, "Failed to open HKCU LocalDumps registry key");
                return;
            }

            key.SetValue("DumpFolder", dumpFolder, RegistryValueKind.ExpandString);
            key.SetValue("DumpType", 2, RegistryValueKind.DWord);
            key.SetValue("DumpCount", 10, RegistryValueKind.DWord);

            DataSourceDebugLog.Important(
                LogSource,
                $"LocalDumps configured | key=HKCU\\{DumpKeyPath} folder={dumpFolder} type=2 count=10");
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Important(
                LogSource,
                $"Failed to configure LocalDumps | {ex.GetType().Name}: {ex.Message}");
        }
    }
}
