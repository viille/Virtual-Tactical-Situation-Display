using System.IO;

namespace TacticalDisplay.App.Services;

public static class WindowsErrorReportCollector
{
    private const string LogSource = "WindowsErrorReporting";
    private const int MaxReportsToLog = 5;

    public static string? LogExistingReports()
    {
        try
        {
            var messages = new List<string>();
            AddCrashLogMessageIfNeeded(messages);
            var fallbackCrashLogCopyPath = CopyFallbackCrashLogToLogDirectoryIfExists();
            var reports = FindReports()
                .OrderByDescending(report => report.LastWriteTimeUtc)
                .Take(MaxReportsToLog)
                .ToArray();

            if (fallbackCrashLogCopyPath is not null)
            {
                messages.Add($"A previous fallback crash log was copied to:{Environment.NewLine}{fallbackCrashLogCopyPath}");
            }

            if (reports.Length > 0)
            {
                var crashDumpCopyPath = CopyLatestReportToLogDirectory(reports[0]);
                if (crashDumpCopyPath is not null)
                {
                    messages.Add($"A Windows crash dump/report was copied to:{Environment.NewLine}{crashDumpCopyPath}");
                }
            }

            var eventLogMessage = WindowsEventLogCollector.CaptureLatestCrash();
            if (!string.IsNullOrWhiteSpace(eventLogMessage))
            {
                messages.Add(eventLogMessage);
            }

            foreach (var report in reports)
            {
                DataSourceDebugLog.Important(
                    LogSource,
                    $"Found crash dump/report | path={report.Path} sizeBytes={report.Length} modified={report.LastWriteTime:yyyy-MM-dd HH:mm:ss zzz}");
            }

            return messages.Count == 0
                ? null
                : string.Join($"{Environment.NewLine}{Environment.NewLine}", messages);
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Important(LogSource, $"Crash dump/report scan failed | {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void AddCrashLogMessageIfNeeded(ICollection<string> messages)
    {
        var logPath = DataSourceDebugLog.CurrentLogFilePath;
        if (!File.Exists(logPath))
        {
            return;
        }

        try
        {
            var crashLineCount = 0;
            string? latestCrashLine = null;
            foreach (var line in File.ReadLines(logPath))
            {
                if (!line.Contains("[CRASH]", StringComparison.Ordinal))
                {
                    continue;
                }

                crashLineCount++;
                latestCrashLine = line;
            }

            if (crashLineCount == 0)
            {
                return;
            }

            DataSourceDebugLog.Important(
                LogSource,
                $"Crash entries found in debug log | path={logPath} count={crashLineCount} latest={latestCrashLine}");
            messages.Add(
                $"Crash entries were found in the debug log:{Environment.NewLine}{logPath}{Environment.NewLine}Latest: {latestCrashLine}");
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Important(
                LogSource,
                $"Crash log scan failed | path={logPath} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? CopyFallbackCrashLogToLogDirectoryIfExists()
    {
        var sourcePath = DataSourceDebugLog.CurrentFallbackCrashLogFilePath;
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(DataSourceDebugLog.CurrentLogDirectoryPath);
            var sourceInfo = new FileInfo(sourcePath);
            var copyPath = Path.Combine(
                DataSourceDebugLog.CurrentLogDirectoryPath,
                $"{sourceInfo.LastWriteTime:yyyyMMdd-HHmmss}-TacticalDisplay-crash.log");

            if (!File.Exists(copyPath))
            {
                File.Copy(sourcePath, copyPath, overwrite: false);
            }

            var copyInfo = new FileInfo(copyPath);
            DataSourceDebugLog.Important(
                LogSource,
                $"Copied fallback crash log | source={sourceInfo.FullName} copy={copyInfo.FullName} sizeBytes={copyInfo.Length}");

            return copyInfo.FullName;
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Important(
                LogSource,
                $"Fallback crash log copy failed | source={sourcePath} error={ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string? CopyLatestReportToLogDirectory(ReportFile report)
    {
        try
        {
            Directory.CreateDirectory(DataSourceDebugLog.CurrentLogDirectoryPath);
            var copyPath = Path.Combine(
                DataSourceDebugLog.CurrentLogDirectoryPath,
                $"{report.LastWriteTime:yyyyMMdd-HHmmss}-crashdumb.dmp");

            if (!File.Exists(copyPath))
            {
                File.Copy(report.Path, copyPath, overwrite: false);
            }

            var copyInfo = new FileInfo(copyPath);
            DataSourceDebugLog.Important(
                LogSource,
                $"Copied latest crash dump/report | source={report.Path} copy={copyInfo.FullName} sizeBytes={copyInfo.Length}");
            return copyInfo.FullName;
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Important(
                LogSource,
                $"Crash dump/report copy failed | source={report.Path} error={ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static IEnumerable<ReportFile> FindReports()
    {
        var processPath = Environment.ProcessPath;
        var executableName = string.IsNullOrWhiteSpace(processPath)
            ? "TacticalDisplay.App.exe"
            : Path.GetFileName(processPath);
        var executableBaseName = Path.GetFileNameWithoutExtension(executableName);

        foreach (var file in EnumerateCrashDumpFiles(executableName, executableBaseName))
        {
            yield return file;
        }

        foreach (var file in EnumerateWerFiles(executableBaseName))
        {
            yield return file;
        }
    }

    private static IEnumerable<ReportFile> EnumerateCrashDumpFiles(string executableName, string executableBaseName)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            yield break;
        }

        var crashDumpDirectory = Path.Combine(localAppData, "CrashDumps");
        foreach (var file in EnumerateFiles(crashDumpDirectory, $"{executableName}*.dmp", SearchOption.TopDirectoryOnly)
                     .Concat(EnumerateFiles(crashDumpDirectory, $"{executableBaseName}*.dmp", SearchOption.TopDirectoryOnly)))
        {
            yield return file;
        }
    }

    private static IEnumerable<ReportFile> EnumerateWerFiles(string executableBaseName)
    {
        foreach (var root in EnumerateWerRoots())
        {
            foreach (var directory in EnumerateDirectories(root, $"*{executableBaseName}*", SearchOption.TopDirectoryOnly)
                         .Concat(EnumerateDirectories(root, "*TacticalDisplay*", SearchOption.TopDirectoryOnly)))
            {
                foreach (var file in EnumerateFiles(directory, "*.dmp", SearchOption.AllDirectories)
                             .Concat(EnumerateFiles(directory, "*.mdmp", SearchOption.AllDirectories))
                             .Concat(EnumerateFiles(directory, "*.hdmp", SearchOption.AllDirectories)))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateWerRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive");
            yield return Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue");
            yield return Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportUpload");
        }

        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(commonAppData))
        {
            yield return Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "ReportArchive");
            yield return Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "ReportQueue");
            yield return Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "ReportUpload");
        }
    }

    private static IEnumerable<ReportFile> EnumerateFiles(string directory, string searchPattern, SearchOption searchOption)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(directory, searchPattern, searchOption);
        }
        catch
        {
            yield break;
        }

        foreach (var path in paths)
        {
            ReportFile? report = null;
            try
            {
                var info = new FileInfo(path);
                report = new ReportFile(info.FullName, info.Length, info.LastWriteTime, info.LastWriteTimeUtc);
            }
            catch
            {
                // Ignore inaccessible WER files.
            }

            if (report is not null)
            {
                yield return report;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string directory, string searchPattern, SearchOption searchOption)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateDirectories(directory, searchPattern, searchOption);
        }
        catch
        {
            yield break;
        }

        foreach (var path in paths)
        {
            yield return path;
        }
    }

    private sealed record ReportFile(string Path, long Length, DateTime LastWriteTime, DateTime LastWriteTimeUtc);
}
