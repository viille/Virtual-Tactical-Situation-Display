using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.Services;

public sealed class DebugReportUploadService
{
    private const string HandleUploadUrl = "https://vtsd-telemetry.vercel.app/api/debug-upload/client";
    private const string BlobApiUrl = "https://vercel.com/api/blob";
    private const string BlobPathname = "debug-reports/debug-report.zip";
    private const int MultipartPartSize = 8 * 1024 * 1024;
    private const string IngestKeyHeaderName = "X-VTSD-Ingest-Key";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

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
        var metadataJson = JsonSerializer.Serialize(
            DebugReportUploadMetadata.FromReportMetadata(report.Metadata),
            JsonOptions);
        var clientPayload = JsonSerializer.Serialize(new
        {
            metadata = JsonSerializer.Deserialize<JsonElement>(metadataJson, JsonOptions),
            file = new
            {
                fileName = "debug-report.zip",
                contentType = "application/zip",
                sizeBytes = report.SizeBytes
            }
        }, JsonOptions);

        var tokenRequest = new
        {
            type = "blob.generate-client-token",
            payload = new
            {
                pathname = "debug-reports/debug-report.zip",
                clientPayload,
                multipart = true
            }
        };
        using var tokenMessage = new HttpRequestMessage(HttpMethod.Post, HandleUploadUrl)
        {
            Content = JsonContent.Create(tokenRequest, options: JsonOptions)
        };
        tokenMessage.Headers.Add(IngestKeyHeaderName, ingestKey.Trim());
        using var tokenResponse = await client.SendAsync(tokenMessage, cancellationToken).ConfigureAwait(false);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The telemetry server could not authorize the debug report upload.");
        }

        var token = (await tokenResponse.Content.ReadFromJsonAsync<ClientTokenResponse>(JsonOptions, cancellationToken).ConfigureAwait(false))?.ClientToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("The telemetry server returned an invalid Blob upload token.");
        }

        var storeId = GetStoreId(token);
        var commonHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {token}",
            ["x-vercel-blob-store-id"] = storeId,
            ["x-vercel-blob-access"] = "private",
            ["x-content-type"] = "application/zip"
        };

        var query = $"pathname={Uri.EscapeDataString(BlobPathname)}";
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"{BlobApiUrl}/mpu?{query}");
        AddHeaders(createRequest, commonHeaders);
        createRequest.Headers.Add("x-mpu-action", "create");
        using var createResponse = await client.SendAsync(createRequest, cancellationToken).ConfigureAwait(false);
        await EnsureBlobSuccessAsync(createResponse, "create multipart upload").ConfigureAwait(false);
        var upload = await createResponse.Content.ReadFromJsonAsync<MultipartUploadResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Blob did not return multipart upload details.");

        var parts = new List<CompletedPart>();
        await using var fileStream = new FileStream(report.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[MultipartPartSize];
        var partNumber = 1;
        int bytesRead;
        while ((bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            using var partContent = new ByteArrayContent(buffer, 0, bytesRead);
            using var partRequest = new HttpRequestMessage(HttpMethod.Post, $"{BlobApiUrl}/mpu?{query}") { Content = partContent };
            AddHeaders(partRequest, commonHeaders);
            partRequest.Headers.Add("x-mpu-action", "upload");
            partRequest.Headers.Add("x-mpu-key", Uri.EscapeDataString(upload.Key));
            partRequest.Headers.Add("x-mpu-upload-id", upload.UploadId);
            partRequest.Headers.Add("x-mpu-part-number", partNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var partResponse = await client.SendAsync(partRequest, cancellationToken).ConfigureAwait(false);
            await EnsureBlobSuccessAsync(partResponse, $"upload multipart part {partNumber}").ConfigureAwait(false);
            var partResult = await partResponse.Content.ReadFromJsonAsync<MultipartPartResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Blob did not return a multipart part etag.");
            parts.Add(new CompletedPart(partNumber, partResult.Etag));
            partNumber++;
        }

        using var completeRequest = new HttpRequestMessage(HttpMethod.Post, $"{BlobApiUrl}/mpu?{query}")
        {
            Content = JsonContent.Create(parts, options: JsonOptions)
        };
        AddHeaders(completeRequest, commonHeaders);
        completeRequest.Headers.Add("x-mpu-action", "complete");
        completeRequest.Headers.Add("x-mpu-key", Uri.EscapeDataString(upload.Key));
        completeRequest.Headers.Add("x-mpu-upload-id", upload.UploadId);
        using var response = await client.SendAsync(completeRequest, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;

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

    private static string GetStoreId(string clientToken)
    {
        var segments = clientToken.Split('_');
        return segments.Length > 3 && !string.IsNullOrWhiteSpace(segments[3])
            ? segments[3]
            : throw new InvalidOperationException("The Blob client token did not contain a store id.");
    }

    private static void AddHeaders(HttpRequestMessage request, IEnumerable<KeyValuePair<string, string>> headers)
    {
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static async Task EnsureBlobSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw new InvalidOperationException($"Blob failed to {operation} (HTTP {(int)response.StatusCode}): {detail}");
    }

    private sealed record ClientTokenResponse(string ClientToken);
    private sealed record MultipartUploadResponse(string Key, string UploadId);
    private sealed record MultipartPartResponse(string Etag);
    private sealed record CompletedPart(int PartNumber, string Etag);

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

        foreach (var logFile in Directory.EnumerateFiles(logDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var relativePath = Path.GetRelativePath(logDirectory, logFile);
            if (string.IsNullOrWhiteSpace(relativePath) ||
                relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                continue;
            }

            var entryName = $"logs/{relativePath.Replace('\\', '/')}";
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
