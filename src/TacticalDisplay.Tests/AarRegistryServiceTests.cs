using Microsoft.Extensions.Logging.Abstractions;
using TacticalLink.Server.Aar;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class AarRegistryServiceTests
{
    [Fact]
    public async Task PersistedSnapshotIsValidatedAndLoadedAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aar-registry-test-" + Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(directory, "cache.json");
        try
        {
            var snapshot = Snapshot();
            using var clients = new EmptyHttpClientFactory();
            using var first = Service(clients, cachePath);
            await first.PersistAsync(snapshot, CancellationToken.None);

            using var restarted = Service(clients, cachePath);
            await restarted.LoadCacheAsync(CancellationToken.None);

            Assert.Equal(snapshot.Version, restarted.Current?.Version);
            Assert.Equal("Using persisted last-known-good registry v7.", restarted.Status);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PartialTemporaryFileIsIgnoredWhenNoCacheExists()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aar-registry-test-" + Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(directory, "cache.json");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(cachePath + ".partial.tmp", "{\"cacheVersion\":1");
            using var clients = new EmptyHttpClientFactory();
            using var service = Service(clients, cachePath);
            await service.LoadCacheAsync(CancellationToken.None);
            Assert.Null(service.Current);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HealthReadinessKeepsServerLiveWhenCloudAndLkgAreUnavailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aar-registry-test-" + Guid.NewGuid().ToString("N"));
        using var clients = new StubHttpClientFactory(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var service = Service(clients, Path.Combine(directory, "cache.json"));
        try
        {
            await service.StartAsync(CancellationToken.None);

            var health = TacticalLinkHealthResponse.From(service);

            Assert.Equal("ok", health.Status);
            Assert.False(health.AarRegistry.Available);
            Assert.Null(health.AarRegistry.Version);
            Assert.Equal("Cloud is unavailable and no valid local registry is available.", health.AarRegistry.Status);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HealthReadinessReportsLkgWhileCloudIsUnavailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aar-registry-test-" + Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(directory, "cache.json");
        using var clients = new StubHttpClientFactory(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var service = Service(clients, cachePath);
        try
        {
            await service.PersistAsync(Snapshot(), CancellationToken.None);
            await service.StartAsync(CancellationToken.None);

            var health = TacticalLinkHealthResponse.From(service);

            Assert.Equal("ok", health.Status);
            Assert.True(health.AarRegistry.Available);
            Assert.Equal(7, health.AarRegistry.Version);
            Assert.Equal("Cloud refresh failed; retaining registry v7.", health.AarRegistry.Status);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HealthReadinessUpdatesAfterCloudRegistryRefresh()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aar-registry-test-" + Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(directory, "cache.json");
        var published = Snapshot() with { Version = 8 };
        using var clients = new StubHttpClientFactory(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(published, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json")
        });
        using var service = Service(clients, cachePath);
        try
        {
            await service.PersistAsync(Snapshot(), CancellationToken.None);
            await service.StartAsync(CancellationToken.None);

            var health = TacticalLinkHealthResponse.From(service);

            Assert.True(health.AarRegistry.Available);
            Assert.Equal(8, health.AarRegistry.Version);
            Assert.Equal("Using published registry v8.", health.AarRegistry.Status);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static AarRegistryService Service(IHttpClientFactory clients, string cachePath) =>
        new(clients, NullLogger<AarRegistryService>.Instance, "https://example.invalid/registry", cachePath);

    private static AarRegistrySnapshot Snapshot() => new(1, 7, DateTimeOffset.UtcNow,
    [
        new("f16", "F-16", ["F16"], true, false, true, [],
            [new("BoomReceptacle", null, "unknown", [])], [], null, "Verified receiver system.")
    ]);

    private sealed class EmptyHttpClientFactory : IHttpClientFactory, IDisposable
    {
        public HttpClient CreateClient(string name) => new();
        public void Dispose() { }
    }

    private sealed class StubHttpClientFactory(Func<HttpResponseMessage> responseFactory) : IHttpClientFactory, IDisposable
    {
        private readonly List<HttpClient> _clients = [];
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(new StubResponseHandler(responseFactory));
            _clients.Add(client);
            return client;
        }
        public void Dispose()
        {
            foreach (var client in _clients) client.Dispose();
        }
    }

    private sealed class StubResponseHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory());
    }
}
