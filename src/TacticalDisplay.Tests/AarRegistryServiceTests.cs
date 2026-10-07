using Microsoft.Extensions.Logging.Abstractions;
using TacticalLink.Server.Aar;
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
}
