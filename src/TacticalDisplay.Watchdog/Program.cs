using System.Diagnostics;

const string watchdogEnvironmentVariable = "TACTICALDISPLAY_WATCHDOG_ACTIVE";
const string applicationDirectoryName = "VirtualTacticalSituationDisplay";

try
{
    if (args.Length < 2)
    {
        return 2;
    }

    if (!int.TryParse(args[0], out var parentProcessId))
    {
        return 3;
    }

    var appExePath = Path.GetFullPath(args[1]);
    if (!File.Exists(appExePath))
    {
        return 4;
    }

    WaitForProcessExit(parentProcessId);

    using var appProcess = StartApp(appExePath, args.Skip(2));
    if (appProcess is null)
    {
        WriteCrashLog($"Failed to start watched app | app={appExePath}");
        return 5;
    }

    appProcess.WaitForExit();
    if (appProcess.ExitCode != 0)
    {
        WriteCrashLog(
            $"Watched app exited unexpectedly | app={appExePath} pid={appProcess.Id} exitCode={appProcess.ExitCode} exitTime={appProcess.ExitTime:yyyy-MM-dd HH:mm:ss.fff zzz}");
    }

    return appProcess.ExitCode;
}
catch (Exception ex)
{
    WriteCrashLog($"Watchdog failed | {ex}");
    return 1;
}

static void WaitForProcessExit(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        if (!process.HasExited)
        {
            process.WaitForExit(30_000);
        }
    }
    catch
    {
        // The bootstrap app process may already be gone.
    }
}

static Process? StartApp(string appExePath, IEnumerable<string> appArguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = appExePath,
        WorkingDirectory = Path.GetDirectoryName(appExePath),
        UseShellExecute = false
    };
    startInfo.Environment[watchdogEnvironmentVariable] = "1";
    foreach (var argument in appArguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    return Process.Start(startInfo);
}

static void WriteCrashLog(string message)
{
    var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [CRASH] [Watchdog] {message}{Environment.NewLine}";
    var logPath = Path.Combine(GetApplicationDataDirectory(), "logs", "debug.log");
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream);
        writer.Write(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }
    catch
    {
        WriteFallbackCrashLog(line);
    }
}

static void WriteFallbackCrashLog(string line)
{
    try
    {
        var fallbackPath = Path.Combine(Path.GetTempPath(), "TacticalDisplay-crash.log");
        using var stream = new FileStream(fallbackPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream);
        writer.Write(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }
    catch
    {
    }
}

static string GetApplicationDataDirectory()
{
    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    return Path.Combine(string.IsNullOrWhiteSpace(appData) ? Path.GetTempPath() : appData, applicationDirectoryName);
}
