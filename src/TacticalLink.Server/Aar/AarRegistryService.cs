using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.Extensions.Hosting;

namespace TacticalLink.Server.Aar;

public sealed class AarRegistryService : IAarRegistryProvider, IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<AarRegistryService> _logger;
    private readonly string _endpoint;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private AarRegistrySnapshot? _current;
    private string? _etag;
    private string? _status = "Registry has not been loaded.";

    public AarRegistryService(IHttpClientFactory clients, ILogger<AarRegistryService> logger)
        : this(clients, logger,
            Environment.GetEnvironmentVariable("TACTICAL_LINK_AAR_REGISTRY_URL")?.Trim() is { Length: > 0 } endpoint ? endpoint : "https://www.vtsd.app/api/v1/tactical-link/aar/registry",
            Environment.GetEnvironmentVariable("TACTICAL_LINK_AAR_REGISTRY_CACHE_PATH")?.Trim() is { Length: > 0 } path ? path : "/var/lib/tactical-link/aar-registry-cache.json")
    {
    }

    internal AarRegistryService(IHttpClientFactory clients, ILogger<AarRegistryService> logger, string endpoint, string cachePath)
    {
        _clients = clients;
        _logger = logger;
        _endpoint = endpoint;
        _cachePath = cachePath;
    }

    public AarRegistrySnapshot? Current => Volatile.Read(ref _current);
    public bool IsAvailable => Current is not null;
    public string? Status => Volatile.Read(ref _status);
    public event EventHandler? Changed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadCacheAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RefreshLoopAsync(_loopCts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _loopCts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (_loopCts?.IsCancellationRequested == true) { }
        }
        _loopCts?.Dispose();
        _loopCts = null;
        _loop = null;
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken)) await RefreshAsync(cancellationToken);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("AAR registry URL must be an absolute HTTPS endpoint.");
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            if (EntityTagHeaderValue.TryParse(_etag, out var etag)) request.Headers.IfNoneMatch.Add(etag);
            using var response = await _clients.CreateClient("AarRegistry").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                Volatile.Write(ref _status, $"Using registry v{Current?.Version}; Cloud confirmed it is current.");
                return;
            }
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var candidate = await JsonSerializer.DeserializeAsync<AarRegistrySnapshot>(body, Json, timeout.Token);
            if (!AarRegistryValidator.TryValidate(candidate, out var reason)) throw new InvalidDataException($"Cloud returned an invalid AAR registry: {reason}");
            var canonical = Canonicalize(candidate!);
            if (Current is { } active)
            {
                if (canonical.Version < active.Version) throw new InvalidDataException("Cloud registry version moved backwards.");
                if (canonical.Version == active.Version)
                {
                    if (!JsonSerializer.SerializeToUtf8Bytes(active, Json).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(canonical, Json)))
                        throw new InvalidDataException("An immutable published registry version changed its content.");
                    _etag = response.Headers.ETag?.ToString();
                    Volatile.Write(ref _status, $"Using registry v{active.Version}; Cloud returned the same immutable version.");
                    return;
                }
            }
            await PersistAsync(canonical, timeout.Token);
            Volatile.Write(ref _current, canonical);
            _etag = response.Headers.ETag?.ToString();
            Volatile.Write(ref _status, $"Using published registry v{canonical.Version}.");
            Changed?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation("Activated AAR registry version {Version} with {ProfileCount} profiles", canonical.Version, canonical.Profiles.Count);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _status, Current is null ? "Cloud refresh timed out; no valid local registry is available." : $"Cloud refresh timed out; retaining registry v{Current.Version}.");
            _logger.LogWarning("AAR registry refresh timed out");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
        {
            Volatile.Write(ref _status, Current is null ? "Cloud is unavailable and no valid local registry is available." : $"Cloud refresh failed; retaining registry v{Current.Version}.");
            _logger.LogWarning(ex, "AAR registry refresh failed; retaining last-known-good snapshot");
        }
        finally { _refreshLock.Release(); }
    }

    internal async Task LoadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            await using var stream = File.OpenRead(_cachePath);
            var cached = await JsonSerializer.DeserializeAsync<CacheFile>(stream, Json, cancellationToken);
            if (cached is null) throw new InvalidDataException("Cached AAR registry file is empty.");
            if (cached.CacheVersion != 1) throw new InvalidDataException("Cached AAR registry cache schema is unsupported.");
            if (!AarRegistryValidator.TryValidate(cached.Snapshot, out var reason)) throw new InvalidDataException($"Cached AAR registry is invalid: {reason}");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Canonicalize(cached.Snapshot!), Json);
            var checksum = Convert.ToHexString(SHA256.HashData(bytes));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(checksum), Encoding.ASCII.GetBytes(cached.Sha256 ?? string.Empty)))
                throw new InvalidDataException("Cached AAR registry checksum does not match.");
            Volatile.Write(ref _current, Canonicalize(cached.Snapshot!));
            Volatile.Write(ref _status, $"Using persisted last-known-good registry v{cached.Snapshot!.Version}.");
            Changed?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation("Loaded persisted AAR registry v{Version}", cached.Snapshot.Version);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            Volatile.Write(ref _status, "Local registry cache is invalid; waiting for Cloud refresh.");
            _logger.LogWarning(ex, "Ignoring invalid persisted AAR registry cache");
        }

    }

    internal async Task PersistAsync(AarRegistrySnapshot snapshot, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_cachePath)) ?? throw new InvalidOperationException("Registry cache directory is invalid.");
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        var envelope = new CacheFile(1, Convert.ToHexString(SHA256.HashData(bytes)), snapshot);
        var tempPath = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, Json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tempPath, _cachePath, true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static AarRegistrySnapshot Canonicalize(AarRegistrySnapshot snapshot) => snapshot with
    {
        Profiles = snapshot.Profiles.OrderBy(profile => profile.ProfileKey, StringComparer.Ordinal).Select(profile => profile with
        {
            IcaoDesignators = profile.IcaoDesignators.Select(code => code.Trim().ToUpperInvariant()).Order(StringComparer.Ordinal).ToArray(),
            TankerSystems = profile.TankerSystems.OrderBy(system => system.Method, StringComparer.Ordinal).ToArray(),
            ReceiverSystems = profile.ReceiverSystems.OrderBy(system => system.Method, StringComparer.Ordinal).ToArray(),
            Sources = profile.Sources.OrderBy(source => source.Id, StringComparer.Ordinal).ToArray(),
        }).ToArray(),
    };

    public void Dispose()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _refreshLock.Dispose();
    }

    private sealed record CacheFile(int CacheVersion, string? Sha256, AarRegistrySnapshot? Snapshot);
}
