using System.IO;

namespace TacticalDisplay.App.Services;

public static class WindowsErrorReportCollector
{
    private const string LogSource = "WindowsErrorReporting";
    private const int MaxReportsToLog = 5;

    public static void LogExistingReports()
    {
        try
        {
            var reports = FindReports()
                .OrderByDescending(report => report.LastWriteTimeUtc)
                .Take(MaxReportsToLog)
                .ToArray();

            if (reports.Length > 0)
            {
                CopyLatestReportToLogDirectory(reports[0]);
            }

            foreach (var report in reports)
            {
                DataSourceDebugLog.Important(
                    LogSource,
                    $"Found crash dump/report | path={report.Path} sizeBytes={report.Length} modified={report.LastWriteTime:yyyy-MM-dd HH:mm:ss zzz}");
            }
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Important(LogSource, $"Crash dump/report scan failed | {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CopyLatestReportToLogDirectory(ReportFile report)
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
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Important(
                LogSource,
                $"Crash dump/report copy failed | source={report.Path} error={ex.GetType().Name}: {ex.Message}");
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
