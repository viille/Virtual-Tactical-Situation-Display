using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace TacticalDisplay.App.Services;

public static class AppWatchdogLauncher
{
    private const string WatchdogEnvironmentVariable = "TACTICALDISPLAY_WATCHDOG_ACTIVE";
    private const string EmbeddedWatchdogResourceName = "tacticaldisplay.watchdog.exe";

    public static bool TryRelaunchUnderWatchdog(string[] appArguments)
    {
        if (string.Equals(Environment.GetEnvironmentVariable(WatchdogEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            return false;
        }

        var currentExePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(currentExePath) || !File.Exists(currentExePath))
        {
            return false;
        }

        var watchdogExePath = Path.Combine(
            Path.GetTempPath(),
            "VirtualTacticalSituationDisplay",
            "watchdog",
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "current",
            "TacticalDisplay.Watchdog.exe");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(watchdogExePath)!);
            if (!TryExtractWatchdog(watchdogExePath))
            {
                DataSourceDebugLog.Important("Watchdog", "Failed to extract embedded watchdog; continuing without watchdog");
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = watchdogExePath,
                WorkingDirectory = Path.GetDirectoryName(currentExePath),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add(currentExePath);
            foreach (var argument in appArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
            DataSourceDebugLog.Important("Watchdog", $"Relaunched under watchdog | watchdog={watchdogExePath} app={currentExePath}");
            return true;
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Important("Watchdog", $"Failed to launch watchdog | {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryExtractWatchdog(string targetPath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedWatchdogResourceName);
        if (stream is not null)
        {
            using var manifestTarget = File.Create(targetPath);
            stream.CopyTo(manifestTarget);
            return true;
        }

        var resource = Application.GetResourceStream(new Uri(EmbeddedWatchdogResourceName, UriKind.Relative));
        if (resource is null)
        {
            return false;
        }

        using var target = File.Create(targetPath);
        resource.Stream.CopyTo(target);
        return true;
    }
}
