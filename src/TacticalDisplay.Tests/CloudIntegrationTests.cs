using System.Net;
using System.Text;
using TacticalDisplay.App.Cloud;
using TacticalDisplay.App.Rendering;
using TacticalDisplay.App.Security;
using TacticalDisplay.App.Storage;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace TacticalDisplay.Tests;

public sealed class CloudIntegrationTests
{
    [Fact]
    public async Task PublicCollectionsRequestDoesNotRequireToken()
    {
        HttpRequestMessage? captured = null;
        var client = CreateClient(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, "{\"collections\":[]}");
        });

        var result = await client.ListCollectionsAsync(CancellationToken.None);

        Assert.Empty(result.Collections);
        Assert.Null(captured!.Headers.Authorization);
    }

    [Fact]
    public async Task AuthenticatedRequestUsesStoredBearerToken()
    {
        HttpRequestMessage? captured = null;
        var client = CreateClient(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, "{\"user\":{\"id\":\"1\",\"displayName\":\"Pilot\",\"vatsimCid\":\"123\"}}");
        }, "session-value");

        await client.GetMeAsync(CancellationToken.None);

        Assert.Equal("Bearer", captured!.Headers.Authorization?.Scheme);
        Assert.Equal("session-value", captured.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task MissingTokenStopsAuthenticatedRequestLocally()
    {
        var called = false;
        var client = CreateClient(_ => { called = true; return Json(HttpStatusCode.OK, "{}"); });

        var error = await Assert.ThrowsAsync<CloudApiException>(() => client.GetMeAsync(CancellationToken.None));

        Assert.True(error.IsUnauthorized);
        Assert.False(called);
    }

    [Fact]
    public async Task ApiErrorsAreMappedCentrally()
    {
        var client = CreateClient(_ => Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"RATE_LIMITED\",\"message\":\"Wait\"}}"));

        var error = await Assert.ThrowsAsync<CloudApiException>(() => client.ListCollectionsAsync(CancellationToken.None));

        Assert.True(error.IsRateLimited);
        Assert.Equal("RATE_LIMITED", error.ErrorCode);
    }

    [Fact]
    public async Task FlatBackendErrorCodeIsPreserved()
    {
        var client = CreateClient(_ => Json(HttpStatusCode.Unauthorized, "{\"error\":\"UNAUTHORIZED\",\"message\":\"Authentication required.\"}"), "expired");

        var error = await Assert.ThrowsAsync<CloudApiException>(() => client.GetMeAsync(CancellationToken.None));

        Assert.Equal("UNAUTHORIZED", error.ErrorCode);
        Assert.True(error.IsUnauthorized);
    }

    [Fact]
    public async Task MalformedSuccessResponseIsRejected()
    {
        var client = CreateClient(_ => Json(HttpStatusCode.OK, "not-json"));

        var error = await Assert.ThrowsAsync<CloudApiException>(() => client.ListCollectionsAsync(CancellationToken.None));

        Assert.Equal("MALFORMED_RESPONSE", error.ErrorCode);
    }

    [Fact]
    public async Task AuthorizedCollectionCacheRoundTripsContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "vtsd-cache-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new LocalCollectionCache(root);
            var collection = new Collection { Slug = "shared-pack", Name = "Shared", CurrentVersion = "1.2", Visibility = CollectionVisibility.Private, AccessSource = CollectionAccessSource.Shared };
            var pages = new[] { new KneepadPage { CollectionSlug = collection.Slug, Slug = "brief", Title = "Brief", ContentMarkdown = "# Safe" } };
            await cache.SaveCollectionAsync(collection, pages, []);

            var cached = Assert.Single(await cache.GetCachedCollectionsAsync());
            var page = Assert.Single(await cache.GetCachedPagesAsync(collection.Slug));
            Assert.Equal("1.2", cached.CachedVersion);
            Assert.Equal("# Safe", page.ContentMarkdown);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CacheRejectsPathTraversalSlug()
    {
        var cache = new LocalCollectionCache(Path.Combine(Path.GetTempPath(), "vtsd-cache-test-" + Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetCachedPagesAsync("../private"));
    }

    [Fact]
    public async Task TelemetryRejectsSensitivePayloadKeys()
    {
        var telemetry = new CloudTelemetryService(CreateClient(_ => Json(HttpStatusCode.OK, "{}")));
        var payload = new Dictionary<string, object?> { ["sessionToken"] = "secret" };

        await Assert.ThrowsAsync<ArgumentException>(() => telemetry.TrackAsync("api_error", payload));
    }

    [Fact]
    public void NewCollectionIsMarkedAsUpdateAvailable()
    {
        var collection = new Collection { CurrentVersion = "1.0", CachedVersion = null };
        Assert.True(collection.UpdateAvailable);
    }

    [Fact]
    public void FileTokenFallbackRequiresExplicitOptIn()
    {
        Assert.Throws<InvalidOperationException>(() => new FileFallbackTokenStore("token.txt", explicitlyAllowInsecureStorage: false));
    }

    [Fact]
    public void ProductionCloudDependencyGraphResolves()
    {
        Assert.NotNull(CloudBootstrapper.Provider.GetRequiredService<CloudStartupService>());
        Assert.NotNull(CloudBootstrapper.Provider.GetRequiredService<DeviceLoginService>());
        Assert.NotNull(CloudBootstrapper.Provider.GetRequiredService<KneepadSyncService>());
        Assert.NotNull(CloudBootstrapper.Provider.GetRequiredService<MapFeatureSyncService>());
    }

    [Fact]
    public async Task DeviceLoginValidatesAndReturnsCompletedBackendSession()
    {
        HttpRequestMessage? statusRequest = null;
        var client = CreateClient(request => request.RequestUri!.AbsolutePath.EndsWith("/start", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, $"{{\"requestId\":\"id\",\"requestToken\":\"request\",\"loginUrl\":\"https://login.test/\",\"expiresAt\":\"{DateTimeOffset.UtcNow.AddMinutes(1):O}\",\"pollIntervalSeconds\":1}}")
            : Capture(request, out statusRequest, Json(HttpStatusCode.OK, "{\"status\":\"completed\",\"sessionToken\":\"session\"}")));
        var service = new DeviceLoginService(client, TimeSpan.Zero);

        var result = await service.SignInAsync(_ => { }, CancellationToken.None);

        Assert.Equal("session", result.SessionToken);
        Assert.Equal(HttpMethod.Post, statusRequest!.Method);
        Assert.EndsWith("/api/v1/auth/device/status", statusRequest.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulCollectionFetchCachesLastRemoteListForOfflineUse()
    {
        var root = Path.Combine(Path.GetTempPath(), "vtsd-list-cache-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new LocalCollectionCache(root);
            var client = CreateClient(_ => Json(HttpStatusCode.OK,
                "{\"collections\":[{\"slug\":\"public-pack\",\"name\":\"Public Pack\",\"currentVersion\":\"1\",\"accessSource\":\"public\",\"visibility\":\"public\"}]}"));
            var collections = new CollectionService(client, new KneepadSyncService(client), new MapFeatureSyncService(client),
                cache, new CloudContentStore(cache), new RecordingTelemetry());

            await collections.FetchAsync(CancellationToken.None);

            var cached = Assert.Single(await cache.GetCachedCollectionsAsync());
            Assert.Equal("public-pack", cached.Slug);
            Assert.Equal(CollectionAccessSource.Public, cached.AccessSource);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CloudDtoParsesMapFeatureLabelsAndVisibility()
    {
        var response = System.Text.Json.JsonSerializer.Deserialize<CollectionMapFeaturesResponse>("""
        {
          "mapFeatures": [
            {
              "collectionSlug": "mission",
              "slug": "wp1",
              "name": "Fallback",
              "label": "ALPHA",
              "featureType": "waypoint",
              "geometryType": "point",
              "geometry": { "lat": 60.1, "lon": 24.9 },
              "visibleOnRadar": true,
              "orderIndex": 2
            }
          ]
        }
        """, JsonOptions.Options);

        var feature = Assert.Single(response!.MapFeatures);
        Assert.True(feature.VisibleOnRadar);
        Assert.Equal(MapFeatureType.Waypoint, feature.FeatureType);
        Assert.Equal(MapFeatureGeometryType.Point, feature.GeometryType);
        Assert.Equal("ALPHA", feature.TacticalLabel);
    }

    [Fact]
    public void CloudDtoAcceptsBackendEnumAliases()
    {
        var response = System.Text.Json.JsonSerializer.Deserialize<CollectionsResponse>("""
        {
          "collections": [
            {
              "slug": "mine",
              "name": "Mine",
              "currentVersion": "1",
              "accessSource": "owned",
              "visibility": "private"
            }
          ]
        }
        """, JsonOptions.Options);

        Assert.Equal(CollectionAccessSource.Owner, Assert.Single(response!.Collections).AccessSource);
    }

    [Fact]
    public void CloudDtoAcceptsNumericVatsimCid()
    {
        var response = System.Text.Json.JsonSerializer.Deserialize<MeResponse>("""
        {
          "user": {
            "id": "1",
            "displayName": "Pilot",
            "vatsimCid": 123456
          }
        }
        """, JsonOptions.Options);

        Assert.Equal("123456", response!.User.VatsimCid);
    }

    [Fact]
    public void ScopeProjectionPlacesPointNorthOfOwnshipAboveCenterInNorthUp()
    {
        var projected = ScopeProjection.ProjectGeographicToScope(
            centerX: 100,
            centerY: 100,
            radiusPixels: 90,
            ownLatitudeDeg: 60,
            ownLongitudeDeg: 24,
            targetLatitudeDeg: 60.1,
            targetLongitudeDeg: 24,
            selectedRangeNm: 40,
            ownHeadingDeg: 0,
            headingUp: false);

        Assert.Equal(100, projected.x, precision: 0);
        Assert.True(projected.y < 100);
    }

    [Fact]
    public void ScopeProjectionRotatesPointNorthOfOwnshipInHeadingUp()
    {
        var projected = ScopeProjection.ProjectGeographicToScope(
            centerX: 100,
            centerY: 100,
            radiusPixels: 90,
            ownLatitudeDeg: 60,
            ownLongitudeDeg: 24,
            targetLatitudeDeg: 60.1,
            targetLongitudeDeg: 24,
            selectedRangeNm: 40,
            ownHeadingDeg: 90,
            headingUp: true);

        Assert.True(projected.x < 100);
        Assert.Equal(100, projected.y, precision: 0);
    }

    [Fact]
    public async Task ManualSyncKeepsNonCachedRemoteCollectionVisible()
    {
        var root = Path.Combine(Path.GetTempPath(), "vtsd-sync-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new Func<HttpRequestMessage, HttpResponseMessage>(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/collections", StringComparison.Ordinal))
                    return Json(HttpStatusCode.OK, "{\"collections\":[{\"slug\":\"cached\",\"name\":\"Cached\",\"currentVersion\":\"1\",\"accessSource\":\"public\",\"visibility\":\"public\"},{\"slug\":\"remote-only\",\"name\":\"Remote\",\"currentVersion\":\"1\",\"accessSource\":\"public\",\"visibility\":\"public\"}]}");
                if (path.EndsWith("/pages", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "{\"pages\":[{\"slug\":\"page\",\"title\":\"Page\",\"contentMarkdown\":\"# Page\"}]}");
                if (path.EndsWith("/map-features", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "{\"mapFeatures\":[]}");
                return Json(HttpStatusCode.NotFound, "{\"error\":\"NOT_FOUND\"}");
            });
            var client = CreateClient(handler); var telemetry = new RecordingTelemetry();
            var cache = new LocalCollectionCache(Path.Combine(root, "cache"));
            var content = new CloudContentStore(cache);
            var overlays = new CloudOverlaySettingsStore(Path.Combine(root, "overlays.json"));
            overlays.Save([new Collection { Slug = "remote-only", CacheOffline = false }]);
            var preferences = new CloudPreferencesStore(Path.Combine(root, "preferences.json"));
            var collections = new CollectionService(client, new KneepadSyncService(client), new MapFeatureSyncService(client), cache, content, telemetry);
            var auth = new AuthService(client, new DeviceLoginService(client, TimeSpan.Zero), new MemoryTokenStore(null), telemetry);
            var startup = new CloudStartupService(auth, collections, overlays, preferences, telemetry, content);
            var viewModel = new CloudSettingsViewModel(auth, collections, overlays, preferences, telemetry, startup);

            await viewModel.SyncAsync(CancellationToken.None);

            Assert.Equal(2, viewModel.Collections.Count);
            Assert.Single(await cache.GetCachedCollectionsAsync());
            var remoteOnly = Assert.Single(viewModel.Collections, item => item.Slug == "remote-only" && !item.CacheOffline);
            Assert.True(content.HasContent(remoteOnly.Slug));
            viewModel.SelectedCollection = remoteOnly;
            Assert.True(viewModel.CanOpenKneepad);
            remoteOnly.ShowKneepadPages = false;
            Assert.False(viewModel.CanOpenKneepad);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RedeemUsesCompleteCollectionFromRefreshedBackendList()
    {
        var root = Path.Combine(Path.GetTempPath(), "vtsd-redeem-test-" + Guid.NewGuid().ToString("N"));
        var redeemed = false;
        try
        {
            var client = CreateClient(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/me", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "{\"user\":{\"id\":\"1\",\"displayName\":\"Pilot\",\"vatsimCid\":\"123\"}}");
                if (path.EndsWith("/share-codes/redeem", StringComparison.Ordinal)) { redeemed = true; return Json(HttpStatusCode.OK, "{\"collection\":{\"slug\":\"shared-pack\"}}"); }
                if (path.EndsWith("/collections", StringComparison.Ordinal))
                    return Json(HttpStatusCode.OK, redeemed
                        ? "{\"collections\":[{\"slug\":\"public\",\"name\":\"Public\",\"currentVersion\":\"1\",\"accessSource\":\"public\",\"visibility\":\"public\"},{\"slug\":\"shared-pack\",\"name\":\"Complete Shared Pack\",\"currentVersion\":\"2\",\"accessSource\":\"shared\",\"visibility\":\"private\"}]}"
                        : "{\"collections\":[{\"slug\":\"public\",\"name\":\"Public\",\"currentVersion\":\"1\",\"accessSource\":\"public\",\"visibility\":\"public\"}]}");
                return Json(HttpStatusCode.NotFound, "{\"error\":\"NOT_FOUND\"}");
            }, "session");
            var telemetry = new RecordingTelemetry(); var cache = new LocalCollectionCache(Path.Combine(root, "cache")); var content = new CloudContentStore(cache);
            var overlays = new CloudOverlaySettingsStore(Path.Combine(root, "overlays.json")); var preferences = new CloudPreferencesStore(Path.Combine(root, "preferences.json"));
            var collections = new CollectionService(client, new KneepadSyncService(client), new MapFeatureSyncService(client), cache, content, telemetry);
            var auth = new AuthService(client, new DeviceLoginService(client, TimeSpan.Zero), new MemoryTokenStore("session"), telemetry);
            var startup = new CloudStartupService(auth, collections, overlays, preferences, telemetry, content);
            var viewModel = new CloudSettingsViewModel(auth, collections, overlays, preferences, telemetry, startup);
            await viewModel.InitializeAsync(CancellationToken.None);

            var collection = await viewModel.RedeemAsync("not-recorded-in-telemetry", CancellationToken.None);

            Assert.NotNull(collection);
            Assert.Equal("Complete Shared Pack", collection.Name);
            Assert.Equal(CollectionAccessSource.Shared, collection.AccessSource);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OnlineAuthorizationRemovesStalePrivateContentFromSessionButKeepsOfflineCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "vtsd-auth-reconcile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new LocalCollectionCache(root); var content = new CloudContentStore(cache);
            var privateCollection = new Collection { Slug = "old-private", Name = "Old private", CurrentVersion = "1", Visibility = CollectionVisibility.Private };
            await cache.SaveCollectionAsync(privateCollection,
                [new KneepadPage { CollectionSlug = privateCollection.Slug, Slug = "page", Title = "Page" }], []);
            await content.LoadAuthorizedCacheAsync();
            Assert.True(content.HasContent(privateCollection.Slug));

            content.ReconcileOnlineAuthorization([new Collection { Slug = "public", Name = "Public", CurrentVersion = "1" }]);
            await content.LoadAuthorizedCacheAsync();

            Assert.False(content.HasContent(privateCollection.Slug));
            Assert.DoesNotContain(content.Collections, x => x.Slug == privateCollection.Slug);
            Assert.Contains(await cache.GetCachedCollectionsAsync(), x => x.Slug == privateCollection.Slug);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static VtsdCloudClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> response, string? token = null)
    {
        var http = new HttpClient(new Handler(response));
        return new VtsdCloudClient(http, new CloudOptions { BaseUri = new Uri("https://cloud.test/"), DashboardUri = new Uri("https://dashboard.test/") }, new MemoryTokenStore(token));
    }
    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Capture(HttpRequestMessage request, out HttpRequestMessage captured, HttpResponseMessage response)
    {
        captured = request;
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class MemoryTokenStore(string? token) : ISecureTokenStore
    {
        private string? _token = token;
        public Task<string?> GetSessionTokenAsync() => Task.FromResult(_token);
        public Task SetSessionTokenAsync(string value) { _token = value; return Task.CompletedTask; }
        public Task ClearSessionTokenAsync() { _token = null; return Task.CompletedTask; }
    }
    private sealed class RecordingTelemetry : ICloudTelemetryService
    {
        public List<string> Events { get; } = [];
        public Task TrackAsync(string eventName, IReadOnlyDictionary<string, object?>? payload = null, CancellationToken ct = default) { Events.Add(eventName); return Task.CompletedTask; }
    }
}
