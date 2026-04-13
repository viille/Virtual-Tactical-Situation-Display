using System.Collections.Concurrent;
using System.IO;

namespace TacticalDisplay.App.Services;

public static class DataSourceDebugLog
{
    private static readonly object Sync = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastWriteByKey = new(StringComparer.Ordinal);
    private static readonly string LogFilePath = ResolveLogFilePath();
    private const long MaxLogBytes = 1_000_000;
    private static volatile bool _isEnabled;

    public static string CurrentLogFilePath => LogFilePath;
    public static string CurrentLogDirectoryPath => Path.GetDirectoryName(LogFilePath)!;
    public static bool IsEnabled => _isEnabled;

    public static void SetEnabled(bool enabled)
    {
        _isEnabled = enabled;
    }

    public static void EnsureLogDirectoryExists()
    {
        Directory.CreateDirectory(CurrentLogDirectoryPath);
    }

    public static void Debug(string source, string message) => Write("DEBUG", source, message);
    public static void Info(string source, string message) => Write("INFO", source, message);
    public static void Warn(string source, string message) => Write("WARN", source, message);
    public static void Error(string source, string message, Exception? ex = null)
    {
        var details = ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}";
        Write("ERROR", source, details);
    }

    public static void ThrottledDebug(string source, string key, TimeSpan interval, Func<string> messageFactory)
    {
        var now = DateTimeOffset.UtcNow;
        if (LastWriteByKey.TryGetValue(key, out var lastWrite) && now - lastWrite < interval)
        {
            return;
        }

        LastWriteByKey[key] = now;
        Debug(source, messageFactory());
    }

    private static void Write(string level, string source, string message)
    {
        if (!_isEnabled)
        {
            return;
        }

        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{source}] {message}{Environment.NewLine}";
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
                RotateIfNeeded();
                File.AppendAllText(LogFilePath, line);
            }
        }
        catch
        {
            // Debug logging must never break the data feed.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogFilePath))
        {
            return;
        }

        var info = new FileInfo(LogFilePath);
        if (info.Length < MaxLogBytes)
        {
            return;
        }

        var archivePath = Path.Combine(
            Path.GetDirectoryName(LogFilePath)!,
            $"debug-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        File.Move(LogFilePath, archivePath);
    }

    private static string ResolveLogFilePath()
    {
        return AppDataPaths.DataSourceDebugLogFilePath;
    }
}
