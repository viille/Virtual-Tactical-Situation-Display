using System.Diagnostics.Eventing.Reader;
using System.IO;

namespace TacticalDisplay.App.Services;

public static class WindowsEventLogCollector
{
    private const string LogSource = "WindowsEventLog";
    private const string ApplicationLogName = "Application";
    private const string FaultProviderName = "Application Error";

    public static string? CaptureLatestCrash()
    {
        try
        {
            var query = new EventLogQuery(
                ApplicationLogName,
                PathType.LogName,
                "*[System[Provider[@Name='Application Error'] and (EventID=1000)]]")
            {
                ReverseDirection = true
            };

            using var reader = new EventLogReader(query);
            for (var i = 0; i < 25; i++)
            {
                using var record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }

                var message = record.FormatDescription();
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                if (!message.Contains("TacticalDisplay.App.exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parsed = ParseApplicationErrorMessage(message);
                var summary = $"Faulting module={parsed.FaultingModule ?? "unknown"} exception={parsed.ExceptionCode ?? "unknown"} offset={parsed.FaultOffset ?? "unknown"}";

                DataSourceDebugLog.Important(
                    LogSource,
                    $"Latest Application Error event | time={record.TimeCreated:yyyy-MM-dd HH:mm:ss zzz} {summary}");

                return
                    $"Windows Event Log crash detected:{Environment.NewLine}" +
                    $"Time: {record.TimeCreated:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}" +
                    $"Faulting module: {parsed.FaultingModule ?? "unknown"}{Environment.NewLine}" +
                    $"Exception code: {parsed.ExceptionCode ?? "unknown"}{Environment.NewLine}" +
                    $"Fault offset: {parsed.FaultOffset ?? "unknown"}";
            }
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Important(
                LogSource,
                $"Failed to read Windows Event Log | {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }

    private static (string? FaultingModule, string? ExceptionCode, string? FaultOffset) ParseApplicationErrorMessage(string message)
    {
        string? faultingModule = null;
        string? exceptionCode = null;
        string? faultOffset = null;

        using var reader = new StringReader(message);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Faulting module name", StringComparison.OrdinalIgnoreCase))
            {
                faultingModule = ExtractAfterColon(trimmed);
                continue;
            }

            if (trimmed.StartsWith("Exception code", StringComparison.OrdinalIgnoreCase))
            {
                exceptionCode = ExtractAfterColon(trimmed);
                continue;
            }

            if (trimmed.StartsWith("Fault offset", StringComparison.OrdinalIgnoreCase))
            {
                faultOffset = ExtractAfterColon(trimmed);
            }
        }

        return (faultingModule, exceptionCode, faultOffset);
    }

    private static string? ExtractAfterColon(string value)
    {
        var index = value.IndexOf(':');
        if (index < 0 || index == value.Length - 1)
        {
            return null;
        }

        return value[(index + 1)..].Trim();
    }
}
