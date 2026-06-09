using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.Services;

public sealed class DebugReportUploadService
{
    private const string EndpointUrl = "https://vtsd-telemetry.vercel.app/api/debug-upload";
    private const string IngestKeyHeaderName = "X-VTSD-Ingest-Key";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly string _appVersion;
    private readonly TacticalDisplaySettings _settings;
    private readonly TelemetryService _telemetryService;

    public DebugReportUploadService(
        string appVersion,
        TacticalDisplaySettings settings,
        TelemetryService telemetryService)
    {
        _appVersion = appVersion;
        _settings = settings;
        _telemetryService = telemetryService;
    }

    public async Task<PreparedDebugReport> PrepareAsync(
        DebugReportOptions options,
        CancellationToken cancellationToken)
    {
        var metadata = new DebugReportMetadata(
            _telemetryService.GetOrCreateInstallationId(),
            _appVersion,
            DateTimeOffset.UtcNow,
            options.UserDescription.Trim(),
            options.IncludeLogs,
            options.IncludeSettings,
            options.IncludeDiagnostics,
            true);

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"vtsd-debug-report-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");

        var entries = new List<DebugReportEntry>();
        using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            await AddJsonEntryAsync(archive, "metadata.json", metadata, entries, cancellationToken).ConfigureAwait(false);

            if (options.IncludeDiagnostics)
            {
                await AddJsonEntryAsync(
                    archive,
                    "diagnostics.json",
                    TelemetryService.TelemetryDiagnostics.FromSettings(_settings),
                    entries,
                    cancellationToken).ConfigureAwait(false);
            }

            if (options.IncludeSettings)
            {
                await AddJsonEntryAsync(archive, "settings.json", _settings, entries, cancellationToken).ConfigureAwait(false);
            }

            if (options.IncludeLogs)
            {
                await AddLogEntriesAsync(archive, entries, cancellationToken).ConfigureAwait(false);
            }
        }

        var sizeBytes = new FileInfo(tempPath).Length;
        return new PreparedDebugReport(tempPath, metadata, entries, sizeBytes);
    }

    public async Task UploadAsync(PreparedDebugReport report, CancellationToken cancellationToken)
    {
        var ingestKey = _telemetryService.GetConfiguredIngestKey();
        if (string.IsNullOrWhiteSpace(ingestKey))
        {
            throw new InvalidOperationException("Debug report upload is not configured for this build.");
        }

        using var client = new HttpClient { Timeout = RequestTimeout };
        using var form = new MultipartFormDataContent();
        var metadataJson = JsonSerializer.Serialize(
            DebugReportUploadMetadata.FromReportMetadata(report.Metadata),
            JsonOptions);
        form.Add(new StringContent(metadataJson, Encoding.UTF8, "application/json"), "metadata");

        await using var fileStream = new FileStream(report.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(fileContent, "file", "debug-report.zip");

        using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
        request.Headers.Add(IngestKeyHeaderName, ingestKey.Trim());
        request.Content = form;

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = response.StatusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => "The debug report metadata or file was rejected by the server.",
            System.Net.HttpStatusCode.Unauthorized => "Debug report upload is not authorized for this build.",
            System.Net.HttpStatusCode.RequestEntityTooLarge => "The debug report is too large for the server.",
            System.Net.HttpStatusCode.InternalServerError => "The telemetry server failed while processing the debug report.",
            _ => $"The telemetry server returned HTTP {(int)response.StatusCode}."
        };

        throw new InvalidOperationException(message);
    }

    private static async Task AddJsonEntryAsync<T>(
        ZipArchive archive,
        string entryName,
        T value,
        List<DebugReportEntry> entries,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await entryStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        entries.Add(new DebugReportEntry(entryName, bytes.Length));
    }

    private static async Task AddLogEntriesAsync(
        ZipArchive archive,
        List<DebugReportEntry> entries,
        CancellationToken cancellationToken)
    {
        var logDirectory = DataSourceDebugLog.CurrentLogDirectoryPath;
        if (!Directory.Exists(logDirectory))
        {
            return;
        }

        foreach (var logFile in Directory.EnumerateFiles(logDirectory)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var fileName = Path.GetFileName(logFile);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var entryName = $"logs/{fileName}";
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            long copiedBytes;
            await using (var source = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            await using (var target = entry.Open())
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                copiedBytes = source.Length;
            }

            entries.Add(new DebugReportEntry(entryName, copiedBytes));
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

public sealed record DebugReportOptions(
    bool IncludeLogs,
    bool IncludeSettings,
    bool IncludeDiagnostics,
    string UserDescription);

public sealed record PreparedDebugReport(
    string ZipPath,
    DebugReportMetadata Metadata,
    IReadOnlyList<DebugReportEntry> Entries,
    long SizeBytes) : IDisposable
{
    public void Dispose()
    {
        try
        {
            if (File.Exists(ZipPath))
            {
                File.Delete(ZipPath);
            }
        }
        catch
        {
        }
    }
}

public sealed record DebugReportEntry(string Path, long SizeBytes);

public sealed record DebugReportMetadata(
    string InstallationId,
    string AppVersion,
    DateTimeOffset CreatedAtUtc,
    string UserDescription,
    bool IncludeLogs,
    bool IncludeSettings,
    bool IncludeDiagnostics,
    bool RawUpload);

public sealed record DebugReportUploadMetadata(
    string InstallationId,
    string AppVersion,
    string UserDescription,
    bool IncludeLogs,
    bool IncludeSettings,
    bool IncludeDiagnostics,
    bool RawUpload)
{
    public static DebugReportUploadMetadata FromReportMetadata(DebugReportMetadata metadata) =>
        new(
            metadata.InstallationId,
            metadata.AppVersion,
            metadata.UserDescription,
            metadata.IncludeLogs,
            metadata.IncludeSettings,
            metadata.IncludeDiagnostics,
            metadata.RawUpload);
}
